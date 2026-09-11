using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Services;
using System.Windows.Media.Effects;  // 提供 DropShadowEffect

namespace MinecraftChatOverlay;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MinecraftLogWatcher _watcher = new();
    private readonly ObservableCollection<TextColorRule> _colorRules = new();
    private readonly ObservableCollection<TextReplaceRule> _replaceRules = new();
    private readonly ObservableCollection<BlockKeywordItem> _blockKeywords = new();
    private readonly ObservableCollection<PlayerQueryField> _playerQueryFields = new();

    // 上一次成功查询的返回数据。可以为空（还没查过）—— 此时字段选择器只给内置字段库。
    // JsonElement 在服务里已经 Clone 过，和原 JsonDocument 解耦，可以安全长期持有。
    private JsonElement? _lastPlayerQueryRoot;
    private List<PlayerQueryFieldCandidate> _fieldPickerCandidates = new();

    // 字段列表拖拽排序
    private Point _fieldDragOrigin;
    private PlayerQueryField? _draggedField;
    private int _draggedIndex = -1;

    /// <summary>空位插在"把被拖行拿掉之后"的序列的第几个位置。</summary>
    private int _gapIndex = -1;

    // 拖到列表顶部/底部时自动滚动（Win32 定时器：DoDragDrop 模态循环里也能触发）
    private IntPtr _fieldAutoScrollTimerId = IntPtr.Zero;
    private TimerProc? _fieldAutoScrollTimerProc;
    private double _fieldAutoScrollDelta;

    private delegate void TimerProc(IntPtr hWnd, uint uMsg, IntPtr nIDEvent, uint dwTime);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, TimerProc lpTimerFunc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    private readonly List<FontItem> _fontItems = new();
    private OverlayWindow? _overlay;
    private bool _loading = true;
    private string _colorRuleColor = "#FFFF0000";
    private string _colorRuleMatchColor = "";
    private bool _autoGgSending;
    private DateTime _lastAutoGgAt = DateTime.MinValue;
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private HookProc? _keyboardHookProc;
    private readonly HashSet<int> _keysDownBeforeBlock = new();
    private readonly MediaPlayer _cialloPlayer = new();
    private bool _cialloBusy;
    private static readonly Dictionary<string, string> PlayerStatLabels = new()
    {
        ["name"] = "玩家",
        ["total_game"] = "总场次",
        ["total_win"] = "总胜场",
        ["total_lose"] = "总败场",
        ["total_fk"] = "总最终击杀",
        ["total_kills"] = "总击杀",
        ["total_deaths"] = "总死亡",
        ["total_bed_destroy"] = "总拆床",
        ["win"] = "胜场",
        ["lose"] = "败场",
        ["kills"] = "击杀",
        ["deaths"] = "死亡",
        ["final_kills"] = "最终击杀",
        ["bed_destory"] = "拆床",
        ["bed_destroy"] = "拆床",
        ["exp"] = "经验",
        ["level"] = "等级",
        ["rank"] = "段位",
        ["online"] = "在线",
        ["last_seen"] = "最后上线"
    };

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowIcon();
        LoadBrandAssets();
        _settings = SettingsService.Load();
        InitializeComboBoxes();
        InitializePlayerQueryUi();
        LoadRuleCollections();
        LoadUiFromSettings();

        // 先把字段下拉填好，否则第一次打开"玩家查询"时下拉是空的
        RebuildFieldPickerOptions();

        _loading = false;
        SubscribeImmediateApply();
        Loaded += MainWindow_Loaded;
        LogStatus(Copy.ConfigLoaded + SettingsService.ConfigPath);
    }

private void MainWindow_Loaded(object sender, RoutedEventArgs e)
{
    MoveIndicatorToSelected();
    
    // 加载主题
    _isDarkMode = _settings.IsDarkMode;
    ApplyTheme(_isDarkMode);
    UpdateListeningIndicator(_watcher.IsRunning);
    
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
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath, UriKind.Absolute));
            }
        }
        catch { }
    }

    private void LoadBrandAssets()
    {
        try
        {
            var logoPath = ResolveAssetPath("LOGO256x.png");
            if (File.Exists(logoPath))
            {
                TitleLogoImage.Source = new BitmapImage(new Uri(logoPath, UriKind.Absolute));
            }
        }
        catch
        {
        }

        try
        {
            var imagePath = ResolveAssetPath("res", "ciallo.png");
            if (File.Exists(imagePath))
            {
                CialloImage.Source = new BitmapImage(new Uri(imagePath, UriKind.Absolute));
            }
        }
        catch
        {
        }

        try
        {
            // 优先使用 mp3：Windows 的 MediaPlayer 对 mp3 支持最稳定，ogg 可能因缺少解码器无声。
            var audioPath = ResolveAssetPath("res", "ciallo.mp3");
            if (!File.Exists(audioPath))
            {
                audioPath = ResolveAssetPath("res", "ciallo.ogg");
            }
            if (!File.Exists(audioPath))
            {
                audioPath = ResolveAssetPath("ciallo.mp3");
            }

            if (File.Exists(audioPath))
            {
                _cialloPlayer.Volume = 1.0;
                _cialloPlayer.Open(new Uri(audioPath, UriKind.Absolute));
            }
        }
        catch
        {
        }
    }

    private static string ResolveAssetPath(params string[] relativeParts)
    {
        var relativePath = Path.Combine(relativeParts);
        var basePath = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (File.Exists(basePath))
        {
            return basePath;
        }

        var currentPath = Path.Combine(Environment.CurrentDirectory, relativePath);
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        // 开发/Vs 调试时，工作目录或输出目录可能不是项目根目录；向上查找一层，
        // 保证 LOGO256x.png、res/ciallo.* 能被找到。
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(startDirectory);
            for (var i = 0; i < 6 && directory != null; i++, directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return basePath;
    }

    private void InitializeComboBoxes()
    {
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
        ColorRuleWeightComboBox.SelectedItem = "Light";
    }

    private void ComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox &&
            comboBox.Template.FindName("PART_Popup", comboBox) is System.Windows.Controls.Primitives.Popup popup)
        {
            popup.PlacementTarget = comboBox;
            popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
    }

    private void InitializePlayerQueryUi()
    {
        PlayerQueryGameTypeComboBox.Items.Add("起床战争");
        PlayerQueryGameTypeComboBox.Items.Add("空岛战争");
        PlayerQueryGameTypeComboBox.SelectedIndex = 0;
        UpdatePlayerQueryModeOptions();
        PlayerQueryFieldItemsControl.ItemsSource = _playerQueryFields;


    }

    private void PlayerQueryGameTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RebuildFieldPickerOptions();

        UpdatePlayerQueryModeOptions();
        if (!_loading)
        {
            SavePlayerQuerySettings(false);
        }
    }

    private void UpdatePlayerQueryModeOptions()
    {
        if (PlayerQueryModeComboBox == null)
        {
            return;
        }

        var selected = PlayerQueryModeComboBox.SelectedItem as string;
        PlayerQueryModeComboBox.Items.Clear();

        if (PlayerQueryGameTypeComboBox.SelectedIndex == 1)
        {
            PlayerQueryModeComboBox.Items.Add("总览");
            PlayerQueryModeComboBox.Items.Add("Solo（sw1）");
            PlayerQueryModeComboBox.Items.Add("双人（sw2）");
        }
        else
        {
            PlayerQueryModeComboBox.Items.Add("总览");
            PlayerQueryModeComboBox.Items.Add("Solo（bw1）");
            PlayerQueryModeComboBox.Items.Add("2v2（bw8）");
            PlayerQueryModeComboBox.Items.Add("4v4（bw16）");
        }

        var index = selected == null ? 0 : PlayerQueryModeComboBox.Items.IndexOf(selected);
        PlayerQueryModeComboBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private async void PlayerQueryButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = PlayerQueryKeyPasswordBox.Password.Trim();
        var playerId = PlayerQueryIdTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            PlayerQueryStatusText.Text = Copy.NeedApiKey;
            ShowToast(Copy.NeedApiKey);
            return;
        }

        if (string.IsNullOrWhiteSpace(playerId))
        {
            PlayerQueryStatusText.Text = Copy.NeedPlayerId;
            ShowToast(Copy.NeedPlayerId);
            return;
        }

        var gametype = PlayerQueryGameTypeComboBox.SelectedIndex == 1 ? "skywars" : "bedwars";
        var modeKey = GetPlayerQueryModeKey(gametype);
        var fields = _playerQueryFields
            .Where(field => !string.IsNullOrWhiteSpace(field.Path))
            .Select(field => field.Clone())
            .ToList();

        if (fields.Count == 0)
        {
            PlayerQueryStatusText.Text = Copy.NeedQueryField;
            ShowToast(Copy.NeedQueryField);
            return;
        }

        CapturePlayerQuerySettings();
        PlayerQueryButton.IsEnabled = false;
        PlayerQueryButton.Content = Copy.QueryingButton;
        PlayerQueryStatusText.Text = Copy.Querying;
        ClearPlayerQueryResultPanel();

        try
        {
            var result = await BuJiDaoQueryService.QueryPlayerAsync(apiKey, playerId, gametype, "all");
            if (!result.Success)
            {
                PlayerQueryStatusText.Text = Copy.QueryFailedShort;
                ShowPlayerQueryError(result.Error);
                ShowToast(Copy.QueryFailed + result.Error);
                return;
            }

            // 留下这次的数据，供"从数据里选字段"枚举可用路径。
            // 放在字段匹配检查之前是有意的：即使当前字段一个都没匹配上，用户也能从真实数据里挑。
            _lastPlayerQueryRoot = result.Root;

            // 查过之后下拉里会多出"本次查询发现"的字段
            RebuildFieldPickerOptions();

            var stats = BuildPlayerQueryStats(result.Root, gametype, modeKey, fields);
            if (stats.Count == 0)
            {
                PlayerQueryStatusText.Text = Copy.NoDisplayData;
                ShowPlayerQueryError(Copy.NoMatchingFieldData);
                return;
            }

            PlayerQueryHintText.Text = Copy.QueryTarget(
                playerId,
                PlayerQueryGameTypeComboBox.SelectedItem?.ToString() ?? "",
                PlayerQueryModeComboBox.SelectedItem?.ToString() ?? "");
            RenderPlayerStats(stats);
            PlayerQueryStatusText.Text = Copy.QueryDoneWithCount(stats.Count);
            ShowToast(Copy.QueryDone);
        }
        catch (Exception ex)
        {
            PlayerQueryStatusText.Text = "查询异常";
            ShowPlayerQueryError(ex.Message);
            ShowToast(Copy.QueryError + ex.Message);
        }
        finally
        {
            PlayerQueryButton.IsEnabled = true;
            PlayerQueryButton.Content = Copy.QueryStartButton;
        }
    }

    private void ClearPlayerQueryButton_Click(object sender, RoutedEventArgs e)
    {
        ClearPlayerQueryResultPanel();
        PlayerQueryStatusText.Text = Copy.QueryCleared;
        PlayerQueryHintText.Text = Copy.QueryIdleHint;
    }

    private void AddPlayerQueryFieldButton_Click(object sender, RoutedEventArgs e)
    {
        _playerQueryFields.Add(new PlayerQueryField { Label = "新字段", Path = "" });
        SavePlayerQuerySettings(false);
    }

    private void DeletePlayerQueryFieldButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PlayerQueryField field })
        {
            _playerQueryFields.Remove(field);
            SavePlayerQuerySettings(false);
            RebuildFieldPickerOptions();
        }
    }

    private void PlayerQueryFieldTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SavePlayerQuerySettings(false);
    }

    private void ApplyBedwarsPlayerQueryPresetButton_Click(object sender, RoutedEventArgs e)
    {
        PlayerQueryGameTypeComboBox.SelectedIndex = 0;
        ApplyPlayerQueryPreset("bedwars");
        SavePlayerQuerySettings(false);
    }

    private void ApplySkywarsPlayerQueryPresetButton_Click(object sender, RoutedEventArgs e)
    {
        PlayerQueryGameTypeComboBox.SelectedIndex = 1;
        ApplyPlayerQueryPreset("skywars");
        SavePlayerQuerySettings(false);
    }

    private void ApplyPlayerQueryPreset(string gametype)
    {
        _playerQueryFields.Clear();

        if (string.Equals(gametype, "skywars", StringComparison.OrdinalIgnoreCase))
        {
            AddPlayerQueryField("玩家", "data.name");
            AddPlayerQueryField("空岛总场数", "data.achievements.sw_games.counts");
            AddPlayerQueryField("空岛总胜场", "data.achievements.sw_wins.counts");
            AddPlayerQueryField("空岛总胜率", "winrate:win=data.achievements.sw_wins.counts;total=data.achievements.sw_games.counts");
            AddPlayerQueryField("当前模式胜率", "winrate:win=data.skywars.{mode}.win;lose=data.skywars.{mode}.lose");
            AddPlayerQueryField("Solo击杀", "data.skywars.sw1.kills");
            AddPlayerQueryField("Solo胜场", "data.skywars.sw1.win");
            AddPlayerQueryField("双人击杀", "data.skywars.sw2.kills");
            AddPlayerQueryField("双人胜场", "data.skywars.sw2.win");
            AddPlayerQueryField("当前模式击杀", "data.skywars.{mode}.kills");
            AddPlayerQueryField("当前模式胜场", "data.skywars.{mode}.win");
        }
        else
        {
            AddPlayerQueryField("玩家", "data.name");
            AddPlayerQueryField("总场数", "data.total_game");
            AddPlayerQueryField("总胜场", "data.total_win");
            AddPlayerQueryField("总胜率", "winrate:win=data.total_win;total=data.total_game");
            AddPlayerQueryField("总最终击杀", "data.total_fk");
            AddPlayerQueryField("总拆床", "data.total_bed_destroy");
            AddPlayerQueryField("当前模式胜率", "winrate:win=data.bedwars.{mode}.win;lose=data.bedwars.{mode}.lose");
            AddPlayerQueryField("当前模式击杀", "data.bedwars.{mode}.kills");
            AddPlayerQueryField("当前模式胜场", "data.bedwars.{mode}.win");
            AddPlayerQueryField("当前模式败场", "data.bedwars.{mode}.lose");
            AddPlayerQueryField("当前模式最终击杀", "data.bedwars.{mode}.final_kills");
            AddPlayerQueryField("当前模式拆床", "data.bedwars.{mode}.bed_destory");
        }
    }

    private void AddPlayerQueryField(string label, string path)
    {
        _playerQueryFields.Add(new PlayerQueryField { Label = label, Path = path });
    }

    private void LoadPlayerQueryUiFromSettings()
    {
        PlayerQueryRememberKeyCheckBox.IsChecked = _settings.PlayerQueryRememberKey;
        PlayerQueryKeyPasswordBox.Password = _settings.PlayerQueryRememberKey ? (_settings.PlayerQueryApiKey ?? "") : "";
        PlayerQueryIdTextBox.Text = _settings.PlayerQueryPlayerId ?? "";

        var gameIndex = string.Equals(_settings.PlayerQueryGameType, "skywars", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        PlayerQueryGameTypeComboBox.SelectedIndex = gameIndex;
        UpdatePlayerQueryModeOptions();

        var modeItem = PlayerQueryModeComboBox.Items
            .Cast<object>()
            .FirstOrDefault(item => string.Equals(item?.ToString(), _settings.PlayerQueryMode, StringComparison.OrdinalIgnoreCase));
        if (modeItem != null)
        {
            PlayerQueryModeComboBox.SelectedItem = modeItem;
        }

        _playerQueryFields.Clear();
        if (_settings.PlayerQueryFields != null)
        {
            foreach (var field in _settings.PlayerQueryFields)
            {
                _playerQueryFields.Add(field.Clone());
            }
        }

        // 第一次使用时给一套可用预设，之后完全以用户保存的字段为准。
        if (_playerQueryFields.Count == 0 && string.IsNullOrWhiteSpace(_settings.PlayerQueryApiKey))
        {
            ApplyPlayerQueryPreset(_settings.PlayerQueryGameType);
        }
    }

    private void CapturePlayerQuerySettings()
    {
        _settings.PlayerQueryRememberKey = PlayerQueryRememberKeyCheckBox.IsChecked == true;
        _settings.PlayerQueryApiKey = _settings.PlayerQueryRememberKey ? PlayerQueryKeyPasswordBox.Password : "";
        _settings.PlayerQueryPlayerId = PlayerQueryIdTextBox.Text.Trim();
        _settings.PlayerQueryGameType = PlayerQueryGameTypeComboBox.SelectedIndex == 1 ? "skywars" : "bedwars";
        _settings.PlayerQueryMode = PlayerQueryModeComboBox.SelectedItem as string ?? "总览";
        _settings.PlayerQueryFields = _playerQueryFields.Select(field => field.Clone()).ToList();
    }

    private void SavePlayerQuerySettings(bool log)
    {
        if (_loading)
        {
            return;
        }

        CapturePlayerQuerySettings();
        SettingsService.Save(_settings);

        if (log)
        {
            LogStatus(Copy.QuerySettingsSaved);
        }
    }

    // ==================== 字段列表：路径下拉 ====================
    //
    // 每一行的路径输入框是一个"可编辑下拉框"：
    //   下拉内容 = 内置字段库（任何时候都有）+ 本次查询数据里额外发现的字段（查过才有）
    //   同时保留手敲路径的能力 —— 万一接口变了用户还能自己救急
    //
    // FieldPickerOptions 是 ObservableCollection，所有行绑定到**同一个实例**，
    // 所以重新填一遍内容，界面上所有行会一起更新，不用逐行去改 ItemsSource。

    public ObservableCollection<PlayerQueryFieldCandidate> FieldPickerOptions { get; } = new();

    private void RebuildFieldPickerOptions()
    {
        var gametype = PlayerQueryGameTypeComboBox.SelectedIndex == 1 ? "skywars" : "bedwars";
        var modeKey = GetPlayerQueryModeKey(gametype);

        var existing = _playerQueryFields
            .Select(field => field.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        var options = PlayerQueryFieldCatalog.Build(_lastPlayerQueryRoot, gametype, modeKey, existing);

        // 关键：重建 ItemsSource 会让 WPF 把每个可编辑框的 Text 清空，
        // 而且顺着双向绑定把 field.Path 也写成空串 —— 用户配好的路径会被抹掉。
        // 所以先记下各行的路径，排到 WPF 处理完之后再恢复（Path 有 INPC，恢复时界面会跟着刷回来）。
        var restore = _playerQueryFields.Select(field => field.Path).ToList();

        FieldPickerOptions.Clear();
        foreach (var option in options)
        {
            FieldPickerOptions.Add(option);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            for (var i = 0; i < _playerQueryFields.Count && i < restore.Count; i++)
            {
                if (_playerQueryFields[i].Path != restore[i])
                {
                    _playerQueryFields[i].Path = restore[i];
                }
            }
        }));
    }

    private void PlayerQueryFieldPathComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: PlayerQueryField field } combo)
        {
            return;
        }

        // 必须排到 Loaded 之后：设置 ItemsSource 时 WPF 会自己动一次 Text，
        // 立刻赋值会被它那一下覆盖掉，表现为"框里什么都没有"。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            combo.Text = field.Path;
        }));
    }

    /// <summary>
    /// 手敲的路径在失焦时才提交。
    /// ComboBox 没有 TextChanged 事件（那是 TextBox 的），而每敲一个字符就写一次模型也没必要 ——
    /// 点"开始查询"会让输入框失焦，所以这个时机足够可靠。
    /// </summary>
    private void PlayerQueryFieldPathComboBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: PlayerQueryField field } combo)
        {
            return;
        }

        var typed = combo.Text ?? "";
        if (!string.Equals(field.Path, typed, StringComparison.Ordinal))
        {
            field.Path = typed;
        }

        SavePlayerQuerySettings(false);
    }

    private void PlayerQueryFieldPathComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: PlayerQueryField field } combo ||
            combo.SelectedItem is not PlayerQueryFieldCandidate candidate)
        {
            return;
        }

        field.Path = candidate.Path;

        // 显示名还是空的、或者还是新行的默认值，就顺手填上建议的中文名；用户自己写过的名字不动。
        if (string.IsNullOrWhiteSpace(field.Label) || field.Label == "新字段")
        {
            field.Label = candidate.Label;
        }

        // 关键：可编辑 ComboBox 在选中项之后，WPF 自己还会再改一次 Text
        // （用的是 ItemTemplate / TextSearch 那边的文字）。这个改动发生在 SelectionChanged
        // **之后**，如果在这里直接赋值就会被覆盖，表现为"选了但框里不显示"。
        // 所以必须排到 WPF 那一下后面再写。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            combo.Text = field.Path;
            SavePlayerQuerySettings(false);
        }));
    }

    // ==================== 字段列表：拖拽排序 ====================
    //
    // 三件事一起做，才有"抓住一条拖过去、其他条目让开"的感觉：
    //   1. 残影：拖动时给这一行拍一张静态图，做成浮层跟着鼠标走
    //   2. 原行变淡：表示"它被拿起来了"
    //   3. 空位：在目标位置撑开一条空位，其他行被布局自动推开（带动画）
    //
    // 松手才真正重排 —— 拖动过程中列表顺序不变，这样"会落到哪里"始终看得清。

    private Popup? _dragGhost;
    private DispatcherTimer? _dragGhostTimer;

    private void PlayerQueryFieldDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _fieldDragOrigin = e.GetPosition(null);
    }

    private void PlayerQueryFieldDragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = _fieldDragOrigin - e.GetPosition(null);
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: PlayerQueryField field } handle)
        {
            return;
        }

        _draggedField = field;
        _draggedIndex = _playerQueryFields.IndexOf(field);

        // 残影要在这时候拍：先拍快照，再让原行变淡，否则残影里也会是淡的
        ShowDragGhost(handle);
        field.IsDragging = true;

        try
        {
            // DoDragDrop 内部跑一个嵌套消息循环，所以拖动期间的界面更新能正常渲染
            DragDrop.DoDragDrop((DependencyObject)handle, field, DragDropEffects.Move);
        }
        finally
        {
            field.IsDragging = false;
            ClearFieldDropFeedback();
            HideDragGhost();
            StopFieldAutoScroll();

            _draggedField = null;
            _draggedIndex = -1;
        }
    }

    private void PlayerQueryFieldList_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedField == null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        UpdateFieldAutoScroll(e);


        // 找光标落在哪一行（被拖的那一行除外 —— 它已经"拿起来"了）
        var host = PlayerQueryFieldItemsControl;
        var cursor = e.GetPosition(host);

        PlayerQueryField? target = null;
        var before = false;

        for (var i = 0; i < _playerQueryFields.Count; i++)
        {
            if (ReferenceEquals(_playerQueryFields[i], _draggedField)) continue;
            if (host.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;

            var topLeft = container.TranslatePoint(new Point(0, 0), host);
            var rowBottom = topLeft.Y + container.ActualHeight;

            if (cursor.Y < topLeft.Y)
            {
                target = _playerQueryFields[i];
                before = true;
                break;
            }

            if (cursor.Y <= rowBottom)
            {
                target = _playerQueryFields[i];
                before = cursor.Y < topLeft.Y + container.ActualHeight / 2;
                break;
            }
        }

        if (target == null)
        {
            // 光标不在任何一行上：把所有反馈收掉。
            // 之前紫线残留的根因就在这 —— 事件挂在行上，拖到行与行之间/列表外面时
            // 根本不会有行来清它，于是那条线就一直留着。
            ClearFieldDropFeedback();
            return;
        }

        // 插入槽位按"把被拖的行从序列里拿掉"之后的坐标算，这样落位才是准的
        var draggedIndex = _playerQueryFields.IndexOf(_draggedField);
        var targetIndex = _playerQueryFields.IndexOf(target);
        var targetSlot = targetIndex < draggedIndex ? targetIndex : targetIndex - 1;
        _gapIndex = before ? targetSlot : targetSlot + 1;

        foreach (var item in _playerQueryFields)
        {
            item.DropTarget = ReferenceEquals(item, target)
                ? (before ? PlayerQueryFieldDropTarget.Before : PlayerQueryFieldDropTarget.After)
                : PlayerQueryFieldDropTarget.None;
        }

        SetFieldGap(target, before);
    }

    private void PlayerQueryFieldList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        // 先把状态读出来再清 —— ClearFieldDropFeedback 会把 _gapIndex 归 -1
        var draggedIndex = _draggedIndex;
        var gapIndex = _gapIndex;
        StopFieldAutoScroll();

        ClearFieldDropFeedback();

        if (_draggedField == null || draggedIndex < 0 || gapIndex < 0)
        {
            _draggedField = null;
            _draggedIndex = -1;
            return;
        }

        // 按空位槽位重排：先把被拖的行从序列里拿掉，再插进空位
        var others = new List<PlayerQueryField>();
        for (var i = 0; i < _playerQueryFields.Count; i++)
        {
            if (i != draggedIndex)
            {
                others.Add(_playerQueryFields[i]);
            }
        }

        others.Insert(Math.Min(gapIndex, others.Count), _draggedField);

        _playerQueryFields.Clear();
        foreach (var item in others)
        {
            _playerQueryFields.Add(item);
        }

        _draggedField = null;
        _draggedIndex = -1;
        SavePlayerQuerySettings(false);
        ShowToast(Copy.OrderUpdated);
    }

    private ScrollViewer? GetPlayerQueryScrollViewer()
    {
        // 直接使用命名元素，比从 ItemsControl 往上摸视觉树可靠得多。
        if (PlayerQueryPanel != null)
        {
            return PlayerQueryPanel;
        }

        DependencyObject? current = PlayerQueryFieldItemsControl;
        while (current != null)
        {
            if (current is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void UpdateFieldAutoScroll(DragEventArgs e)
    {
        var scrollViewer = GetPlayerQueryScrollViewer();
        if (scrollViewer == null || _draggedField == null)
        {
            StopFieldAutoScroll();
            return;
        }

        var position = e.GetPosition(scrollViewer);
        const double edge = 44.0;
        const double minSpeed = 6.0;
        const double maxSpeed = 26.0;

        if (position.Y < edge)
        {
            var intensity = Math.Clamp((edge - position.Y) / edge, 0.0, 1.0);
            _fieldAutoScrollDelta = -Math.Min(maxSpeed, minSpeed + intensity * (maxSpeed - minSpeed));
        }
        else if (position.Y > scrollViewer.ActualHeight - edge)
        {
            var intensity = Math.Clamp((position.Y - (scrollViewer.ActualHeight - edge)) / edge, 0.0, 1.0);
            _fieldAutoScrollDelta = Math.Min(maxSpeed, minSpeed + intensity * (maxSpeed - minSpeed));
        }
        else
        {
            StopFieldAutoScroll();
            return;
        }

        StartFieldAutoScrollTimer();
    }

    private void StopFieldAutoScroll()
    {
        _fieldAutoScrollDelta = 0;
        if (_fieldAutoScrollTimerId != IntPtr.Zero)
        {
            KillTimer(IntPtr.Zero, _fieldAutoScrollTimerId);
            _fieldAutoScrollTimerId = IntPtr.Zero;
        }
    }

    private void StartFieldAutoScrollTimer()
    {
        if (_fieldAutoScrollTimerId != IntPtr.Zero)
        {
            return;
        }

        _fieldAutoScrollTimerProc ??= FieldAutoScrollTimerCallback;
        _fieldAutoScrollTimerId = SetTimer(IntPtr.Zero, IntPtr.Zero, 30, _fieldAutoScrollTimerProc!);
    }

    private void FieldAutoScrollTimerCallback(IntPtr hWnd, uint uMsg, IntPtr nIDEvent, uint dwTime)
    {
        if (Math.Abs(_fieldAutoScrollDelta) < 0.1)
        {
            StopFieldAutoScroll();
            return;
        }

        var scrollViewer = GetPlayerQueryScrollViewer();
        if (scrollViewer == null)
        {
            StopFieldAutoScroll();
            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + _fieldAutoScrollDelta);
        UpdateFieldDropTargetFromCursor();
    }

    private void UpdateFieldDropTargetFromCursor()
    {
        if (_draggedField == null)
        {
            return;
        }

        var host = PlayerQueryFieldItemsControl;
        var cursor = Mouse.GetPosition(host);

        PlayerQueryField? target = null;
        var before = false;

        for (var i = 0; i < _playerQueryFields.Count; i++)
        {
            if (ReferenceEquals(_playerQueryFields[i], _draggedField)) continue;
            if (host.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container) continue;

            var topLeft = container.TranslatePoint(new Point(0, 0), host);
            var rowBottom = topLeft.Y + container.ActualHeight;

            if (cursor.Y < topLeft.Y)
            {
                target = _playerQueryFields[i];
                before = true;
                break;
            }

            if (cursor.Y <= rowBottom)
            {
                target = _playerQueryFields[i];
                before = cursor.Y < topLeft.Y + container.ActualHeight / 2;
                break;
            }
        }

        if (target == null)
        {
            ClearFieldDropFeedback();
            return;
        }

        var draggedIndex = _playerQueryFields.IndexOf(_draggedField);
        var targetIndex = _playerQueryFields.IndexOf(target);
        var targetSlot = targetIndex < draggedIndex ? targetIndex : targetIndex - 1;
        _gapIndex = before ? targetSlot : targetSlot + 1;

        foreach (var item in _playerQueryFields)
        {
            item.DropTarget = ReferenceEquals(item, target)
                ? (before ? PlayerQueryFieldDropTarget.Before : PlayerQueryFieldDropTarget.After)
                : PlayerQueryFieldDropTarget.None;
        }

        SetFieldGap(target, before);
    }
    private void PlayerQueryFieldList_DragLeave(object sender, DragEventArgs e)
    {
        StopFieldAutoScroll();
    }



    private void PlayerQueryFieldItemsControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 只有正在拖拽条目时才手动滚外层，避免抢走路径下拉框弹出层的滚轮。
        if (_draggedField == null)
        {
            return;
        }

        var scrollViewer = GetPlayerQueryScrollViewer();
        if (scrollViewer == null)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }


    /// <summary>清掉所有拖拽反馈：插入线 + 空位。所有退出路径（换目标/拖出列表/松手/取消）都必须走这里。</summary>
    private void ClearFieldDropFeedback()
    {
        foreach (var item in _playerQueryFields)
        {
            item.DropTarget = PlayerQueryFieldDropTarget.None;
        }

        ClearFieldGap();
        _gapIndex = -1;
    }

    // ---------- 残影 ----------

    private void ShowDragGhost(FrameworkElement source)
    {
        HideDragGhost();

        var ghost = new Border
        {
            Width = source.ActualWidth,
            Height = source.ActualHeight,
            Background = SnapshotVisual(source),
            CornerRadius = new CornerRadius(10),
            Opacity = 0.94,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 5,
                Direction = 270,
                Opacity = 0.30,
                Color = Colors.Black
            }
        };

        _dragGhost = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            StaysOpen = true,
            PlacementTarget = this,
            Placement = PlacementMode.Relative,
            Child = ghost
        };
        _dragGhost.IsOpen = true;

        // DoDragDrop 期间普通鼠标事件到不了控件，所以用定时器主动跟随鼠标位置
        _dragGhostTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _dragGhostTimer.Tick += (_, _) => MoveDragGhost(ghost);
        _dragGhostTimer.Start();

        MoveDragGhost(ghost);
    }

    private void MoveDragGhost(FrameworkElement ghost)
    {
        if (_dragGhost == null)
        {
            return;
        }

        var position = Mouse.GetPosition(this);
        _dragGhost.HorizontalOffset = position.X - ghost.Width / 2;
        _dragGhost.VerticalOffset = position.Y - ghost.Height / 2;
    }

    private void HideDragGhost()
    {
        _dragGhostTimer?.Stop();
        _dragGhostTimer = null;

        if (_dragGhost != null)
        {
            _dragGhost.IsOpen = false;
            _dragGhost.Child = null;
            _dragGhost = null;
        }
    }

    /// <summary>
    /// 给残影拍一张静态快照。
    /// 用 RenderTargetBitmap 而不是 VisualBrush：VisualBrush 是实时的，
    /// 会把原行"变淡"的效果一起画进残影里。
    /// </summary>
    private static ImageBrush SnapshotVisual(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));

        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();

        var brush = new ImageBrush(bitmap)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        brush.Freeze();
        return brush;
    }

    // ---------- 空位 ----------

    /// <summary>空位高度，约等于一行（含行间距）。和字段行的高度保持视觉一致即可。</summary>
    private const double FieldRowGapHeight = 42;

    private PlayerQueryField? _gapRow;
    private bool _gapBefore;

    /// <summary>
    /// 在目标行上边或下边撑开一条空位。
    ///
    /// 做法是给这个行的容器加一个带动画的 Margin —— 而不是往列表里插一个占位项。
    /// 好处是"其他条目闪开"是布局自然产生的结果，不用手工算谁该位移多少、位移多少像素。
    /// 代价是动画期间每帧要走一次布局；字段列表只有十几行，可以忽略。
    /// </summary>
    private void SetFieldGap(PlayerQueryField row, bool before)
    {
        if (ReferenceEquals(_gapRow, row) && _gapBefore == before)
        {
            return;
        }

        ClearFieldGap();

        if (PlayerQueryFieldItemsControl.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container)
        {
            return;
        }

        _gapRow = row;
        _gapBefore = before;

        var target = before
            ? new Thickness(0, FieldRowGapHeight, 0, 0)
            : new Thickness(0, 0, 0, FieldRowGapHeight);

        container.BeginAnimation(FrameworkElement.MarginProperty,
            new ThicknessAnimation(target, TimeSpan.FromMilliseconds(180)) { EasingFunction = Motion.Soft() });
    }

    private void ClearFieldGap()
    {
        if (_gapRow != null &&
            PlayerQueryFieldItemsControl.ItemContainerGenerator.ContainerFromItem(_gapRow) is FrameworkElement container)
        {
            container.BeginAnimation(FrameworkElement.MarginProperty,
                new ThicknessAnimation(new Thickness(0), TimeSpan.FromMilliseconds(150)) { EasingFunction = Motion.Soft() });
        }

        _gapRow = null;
        _gapBefore = false;
    }

    private List<(string Label, string Value)> BuildPlayerQueryStats(
        JsonElement root,
        string gametype,
        string modeKey,
        List<PlayerQueryField> fields)
    {
        var stats = new List<(string Label, string Value)>();

        foreach (var field in fields)
        {
            var label = string.IsNullOrWhiteSpace(field.Label) ? field.Path : field.Label;
            var path = ApplyPlayerQueryPathTokens(field.Path, gametype, modeKey);
            var value = TryResolvePlayerQueryComputed(root, path, out var computed)
                ? computed
                : ResolvePlayerQueryPath(root, path);
            stats.Add((label, value));
        }

        return stats;
    }

    private static bool TryResolvePlayerQueryComputed(JsonElement root, string path, out string value)
    {
        value = "—";

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith("winrate:", StringComparison.OrdinalIgnoreCase))
        {
            return ComputePlayerQueryWinRate(root, path["winrate:".Length..], out value);
        }

        if (path.StartsWith("ratio:", StringComparison.OrdinalIgnoreCase))
        {
            return ComputePlayerQueryRatio(root, path["ratio:".Length..], false, out value);
        }

        return false;
    }

    private static bool ComputePlayerQueryWinRate(JsonElement root, string expression, out string value)
    {
        value = "—";

        var winPath = "";
        var losePath = "";
        var totalPath = "";

        if (expression.Contains('='))
        {
            foreach (var segment in expression.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = segment.Split('=', 2);
                if (pair.Length != 2)
                {
                    continue;
                }

                var key = pair[0].Trim();
                var pathValue = pair[1].Trim();
                if (key.Equals("win", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("wins", StringComparison.OrdinalIgnoreCase))
                {
                    winPath = pathValue;
                }
                else if (key.Equals("lose", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("loss", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("losses", StringComparison.OrdinalIgnoreCase))
                {
                    losePath = pathValue;
                }
                else if (key.Equals("total", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("games", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("total_game", StringComparison.OrdinalIgnoreCase))
                {
                    totalPath = pathValue;
                }
            }
        }
        else if (expression.Contains('|'))
        {
            // 兼容旧格式：win|total；如果第二段看起来是败场，则按 win + lose 计算总场数。
            var parts = expression.Split('|', StringSplitOptions.None);
            if (parts.Length == 2)
            {
                winPath = parts[0].Trim();
                var second = parts[1].Trim();
                if (second.Contains("lose", StringComparison.OrdinalIgnoreCase) ||
                    second.Contains("loss", StringComparison.OrdinalIgnoreCase) ||
                    second.Contains("defeat", StringComparison.OrdinalIgnoreCase))
                {
                    losePath = second;
                }
                else
                {
                    totalPath = second;
                }
            }
        }
        else
        {
            winPath = expression.Trim();
        }

        if (string.IsNullOrWhiteSpace(winPath))
        {
            return true;
        }

        var winText = ResolvePlayerQueryPath(root, winPath);
        if (!double.TryParse(winText, NumberStyles.Float, CultureInfo.InvariantCulture, out var wins))
        {
            return true;
        }

        double? total = null;

        if (!string.IsNullOrWhiteSpace(totalPath))
        {
            var totalText = ResolvePlayerQueryPath(root, totalPath);
            if (double.TryParse(totalText, NumberStyles.Float, CultureInfo.InvariantCulture, out var totalValue))
            {
                total = totalValue;
            }
        }

        if (!total.HasValue && !string.IsNullOrWhiteSpace(losePath))
        {
            var loseText = ResolvePlayerQueryPath(root, losePath);
            if (double.TryParse(loseText, NumberStyles.Float, CultureInfo.InvariantCulture, out var loses))
            {
                total = wins + loses;
            }
        }

        // 未提供场数时，尝试自动把 .win 替换成 .lose 推导总场数。
        if (!total.HasValue && winPath.EndsWith(".win", StringComparison.OrdinalIgnoreCase))
        {
            losePath = winPath[..^4] + ".lose";
            var loseText = ResolvePlayerQueryPath(root, losePath);
            if (double.TryParse(loseText, NumberStyles.Float, CultureInfo.InvariantCulture, out var loses))
            {
                total = wins + loses;
            }
        }

        if (!total.HasValue || Math.Abs(total.Value) < double.Epsilon)
        {
            return true;
        }

        value = (wins / total.Value * 100.0).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        return true;
    }

    private static bool ComputePlayerQueryRatio(JsonElement root, string expression, bool percent, out string value)
    {
        value = "—";
        var parts = expression.Split('|', StringSplitOptions.None);
        if (parts.Length != 2)
        {
            return true;
        }

        var numeratorText = ResolvePlayerQueryPath(root, parts[0].Trim());
        var denominatorText = ResolvePlayerQueryPath(root, parts[1].Trim());

        if (!double.TryParse(numeratorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(denominatorText, NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            Math.Abs(denominator) < double.Epsilon)
        {
            return true;
        }

        var ratio = numerator / denominator;
        value = percent
            ? (ratio * 100.0).ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : ratio.ToString("0.##", CultureInfo.InvariantCulture);
        return true;
    }

    private static string ApplyPlayerQueryPathTokens(string path, string gametype, string modeKey)
    {
        var result = path
            .Replace("{gametype}", gametype, StringComparison.OrdinalIgnoreCase)
            .Replace("{mode}", modeKey, StringComparison.OrdinalIgnoreCase);

        while (result.Contains("..", StringComparison.Ordinal))
        {
            result = result.Replace("..", ".", StringComparison.Ordinal);
        }

        return result.Trim('.');
    }

    private static string ResolvePlayerQueryPath(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "—";
        }

        var current = root;
        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment;
            var bracketIndex = segment.IndexOf('[');
            string? arrayIndexText = null;

            if (bracketIndex >= 0)
            {
                arrayIndexText = segment[(bracketIndex + 1)..].TrimEnd(']');
                segment = segment[..bracketIndex];
            }

            if (!string.IsNullOrWhiteSpace(segment) &&
                !TryMovePlayerQueryProperty(ref current, segment))
            {
                return "—";
            }

            if (arrayIndexText != null &&
                (!int.TryParse(arrayIndexText, out var arrayIndex) ||
                 !TryMovePlayerQueryArrayIndex(ref current, arrayIndex)))
            {
                return "—";
            }
        }

        return FormatPlayerQueryValue(current);
    }

    private static bool TryMovePlayerQueryProperty(ref JsonElement current, string propertyName)
    {
        if (current.ValueKind == JsonValueKind.Object && TryGetPlayerProperty(current, propertyName, out var next))
        {
            current = next;
            return true;
        }

        return false;
    }

    private static bool TryMovePlayerQueryArrayIndex(ref JsonElement current, int index)
    {
        if (current.ValueKind == JsonValueKind.Array && index >= 0 && index < current.GetArrayLength())
        {
            current = current[index];
            return true;
        }

        return false;
    }

    private static string FormatPlayerQueryValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            JsonValueKind.Null => "—",
            JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(element),
            _ => "—"
        };
    }


    private string GetPlayerQueryModeKey(string gametype)
    {
        var mode = PlayerQueryModeComboBox.SelectedItem as string ?? "总览";
        if (mode.StartsWith("总览", StringComparison.Ordinal))
        {
            return "";
        }

        var isBedwars = string.Equals(gametype, "bedwars", StringComparison.OrdinalIgnoreCase);
        if (mode.Contains("Solo", StringComparison.OrdinalIgnoreCase))
        {
            return isBedwars ? "bw1" : "sw1";
        }

        if (mode.Contains("双人", StringComparison.Ordinal) || mode.Contains("2v2", StringComparison.OrdinalIgnoreCase))
        {
            return isBedwars ? "bw8" : "sw2";
        }

        return isBedwars ? "bw16" : "sw2";
    }

    private void ClearPlayerQueryResultPanel()
    {
        PlayerQueryResultPanel.Children.Clear();
    }

    private void ShowPlayerQueryError(string message)
    {
        var textBlock = new TextBlock
        {
            Text = message,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Child = textBlock
        };
        border.SetResourceReference(Border.BackgroundProperty, "DangerSoftBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "DangerBrush");
        PlayerQueryResultPanel.Children.Add(border);
    }

    private List<(string Label, string Value)> ExtractPlayerStats(JsonElement root, string gametype, string modeKey)
    {
        var data = root;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var dataNode) &&
            dataNode.ValueKind == JsonValueKind.Object)
        {
            data = dataNode;
        }

        var gameNodeName = string.Equals(gametype, "skywars", StringComparison.OrdinalIgnoreCase) ? "skywars" : "bedwars";
        var metricNode = ResolvePlayerMetricNode(data, gameNodeName, modeKey);
        var stats = new List<(string Label, string Value)>();

        if (!metricNode.HasValue || metricNode.Value.ValueKind != JsonValueKind.Object)
        {
            return stats;
        }

        var node = metricNode.Value;
        var win = GetPlayerNumber(node, "win", "wins", "victory", "victories");
        var lose = GetPlayerNumber(node, "lose", "losses", "defeat", "defeats");
        var total = GetPlayerNumber(node, "total_game", "total_games", "game", "games", "match", "matches", "total_match", "total_matches")
                    ?? (win.HasValue && lose.HasValue ? win.Value + lose.Value : null);

        stats.Add(("总场数", total.HasValue ? FormatPlayerNumber(total.Value) : "—"));

        var winRate = GetPlayerNumber(node, "win_rate", "winrate", "winRate", "win_ratio", "wins_rate");
        if (winRate.HasValue)
        {
            stats.Add(("胜率", FormatPlayerPercent(winRate.Value)));
        }
        else if (total.HasValue && total.Value > 0 && win.HasValue)
        {
            stats.Add(("胜率", FormatPlayerPercent(win.Value / total.Value * 100.0)));
        }
        else
        {
            stats.Add(("胜率", "—"));
        }

        var kd = GetPlayerRatio(node, "kd", "kdr", "kd_ratio", "kill_death_ratio", "killDeathRatio",
            new[] { "kills", "kill", "total_kills" },
            new[] { "deaths", "death", "total_deaths" });
        stats.Add(("KD", kd ?? "—"));

        if (string.Equals(gametype, "bedwars", StringComparison.OrdinalIgnoreCase))
        {
            var fkd = GetPlayerRatio(node, "fkd", "fkdr", "final_kd", "final_kdr", "final_kill_death_ratio",
                new[] { "final_kills", "finalKills", "final_kill", "fk", "total_fk" },
                new[] { "final_deaths", "finalDeaths", "final_death", "fd", "deaths", "death" });
            stats.Add(("FKD", fkd ?? "—"));

            var beds = GetPlayerNumber(node, "bed_destory", "bed_destroy", "bedDestroyed", "bed_destroyed", "beds", "bed_break", "break_bed", "total_bed_destroy");
            stats.Add(("拆床数", beds.HasValue ? FormatPlayerNumber(beds.Value) : "—"));
        }

        return stats.Any(item => item.Value != "—") ? stats : new List<(string Label, string Value)>();
    }

    private static JsonElement? ResolvePlayerMetricNode(JsonElement data, string gameNodeName, string modeKey)
    {
        if (string.IsNullOrEmpty(modeKey))
        {
            return data;
        }

        if (data.ValueKind == JsonValueKind.Object &&
            TryGetPlayerProperty(data, gameNodeName, out var gameNode) &&
            gameNode.ValueKind == JsonValueKind.Object)
        {
            if (!string.IsNullOrEmpty(modeKey) &&
                TryGetPlayerProperty(gameNode, modeKey, out var modeNode) &&
                modeNode.ValueKind == JsonValueKind.Object)
            {
                return modeNode;
            }

            if (TryGetPlayerProperty(gameNode, "all", out var allNode) && allNode.ValueKind == JsonValueKind.Object)
            {
                return allNode;
            }

            return gameNode;
        }

        if (!string.IsNullOrEmpty(modeKey) && TryFindPlayerObject(data, modeKey, out var found))
        {
            return found;
        }

        return data;
    }

    private static bool TryFindPlayerObject(JsonElement element, string propertyName, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    found = property.Value;
                    return true;
                }

                if (TryFindPlayerObject(property.Value, propertyName, out found))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindPlayerObject(item, propertyName, out found))
                {
                    return true;
                }
            }
        }

        found = default;
        return false;
    }

    private static double? GetPlayerNumber(JsonElement element, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (!TryGetPlayerProperty(element, alias, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number)
            {
                return property.GetDouble();
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var text = property.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var normalized = text.Trim().TrimEnd('%');
                if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static string? GetPlayerRatio(
        JsonElement element,
        string primaryAlias,
        string secondAlias,
        string thirdAlias,
        string fourthAlias,
        string fifthAlias,
        string[] numeratorAliases,
        string[] denominatorAliases)
    {
        var direct = GetPlayerNumber(element, primaryAlias, secondAlias, thirdAlias, fourthAlias, fifthAlias);
        if (direct.HasValue)
        {
            return FormatPlayerDecimal(direct.Value);
        }

        var numerator = GetPlayerNumber(element, numeratorAliases);
        var denominator = GetPlayerNumber(element, denominatorAliases);
        if (numerator.HasValue && denominator.HasValue && denominator.Value > 0)
        {
            return FormatPlayerDecimal(numerator.Value / denominator.Value);
        }

        return null;
    }

    private static bool TryGetPlayerProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string FormatPlayerNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.01
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string FormatPlayerDecimal(double value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string FormatPlayerPercent(double value)
    {
        var percent = value is > 0 and <= 1 ? value * 100.0 : value;
        return percent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    private static void AddPlayerStat(JsonElement data, string key, List<(string Label, string Value)> stats)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(key, out var value))
        {
            return;
        }

        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return;
        }

        stats.Add((PlayerStatLabels.TryGetValue(key, out var label) ? label : key, FormatPlayerStatValue(value)));
    }

    private static void TryFindPlayerStatsNode(JsonElement element, string modeKey, List<(string Label, string Value)> stats)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, modeKey, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    FlattenPlayerStats(property.Value, "", stats);
                    return;
                }

                TryFindPlayerStatsNode(property.Value, modeKey, stats);
                if (stats.Count > 0)
                {
                    return;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                TryFindPlayerStatsNode(item, modeKey, stats);
                if (stats.Count > 0)
                {
                    return;
                }
            }
        }
    }

    private static void FlattenPlayerStats(JsonElement element, string path, List<(string Label, string Value)> stats)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    FlattenPlayerStats(property.Value, childPath, stats);
                }
                else
                {
                    var key = property.Name.ToLowerInvariant();
                    var label = PlayerStatLabels.TryGetValue(key, out var mapped) ? mapped : property.Name;
                    stats.Add((label, FormatPlayerStatValue(property.Value)));
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                FlattenPlayerStats(item, $"{path}[{index}]", stats);
                index++;
            }
        }
    }

    private static string FormatPlayerStatValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            JsonValueKind.Null => "—",
            _ => element.GetRawText()
        };
    }

    private void RenderPlayerStats(List<(string Label, string Value)> stats)
    {
        for (var i = 0; i < stats.Count; i++)
        {
            var item = stats[i];
            var row = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Opacity = 0,
                RenderTransform = new TranslateTransform(20, 0)
            };
            row.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
            row.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = item.Label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var value = new TextBlock
            {
                Text = item.Value,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
            row.Child = grid;
            PlayerQueryResultPanel.Children.Add(row);

            var begin = TimeSpan.FromMilliseconds(i * 28);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
            {
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            row.BeginAnimation(OpacityProperty, fade);

            if (row.RenderTransform is TranslateTransform translate)
            {
                var slide = new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(380))
                {
                    BeginTime = begin,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                translate.BeginAnimation(TranslateTransform.XProperty, slide);
            }
        }
    }



private bool _isDarkMode = false;

private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
{
    _isDarkMode = !_isDarkMode;
    ApplyTheme(_isDarkMode);
    UpdateListeningIndicator(_watcher.IsRunning);
    SaveThemePreference(_isDarkMode);
    LogStatus(_isDarkMode ? Copy.SwitchedToDark : Copy.SwitchedToLight);
}

private void ApplyTheme(bool darkMode)
{
    // 颜色定义
    Color primaryColor, primaryHoverColor, primaryPressedColor;
    Color accentColor, sidebarBgColor, contentBgColor, cardBgColor, borderColor;
    Color textPrimaryColor, textSecondaryColor, textOnPrimaryColor;
    Color switchTrackColor, switchTrackBorderColor, switchThumbColor;
    Color hoverBgColor, pressedBgColor;

    if (darkMode)
    {
        // 夜间模式：来自 GUI.html 的深色主题变量
        primaryColor = Color.FromRgb(0x8A, 0x8A, 0x96);
        primaryHoverColor = Color.FromRgb(0xA8, 0xA8, 0xB6);
        primaryPressedColor = Color.FromRgb(0x72, 0x72, 0x7E);
        accentColor = Color.FromRgb(0xB0, 0x8A, 0x9C);
        sidebarBgColor = Color.FromRgb(0x1F, 0x1F, 0x22);
        contentBgColor = Color.FromRgb(0x23, 0x23, 0x26);
        cardBgColor = Color.FromRgb(0x2A, 0x2A, 0x2E);
        borderColor = Color.FromRgb(0x3A, 0x3A, 0x41);
        textPrimaryColor = Color.FromRgb(0xF1, 0xF1, 0xF3);
        textSecondaryColor = Color.FromRgb(0xA9, 0xA9, 0xB4);
        textOnPrimaryColor = Colors.White;
        switchTrackColor = Color.FromRgb(0x4A, 0x4A, 0x52);
        switchTrackBorderColor = Color.FromRgb(0x4B, 0x4B, 0x54);
        switchThumbColor = Color.FromRgb(0xF1, 0xF1, 0xF3);
        hoverBgColor = Color.FromRgb(0x33, 0x33, 0x38);
        pressedBgColor = Color.FromRgb(0x3E, 0x3E, 0x45);
    }
    else
    {
        // 日间模式：来自 GUI.html 的浅色主题变量
        primaryColor = Color.FromRgb(0x7C, 0x6C, 0xF0);
        primaryHoverColor = Color.FromRgb(0xA1, 0x8C, 0xFF);
        primaryPressedColor = Color.FromRgb(0x5F, 0x4A, 0xD6);
        accentColor = Color.FromRgb(0xFF, 0x8F, 0xBE);
        sidebarBgColor = Color.FromRgb(0xFB, 0xFB, 0xFC);
        contentBgColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
        cardBgColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
        borderColor = Color.FromRgb(0xE7, 0xE7, 0xEC);
        textPrimaryColor = Color.FromRgb(0x2A, 0x2A, 0x31);
        textSecondaryColor = Color.FromRgb(0x6E, 0x6E, 0x7A);
        textOnPrimaryColor = Colors.White;
        switchTrackColor = Color.FromRgb(0xE4, 0xE4, 0xEA);
        switchTrackBorderColor = Color.FromRgb(0xD4, 0xD4, 0xDB);
        switchThumbColor = Colors.White;
        hoverBgColor = Color.FromRgb(0xF4, 0xF4, 0xF7);
        pressedBgColor = Color.FromRgb(0xEA, 0xEA, 0xEF);
    }

    // 替换资源字典里的画刷对象。
    // XAML 中对应引用已改为 DynamicResource，因此切换主题时控件会自动跟随新画刷。
    SetResourceBrush("PrimaryBrush", primaryColor);
    SetResourceBrush("PrimaryHoverBrush", primaryHoverColor);
    SetResourceBrush("PrimaryPressedBrush", primaryPressedColor);
    SetResourceBrush("AccentBrush", accentColor);
    SetResourceBrush("SidebarBgBrush", sidebarBgColor);
    SetResourceBrush("ContentBgBrush", contentBgColor);
    SetResourceBrush("CardBgBrush", cardBgColor);
    SetResourceBrush("BorderBrush", borderColor);
    SetResourceBrush("TextPrimaryBrush", textPrimaryColor);
    SetResourceBrush("TextSecondaryBrush", textSecondaryColor);
    SetResourceBrush("TextOnPrimaryBrush", textOnPrimaryColor);
    SetResourceBrush("HoverBgBrush", hoverBgColor);
    SetResourceBrush("PressedBgBrush", pressedBgColor);
    SetResourceBrush("NavHoverBrush", darkMode ? hoverBgColor : Color.FromRgb(0xF5, 0xF6, 0xFA));
    SetResourceBrush("SwitchTrackBrush", switchTrackColor);
    SetResourceBrush("SwitchTrackBorderBrush", switchTrackBorderColor);
    SetResourceBrush("SwitchThumbBrush", switchThumbColor);

    // GUI.html 里新增的语义色，ModernControls.xaml 中的控件会通过 DynamicResource 自动跟随。
    var surface2Color = darkMode ? Color.FromRgb(0x33, 0x33, 0x38) : Color.FromRgb(0xF4, 0xF4, 0xF7);
    var surface3Color = darkMode ? Color.FromRgb(0x3E, 0x3E, 0x45) : Color.FromRgb(0xEA, 0xEA, 0xEF);
    var borderStrongColor = darkMode ? Color.FromRgb(0x4B, 0x4B, 0x54) : Color.FromRgb(0xD4, 0xD4, 0xDB);
    var textTertiaryColor = darkMode ? Color.FromRgb(0x7D, 0x7D, 0x89) : Color.FromRgb(0x9B, 0x9B, 0xA7);
    var trackColor = darkMode ? Color.FromRgb(0x4A, 0x4A, 0x52) : Color.FromRgb(0xE4, 0xE4, 0xEA);
    var windowBgColor = darkMode ? Color.FromRgb(0x14, 0x14, 0x16) : Color.FromRgb(0xED, 0xED, 0xF1);
    var surfaceColor = cardBgColor;
    var primarySoftColor = Color.FromArgb(darkMode ? (byte)0x2E : (byte)0x1F, primaryColor.R, primaryColor.G, primaryColor.B);
    var primarySofterColor = Color.FromArgb(darkMode ? (byte)0x1A : (byte)0x12, primaryColor.R, primaryColor.G, primaryColor.B);
    var accentSoftColor = Color.FromArgb(darkMode ? (byte)0x26 : (byte)0x26, darkMode ? (byte)0xB0 : (byte)0xFF, darkMode ? (byte)0x8A : (byte)0x8F, darkMode ? (byte)0x9C : (byte)0xBE);
    var mintSoftColor = Color.FromArgb(0x26, darkMode ? (byte)0x5B : (byte)0x3F, darkMode ? (byte)0xBF : (byte)0xCF, darkMode ? (byte)0x9A : (byte)0xA9);
    var dangerSoftColor = Color.FromArgb(darkMode ? (byte)0x29 : (byte)0x21, darkMode ? (byte)0xD4 : (byte)0xFF, darkMode ? (byte)0x82 : (byte)0x6B, darkMode ? (byte)0x8E : (byte)0x81);

    SetResourceBrush("WindowBgBrush", windowBgColor);
    SetResourceBrush("SurfaceBrush", surfaceColor);
    SetResourceBrush("Surface2Brush", surface2Color);
    SetResourceBrush("Surface3Brush", surface3Color);
    SetResourceBrush("ContentBgBrush", contentBgColor);
    SetResourceBrush("BorderStrongBrush", borderStrongColor);
    SetResourceBrush("TextTertiaryBrush", textTertiaryColor);
    SetResourceBrush("PrimarySoftBrush", primarySoftColor);
    SetResourceBrush("PrimarySofterBrush", primarySofterColor);
    SetResourceBrush("AccentSoftBrush", accentSoftColor);
    SetResourceBrush("MintSoftBrush", mintSoftColor);
    SetResourceBrush("MintBrush", darkMode ? Color.FromRgb(0x5B, 0xBF, 0x9A) : Color.FromRgb(0x3F, 0xCF, 0xA9));
    SetResourceBrush("DangerBrush", darkMode ? Color.FromRgb(0xD4, 0x82, 0x8E) : Color.FromRgb(0xFF, 0x6B, 0x81));
    SetResourceBrush("DangerSoftBrush", dangerSoftColor);
    SetResourceBrush("TrackBrush", trackColor);
    SetResourceBrush("AmberBrush", darkMode ? Color.FromRgb(0xC9, 0xA8, 0x6A) : Color.FromRgb(0xFF, 0xB5, 0x47));
    SetResourceBrush("SkyBrush", darkMode ? Color.FromRgb(0x6F, 0xA8, 0xC9) : Color.FromRgb(0x57, 0xC5, 0xF2));
    SetResourceBrush("ConsoleBgBrush", Color.FromRgb(0x12, 0x10, 0x1F));
    SetResourceBrush("ConsoleTextBrush", Color.FromRgb(0xCF, 0xC9, 0xF5));
    SetResourceBrush("ConsoleMutedBrush", Color.FromRgb(0x6F, 0x6A, 0x9A));
    SetResourceBrush("ConsoleChatBrush", Color.FromRgb(0x9B, 0xE7, 0xC4));
    SetResourceBrush("ConsoleInfoBrush", Color.FromRgb(0xB4, 0xA8, 0xFF));
    SetResourceBrush("ConsoleWarnBrush", Color.FromRgb(0xFF, 0xC0, 0x69));

    Resources["GlowShadow"] = new DropShadowEffect
    {
        BlurRadius = 16,
        ShadowDepth = 0,
        Opacity = darkMode ? 0.42 : 0.35,
        Color = primaryColor
    };

    // 覆盖 WPF 默认控件使用的系统颜色，否则 ComboBox 的下拉 Popup
    // 和 ComboBoxItem 的选中/高亮状态仍然会用日间系统色。
    UpdateSystemColorResources(cardBgColor, borderColor, textPrimaryColor, textSecondaryColor, primaryColor, textOnPrimaryColor);

    // 让没有单独设置 Foreground 的 TextBlock 继承窗口前景色，
    // 避免卡片背景已变深但默认文字仍是日间黑色。
    Foreground = new SolidColorBrush(textPrimaryColor);
    System.Windows.Documents.TextElement.SetForeground(this, new SolidColorBrush(textPrimaryColor));
    if (Content is System.Windows.Controls.DockPanel rootPanel)
    {
        System.Windows.Documents.TextElement.SetForeground(rootPanel, new SolidColorBrush(textPrimaryColor));
    }

    // 更新主题切换按钮图标
    ThemeToggleButton.Content = darkMode ? "☀️" : "🌙";
    ThemeToggleButton.Foreground = new SolidColorBrush(textPrimaryColor);

    // 更新窗口背景
    Background = new SolidColorBrush(contentBgColor);

    // 更新标题栏
    TitleBarBorder.Background = new SolidColorBrush(cardBgColor);
    TitleBarBorder.BorderBrush = new SolidColorBrush(borderColor);
    TitleTextBlock.Foreground = darkMode ? new SolidColorBrush(textPrimaryColor) : new SolidColorBrush(primaryColor);

    // 更新侧边栏
    SidebarBorder.Background = new SolidColorBrush(sidebarBgColor);
    SidebarBorder.BorderBrush = new SolidColorBrush(borderColor);
    NavIndicator.Background = darkMode ? new SolidColorBrush(textPrimaryColor) : new SolidColorBrush(accentColor);
    NavIndicator.Effect = new DropShadowEffect
    {
        BlurRadius = 10,
        ShadowDepth = 0,
        Opacity = darkMode ? 0.35 : 0.45,
        Color = darkMode ? textPrimaryColor : accentColor
    };

    // 更新主内容区
    MainContentGrid.Background = new SolidColorBrush(contentBgColor);

    // 更新顶部工具栏
    ToolbarBorder.Background = new SolidColorBrush(cardBgColor);
    ToolbarBorder.BorderBrush = new SolidColorBrush(borderColor);

    // 更新底部状态栏
    StatusBarBorder.Background = new SolidColorBrush(cardBgColor);
    StatusBarBorder.BorderBrush = new SolidColorBrush(borderColor);
    StatusTextBlock.Foreground = new SolidColorBrush(textSecondaryColor);

    // 更新按钮样式资源（阴影等）。
    UpdateButtonStyles(darkMode, primaryColor, primaryHoverColor, primaryPressedColor, 
                       textPrimaryColor, textOnPrimaryColor, borderColor, hoverBgColor, pressedBgColor);

    // 文本框、列表、开关、卡片和导航按钮的 XAML 样式内部都使用 DynamicResource，
    // 上面的 SetResourceBrush 已经替换了画刷，因此不再用代码重建模板。
}

private void RefreshLoadedStyles()
{
    if (Resources[typeof(ListBoxItem)] is Style listBoxItemStyle)
    {
        ApplyStyleToAll<ListBoxItem>(listBoxItemStyle);
    }

    if (Resources[typeof(ListBox)] is Style listBoxStyle)
    {
        ApplyStyleToAll<ListBox>(listBoxStyle);
    }

    if (Resources[typeof(TextBox)] is Style textBoxStyle)
    {
        ApplyTextBoxStyleToDirectTextBoxes(textBoxStyle);
    }
}

private void ApplyTextBoxStyleToDirectTextBoxes(Style style)
{
    ApplyTextBoxStyleRecursive(this, style, new HashSet<DependencyObject>());
}

private void ApplyTextBoxStyleRecursive(DependencyObject parent, Style style, HashSet<DependencyObject> visited)
{
    if (!visited.Add(parent))
    {
        return;
    }

    foreach (var child in GetLogicalAndVisualChildren(parent))
    {
        if (child is TextBox textBox && textBox.TemplatedParent == null)
        {
            textBox.Style = style;
        }

        ApplyTextBoxStyleRecursive(child, style, visited);
    }
}

private void ApplyStyleToAll<T>(Style style) where T : FrameworkElement
{
    ApplyStyleRecursive<T>(this, style, new HashSet<DependencyObject>());
}

private void ApplyStyleRecursive<T>(DependencyObject parent, Style style, HashSet<DependencyObject> visited) where T : FrameworkElement
{
    if (!visited.Add(parent))
    {
        return;
    }

    foreach (var child in GetLogicalAndVisualChildren(parent))
    {
        if (child is T element)
        {
            element.Style = style;
        }

        ApplyStyleRecursive<T>(child, style, visited);
    }
}

private void ApplyComboBoxThemeToAll(Style style)
{
    ApplyComboBoxThemeRecursive(this, style, new HashSet<DependencyObject>());
}

private void ApplyComboBoxThemeRecursive(DependencyObject parent, Style style, HashSet<DependencyObject> visited)
{
    if (!visited.Add(parent))
    {
        return;
    }

    foreach (var child in GetLogicalAndVisualChildren(parent))
    {
        if (child is ComboBox comboBox)
        {
            comboBox.Style = style;

            // 直接设置本地值，保证默认 ComboBox 模板的主选框区域也跟随主题。
            if (Resources["CardBgBrush"] is Brush cardBg)
            {
                comboBox.Background = cardBg;
            }
            if (Resources["TextPrimaryBrush"] is Brush textPrimary)
            {
                comboBox.Foreground = textPrimary;
            }
            if (Resources["BorderBrush"] is Brush borderBrush)
            {
                comboBox.BorderBrush = borderBrush;
            }

            // 默认 ComboBox 模板内部会从控件自己的 Resources 里查找系统颜色，
            // 这里给每个 ComboBox 单独复制一份，确保主选框和 Popup 都使用主题色。
            ApplySystemColorResourcesToComboBox(comboBox);
            ApplyComboBoxTextBoxStyle(comboBox, Resources["TextPrimaryBrush"] as Brush);
        }

        ApplyComboBoxThemeRecursive(child, style, visited);
    }
}

private void ApplySystemColorResourcesToComboBox(ComboBox comboBox)
{
    var keys = new object[]
    {
        SystemColors.WindowBrushKey,
        SystemColors.WindowTextBrushKey,
        SystemColors.ControlBrushKey,
        SystemColors.ControlTextBrushKey,
        SystemColors.ControlDarkBrushKey,
        SystemColors.ControlLightBrushKey,
        SystemColors.ControlLightLightBrushKey,
        SystemColors.ControlDarkDarkBrushKey,
        SystemColors.HighlightBrushKey,
        SystemColors.HighlightTextBrushKey,
        SystemColors.InactiveSelectionHighlightBrushKey,
        SystemColors.InactiveSelectionHighlightTextBrushKey,
        SystemColors.GrayTextBrushKey,
        SystemColors.MenuBrushKey,
        SystemColors.MenuTextBrushKey
    };

    foreach (var key in keys)
    {
        if (Resources[key] is Brush brush)
        {
            comboBox.Resources[key] = brush;
        }
    }
}

private void ApplyComboBoxTextBoxStyle(ComboBox comboBox, Brush? textPrimary)
{
    if (textPrimary == null)
    {
        return;
    }

    // 可编辑 ComboBox 内部有一个 TextBox，单独给它一个透明、无边框的样式，
    // 避免主选框区域出现日间白色输入框。
    var style = new Style(typeof(TextBox));
    style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
    style.Setters.Add(new Setter(Control.ForegroundProperty, textPrimary));
    style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
    style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
    style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0)));
    style.Setters.Add(new Setter(TextBox.CaretBrushProperty, textPrimary));
    comboBox.Resources[typeof(TextBox)] = style;
}

private static IEnumerable<DependencyObject> GetLogicalAndVisualChildren(DependencyObject parent)
{
    var seen = new HashSet<DependencyObject>();

    foreach (var child in LogicalTreeHelper.GetChildren(parent))
    {
        if (child is DependencyObject dependencyObject && seen.Add(dependencyObject))
        {
            yield return dependencyObject;
        }
    }

    if (parent is Visual)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (seen.Add(child))
            {
                yield return child;
            }
        }
    }
}

private void ApplyCardStyleToLoadedBorders(Style style)
{
    ApplyCardStyleRecursive(this, style);
}

private void ApplyCardStyleRecursive(DependencyObject parent, Style style)
{
    var count = VisualTreeHelper.GetChildrenCount(parent);
    for (var i = 0; i < count; i++)
    {
        var child = VisualTreeHelper.GetChild(parent, i);
        if (child is Border border && border.Style != null)
        {
            // 这个窗口里有具名 Style 的 Border 都是卡片（CardStyle）。
            border.Style = style;
        }

        ApplyCardStyleRecursive(child, style);
    }
}

private void UpdateSystemColorResources(Color cardBgColor, Color borderColor, Color textPrimaryColor,
                                        Color textSecondaryColor, Color primaryColor, Color textOnPrimaryColor)
{
    // 让 WPF 默认模板里使用的 SystemColors.* 也跟随主题，
    // 主要解决 ComboBox 下拉 Popup、ComboBoxItem 选中/高亮仍是日间色的问题。
    void SetSystemColor(object key, Color color)
    {
        var brush = new SolidColorBrush(color);
        Resources[key] = brush;
        if (Application.Current != null)
        {
            Application.Current.Resources[key] = brush;
        }
    }

    SetSystemColor(SystemColors.WindowBrushKey, cardBgColor);
    SetSystemColor(SystemColors.WindowTextBrushKey, textPrimaryColor);
    SetSystemColor(SystemColors.ControlBrushKey, cardBgColor);
    SetSystemColor(SystemColors.ControlTextBrushKey, textPrimaryColor);
    SetSystemColor(SystemColors.ControlDarkBrushKey, borderColor);
    SetSystemColor(SystemColors.ControlLightBrushKey, borderColor);
    SetSystemColor(SystemColors.ControlLightLightBrushKey, cardBgColor);
    SetSystemColor(SystemColors.ControlDarkDarkBrushKey, borderColor);
    SetSystemColor(SystemColors.HighlightBrushKey, primaryColor);
    SetSystemColor(SystemColors.HighlightTextBrushKey, textOnPrimaryColor);
    SetSystemColor(SystemColors.InactiveSelectionHighlightBrushKey, primaryColor);
    SetSystemColor(SystemColors.InactiveSelectionHighlightTextBrushKey, textOnPrimaryColor);
    SetSystemColor(SystemColors.GrayTextBrushKey, textSecondaryColor);
    SetSystemColor(SystemColors.MenuBrushKey, cardBgColor);
    SetSystemColor(SystemColors.MenuTextBrushKey, textPrimaryColor);
}

private void SetResourceBrush(string key, Color color)
{
    // XAML/BAML 中的 Freezable 资源可能已被冻结，不能原地改 Color；
    // 所以这里直接放入一个全新的可变 SolidColorBrush，
    // 并依靠 DynamicResource 让已加载控件自动更新。
    Resources[key] = new SolidColorBrush(color);
}

private void UpdateButtonStyles(bool darkMode, Color primaryColor, Color primaryHoverColor, 
                                 Color primaryPressedColor, Color textPrimaryColor, 
                                 Color textOnPrimaryColor, Color borderColor, 
                                 Color hoverBgColor, Color pressedBgColor)
{
    // 画刷已在 ApplyTheme 里通过 SetResourceBrush 替换为新的可变画刷，
    // 这里只需更新非画刷资源（颜色值、阴影效果）。
    Resources["PrimaryColor"] = primaryColor;

    // HoverShadow 同样可能是已冻结的 Freezable，因此直接整体替换。
    Resources["HoverShadow"] = new DropShadowEffect
    {
        BlurRadius = 16,
        ShadowDepth = 4,
        Opacity = darkMode ? 0.3 : 0.15,
        Color = Colors.Black
    };
}

private void UpdateTextBoxStyles(bool darkMode, Color cardBgColor, Color borderColor, 
                                  Color textPrimaryColor, Color primaryColor)
{
    // 更新所有 TextBox 的样式
    var textBoxStyle = new Style(typeof(TextBox));
    textBoxStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, new SolidColorBrush(cardBgColor)));
    textBoxStyle.Setters.Add(new Setter(TextBox.BorderBrushProperty, new SolidColorBrush(borderColor)));
    textBoxStyle.Setters.Add(new Setter(TextBox.ForegroundProperty, new SolidColorBrush(textPrimaryColor)));
    textBoxStyle.Setters.Add(new Setter(TextBox.CaretBrushProperty, new SolidColorBrush(textPrimaryColor)));
    textBoxStyle.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(1)));
    textBoxStyle.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(8, 4, 8, 4)));
    textBoxStyle.Setters.Add(new Setter(TextBox.MarginProperty, new Thickness(4)));
    textBoxStyle.Setters.Add(new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));
    
    // 更新模板
    var template = new ControlTemplate(typeof(TextBox));
    var borderElement = new FrameworkElementFactory(typeof(Border));
    borderElement.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
    borderElement.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(TextBox.BorderBrushProperty));
    borderElement.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(TextBox.BorderThicknessProperty));
    borderElement.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
    borderElement.SetValue(Border.PaddingProperty, new TemplateBindingExtension(TextBox.PaddingProperty));
    
    var scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
    scrollViewer.Name = "PART_ContentHost";
    borderElement.AppendChild(scrollViewer);
    
    template.VisualTree = borderElement;
    textBoxStyle.Setters.Add(new Setter(TextBox.TemplateProperty, template));
    
    Resources[typeof(TextBox)] = textBoxStyle;
}

private void UpdateToggleSwitchStyles(bool darkMode, Color switchTrackColor, Color switchTrackBorderColor, 
                                       Color switchThumbColor, Color primaryColor, Color textPrimaryColor)
{
    // 更新 ToggleSwitchStyle
    var toggleStyle = new Style(typeof(CheckBox));
    toggleStyle.Setters.Add(new Setter(CheckBox.ForegroundProperty, new SolidColorBrush(textPrimaryColor)));
    toggleStyle.Setters.Add(new Setter(CheckBox.CursorProperty, Cursors.Hand));
    
    var template = new ControlTemplate(typeof(CheckBox));
    var stackPanel = new FrameworkElementFactory(typeof(StackPanel));
    stackPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
    stackPanel.SetValue(StackPanel.BackgroundProperty, Brushes.Transparent);
    
    var grid = new FrameworkElementFactory(typeof(Grid));
    grid.SetValue(Grid.WidthProperty, 44.0);
    grid.SetValue(Grid.HeightProperty, 24.0);
    grid.SetValue(Grid.VerticalAlignmentProperty, VerticalAlignment.Center);
    
    var track = new FrameworkElementFactory(typeof(Border));
    track.Name = "track";
    track.SetValue(Border.BackgroundProperty, new SolidColorBrush(switchTrackColor));
    track.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
    track.SetValue(Border.BorderBrushProperty, new SolidColorBrush(switchTrackBorderColor));
    track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
    
    var thumb = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
    thumb.Name = "thumb";
    thumb.SetValue(System.Windows.Shapes.Ellipse.WidthProperty, 18.0);
    thumb.SetValue(System.Windows.Shapes.Ellipse.HeightProperty, 18.0);
    thumb.SetValue(System.Windows.Shapes.Ellipse.FillProperty, new SolidColorBrush(switchThumbColor));
    thumb.SetValue(System.Windows.Shapes.Ellipse.HorizontalAlignmentProperty, HorizontalAlignment.Left);
    thumb.SetValue(System.Windows.Shapes.Ellipse.MarginProperty, new Thickness(2, 0, 0, 0));
    thumb.SetValue(System.Windows.Shapes.Ellipse.VerticalAlignmentProperty, VerticalAlignment.Center);
    thumb.SetValue(System.Windows.Shapes.Ellipse.EffectProperty, new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.3, Color = Colors.Black });
    
    grid.AppendChild(track);
    grid.AppendChild(thumb);
    stackPanel.AppendChild(grid);
    
    var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
    contentPresenter.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 0, 0));
    contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
    contentPresenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
    stackPanel.AppendChild(contentPresenter);
    
    template.VisualTree = stackPanel;
    
    // 添加触发器
    var checkedTrigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
    // 直接设置颜色，不使用空 BeginStoryboard（否则 Style 密封时会抛异常）
    checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(primaryColor), "track"));
    checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(primaryColor), "track"));
    checkedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Ellipse.MarginProperty, new Thickness(22, 0, 0, 0), "thumb"));
    template.Triggers.Add(checkedTrigger);
    
    var mouseOverTrigger = new Trigger { Property = CheckBox.IsMouseOverProperty, Value = true };
    mouseOverTrigger.Setters.Add(new Setter(Border.EffectProperty, new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.2, Color = Colors.Black }, "track"));
    template.Triggers.Add(mouseOverTrigger);
    
    toggleStyle.Setters.Add(new Setter(CheckBox.TemplateProperty, template));
    Resources["ToggleSwitchStyle"] = toggleStyle;
}

private void UpdateCardStyles(Color cardBgColor, Color borderColor)
{
    var cardStyle = new Style(typeof(Border));
    cardStyle.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(cardBgColor)));
    cardStyle.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(borderColor)));
    cardStyle.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
    cardStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(12)));
    cardStyle.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(16)));
    cardStyle.Setters.Add(new Setter(Border.MarginProperty, new Thickness(0, 0, 0, 12)));
    cardStyle.Setters.Add(new Setter(Border.EffectProperty, new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.08, Color = Colors.Black }));
    Resources["CardStyle"] = cardStyle;
}

private void UpdateNavRadioStyles(bool darkMode, Color textSecondaryColor, Color hoverBgColor, Color textPrimaryColor)
{
    var navStyle = new Style(typeof(RadioButton));
    navStyle.Setters.Add(new Setter(RadioButton.ForegroundProperty, new SolidColorBrush(textSecondaryColor)));
    navStyle.Setters.Add(new Setter(RadioButton.FontSizeProperty, 14.0));
    navStyle.Setters.Add(new Setter(RadioButton.FontWeightProperty, FontWeights.SemiBold));
    navStyle.Setters.Add(new Setter(RadioButton.MarginProperty, new Thickness(0, 2, 0, 2)));
    navStyle.Setters.Add(new Setter(RadioButton.CursorProperty, Cursors.Hand));
    
    var template = new ControlTemplate(typeof(RadioButton));
    var border = new FrameworkElementFactory(typeof(Border));
    border.Name = "border";
    border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
    border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
    border.SetValue(Border.PaddingProperty, new Thickness(14, 10, 14, 10));
    border.SetValue(Border.MarginProperty, new Thickness(5, 0, 0, 0));
    
    var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
    contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
    border.AppendChild(contentPresenter);
    
    template.VisualTree = border;
    
    var mouseOverTrigger = new Trigger { Property = RadioButton.IsMouseOverProperty, Value = true };
    mouseOverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hoverBgColor), "border"));
    mouseOverTrigger.Setters.Add(new Setter(Border.EffectProperty, new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.15, Color = Colors.Black }, "border"));
    template.Triggers.Add(mouseOverTrigger);
    
    navStyle.Setters.Add(new Setter(RadioButton.TemplateProperty, template));
    Resources["NavRadioButtonStyle"] = navStyle;
}

private void UpdateListAndComboBoxStyles(bool darkMode, Color cardBgColor, Color borderColor, Color textPrimaryColor)
{
    // 更新 ListBox 样式
    var listBoxStyle = new Style(typeof(ListBox));
    listBoxStyle.Setters.Add(new Setter(ListBox.BackgroundProperty, new SolidColorBrush(cardBgColor)));
    listBoxStyle.Setters.Add(new Setter(ListBox.BorderBrushProperty, new SolidColorBrush(borderColor)));
    listBoxStyle.Setters.Add(new Setter(ListBox.ForegroundProperty, new SolidColorBrush(textPrimaryColor)));
    Resources[typeof(ListBox)] = listBoxStyle;

    var listBoxItemStyle = new Style(typeof(ListBoxItem));
    listBoxItemStyle.Setters.Add(new Setter(ListBoxItem.ForegroundProperty, new SolidColorBrush(textPrimaryColor)));
    Resources[typeof(ListBoxItem)] = listBoxItemStyle;
}

private void SaveThemePreference(bool darkMode)
{
    try
    {
        _settings.EnableDebugLog = _settings.EnableDebugLog; // 触发保存
        _settings.IsDarkMode = darkMode;
        SettingsService.Save(_settings);
    }
    catch { }
}

    private void LoadRuleCollections()
    {
        _colorRules.Clear();
        foreach (var rule in _settings.ColorRules) _colorRules.Add(rule);

        _replaceRules.Clear();
        foreach (var rule in _settings.ReplaceRules) _replaceRules.Add(rule);

        _blockKeywords.Clear();
        foreach (var keyword in _settings.BlockKeywords) _blockKeywords.Add(keyword);

        ColorRuleListBox.ItemsSource = _colorRules;
        ReplaceRuleListBox.ItemsSource = _replaceRules;
        BlockKeywordListBox.ItemsSource = _blockKeywords;
    }

    private void LoadUiFromSettings()
    {
        LogPathTextBox.Text = _settings.LogPath;
        var logEncodingItem = LogEncodingComboBox.Items.Cast<object>()
            .FirstOrDefault(x => string.Equals(x?.ToString(), _settings.LogEncoding, StringComparison.OrdinalIgnoreCase));
        if (logEncodingItem != null)
        {
            LogEncodingComboBox.SelectedItem = logEncodingItem;
        }
        else if (LogEncodingComboBox.Items.Count > 0)
        {
            LogEncodingComboBox.SelectedIndex = 0;
        }
        OverlayWidthTextBox.Text = _settings.OverlayWidth.ToString("0.#");
        OpacitySlider.Value = Math.Clamp(_settings.OverlayOpacity, 0.1, 1.0);
        BackgroundOpacitySlider.Value = Math.Clamp(_settings.BackgroundOpacity, 0.0, 1.0);
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
            fontItem = new FontItem(_settings.FontFamily, _settings.FontFamily);
            _fontItems.Insert(0, fontItem);
            FontFamilyComboBox.ItemsSource = null;
            FontFamilyComboBox.ItemsSource = _fontItems;
            FontFamilyComboBox.SelectedItem = fontItem;
        }

        FontSizeTextBox.Text = _settings.FontSize.ToString("0.#");
        FontWeightComboBox.SelectedItem = FontWeightComboBox.Items.Cast<string>().FirstOrDefault(x => string.Equals(x, _settings.FontWeight, StringComparison.OrdinalIgnoreCase));
        TextColorPreview.Background = ParseBrush(_settings.TextColor, Brushes.White);
        BackgroundColorPreview.Background = ParseBrush(_settings.BackgroundColor, new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
        PlayerContentColorPreview.Background = string.IsNullOrWhiteSpace(_settings.PlayerContentColor) ? Brushes.Transparent : ParseBrush(_settings.PlayerContentColor, Brushes.White);
        PlayerNameColorPreview.Background = string.IsNullOrWhiteSpace(_settings.PlayerNameColor) ? Brushes.Transparent : ParseBrush(_settings.PlayerNameColor, Brushes.White);

        TextColorHexText.Text = FormatHex(_settings.TextColor);
        BackgroundColorHexText.Text = FormatHex(_settings.BackgroundColor);
        PlayerContentColorHexText.Text = FormatHex(_settings.PlayerContentColor);
        PlayerNameColorHexText.Text = FormatHex(_settings.PlayerNameColor);

        EnableDebugLogCheckBox.IsChecked = _settings.EnableDebugLog;
        EnableTextSelectionCheckBox.IsChecked = _settings.EnableTextSelection;
        MergeDuplicateMessagesCheckBox.IsChecked = _settings.MergeDuplicateMessages;
        EnableMessageAnimationCheckBox.IsChecked = _settings.EnableMessageAnimation;
        EnableAutoGgCheckBox.IsChecked = _settings.EnableAutoGg;
        AutoGgTriggerTextBox.Text = _settings.AutoGgTriggerPattern;
        AutoGgChatKeyTextBox.Text = _settings.AutoGgChatKey;
        AutoGgTextTextBox.Text = _settings.AutoGgText;
        AutoGgUseClipboardCheckBox.IsChecked = _settings.AutoGgUseClipboard;
        UpdateDebugLogVisibility();
        UpdateAutoGgVisibility();
        UpdateDebugLogCount();
        LoadPlayerQueryUiFromSettings();
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
        EnableMessageAnimationCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        EnableMessageAnimationCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
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

        PlayerQueryKeyPasswordBox.LostFocus += (_, _) => SavePlayerQuerySettings(false);
        PlayerQueryIdTextBox.LostFocus += (_, _) => SavePlayerQuerySettings(false);
        PlayerQueryModeComboBox.SelectionChanged += (_, _) => SavePlayerQuerySettings(false);
        PlayerQueryRememberKeyCheckBox.Checked += (_, _) => SavePlayerQuerySettings(false);
        PlayerQueryRememberKeyCheckBox.Unchecked += (_, _) => SavePlayerQuerySettings(false);
    }

    private void SaveSettingsFromUi(bool log = true)
    {
        if (_loading) return;

        _settings.LogPath = LogPathTextBox.Text.Trim();
        var logEncoding = LogEncodingComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(logEncoding))
        {
            logEncoding = "Auto";
        }
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
        if (FontFamilyComboBox.SelectedItem is FontItem selectedFont) _settings.FontFamily = selectedFont.Source;
        else _settings.FontFamily = string.IsNullOrWhiteSpace(FontFamilyComboBox.Text) ? "Microsoft YaHei UI" : FontFamilyComboBox.Text.Trim();
        _settings.FontSize = ParseDouble(FontSizeTextBox.Text, 16, 8, 96);
        _settings.FontWeight = FontWeightComboBox.SelectedItem as string ?? "Normal";
        _settings.EnableDebugLog = EnableDebugLogCheckBox.IsChecked == true;
        _settings.EnableTextSelection = EnableTextSelectionCheckBox.IsChecked == true;
        _settings.MergeDuplicateMessages = MergeDuplicateMessagesCheckBox.IsChecked == true;
        _settings.EnableMessageAnimation = EnableMessageAnimationCheckBox.IsChecked == true;
        _settings.EnableAutoGg = EnableAutoGgCheckBox.IsChecked == true;
        _settings.AutoGgTriggerPattern = AutoGgTriggerTextBox.Text.Trim();
        _settings.AutoGgChatKey = AutoGgChatKeyTextBox.Text.Trim();
        _settings.AutoGgText = AutoGgTextTextBox.Text;
        _settings.AutoGgUseClipboard = AutoGgUseClipboardCheckBox.IsChecked == true;
        _settings.ColorRules = _colorRules.ToList();
        _settings.ReplaceRules = _replaceRules.ToList();
        _settings.BlockKeywords = _blockKeywords.ToList();
        CapturePlayerQuerySettings();

        SettingsService.Save(_settings);
        _overlay?.ApplySettings();

        if (logEncodingChanged && _watcher.IsRunning)
        {
            _watcher.UpdateEncoding(_settings.LogEncoding);
            _overlay?.ClearMessages();
            AppendDebugLog("[编码] 已切换为 " + _settings.LogEncoding + "，已清空悬浮窗旧消息");
        }

        if (log) LogStatus(Copy.ConfigSaved);
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
                StopListening();
                StartListening();
            }
            else SaveSettingsFromUi();
        }
    }

    private void ToggleListenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_watcher.IsRunning) StopListening();
        else StartListening();
    }

    private void StartListening()
    {
        SaveSettingsFromUi();
        if (string.IsNullOrWhiteSpace(_settings.LogPath))
        {
            // 先让按钮晃一下表示"不行哦"，再弹原因 —— 比直接弹框温柔，也更符合轻声提醒的语气。
            // MessageBox 会阻塞 UI 线程，所以必须等晃动播完再弹，否则动画根本渲染不出来。
            Motion.Shake(ToggleListenButton);
            var prompt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.ShakeMs + 40) };
            prompt.Tick += (_, _) =>
            {
                prompt.Stop();
                MessageBox.Show(this, Copy.NeedLogPath, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            prompt.Start();
            return;
        }

        _watcher.ChatLineReceived += Watcher_ChatLineReceived;
        _watcher.StatusChanged += Watcher_StatusChanged;
        _watcher.DebugLineReceived += Watcher_DebugLineReceived;
        _watcher.Start(_settings.LogPath, _settings.LogEncoding);
        ToggleListenButton.Content = "停止监听";
        UpdateListeningIndicator(true);
        ShowOverlay();
        LogStatus(Copy.WatchStarted + _settings.LogPath);
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
        UpdateListeningIndicator(false);
        LogStatus(Copy.ListeningStopped);
        AppendDebugLog("== 已停止监听 ==");
    }

    private void Watcher_ChatLineReceived(string chatMessage)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var activeReplaceRules = _replaceRules.Where(r => r.IsEnabled).ToList();
            var activeBlockKeywords = _blockKeywords.Where(k => k.IsEnabled).Select(k => k.Keyword).ToList();
            var replacedForCheck = ChatTextProcessor.ApplyReplacements(chatMessage, activeReplaceRules);
            if (ChatTextProcessor.IsBlocked(replacedForCheck, activeBlockKeywords))
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
        if (!_settings.EnableAutoGg || string.IsNullOrWhiteSpace(_settings.AutoGgTriggerPattern)) return;
        if (_autoGgSending) return;
        if ((DateTime.Now - _lastAutoGgAt).TotalSeconds < 5) return;

        try
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(message, _settings.AutoGgTriggerPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return;
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

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            else WindowState = WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized) return;
        DragMove();
    }

    private void StartKeyboardBlock()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            return;
        }

        _keysDownBeforeBlock.Clear();
        for (var key = 0x08; key <= 0xFE; key++)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
            {
                _keysDownBeforeBlock.Add(key);
            }
        }

        _keyboardHookProc = KeyboardHookCallback;
        _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc, GetModuleHandle(null), 0);
    }

    private void StopKeyboardBlock()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }

        _keyboardHookProc = null;
        _keysDownBeforeBlock.Clear();
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hook = Marshal.PtrToStructure<KeyboardLowLevelHookStruct>(lParam);
            var isInjected = (hook.Flags & LlkhfInjected) != 0;

            if (!isInjected)
            {
                var isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                if (isKeyUp && _keysDownBeforeBlock.Remove(hook.VirtualKeyCode))
                {
                    return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
                }

                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int LlkhfInjected = 0x00000010;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardLowLevelHookStruct
    {
        public int VirtualKeyCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private async Task SendAutoGgAsync()
    {
        string? oldClipboardText = null;
        var useClipboardPaste = false;

        try
        {
            AppendDebugLog("[自动GG] 检测到胜利消息，准备发送 gg...");
            await Task.Delay(200);

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

            StartKeyboardBlock();

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
                System.Windows.Forms.SendKeys.SendWait("^v");
            }
            else
            {
                System.Windows.Forms.SendKeys.SendWait(_settings.AutoGgText);
            }

            await Task.Delay(100);
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            AppendDebugLog("[自动GG] 已发送：" + _settings.AutoGgText);
            if (AutoGgLastTriggerText != null)
            {
                AutoGgLastTriggerText.Text = "上次触发：" + DateTime.Now.ToString("HH:mm:ss");
            }
        }
        catch (Exception ex)
        {
            AppendDebugLog("[自动GG] 发送失败：" + ex.Message);
        }
        finally
        {
            StopKeyboardBlock();

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
            UpdateListeningIndicator(_watcher.IsRunning);
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
            LogStatus(Copy.OverlayHidden);
        }
        else
        {
            ShowOverlay();
            LogStatus(Copy.OverlayShown);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _overlay?.ClearMessages();
        LogStatus(Copy.OverlayCleared);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        // 成功后轻轻"跳"一下就行，不需要任何文字提示
        Motion.Heartbeat(SaveButton);
    }

    private void ResetOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        OverlayWidthTextBox.Text = "420";
        OverlayMaxHeightTextBox.Text = "620";
        WrapLengthTextBox.Text = "40";
        MaxMessagesTextBox.Text = "200";
        OpacitySlider.Value = 0.9;
        BackgroundOpacitySlider.Value = 1.0;
        var defaultFont = _fontItems.FirstOrDefault(x => string.Equals(x.Source, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase))
                          ?? _fontItems.FirstOrDefault();
        if (defaultFont != null)
        {
            FontFamilyComboBox.SelectedItem = defaultFont;
        }
        FontSizeTextBox.Text = "16";
        FontWeightComboBox.SelectedItem = "SemiBold";

        _settings.TextColor = "#FFFFFF";
        _settings.BackgroundColor = "#0A0912";
        _settings.PlayerContentColor = "#BFE9FF";
        _settings.PlayerNameColor = "#FFD166";

        TextColorPreview.Background = ParseBrush(_settings.TextColor, Brushes.White);
        BackgroundColorPreview.Background = ParseBrush(_settings.BackgroundColor, new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
        PlayerContentColorPreview.Background = ParseBrush(_settings.PlayerContentColor, Brushes.White);
        PlayerNameColorPreview.Background = ParseBrush(_settings.PlayerNameColor, Brushes.White);
        TextColorHexText.Text = FormatHex(_settings.TextColor);
        BackgroundColorHexText.Text = FormatHex(_settings.BackgroundColor);
        PlayerContentColorHexText.Text = FormatHex(_settings.PlayerContentColor);
        PlayerNameColorHexText.Text = FormatHex(_settings.PlayerNameColor);

        SaveSettingsFromUi(false);
        LogStatus(Copy.ResetOverlay);
    }

    private void AutoGgResetButton_Click(object sender, RoutedEventArgs e)
    {
        AutoGgTriggerTextBox.Text = @"恭喜! .+? 获得胜利!";
        AutoGgChatKeyTextBox.Text = "t";
        AutoGgTextTextBox.Text = "gg";
        AutoGgUseClipboardCheckBox.IsChecked = true;
        SaveSettingsFromUi(false);
        LogStatus(Copy.ResetAutoGg);
    }

    private void CialloButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cialloBusy)
        {
            return;
        }

        _cialloBusy = true;
        CialloButton.IsEnabled = false;

        if (CialloImage.Source == null)
        {
            LoadBrandAssets();
        }

        try
        {
            _cialloPlayer.Stop();
            _cialloPlayer.Position = TimeSpan.Zero;
            _cialloPlayer.Play();
        }
        catch
        {
        }

        CialloOverlay.Visibility = Visibility.Visible;
        CialloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CialloScale.ScaleX = 0;

        var expand = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(640))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        expand.Completed += (_, _) =>
        {
            var holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            holdTimer.Tick += (_, _) =>
            {
                holdTimer.Stop();
                var retract = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(520))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                retract.Completed += (_, _) =>
                {
                    CialloOverlay.Visibility = Visibility.Collapsed;
                    CialloScale.ScaleX = 0;
                    CialloButton.IsEnabled = true;
                    _cialloBusy = false;
                };
                CialloScale.BeginAnimation(ScaleTransform.ScaleXProperty, retract);
            };
            holdTimer.Start();
        };
        CialloScale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
    }

    private void EnableDebugLogCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDebugLogVisibility();
        SaveSettingsFromUi(false);
    }

    private void EnableAutoGgCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAutoGgVisibility();
        SaveSettingsFromUi(false);
    }

    private void UpdateDebugLogVisibility()
    {
        DebugLogGroup.Visibility = EnableDebugLogCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateDebugLogCount();
    }

    private void UpdateAutoGgVisibility()
    {
        AutoGgBody.Visibility = EnableAutoGgCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDebugLogCount()
    {
        if (DebugLogTextBox == null || DebugLogCountText == null)
        {
            return;
        }

        var text = DebugLogTextBox.Text ?? string.Empty;
        var lines = 0;
        if (text.Length > 0)
        {
            lines = text.Count(c => c == '\n');
            if (!text.EndsWith('\n'))
            {
                lines++;
            }
        }

        DebugLogCountText.Text = $"共 {lines} 行";
    }

    private void CopyDebugLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DebugLogTextBox.Text);
            LogStatus(Copy.DebugCopied);
        }
        catch
        {
            LogStatus(Copy.CopyFailed);
        }
    }

    private void ClearDebugLogButton_Click(object sender, RoutedEventArgs e)
    {
        DebugLogTextBox.Clear();
        UpdateDebugLogCount();
        LogStatus(Copy.DebugCleared);
    }

    private void SendManualMessageButton_Click(object sender, RoutedEventArgs e)
    {
        var text = ManualMessageTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ShowOverlay();
        _overlay?.AddMessage(text);
        ManualMessageTextBox.Clear();
        LogStatus(Copy.OverlaySentTo);
    }

    private void AppendDebugLog(string line)
    {
        if (!_settings.EnableDebugLog)
        {
            return;
        }

        DebugLogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        DebugLogTextBox.ScrollToEnd();
        UpdateDebugLogCount();
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
            LogStatus(Copy.RulesExported + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Copy.ExportFailed + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(this, Copy.ImportEmpty, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.ColorRules = export.ColorRules ?? new List<TextColorRule>();
            _settings.ReplaceRules = export.ReplaceRules ?? new List<TextReplaceRule>();
            _settings.BlockKeywords = export.BlockKeywords ?? new List<BlockKeywordItem>();
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
            LogStatus(Copy.RulesImported + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Copy.ImportFailed + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
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
        TextColorHexText.Text = FormatHex(hex);
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
        BackgroundColorHexText.Text = FormatHex(hex);
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
        PlayerContentColorHexText.Text = FormatHex(hex);
        SaveSettingsFromUi(false);
    }

    private void ClearPlayerContentColorButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PlayerContentColor = "";
        PlayerContentColorPreview.Background = Brushes.Transparent;
        PlayerContentColorHexText.Text = FormatHex("");
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
        PlayerNameColorHexText.Text = FormatHex(hex);
        SaveSettingsFromUi(false);
    }

    private void ClearPlayerNameColorButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PlayerNameColor = "";
        PlayerNameColorPreview.Background = Brushes.Transparent;
        PlayerNameColorHexText.Text = FormatHex("");
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
            var alpha = currentColor.A;
            var mediaColor = Color.FromArgb(alpha, c.R, c.G, c.B);
            return mediaColor.A == 0xFF
                ? $"#{mediaColor.R:X2}{mediaColor.G:X2}{mediaColor.B:X2}"
                : $"#{mediaColor.A:X2}{mediaColor.R:X2}{mediaColor.G:X2}{mediaColor.B:X2}";
        }
        catch
        {
            return null;
        }
    }

    // ---------- 文本彩色渲染规则 ----------

    private void ColorRuleListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorRuleListBox.SelectedItem is not TextColorRule rule)
        {
            return;
        }

        ColorRuleTextTextBox.Text = rule.Text;
        _colorRuleColor = rule.Color;
        ColorRuleColorPreview.Background = ParseBrush(rule.Color, Brushes.Red);
        ColorRuleWeightComboBox.SelectedItem = rule.FontWeight;
        ColorRuleRegexCheckBox.IsChecked = rule.UseRegex;
        ColorRuleRegexGroupTextBox.Text = rule.RegexGroup.ToString();
        _colorRuleMatchColor = rule.MatchColor ?? "";
        ColorRuleMatchColorPreview.Background = string.IsNullOrWhiteSpace(_colorRuleMatchColor)
            ? Brushes.Transparent
            : ParseBrush(_colorRuleMatchColor, Brushes.Gold);
        ColorRuleEnabledCheckBox.IsChecked = rule.IsEnabled;
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
            MessageBox.Show(this, Copy.NeedColorText, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rule = new TextColorRule
        {
            Text = text,
            Color = _colorRuleColor,
            FontWeight = ColorRuleWeightComboBox.SelectedItem as string ?? "Light",
            UseRegex = ColorRuleRegexCheckBox.IsChecked == true,
            RegexGroup = ParseInt(ColorRuleRegexGroupTextBox.Text, 0, 0, 100),
            MatchColor = _colorRuleMatchColor,
            IsEnabled = true
        };
        _colorRules.Add(rule);
        ColorRuleListBox.SelectedItem = rule;
        SaveSettingsFromUi(false);
    }

    private void UpdateColorRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ColorRuleListBox.SelectedItem is not TextColorRule rule)
        {
            MessageBox.Show(this, Copy.PickColorRule, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        rule.Text = ColorRuleTextTextBox.Text.Trim();
        rule.Color = _colorRuleColor;
        rule.FontWeight = ColorRuleWeightComboBox.SelectedItem as string ?? "Light";
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

    private void ColorRuleEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ColorRuleListBox.SelectedItem is TextColorRule rule)
        {
            rule.IsEnabled = ColorRuleEnabledCheckBox.IsChecked == true;
            ColorRuleListBox.Items.Refresh();
            ColorRuleListBox.SelectedItem = rule;
            SaveSettingsFromUi(false);
        }
    }

    // ---------- 文本替换 ----------

    private void ReplaceRuleListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReplaceRuleListBox.SelectedItem is not TextReplaceRule rule)
        {
            return;
        }

        ReplaceFindTextBox.Text = rule.FindText;
        ReplaceWithTextBox.Text = rule.ReplaceText;
        ReplaceOnlyPlayerContentCheckBox.IsChecked = rule.OnlyPlayerContent;
        ReplaceRegexCheckBox.IsChecked = rule.UseRegex;
        ReplaceRuleEnabledCheckBox.IsChecked = rule.IsEnabled;
    }

    private void AddReplaceRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var find = ReplaceFindTextBox.Text;
        if (string.IsNullOrEmpty(find))
        {
            MessageBox.Show(this, Copy.NeedFindText, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rule = new TextReplaceRule
        {
            FindText = find,
            ReplaceText = ReplaceWithTextBox.Text ?? string.Empty,
            OnlyPlayerContent = ReplaceOnlyPlayerContentCheckBox.IsChecked == true,
            UseRegex = ReplaceRegexCheckBox.IsChecked == true,
            IsEnabled = true
        };
        _replaceRules.Add(rule);
        ReplaceRuleListBox.SelectedItem = rule;
        SaveSettingsFromUi(false);
    }

    private void UpdateReplaceRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReplaceRuleListBox.SelectedItem is not TextReplaceRule rule)
        {
            MessageBox.Show(this, Copy.PickReplaceRule, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void ReplaceRuleEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ReplaceRuleListBox.SelectedItem is TextReplaceRule rule)
        {
            rule.IsEnabled = ReplaceRuleEnabledCheckBox.IsChecked == true;
            ReplaceRuleListBox.Items.Refresh();
            ReplaceRuleListBox.SelectedItem = rule;
            SaveSettingsFromUi(false);
        }
    }

    // ---------- 屏蔽关键词 ----------

    private void BlockKeywordListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BlockKeywordListBox.SelectedItem is BlockKeywordItem item)
        {
            BlockKeywordTextBox.Text = item.Keyword;
            BlockKeywordEnabledCheckBox.IsChecked = item.IsEnabled;
        }
    }

    private void AddBlockKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        var keyword = BlockKeywordTextBox.Text.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return;
        }

        var item = new BlockKeywordItem
        {
            Keyword = keyword,
            IsEnabled = true
        };
        _blockKeywords.Add(item);
        BlockKeywordTextBox.Clear();
        SaveSettingsFromUi(false);
    }

    private void UpdateBlockKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockKeywordListBox.SelectedItem is not BlockKeywordItem item)
        {
            MessageBox.Show(this, Copy.PickKeyword, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var keyword = BlockKeywordTextBox.Text.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            MessageBox.Show(this, Copy.NeedKeyword, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        item.Keyword = keyword;
        BlockKeywordListBox.Items.Refresh();
        SaveSettingsFromUi(false);
    }

    private void DeleteBlockKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockKeywordListBox.SelectedItem is BlockKeywordItem item)
        {
            _blockKeywords.Remove(item);
            SaveSettingsFromUi(false);
        }
    }

    private void BlockKeywordEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (BlockKeywordListBox.SelectedItem is BlockKeywordItem item)
        {
            item.IsEnabled = BlockKeywordEnabledCheckBox.IsChecked == true;
            BlockKeywordListBox.Items.Refresh();
            BlockKeywordListBox.SelectedItem = item;
            SaveSettingsFromUi(false);
        }
    }

    // ---------- 窗口控制按钮事件 ----------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeButton.Content = "□";
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeButton.Content = "❐";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ---------- 导航指示条动画 ----------

    private void NavRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            MoveIndicatorToSelected();
        }
    }

    private void MoveIndicatorToSelected()
    {
        RadioButton? selected = null;
        if (NavOverlayDisplay.IsChecked == true) selected = NavOverlayDisplay;
        else if (NavAutoGG.IsChecked == true) selected = NavAutoGG;
        else if (NavTextRules.IsChecked == true) selected = NavTextRules;
        else if (NavPlayerQuery.IsChecked == true) selected = NavPlayerQuery;
        else if (NavDebug.IsChecked == true) selected = NavDebug;

        if (selected == null || NavIndicator == null || IndicatorTranslate == null)
        {
            return;
        }

        var parentGrid = (UIElement)NavIndicator.Parent;
        Point relativePoint = selected.TranslatePoint(new Point(0, 0), parentGrid);

        double targetY = relativePoint.Y + selected.ActualHeight / 2 - NavIndicator.ActualHeight / 2;

        var animation = new DoubleAnimation
        {
            To = targetY,
            Duration = TimeSpan.FromMilliseconds(Motion.IndicatorMs),
            // 带一点惯性的落位：轻轻过冲再收回来，而不是硬生生停住
            EasingFunction = Motion.Settle()
        };

        IndicatorTranslate.BeginAnimation(TranslateTransform.YProperty, animation);
        AnimateActivePanel();
    }

    private void AnimateActivePanel()
    {
        FrameworkElement? activePanel = null;
        if (NavOverlayDisplay.IsChecked == true) activePanel = OverlayPanel;
        else if (NavAutoGG.IsChecked == true) activePanel = AutoGgPanel;
        else if (NavTextRules.IsChecked == true) activePanel = RulesPanel;
        else if (NavPlayerQuery.IsChecked == true) activePanel = PlayerQueryPanel;
        else if (NavDebug.IsChecked == true) activePanel = DebugPanel;

        if (activePanel == null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            // 样板阶段：只在"悬浮窗显示"这一个面板上启用错峰浮入，方便和其它面板对比观感。
            // 验收满意后把这段判断删掉，五个面板就都走新动效。
            if (ReferenceEquals(activePanel, OverlayPanel))
            {
                Motion.StaggerIn(activePanel, TryFindResource("CardStyle") as Style);
                return;
            }

            // 旧行为：整个面板当一个单位动（没有错峰，观感偏"整体平移"）
            activePanel.Opacity = 0;
            var translate = new TranslateTransform(0, 18);
            activePanel.RenderTransform = translate;

            activePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(460))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }));
    }

    // ---------- 其它 ----------

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSettingsFromUi(false);
        StopListening();
        _overlay?.Close();
    }

    private void UpdateListeningIndicator(bool listening)
    {
        var brush = (TryFindResource(listening ? "MintBrush" : "TextTertiaryBrush") as Brush)
                    ?? (listening ? Brushes.MediumSeaGreen : Brushes.Gray);

        var miniDot = MiniStatusDot;
        var statusDot = StatusDot;

        if (miniDot != null)
        {
            miniDot.Fill = brush;
        }

        if (statusDot != null)
        {
            statusDot.Fill = brush;
        }

        if (miniDot != null && statusDot != null)
        {
            if (listening)
            {
                var pulse = new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(820))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                miniDot.BeginAnimation(OpacityProperty, pulse);
                statusDot.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                miniDot.BeginAnimation(OpacityProperty, null);
                statusDot.BeginAnimation(OpacityProperty, null);
                miniDot.Opacity = 1;
                statusDot.Opacity = 1;
            }
        }

        if (MiniStatusText != null)
        {
            MiniStatusText.Text = listening ? "监听中" : "未连接";
        }

        if (ToggleListenButton != null)
        {
            var style = TryFindResource(listening ? "SmallDangerButtonStyle" : "SmallPrimaryButtonStyle") as Style;
            if (style != null)
            {
                ToggleListenButton.Style = style;
            }
        }
    }

    private void LogStatus(string message)
    {
        if (StatusTextBlock != null)
        {
            StatusTextBlock.Text = message;
        }

        if (IsLoaded && !string.IsNullOrWhiteSpace(message))
        {
            ShowToast(message);
        }
    }

    private void ShowToast(string message)
    {
        if (ToastHost == null)
        {
            return;
        }

        var surface = (TryFindResource("SurfaceBrush") as Brush) ?? Brushes.White;
        var accent = (TryFindResource("MintBrush") as Brush) ?? Brushes.MediumSeaGreen;
        var textBrush = (TryFindResource("TextPrimaryBrush") as Brush) ?? Brushes.Black;
        var shadow = TryFindResource("CardHoverShadow") as Effect;

        var toast = new Border
        {
            Background = surface,
            BorderBrush = accent,
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(15, 11, 17, 11),
            Margin = new Thickness(0, 0, 0, 10),
            Effect = shadow,
            Opacity = 0,
            RenderTransform = new TranslateTransform(34, 0),
            Child = new TextBlock
            {
                Text = message,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        ToastHost.Children.Add(toast);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        toast.BeginAnimation(OpacityProperty, fade);

        if (toast.RenderTransform is TranslateTransform translate)
        {
            var slide = new DoubleAnimation(34, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
            };
            translate.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ToastHost.Children.Remove(toast);
        };
        timer.Start();
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

    private static string FormatHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "默认";
        }

        try
        {
            if (ColorConverter.ConvertFromString(value) is Color color)
            {
                return color.A == 0xFF
                    ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                    : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            }
        }
        catch
        {
        }

        return value.Trim().ToUpperInvariant();
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

        public override string ToString() => Display;
    }
}

/// <summary>把规则中的颜色字符串转换为画刷，用于规则列表里的圆点。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Brushes.Transparent;
        }

        try
        {
            return new BrushConverter().ConvertFromString(text) as Brush ?? Brushes.Transparent;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}