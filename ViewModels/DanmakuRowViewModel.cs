using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MinecraftChatOverlay.Models;

namespace MinecraftChatOverlay.ViewModels;

public sealed class DanmakuRowViewModel : INotifyPropertyChanged
{
    private string _displayText = "";
    private ImageSource? _avatar;
    private Brush _nameBrush = Brushes.White;
    private Brush _textBrush = Brushes.White;
    private Brush _medalBrush = Brushes.Orange;
    private string _medalText = "";
    private DropShadowEffect? _textShadow;
    private DropShadowEffect? _nameShadow;
    private double _fontSize = 15;
    private double _nameFontSize = 12;
    private double _textMaxWidth = 370;

    public long UserId { get; }

    public string UserName { get; }

    public string AvatarUrl { get; }

    public string RawText { get; }

    public string MedalName { get; }

    public int MedalLevel { get; }

    public int GuardLevel { get; }

    public bool HasMedal => !string.IsNullOrEmpty(MedalName) && MedalLevel > 0;

    public bool HasGuard => GuardLevel > 0;

    public string GuardText => GuardLevel switch
    {
        1 => "总督",
        2 => "提督",
        3 => "舰长",
        _ => ""
    };

    public Brush GuardBrush => GuardLevel switch
    {
        1 => new SolidColorBrush(Color.FromRgb(0xE6, 0x3E, 0x3E)),
        2 => new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
        3 => new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
        _ => Brushes.Transparent
    };

    public string UserIdText => UserId > 0 ? $"UID:{UserId}" : "";

    /// <summary>
    /// SC 金额（元）。只有 SC 区造出来的行才 > 0，普通弹幕行是 0。
    /// 在加进列表之前就设置好，所以这几个计算属性不需要通知变更。
    /// </summary>
    public double ScAmount { get; set; }

    /// <summary>是否显示 SC 标识。</summary>
    public bool HasScBadge => ScAmount > 0;

    public string ScBadgeText => ScAmount > 0 ? $"SC ¥{ScAmount:0.##}" : "";

    /// <summary>SC 标识底色，按金额分档（和 B站 SC 的配色习惯一致）。</summary>
    public Brush ScBadgeBrush => ScAmount switch
    {
        <= 0 => Brushes.Transparent,
        < 50 => new SolidColorBrush(Color.FromRgb(0x2A, 0x6B, 0xE8)),
        < 100 => new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x1E)),
        < 500 => new SolidColorBrush(Color.FromRgb(0xE6, 0x6A, 0x1E)),
        < 1000 => new SolidColorBrush(Color.FromRgb(0xE8, 0x3E, 0x8C)),
        _ => new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F))
    };

    public string DisplayText
    {
        get => _displayText;
        set => SetField(ref _displayText, value);
    }

    public ImageSource? Avatar
    {
        get => _avatar;
        set => SetField(ref _avatar, value);
    }

    public Brush NameBrush
    {
        get => _nameBrush;
        set => SetField(ref _nameBrush, value);
    }

    public Brush TextBrush
    {
        get => _textBrush;
        set => SetField(ref _textBrush, value);
    }

    public Brush MedalBrush
    {
        get => _medalBrush;
        set => SetField(ref _medalBrush, value);
    }

    public string MedalText
    {
        get => _medalText;
        set => SetField(ref _medalText, value);
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetField(ref _fontSize, value);
    }

    public double NameFontSize
    {
        get => _nameFontSize;
        set => SetField(ref _nameFontSize, value);
    }

    public double TextMaxWidth
    {
        get => _textMaxWidth;
        set => SetField(ref _textMaxWidth, value);
    }

    public DropShadowEffect? TextShadow
    {
        get => _textShadow;
        set => SetField(ref _textShadow, value);
    }

    public DropShadowEffect? NameShadow
    {
        get => _nameShadow;
        set => SetField(ref _nameShadow, value);
    }

    public DanmakuRowViewModel(DanmakuItem item, int wrapLength, Brush defaultTextBrush, Brush defaultNameBrush, DropShadowEffect? textShadow, DropShadowEffect? nameShadow, double fontSize = 15, double textMaxWidth = 370)
    {
        UserId = item.UserId;
        UserName = item.UserName;
        AvatarUrl = item.Face;
        RawText = item.Text;
        MedalName = item.MedalName ?? "";
        MedalLevel = item.MedalLevel;
        GuardLevel = item.GuardLevel;
        DisplayText = WrapText(item.Text, wrapLength);
        TextBrush = defaultTextBrush;
        NameBrush = defaultNameBrush;
        TextShadow = textShadow;
        NameShadow = nameShadow;
        FontSize = fontSize;
        NameFontSize = Math.Max(10, fontSize * 0.8);
        TextMaxWidth = Math.Max(160, textMaxWidth);

        if (HasMedal)
        {
            MedalText = $" {MedalName} {MedalLevel}级 ";
            // 粉丝团灯牌默认淡黄色，可被全局/礼物配置覆盖。
            MedalBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x66));
        }
        else
        {
            MedalText = "";
        }
    }

    /// <summary>按配置的字符数在弹幕中插入换行。</summary>
    public static string WrapText(string text, int wrapLength)
    {
        if (wrapLength <= 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 8);
        var count = 0;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n')
            {
                sb.Append(ch);
                count = 0;
                continue;
            }

            sb.Append(ch);
            count++;

            if (count >= wrapLength)
            {
                sb.AppendLine();
                count = 0;
            }
        }

        return sb.ToString();
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
