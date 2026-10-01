using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;
using System.Windows.Media.Effects;

namespace MinecraftChatOverlay.Plugins.NeteaseLyrics;

/// <summary>
/// 歌词窗：无边框、透明、置顶、不占任务栏；纯文字 + 模糊偏移阴影（外观参考 netease_lyric_shadow）。
/// 不设 WS_EX_NOACTIVATE（那是"点了也不激活"，跟这个窗没关系）；WS_EX_TOOLWINDOW 是**要设的**，
/// 不设的话它会被算成一个独立窗口、跑到任务栏里去 —— 见 MakeToolWindow。
/// </summary>
public partial class LyricsWindow : Window
{
    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")] private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int index, IntPtr value);

    public LyricsWindow()
    {
        InitializeComponent();
    }

    public bool ClickThrough { get; set; }

    /// <summary>当前的设置快照（ShowLine 要按它算已唱/未唱的颜色）。</summary>
    private NeteaseLyricsSettings? _settings;

    /// <summary>逐字歌词里"已唱"那一半的颜色。</summary>
    private SolidColorBrush _karaokeBrush = new(Color.FromRgb(0x7F, 0xD8, 0xFF));

    /// <summary>逐字歌词里"还没唱"那一半的颜色（文字色压低透明度）。</summary>
    private SolidColorBrush _dimBrush = new(Color.FromArgb(0x6B, 0xFF, 0xFF, 0xFF));

    public void ApplySettings(NeteaseLyricsSettings settings)
    {
        _settings = settings;

        var family = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily) ? "Microsoft YaHei UI" : settings.FontFamily);
        var weight = settings.Bold ? FontWeights.Bold : FontWeights.Normal;
        var textColor = ParseColor(settings.TextColor, Colors.White);
        var foreground = new SolidColorBrush(textColor);
        var shadow = ParseColor(settings.ShadowColor, Colors.Black);

        // 逐字：已唱 = KaraokeColor；未唱 = 文字色压到约 42% 不透明度（同一盏灯，只是暗着）
        _karaokeBrush = ParseBrush(settings.KaraokeColor, Color.FromRgb(0x7F, 0xD8, 0xFF));
        _dimBrush = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(textColor.A * 0.42), 0, 255),
            textColor.R,
            textColor.G,
            textColor.B));

        CurrentText.FontFamily = family;
        CurrentText.FontWeight = weight;
        CurrentText.FontSize = Math.Max(10, settings.FontSize);
        CurrentText.Foreground = foreground;
        ApplyShadow(CurrentShadow, shadow, settings);

        // 翻译 / 罗马音 用一个缩放，下一句用另一个缩放 —— 互不影响
        var translationSize = Math.Max(10, settings.FontSize * Math.Clamp(settings.TranslationScale, 0.3, 1.5));
        var nextSize = Math.Max(10, settings.FontSize * Math.Clamp(settings.NextLineScale, 0.3, 1.5));

        TranslationText.FontFamily = family;
        TranslationText.FontSize = translationSize;
        // 翻译跟"文字颜色"走 —— 不再用 settings.TranslationColor：
        // 那个字段界面上根本没地方改，默认值 #FFDDEEFF 又和罗马音色很接近，
        // 结果看起来就像"翻译跟着罗马音颜色走"。
        // 这里拿文字色压到 90% 不透明度：跟着文字色变，又比当前行稍微轻一点。
        TranslationText.Foreground = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(textColor.A * 0.9), 0, 255),
            textColor.R,
            textColor.G,
            textColor.B));
        ApplyShadow(TranslationShadow, shadow, settings);

        RomajiText.FontFamily = family;
        RomajiText.FontSize = Math.Max(10, settings.FontSize * Math.Clamp(settings.RomajiScale, 0.2, 1.5));
        RomajiText.FontStyle = settings.RomajiItalic ? FontStyles.Italic : FontStyles.Normal;
        RomajiText.Foreground = ParseBrush(settings.RomajiColor, textColor);
        ApplyShadow(RomajiShadow, shadow, settings);

        NextText.FontFamily = family;
        NextText.FontSize = nextSize;
        NextText.Foreground = foreground;
        ApplyShadow(NextShadow, shadow, settings);

        ProgressText.FontFamily = family;
        ApplyShadow(ProgressShadow, shadow, settings);

        RootGrid.Background = settings.BackgroundOpacity > 0.005
            ? new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(settings.BackgroundOpacity, 0, 1) * 255), 0, 0, 0))
            : Brushes.Transparent;

        ClickThrough = settings.ClickThrough;
        Width = Math.Max(240, settings.WindowWidth);
        Left = settings.WindowLeft;
        Top = settings.WindowTop;
        UpdateTransparent();
    }

    /// <summary>
    /// 刷新一帧。<paramref name="karaoke"/> 为 true 时，当前行按 <paramref name="sungChars"/> 拆成
    /// "已唱 / 未唱"两段染色（逐字歌词）。
    /// </summary>
    public void ShowLine(string current, string? translation, string? romaji, string? next, string progress,
                         bool showTranslation, bool showRomaji, bool showNext, bool showTrackInfo,
                         bool karaoke = false, int sungChars = -1)
    {
        SetCurrentLine(current, karaoke ? sungChars : -1);

        TranslationText.Text = translation ?? "";
        TranslationText.Visibility = showTranslation && !string.IsNullOrWhiteSpace(translation) ? Visibility.Visible : Visibility.Collapsed;

        RomajiText.Text = romaji ?? "";
        RomajiText.Visibility = showRomaji && !string.IsNullOrWhiteSpace(romaji) ? Visibility.Visible : Visibility.Collapsed;

        NextText.Text = next ?? "";
        NextText.Visibility = showNext && !string.IsNullOrWhiteSpace(next) ? Visibility.Visible : Visibility.Collapsed;

        ProgressText.Text = progress;
        ProgressText.Visibility = showTrackInfo && !string.IsNullOrWhiteSpace(progress) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 画当前行。<paramref name="sungChars"/> 小于 0 = 没有逐字数据，整行一个颜色；
    /// 否则 0..文字长度 = 已经唱到第几个字。
    /// </summary>
    private void SetCurrentLine(string text, int sungChars)
    {
        // 上一帧可能是逐字（Inlines 里有 Run），也可能是普通文字（Text 直接赋值）——
        // 两条路都要先把内容清干净，否则会残留上一帧的染色。
        if (sungChars < 0 || string.IsNullOrEmpty(text))
        {
            if (CurrentText.Inlines.Count > 0)
            {
                CurrentText.Inlines.Clear();
            }

            CurrentText.Text = text;
            return;
        }

        var sung = Math.Clamp(sungChars, 0, text.Length);

        CurrentText.Inlines.Clear();
        if (sung > 0)
        {
            CurrentText.Inlines.Add(new Run(text[..sung]) { Foreground = _karaokeBrush });
        }

        if (sung < text.Length)
        {
            CurrentText.Inlines.Add(new Run(text[sung..]) { Foreground = _dimBrush });
        }
    }

    public bool IsReallyVisible()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            return handle != IntPtr.Zero && IsWindowVisible(handle);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清掉所有者窗口（宿主主窗口在托盘里时，被拥有的窗口会一直 IsWindowVisible=false）。</summary>
    public void DetachFromOwner()
    {
        try
        {
            Owner = null;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(handle, GwlpHwndParent, IntPtr.Zero);
            }
            else
            {
                SetWindowLongPtr32(handle, GwlpHwndParent, IntPtr.Zero);
            }

            // 顺手把"不占任务栏"钉死 —— 见 MakeToolWindow 的注释
            MakeToolWindow();
        }
        catch
        {
        }
    }

    /// <summary>
    /// 不让它出现在任务栏 / Alt+Tab 里。
    ///
    /// 为什么不靠 WPF 的 ShowInTaskbar="False"：那个是靠"给窗口挂一个隐藏所有者"实现的，
    /// 而我们为了让歌词窗不被主窗口带着最小化，又必须把所有者清掉（DetachFromOwner）——
    /// 一清掉就退回成普通顶层窗口：任务栏里立刻多出一个按钮，用户还能右键把它关掉，
    /// 关掉之后这个 Window 实例就废了（WPF 不允许 Show 一个已 Close 的窗口）。
    /// 所以这里直接上 WS_EX_TOOLWINDOW：系统层面"这不是一个应用主窗口"的标记，
    /// 和所有者是谁无关，任务栏和 Alt+Tab 都会跳过它。
    /// </summary>
    public void MakeToolWindow()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            if ((style & WsExToolWindow) != 0)
            {
                return;
            }

            SetWindowLong(handle, GwlExStyle, style | WsExToolWindow);
        }
        catch
        {
        }
    }


    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DetachFromOwner();
        UpdateTransparent();
    }

    private void UpdateTransparent()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            style = ClickThrough ? (style | WsExTransparent) : (style & ~WsExTransparent);
            SetWindowLong(handle, GwlExStyle, style);
        }
        catch
        {
        }
    }

    private static void ApplyShadow(DropShadowEffect effect, Color color, NeteaseLyricsSettings settings)
    {
        effect.Color = color;
        effect.BlurRadius = Math.Max(0, settings.ShadowBlur);
        effect.Opacity = Math.Clamp(settings.ShadowOpacity, 0, 1);
        effect.ShadowDepth = Math.Max(0, settings.ShadowOffset);
        effect.Direction = ((settings.ShadowDirection % 360) + 360) % 360;
    }

    private static SolidColorBrush ParseBrush(string? text, Color fallback) => new(ParseColor(text, fallback));

    private static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) && ColorConverter.ConvertFromString(text) is Color color)
            {
                return color;
            }
        }
        catch
        {
        }

        return fallback;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!ClickThrough)
        {
            try { DragMove(); } catch { }
        }
    }
}
