using System.Collections.Generic;

namespace MinecraftChatOverlay.Plugins.PlayerQuery;

/// <summary>
/// 玩家查询的设置（原来在宿主的 AppSettings 里，字段名去掉了 PlayerQuery 前缀）。
/// 落盘位置：%APPDATA%\MinecraftChatOverlay\plugin-data\goldiamond.playerquery\settings.json
/// </summary>
public sealed class PlayerQuerySettings
{
    /// <summary>记住 API KEY（勾了才把上一行的值存盘）。</summary>
    public bool RememberKey { get; set; } = true;

    /// <summary>上次用的 API KEY（只在 RememberKey 为 true 时保存）。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>上次查询的玩家 ID。</summary>
    public string PlayerId { get; set; } = "";

    /// <summary>上次选的游戏类型 bedwars / skywars。</summary>
    public string GameType { get; set; } = "bedwars";

    /// <summary>上次选的模式显示名。</summary>
    public string Mode { get; set; } = "总览";

    /// <summary>要显示的字段（列表顺序就是显示顺序）。</summary>
    public List<PlayerQueryField> Fields { get; set; } = new();
}
