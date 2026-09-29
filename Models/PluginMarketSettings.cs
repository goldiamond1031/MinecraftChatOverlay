namespace MinecraftChatOverlay.Models;

/// <summary>
/// 插件市场的连接设置。
///
/// 市场没有服务器：清单就是仓库里的一个 markdown 级小文件 market/index.json，
/// 插件包就是仓库里的 zip。所以这里只有"去哪儿看清单"这一个变量。
/// </summary>
public sealed class PluginMarketSettings
{
    /// <summary>清单地址。默认走 jsDelivr 镜像（国内一般能通）。</summary>
    public string IndexUrl { get; set; } = DefaultIndexUrl;

    /// <summary>主源拿不到时自动试的备用源（GitHub 官方 raw）。</summary>
    public string FallbackUrl { get; set; } = DefaultFallbackUrl;

    /// <summary>上次成功刷新清单的时间。</summary>
    public DateTime? LastRefreshAt { get; set; }

    public const string DefaultIndexUrl =
        "https://cdn.jsdelivr.net/gh/goldiamond1031/MinecraftChatOverlay@main/market/index.json";

    public const string DefaultFallbackUrl =
        "https://raw.githubusercontent.com/goldiamond1031/MinecraftChatOverlay/main/market/index.json";
}