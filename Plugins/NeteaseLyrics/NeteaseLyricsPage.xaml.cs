using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MinecraftChatOverlay.Plugins.NeteaseLyrics;

public partial class NeteaseLyricsPage : System.Windows.Controls.UserControl
{
    private static readonly string[] CommonFonts =
    {
        "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "等线", "黑体", "宋体", "楷体", "Consolas",
    };

    private readonly NeteaseLyricsPlugin _plugin;
    private readonly DispatcherTimer _timer;
    private bool _loading = true;

    public NeteaseLyricsPage(NeteaseLyricsPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        _loading = true;

        try
        {
            PopulateAndLoad();
        }
        catch (Exception ex)
        {
            _plugin.Log("[歌词] 页面初始化失败：" + ex.Message);
        }
        finally
        {
            _loading = false;   // 坑 53：填控件期间不许回写设置
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    public void StopTimer()
    {
        try { _timer.Stop(); } catch { }
    }

    private void PopulateAndLoad()
    {
        var s = _plugin.Settings;

        FontComboBox.Items.Clear();
        foreach (var name in CommonFonts)
        {
            FontComboBox.Items.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(s.FontFamily) && !CommonFonts.Contains(s.FontFamily))
        {
            FontComboBox.Items.Add(s.FontFamily);
        }

        FontComboBox.SelectedItem = s.FontFamily;
        if (FontComboBox.SelectedItem is null && FontComboBox.Items.Count > 0)
        {
            FontComboBox.SelectedIndex = 0;
        }

        EnabledCheckBox.IsChecked = s.Enabled;
        ShowTranslationCheckBox.IsChecked = s.ShowTranslation;
        ShowRomajiCheckBox.IsChecked = s.ShowRomaji;
        KaraokeCheckBox.IsChecked = s.Karaoke;
        ShowNextLineCheckBox.IsChecked = s.ShowNextLine;
        ShowTrackInfoCheckBox.IsChecked = s.ShowTrackInfo;
        ClickThroughCheckBox.IsChecked = s.ClickThrough;
        ObsModeCheckBox.IsChecked = s.ObsMode;

        FontSizeSlider.Value = Clamp(s.FontSize, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
        NextScaleSlider.Value = Clamp(s.NextLineScale, NextScaleSlider.Minimum, NextScaleSlider.Maximum);
        TranslationScaleSlider.Value = Clamp(s.TranslationScale, TranslationScaleSlider.Minimum, TranslationScaleSlider.Maximum);
        RomajiScaleSlider.Value = Clamp(s.RomajiScale, RomajiScaleSlider.Minimum, RomajiScaleSlider.Maximum);
        OpacitySlider.Value = Clamp(s.BackgroundOpacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        ShadowBlurSlider.Value = Clamp(s.ShadowBlur, ShadowBlurSlider.Minimum, ShadowBlurSlider.Maximum);
        ShadowOffsetSlider.Value = Clamp(s.ShadowOffset, ShadowOffsetSlider.Minimum, ShadowOffsetSlider.Maximum);
        ShadowDirectionSlider.Value = Clamp(s.ShadowDirection, ShadowDirectionSlider.Minimum, ShadowDirectionSlider.Maximum);
        ShadowOpacitySlider.Value = Clamp(s.ShadowOpacity, ShadowOpacitySlider.Minimum, ShadowOpacitySlider.Maximum);
        LyricOffsetSlider.Value = Clamp(s.LyricOffsetMs, LyricOffsetSlider.Minimum, LyricOffsetSlider.Maximum);
        WidthSlider.Value = Clamp(s.WindowWidth, WidthSlider.Minimum, WidthSlider.Maximum);

        UpdateLabels();
    }

    private static double Clamp(double value, double min, double max) => value < min ? min : (value > max ? max : value);

    private void UpdateLabels()
    {
        FontSizeText.Text = ((int)FontSizeSlider.Value) + " px";
        NextScaleText.Text = NextScaleSlider.Value.ToString("0.00") + "×";
        TranslationScaleText.Text = TranslationScaleSlider.Value.ToString("0.00") + "×";
        RomajiScaleText.Text = RomajiScaleSlider.Value.ToString("0.00") + "×";
        OpacityText.Text = OpacitySlider.Value.ToString("P0");
        ShadowBlurText.Text = ((int)ShadowBlurSlider.Value).ToString();
        ShadowOffsetText.Text = ShadowOffsetSlider.Value.ToString("0.#") + " px";
        ShadowDirectionText.Text = ((int)ShadowDirectionSlider.Value) + "°";
        ShadowOpacityText.Text = ShadowOpacitySlider.Value.ToString("P0");
        LyricOffsetText.Text = ((int)LyricOffsetSlider.Value) + " ms";
        WidthText.Text = ((int)WidthSlider.Value) + " px";
        BoldText.Text = _plugin.Settings.Bold ? "已开" : "已关";
        RomajiItalicButton.Content = _plugin.Settings.RomajiItalic ? "斜体：开" : "斜体：关";
        FontTipText.Text = _plugin.Settings.FontFamily;

        // 色块 = 真颜色 + 色值文本（照软件本体的做法：Border 当色块，旁边 Consolas 写 hex）
        ApplySwatch(TextColorPreview, TextColorHexText, _plugin.Settings.TextColor, System.Windows.Media.Colors.White);
        ApplySwatch(RomajiColorPreview, RomajiColorHexText, _plugin.Settings.RomajiColor, System.Windows.Media.Colors.White);
        ApplySwatch(KaraokeColorPreview, KaraokeColorHexText, _plugin.Settings.KaraokeColor, System.Windows.Media.Colors.DeepSkyBlue);
        ApplySwatch(ShadowColorPreview, ShadowColorHexText, _plugin.Settings.ShadowColor, System.Windows.Media.Colors.Black);
    }

    /// <summary>把一个色块 Border + 色值文本刷成 hex 对应的颜色。</summary>
    private static void ApplySwatch(System.Windows.Controls.Border preview, System.Windows.Controls.TextBlock text, string? hex, System.Windows.Media.Color fallback)
    {
        preview.Background = new System.Windows.Media.SolidColorBrush(ParseColor(hex, fallback));
        text.Text = FormatHex(hex);
    }

    private static System.Windows.Media.Color ParseColor(string? value, System.Windows.Media.Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value)
                && System.Windows.Media.ColorConverter.ConvertFromString(value) is System.Windows.Media.Color color)
            {
                return color;
            }
        }
        catch
        {
        }

        return fallback;
    }

    /// <summary>色值文本：不透明显示 #RRGGBB，带透明度显示 #AARRGGBB。</summary>
    private static string FormatHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(默认)";
        }

        try
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(value) is System.Windows.Media.Color color)
            {
                return color.A == 0xFF
                    ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                    : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            }
        }
        catch
        {
        }

        return value;
    }


    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        // XAML 加载期间 Minimum/Maximum/SelectionChanged 都会触发本事件，那时 _plugin 还没赋值。
        if (_loading || _plugin is null)
        {
            return;
        }

        var s = _plugin.Settings;
        s.Enabled = EnabledCheckBox.IsChecked == true;
        s.ShowTranslation = ShowTranslationCheckBox.IsChecked == true;
        s.ShowRomaji = ShowRomajiCheckBox.IsChecked == true;
        s.Karaoke = KaraokeCheckBox.IsChecked == true;
        s.ShowNextLine = ShowNextLineCheckBox.IsChecked == true;
        s.ShowTrackInfo = ShowTrackInfoCheckBox.IsChecked == true;
        s.ClickThrough = ClickThroughCheckBox.IsChecked == true;
        s.ObsMode = ObsModeCheckBox.IsChecked == true;

        if (FontComboBox.SelectedItem is string font && !string.IsNullOrWhiteSpace(font))
        {
            s.FontFamily = font;
        }

        s.FontSize = FontSizeSlider.Value;
        s.NextLineScale = NextScaleSlider.Value;
        s.TranslationScale = TranslationScaleSlider.Value;
        s.RomajiScale = RomajiScaleSlider.Value;
        s.BackgroundOpacity = OpacitySlider.Value;
        s.ShadowBlur = ShadowBlurSlider.Value;
        s.ShadowOffset = ShadowOffsetSlider.Value;
        s.ShadowDirection = ShadowDirectionSlider.Value;
        s.ShadowOpacity = ShadowOpacitySlider.Value;
        s.LyricOffsetMs = LyricOffsetSlider.Value;
        s.WindowWidth = WidthSlider.Value;

        UpdateLabels();
        _plugin.SaveSettings();
        _plugin.ShowWindow();

        if (!s.Enabled)
        {
            _plugin.HideWindow();
        }
    }


    private void HideGuideButton_Click(object sender, RoutedEventArgs e)
    {
        _plugin.GuideVisible = false;
        GuidePanel.Visibility = Visibility.Collapsed;
    }

    private void RefreshLogButton_Click(object sender, RoutedEventArgs e) => LogBox.Text = _plugin.RecentLog;

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        _plugin.ClearLog();
        LogBox.Text = "";
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _plugin.LogFilePath;
            if (!System.IO.File.Exists(path))
            {
                System.Windows.MessageBox.Show("还没有日志文件。", "插件日志", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打不开：" + ex.Message, "插件日志", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
    private void RomajiItalicButton_Click(object sender, RoutedEventArgs e)
    {
        _plugin.Settings.RomajiItalic = !_plugin.Settings.RomajiItalic;
        UpdateLabels();
        _plugin.SaveSettings();
        _plugin.ShowWindow();
    }

    /// <summary>【使用说明】：弹软件本体那种独立窗口（不是页面内展开、也不是系统 MessageBox）。</summary>
    private void OnboardingButton_Click(object sender, RoutedEventArgs e)
        => _plugin.ShowHelp(Window.GetWindow(this));


    private void InstallRelayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _plugin is null)
        {
            return;
        }

        // 结果直接写进引导卡片（顺手把它展开），不再弹系统 MessageBox
        var message = _plugin.InstallRelay();
        _plugin.GuideVisible = true;
        GuidePanel.Visibility = Visibility.Visible;
        RelayResultText.Text = message;
    }


    /// <summary>
    /// 调色：主路用系统原生的 WinForms 取色器 —— 和软件本体（MainWindow.xaml.cs 的 PickColorHex）
    /// 完全同一套做法、同一个外观；取消返回 null。WinForms 取色器没有 alpha 通道，所以 alpha 沿用原色。
    /// 万一系统取色器起不来，退回插件自带的 WPF 取色器，别让用户连颜色都改不了。
    /// </summary>
    private string? PickColorHex(string? currentHex)
    {
        var current = ParseColor(currentHex, System.Windows.Media.Colors.White);

        try
        {
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B),
            };

            _plugin.Log("[歌词] 打开系统取色器，当前=" + (currentHex ?? "(空)"));
            var result = dialog.ShowDialog();
            _plugin.Log("[歌词] 系统取色器返回=" + result + "，选中=" + dialog.Color.ToArgb().ToString("X8"));

            if (result != System.Windows.Forms.DialogResult.OK)
            {
                return null;
            }

            var picked = dialog.Color;
            return HexOf(System.Windows.Media.Color.FromArgb(current.A, picked.R, picked.G, picked.B));
        }
        catch (Exception ex)
        {
            _plugin.Log("[歌词] 系统取色器起不来，改用插件自带的 WPF 取色器：" + ex.Message);
        }

        return ColorPickerWindow.Pick(Window.GetWindow(this), currentHex ?? "#FFFFFFFF");
    }

    private static string HexOf(System.Windows.Media.Color color) =>
        color.A == 0xFF
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>取色 → 写设置 → 刷色块 → 存盘 → 让歌词窗立刻用上新颜色。</summary>
    private void PickAndApply(string what, string? current, Action<string> apply)
    {
        if (_loading || _plugin is null)
        {
            return;
        }

        var picked = PickColorHex(current);
        if (picked is null || string.Equals(picked, current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _plugin.Log($"[歌词] {what} → {picked}");
        apply(picked);
        UpdateLabels();
        _plugin.SaveSettings();
        _plugin.ShowWindow();
    }


    private void TextColorButton_Click(object sender, RoutedEventArgs e) =>
        PickAndApply("文字颜色", _plugin.Settings.TextColor, hex => _plugin.Settings.TextColor = hex);

    private void ShadowColorButton_Click(object sender, RoutedEventArgs e) =>
        PickAndApply("阴影颜色", _plugin.Settings.ShadowColor, hex => _plugin.Settings.ShadowColor = hex);

    private void RomajiColorButton_Click(object sender, RoutedEventArgs e) =>
        PickAndApply("罗马音颜色", _plugin.Settings.RomajiColor, hex => _plugin.Settings.RomajiColor = hex);

    private void KaraokeColorButton_Click(object sender, RoutedEventArgs e) =>
        PickAndApply("已唱颜色", _plugin.Settings.KaraokeColor, hex => _plugin.Settings.KaraokeColor = hex);

    private void BoldButton_Click(object sender, RoutedEventArgs e)
    {
        _plugin.Settings.Bold = !_plugin.Settings.Bold;
        UpdateLabels();
        _plugin.SaveSettings();
        _plugin.ShowWindow();
    }

    private void ResetStyleButton_Click(object sender, RoutedEventArgs e)
    {
        var s = _plugin.Settings;
        var defaults = new NeteaseLyricsSettings();
        s.FontFamily = defaults.FontFamily;
        s.FontSize = defaults.FontSize;
        s.Bold = defaults.Bold;
        s.TextColor = defaults.TextColor;
        s.BackgroundOpacity = defaults.BackgroundOpacity;
        s.ShadowColor = defaults.ShadowColor;
        s.ShadowBlur = defaults.ShadowBlur;
        s.ShadowOpacity = defaults.ShadowOpacity;
        s.ShadowOffset = defaults.ShadowOffset;
        s.ShadowDirection = defaults.ShadowDirection;
        s.TranslationColor = defaults.TranslationColor;
        s.RomajiColor = defaults.RomajiColor;
        s.RomajiItalic = defaults.RomajiItalic;
        s.KaraokeColor = defaults.KaraokeColor;
        s.NextLineScale = defaults.NextLineScale;
        s.LyricOffsetMs = defaults.LyricOffsetMs;

        _loading = true;
        try
        {
            PopulateAndLoad();
        }
        finally
        {
            _loading = false;
        }

        _plugin.SaveSettings();
        _plugin.ShowWindow();
    }

    private void ShowButton_Click(object sender, RoutedEventArgs e)
    {
        _plugin.Settings.Enabled = true;
        if (EnabledCheckBox.IsChecked == true)
        {
            _plugin.SaveSettings();
        }
        else
        {
            EnabledCheckBox.IsChecked = true;
        }

        _plugin.ShowWindow();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => _plugin.HideWindow();

    private void TopRightButton_Click(object sender, RoutedEventArgs e) => _plugin.MoveToTopRight();

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _plugin.RefreshLyrics();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_plugin is null)
        {
            return;
        }

        try
        {
            StatusText.Text = _plugin.Status;
            GuidePanel.Visibility = _plugin.GuideVisible ? Visibility.Visible : Visibility.Collapsed;
            RelayResultText.Text = _plugin.RelayInstallResult;
            LogBox.Text = _plugin.RecentLog;

            var state = _plugin.LastState;
            var document = _plugin.Document;

            var positionText = state is null
                ? "进度：没读到（网易云没在放歌 / 中继插件没加载）"
                : "进度：" + state.PositionNow.ToString("F0") + "s / " + state.Duration.ToString("F0")
                  + "s   播放中=" + state.Playing + "   偏移=" + ((int)_plugin.Settings.LyricOffsetMs) + "ms";
            var lyricsText = document is null
                ? "歌词：未加载"
                : "歌词：《" + document.Title + "》- " + document.Artist + "   " + document.Lines.Count + " 行   翻译=" + document.HasTranslation;

            LyricsText.Text = positionText + "\n" + lyricsText + "\n窗口：" + (_plugin.IsWindowVisible ? "显示中" : "已隐藏");
        }
        catch
        {
        }
    }
}
