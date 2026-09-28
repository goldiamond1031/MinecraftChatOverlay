using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MinecraftChatOverlay;

/// <summary>
/// 击杀确认图标窗口 —— CS / 瓦 那种「屏幕上弹一个图标」的效果。
///
/// 设计要点：
///
/// <b>1. 窗口铺满全屏，图标在画布里按百分比摆。</b>
/// 位置不靠移动窗口决定，而是窗口固定铺满主屏、图标在画布里按百分比坐标摆。
///   * 多显示器 / 不同分辨率下，百分比坐标天然跟着走，不用每次重算；
///   * 窗口不移动 = 不会触发窗口动画，图标出现的位置更"稳"。
///
/// <b>2. 只有用户导入的图片，没有内置图标。</b>
/// v8 起软件自带的矢量图标全删了 —— 用户想要什么图标自己导入。
///
/// <b>3. 显示时长拆成三段：淡入 + 停留 + 淡出。</b>
/// 用 Storyboard 做不透明度动画，而不是定时器里手算 ——
/// 后者的帧率不稳，淡出在低帧时会显得一跳一跳。
///
/// <b>4. 支持"真拖动"。</b>
/// 平时 <c>IsHitTestVisible=False</c>（鼠标穿透给游戏），
/// 只有用户点了设置页的【拖动定位】按钮时，才由
/// <see cref="BeginDragMode"/> 打开命中测试并接管鼠标。见那边的注释。
///
/// <b>5. 绝对不抢前台（<c>WS_EX_NOACTIVATE</c>）。</b>
/// 这是修一个真 bug 加的：原先只靠 XAML 上的 <c>ShowActivated="False"</c>，
/// 那东西**只在窗口第一次显示时可靠**；本窗口是"Hide 了下次击杀再 Show"的复用窗口，
/// 重新 Show 时不保证不激活，结果图标一弹就把游戏顶到后台，
/// Minecraft 检测到失焦就自己弹出 ESC 菜单（看起来像"被切了窗口"）。
/// 现在在创建时打上 <c>WS_EX_NOACTIVATE</c>，Win32 层保证它永远不会成为前台窗口。
/// <b>只有拖动模式那会儿要临时摘掉</b>，见 <see cref="SetNoActivate"/>。
///
/// <b>6. 常驻窗口，不 Hide/Show；鼠标真穿透（<c>WS_EX_TRANSPARENT</c>）。</b>
/// 见 <see cref="EnsureShown"/> 与 <see cref="SetClickThrough"/> 的注释。
/// 一句话：窗口从头到尾就那一个，击杀只是"让图标亮一下"。
/// </summary>
public partial class KillBannerWindow : Window
{
    // ---- Win32：窗口扩展样式 ----
    private const int GwlExStyle = -20;
    private const uint WsExNoActivate = 0x08000000;

    /// <summary>
    /// <c>WS_EX_TRANSPARENT</c>：鼠标**真穿透**。
    ///
    /// 这是"鼠标光标跑出来"那个 bug 的解 ——
    /// WPF 的 <c>IsHitTestVisible="False"</c> 只管 WPF 内部元素不响应鼠标，
    /// 但窗口在系统眼里仍然是"可点击的"，鼠标移上去光标就会现形。
    /// 打上这个位，系统直接把鼠标消息透传给下层的游戏窗口。
    /// </summary>
    private const uint WsExTransparent = 0x00000020;

    /// <summary>图标中心相对画布的百分比位置（0~1）。拖完之后写回配置用这个值。</summary>
    public double LastPosX { get; private set; } = 0.5;

    /// <inheritdoc cref="LastPosX"/>
    public double LastPosY { get; private set; } = 0.58;

    /// <summary>拖动模式是不是开着。</summary>
    public bool IsDragMode { get; private set; }

    /// <summary>拖动结束时把新坐标报出去（百分比）。</summary>
    public event Action<double, double>? PositionChanged;

    private string _currentFile = "";
    private BitmapImage? _loadedImage;
    private double _iconSize = 96;
    private Color _haloColor = Colors.White;

    private DispatcherTimer? _hideTimer;

    /// <summary>正在播的淡入淡出动画。连杀换图标时要把上一轮的动画掐掉再起新的。</summary>
    private Storyboard? _fadeStoryboard;

    public KillBannerWindow()
    {
        InitializeComponent();
        ApplyScreenBounds();
        Loaded += (_, _) => ApplyScreenBounds();

        // HWND 一创建就把两个位一次性打上 —— 越早越好，晚了会先被抢一次焦点 / 露一次鼠标。
        //   WS_EX_NOACTIVATE  → 永不成为前台窗口
        //   WS_EX_TRANSPARENT → 鼠标真穿透
        SourceInitialized += (_, _) =>
        {
            SetNoActivate(true);
            SetClickThrough(true);
        };
    }

    /// <summary>
    /// 保证窗口已经"在场"（常驻显示）。公开给调用方在启动时调一次。
    ///
    /// **只做一次**：第一次调用时 `Show()`，之后每次进来都是空操作。
    /// 换掉了以前那种"Hide 了再 Show"的反复折腾 ——
    /// 那才是扰动游戏的根源（每次显示都要重排 z-order、重建 layered surface）。
    ///
    /// 现在"图标不显示"靠的是图标自身 `Collapsed` / `Opacity=0`，
    /// 不是把整个窗口藏起来。窗口的 Win32 状态从头到尾不变。
    /// </summary>
    public void EnsureVisible()
    {
        EnsureShown();
    }

    /// <inheritdoc cref="EnsureVisible"/>
    private void EnsureShown()
    {
        ApplyScreenBounds();

        if (!IsVisible)
        {
            Show();
        }

        Topmost = true;

        // 保险：Show() / 改 Topmost 都会去动窗口样式，确认两个位都还在。
        // 成本几乎为零，换来"绝不抢焦点 + 鼠标绝不现形"这两条硬保证。
        if (!IsDragMode)
        {
            SetNoActivate(true);
            SetClickThrough(true);
        }
    }

    /// <summary>
    /// 开/关 <c>WS_EX_NOACTIVATE</c>。
    ///
    /// true（平时）= 窗口永远不会成为前台窗口，弹图标不会把游戏顶到后台。
    /// false（拖动模式）= 让它能正常接收鼠标交互。
    ///
    /// 注意这个位是 Win32 层的，和 WPF 的 <c>ShowActivated</c> 无关 ——
    /// 后者只在第一次显示时可靠，对本窗口这种复用场景不够。
    /// </summary>
    /// <returns>成功改到返回 true；拿不到 HWND 返回 false。</returns>
    private bool SetNoActivate(bool enabled)
    {
        return SetExStyleBit(WsExNoActivate, enabled);
    }

    /// <summary>
    /// 开/关 <c>WS_EX_TRANSPARENT</c>（鼠标真穿透）。
    ///
    /// true（平时）= 鼠标消息直接透传给下层窗口，游戏里光标不会因为这块透明窗口而现形。
    /// false（拖动模式）= 窗口自己收鼠标，用户才能拖图标。
    /// </summary>
    private bool SetClickThrough(bool enabled)
    {
        return SetExStyleBit(WsExTransparent, enabled);
    }

    /// <summary>改 <c>GWL_EXSTYLE</c> 里的某一位；已是目标状态就不重复调。</summary>
    private bool SetExStyleBit(uint bit, bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var style = (uint)GetWindowLong(handle, GwlExStyle);
        var updated = enabled ? (style | bit) : (style & ~bit);

        if (updated != style)
        {
            SetWindowLong(handle, GwlExStyle, (int)updated);
        }

        return true;
    }

    /// <summary>把窗口挪成全屏大小。Show() 前后各调一次，保证布局怎么变都贴在屏幕上。</summary>
    private void ApplyScreenBounds()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;
    }

    // ===================== 显示 =====================

    /// <summary>
    /// 显示击杀图标。
    ///
    /// 这个入口会被"真的击杀"和"设置页试一下 / 拖动定位"三种场景调用，
    /// 所以参数全由调用方给，自己没有默认行为，保证三条路径看到的完全一致。
    /// </summary>
    /// <param name="imagePath">用户导入的图片路径。</param>
    /// <param name="posX">图标中心横向百分比 0~1。</param>
    /// <param name="posY">图标中心纵向百分比 0~1。</param>
    /// <param name="iconSize">图标边长（像素）。</param>
    /// <param name="haloColor">外发光颜色。</param>
    /// <param name="fadeInMs">淡入时长。</param>
    /// <param name="holdMs">停留时长。</param>
    /// <param name="fadeOutMs">淡出时长。</param>
    /// <param name="persist">
    /// true = 一直亮着不淡出、也不自动收（拖动定位用）；
    /// false = 走完整的淡入→停留→淡出→收起。
    /// </param>
    /// <returns>图片读不出来时返回 false，调用方好据此给用户提示。</returns>
    public bool ShowIcon(
        string imagePath,
        double posX,
        double posY,
        double iconSize,
        Color haloColor,
        double fadeInMs,
        double holdMs,
        double fadeOutMs,
        bool persist = false)
    {
        var area = SystemParameters.WorkArea;

        // 窗口常驻，不 Hide/Show（见 EnsureShown 的注释）。
        EnsureShown();

        var loaded = ApplyIcon(imagePath, iconSize, haloColor);
        PlaceIcon(posX, posY, iconSize, area);

        LastPosX = Math.Clamp(posX, 0.0, 1.0);
        LastPosY = Math.Clamp(posY, 0.0, 1.0);

        // 时间三段都在这里收口，所以先停掉上一轮的定时器与动画。
        // 这就是"没放完又来一次击杀就直接换图标刷新时长"的落点。
        _hideTimer?.Stop();
        _fadeStoryboard?.Stop(this);
        IconHost.Opacity = 1;

        if (persist)
        {
            // 拖动模式：保持全亮，不安排淡出。收起交给 EndDragMode。
            return loaded;
        }

        StartFadeSequence(fadeInMs, holdMs, fadeOutMs);
        return loaded;
    }

    /// <summary>三段不透明度动画：淡入 → 停留 → 淡出，播完把图标收起来。</summary>
    private void StartFadeSequence(double fadeInMs, double holdMs, double fadeOutMs)
    {
        IconHost.Visibility = Visibility.Visible;
        var fadeIn = Math.Max(0, fadeInMs);
        var hold = Math.Max(0, holdMs);
        var fadeOut = Math.Max(0, fadeOutMs);

        var board = new Storyboard();
        var opacity = new DoubleAnimationUsingKeyFrames
        {
            // 不冻结：要让它能被重新起播（连杀时会 Stop 再重来）
            Duration = TimeSpan.FromMilliseconds(fadeIn + hold + fadeOut)
        };

        // 起始是 0（完全透明），这样淡入才有意义
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));

        var fadeInEnd = TimeSpan.FromMilliseconds(fadeIn);
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(fadeInEnd)));

        // 停留段：保持 1 不变，只是把时间轴往后推
        var holdEnd = TimeSpan.FromMilliseconds(fadeIn + hold);
        if (hold > 0)
        {
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(holdEnd)));
        }

        // 淡出段
        var total = TimeSpan.FromMilliseconds(fadeIn + hold + fadeOut);
        if (fadeOut > 0)
        {
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(total)));
        }
        else
        {
            // 淡出设为 0：就是"停留一结束立刻消失"，不要补一个 0 的关键帧，
            // 否则会变成瞬时从 1 跳到 0，看起来像闪一下。
            opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(total)));
        }

        Storyboard.SetTarget(opacity, IconHost);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
        board.Children.Add(opacity);

        _fadeStoryboard = board;

        // 收起用定时器而不是 Completed 事件：
        // Completed 在动画被 Stop 掉时也会触发（WPF 的行为），
        // 连杀时会把刚换上的新图标一起收掉。定时器的时间我们自己控，行为可预期。
        //
        // 注意这里**只把图标 Collapsed，不动窗口** ——
        // 窗口是常驻的（见 EnsureShown），藏窗口才是扰动游戏的元凶。
        _hideTimer = new DispatcherTimer { Interval = total };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer!.Stop();
            IconHost.Opacity = 1;
            IconHost.Visibility = Visibility.Collapsed;
        };
        _hideTimer.Start();

        board.Begin(this, true);
    }

    // ===================== 拖动定位 =====================

    /// <summary>
    /// 进入拖动模式：打开鼠标命中测试，让用户能把图标拖到想要的位置。
    ///
    /// 平时这个窗口同时带 <c>WS_EX_TRANSPARENT</c>（鼠标真穿透），
    /// 拖动时反过来摘掉，让窗口自己收鼠标，代价是这一小段时间里鼠标点不到游戏 ——
    /// 所以只由设置页的按钮显式开启，并且一松手就立刻关掉（见 <see cref="EndDragMode"/>）。
    /// </summary>
    /// <param name="imagePath">要拖的图标。</param>
    /// <param name="posX">当前横向位置（作为拖动起点）。</param>
    /// <param name="posY">当前纵向位置（作为拖动起点）。</param>
    /// <param name="iconSize">图标边长，拖动时按这个尺寸看落点。</param>
    /// <param name="haloColor">外发光颜色。</param>
    public bool BeginDragMode(string imagePath, double posX, double posY, double iconSize, Color haloColor)
    {
        IsDragMode = true;

        // 拖动要正常收鼠标，两个位都先摘掉（见 SetNoActivate / SetClickThrough 的注释）。
        // 拖完在 EndDragMode 里打回去。
        SetNoActivate(false);
        SetClickThrough(false);

        // 常亮、不淡出 —— 用户要盯着它拖，中途淡出会很莫名
        var loaded = ShowIcon(imagePath, posX, posY, iconSize, haloColor, 0, 0, 0, persist: true);

        IsHitTestVisible = true;
        RootCanvas.Cursor = Cursors.SizeAll;

        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            // 按钮是按下的状态里进过来（点按钮时鼠标还按着），
            // 这里先不抓；等用户松开再按下去才开始拖，否则第一下会"跳"到鼠标位置。
            _awaitRelease = true;
        }
        else
        {
            CaptureMouse();
        }

        return loaded;
    }

    private bool _awaitRelease;

    /// <summary>退出拖动模式：把鼠标让回游戏，并把图标收起来（窗口本身继续常驻）。</summary>
    public void EndDragMode()
    {
        if (!IsDragMode)
        {
            return;
        }

        IsDragMode = false;
        _awaitRelease = false;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        IsHitTestVisible = false;
        RootCanvas.Cursor = Cursors.Arrow;

        _hideTimer?.Stop();
        _fadeStoryboard?.Stop(this);
        IconHost.Opacity = 1;

        // 只收图标，不藏窗口 —— 窗口常驻（见 EnsureShown 的注释）。
        IconHost.Visibility = Visibility.Collapsed;

        // 拖完把两个位都打回去，否则以后击杀弹图标会重新抢焦点 / 露鼠标
        SetNoActivate(true);
        SetClickThrough(true);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (!IsDragMode)
        {
            return;
        }

        _awaitRelease = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!IsDragMode)
        {
            return;
        }

        // 从"按钮还按着"的状态进来时，等它松一次，免得图标一上来就跳到鼠标底下
        if (_awaitRelease)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _awaitRelease = false;
                e.Handled = true;
            }

            return;
        }

        // 只有按住左键才动它
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        // 鼠标在屏幕坐标系里的位置 → 转成窗口内的坐标 → 再转百分比
        var point = e.GetPosition(this);
        var posX = Math.Clamp(point.X / area.Width, 0.0, 1.0);
        var posY = Math.Clamp(point.Y / area.Height, 0.0, 1.0);

        PlaceIcon(posX, posY, _iconSize, area);
        LastPosX = posX;
        LastPosY = posY;

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!IsDragMode)
        {
            return;
        }

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        // 松手就上报，让设置页把百分比写进配置
        PositionChanged?.Invoke(LastPosX, LastPosY);
        e.Handled = true;
    }

    // ===================== 摆位 / 加载 =====================

    /// <summary>按百分比把图标摆到画布上的指定位置（图标中心对准那个点）。</summary>
    private void PlaceIcon(double posX, double posY, double iconSize, Rect canvas)
    {
        var canvasW = canvas.Width;
        var canvasH = canvas.Height;

        var left = Math.Clamp(posX, 0.0, 1.0) * canvasW - iconSize / 2.0;
        var top = Math.Clamp(posY, 0.0, 1.0) * canvasH - iconSize / 2.0;

        // 别让图标整个跑出屏幕：贴边时至少留一半在里面
        left = Math.Clamp(left, -iconSize / 2.0, Math.Max(-iconSize / 2.0, canvasW - iconSize / 2.0));
        top = Math.Clamp(top, -iconSize / 2.0, Math.Max(-iconSize / 2.0, canvasH - iconSize / 2.0));

        IconHost.Margin = new Thickness(left, top, 0, 0);
    }

    /// <summary>加载并应用用户图片。读不出来返回 false（界面上有提示，不静默）。</summary>
    private bool ApplyIcon(string imagePath, double iconSize, Color haloColor)
    {
        _iconSize = iconSize;
        _haloColor = haloColor;

        var bmp = TryLoadImage(imagePath);
        if (bmp == null)
        {
            IconHost.Visibility = Visibility.Collapsed;
            return false;
        }

        IconImage.Source = bmp;

        // 外发光用用户选的强调色 + 一圈黑底影。
        // 单用强调色在亮背景上会糊掉，所以还是保留黑色描边，只是把光环染成强调色。
        IconHalo.Color = haloColor;
        IconHalo.Opacity = 0.9;

        IconHost.Width = iconSize;
        IconHost.Height = iconSize;
        IconHost.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>读图片；同一个路径只读一次（缓存住 BitmapImage）。</summary>
    private BitmapImage? TryLoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (_loadedImage != null && string.Equals(_currentFile, path, StringComparison.OrdinalIgnoreCase))
        {
            return _loadedImage;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            // OnLoad：立刻把文件读完、不占着文件句柄，用户改图后能直接换
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            _loadedImage = bmp;
            _currentFile = path;
            return bmp;
        }
        catch
        {
            // 格式不支持 / 文件损坏 / 被占用，都走这里
            return null;
        }
    }

    /// <summary>
    /// 图片能不能读出来。设置页在导入时要当场验一次，
    /// 免得用户选了张坏图、直到真的击杀才发现是空白。
    /// </summary>
    public static bool CanLoadImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            return bmp.PixelWidth > 0 && bmp.PixelHeight > 0;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _hideTimer?.Stop();
        _fadeStoryboard?.Stop(this);
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
