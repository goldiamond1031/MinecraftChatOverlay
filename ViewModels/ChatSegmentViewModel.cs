using System.Windows;
using System.Windows.Media;

namespace MinecraftChatOverlay.ViewModels;

/// <summary>聊天消息中的一段文字，可带颜色/字重/斜体。</summary>
public sealed class ChatSegmentViewModel
{
    public string Text { get; set; } = "";

    public Brush Foreground { get; set; } = Brushes.White;

    public FontWeight FontWeight { get; set; } = FontWeights.Normal;

    public FontStyle FontStyle { get; set; } = FontStyles.Normal;

    /// <summary>是否为用户彩色渲染规则主动染色的片段；玩家发言颜色不会覆盖它。</summary>
    public bool IsUserColored { get; set; }

    /// <summary>下划线（游戏内样式）。</summary>
    public bool Underline { get; set; }

    /// <summary>删除线（游戏内样式）。</summary>
    public bool Strikethrough { get; set; }

    public ChatSegmentViewModel()
    {
    }

    public ChatSegmentViewModel(string text, Brush foreground, FontWeight fontWeight, FontStyle fontStyle = default)
    {
        Text = text;
        Foreground = foreground;
        FontWeight = fontWeight;
        FontStyle = fontStyle;
    }
}
