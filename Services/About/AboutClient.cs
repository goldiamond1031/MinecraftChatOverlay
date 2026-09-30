using System.Net.Http;

namespace MinecraftChatOverlay.Services.About;

/// <summary>拉鸣谢清单的结果。失败时 Index 为 null、Error 是人话。</summary>
public sealed record AboutFetchResult(AboutIndex? Index, string SourceUrl, string? Error)
{
    public bool Ok => Index is not null;
}

/// <summary>
/// 「关于」页客户端：从仓库拉 about/about.json。
/// 和市场同一个套路 —— 没有 API key，纯 HTTPS GET 走 CDN；
/// 主源（jsDelivr）和备用源（GitHub raw）都查，取 updatedAt 更新的那份
/// （jsDelivr 对分支 URL 的缓存能压很久，刚推完时 primary 可能还是旧的）。
/// </summary>
public static class AboutClient
{
    public const string DefaultIndexUrl =
        "https://cdn.jsdelivr.net/gh/goldiamond1031/MinecraftChatOverlay@main/about/about.json";

    public const string DefaultFallbackUrl =
        "https://raw.githubusercontent.com/goldiamond1031/MinecraftChatOverlay/main/about/about.json";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftChatOverlay-About/1.0");
        }
        catch
        {
            // 加不上 UA 也不影响
        }

        return http;
    }

    public static async Task<AboutFetchResult> FetchAsync(string primaryUrl, string fallbackUrl, CancellationToken token)
    {
        var urls = new[] { primaryUrl, fallbackUrl }
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            return new AboutFetchResult(null, "", "没有配置鸣谢清单地址");
        }

        var attempts = await Task.WhenAll(urls.Select(u => FetchOneAsync(u, token))).ConfigureAwait(false);

        var okList = attempts
            .Where(a => a.Index is not null)
            .OrderByDescending(a => a.Index!.UpdatedAt ?? DateTime.MinValue)
            .ToList();

        if (okList.Count == 0)
        {
            var reasons = attempts.Select(a => $"{Shorten(a.Url)}：{a.Error}");
            return new AboutFetchResult(null, "", string.Join("；", reasons));
        }

        var best = okList[0];
        return new AboutFetchResult(best.Index, best.Url, null);
    }

    /// <summary>拉单个源。10 秒上限，失败返回原因（不抛）。</summary>
    private static async Task<(AboutIndex? Index, string Url, string? Error)> FetchOneAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            // 时间戳绕开 CDN 缓存
            var requestUrl = url + (url.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
            var json = await Http.GetStringAsync(requestUrl, timeout.Token).ConfigureAwait(false);
            var index = AboutIndex.TryParse(json, url);
            return index is null
                ? (null, url, "清单格式不对")
                : (index, url, null);
        }
        catch (Exception ex)
        {
            return (null, url, Describe(ex));
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
