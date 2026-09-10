namespace MinecraftChatOverlay.Models;

/// <summary>文本自动替换规则。</summary>
public sealed class TextReplaceRule
{
    public string FindText { get; set; } = "";

    public string ReplaceText { get; set; } = "";

    /// <summary>为 true 时只替换玩家发言中的内容部分（ID &gt; 内容），不修改前缀/昵称。</summary>
    public bool OnlyPlayerContent { get; set; }

    /// <summary>是否把 FindText 当作正则表达式使用，ReplaceText 中可使用 $1 等。</summary>
    public bool UseRegex { get; set; }

    /// <summary>是否启用此规则。</summary>
    public bool IsEnabled { get; set; } = true;

    public string DisplayText
    {
        get
        {
            var enabledMark = IsEnabled ? "✅" : "⛔";
            if (string.IsNullOrWhiteSpace(FindText))
            {
                return $"{enabledMark} (未设置查找文字)";
            }

            return $"{enabledMark} {FindText}  →  {ReplaceText}{(UseRegex ? "  [正则]" : "")}{(OnlyPlayerContent ? "  [仅玩家内容]" : "")}";
        }
    }
}