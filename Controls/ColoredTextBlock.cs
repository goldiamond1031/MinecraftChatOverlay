using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MinecraftChatOverlay.ViewModels;

namespace MinecraftChatOverlay.Controls;

/// <summary>根据分段集合生成带颜色/字重/斜体的 TextBlock。</summary>
public sealed class ColoredTextBlock : TextBlock
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments),
        typeof(IReadOnlyList<ChatSegmentViewModel>),
        typeof(ColoredTextBlock),
        new PropertyMetadata(null, OnSegmentsChanged));

    public IReadOnlyList<ChatSegmentViewModel>? Segments
    {
        get => (IReadOnlyList<ChatSegmentViewModel>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ColoredTextBlock)d).RebuildInlines();
    }

    private void RebuildInlines()
    {
        Inlines.Clear();
        var segments = Segments;
        if (segments == null)
        {
            return;
        }

        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment.Text))
            {
                continue;
            }

            Inlines.Add(new Run(segment.Text)
            {
                Foreground = segment.Foreground,
                FontWeight = segment.FontWeight,
                FontStyle = segment.FontStyle
            });
        }
    }
}
