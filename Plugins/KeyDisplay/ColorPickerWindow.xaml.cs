using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace MinecraftChatOverlay.Plugins.KeyDisplay;

/// <summary>
/// 纯 WPF 取色器：一排常用色板（点一下直接套用）+ RGB/A 四条滑块自己微调。
/// 不用 WinForms 的 ColorDialog 是因为它在插件里调不起来。它自带完整的背景与控件，
/// 不依赖宿主的主题资源 —— 独立窗口也拿不到宿主挂在 Window 层的那本字典。
/// </summary>
public partial class ColorPickerWindow : Window
{
    /// <summary>常用色板。前四个是黑白和两种半透明黑（按键背景最常用的）。</summary>
    private static readonly string[] PresetColors =
    {
        "#FFFFFFFF", "#FF9CA3AF", "#FF000000", "#80000000",
        "#FFEF4444", "#FFF97316", "#FFFACC15", "#FF22C55E",
        "#FF06B6D4", "#FF3B82F6", "#FF8B5CF6", "#FFEC4899",
    };

    private bool _loading = true;

    public ColorPickerWindow(string hex)
    {
        InitializeComponent();

        FillPresets();

        var color = Parse(hex);
        RSlider.Value = color.R;
        GSlider.Value = color.G;
        BSlider.Value = color.B;
        ASlider.Value = color.A;
        _loading = false;
        UpdatePreview();
    }

    private void FillPresets()
    {
        var borderBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        foreach (var hex in PresetColors)
        {
            var swatch = new Border
            {
                // 24 + 4 = 28 一块，12 块正好 336，能在一行里放完（窗口内容区约 348）
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(5),
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Parse(hex)),
                Cursor = Cursors.Hand,
                Tag = hex,
                ToolTip = hex,
            };

            swatch.MouseLeftButtonDown += Swatch_Click;
            SwatchPanel.Children.Add(swatch);
        }
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string hex)
        {
            return;
        }

        var color = Parse(hex);
        RSlider.Value = color.R;
        GSlider.Value = color.G;
        BSlider.Value = color.B;
        ASlider.Value = color.A;
        UpdatePreview();
    }

    public string ResultHex { get; private set; } = "#FFFFFFFF";

    /// <summary>
    /// 弹调色盘。优先用**宿主内置模块同款的 <c>System.Windows.Forms.ColorDialog</c>** ——
    /// 就是「击杀反馈」页选边缘颜色时弹的那个：左边一排预设，右边色域可以随便调、还能自定义，
    /// 不是"只能在预设里挑"。
    ///
    /// 万一在插件的加载上下文里调不起来（历史上网易云歌词插件就踩过这个坑），
    /// 退回本文件这个自绘的 RGB/A 滑块窗 —— 有兜底总比点了没反应强。
    /// </summary>
    public static string Pick(Window? owner, string current)
    {
        return TryPickWithSystemDialog(current, out var picked) ? picked : PickWithOwnWindow(owner, current);
    }

    /// <summary>
    /// 能不能用系统调色盘。false = 调不起来，调用方该走兜底。
    ///
    /// ⚠ 这里的写法**逐条对齐宿主 <c>MainWindow.PickColorHex</c>**（击杀反馈页选边缘颜色用的那个）：
    ///   · <c>FullOpen = true</c> —— 一打开就展开色域，不用再点一下「规定自定义颜色」
    ///   · **不设 <c>AnyColor</c>** —— 宿主也没设。有 <c>FullOpen</c> 就能调任意色了
    ///   · **显式保留原来的 A 通道** —— 调色盘不支持 alpha，它返回的 A 恒为 255，
    ///     直接拿来用会把用户设好的透明度抹平（纯色）
    ///   · hex 格式也跟宿主一致：**纯色时省略 A**（`#RRGGBB`），半透明才写 `#AARRGGBB`
    /// </summary>
    private static bool TryPickWithSystemDialog(string currentHex, out string result)
    {
        result = currentHex;

        try
        {
            var start = Parse(currentHex);

            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                ShowHelp = false,
                Color = System.Drawing.Color.FromArgb(start.A, start.R, start.G, start.B),
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var c = dialog.Color;

                // 只取 RGB，A 用原来那个 —— ColorDialog 的 A 恒为 255，照抄会把透明度抹掉
                var picked = Color.FromArgb(start.A, c.R, c.G, c.B);
                result = FormatHex(picked);
            }

            // 能弹出来就算成功 —— 用户点取消时 result 保持原值
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 输出 hex，格式跟宿主 <c>MainWindow.PickColorHex</c> 一致：**纯色省掉 A**，半透明才写全。
    /// 好处是色块旁边那行字不会一直挂着一串没必要的 "FF"，跟宿主界面看起来是同一套。
    ///
    /// internal：页面写回颜色时也走这里，别两处各写一遍格式化。
    /// </summary>
    internal static string FormatHex(Color color) =>
        color.A == 0xFF
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string PickWithOwnWindow(Window? owner, string current)
    {
        try
        {
            var dialog = new ColorPickerWindow(current);
            if (owner is not null && owner.IsVisible)
            {
                dialog.Owner = owner;
            }

            return dialog.ShowDialog() == true ? dialog.ResultHex : current;
        }
        catch
        {
            return current;
        }
    }

    private static Color Parse(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color color)
            {
                return color;
            }
        }
        catch
        {
        }

        return Colors.White;
    }

    /// <summary>自检用：验 hex 能不能解析回来（尤其"纯色省掉 A"之后那种 7 字符写法）。</summary>
    internal static Color ParseForTest(string hex) => Parse(hex);

    private void Slider_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        RText.Text = ((int)RSlider.Value).ToString();
        GText.Text = ((int)GSlider.Value).ToString();
        BText.Text = ((int)BSlider.Value).ToString();
        AText.Text = ((int)ASlider.Value).ToString();

        var color = Color.FromArgb((byte)ASlider.Value, (byte)RSlider.Value, (byte)GSlider.Value, (byte)BSlider.Value);
        Preview.Background = new SolidColorBrush(color);

        // 跟系统调色盘那条路径统一格式（纯色省略 A）—— 两条路的输出不该长得不一样
        ResultHex = FormatHex(color);
        HexText.Text = ResultHex;

        if (!_loading)
        {
            Title = "选择颜色 —— " + ResultHex;
        }
    }

    private void White_Click(object sender, RoutedEventArgs e)
    {
        RSlider.Value = 255;
        GSlider.Value = 255;
        BSlider.Value = 255;
        ASlider.Value = 255;
        UpdatePreview();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
