using System.Net.Http;

namespace MinecraftChatOverlay.Services.About;

/// <summary>
/// 公告客户端：从仓库拉 about/announcement.json。
/// 和鸣谢名单同一个套路（jsDelivr 主源 + GitHub raw 备用，都不是就返回原意）。
/// 但公告**不挑 updatedAt 更新的那份**：公告只认主源拿到的第一份有效结果即可，
/// 简单点、少一次网络等待 —— 拉不到就当作没公告，不打扰用户。
/// </summary>
public static class AnnouncementClient
{
    public const string DefaultUrl =
        "https://cdn.jsdelivr.net/gh/goldiamond1031/MinecraftChatOverlay@main/about/announcement.json";

    public const string DefaultFallbackUrl =
        "https://raw.githubusercontent.com/goldiamond1031/MinecraftChatOverlay/main/about/announcement.json";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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

    /// <summary>
    /// 拉公告。没公告 / 拉不到都返回 null（调用方什么都不用做）。
    /// 主源不通时自动试备用源；两个都拿到时取 updatedAt 更新的那份
    /// （jsDelivr 对分支 URL 的缓存压得久，刚推完的公告可能只有 raw 是新的）。
    /// </summary>
    public static async Task<Announcement?> FetchAsync(CancellationToken token)
    {
        var urls = new[] { DefaultUrl, DefaultFallbackUrl }
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var attempts = await Task.WhenAll(urls.Select(u => FetchOneAsync(u, token))).ConfigureAwait(false);

        return attempts
            .Where(a => a is not null)
            .OrderByDescending(a => a!.UpdatedAt ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    private static async Task<Announcement?> FetchOneAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            // 时间戳绕开 CDN 缓存
            var requestUrl = url + (url.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
            var json = await Http.GetStringAsync(requestUrl, timeout.Token).ConfigureAwait(false);
            return Announcement.TryParse(json);
        }
        catch
        {
            // 公告是"有就看一眼"的东西，失败不报错、不打扰
            return null;
        }
    }
}
