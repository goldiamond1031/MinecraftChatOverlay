using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MinecraftChatOverlay.Plugins.FpsOverlay;

/// <summary>
/// FPS 悬浮窗。这是插件自己的独立窗口，不是宿主那个聊天悬浮窗。
///
/// 两条硬要求（照 DEV-NOTES 坑 40 / 41 / 42）：
///   * WS_EX_NOACTIVATE  —— 永远不抢游戏焦点。ShowActivated="False" 只在窗口第一次显示时可靠，
///                          复用型窗口不保证，所以必须在 SourceInitialized 里把扩展样式打上。
///   * WS_EX_TRANSPARENT —— 真鼠标穿透。IsHitTestVisible="False" 只管 WPF 层的命中测试，
///                          窗口在系统眼里依然可点，全屏游戏里光标照样会现形。
/// 另外窗口**常驻**，隐藏时只把 Opacity 归零，不反复 Hide()/Show()（那会重排 z-order、扰动游戏）。
/// </summary>
public partial class FpsOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private IntPtr _handle;
    private bool _clickThrough = true;

    /// <summary>设置里想要的位置（穿透时不能拖，所以位置以设置为准）。</summary>
    private bool _suppressPositionEvent;

    /// <summary>拖动之后位置变了，通知外面存盘。</summary>
    public event Action<double, double>? PositionChanged;

    public FpsOverlayWindow()
    {
        InitializeComponent();
    }

    // ===================== 窗口样式 =====================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        ApplyExtendedStyles();
    }

    private void ApplyExtendedStyles()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var current = GetWindowLong(_handle, GWL_EXSTYLE);
        var wanted = current | WS_EX_NOACTIVATE;

        if (_clickThrough)
        {
            wanted |= WS_EX_TRANSPARENT;
        }
        else
        {
            wanted &= ~WS_EX_TRANSPARENT;
        }

        if (wanted != current)
        {
            SetWindowLong(_handle, GWL_EXSTYLE, wanted);
        }
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        ApplyExtendedStyles();
    }

    // ===================== 显示 / 隐藏 =====================

    /// <summary>显示。隐藏时把鼠标穿透打开，免得 Opacity=0 的窗口还挡着游戏点击。</summary>
    public void ShowOverlay()
    {
        if (!IsVisible)
        {
            Show();
        }

        Opacity = 1;
        SetClickThrough(_clickThrough);
    }

    public void HideOverlay()
    {
        Opacity = 0;
        SetClickThrough(true);
    }

    // ===================== 外观 =====================

    public void ApplySettings(FpsOverlaySettings settings)
    {
        _suppressPositionEvent = true;
        try
        {
            // ----- 文字 -----
            FpsText.FontFamily = new FontFamily(
                string.IsNullOrWhiteSpace(settings.FontFamily) ? "Consolas" : settings.FontFamily);
            FpsText.FontSize = Math.Clamp(settings.FontSize, 8, 160);
            FpsText.FontWeight = ParseFontWeight(settings.FontWeight);
            FpsText.Foreground = new SolidColorBrush(ParseColor(settings.Foreground, Colors.White));

            // ----- 文字阴影 -----
            if (settings.ShadowEnabled)
            {
                var dx = settings.ShadowOffsetX;
                var dy = settings.ShadowOffsetY;
                var depth = Math.Sqrt(dx * dx + dy * dy);

                // WPF 的 Direction 是"从正右方逆时针算的角度"，而屏幕坐标 y 向下，
                // 所以要 360 - atan2(dy, dx)，右下方向才对应默认的 315。
                var degrees = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                var direction = (360.0 - degrees) % 360.0;

                FpsText.Effect = new DropShadowEffect
                {
                    Color = ParseColor(settings.ShadowColor, Colors.Black),
                    BlurRadius = Math.Max(0, settings.ShadowBlur),
                    ShadowDepth = depth,
                    Direction = direction,
                    Opacity = Math.Clamp(settings.ShadowOpacity, 0, 1),
                };
            }
            else
            {
                FpsText.Effect = null;
            }

            // ----- 背景 -----
            RootBorder.CornerRadius = new CornerRadius(Math.Max(0, settings.CornerRadius));
            RootBorder.Padding = new Thickness(
                Math.Max(0, settings.PaddingX), Math.Max(0, settings.PaddingY),
                Math.Max(0, settings.PaddingX), Math.Max(0, settings.PaddingY));

            if (settings.BackgroundEnabled)
            {
                var baseColor = ParseColor(settings.BackgroundColor, Colors.Black);
                var alpha = (byte)Math.Round(Math.Clamp(settings.BackgroundOpacity, 0, 1) * 255);
                RootBorder.Background =
                    new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            }
            else
            {
                RootBorder.Background = Brushes.Transparent;
            }

            // ----- 穿透 -----
            SetClickThrough(settings.ClickThrough);

            // ----- 位置 -----
            ApplyPosition(settings.Left, settings.Top);
        }
        finally
        {
            _suppressPositionEvent = false;
        }
    }

    /// <summary>
    /// 摆位置。整块不在工作区内就挪到右上角 ——
    /// 按 100% 缩放写死的坐标在 150% / 200% 下会整块落到屏幕外（坑 46），
    /// 而且 SystemParameters.WorkArea 给的是 DIP，不是物理像素。
    /// </summary>
    public void ApplyPosition(double left, double top)
    {
        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 1 ? ActualWidth : 160;
        var height = ActualHeight > 1 ? ActualHeight : 40;

        var offScreen =
            double.IsNaN(left) || double.IsNaN(top) ||
            left + width < area.Left + 8 || left > area.Right - 8 ||
            top + height < area.Top + 8 || top > area.Bottom - 8;

        if (offScreen)
        {
            left = area.Right - width - 16;
            top = area.Top + 16;
        }

        Left = left;
        Top = top;
    }

    public void UpdateText(string text)
    {
        if (!string.Equals(FpsText.Text, text, StringComparison.Ordinal))
        {
            FpsText.Text = text;
        }
    }

    // ===================== 拖动 =====================

    /// <summary>穿透关着的时候，按住窗口就能拖。穿透开着收不到鼠标事件，位置只能从设置页改。</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (_clickThrough)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove 在极端情况下会抛（鼠标已经抬起），忽略
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);

        if (_suppressPositionEvent || double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        PositionChanged?.Invoke(Left, Top);
    }

    // ===================== 收尾 =====================

    /// <summary>退出路径必须显式 Close —— 窗口是常驻的，不关的话进程退不干净（坑 42）。</summary>
    public void Shutdown()
    {
        try
        {
            Close();
        }
        catch
        {
            // 关闭流程里出错也不能拦住退出
        }
    }

    // ===================== 小工具 =====================

    private static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        try
        {
            if (ColorConverter.ConvertFromString(text.Trim()) is Color parsed)
            {
                return parsed;
            }
        }
        catch
        {
            // 解析不了就用兜底色
        }

        return fallback;
    }

    private static FontWeight ParseFontWeight(string? name) => name switch
    {
        "Light" => FontWeights.Light,
        "Normal" => FontWeights.Normal,
        "Medium" => FontWeights.Medium,
        "SemiBold" => FontWeights.SemiBold,
        "Bold" => FontWeights.Bold,
        "ExtraBold" => FontWeights.ExtraBold,
        "Black" => FontWeights.Black,
        _ => FontWeights.Bold,
    };
}
