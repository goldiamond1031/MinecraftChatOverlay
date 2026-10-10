namespace DouyinDanmaku;

/// <summary>
/// 插件配置。「值变化即保存」由页面的 <c>_loading</c> 闸门保护（铁律四）。
/// </summary>
public sealed class Settings
{
    /// <summary>抖音直播间地址里的短 id（live.douyin.com/ 后面那段）。</summary>
    public string WebRoomId { get; set; } = "";

    /// <summary>启动时自动连接上次的直播间。</summary>
    public bool AutoConnect { get; set; }

    // ---- 六类消息开关 ----

    /// <summary>弹幕。</summary>
    public bool ShowChat { get; set; } = true;

    /// <summary>点赞。</summary>
    public bool ShowLike { get; set; } = true;

    /// <summary>礼物。</summary>
    public bool ShowGift { get; set; } = true;

    /// <summary>入场。</summary>
    public bool ShowMember { get; set; } = true;

    /// <summary>房间统计（默认关：数据变化频繁，容易刷屏）。</summary>
    public bool ShowRoomStats { get; set; }

    /// <summary>粉丝团。</summary>
    public bool ShowFansclub { get; set; } = true;

    // ---- 输出模板（${变量名}，未赋值的占位符渲染时自动清除）----

    public string ChatTemplate { get; set; } = "[抖音] ${nickname}：${content}";
    public string LikeTemplate { get; set; } = "[抖音] ${nickname} 点了 ${count} 个赞";
    public string GiftTemplate { get; set; } = "[抖音] ${nickname} 送出 ${giftName} ×${giftCombo}";
    public string MemberTemplate { get; set; } = "[抖音] ${nickname} 进入直播间";
    public string RoomStatsTemplate { get; set; } = "[抖音] 当前在线 ${totalStr} 人（累计 ${totalPvForAnchor}）";
    public string FansclubTemplate { get; set; } = "[抖音] ${nickname} ${content}";

    // ---- 关键词过滤 ----

    /// <summary>disabled / blacklist / whitelist。</summary>
    public string FilterMode { get; set; } = "disabled";

    /// <summary>关键词，逗号或换行分隔，不区分大小写。</summary>
    public string Keywords { get; set; } = "";
}
