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

    /// <summary>先试主源，不行再试备用源；都失败就把每个源的失败原因拼起来返回。</summary>
    public static async Task<MarketFetchResult> FetchIndexAsync(string primaryUrl, string fallbackUrl, CancellationToken token)
    {
        var errors = new List<string>();
        var urls = new[] { primaryUrl, fallbackUrl }
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var url in urls)
        {
            try
            {
                var json = await Http.GetStringAsync(url, token).ConfigureAwait(false);
                var index = MarketIndex.TryParse(json, url);
                if (index is null)
                {
                    errors.Add($"{Shorten(url)}：清单格式不对");
                    continue;
                }

                return new MarketFetchResult(index, url, json, null);
            }
            catch (Exception ex)
            {
                errors.Add($"{Shorten(url)}：{Describe(ex)}");
            }
        }

        return new MarketFetchResult(null, "", "", errors.Count > 0 ? string.Join("；", errors) : "没有填市场地址");
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