namespace MinecraftChatOverlay.Models;

/// <summary>屏蔽关键词项，支持启用/禁用。</summary>
public sealed class BlockKeywordItem
{
    public string Keyword { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 只在这一条是"玩家发言"时才拿它做屏蔽判断。
    ///
    /// 用来区分"玩家说了这个字"和"系统因为某个原因提到了这个字" ——
    /// 比如屏蔽词是某个玩家的 ID，系统消息（进服/退服/成就）里也会带上它，
    /// 那种情况往往并不想一起吞掉。和替换规则那边的同款开关语义一致：
    /// **只看关键词在不在「玩家发言的内容段」里**，前缀 / 系统整句都不算。
    /// </summary>
    public bool OnlyPlayerContent { get; set; }

    public string DisplayText =>
        $"{(IsEnabled ? "✅" : "⛔")} {Keyword}{(OnlyPlayerContent ? "  [仅玩家内容]" : "")}";
}