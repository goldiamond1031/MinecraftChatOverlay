using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MinecraftChatOverlay.Models;

namespace MinecraftChatOverlay.ViewModels;

public sealed class NoticeItemViewModel : INotifyPropertyChanged
{
    private double _fontSize = 11;
    private DropShadowEffect? _shadow;

    public double FontSize
    {
        get => _fontSize;
        set => SetField(ref _fontSize, value);
    }

    public DropShadowEffect? Shadow
    {
        get => _shadow;
        set => SetField(ref _shadow, value);
    }

    public ObservableCollection<NoticeSegmentViewModel> Segments { get; } = new();

    public string? GiftKey { get; set; }

    public string? GiftName { get; set; }

    public int GiftCount { get; set; } = 1;

    public double GiftAmount { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static NoticeItemViewModel FromNotice(
        NoticeItem notice,
        Brush defaultForeground,
        double fontSize,
        DropShadowEffect? shadow,
        Func<string, Brush?>? giftColorResolver = null)
    {
        var vm = new NoticeItemViewModel
        {
            FontSize = fontSize,
            Shadow = shadow,
            GiftKey = notice.GiftKey,
            GiftName = notice.GiftName,
            GiftCount = notice.GiftCount,
            GiftAmount = notice.GiftAmount
        };

        if (notice.Segments is { Count: > 0 })
        {
            foreach (var segment in notice.Segments)
            {
                vm.Segments.Add(NoticeSegmentViewModel.FromSegment(segment, giftColorResolver, defaultForeground));
            }
        }
        else
        {
            vm.Segments.Add(new NoticeSegmentViewModel(notice.Text, defaultForeground, false));
        }

        return vm;
    }

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

public sealed class NoticeSegmentViewModel : INotifyPropertyChanged
{
    private string _text;

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }
    }

    public Brush Foreground { get; }

    public bool Bold { get; }

    public FontWeight FontWeight => Bold ? FontWeights.Bold : FontWeights.Normal;

    public NoticeSegmentViewModel(string text, Brush foreground, bool bold)
    {
        _text = text;
        Foreground = foreground;
        Bold = bold;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static NoticeSegmentViewModel FromSegment(
        NoticeSegment segment,
        Func<string, Brush?>? giftColorResolver = null,
        Brush? defaultForeground = null)
    {
        // 礼物/大航海名称与金额：优先用「礼物/粉丝牌颜色」页配置的颜色；
        // 匹配不到时用这条消息自己的兜底色（礼物通常自带金色）。
        if (!string.IsNullOrEmpty(segment.GiftColorKey))
        {
            return giftColorResolver?.Invoke(segment.GiftColorKey) is { } configured
                ? new NoticeSegmentViewModel(segment.Text, configured, segment.Bold)
                : new NoticeSegmentViewModel(segment.Text, ParseBrush(segment.Color, Brushes.LightGray), segment.Bold);
        }

        // 没有关联礼物颜色的段落（例如「XX投喂了」这句）用该区域的默认文字颜色，
        // 这样就能在设置界面里调，而不是写死在代码里。
        return new NoticeSegmentViewModel(
            segment.Text,
            defaultForeground ?? ParseBrush(segment.Color, Brushes.LightGray),
            segment.Bold);
    }

    private static Brush ParseBrush(string? value, Brush fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            }
        }
        catch
        {
        }

        return fallback;
    }
}
