namespace MinecraftChatOverlay.Models;

/// <summary>玩家查询显示字段：自定义显示名 + JSON 路径。</summary>
public sealed class PlayerQueryField
{
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";

    public PlayerQueryField Clone() => new() { Label = Label, Path = Path };
}
