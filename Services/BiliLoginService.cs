using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftChatOverlay.Services;

/// <summary>扫码登录状态。</summary>
public enum QrLoginState
{
    /// <summary>还没扫码。</summary>
    Waiting,

    /// <summary>已扫码，等待手机上确认。</summary>
    Scanned,

    /// <summary>已确认，登录成功。</summary>
    Confirmed,

    /// <summary>二维码已过期，需要重新获取。</summary>
    Expired,

    /// <summary>请求出错。</summary>
    Failed
}

/// <param name="State">状态。</param>
/// <param name="Message">给人看的说明。</param>
public sealed record QrLoginResult(QrLoginState State, string Message, string Cookie = "");

/// <param name="IsLogin">登录态是否有效。</param>
/// <param name="UserName">昵称（有效时）。</param>
/// <param name="Uid">UID（有效时）。</param>
/// <param name="Message">给人看的说明。</param>
public sealed record BiliLoginInfo(bool IsLogin, string UserName, long Uid, string Message);

/// <summary>
/// B站扫码登录：拿二维码链接 → 轮询状态 → 成功后从返回的 url 里取出 Cookie。
/// 让不会手动导 Cookie 的用户也能登录。
/// </summary>
public static class BiliLoginService
{
    private const string GenerateUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
    private const string PollUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll";

    /// <summary>
    /// 取设备指纹（buvid3 / buvid4）放进 CookieContainer。
    /// 官方网页登录流程在任何请求前都会有这几个 Cookie，B站把它们用于风控追踪；
    /// 不带的话请求看起来"不像正常浏览器"，更容易被要求安全验证。
    /// </summary>
    public static async Task EnsureDeviceFingerprintAsync(
        HttpClient http, CookieContainer cookieContainer, CancellationToken token)
    {
        try
        {
            // 先访问主站：通常会直接 Set-Cookie（buvid3 / buvid4 / b_nut）
            using (var warmup = new HttpRequestMessage(HttpMethod.Get, "https://www.bilibili.com/"))
            {
                ApplyCommonHeaders(warmup);
                using var _ = await http.SendAsync(warmup, token).ConfigureAwait(false);
            }
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
            // 兜底：从官方指纹接口显式取，再手动写进容器
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/frontend/finger/spi");
            ApplyCommonHeaders(request);
            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
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

            AddCookie(cookieContainer, "buvid3", data, "b_3");
            AddCookie(cookieContainer, "buvid4", data, "b_4");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private static void AddCookie(CookieContainer container, string name, JsonElement data, string field)
    {
        if (!data.TryGetProperty(field, out var element) || element.GetString() is not { Length: > 0 } value)
        {
            return;
        }

        try
        {
            container.Add(new Cookie(name, value) { Domain = ".bilibili.com", Path = "/" });
        }
        catch
        {
        }
    }

    /// <summary>生成二维码，返回需要被扫码的链接与轮询用的 key；失败返回 null。</summary>
    public static async Task<(string Url, string Key)?> GenerateQrCodeAsync(HttpClient http, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GenerateUrl);
            ApplyCommonHeaders(request);

            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("url", out var urlElement) ||
                !data.TryGetProperty("qrcode_key", out var keyElement))
            {
                return null;
            }

            var url = urlElement.GetString();
            var key = keyElement.GetString();
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            {
                return null;
            }

            return (url, key);
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

    /// <summary>
    /// 查询扫码状态。登录成功时把 Cookie 取出来。
    /// B站的登录字段有两个可能来源，两个都要试：
    ///   1) 响应 data.url 的查询参数（crossDomain?SESSDATA=…&DedeUserID=…）
    ///   2) 响应的 Set-Cookie 头（会落在传进来的 CookieContainer 里）
    /// </summary>
    public static async Task<QrLoginResult> PollAsync(
        HttpClient http, CookieContainer cookieContainer, string qrCodeKey, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                PollUrl + "?qrcode_key=" + Uri.EscapeDataString(qrCodeKey));
            ApplyCommonHeaders(request);

            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch
            {
                // 登录成功时有可能返回 302，响应体不是 JSON —— 此时只可能从响应头拿 Cookie
            }

            if (doc == null)
            {
                var headerCookie = CookieTools.FromHeaders(response);
                return string.IsNullOrEmpty(headerCookie)
                    ? new QrLoginResult(QrLoginState.Failed,
                        $"响应不是 JSON（HTTP {(int)response.StatusCode}），"
                        + $"响应头=[{CookieTools.DescribeHeaders(response)}]，容器=[{CookieTools.DescribeContainer(cookieContainer)}]")
                    : new QrLoginResult(QrLoginState.Confirmed, "登录成功（响应头）", headerCookie);
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("data", out var data))
                {
                    return new QrLoginResult(QrLoginState.Failed, "返回数据异常：没有 data 字段");
                }

                var code = data.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var codeValue)
                    ? codeValue
                    : -1;

                switch (code)
                {
                    case 0:
                    {
                        var urlText = data.TryGetProperty("url", out var urlElement)
                            ? urlElement.GetString() ?? ""
                            : "";

                        // 依次尝试三个来源，B站不同时期的下发方式不一样：
                        //   1) 响应 data.url 的查询参数
                        //   2) 响应头的 Set-Cookie
                        //   3) CookieContainer（HttpClient 自动收集的）
                        var cookie = CookieTools.FromUrl(urlText);
                        var source = "url 参数";

                        if (string.IsNullOrEmpty(cookie))
                        {
                            cookie = CookieTools.FromHeaders(response);
                            source = "响应头 Set-Cookie";
                        }

                        if (string.IsNullOrEmpty(cookie))
                        {
                            cookie = CookieTools.FromContainer(cookieContainer);
                            source = "Cookie 容器";
                        }

                        if (string.IsNullOrEmpty(cookie))
                        {
                            // 只报告字段名，不打印 Cookie 内容，避免泄漏登录凭据
                            return new QrLoginResult(QrLoginState.Failed,
                                $"登录成功但没取到 Cookie（HTTP {(int)response.StatusCode}；"
                                + $"url 参数=[{CookieTools.DescribeUrlParams(urlText)}]；"
                                + $"响应头=[{CookieTools.DescribeHeaders(response)}]；"
                                + $"容器=[{CookieTools.DescribeContainer(cookieContainer)}]；"
                                + $"响应字段=[{DescribeJsonShape(doc.RootElement)}]）");
                        }

                        return new QrLoginResult(QrLoginState.Confirmed, $"登录成功（{source}）", cookie);
                    }

                    case 86090:
                        return new QrLoginResult(QrLoginState.Scanned, "已扫码，请在手机上点确认");

                    case 86038:
                        return new QrLoginResult(QrLoginState.Expired, "二维码已过期，请刷新");

                    case 86101:
                        return new QrLoginResult(QrLoginState.Waiting, "等待扫码");

                    default:
                    {
                        // 未知状态码：把 B站的原文提示带出来，可能是风控/需要安全验证
                        var message = data.TryGetProperty("message", out var messageElement)
                            ? messageElement.GetString() ?? ""
                            : "";
                        return new QrLoginResult(QrLoginState.Waiting,
                            $"等待扫码（状态码 {code}{(string.IsNullOrEmpty(message) ? "" : "：" + message)}）");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new QrLoginResult(QrLoginState.Failed, "查询失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 验证 Cookie 的登录态是否还有效。
    /// 用于「保存并验证登录」按钮，以及程序启动时检查登录是否已失效（失效就提醒重新扫码）。
    /// </summary>
    public static async Task<BiliLoginInfo> VerifyLoginAsync(string cookie, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return new BiliLoginInfo(false, "", 0, "未填写 Cookie");
        }

        try
        {
            using var handler = new HttpClientHandler { UseCookies = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);

            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("isLogin", out var isLogin) &&
                isLogin.ValueKind == JsonValueKind.True)
            {
                var userName = data.TryGetProperty("uname", out var unameElement)
                    ? unameElement.GetString() ?? ""
                    : "";
                var uid = data.TryGetProperty("mid", out var midElement) && midElement.TryGetInt64(out var midValue)
                    ? midValue
                    : 0;
                return new BiliLoginInfo(true, userName, uid, $"已登录：{userName}（UID:{uid}）");
            }

            return new BiliLoginInfo(false, "", 0, "登录已失效，请重新扫码登录");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BiliLoginInfo(false, "", 0, "验证失败：" + ex.Message);
        }
    }

    /// <summary>列出 JSON 的字段名（顶层 + data 一层，不列值），用于诊断。</summary>
    private static string DescribeJsonShape(JsonElement root)
    {
        var top = root.EnumerateObject().Select(p => p.Name).ToArray();
        var desc = "顶层:" + string.Join(",", top);
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            desc += " / data:" + string.Join(",", data.EnumerateObject().Select(p => p.Name));
        }

        return desc;
    }

    private static void ApplyCommonHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
    }
}
