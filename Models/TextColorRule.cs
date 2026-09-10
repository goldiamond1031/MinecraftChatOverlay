namespace MinecraftChatOverlay.Models;

/// <summary>彩色渲染规则：聊天文本中出现指定文字时，用指定颜色/字重显示。</summary>
public sealed class TextColorRule
{
    public string Text { get; set; } = "";

    public string Color { get; set; } = "#FFFF0000";

    /// <summary>整句匹配时的非高亮部分颜色（例如正则匹配整句话，只把其中一组数字染成 Color）。</summary>
    public string MatchColor { get; set; } = "";

    /// <summary>Normal / Thin / Light / SemiBold / Bold</summary>
    public string FontWeight { get; set; } = "Normal";

    /// <summary>是否把 Text 当作正则表达式使用。</summary>
    public bool UseRegex { get; set; }

    /// <summary>正则模式下要高亮的捕获组序号，0 表示整个匹配。</summary>
    public int RegexGroup { get; set; }

    /// <summary>是否启用此规则。</summary>
    public bool IsEnabled { get; set; } = true;

    public string DisplayText
    {
        get
        {
            var enabledMark = IsEnabled ? "✅" : "⛔";
            if (string.IsNullOrWhiteSpace(Text))
            {
                return $"{enabledMark} (未设置文字)";
            }

            var mode = UseRegex ? $"正则(组{RegexGroup})" : "普通";
            var matchPart = string.IsNullOrWhiteSpace(MatchColor) ? "" : $" 整句={MatchColor}";
            return $"{enabledMark} {Text}  [{mode} {Color} {FontWeight}{matchPart}]";
        }
    }
}