using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;



namespace MinecraftChatOverlay.Plugins.RegionMagnifier;

/// <summary>
/// 「区域放大」的显示窗口：把游戏窗口里框出来的那一小块，实时抓屏放大挂在这里。
///
/// 几个关键决定（都是踩过坑之后的结论）：
///  · 抓屏走 <see cref="RegionFrameGrabber"/>：屏幕 DC + CAPTUREBLT，这样硬件加速
///    渲染的游戏画面才抓得到（坑 44）。显示窗口自己用 WDA_EXCLUDEFROMCAPTURE
///    从抓屏里排除，否则选区一旦和它重叠就会无限自己拍自己。
///  · 不抢焦点（WS_EX_NOACTIVATE）：Minecraft 一失焦就自己弹 ESC 菜单。
///  · 重申置顶那句**必须等第一次布局跑完**再发。窗口刚变可见时调 SetWindowPos
///    会把窗口尺寸冻死在"还没布局"的 2px（坑 43，悬浮窗就是这么坏过一次）。
/// </summary>
public partial class RegionMagnifierWindow : Window
{
    private readonly RegionMagnifierSettings _settings;
    private readonly RegionFrameGrabber _grabber = new();

    private WriteableBitmap? _bitmap;
    private DispatcherTimer? _frameTimer;
    private DispatcherTimer? _keepAliveTimer;
    private IntPtr _targetHandle = IntPtr.Zero;
    private int _resolveCooldown;
    private bool _ready;

    /// <summary>显示窗口被拖动后触发（参数是新的 DIP 位置），页面据此记忆位置。</summary>
    public event Action<double, double>? Moved;

    public RegionMagnifierWindow(RegionMagnifierSettings settings)
    {
        _settings = settings;

        InitializeComponent();

        ApplySettings();
        PositionFromSettings();

        // 第一次布局之后再做"改动窗口 Win32 状态"的事（坑 43）
        Loaded += (_, _) =>
        {
            _ready = true;
            ApplySettings();   // 这时才拿得到真实 DPI，按它重算一次窗口大小
            ApplyWindowStyles();
            StartTimers();
        };

        LocationChanged += (_, _) =>
        {
            if (_ready)
            {
                Moved?.Invoke(Left, Top);
            }
        };

        MouseLeftButtonDown += (_, _) =>
        {
            if (_settings.ClickThrough)
            {
                return;
            }

            try
            {
                DragMove();
            }
            catch
            {
                // 按钮已经松开时 DragMove 会抛，忽略即可
            }
        };

        Closed += (_, _) => StopEverything();

        // 藏起来的窗口不该继续抓屏：用户点了"隐藏"却发现 CPU 还在按帧跑，会以为没关掉
        IsVisibleChanged += (_, _) =>
        {
            if (!_ready)
            {
                return;
            }

            if (IsVisible)
            {
                StartTimers();
            }
            else
            {
                PauseTimers();
            }
        };
    }

    /// <summary>当前状态一句话（给页面上的状态文本用）。</summary>
    public string StatusText { get; private set; } = "还没开始抓取";

    /// <summary>显示窗口当前的 DIP 位置与大小（页面保存设置时读它）。</summary>
    public (double Left, double Top, double Width, double Height) Bounds
        => (Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);

    /// <summary>配置变了（换了选区 / 倍数 / 帧率 / 开关）后调一次即可全部生效。</summary>
    public void ApplySettings()
    {
        var s = _settings;

        // DPI 要窗口显示出来之后才拿得准；拿到之前先按 1:1 算，Loaded 后会再调一次
        var scaleX = 1.0;
        var scaleY = 1.0;
        if (_ready)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0)
            {
                scaleX = dpi.DpiScaleX;
            }

            if (dpi.DpiScaleY > 0)
            {
                scaleY = dpi.DpiScaleY;
            }
        }

        var zoom = Math.Clamp(s.Zoom, 1, 8);
        var desiredWidth = Math.Max(24, s.RegionWidth * zoom / scaleX);
        var desiredHeight = Math.Max(24, s.RegionHeight * zoom / scaleY);

        MinWidth = 24;
        MinHeight = 24;
        Width = desiredWidth;
        Height = desiredHeight;

        Frame.BorderThickness = s.ShowBorder ? new Thickness(1) : new Thickness(0);
        Frame.BorderBrush = s.ShowBorder ? new SolidColorBrush(Color.FromRgb(0x6E, 0x56, 0xCF)) : Brushes.Transparent;

        RenderOptions.SetBitmapScalingMode(
            Preview,
            s.SmoothScale ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);

        _bitmap = null;                 // 选区尺寸可能变了，下一帧重建
        Preview.Source = null;

        if (_ready)
        {
            ApplyWindowStyles();
            StartTimers();
        }

        ClampIntoScreen();
    }

    /// <summary>把窗口摆到设置里记的位置。</summary>
    public void PositionFromSettings()
    {
        var s = _settings;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = s.DisplayLeft;
        Top = s.DisplayTop;
    }

    /// <summary>挪到主屏右上角（用户点了「放右上角」）。</summary>
    public void MoveToTopRight()
    {
        var area = SystemParameters.WorkArea;
        var margin = 16.0;
        Left = area.Right - ActualWidth - margin;
        Top = area.Top + margin;
    }

    /// <summary>外部主动重申置顶（例如游戏窗口刚铺满屏幕）。</summary>
    public void ReassertTopmost()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            RegionNative.RaiseTopmost(handle);
        }
        catch
        {
            // 抬不起来不该让调用方崩
        }
    }

    /// <summary>
    /// 把窗口拉回屏幕可见区域。
    /// 默认位置是按 100% 缩放写的，到了 150%/200% 或换过显示器就可能整块落在屏幕外
    /// —— 那时用户只看到状态里写着"正在抓取"，屏幕上却没有窗口。整块不可见时统一挪到右上角。
    /// </summary>
    private void ClampIntoScreen()
    {
        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        if (double.IsNaN(width) || width <= 0 || double.IsNaN(height) || height <= 0)
        {
            return;
        }

        var left = double.IsNaN(Left) ? area.Left + 16 : Left;
        var top = double.IsNaN(Top) ? area.Top + 16 : Top;

        var fullyVisible = left >= area.Left - 1 && top >= area.Top - 1
                           && left + width <= area.Right + 1 && top + height <= area.Bottom + 1;

        if (fullyVisible)
        {
            Left = left;
            Top = top;
            return;
        }

        Left = Math.Max(area.Left + 16, area.Right - width - 16);
        Top = area.Top + 16;
    }

    private void ApplyWindowStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var s = _settings;

        RegionNative.UpdateExStyle(handle, RegionNative.WsExNoActivate, true);
        RegionNative.UpdateExStyle(handle, RegionNative.WsExTransparent, s.ClickThrough);

        // 把自己从抓屏里排除，避免"自己拍自己"（Win10 2004+ 支持；失败也不影响其它功能）
        RegionNative.SetWindowDisplayAffinity(
            handle,
            s.ExcludeFromCapture ? RegionNative.WdaExcludeFromCapture : RegionNative.WdaNone);

        RegionNative.RaiseTopmost(handle);
    }

    private void StartTimers()
    {
        var fps = Math.Clamp(_settings.Fps, 5, 60);

        _frameTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _frameTimer.Stop();
        _frameTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        _frameTimer.Tick -= OnFrameTick;
        _frameTimer.Tick += OnFrameTick;
        _frameTimer.Start();

        _keepAliveTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _keepAliveTimer.Tick -= OnKeepAliveTick;
        _keepAliveTimer.Tick += OnKeepAliveTick;
        _keepAliveTimer.Start();
    }

    private void OnKeepAliveTick(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        RegionNative.RaiseTopmost(handle);
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        var s = _settings;
        var width = Math.Clamp(s.RegionWidth, 1, 4096);
        var height = Math.Clamp(s.RegionHeight, 1, 4096);

        // 解析目标窗口（每秒最多试一次，免得每帧都枚举窗口）
        if (_targetHandle == IntPtr.Zero || _resolveCooldown <= 0)
        {
            _resolveCooldown = 20;
            var handle = RegionWindowFinder.ResolveTarget(s.TargetProcessName, s.TargetTitleHint, out _);
            if (handle != IntPtr.Zero || _targetHandle == IntPtr.Zero)
            {
                _targetHandle = handle;
            }
        }
        else
        {
            _resolveCooldown--;
        }

        if (!ResolveSourceRect(s, width, height, out var sourceX, out var sourceY, out var message))
        {
            ShowFailure(message);
            return;
        }

        if (!_grabber.Capture(sourceX, sourceY, width, height))
        {
            ShowFailure("抓屏失败（BitBlt 返回失败）");
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            Preview.Source = _bitmap;
        }

        _grabber.CopyTo(_bitmap);

        if (_grabber.HasContent)
        {
            Failure.Visibility = Visibility.Collapsed;
            StatusText = $"{width}×{height} @ ({sourceX},{sourceY})  放大 {Math.Clamp(s.Zoom, 1, 8):0.#}×   {Math.Clamp(s.Fps, 5, 60)} FPS";
        }
        else
        {
            // 抓到一片黑：最常见的原因是目标窗口没在前台 / 被别的东西盖住 / 游戏用了独占全屏
            ShowFailure("抓到的是黑屏\n· 让游戏窗口在前台（或用无边框全屏）\n· 独占全屏（F11 那种）抓不到");
            StatusText = "抓到黑屏（让游戏在窗口化/无边框全屏下再试）";
        }
    }

    /// <summary>算出这一帧要从屏幕哪里抓。返回 false 时 message 里是原因。</summary>
    private bool ResolveSourceRect(RegionMagnifierSettings s, int width, int height, out int x, out int y, out string message)
    {
        x = 0;
        y = 0;
        message = "";

        if (!s.RelativeToWindow)
        {
            x = s.RegionX;
            y = s.RegionY;
            return true;
        }

        if (_targetHandle == IntPtr.Zero || !RegionWindowFinder.TryGetClientRectOnScreen(_targetHandle, out var clientX, out var clientY, out var clientWidth, out var clientHeight))
        {
            message = "没找到游戏窗口\n· 在页面里点「刷新列表」重选目标\n· 或把游戏开起来";
            return false;
        }

        // 选区可能超出客户区（改过分辨率/窗口大小），夹一下，保证抓的还是这块地方
        x = clientX + Math.Clamp(s.RegionX, 0, Math.Max(0, clientWidth - 1));
        y = clientY + Math.Clamp(s.RegionY, 0, Math.Max(0, clientHeight - 1));
        return true;
    }

    private void ShowFailure(string message)
    {
        if (FailureText.Text != message)
        {
            FailureText.Text = message;
        }

        Failure.Visibility = Visibility.Visible;
    }

    /// <summary>只停抓取，不动缓冲区（隐藏时用；下次显示直接 StartTimers 恢复）。</summary>
    private void PauseTimers()
    {
        _frameTimer?.Stop();
        _keepAliveTimer?.Stop();
    }
    private void StopEverything()
    {
        _frameTimer?.Stop();
        _keepAliveTimer?.Stop();
        _frameTimer = null;
        _keepAliveTimer = null;
        _grabber.Dispose();
    }
}
