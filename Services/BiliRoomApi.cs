using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinecraftChatOverlay.Models;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// 直播间公开信息查询（房间标题 / 主播 / 人数）。
///
/// 两个要注意的点（2026-09 实测）：
/// 1. 旧的 getOnlineRank 接口已废弃，恒返回 onlineNum=0，所以不能再用它拿人数。
///    B 站现在对外只提供「人气」值，也就是 getInfoByRoom 里的 room_info.online，
///    数值与网页端房间页顶部显示的「x.x万人气」一致。
/// 2. getInfoByRoom 需要浏览器 Cookie（buvid3 等），裸请求会返回 code=-352 风控。
///    这里用「访问主站拿 buvid + 指纹接口补 buvid4 + 可选登录 Cookie」的方式准备。
/// </summary>
public static class BiliRoomApi
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";

    private static readonly Uri HomeUri = new("https://www.bilibili.com/");

    /// <summary>创建带 Cookie 容器的 HttpClient，并把登录 Cookie 写进容器。</summary>
    public static HttpClient CreateHttpClient(out CookieContainer cookieContainer, string? loginCookie = null)
    {
        // 注意：CookieContainer 默认每个域最多只存 20 个 Cookie，
        // 浏览器导出的完整 Cookie 往往有 40 个以上，超出的会被静默丢弃
        // （SESSDATA / DedeUserID / buvid_fp 等常被挤掉，导致登录态失效、接口吃 -352）。
        cookieContainer = new CookieContainer
        {
            Capacity = 500,
            PerDomainCapacity = 300,
            MaxCookieSize = 8192
        };

        var http = new HttpClient(new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");

        if (!string.IsNullOrWhiteSpace(loginCookie))
        {
            foreach (var part in loginCookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                var name = part[..idx].Trim();
                var value = part[(idx + 1)..].Trim();
                try
                {
                    cookieContainer.Add(HomeUri, new Cookie(name, value) { Domain = "bilibili.com", Path = "/" });
                }
                catch
                {
                }
            }
        }

        return http;
    }

    /// <summary>准备 buvid3/buvid4 等风控 Cookie。失败不抛异常，交给调用方后续处理。</summary>
    public static async Task EnsureCookieAsync(HttpClient http, CookieContainer cookieContainer, CancellationToken token)
    {
        try
        {
            // 访问主站通常会 Set-Cookie: buvid3 / buvid4 / b_nut。
            using var _ = await http.GetAsync(HomeUri, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        try
        {
            using var response = await http.GetAsync("https://api.bilibili.com/x/frontend/finger/spi", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return;
            }

            AddCookieIfMissing(cookieContainer, "buvid3", "b_3", data);
            AddCookieIfMissing(cookieContainer, "buvid4", "b_4", data);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private static void AddCookieIfMissing(CookieContainer container, string cookieName, string jsonName, JsonElement data)
    {
        if (container.GetCookies(HomeUri)[cookieName]?.Value is { Length: > 0 })
        {
            return;
        }

        if (!data.TryGetProperty(jsonName, out var element) || element.GetString() is not { Length: > 0 } value)
        {
            return;
        }

        try
        {
            container.Add(HomeUri, new Cookie(cookieName, value) { Domain = "bilibili.com", Path = "/" });
        }
        catch
        {
        }
    }

    /// <summary>查询房间信息。失败返回 null（不抛异常）。</summary>
    /// <param name="withInteraction">是否顺带查一次互动人数（多一次 HTTP 请求）。</param>
    public static async Task<RoomInfo?> QueryAsync(
        HttpClient http, CookieContainer cookieContainer, long roomId, CancellationToken token, bool withInteraction = true)
    {
        if (roomId <= 0)
        {
            return null;
        }

        var info = await TryQueryInfoByRoomAsync(http, cookieContainer, roomId, token).ConfigureAwait(false)
                   ?? await TryQueryRoomInitAsync(http, cookieContainer, roomId, token).ConfigureAwait(false);
        if (info == null)
        {
            return null;
        }

        if (withInteraction && info.Uid > 0)
        {
            info.Interaction = await QueryInteractionAsync(http, cookieContainer, info.RoomId, info.Uid, token).ConfigureAwait(false);
        }

        return info;
    }

    /// <summary>
    /// 查询互动人数（高能榜口径：「投喂、发弹幕均可上榜」）。
    /// 这是真实人数，但不等于在线人数 —— 只看不说话的观众不计入。
    /// 查询失败返回 0。
    /// </summary>
    public static async Task<int> QueryInteractionAsync(
        HttpClient http, CookieContainer cookieContainer, long roomId, long ruid, CancellationToken token)
    {
        if (roomId <= 0 || ruid <= 0)
        {
            return 0;
        }

        try
        {
            var url = "https://api.live.bilibili.com/xlive/general-interface/v1/rank/getOnlineGoldRank" +
                      $"?roomId={roomId}&ruid={ruid}&page=1&pageSize=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            var cookieHeader = cookieContainer.GetCookieHeader(new Uri("https://api.live.bilibili.com/"));
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("onlineNum", out var onlineNum) &&
                onlineNum.TryGetInt32(out var value))
            {
                return value;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<RoomInfo?> TryQueryInfoByRoomAsync(
        HttpClient http, CookieContainer cookieContainer, long roomId, CancellationToken token)
    {
        try
        {
            var url = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?room_id={roomId}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // 部分情况下 CookieContainer 的自动附加不生效，这里显式带上。
            var cookieHeader = cookieContainer.GetCookieHeader(new Uri("https://api.live.bilibili.com/"));
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var code) && code != 0)
            {
                // -352 表示风控，通常是没有 buvid Cookie。
                return null;
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("room_info", out var roomInfoElement))
            {
                return null;
            }

            var info = new RoomInfo
            {
                RoomId = GetInt64(roomInfoElement, "room_id"),
                Uid = GetInt64(roomInfoElement, "uid"),
                Title = GetString(roomInfoElement, "title"),
                Online = GetInt32(roomInfoElement, "online"),
                LiveStatus = GetInt32(roomInfoElement, "live_status") == 1
            };

            if (data.TryGetProperty("anchor_info", out var anchor) &&
                anchor.TryGetProperty("base_info", out var baseInfo))
            {
                info.UserName = GetString(baseInfo, "uname");
            }

            // watched_show 这个位置的含义随房间而变：
            //   显示「X人看过」→ num 是真实观看人数（本场累计）；
            //   显示「X人气」  → num 与 room_info.online 同值，没有额外信息。
            // 所以必须看文案才能判断，不能直接拿 num。
            if (data.TryGetProperty("watched_show", out var watchedShow) &&
                GetString(watchedShow, "text_large").Contains("看过", StringComparison.Ordinal))
            {
                info.Watched = GetInt32(watchedShow, "num");
            }

            return info.RoomId > 0 ? info : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<RoomInfo?> TryQueryRoomInitAsync(
        HttpClient http, CookieContainer cookieContainer, long roomId, CancellationToken token)
    {
        try
        {
            var url = $"https://api.live.bilibili.com/room/v1/Room/room_init?id={roomId}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            var cookieHeader = cookieContainer.GetCookieHeader(new Uri("https://api.live.bilibili.com/"));
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return null;
            }

            var info = new RoomInfo
            {
                RoomId = GetInt64(data, "room_id"),
                Uid = GetInt64(data, "uid"),
                Title = GetString(data, "title"),
                Online = GetInt32(data, "online"),
                LiveStatus = GetInt32(data, "live_status") == 1
            };

            return info.RoomId > 0 ? info : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long GetInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static int GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}
