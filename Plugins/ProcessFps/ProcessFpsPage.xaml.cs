using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MinecraftChatOverlay.Plugin;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace MinecraftChatOverlay.Plugins.ProcessFps;

/// <summary>
/// 「游戏 FPS」设置页。控件一律用宿主的样式键（见 ProcessFpsPage.xaml），
/// 页面本身只负责"把控件值搬进设置、再让插件立刻应用"。
///
/// 闸门说明（DEV-NOTES 坑 53/58）：构造里填控件值会触发 Checked / SelectionChanged / TextChanged，
/// 那些处理器一跑就会把"还没填上的空值"存回配置，所以填值期间必须把 _loading 关掉。
/// </summary>
public partial class ProcessFpsPage : System.Windows.Controls.UserControl
{
    private readonly ProcessFpsPlugin _plugin;

    /// <summary>true = 正在往控件里填值 / 页面还没准备好，事件处理器直接返回。</summary>
    private bool _loading = true;

    private ProcessFpsSettings _settings => _plugin.Settings;

    private static readonly string[] CommonFonts =
    {
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "Segoe UI",
        "SimHei",
        "SimSun",
        "Consolas",
        "Cascadia Mono",
        "Arial",
        "Impact",
        "微软雅黑",
    };

    private static readonly (int Ms, string Text)[] RefreshChoices =
    {
        (100, "100 毫秒（最跟手）"),
        (250, "250 毫秒（和钩子同步，推荐）"),
        (500, "500 毫秒"),
        (1000, "1 秒（最省）"),
    };

    public ProcessFpsPage(ProcessFpsPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        _loading = true;
        try
        {
            FillFonts();
            FillRefreshChoices();
            LoadFromSettings();
            RefreshTargetList();
        }
        finally
        {
            _loading = false;
        }

        UpdateValueLabels();
        RefreshPositionText();
        UpdateDiagnostics();
    }

    // ===================== 初始化填值 =====================

    private void FillFonts()
    {
        foreach (var font in CommonFonts)
        {
            FontComboBox.Items.Add(font);
        }
    }

    private void FillRefreshChoices()
    {
        foreach (var choice in RefreshChoices)
        {
            RefreshComboBox.Items.Add(new ComboBoxItem { Content = choice.Text, Tag = choice.Ms });
        }
    }

    private void LoadFromSettings()
    {
        var s = _settings;

        EnabledCheckBox.IsChecked = s.Enabled;
        HideWhenNoDataCheckBox.IsChecked = s.HideWhenNoData;
        ClickThroughCheckBox.IsChecked = s.ClickThrough;
        ShadowEnabledCheckBox.IsChecked = s.ShadowEnabled;

        PrefixBox.Text = s.Prefix;
        SuffixBox.Text = s.Suffix;
        NoDataBox.Text = s.NoDataText;

        FontComboBox.Text = s.FontFamily;

        FontSizeSlider.Value = Math.Clamp(s.FontSize, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
        DecimalsSlider.Value = Math.Clamp(s.Decimals, 0, 2);
        ShadowBlurSlider.Value = Math.Clamp(s.ShadowBlur, 0, 30);
        ShadowOffsetSlider.Value = Math.Clamp(s.ShadowOffset, 0, 12);
        ShadowOpacitySlider.Value = Math.Clamp(s.ShadowOpacity, 0, 1);
        ShadowDirectionSlider.Value = Math.Clamp(s.ShadowDirection, 0, 359);
        CornerSlider.Value = Math.Clamp(s.CornerRadius, 0, 40);
        PaddingXSlider.Value = Math.Clamp(s.PaddingX, 0, 60);
        PaddingYSlider.Value = Math.Clamp(s.PaddingY, 0, 40);

        // 刷新间隔：选项里没有就退回最接近的
        var index = Array.FindIndex(RefreshChoices, c => c.Ms == s.RefreshMs);
        RefreshComboBox.SelectedIndex = index >= 0 ? index : 1;

        RefreshColorPreviews();
    }

    private void RefreshColorPreviews()
    {
        var s = _settings;
        TextColorPreview.Background = MakeBrush(s.TextColor, Colors.White);
        ShadowColorPreview.Background = MakeBrush(s.ShadowColor, Colors.Black);
        BackgroundColorPreview.Background = MakeBrush(s.BackgroundColor, Colors.Transparent);
        TextColorHexText.Text = s.TextColor;
        ShadowColorHexText.Text = s.ShadowColor;
        BackgroundColorHexText.Text = s.BackgroundColor;
        BoldButton.Content = s.Bold ? "粗体：开" : "粗体：关";
    }

    private void UpdateValueLabels()
    {
        FontSizeText.Text = ((int)Math.Round(FontSizeSlider.Value)) + " px";
        DecimalsText.Text = ((int)Math.Round(DecimalsSlider.Value)) + " 位";
        ShadowBlurText.Text = ((int)Math.Round(ShadowBlurSlider.Value)).ToString();
        ShadowOffsetText.Text = ((int)Math.Round(ShadowOffsetSlider.Value)).ToString();
        ShadowOpacityText.Text = ShadowOpacitySlider.Value.ToString("0.00");
        ShadowDirectionText.Text = ((int)Math.Round(ShadowDirectionSlider.Value)) + "°";
        CornerText.Text = ((int)Math.Round(CornerSlider.Value)).ToString();
        PaddingXText.Text = ((int)Math.Round(PaddingXSlider.Value)).ToString();
        PaddingYText.Text = ((int)Math.Round(PaddingYSlider.Value)).ToString();
    }

    // ===================== 目标列表 =====================

    private void ComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        // 下拉弹出层贴到控件下沿（和宿主里那个同名处理器一个作用，插件页面自己带一份）
        if (sender is ComboBox comboBox &&
            comboBox.Template.FindName("PART_Popup", comboBox) is System.Windows.Controls.Primitives.Popup popup)
        {
            popup.PlacementTarget = comboBox;
            popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshTargetList();
        UpdateDiagnostics();
    }

    private void RefreshTargetList()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var targets = _plugin.GetGameWindows();
            TargetComboBox.ItemsSource = targets;

            if (targets.Count == 0)
            {
                return;
            }

            var s = _settings;

            // 先按 PID 找（最准），再退化成窗口标题、进程名
            var index = -1;
            if (s.TargetProcessId > 0)
            {
                index = targets.ToList().FindIndex(t => t.ProcessId == s.TargetProcessId);
            }

            if (index < 0 && !string.IsNullOrWhiteSpace(s.TargetWindowTitle))
            {
                index = targets.ToList().FindIndex(t =>
                    string.Equals(t.Title, s.TargetWindowTitle, StringComparison.Ordinal));
            }

            if (index < 0 && !string.IsNullOrWhiteSpace(s.TargetProcessName))
            {
                index = targets.ToList().FindIndex(t =>
                    string.Equals(t.ProcessName, s.TargetProcessName, StringComparison.OrdinalIgnoreCase));
            }

            TargetComboBox.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void TargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (TargetComboBox.SelectedItem is GameWindowInfo info)
        {
            var s = _settings;
            s.TargetProcessId = info.ProcessId;
            s.TargetProcessName = info.ProcessName;
            s.TargetWindowTitle = info.Title;

            _plugin.SaveSettings();
            _plugin.NoteTargetChanged();
        }
    }

    // ===================== 值变化统一入口 =====================

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var s = _settings;

        s.Enabled = EnabledCheckBox.IsChecked == true;
        s.HideWhenNoData = HideWhenNoDataCheckBox.IsChecked == true;
        s.ClickThrough = ClickThroughCheckBox.IsChecked == true;
        s.ShadowEnabled = ShadowEnabledCheckBox.IsChecked == true;

        s.Prefix = PrefixBox.Text ?? "";
        s.Suffix = SuffixBox.Text ?? "";
        s.NoDataText = NoDataBox.Text ?? "";

        var typedFont = (FontComboBox.Text ?? "").Trim();
        if (typedFont.Length > 0)
        {
            s.FontFamily = typedFont;
        }

        s.FontSize = FontSizeSlider.Value;
        s.Decimals = (int)Math.Round(DecimalsSlider.Value);
        s.ShadowBlur = ShadowBlurSlider.Value;
        s.ShadowOffset = ShadowOffsetSlider.Value;
        s.ShadowOpacity = ShadowOpacitySlider.Value;
        s.ShadowDirection = ShadowDirectionSlider.Value;
        s.CornerRadius = CornerSlider.Value;
        s.PaddingX = PaddingXSlider.Value;
        s.PaddingY = PaddingYSlider.Value;

        if (RefreshComboBox.SelectedItem is ComboBoxItem item && item.Tag is int ms)
        {
            s.RefreshMs = ms;
        }

        UpdateValueLabels();

        // 立刻生效（窗口外观 / 启停 / 刷新节奏），然后存盘
        _plugin.OnSettingsChanged();
        _plugin.SaveSettings();
    }

    private void FontComboBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 手打字体名之后不会触发 SelectionChanged，失焦时补一次
        Option_Changed(sender, e);
    }

    // ===================== 颜色 =====================

    private void TextColorButton_Click(object sender, RoutedEventArgs e)
    {
        var picked = ColorPickerWindow.Pick(Window.GetWindow(this), _settings.TextColor);
        _settings.TextColor = picked;
        AfterColorChanged();
    }

    private void ShadowColorButton_Click(object sender, RoutedEventArgs e)
    {
        var picked = ColorPickerWindow.Pick(Window.GetWindow(this), _settings.ShadowColor);
        _settings.ShadowColor = picked;
        AfterColorChanged();
    }

    private void BackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        var picked = ColorPickerWindow.Pick(Window.GetWindow(this), _settings.BackgroundColor);
        _settings.BackgroundColor = picked;
        AfterColorChanged();
    }

    private void BackgroundClearButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundColor = "#00000000";
        AfterColorChanged();
    }

    private void AfterColorChanged()
    {
        RefreshColorPreviews();
        _plugin.OnSettingsChanged();
        _plugin.SaveSettings();
    }

    private void BoldButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Bold = !_settings.Bold;
        RefreshColorPreviews();
        _plugin.OnSettingsChanged();
        _plugin.SaveSettings();
    }

    // ===================== 位置 =====================

    private void MoveTopLeft_Click(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        _settings.WindowLeft = area.Left + 24;
        _settings.WindowTop = area.Top + 24;
        AfterPositionChanged();
    }

    private void MoveTopRight_Click(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        // 窗口宽度要等布局算完才知道，这里按当前实际宽度推
        var width = _plugin.OverlayWidth > 1 ? _plugin.OverlayWidth : 200;
        _settings.WindowLeft = Math.Max(area.Left, area.Right - width - 24);
        _settings.WindowTop = area.Top + 24;
        AfterPositionChanged();
    }

    private void AfterPositionChanged()
    {
        _plugin.MoveOverlay();
        _plugin.SaveSettings();
        RefreshPositionText();
    }

    public void RefreshPositionText()
    {
        var s = _settings;
        PositionText.Text = $"当前位置 {s.WindowLeft:0} , {s.WindowTop:0}";
    }

    // ===================== 诊断 =====================

    private void RefreshDiagnostics_Click(object sender, RoutedEventArgs e) => UpdateDiagnostics();

    private void DumpLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _plugin.DumpDiagnostics();
            MessageBox.Show("已写到：\n" + path, "游戏 FPS", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("写失败：" + ex.Message, "游戏 FPS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>插件每轮采样后调一次，页面没显示时调了也无所谓。</summary>
    internal void OnSample(FpsSample sample, string status)
    {
        try
        {
            LinkStatusText.Text = status;
            DiagnosticsText.Text = _plugin.BuildDiagnostics();
        }
        catch
        {
            // 页面更新失败不该影响采样
        }
    }

    private void UpdateDiagnostics()
    {
        try
        {
            DiagnosticsText.Text = _plugin.BuildDiagnostics();
            LinkStatusText.Text = _plugin.BuildStatus();
        }
        catch
        {
        }
    }

    private static Brush MakeBrush(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) &&
                ColorConverter.ConvertFromString(hex) is Color color)
            {
                return new SolidColorBrush(color);
            }
        }
        catch
        {
        }

        return new SolidColorBrush(fallback);
    }
}
