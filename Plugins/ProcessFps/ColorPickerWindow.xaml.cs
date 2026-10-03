using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace MinecraftChatOverlay.Plugins.ProcessFps;

/// <summary>
/// 纯 WPF 取色器（自己画的 RGB+A 滑块）。
/// 不用 WinForms 的 ColorDialog 是因为它在插件里调不起来。它自带完整的背景与控件，
/// 不依赖宿主的主题资源 —— 独立窗口也拿不到宿主挂在 Window 层的那本字典。
/// </summary>
public partial class ColorPickerWindow : Window
{
    private bool _loading = true;

    public ColorPickerWindow(string hex)
    {
        InitializeComponent();

        var color = Parse(hex);
        RSlider.Value = color.R;
        GSlider.Value = color.G;
        BSlider.Value = color.B;
        ASlider.Value = color.A;
        _loading = false;
        UpdatePreview();
    }

    public string ResultHex { get; private set; } = "#FFFFFFFF";

    /// <summary>弹一个取色器；用户取消就返回原值。</summary>
    public static string Pick(Window? owner, string current)
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

    private void Slider_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        RText.Text = ((int)RSlider.Value).ToString();
        GText.Text = ((int)GSlider.Value).ToString();
        BText.Text = ((int)BSlider.Value).ToString();
        AText.Text = ((int)ASlider.Value).ToString();

        var color = Color.FromArgb((byte)ASlider.Value, (byte)RSlider.Value, (byte)GSlider.Value, (byte)BSlider.Value);
        Preview.Background = new SolidColorBrush(color);
        HexText.Text = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        ResultHex = HexText.Text;

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
