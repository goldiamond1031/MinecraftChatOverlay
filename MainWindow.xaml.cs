using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Services;

namespace MinecraftChatOverlay;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MinecraftLogWatcher _watcher = new();
    private readonly ObservableCollection<TextColorRule> _colorRules = new();
    private readonly ObservableCollection<TextReplaceRule> _replaceRules = new();
    private readonly ObservableCollection<string> _blockKeywords = new();
    private readonly List<FontItem> _fontItems = new();
    private OverlayWindow? _overlay;
    private bool _loading = true;
    private string _colorRuleColor = "#FFFF0000";
    private string _colorRuleMatchColor = "";
    private bool _autoGgSending;
    private DateTime _lastAutoGgAt = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowIcon();
        _settings = SettingsService.Load();
        InitializeComboBoxes();
        LoadRuleCollections();
        LoadUiFromSettings();
        _loading = false;
        SubscribeImmediateApply();
        Loaded += MainWindow_Loaded;
        LogStatus("配置已加载：" + SettingsService.ConfigPath);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_watcher.IsRunning && !string.IsNullOrWhiteSpace(_settings.LogPath))
        {
            StartListening();
        }
    }

    private void LoadWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "MinecraftChatOverlay.ico");
            if (File.Exists(iconPath))
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath));
            }
        }
        catch
        {
            // 图标加载失败不影响程序运行。
        }
    }

    private void InitializeComboBoxes()
    {
        // 列出当前 Windows 已安装的所有系统字体。
        // 很多中文字体有中文显示名（例如“印品鸿蒙体”），这里同时显示中文名和英文内部名。
        _fontItems.Clear();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            var source = family.Source;
            var localized = GetLocalizedFontName(family);
            var display = string.IsNullOrEmpty(localized) || string.Equals(localized, source, StringComparison.OrdinalIgnoreCase)
                ? source
                : $"{localized} ({source})";
            _fontItems.Add(new FontItem(display, source));
        }

        _fontItems.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
        FontFamilyComboBox.ItemsSource = _fontItems;
        FontFamilyComboBox.DisplayMemberPath = nameof(FontItem.Display);

        FontWeightComboBox.Items.Add("Normal");
        FontWeightComboBox.Items.Add("SemiBold");
        FontWeightComboBox.Items.Add("Bold");
        FontWeightComboBox.Items.Add("Light");

        LogEncodingComboBox.Items.Add("Auto");
        LogEncodingComboBox.Items.Add("UTF-8");
        LogEncodingComboBox.Items.Add("GBK");

        ColorRuleWeightComboBox.Items.Add("Normal");
        ColorRuleWeightComboBox.Items.Add("Thin");
        ColorRuleWeightComboBox.Items.Add("Light");
        ColorRuleWeightComboBox.Items.Add("SemiBold");
        ColorRuleWeightComboBox.Items.Add("Bold");
        ColorRuleWeightComboBox.Text = "Light";
    }

    private void LoadRuleCollections()
    {
        _colorRules.Clear();
        foreach (var rule in _settings.ColorRules)
        {
            _colorRules.Add(rule);
        }

        _replaceRules.Clear();
        foreach (var rule in _settings.ReplaceRules)
        {
            _replaceRules.Add(rule);
        }

        _blockKeywords.Clear();
        foreach (var keyword in _settings.BlockKeywords)
        {
            _blockKeywords.Add(keyword);
        }

        ColorRuleListBox.ItemsSource = _colorRules;
        ReplaceRuleListBox.ItemsSource = _replaceRules;
        BlockKeywordListBox.ItemsSource = _blockKeywords;
    }

    private void LoadUiFromSettings()
    {
        LogPathTextBox.Text = _settings.LogPath;
        LogEncodingComboBox.Text = _settings.LogEncoding;
        OverlayWidthTextBox.Text = _settings.OverlayWidth.ToString("0.#");
        OpacitySlider.Value = Math.Clamp(_settings.OverlayOpacity, 0.1, 1.0);
        BackgroundOpacitySlider.Value = Math.Clamp(_settings.BackgroundOpacity, 0.1, 1.0);
        OverlayMaxHeightTextBox.Text = _settings.OverlayMaxHeight.ToString("0.#");
        WrapLengthTextBox.Text = _settings.WrapLength.ToString();
        MaxMessagesTextBox.Text = _settings.MaxMessages.ToString();
        ClickThroughCheckBox.IsChecked = _settings.ClickThrough;
        ShowTimestampCheckBox.IsChecked = _settings.ShowTimestamp;
        ShadowCheckBox.IsChecked = _settings.TextShadow;
        var fontItem = _fontItems.FirstOrDefault(x => string.Equals(x.Source, _settings.FontFamily, StringComparison.OrdinalIgnoreCase));
        if (fontItem != null)
        {
            FontFamilyComboBox.SelectedItem = fontItem;
        }
        else
        {
            FontFamilyComboBox.Text = _settings.FontFamily;
        }

        FontSizeTextBox.Text = _settings.FontSize.ToString("0.#");
        FontWeightComboBox.Text = _settings.FontWeight;
        TextColorPreview.Background = ParseBrush(_settings.TextColor, Brushes.White);
        BackgroundColorPreview.Background = ParseBrush(_settings.BackgroundColor, new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
        PlayerContentColorPreview.Background = string.IsNullOrWhiteSpace(_settings.PlayerContentColor)
            ? Brushes.Transparent
            : ParseBrush(_settings.PlayerContentColor, Brushes.White);
        PlayerNameColorPreview.Background = string.IsNullOrWhiteSpace(_settings.PlayerNameColor)
            ? Brushes.Transparent
            : ParseBrush(_settings.PlayerNameColor, Brushes.White);

        EnableDebugLogCheckBox.IsChecked = _settings.EnableDebugLog;
        EnableTextSelectionCheckBox.IsChecked = _settings.EnableTextSelection;
        MergeDuplicateMessagesCheckBox.IsChecked = _settings.MergeDuplicateMessages;
        EnableAutoGgCheckBox.IsChecked = _settings.EnableAutoGg;
        AutoGgTriggerTextBox.Text = _settings.AutoGgTriggerPattern;
        AutoGgChatKeyTextBox.Text = _settings.AutoGgChatKey;
        AutoGgTextTextBox.Text = _settings.AutoGgText;
        AutoGgUseClipboardCheckBox.IsChecked = _settings.AutoGgUseClipboard;
        UpdateDebugLogVisibility();
    }

    private void SubscribeImmediateApply()
    {
        OpacitySlider.ValueChanged += (_, _) => SaveSettingsFromUi(false);
        BackgroundOpacitySlider.ValueChanged += (_, _) => SaveSettingsFromUi(false);
        ClickThroughCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        ClickThroughCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        ShowTimestampCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        ShowTimestampCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        ShadowCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        ShadowCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        EnableTextSelectionCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        EnableTextSelectionCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        MergeDuplicateMessagesCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        MergeDuplicateMessagesCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        EnableAutoGgCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        EnableAutoGgCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        AutoGgTriggerTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        AutoGgChatKeyTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        AutoGgTextTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        AutoGgUseClipboardCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        AutoGgUseClipboardCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);

        LogPathTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        OverlayWidthTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        OverlayMaxHeightTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        WrapLengthTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        MaxMessagesTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        FontFamilyComboBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        FontSizeTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        FontWeightComboBox.SelectionChanged += (_, _) => SaveSettingsFromUi(false);
        FontWeightComboBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        LogEncodingComboBox.SelectionChanged += (_, _) => SaveSettingsFromUi(false);
        LogEncodingComboBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        FontFamilyComboBox.SelectionChanged += (_, _) => SaveSettingsFromUi(false);
    }

    private void SaveSettingsFromUi(bool log = true)
    {
        if (_loading)
        {
            return;
        }

        _settings.LogPath = LogPathTextBox.Text.Trim();
        var logEncoding = string.IsNullOrWhiteSpace(LogEncodingComboBox.Text) ? "Auto" : LogEncodingComboBox.Text.Trim();
        var logEncodingChanged = !string.Equals(_settings.LogEncoding, logEncoding, StringComparison.OrdinalIgnoreCase);
        _settings.LogEncoding = logEncoding;
        _settings.OverlayWidth = ParseDouble(OverlayWidthTextBox.Text, 420, 120, 2000);
        _settings.OverlayOpacity = OpacitySlider.Value;
        _settings.BackgroundOpacity = BackgroundOpacitySlider.Value;
        _settings.OverlayMaxHeight = ParseDouble(OverlayMaxHeightTextBox.Text, 620, 100, 2000);
        _settings.WrapLength = ParseInt(WrapLengthTextBox.Text, 40, 5, 2000);
        _settings.MaxMessages = ParseInt(MaxMessagesTextBox.Text, 200, 1, 2000);
        _settings.ClickThrough = ClickThroughCheckBox.IsChecked == true;
        _settings.ShowTimestamp = ShowTimestampCheckBox.IsChecked == true;
        _settings.TextShadow = ShadowCheckBox.IsChecked == true;
        if (FontFamilyComboBox.SelectedItem is FontItem selectedFont)
        {
            _settings.FontFamily = selectedFont.Source;
        }
        else
        {
            _settings.FontFamily = string.IsNullOrWhiteSpace(FontFamilyComboBox.Text) ? "Microsoft YaHei UI" : FontFamilyComboBox.Text.Trim();
        }
        _settings.FontSize = ParseDouble(FontSizeTextBox.Text, 16, 8, 96);
        _settings.FontWeight = string.IsNullOrWhiteSpace(FontWeightComboBox.Text) ? "Normal" : FontWeightComboBox.Text.Trim();
        _settings.EnableDebugLog = EnableDebugLogCheckBox.IsChecked == true;
        _settings.EnableTextSelection = EnableTextSelectionCheckBox.IsChecked == true;
        _settings.MergeDuplicateMessages = MergeDuplicateMessagesCheckBox.IsChecked == true;
        _settings.EnableAutoGg = EnableAutoGgCheckBox.IsChecked == true;
        _settings.AutoGgTriggerPattern = AutoGgTriggerTextBox.Text.Trim();
        _settings.AutoGgChatKey = AutoGgChatKeyTextBox.Text.Trim();
        _settings.AutoGgText = AutoGgTextTextBox.Text;
        _settings.AutoGgUseClipboard = AutoGgUseClipboardCheckBox.IsChecked == true;
        _settings.ColorRules = _colorRules.ToList();
        _settings.ReplaceRules = _replaceRules.ToList();
        _settings.BlockKeywords = _blockKeywords.ToList();

        SettingsService.Save(_settings);
        _overlay?.ApplySettings();

        // 监听中切换编码时立即对后续日志生效，不需要停止再启动。
        if (logEncodingChanged && _watcher.IsRunning)
        {
            _watcher.UpdateEncoding(_settings.LogEncoding);
            _overlay?.ClearMessages();
            AppendDebugLog("[编码] 已切换为 " + _settings.LogEncoding + "，已清空悬浮窗旧消息");
        }

        if (log)
        {
            LogStatus("配置已保存");
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Minecraft 日志文件",
            Filter = "Minecraft 日志 (*.log)|*.log|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            LogPathTextBox.Text = dialog.FileName;
            if (_watcher.IsRunning)
            {
                // 监听中重新选择文件时，切换到新路径继续监听。
                StopListening();
                StartListening();
            }
            else
            {
                SaveSettingsFromUi();
            }
        }
    }

    private void ToggleListenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_watcher.IsRunning)
        {
            StopListening();
        }
        else
        {
            StartListening();
        }
    }

    private void StartListening()
    {
        SaveSettingsFromUi();
        if (string.IsNullOrWhiteSpace(_settings.LogPath))
        {
            MessageBox.Show(this, "请先选择 Minecraft 日志文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _watcher.ChatLineReceived += Watcher_ChatLineReceived;
        _watcher.StatusChanged += Watcher_StatusChanged;
        _watcher.DebugLineReceived += Watcher_DebugLineReceived;
        _watcher.Start(_settings.LogPath, _settings.LogEncoding);
        ToggleListenButton.Content = "停止监听";
        ShowOverlay();
        LogStatus("开始监听：" + _settings.LogPath);
        AppendDebugLog("== 开始监听 ==");
        AppendDebugLog("日志文件: " + _settings.LogPath);
        AppendDebugLog("日志编码: " + _settings.LogEncoding);
    }

    private void StopListening()
    {
        _watcher.ChatLineReceived -= Watcher_ChatLineReceived;
        _watcher.StatusChanged -= Watcher_StatusChanged;
        _watcher.DebugLineReceived -= Watcher_DebugLineReceived;
        _watcher.Stop();
        ToggleListenButton.Content = "开始监听";
        LogStatus("已停止监听");
        AppendDebugLog("== 已停止监听 ==");
    }

    private void Watcher_ChatLineReceived(string chatMessage)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // 屏蔽判断基于替换后的文本，但悬浮窗保留原始文本，
            // 这样之后修改替换规则时，已经显示的消息也能按新规则重新渲染。
            var replacedForCheck = ChatTextProcessor.ApplyReplacements(chatMessage, _settings.ReplaceRules);
            if (ChatTextProcessor.IsBlocked(replacedForCheck, _settings.BlockKeywords))
            {
                AppendDebugLog("[屏蔽] 已屏蔽，不显示到悬浮窗：" + replacedForCheck);
                return;
            }

            TryAutoGg(chatMessage);
            ShowOverlay();
            _overlay?.AddMessage(chatMessage);
        });
    }

    private void TryAutoGg(string message)
    {
        if (!_settings.EnableAutoGg || string.IsNullOrWhiteSpace(_settings.AutoGgTriggerPattern))
        {
            return;
        }

        // 防止同一条/短时间内重复触发导致反复输入发送。
        if (_autoGgSending)
        {
            return;
        }

        if ((DateTime.Now - _lastAutoGgAt).TotalSeconds < 5)
        {
            return;
        }

        try
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    message,
                    _settings.AutoGgTriggerPattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return;
            }
        }
        catch (ArgumentException)
        {
            AppendDebugLog("[自动GG] 触发正则不合法，已跳过");
            return;
        }

        _autoGgSending = true;
        _lastAutoGgAt = DateTime.Now;
        _ = SendAutoGgAsync();
    }

    private async Task SendAutoGgAsync()
    {
        string? oldClipboardText = null;
        var useClipboardPaste = false;

        try
        {
            AppendDebugLog("[自动GG] 检测到胜利消息，准备发送 gg...");
            await Task.Delay(200);

            // 如果启用剪贴板粘贴，先把要发送的文字放到剪贴板。
            if (_settings.AutoGgUseClipboard)
            {
                try
                {
                    oldClipboardText = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                    Clipboard.SetText(_settings.AutoGgText);
                    useClipboardPaste = true;
                    AppendDebugLog("[自动GG] 已使用剪贴板模式，避免中文输入法干扰");
                }
                catch
                {
                    useClipboardPaste = false;
                    AppendDebugLog("[自动GG] 剪贴板暂不可用，自动改用直接输入模式");
                }
            }

            var chatKey = _settings.AutoGgChatKey.Trim();
            if (string.Equals(chatKey, "enter", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(chatKey, "回车", StringComparison.Ordinal))
            {
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            }
            else if (!string.IsNullOrEmpty(chatKey))
            {
                System.Windows.Forms.SendKeys.SendWait(chatKey);
            }

            await Task.Delay(250);

            if (useClipboardPaste)
            {
                // Ctrl+V 粘贴，不经过中文输入法，不会把 gg 变成拼音。
                System.Windows.Forms.SendKeys.SendWait("^v");
            }
            else
            {
                System.Windows.Forms.SendKeys.SendWait(_settings.AutoGgText);
            }

            await Task.Delay(100);
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            AppendDebugLog("[自动GG] 已发送：" + _settings.AutoGgText);
        }
        catch (Exception ex)
        {
            AppendDebugLog("[自动GG] 发送失败：" + ex.Message);
        }
        finally
        {
            // 尽量恢复用户原来的剪贴板内容。
            if (useClipboardPaste)
            {
                try
                {
                    if (oldClipboardText != null)
                    {
                        Clipboard.SetText(oldClipboardText);
                    }
                    else
                    {
                        Clipboard.Clear();
                    }
                }
                catch
                {
                    // 恢复失败不影响游戏内发送。
                }
            }

            _autoGgSending = false;
        }
    }

    private void Watcher_StatusChanged(string status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            LogStatus(status);
            AppendDebugLog("[状态] " + status);
        });
    }

    private void Watcher_DebugLineReceived(string decodedLine, string hex)
    {
        Dispatcher.InvokeAsync(() =>
        {
            AppendDebugLog("[行] " + decodedLine);
            AppendDebugLog("[HEX] " + hex);
        });
    }

    private void ShowOverlay()
    {
        if (_overlay == null)
        {
            _overlay = new OverlayWindow(_settings);
            _overlay.Closed += (_, _) =>
            {
                _overlay = null;
                ToggleOverlayButton.Content = "显示悬浮窗";
            };
        }

        if (!_overlay.IsVisible)
        {
            _overlay.Show();
        }

        ToggleOverlayButton.Content = "隐藏悬浮窗";
    }

    private void HideOverlay()
    {
        _overlay?.Hide();
        ToggleOverlayButton.Content = "显示悬浮窗";
    }

    private void ToggleOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay?.IsVisible == true)
        {
            HideOverlay();
        }
        else
        {
            ShowOverlay();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _overlay?.ClearMessages();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
    }

    private void EnableDebugLogCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDebugLogVisibility();
        SaveSettingsFromUi(false);
    }

    private void UpdateDebugLogVisibility()
    {
        DebugLogGroup.Visibility = EnableDebugLogCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyDebugLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DebugLogTextBox.Text);
            LogStatus("调试日志已复制到剪贴板");
        }
        catch
        {
            LogStatus("复制失败");
        }
    }

    private void ClearDebugLogButton_Click(object sender, RoutedEventArgs e)
    {
        DebugLogTextBox.Clear();
    }

    private void AppendDebugLog(string line)
    {
        if (!_settings.EnableDebugLog)
        {
            return;
        }

        DebugLogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        DebugLogTextBox.ScrollToEnd();
    }

    // ---------- 规则导入/导出 ----------

    private void ExportRulesButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi(false);
        var dialog = new SaveFileDialog
        {
            Title = "导出文本处理规则",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = "minecraft-chat-text-rules.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var export = new TextRuleExport
        {
            ColorRules = _settings.ColorRules.ToList(),
            ReplaceRules = _settings.ReplaceRules.ToList(),
            BlockKeywords = _settings.BlockKeywords.ToList()
        };

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
            LogStatus("规则已导出：" + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportRulesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入文本处理规则",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var export = JsonSerializer.Deserialize<TextRuleExport>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (export == null)
            {
                MessageBox.Show(this, "文件内容为空或格式不正确。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.ColorRules = export.ColorRules ?? new List<TextColorRule>();
            _settings.ReplaceRules = export.ReplaceRules ?? new List<TextReplaceRule>();
            _settings.BlockKeywords = export.BlockKeywords ?? new List<string>();
            _colorRules.Clear();
            foreach (var rule in _settings.ColorRules)
            {
                _colorRules.Add(rule);
            }

            _replaceRules.Clear();
            foreach (var rule in _settings.ReplaceRules)
            {
                _replaceRules.Add(rule);
            }

            _blockKeywords.Clear();
            foreach (var keyword in _settings.BlockKeywords)
            {
                _blockKeywords.Add(keyword);
            }

            SaveSettingsFromUi(false);
            LogStatus("规则已导入：" + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导入失败：" + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RegexTutorialButton_Click(object sender, RoutedEventArgs e)
    {
        const string tutorial =
            "正则匹配简单教程\n\n" +
            "1. 普通文字直接填写，例如：红队\n" +
            "2. \\d 表示数字，\\d+ 表示一个或多个数字\n" +
            "   例：游戏还有 (\\d+) 秒开始\n" +
            "3. .+? 表示任意内容（尽量短）\n" +
            "   例：玩家 (.+?) 退出了游戏！\n" +
            "4. 括号 () 用于捕获内容\n" +
            "   - 颜色规则“高亮第几组填 1”只染第一个括号里的内容\n" +
            "   - 替换规则里可用 $1 引用第一个括号内容\n" +
            "5. 想同时给整句上色，在颜色规则里设置“整句颜色”\n\n" +
            "示例：让“游戏还有 X 秒开始”中的 X 变浅红、其他字变金色\n" +
            "正则：游戏还有 (\\d+) 秒开始\n" +
            "高亮组：1\n" +
            "颜色：浅红\n" +
            "整句颜色：金色";
        MessageBox.Show(this, tutorial, "正则教程", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- 颜色选择 ----------

    private void TextColorButton_Click(object sender, RoutedEventArgs e)
    {
        var hex = PickColorHex(_settings.TextColor);
        if (hex == null)
        {
            return;
        }

        _settings.TextColor = hex;
        TextColorPreview.Background = ParseBrush(hex, Brushes.White);
        SaveSettingsFromUi(false);
    }

    private void BackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        var hex = PickColorHex(_settings.BackgroundColor);
        if (hex == null)
        {
            return;
        }

        _settings.BackgroundColor = hex;
        BackgroundColorPreview.Background = ParseBrush(hex, new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
        SaveSettingsFromUi(false);
    }

    private void PlayerContentColorButton_Click(object sender, RoutedEventArgs e)
    {
        var current = string.IsNullOrWhiteSpace(_settings.PlayerContentColor) ? "#FFFFFF" : _settings.PlayerContentColor;
        var hex = PickColorHex(current);
        if (hex == null)
        {
            return;
        }

        _settings.PlayerContentColor = hex;
        PlayerContentColorPreview.Background = ParseBrush(hex, Brushes.White);
        SaveSettingsFromUi(false);
    }

    private void ClearPlayerContentColorButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PlayerContentColor = "";
        PlayerContentColorPreview.Background = Brushes.Transparent;
        SaveSettingsFromUi(false);
    }

    private void PlayerNameColorButton_Click(object sender, RoutedEventArgs e)
    {
        var current = string.IsNullOrWhiteSpace(_settings.PlayerNameColor) ? "#FFFFFF" : _settings.PlayerNameColor;
        var hex = PickColorHex(current);
        if (hex == null)
        {
            return;
        }

        _settings.PlayerNameColor = hex;
        PlayerNameColorPreview.Background = ParseBrush(hex, Brushes.White);
        SaveSettingsFromUi(false);
    }

    private void ClearPlayerNameColorButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PlayerNameColor = "";
        PlayerNameColorPreview.Background = Brushes.Transparent;
        SaveSettingsFromUi(false);
    }

    private string? PickColorHex(string currentHex)
    {
        try
        {
            var currentColor = ParseColor(currentHex, Colors.White);
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(currentColor.A, currentColor.R, currentColor.G, currentColor.B)
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                return null;
            }

            var c = dialog.Color;
            // WinForms 调色盘不提供 Alpha，保留原颜色透明度，避免背景变得完全不透明。
            var alpha = currentColor.A;
            var mediaColor = Color.FromArgb(alpha, c.R, c.G, c.B);
            return $"#{mediaColor.A:X2}{mediaColor.R:X2}{mediaColor.G:X2}{mediaColor.B:X2}";
        }
        catch
        {
            return null;
        }
    }

    // ---------- 文本彩色渲染规则 ----------

    private void ColorRuleListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ColorRuleListBox.SelectedItem is not TextColorRule rule)
        {
            return;
        }

        ColorRuleTextTextBox.Text = rule.Text;
        _colorRuleColor = rule.Color;
        ColorRuleColorPreview.Background = ParseBrush(rule.Color, Brushes.Red);
        ColorRuleWeightComboBox.Text = rule.FontWeight;
        ColorRuleRegexCheckBox.IsChecked = rule.UseRegex;
        ColorRuleRegexGroupTextBox.Text = rule.RegexGroup.ToString();
        _colorRuleMatchColor = rule.MatchColor ?? "";
        ColorRuleMatchColorPreview.Background = string.IsNullOrWhiteSpace(_colorRuleMatchColor)
            ? Brushes.Transparent
            : ParseBrush(_colorRuleMatchColor, Brushes.Gold);
    }

    private void ChooseColorRuleColorButton_Click(object sender, RoutedEventArgs e)
    {
        var hex = PickColorHex(_colorRuleColor);
        if (hex == null)
        {
            return;
        }

        _colorRuleColor = hex;
        ColorRuleColorPreview.Background = ParseBrush(hex, Brushes.Red);
    }

    private void ChooseColorRuleMatchColorButton_Click(object sender, RoutedEventArgs e)
    {
        var current = string.IsNullOrWhiteSpace(_colorRuleMatchColor) ? "#FFD700" : _colorRuleMatchColor;
        var hex = PickColorHex(current);
        if (hex == null)
        {
            return;
        }

        _colorRuleMatchColor = hex;
        ColorRuleMatchColorPreview.Background = ParseBrush(hex, Brushes.Gold);
    }

    private void ClearColorRuleMatchColorButton_Click(object sender, RoutedEventArgs e)
    {
        _colorRuleMatchColor = "";
        ColorRuleMatchColorPreview.Background = Brushes.Transparent;
    }

    private void AddColorRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var text = ColorRuleTextTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            MessageBox.Show(this, "请输入需要染色的文字。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rule = new TextColorRule
        {
            Text = text,
            Color = _colorRuleColor,
            FontWeight = string.IsNullOrWhiteSpace(ColorRuleWeightComboBox.Text) ? "Light" : ColorRuleWeightComboBox.Text.Trim(),
            UseRegex = ColorRuleRegexCheckBox.IsChecked == true,
            RegexGroup = ParseInt(ColorRuleRegexGroupTextBox.Text, 0, 0, 100),
            MatchColor = _colorRuleMatchColor
        };
        _colorRules.Add(rule);
        ColorRuleListBox.SelectedItem = rule;
        SaveSettingsFromUi(false);
    }

    private void UpdateColorRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ColorRuleListBox.SelectedItem is not TextColorRule rule)
        {
            MessageBox.Show(this, "请先在列表中选中一条规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        rule.Text = ColorRuleTextTextBox.Text.Trim();
        rule.Color = _colorRuleColor;
        rule.FontWeight = string.IsNullOrWhiteSpace(ColorRuleWeightComboBox.Text) ? "Light" : ColorRuleWeightComboBox.Text.Trim();
        rule.UseRegex = ColorRuleRegexCheckBox.IsChecked == true;
        rule.RegexGroup = ParseInt(ColorRuleRegexGroupTextBox.Text, 0, 0, 100);
        rule.MatchColor = _colorRuleMatchColor;
        ColorRuleListBox.Items.Refresh();
        SaveSettingsFromUi(false);
    }

    private void DeleteColorRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ColorRuleListBox.SelectedItem is TextColorRule rule)
        {
            _colorRules.Remove(rule);
            SaveSettingsFromUi(false);
        }
    }

    // ---------- 文本替换 ----------

    private void ReplaceRuleListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReplaceRuleListBox.SelectedItem is not TextReplaceRule rule)
        {
            return;
        }

        ReplaceFindTextBox.Text = rule.FindText;
        ReplaceWithTextBox.Text = rule.ReplaceText;
        ReplaceOnlyPlayerContentCheckBox.IsChecked = rule.OnlyPlayerContent;
        ReplaceRegexCheckBox.IsChecked = rule.UseRegex;
    }

    private void AddReplaceRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var find = ReplaceFindTextBox.Text;
        if (string.IsNullOrEmpty(find))
        {
            MessageBox.Show(this, "请输入需要查找的文字。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rule = new TextReplaceRule
        {
            FindText = find,
            ReplaceText = ReplaceWithTextBox.Text ?? string.Empty,
            OnlyPlayerContent = ReplaceOnlyPlayerContentCheckBox.IsChecked == true,
            UseRegex = ReplaceRegexCheckBox.IsChecked == true
        };
        _replaceRules.Add(rule);
        ReplaceRuleListBox.SelectedItem = rule;
        SaveSettingsFromUi(false);
    }

    private void UpdateReplaceRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReplaceRuleListBox.SelectedItem is not TextReplaceRule rule)
        {
            MessageBox.Show(this, "请先在列表中选中一条替换规则。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        rule.FindText = ReplaceFindTextBox.Text;
        rule.ReplaceText = ReplaceWithTextBox.Text ?? string.Empty;
        rule.OnlyPlayerContent = ReplaceOnlyPlayerContentCheckBox.IsChecked == true;
        rule.UseRegex = ReplaceRegexCheckBox.IsChecked == true;
        ReplaceRuleListBox.Items.Refresh();
        SaveSettingsFromUi(false);
    }

    private void DeleteReplaceRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReplaceRuleListBox.SelectedItem is TextReplaceRule rule)
        {
            _replaceRules.Remove(rule);
            SaveSettingsFromUi(false);
        }
    }

    // ---------- 屏蔽关键词 ----------

    private void AddBlockKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        var keyword = BlockKeywordTextBox.Text.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return;
        }

        _blockKeywords.Add(keyword);
        BlockKeywordTextBox.Clear();
        SaveSettingsFromUi(false);
    }

    private void DeleteBlockKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockKeywordListBox.SelectedItem is string keyword)
        {
            _blockKeywords.Remove(keyword);
            SaveSettingsFromUi(false);
        }
    }

    // ---------- 其它 ----------

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSettingsFromUi(false);
        StopListening();
        _overlay?.Close();
    }

    private void LogStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private static double ParseDouble(string? text, double defaultValue, double min, double max)
    {
        if (!double.TryParse(text, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static int ParseInt(string? text, int defaultValue, int min, int max)
    {
        if (!int.TryParse(text, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
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

    private static Color ParseColor(string? value, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var color = ColorConverter.ConvertFromString(value) as Color?;
                if (color.HasValue)
                {
                    return color.Value;
                }
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static string? GetLocalizedFontName(FontFamily family)
    {
        foreach (var pair in family.FamilyNames)
        {
            var tag = pair.Key.IetfLanguageTag;
            if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private sealed class FontItem
    {
        public string Display { get; }

        public string Source { get; }

        public FontItem(string display, string source)
        {
            Display = display;
            Source = source;
        }
    }
}
