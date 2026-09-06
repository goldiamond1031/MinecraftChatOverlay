using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MinecraftChatOverlay.ViewModels;

namespace MinecraftChatOverlay.Controls;

/// <summary>
/// 支持彩色分段、可开关文本选择的只读 RichTextBox。
/// 关闭文本选择时不允许鼠标点击，方便拖动悬浮窗；开启后可选中文字复制。
/// </summary>
public sealed class SelectableChatTextBox : RichTextBox
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<ChatSegmentViewModel>),
        typeof(SelectableChatTextBox),
        new PropertyMetadata(null, OnSegmentsChanged));

    public static readonly DependencyProperty TextSelectionEnabledProperty = DependencyProperty.Register(
        nameof(TextSelectionEnabled),
        typeof(bool),
        typeof(SelectableChatTextBox),
        new PropertyMetadata(false, OnTextSelectionEnabledChanged));

    public IReadOnlyList<ChatSegmentViewModel>? Segments
    {
        get => (IReadOnlyList<ChatSegmentViewModel>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public bool TextSelectionEnabled
    {
        get => (bool)GetValue(TextSelectionEnabledProperty);
        set => SetValue(TextSelectionEnabledProperty, value);
    }

    public SelectableChatTextBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        IsInactiveSelectionHighlightEnabled = true;
        UpdateHitTest();
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SelectableChatTextBox)d).RebuildDocument();
    }

    private static void OnTextSelectionEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SelectableChatTextBox)d).UpdateHitTest();
    }

    private void UpdateHitTest()
    {
        IsHitTestVisible = TextSelectionEnabled;
        Focusable = TextSelectionEnabled;
        IsReadOnlyCaretVisible = TextSelectionEnabled;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty ||
            e.Property == FontSizeProperty ||
            e.Property == ForegroundProperty)
        {
            RebuildDocument();
        }
    }

    private void RebuildDocument()
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0)
        };

        var segments = Segments;
        if (segments != null)
        {
            foreach (var segment in segments)
            {
                if (string.IsNullOrEmpty(segment.Text))
                {
                    continue;
                }

                paragraph.Inlines.Add(new Run(segment.Text)
                {
                    Foreground = segment.Foreground,
                    FontWeight = segment.FontWeight,
                    FontStyle = segment.FontStyle
                });
            }
        }

        var document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = FontFamily,
            FontSize = FontSize,
            Foreground = Foreground
        };

        Document = document;
    }
}
