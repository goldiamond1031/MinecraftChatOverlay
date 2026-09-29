using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.PlayerQuery;

/// <summary>
/// 「玩家查询」页面（原本是主程序 MainWindow.Modern.xaml 里的 PlayerQueryPanel，整块搬过来了）。
///
/// 注意：样式/画刷一律用 DynamicResource —— 插件控件是先 new 出来、再挂进宿主窗口的，
/// 用 StaticResource 解析时找不到宿主 Window 里的资源字典（会直接抛 XamlParseException）。
/// </summary>
public partial class PlayerQueryPage : System.Windows.Controls.UserControl
{
    private readonly PlayerQueryPlugin _plugin;

    /// <summary>设置直接指向插件自己那本（字段名去掉了原来的 PlayerQuery 前缀）。</summary>
    private PlayerQuerySettings _settings => _plugin.Settings;

    /// <summary>填控件期间不许回存（否则会把还没填上的空值写回去）。</summary>
    private bool _loading;

    /// <summary>战绩字段的中文名（原来在宿主的 MainWindow.xaml.cs 里，搬插件时一起带过来）。</summary>
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
            PlayerQueryStatusText.Text = PlayerQueryTexts.NeedApiKey;
            _plugin.Host.ShowToast(PlayerQueryTexts.NeedApiKey);
            return;
        }

        if (string.IsNullOrWhiteSpace(playerId))
        {
            PlayerQueryStatusText.Text = PlayerQueryTexts.NeedPlayerId;
            _plugin.Host.ShowToast(PlayerQueryTexts.NeedPlayerId);
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
            PlayerQueryStatusText.Text = PlayerQueryTexts.NeedQueryField;
            _plugin.Host.ShowToast(PlayerQueryTexts.NeedQueryField);
            return;
        }

        CapturePlayerQuerySettings();
        PlayerQueryButton.IsEnabled = false;
        PlayerQueryButton.Content = PlayerQueryTexts.QueryingButton;
        PlayerQueryStatusText.Text = PlayerQueryTexts.Querying;
        ClearPlayerQueryResultPanel();

        try
        {
            var result = await BuJiDaoQueryService.QueryPlayerAsync(apiKey, playerId, gametype, "all");
            if (!result.Success)
            {
                PlayerQueryStatusText.Text = PlayerQueryTexts.QueryFailedShort;
                ShowPlayerQueryError(result.Error);
                _plugin.Host.ShowToast(PlayerQueryTexts.QueryFailed + result.Error);
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
                PlayerQueryStatusText.Text = PlayerQueryTexts.NoDisplayData;
                ShowPlayerQueryError(PlayerQueryTexts.NoMatchingFieldData);
                return;
            }

            PlayerQueryHintText.Text = PlayerQueryTexts.QueryTarget(
                playerId,
                PlayerQueryGameTypeComboBox.SelectedItem?.ToString() ?? "",
                PlayerQueryModeComboBox.SelectedItem?.ToString() ?? "");
            RenderPlayerStats(stats);
            PlayerQueryStatusText.Text = PlayerQueryTexts.QueryDoneWithCount(stats.Count);
            _plugin.Host.ShowToast(PlayerQueryTexts.QueryDone);
        }
        catch (Exception ex)
        {
            PlayerQueryStatusText.Text = "查询异常";
            ShowPlayerQueryError(ex.Message);
            _plugin.Host.ShowToast(PlayerQueryTexts.QueryError + ex.Message);
        }
        finally
        {
            PlayerQueryButton.IsEnabled = true;
            PlayerQueryButton.Content = PlayerQueryTexts.QueryStartButton;
        }
    }

    private void ClearPlayerQueryButton_Click(object sender, RoutedEventArgs e)
    {
        ClearPlayerQueryResultPanel();
        PlayerQueryStatusText.Text = PlayerQueryTexts.QueryCleared;
        PlayerQueryHintText.Text = PlayerQueryTexts.QueryIdleHint;
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
        PlayerQueryRememberKeyCheckBox.IsChecked = _settings.RememberKey;
        PlayerQueryKeyPasswordBox.Password = _settings.RememberKey ? (_settings.ApiKey ?? "") : "";
        PlayerQueryIdTextBox.Text = _settings.PlayerId ?? "";

        var gameIndex = string.Equals(_settings.GameType, "skywars", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        PlayerQueryGameTypeComboBox.SelectedIndex = gameIndex;
        UpdatePlayerQueryModeOptions();

        var modeItem = PlayerQueryModeComboBox.Items
            .Cast<object>()
            .FirstOrDefault(item => string.Equals(item?.ToString(), _settings.Mode, StringComparison.OrdinalIgnoreCase));
        if (modeItem != null)
        {
            PlayerQueryModeComboBox.SelectedItem = modeItem;
        }

        _playerQueryFields.Clear();
        if (_settings.Fields != null)
        {
            foreach (var field in _settings.Fields)
            {
                _playerQueryFields.Add(field.Clone());
            }
        }

        // 第一次使用时给一套可用预设，之后完全以用户保存的字段为准。
        if (_playerQueryFields.Count == 0 && string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            ApplyPlayerQueryPreset(_settings.GameType);
        }
    }

    private void CapturePlayerQuerySettings()
    {
        _settings.RememberKey = PlayerQueryRememberKeyCheckBox.IsChecked == true;
        _settings.ApiKey = _settings.RememberKey ? PlayerQueryKeyPasswordBox.Password : "";
        _settings.PlayerId = PlayerQueryIdTextBox.Text.Trim();
        _settings.GameType = PlayerQueryGameTypeComboBox.SelectedIndex == 1 ? "skywars" : "bedwars";
        _settings.Mode = PlayerQueryModeComboBox.SelectedItem as string ?? "总览";
        _settings.Fields = _playerQueryFields.Select(field => field.Clone()).ToList();
    }

    private void SavePlayerQuerySettings(bool log)
    {
        if (_loading)
        {
            return;
        }

        CapturePlayerQuerySettings();
        _plugin.SaveSettings();

        if (log)
        {
            _plugin.Host.Log(PlayerQueryTexts.QuerySettingsSaved);
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
        _plugin.Host.ShowToast(PlayerQueryTexts.OrderUpdated);
    }

    private ScrollViewer? GetPlayerQueryScrollViewer()
    {
        // 插件页面里没有自己的 ScrollViewer（外层滚动由宿主提供），
        // 所以直接走下面这条"从 ItemsControl 往上摸视觉树"的兜底路。

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
            new ThicknessAnimation(target, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
    }

    private void ClearFieldGap()
    {
        if (_gapRow != null &&
            PlayerQueryFieldItemsControl.ItemContainerGenerator.ContainerFromItem(_gapRow) is FrameworkElement container)
        {
            container.BeginAnimation(FrameworkElement.MarginProperty,
                new ThicknessAnimation(new Thickness(0), TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
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
    public PlayerQueryPage(PlayerQueryPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        // 初始化期间必须挂上闸门（坑 53）：InitializePlayerQueryUi 里设下拉选中会触发
        // SelectionChanged，而那时 API KEY / 玩家 ID 还没回填 —— 不挡的话会把空值写回配置，
        // 等于把用户存好的 API KEY 抹掉（这个坑真踩过一次）。
        _loading = true;
        try
        {
            InitializePlayerQueryUi();
            RebuildFieldPickerOptions();
            LoadPlayerQueryUiFromSettings();
        }
        finally
        {
            _loading = false;
        }

        PlayerQueryKeyPasswordBox.LostFocus += (_, _) => SavePlayerQuerySettings(false);
        PlayerQueryIdTextBox.LostFocus += (_, _) => SavePlayerQuerySettings(false);
        PlayerQueryModeComboBox.SelectionChanged += (_, _) => SavePlayerQuerySettings(false);
        PlayerQueryRememberKeyCheckBox.Checked += (_, _) => SavePlayerQuerySettings(false);
        PlayerQueryRememberKeyCheckBox.Unchecked += (_, _) => SavePlayerQuerySettings(false);
    }

    /// <summary>下拉框的弹出层贴到控件下沿（和宿主里那个同名处理器一个作用，插件页面自己带一份）。</summary>
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
