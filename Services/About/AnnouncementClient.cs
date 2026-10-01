using System.Net.Http;

namespace MinecraftChatOverlay.Services.About;

/// <summary>
/// 公告客户端：从仓库拉 about/announcement.json。
/// 和鸣谢名单同一个套路：jsDelivr 主源 + GitHub raw 备用，两个都拉、都拿不到就当作没公告。
/// 取哪一份先比 `updatedAt`；**打平取 GitHub 本体那份**（jsDelivr 只是镜像，只会更旧）。
/// 拉不到就当作没公告，不打扰用户。
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

        // 一并记下"这是第几个源"：urls[0] = jsDelivr 镜像，urls[1] = GitHub 本体。
        // 平局排序要用到它，见下面。
        var attempts = await Task
            .WhenAll(urls.Select((u, index) => FetchOneAsync(u, index, token)))
            .ConfigureAwait(false);

        // 排序规则：先比 updatedAt，**打平就取 GitHub 本体那份**（index 大的赢）。
        //
        // 为什么平局要偏向本体：jsDelivr 只是 GitHub 的镜像，只会比本体旧，不可能更新。
        // 真踩过的坑（2026-10-01 的 eve 公告）：新公告换了 id，但 updatedAt 忘了跟着改，
        // 两个源的时间戳一模一样 —— 平局时按源顺序取到了 jsDelivr 上那份旧的，
        // id 又正好和"已经弹过"的那条一样，于是用户永远看不到新公告。
        return attempts
            .Where(a => a.Item is not null)
            .OrderByDescending(a => a.Item!.UpdatedAt ?? DateTime.MinValue)
            .ThenByDescending(a => a.Index)
            .Select(a => a.Item)
            .FirstOrDefault();
    }

    private static async Task<(Announcement? Item, int Index)> FetchOneAsync(string url, int index, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            // 时间戳绕开 CDN 缓存
            var requestUrl = url + (url.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
            var json = await Http.GetStringAsync(requestUrl, timeout.Token).ConfigureAwait(false);
            return (Announcement.TryParse(json), index);
        }
        catch
        {
            // 公告是"有就看一眼"的东西，失败不报错、不打扰
            return (null, index);
        }
    }
}

