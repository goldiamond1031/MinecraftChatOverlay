using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace MinecraftChatOverlay.Plugins.ProcessFps;

/// <summary>
/// 帧率悬浮窗：无边框、透明、置顶、不占任务栏，里面只有一个 TextBlock。
///
/// 窗口的生命周期跟着「启用」开关走，**不跟着数字走** —— 帧率每秒变好几次，
/// 但窗口一次都不重新显示；更新的只是 <see cref="SetText"/> 里的文字。
/// 反复 Show/Hide 一个分层窗口本身就是游戏扰动源（DEV-NOTES 坑 42）。
/// </summary>
public partial class FpsOverlayWindow : Window
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

    public FpsOverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>鼠标穿透。开着时点不到这个窗口（适合盖在游戏上）。</summary>
    public bool ClickThrough { get; set; }

    /// <summary>按设置刷新外观。改任何一项外观都走这里，改完立刻生效。</summary>
    public void ApplySettings(ProcessFpsSettings settings)
    {
        var family = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily)
            ? "Microsoft YaHei UI"
            : settings.FontFamily);

        FpsText.FontFamily = family;
        FpsText.FontSize = Math.Clamp(settings.FontSize, 8, 200);
        FpsText.FontWeight = settings.Bold ? FontWeights.Bold : FontWeights.Normal;
        FpsText.Foreground = new SolidColorBrush(ParseColor(settings.TextColor, Colors.White));

        // 阴影：关掉时把 Effect 整个摘掉（留着会白算一遍模糊）
        if (settings.ShadowEnabled)
        {
            TextShadow.Color = ParseColor(settings.ShadowColor, Colors.Black);
            TextShadow.BlurRadius = Math.Max(0, settings.ShadowBlur);
            TextShadow.Opacity = Math.Clamp(settings.ShadowOpacity, 0, 1);
            TextShadow.ShadowDepth = Math.Max(0, settings.ShadowOffset);
            TextShadow.Direction = ((settings.ShadowDirection % 360) + 360) % 360;
            FpsText.Effect = TextShadow;
        }
        else
        {
            FpsText.Effect = null;
        }

        RootBorder.CornerRadius = new CornerRadius(Math.Max(0, settings.CornerRadius));
        RootBorder.Padding = new Thickness(
            Math.Max(0, settings.PaddingX),
            Math.Max(0, settings.PaddingY),
            Math.Max(0, settings.PaddingX),
            Math.Max(0, settings.PaddingY));
        RootBorder.Background = new SolidColorBrush(ParseColor(settings.BackgroundColor, Colors.Transparent));

        ClickThrough = settings.ClickThrough;

        // 位置：已经显示过就别再搬（用户可能刚拖过），只在第一次摆位
        if (!_positioned)
        {
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
            _positioned = true;
        }

        UpdateTransparent();
    }

    private bool _positioned;

    /// <summary>只改文字 —— 帧率刷新走这儿，不碰窗口状态。</summary>
    public void SetText(string text)
    {
        if (!string.Equals(FpsText.Text, text, StringComparison.Ordinal))
        {
            FpsText.Text = text;
        }
    }

    /// <summary>让用户"拖过"这件事能被记住：插件订阅它来存位置。</summary>
    public void RememberCurrentPosition(ProcessFpsSettings settings)
    {
        settings.WindowLeft = Left;
        settings.WindowTop = Top;
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

    /// <summary>清掉所有者窗口（宿主主窗口最小化时，被拥有的窗口会跟着不可见）。</summary>
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

            MakeToolWindow();
        }
        catch
        {
        }
    }

    /// <summary>
    /// 不占任务栏 / 不进 Alt+Tab。必须用 Win32 的 WS_EX_TOOLWINDOW，
    /// 不能靠 WPF 的 ShowInTaskbar="False" —— 那玩意是"挂个隐藏所有者"实现的，
    /// 而我们为了让窗口不被主窗口带着最小化，又必须把所有者清掉（见 DetachFromOwner）。
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

    private static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) &&
                ColorConverter.ConvertFromString(text) is Color color)
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
