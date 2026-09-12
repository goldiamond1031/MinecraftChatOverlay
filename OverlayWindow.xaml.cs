using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Services;
using MinecraftChatOverlay.ViewModels;

namespace MinecraftChatOverlay;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;

    private const int MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly AppSettings _settings;
    private readonly ObservableCollection<ChatMessageViewModel> _messages = new();
    private bool _isMouseOverOverlay;

    /// <summary>
    /// 悬浮窗的“停靠底边”（DIP）。悬浮窗高度变化时以它为锚点保持底边不动：
    /// 新消息让窗口向上长，清空消息让窗口向下收。
    /// 只有用户拖动窗口、或程序主动把它拉回屏幕内时才会重新记录。
    /// </summary>
    private double _anchorBottom = double.NaN;

    /// <summary>程序正在自行调整位置（吸底 / 拉回屏幕）时为 true，此时不重算停靠点。</summary>
    private bool _suppressAnchor;

    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ChatList.ItemsSource = _messages;
        ApplySettings();
        if (_settings.OverlayLeft.HasValue)
        {
            Left = _settings.OverlayLeft.Value;
        }

        if (_settings.OverlayTop.HasValue)
        {
            Top = _settings.OverlayTop.Value;
        }

        if (_settings.OverlayAnchorBottom.HasValue)
        {
            _anchorBottom = _settings.OverlayAnchorBottom.Value;
        }

        LocationChanged += OverlayWindow_LocationChanged;
        SizeChanged += OverlayWindow_SizeChanged;
    }

    private void OverlayWindow_LocationChanged(object? sender, EventArgs e)
    {
        if (_suppressAnchor)
        {
            // 程序自己微调位置（吸底 / 拉回屏幕）不落盘，避免每条消息都写一次配置。
            return;
        }

        // 用户拖动（或系统移动）后，把新的底边记成停靠点。
        if (ActualHeight > 0 && !double.IsNaN(Top))
        {
            _anchorBottom = Top + ActualHeight;
        }
    }

    private void OverlayWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_suppressAnchor)
        {
            return;
        }

        if (e.HeightChanged)
        {
            // 高度变化时保持底边不动，避免窗口向顶边塌陷（甚至塌到屏幕外）。
            ApplyAnchor();
        }
        else if (e.WidthChanged)
        {
            KeepOnScreen();
        }
    }

    /// <summary>把窗口底边放回停靠点，并保证结果仍在屏幕内。</summary>
    private void ApplyAnchor()
    {
        if (ActualHeight <= 0 || double.IsNaN(Top))
        {
            return;
        }

        if (double.IsNaN(_anchorBottom))
        {
            // 第一次布局：把当前底边记为停靠点。
            _anchorBottom = Top + ActualHeight;
            KeepOnScreen();
            return;
        }

        var previous = _suppressAnchor;
        _suppressAnchor = true;
        try
        {
            var target = _anchorBottom - ActualHeight;
            if (Math.Abs(Top - target) > 0.5)
            {
                Top = target;
            }

            KeepOnScreen();
        }
        finally
        {
            _suppressAnchor = previous;
        }
    }

    /// <summary>把悬浮窗拉回当前显示器的工作区，保证任何时候都完整可见。</summary>
    private void KeepOnScreen()
    {
        if (!IsInitialized)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (!GetWindowRect(handle, out var rect))
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo32 { cbSize = Marshal.SizeOf<MonitorInfo32>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var work = info.rcWork;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // 比工作区还宽/还高时（理论上升级前的老配置可能触发）优先贴左上角，至少保证可见。
        var x = width <= work.Right - work.Left
            ? Math.Clamp(rect.Left, work.Left, work.Right - width)
            : work.Left;
        var y = height <= work.Bottom - work.Top
            ? Math.Clamp(rect.Top, work.Top, work.Bottom - height)
            : work.Top;

        if (x == rect.Left && y == rect.Top)
        {
            return;
        }

        var previous = _suppressAnchor;
        _suppressAnchor = true;
        try
        {
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        finally
        {
            _suppressAnchor = previous;
        }
    }

    /// <summary>把悬浮窗放到主显示器右下角的默认位置。</summary>
    public void ResetPosition()
    {
        // 先清掉停靠点，等布局完成后按新位置重新记录。
        _anchorBottom = double.NaN;
        _settings.OverlayAnchorBottom = null;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            // 还没显示过：直接给一个保守的默认坐标，显示后再由 KeepOnScreen 修正。
            Left = 40;
            Top = 40;
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo32 { cbSize = Marshal.SizeOf<MonitorInfo32>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var work = info.rcWork;
        var width = ActualWidth > 0 ? (int)Math.Ceiling(ActualWidth * DpiScaleX) : 420;
        var height = ActualHeight > 0 ? (int)Math.Ceiling(ActualHeight * DpiScaleY) : 120;
        var margin = 24;

        var x = Math.Max(work.Left, work.Right - width - margin);
        var y = Math.Max(work.Top, work.Bottom - height - margin);

        var previous = _suppressAnchor;
        _suppressAnchor = true;
        try
        {
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        finally
        {
            _suppressAnchor = previous;
        }

        if (!double.IsNaN(Top) && ActualHeight > 0)
        {
            _anchorBottom = Top + ActualHeight;
        }

        SavePosition();
    }

    private double DpiScaleX => GetDpiScale().DpiScaleX;

    private double DpiScaleY => GetDpiScale().DpiScaleY;

    private DpiScale GetDpiScale()
    {
        try
        {
            return VisualTreeHelper.GetDpi(this);
        }
        catch
        {
            return new DpiScale(1, 1);
        }
    }

    private void SavePosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        _settings.OverlayLeft = Left;
        _settings.OverlayTop = Top;
        _settings.OverlayAnchorBottom = double.IsNaN(_anchorBottom) ? null : _anchorBottom;
        try
        {
            SettingsService.Save(_settings);
        }
        catch
        {
            // 记忆位置失败不应影响悬浮窗使用。
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateClickThrough();
        RefreshContentLimit();
        KeepOnScreen();
    }

    public void ApplySettings()
    {
        Width = Math.Clamp(_settings.OverlayWidth, 120, 2000);
        Opacity = Math.Clamp(_settings.OverlayOpacity, 0.1, 1.0);
        RootBorder.Background = BuildBackgroundBrush();
        ChatScroll.MaxHeight = ResolveContentLimit();
        UpdateClickThrough();

        var fontFamily = ParseFontFamily(_settings.FontFamily);
        var fontSize = Math.Clamp(_settings.FontSize, 8, 96);
        var fontWeight = ParseFontWeight(_settings.FontWeight);
        var foreground = ParseBrush(_settings.TextColor, Brushes.White);
        var shadow = CreateShadow(_settings.TextShadow);
        var maxTextWidth = Math.Max(80, Width - 40);

        foreach (var message in _messages)
        {
            message.Segments = BuildSegments(message.RawText, message.Timestamp);
            message.FontFamily = fontFamily;
            message.FontSize = fontSize;
            message.FontWeight = fontWeight;
            message.Foreground = foreground;
            message.Shadow = shadow;
            message.MaxTextWidth = maxTextWidth;
            message.TextSelectionEnabled = _settings.EnableTextSelection;
        }

        while (_messages.Count > Math.Max(1, _settings.MaxMessages))
        {
            _messages.RemoveAt(0);
        }
    }

    public void AddMessage(string chatMessage)
    {
        // 合并连续重复消息：只有紧挨着的同一条消息会被忽略。
        if (_settings.MergeDuplicateMessages &&
            _messages.Count > 0 &&
            string.Equals(_messages[^1].RawText, chatMessage, StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTime.Now;
        // 鼠标不在悬浮窗上时始终自动跟随最新消息；
        // 鼠标在悬浮窗上时，只有原本在底部附近才自动跟随，方便查看历史。
        var autoScroll = !_isMouseOverOverlay || IsNearBottom();
        var vm = new ChatMessageViewModel
        {
            Timestamp = now,
            RawText = chatMessage,
            Segments = BuildSegments(chatMessage, now),
            FontFamily = ParseFontFamily(_settings.FontFamily),
            FontSize = Math.Clamp(_settings.FontSize, 8, 96),
            FontWeight = ParseFontWeight(_settings.FontWeight),
            Foreground = ParseBrush(_settings.TextColor, Brushes.White),
            Shadow = CreateShadow(_settings.TextShadow),
            MaxTextWidth = Math.Max(80, Width - 40),
            TextSelectionEnabled = _settings.EnableTextSelection
        };

        _messages.Add(vm);
        while (_messages.Count > Math.Max(1, _settings.MaxMessages))
        {
            _messages.RemoveAt(0);
        }

        // 如果用户正在查看历史消息，不强制跳到底部；只有原本就在底部附近时才自动跟随。
        if (autoScroll)
        {
            ChatScroll.ScrollToEnd();
        }

        // 让悬浮窗保持底边位置不变，新消息加入后整体向上扩展。
        // 具体的位置补偿放在 SizeChanged 里统一处理，这里只需触发一次布局。
        UpdateLayout();
        ApplyAnchor();

        AnimateNewMessage(vm);
    }

    /// <summary>
    /// 把"窗口最大高度"限制在当前显示器工作区内，避免悬浮窗比屏幕还高、必然露到屏幕外。
    /// </summary>
    private void RefreshContentLimit()
    {
        ChatScroll.MaxHeight = ResolveContentLimit();
    }

    private double ResolveContentLimit()
    {
        var desired = Math.Clamp(_settings.OverlayMaxHeight, 100, 2000);

        var available = AvailableContentHeight();
        if (!double.IsNaN(available) && available > 0)
        {
            desired = Math.Min(desired, available);
        }

        return Math.Max(100, desired);
    }

    private double AvailableContentHeight()
    {
        if (!IsInitialized)
        {
            return double.NaN;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return double.NaN;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo32 { cbSize = Marshal.SizeOf<MonitorInfo32>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return double.NaN;
        }

        var scale = DpiScaleY;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var workHeightDip = (info.rcWork.Bottom - info.rcWork.Top) / scale;

        // 除聊天区以外的固定高度：边框内边距 + 边框 + 滚动区外边距，再留一点余量。
        var chrome = RootBorder.Padding.Top + RootBorder.Padding.Bottom
                     + RootBorder.BorderThickness.Top + RootBorder.BorderThickness.Bottom
                     + ChatScroll.Margin.Top + ChatScroll.Margin.Bottom + 8;

        return workHeightDip - chrome;
    }

    private void AnimateNewMessage(ChatMessageViewModel message)
    {
        if (!_settings.EnableMessageAnimation)
        {
            return;
        }

        try
        {
            ChatList.UpdateLayout();
            if (ChatList.ItemContainerGenerator.ContainerFromItem(message) is not FrameworkElement container)
            {
                return;
            }

            var translate = new TranslateTransform(0, 24);
            container.RenderTransform = translate;
            container.Opacity = 0;

            // 从下往上滑入，使用曲线缓动。
            var slide = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            translate.BeginAnimation(TranslateTransform.YProperty, slide);

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            container.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        catch
        {
            // 动画失败不应影响消息显示。
        }
    }

    private bool IsNearBottom()
    {
        return ChatScroll.ScrollableHeight <= 0 || ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 40;
    }

    public void ClearMessages()
    {
        if (_messages.Count == 0)
        {
            return;
        }

        _messages.Clear();

        // 清空后同样按"底边不动"收拢：窗口高度变小，位置补偿放在 ApplyAnchor 里，
        // 这样悬浮窗会缩回用户放置的位置，而不是向顶边塌陷到屏幕外。
        UpdateLayout();
        ApplyAnchor();
    }

    private List<ChatSegmentViewModel> BuildSegments(string rawText, DateTime timestamp)
    {
        // 这里保留原始文本并实时应用替换规则，修改规则后已显示消息也会重新渲染。
        var displayText = ChatTextProcessor.ApplyReplacements(rawText, _settings.ReplaceRules);

        var foreground = ParseBrush(_settings.TextColor, Brushes.White);
        var defaultWeight = ParseFontWeight(_settings.FontWeight);

        var segments = new List<ChatSegmentViewModel>();
        if (_settings.ShowTimestamp)
        {
            segments.Add(new ChatSegmentViewModel(
                $"[{timestamp:HH:mm:ss}] ",
                foreground,
                defaultWeight));
        }

        // 先按用户颜色规则生成分段，再叠加“玩家发言颜色/发言玩家 ID 颜色”。
        var colored = ChatTextProcessor.BuildColoredSegments(displayText, _settings.ColorRules, foreground, defaultWeight);
        var contentBrush = string.IsNullOrWhiteSpace(_settings.PlayerContentColor)
            ? null
            : ParseBrush(_settings.PlayerContentColor, foreground);
        var nameBrush = string.IsNullOrWhiteSpace(_settings.PlayerNameColor)
            ? null
            : ParseBrush(_settings.PlayerNameColor, foreground);
        colored = ChatTextProcessor.ApplyPlayerColorOverrides(colored, displayText, foreground, contentBrush, nameBrush);

        foreach (var segment in colored)
        {
            segment.Text = WrapText(segment.Text, _settings.WrapLength);
            segments.Add(segment);
        }

        return segments;
    }

    private static string WrapText(string text, int wrapLength)
    {
        if (wrapLength <= 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 16);
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

    private static FontFamily ParseFontFamily(string? value)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value) ? new FontFamily("Microsoft YaHei UI") : new FontFamily(value);
        }
        catch
        {
            return new FontFamily("Microsoft YaHei UI");
        }
    }

    private static FontWeight ParseFontWeight(string? value)
    {
        return value?.Trim() switch
        {
            "Bold" => FontWeights.Bold,
            "SemiBold" => FontWeights.SemiBold,
            "Light" => FontWeights.Light,
            "Thin" => FontWeights.Thin,
            _ => FontWeights.Normal
        };
    }

    private static DropShadowEffect? CreateShadow(bool enabled)
    {
        if (!enabled)
        {
            return null;
        }

        return new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 2.5,
            ShadowDepth = 1,
            Direction = 270,
            Opacity = 0.85
        };
    }

    private static Brush ParseBrush(string? value, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            var brush = new BrushConverter().ConvertFromString(value) as Brush;
            return brush ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private Brush BuildBackgroundBrush()
    {
        var fallback = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        var brush = ParseBrush(_settings.BackgroundColor, fallback);
        if (brush is SolidColorBrush solid)
        {
            var alpha = (byte)Math.Round(solid.Color.A * Math.Clamp(_settings.BackgroundOpacity, 0.0, 1.0));
            return new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
        }

        return brush;
    }

    private void RootBorder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isMouseOverOverlay = true;
    }

    private void RootBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isMouseOverOverlay = false;
        // 鼠标一旦离开悬浮窗，自动回到最新消息。
        ChatScroll.ScrollToEnd();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !_settings.ClickThrough)
        {
            DragMove();

            // 拖动结束后把新位置记为停靠点，并确保没有滑出屏幕。
            KeepOnScreen();
            if (!double.IsNaN(Top) && ActualHeight > 0)
            {
                _anchorBottom = Top + ActualHeight;
            }

            SavePosition();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePosition();
        base.OnClosed(e);
    }

    private void UpdateClickThrough()
    {
        if (!IsInitialized)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        if (_settings.ClickThrough)
        {
            SetWindowLong(handle, GwlExStyle, style | WsExTransparent);
        }
        else
        {
            SetWindowLong(handle, GwlExStyle, style & ~WsExTransparent);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo32 lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo32
    {
        public int cbSize;
        public Win32Rect rcMonitor;
        public Win32Rect rcWork;
        public uint dwFlags;
    }
}
