using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MinecraftChatOverlay.Plugins.NetEaseLyric;

/// <summary>
/// 歌词悬浮窗。
///
/// 视觉是照参考项目做的：**主文字后面叠一层同样文字当"影子"**（颜色/透明度/模糊/偏移都能调），
/// 不靠背景色压住画面，纯靠影子把字从游戏画面里"抠"出来。
/// 行为：
///   - 锁定（默认玩的时候开）：鼠标点击穿透（WS_EX_TRANSPARENT），点不到、不挡操作；
///   - 解锁：可以用鼠标拖动，松手把位置存回配置。
/// </summary>
public partial class LyricWindow : Window
{
    private readonly Action _onPositionChanged;
    private NetEaseLyricSettings _settings;
    private bool _dragging;
    private Point _dragStart;
    private string _lastCurrentLine = "\u0000";

    public LyricWindow(NetEaseLyricSettings settings, Action onPositionChanged)
    {
        _settings = settings;
        _onPositionChanged = onPositionChanged;

        InitializeComponent();

        Width = Math.Max(320, settings.WindowWidth);
        Height = Math.Max(100, settings.WindowHeight);

        if (settings.WindowX > -9000 && settings.WindowY > -9000)
        {
            Left = settings.WindowX;
            Top = settings.WindowY;
        }
        else
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = SystemParameters.PrimaryScreenHeight - Height - 80;
        }

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;

        ApplySettings(settings);

        SourceInitialized += (_, _) =>
        {
            ApplyWindowStyles();
            ApplyClickThrough();
        };
    }

    // ------------------------------------------------------------ 应用设置

    /// <summary>把设置应用到控件上（改设置后立刻调用，所见即所得）。</summary>
    public void ApplySettings(NetEaseLyricSettings settings)
    {
        _settings = settings;

        var family = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily) ? "Microsoft YaHei UI" : settings.FontFamily);
        var weight = settings.Bold ? FontWeights.Bold : FontWeights.Normal;
        var textBrush = Brush(settings.TextColor, Colors.White);
        var shadowBrush = Brush(settings.ShadowColor, Colors.Black);

        // 主文字 + 影子
        foreach (var block in new[] { CurrentText, CurrentShadow })
        {
            block.FontFamily = family;
            block.FontSize = Math.Clamp(settings.FontSize, 12, 96);
            block.FontWeight = weight;
        }

        CurrentText.Foreground = textBrush;
        CurrentShadow.Foreground = shadowBrush;
        CurrentShadow.Opacity = Math.Clamp(settings.ShadowOpacity / 255.0, 0, 1);
        CurrentShadow.Visibility = settings.ShadowOpacity > 0 ? Visibility.Visible : Visibility.Hidden;
        CurrentShadow.RenderTransform = new TranslateTransform(settings.ShadowOffsetX, settings.ShadowOffsetY);
        CurrentShadow.Effect = settings.ShadowBlur > 0 ? new BlurEffect { Radius = settings.ShadowBlur } : null;

        // 歌名 / 下一句 / 翻译：字号按比例缩，用同一套颜色但淡一点
        var small = Math.Clamp(settings.FontSize * 0.42, 10, 30);
        TrackText.FontFamily = family;
        TrackText.FontSize = small;
        TrackText.FontWeight = FontWeights.Normal;
        TrackText.Foreground = WithOpacity(textBrush, 0.75);
        TrackText.Visibility = settings.ShowTrackInfo ? Visibility.Visible : Visibility.Collapsed;

        var nextSize = Math.Clamp(settings.FontSize * Math.Clamp(settings.NextLineScale, 0.3, 1.0), 10, 60);
        NextText.FontFamily = family;
        NextText.FontSize = nextSize;
        NextText.FontWeight = weight;
        NextText.Foreground = WithOpacity(textBrush, 0.55);
        NextText.Effect = new DropShadowEffect
        {
            Color = Colors.Black,
            ShadowDepth = 1.5,
            BlurRadius = 6,
            Opacity = 0.65,
        };
        NextText.Visibility = settings.ShowNextLine ? Visibility.Visible : Visibility.Collapsed;

        TranslationText.FontFamily = family;
        TranslationText.FontSize = Math.Clamp(settings.FontSize * 0.55, 10, 40);
        TranslationText.FontWeight = FontWeights.Normal;
        TranslationText.Foreground = WithOpacity(textBrush, 0.85);
        TranslationText.Effect = new DropShadowEffect
        {
            Color = Colors.Black,
            ShadowDepth = 1.5,
            BlurRadius = 6,
            Opacity = 0.65,
        };
        TranslationText.Visibility = settings.ShowTranslation ? Visibility.Visible : Visibility.Collapsed;

        // 底色（默认完全透明）
        var alpha = (byte)Math.Clamp(settings.BackgroundOpacity, 0, 255);
        RootGrid.Background = alpha == 0
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));

        ApplyClickThrough();
    }

    /// <summary>刷新一帧。</summary>
    public void UpdateFrame(LyricFrame frame)
    {
        var current = frame.Current;
        var message = frame.Message;

        // 没到第一句 / 没歌词时，把说明文字放到主行位置上，别让窗空着
        var mainText = current.Length > 0
            ? current
            : (message.Length > 0 ? message : "♪");

        var isRealLine = current.Length > 0;
        var faded = isRealLine ? 1.0 : 0.66;

        if (!string.Equals(_lastCurrentLine, mainText, StringComparison.Ordinal))
        {
            _lastCurrentLine = mainText;
            CurrentText.Text = mainText;
            CurrentShadow.Text = mainText;
            FadeInCurrentLine();
        }

        CurrentText.Opacity = faded;
        CurrentShadow.Opacity = Math.Clamp(_settings.ShadowOpacity / 255.0, 0, 1) * faded;

        TrackText.Text = frame.Title.Length > 0 ? $"{frame.Title} · {frame.Artist}" : "";

        var showNext = _settings.ShowNextLine && isRealLine && frame.Next.Length > 0;
        NextText.Text = showNext ? frame.Next : "";
        NextText.Visibility = showNext && _settings.ShowNextLine ? Visibility.Visible : Visibility.Collapsed;

        var translation = _settings.ShowTranslation ? frame.Translation : "";
        TranslationText.Text = translation;
        TranslationText.Visibility = translation.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Visibility = _settings.HideWhenPaused && !frame.Playing && !frame.Loading && !isRealLine
            ? Visibility.Hidden
            : Visibility.Visible;
    }

    // ------------------------------------------------------------ 动效

    private void FadeInCurrentLine()
    {
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        CurrentLineHost.BeginAnimation(OpacityProperty, animation);

        var slide = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        CurrentText.RenderTransform = new TranslateTransform();
        CurrentText.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slide);

        CurrentShadow.RenderTransform = new TranslateTransform(_settings.ShadowOffsetX, _settings.ShadowOffsetY);
    }

    // ------------------------------------------------------------ 拖动 / 穿透

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Locked)
        {
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _settings.Locked)
        {
            return;
        }

        var point = e.GetPosition(this);
        Left += point.X - _dragStart.X;
        Top += point.Y - _dragStart.Y;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        _settings.WindowX = Left;
        _settings.WindowY = Top;
        _onPositionChanged();
    }

    /// <summary>鼠标穿透：锁定后整窗不接收鼠标事件（玩游戏时点得到下面）。</summary>
    private void ApplyClickThrough()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            style = _settings.Locked ? style | WsExTransparent : style & ~WsExTransparent;
            SetWindowLong(handle, GwlExStyle, style);
        }
        catch
        {
            // 加不上就算了，最多是挡住点击
        }
    }

    /// <summary>工具窗 + 不抢焦点：不出现在 Alt+Tab 里，也不会把游戏的焦点抢走。</summary>
    private void ApplyWindowStyles()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            style |= WsExToolWindow | WsExNoActivate;
            SetWindowLong(handle, GwlExStyle, style);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>把窗口挪回屏幕底部居中（插件页的「重置位置」）。</summary>
    public void ResetPosition()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = SystemParameters.PrimaryScreenHeight - Height - 80;
        _settings.WindowX = Left;
        _settings.WindowY = Top;
        _onPositionChanged();
    }

    // ------------------------------------------------------------ 小工具

    private static Brush Brush(string hex, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color color)
            {
                return new SolidColorBrush(color);
            }
        }
        catch
        {
            // 用户填错了就用兜底色
        }

        return new SolidColorBrush(fallback);
    }

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        if (brush is SolidColorBrush solid)
        {
            var color = solid.Color;
            return new SolidColorBrush(Color.FromArgb((byte)(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B));
        }

        return brush;
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
