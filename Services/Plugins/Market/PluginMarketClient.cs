using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace MinecraftChatOverlay.Services.Plugins.Market;

/// <summary>拉清单的结果。失败时 Index 为 null、Error 是人话。</summary>
public sealed record MarketFetchResult(MarketIndex? Index, string SourceUrl, string RawJson, string? Error)
{
    public bool Ok => Index is not null;
}

/// <summary>
/// 市场客户端：拉清单 + 下插件包。
/// 没有服务器也没有 API key，就是普通的 HTTPS GET（走 CDN，别用 api.github.com —— 它未登录只有 60 次/小时）。
/// </summary>
public static class PluginMarketClient
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftChatOverlay-Market/1.0");
        }
        catch
        {
            // 加不上 UA 也不影响
        }

        return http;
    }

    /// <summary>
    /// 拉清单。
    ///
    /// 主源（jsDelivr）和备用源（GitHub raw）**都查**，然后取 <c>updatedAt</c> 更新的那一份 —— 原因是
    /// jsDelivr 对分支 URL 的缓存能压很久：刚推完新清单时 primary 可能还是旧的，而 raw 通常几分钟内就是新的。
    /// 谁新用谁，并且把胜出者的地址记下来当下载基准，保证清单和插件包来自同一个源。
    /// </summary>
    public static async Task<MarketFetchResult> FetchIndexAsync(string primaryUrl, string fallbackUrl, CancellationToken token)
    {
        var urls = new[] { primaryUrl, fallbackUrl }
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            return new MarketFetchResult(null, "", "", "没有填市场地址");
        }

        var attempts = await Task.WhenAll(urls.Select(u => FetchOneAsync(u, token))).ConfigureAwait(false);

        var ok = attempts
            .Where(a => a.Index is not null)
            .OrderByDescending(a => a.Index!.UpdatedAt ?? DateTime.MinValue)
            .ToList();

        if (ok.Count == 0)
        {
            var reasons = attempts.Select(a => $"{Shorten(a.Url)}：{a.Error}");
            return new MarketFetchResult(null, "", "", string.Join("；", reasons));
        }

        var best = ok[0];
        return new MarketFetchResult(best.Index, best.Url, best.Raw, null);
    }

    /// <summary>拉单个源。10 秒上限，失败返回原因（不抛）。</summary>
    private static async Task<(MarketIndex? Index, string Raw, string Url, string? Error)> FetchOneAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            // 时间戳绕开 CDN 缓存；SourceUrl 仍记干净地址，免得相对路径解析带上参数
            var requestUrl = url + (url.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
            var json = await Http.GetStringAsync(requestUrl, timeout.Token).ConfigureAwait(false);
            var index = MarketIndex.TryParse(json, url);
            return index is null
                ? (null, "", url, "清单格式不对")
                : (index, json, url, null);
        }
        catch (Exception ex)
        {
            return (null, "", url, Describe(ex));
        }
    }
    /// <summary>下载插件包到 destPath。清单里有大小/SHA256 就顺手校验，对不上删掉并返回失败。</summary>
    public static async Task<(bool Ok, string Error)> DownloadAsync(
        string url, string destPath, long? expectedSize, string? expectedSha256, CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var dest = File.Create(destPath))
            {
                await source.CopyToAsync(dest, token).ConfigureAwait(false);
            }

            var length = new FileInfo(destPath).Length;
            if (expectedSize is > 0 && length != expectedSize)
            {
                TryDelete(destPath);
                return (false, $"下载大小不对（拿到 {length} 字节，清单写的是 {expectedSize}）");
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = await Task.Run(
                    () => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destPath))), token).ConfigureAwait(false);

                if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(destPath);
                    return (false, "下载内容的 SHA256 和清单对不上，已丢弃（可能传输损坏，也可能包被人改过）");
                }
            }

            return (true, "");
        }
        catch (Exception ex)
        {
            TryDelete(destPath);
            return (false, Describe(ex));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就算了，下次覆盖
        }
    }

    private static string Shorten(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host + uri.AbsolutePath : url;

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "超时",
        HttpRequestException http => "网络不通（" + (http.InnerException?.Message ?? http.Message) + "）",
        _ => ex.Message,
    };
}