namespace MinecraftChatOverlay.Models;

/// <summary>用于导出/导入文本处理规则的 JSON 结构。</summary>
public sealed class TextRuleExport
{
    public List<TextColorRule> ColorRules { get; set; } = new();

    public List<TextReplaceRule> ReplaceRules { get; set; } = new();

    public List<BlockKeywordItem> BlockKeywords { get; set; } = new();
}
