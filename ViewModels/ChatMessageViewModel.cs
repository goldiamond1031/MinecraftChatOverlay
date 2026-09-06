using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MinecraftChatOverlay.ViewModels;

public sealed class ChatMessageViewModel : INotifyPropertyChanged
{
    private string _text = "";
    private FontFamily _fontFamily = new("Microsoft YaHei UI");
    private double _fontSize = 16;
    private FontWeight _fontWeight = FontWeights.Normal;
    private Brush _foreground = Brushes.White;
    private DropShadowEffect? _shadow;
    private double _maxTextWidth = 360;
    private bool _textSelectionEnabled;
    private IReadOnlyList<ChatSegmentViewModel> _segments = Array.Empty<ChatSegmentViewModel>();

    /// <summary>不带时间前缀和手工换行的原始聊天内容。</summary>
    public string RawText { get; set; } = "";

    /// <summary>消息被接收的本地时间。</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    /// <summary>用于彩色渲染的分段文本。</summary>
    public IReadOnlyList<ChatSegmentViewModel> Segments
    {
        get => _segments;
        set => SetField(ref _segments, value);
    }

    public FontFamily FontFamily
    {
        get => _fontFamily;
        set => SetField(ref _fontFamily, value);
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetField(ref _fontSize, value);
    }

    public FontWeight FontWeight
    {
        get => _fontWeight;
        set => SetField(ref _fontWeight, value);
    }

    public Brush Foreground
    {
        get => _foreground;
        set => SetField(ref _foreground, value);
    }

    public DropShadowEffect? Shadow
    {
        get => _shadow;
        set => SetField(ref _shadow, value);
    }

    public double MaxTextWidth
    {
        get => _maxTextWidth;
        set => SetField(ref _maxTextWidth, value);
    }

    public bool TextSelectionEnabled
    {
        get => _textSelectionEnabled;
        set => SetField(ref _textSelectionEnabled, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
