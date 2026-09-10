namespace MinecraftChatOverlay.Models;

/// <summary>屏蔽关键词项，支持启用/禁用。</summary>
public sealed class BlockKeywordItem
{
    public string Keyword { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public string DisplayText => $"{(IsEnabled ? "✅" : "⛔")} {Keyword}";
}