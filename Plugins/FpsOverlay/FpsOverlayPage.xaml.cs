using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.FpsOverlay;

/// <summary>
/// FPS 悬浮窗的设置页（自绘）。
///
/// 两条必须守住的规矩：
///   1. 初始化期间把 <see cref="_loading"/> 闸门关上 —— 填下拉框会触发 SelectionChanged、
///      而"值一变就存盘"会在其他控件还没填上的时候把空值写回配置（DEV-NOTES 坑 53 / 58）。
///   2. 文本输入用 debounce 存盘，别每敲一个键就写一次文件（坑 6）。
/// </summary>
public partial class FpsOverlayPage : UserControl
{
    private static readonly string[] ColorPresets =
    {
        "#FFFFFF", "#000000", "#FF5252", "#FFD740", "#69F0AE", "#40C4FF",
        "#B388FF", "#FF8A65", "#E0E0E0", "#4CAF50", "#F44336", "#2196F3",
    };

    private static readonly string[] WeightNames =
    {
        "Light", "Normal", "Medium", "SemiBold", "Bold", "ExtraBold", "Black",
    };

    private static readonly string[] DecimalOptions = { "0", "1", "2" };

    private readonly FpsOverlayPlugin _plugin;

    /// <summary>初始化闸门：true 的时候一切"控件值变化"都不该回写配置。</summary>
    private bool _loading = true;

    private DispatcherTimer? _saveTimer;
    private DispatcherTimer? _statusTimer;

    // ===================== 构造 =====================

    public FpsOverlayPage(FpsOverlayPlugin plugin)
    {
        _plugin = plugin;

        // 闸门必须在 InitializeComponent 之前就关上：XAML 里的事件绑定在解析时就生效了
        _loading = true;

        InitializeComponent();

        try
        {
            BuildChoices();
            BuildSwatches();

            LeftBox.Text = "";
            TopBox.Text = "";

            LoadFromSettings();
        }
        finally
        {
            _loading = false;
        }

        RefreshTargets();
        UpdateStatus();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
    }

    /// <summary>插件停用 / 程序退出时调，把轮询停干净。</summary>
    public void StopStatusTimer()
    {
        try
        {
            _statusTimer?.Stop();
            _statusTimer = null;
            _saveTimer?.Stop();
            _saveTimer = null;
        }
        catch
        {
            // 停不掉无所谓，进程马上就没了
        }
    }

    // ===================== 候选项 =====================

    private void BuildChoices()
    {
        DecimalsCombo.ItemsSource = DecimalOptions;
        WeightCombo.ItemsSource = WeightNames;

        var fonts = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FontCombo.ItemsSource = fonts;
    }

    private void BuildSwatches()
    {
        BuildSwatchRow(ForegroundSwatches, ForegroundBox);
        BuildSwatchRow(ShadowSwatches, ShadowColorBox);
        BuildSwatchRow(BackgroundSwatches, BackgroundColorBox);
    }

    private void BuildSwatchRow(Panel host, TextBox target)
    {
        foreach (var hex in ColorPresets)
        {
            var button = new Button
            {
                Style = (Style)Resources["ColorSwatchStyle"],
                Background = new SolidColorBrush(ParseColor(hex, Colors.White)),
                ToolTip = hex,
            };

            var captured = hex;
            button.Click += (_, _) =>
            {
                target.Text = captured;   // 会触发 TextChanged → 正常走 ApplyFromUi
            };

            host.Children.Add(button);
        }
    }

    // ===================== 设置 ↔ 控件 =====================

    private void LoadFromSettings()
    {
        var s = _plugin.Settings;

        PrefixBox.Text = s.Prefix;
        SuffixBox.Text = s.Suffix;
        DecimalsCombo.SelectedIndex = Math.Clamp(s.Decimals, 0, DecimalOptions.Length - 1);

        FontCombo.SelectedItem = FontCombo.Items.Contains(s.FontFamily)
            ? s.FontFamily
            : FontCombo.Items.Cast<string>().FirstOrDefault(x =>
                  string.Equals(x, "Consolas", StringComparison.OrdinalIgnoreCase));

        FontSizeBox.Text = s.FontSize.ToString("0.#");
        WeightCombo.SelectedItem = WeightNames.Contains(s.FontWeight) ? s.FontWeight : "Bold";
        ForegroundBox.Text = s.Foreground;

        ShadowCheckBox.IsChecked = s.ShadowEnabled;
        ShadowColorBox.Text = s.ShadowColor;
        ShadowBlurBox.Text = s.ShadowBlur.ToString("0.#");
        ShadowOffsetXBox.Text = s.ShadowOffsetX.ToString("0.#");
        ShadowOffsetYBox.Text = s.ShadowOffsetY.ToString("0.#");
        ShadowOpacityBox.Text = s.ShadowOpacity.ToString("0.##");

        BackgroundCheckBox.IsChecked = s.BackgroundEnabled;
        BackgroundColorBox.Text = s.BackgroundColor;
        BackgroundOpacityBox.Text = s.BackgroundOpacity.ToString("0.##");
        CornerRadiusBox.Text = s.CornerRadius.ToString("0.#");
        PaddingXBox.Text = s.PaddingX.ToString("0.#");
        PaddingYBox.Text = s.PaddingY.ToString("0.#");

        ClickThroughCheckBox.IsChecked = s.ClickThrough;
        VisibleCheckBox.IsChecked = s.OverlayVisible;
    }

    // TextChanged / SelectionChanged / Checked 的参数类型各不相同，不能共用一个处理器
    private void Text_Changed(object sender, TextChangedEventArgs e) => ApplyFromUi();

    private void Select_Changed(object sender, SelectionChangedEventArgs e) => ApplyFromUi();

    private void Toggle_Changed(object sender, RoutedEventArgs e) => ApplyFromUi();

    /// <summary>把界面上所有的值收一遍，写进设置、立刻反映到悬浮窗、再排队存盘。</summary>
    private void ApplyFromUi()
    {
        if (_loading)
        {
            return;
        }

        var s = _plugin.Settings;

        s.Prefix = PrefixBox.Text ?? "";
        s.Suffix = SuffixBox.Text ?? "";
        s.Decimals = DecimalsCombo.SelectedIndex >= 0 ? DecimalsCombo.SelectedIndex : 0;

        if (FontCombo.SelectedItem is string font && !string.IsNullOrWhiteSpace(font))
        {
            s.FontFamily = font;
        }

        s.FontSize = ParseDouble(FontSizeBox.Text, s.FontSize);

        if (WeightCombo.SelectedItem is string weight)
        {
            s.FontWeight = weight;
        }

        s.Foreground = ForegroundBox.Text ?? s.Foreground;

        s.ShadowEnabled = ShadowCheckBox.IsChecked == true;
        s.ShadowColor = ShadowColorBox.Text ?? s.ShadowColor;
        s.ShadowBlur = ParseDouble(ShadowBlurBox.Text, s.ShadowBlur);
        s.ShadowOffsetX = ParseDouble(ShadowOffsetXBox.Text, s.ShadowOffsetX);
        s.ShadowOffsetY = ParseDouble(ShadowOffsetYBox.Text, s.ShadowOffsetY);
        s.ShadowOpacity = ParseDouble(ShadowOpacityBox.Text, s.ShadowOpacity);

        s.BackgroundEnabled = BackgroundCheckBox.IsChecked == true;
        s.BackgroundColor = BackgroundColorBox.Text ?? s.BackgroundColor;
        s.BackgroundOpacity = ParseDouble(BackgroundOpacityBox.Text, s.BackgroundOpacity);
        s.CornerRadius = ParseDouble(CornerRadiusBox.Text, s.CornerRadius);
        s.PaddingX = ParseDouble(PaddingXBox.Text, s.PaddingX);
        s.PaddingY = ParseDouble(PaddingYBox.Text, s.PaddingY);

        s.ClickThrough = ClickThroughCheckBox.IsChecked == true;
        s.OverlayVisible = VisibleCheckBox.IsChecked == true;

        // 位置：留空表示"还没定过位"，用默认的右上角
        var left = ParseOptionalDouble(LeftBox.Text, s.Left);
        var top = ParseOptionalDouble(TopBox.Text, s.Top);
        s.Left = left;
        s.Top = top;

        _plugin.RefreshOverlay();
        QueueSave();
    }

    /// <summary>存盘 debounce —— 打字时别每敲一下写一次盘（坑 6）。</summary>
    private void QueueSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer?.Stop();
                _plugin.SaveSettings();
            };
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ===================== 目标进程 =====================

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshTargets();
        StatusText.Text = TargetComboBox.Items.Count == 0
            ? "没找到带窗口的进程。先把游戏启动起来，再点【刷新列表】。"
            : "列表已刷新，点【注入并开始】。";
    }

    private void RefreshTargets()
    {
        try
        {
            var windows = _plugin.Host.GetGameWindows();
            TargetComboBox.ItemsSource = windows;

            if (windows.Count == 0)
            {
                return;
            }

            var s = _plugin.Settings;

            // 先按 pid 找，再退化成按进程名找（同一台机器可能开着好几个 javaw）
            var index = -1;
            if (s.TargetProcessId > 0)
            {
                // IReadOnlyList 没有 IndexOf，自己扫一遍
                for (var i = 0; i < windows.Count; i++)
                {
                    if (windows[i].ProcessId == s.TargetProcessId)
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index < 0 && !string.IsNullOrWhiteSpace(s.TargetProcessName))
            {
                for (var i = 0; i < windows.Count; i++)
                {
                    if (string.Equals(windows[i].ProcessName, s.TargetProcessName,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
            }

            TargetComboBox.SelectedIndex = index >= 0 ? index : 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = "列出窗口失败：" + ex.Message;
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (TargetComboBox.SelectedItem is not GameWindowInfo target)
        {
            StatusText.Text = "先在上面选一个游戏窗口。";
            return;
        }

        var s = _plugin.Settings;
        s.TargetProcessId = target.ProcessId;
        s.TargetProcessName = target.ProcessName;
        s.TargetWindowTitle = target.Title;

        StatusText.Text = _plugin.StartProbe();
        RefreshTargets();
        UpdateStatus();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _plugin.StopProbe();
        StatusText.Text = "已停止显示。探针还留在目标进程里（手动映射进去的模块卸不掉），"
                        + "但已经不再读它了 —— 再点【注入并开始】就能接着用。";
        UpdateStatus();
    }

    // ===================== 位置 / 重置 =====================

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            LeftBox.Text = "";
            TopBox.Text = "";
            _plugin.Settings.Left = double.NaN;
            _plugin.Settings.Top = double.NaN;
        }
        finally
        {
            _loading = false;
        }

        _plugin.RefreshOverlay();
        QueueSave();
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        var current = _plugin.Settings;
        var fresh = new FpsOverlaySettings
        {
            // 目标和位置保留，只重置外观
            TargetProcessId = current.TargetProcessId,
            TargetProcessName = current.TargetProcessName,
            TargetWindowTitle = current.TargetWindowTitle,
            Left = current.Left,
            Top = current.Top,
            OverlayVisible = current.OverlayVisible,
        };

        _plugin.ReplaceSettings(fresh);

        _loading = true;
        try
        {
            LoadFromSettings();
        }
        finally
        {
            _loading = false;
        }

        _plugin.RefreshOverlay();
        QueueSave();
        StatusText.Text = "外观已恢复默认。";
    }

    // ===================== 状态 =====================

    private void UpdateStatus()
    {
        var reader = _plugin.Reader;

        if (reader == null)
        {
            ProbeStatusText.Text = _plugin.IsRunning
                ? "正在等探针起来…（注入后要等一两百毫秒）"
                : "探针没在跑。选好目标进程后点【注入并开始】。";
            return;
        }

        ProbeStatusText.Text =
            $"目标 pid {reader.TargetPid}"
            + $" · 钩子 {(reader.HookInstalled != 0 ? "已装" : "没装上")}"
            + $" · 命中 gdi32={reader.GdiHooked} / opengl32={reader.WglHooked}"
            + $" · 补丁槽位 {reader.PatchedSlots}"
            + $" · 补扫 {reader.PumpRuns} 轮"
            + $" · 当前 {reader.CurrentFps:F1} FPS";
    }

    // ===================== 小工具 =====================

    private static double ParseDouble(string? text, double fallback)
    {
        return double.TryParse(text, out var value) ? value : fallback;
    }

    private static double ParseOptionalDouble(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return double.NaN;
        }

        return double.TryParse(text, out var value) ? value : fallback;
    }

    private static Color ParseColor(string text, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(text) is Color parsed)
            {
                return parsed;
            }
        }
        catch
        {
            // 忽略，用兜底色
        }

        return fallback;
    }

    /// <summary>下拉框的弹出层贴到控件下沿（和宿主里那个同名处理器一个作用，插件页自己带一份）。</summary>
    private void ComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox &&
            comboBox.Template.FindName("PART_Popup", comboBox) is System.Windows.Controls.Primitives.Popup popup)
        {
            popup.PlacementTarget = comboBox;
            popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
    }
}
