namespace MinecraftChatOverlay.Models;

public sealed class RoomInfo
{
    public long RoomId { get; set; }

    public long Uid { get; set; }

    public string Title { get; set; } = "";

    public string UserName { get; set; } = "";

    /// <summary>
    /// B站官方「人气」值（room_info.online）。
    /// 注意这是加权指标，不等于观看人数：实测有房间人气 52.6 万而只有 395 人看过。
    /// </summary>
    public int Online { get; set; }

    /// <summary>
    /// 本场累计「看过」人数。—— 这是真实的独立观众数，也是这里唯一代表真人的口径。
    /// B站只对部分房间提供该口径（其余房间这个位置显示的是人气），拿不到时为 0。
    /// </summary>
    public int Watched { get; set; }

    /// <summary>
    /// 本场互动人数（高能榜口径：「投喂、发弹幕均可上榜」）。
    /// 真实人数，但只统计互动过的观众，潜水观众不计。
    /// </summary>
    public int Interaction { get; set; }

    public bool LiveStatus { get; set; }

    public override string ToString() => $"{Title} - {UserName}";
}
