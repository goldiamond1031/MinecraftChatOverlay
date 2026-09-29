using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MinecraftChatOverlay.Plugins.WindowFullscreen;

/// <summary>
/// 「窗口全屏」页面（原本是主程序 MainWindow.Modern.xaml 里的 WindowFullscreenPanel + 同名 partial 逻辑文件，
/// 整块搬过来了）。真正的窗口操作在 <see cref="GameWindowFullscreen"/> 里，这里只负责界面和配置。
///
/// 注意：样式/画刷一律用 DynamicResource —— 插件控件是先 new 出来、再挂进宿主窗口的，
/// 用 StaticResource 解析时找不到宿主 Window 里的资源字典（会直接抛 XamlParseException）。
/// </summary>
public partial class WindowFullscreenPage : System.Windows.Controls.UserControl
{
    private readonly WindowFullscreenPlugin _plugin;

    /// <summary>设置直接指向插件自己那本。</summary>
    private WindowFullscreenSettings _settings => _plugin.Settings;

    private bool _windowFullscreenUiReady;
    private DispatcherTimer? _windowFullscreenTimer;

    /// <summary>窗口加载时调用（见 MainWindow_Loaded）。</summary>
    private void InitializeWindowFullscreenUi()
    {
        _windowFullscreenUiReady = false;

        var settings = _settings;
        WindowFullscreenCoverTaskbarCheckBox.IsChecked = settings.CoverTaskbar;
        WindowFullscreenRestoreOnExitCheckBox.IsChecked = settings.RestoreOnExit;

        _windowFullscreenUiReady = true;

        RefreshWindowFullscreenTargets();
        UpdateWindowFullscreenStatus();

        // 游戏可能被玩家自己关掉 / 重开，句柄会失效；定时刷新状态，让界面别停在"已生效"
        _windowFullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _windowFullscreenTimer.Tick += (_, _) => UpdateWindowFullscreenStatus();
        _windowFullscreenTimer.Start();
    }

    // ===================== 列表 =====================

    private void WindowFullscreenRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindowFullscreenTargets();
        UpdateWindowFullscreenStatus();
    }

    private void RefreshWindowFullscreenTargets()
    {
        try
        {
            var targets = GameWindowFullscreen.FindCandidates();
            WindowFullscreenTargetComboBox.ItemsSource = targets;

            if (targets.Count == 0)
            {
                return;
            }

            var settings = _settings;

            // 先按上次的窗口标题找（同一台电脑上可能开着好几个 javaw），再退化成按进程名找
            var index = -1;
            if (!string.IsNullOrWhiteSpace(settings.TargetWindowTitle))
            {
                index = targets.FindIndex(t => string.Equals(t.Title, settings.TargetWindowTitle, StringComparison.Ordinal));
            }

            if (index < 0 && !string.IsNullOrWhiteSpace(settings.TargetProcessName))
            {
                index = targets.FindIndex(t => string.Equals(t.ProcessName, settings.TargetProcessName,
                                                              StringComparison.OrdinalIgnoreCase));
            }

            WindowFullscreenTargetComboBox.SelectedIndex = index >= 0 ? index : 0;
        }
        catch (Exception ex)
        {
            WindowFullscreenStatusText.Text = "列出窗口失败：" + ex.Message;
        }
    }

    // ===================== 变成无边框全屏 / 还原 =====================

    private void WindowFullscreenApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowFullscreenTargetComboBox.SelectedItem is not GameWindowCandidate target)
        {
            WindowFullscreenStatusText.Text = "先在上面选一个窗口（Java 版游戏一般是 javaw.exe）。";
            return;
        }

        var coverTaskbar = WindowFullscreenCoverTaskbarCheckBox.IsChecked == true;
        if (!GameWindowFullscreen.TryApply(target.Handle, coverTaskbar, out var message))
        {
            WindowFullscreenStatusText.Text = message;
            return;
        }

        _settings.TargetProcessName = target.ProcessName;
        _settings.TargetWindowTitle = target.Title;
        SaveWindowFullscreenSettings();

        WindowFullscreenStatusText.Text = message;
        _plugin.Host.Log("[窗口全屏] " + message);

        // 游戏窗口刚铺满整块屏幕，把悬浮窗再抬一次，避免被新窗口挤下去
        _plugin.Host.ReassertOverlayTopmost();

        _plugin.Host.ShowToast(GameWindowFullscreen.IsApplied ? "游戏窗口已切成无边框全屏" : "窗口已处理");
    }

    private void WindowFullscreenRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (!GameWindowFullscreen.TryRestore(out var message))
        {
            WindowFullscreenStatusText.Text = message;
            return;
        }

        WindowFullscreenStatusText.Text = message;
        _plugin.Host.Log("[窗口全屏] " + message);
        _plugin.Host.ShowToast("游戏窗口已还原");
    }

    // ===================== 配置 =====================

    private void WindowFullscreenOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_windowFullscreenUiReady)
        {
            return;
        }

        _settings.CoverTaskbar = WindowFullscreenCoverTaskbarCheckBox.IsChecked == true;
        _settings.RestoreOnExit = WindowFullscreenRestoreOnExitCheckBox.IsChecked == true;
        SaveWindowFullscreenSettings();
    }

    private void SaveWindowFullscreenSettings()
    {
        try
        {
            _plugin.SaveSettings();
        }
        catch
        {
            // 存配置失败不该影响使用
        }
    }

    // ===================== 状态文字 =====================

    private void UpdateWindowFullscreenStatus()
    {
        if (WindowFullscreenStatusText == null)
        {
            return;
        }

        // 游戏自己关掉了：窗口没了，改动也跟着没了，把状态清干净
        if (GameWindowFullscreen.IsApplied && !GameWindowFullscreen.IsAppliedWindowAlive)
        {
            GameWindowFullscreen.ForgetAppliedState();
        }

        if (GameWindowFullscreen.IsApplied)
        {
            var title = string.IsNullOrWhiteSpace(GameWindowFullscreen.AppliedTitle)
                ? ""
                : "（" + GameWindowFullscreen.AppliedTitle + "）";
            WindowFullscreenStatusText.Text =
                "已生效：" + GameWindowFullscreen.AppliedProcessName + title +
                " 现在是无边框全屏。切回游戏看看悬浮窗有没有盖在上面；不需要了就点【还原窗口】。";
            return;
        }

        var count = WindowFullscreenTargetComboBox.Items.Count;
        WindowFullscreenStatusText.Text = count == 0
            ? "没找到带窗口的进程。先启动游戏，再点【刷新列表】。"
            : "还没改：游戏窗口保持原样。选好窗口后点【变无边框全屏】。";
    }

    // ===================== 退出时还原 =====================

    /// <summary>插件停用 / 程序退出时由插件调一下，把状态轮询停掉（还原窗口的事在插件那边做）。</summary>
    public void StopStatusTimer()
    {
        try
        {
            _windowFullscreenTimer?.Stop();
            _windowFullscreenTimer = null;
        }
        catch
        {
            // 停不掉也无所谓，进程马上就没了
        }
    }

    public WindowFullscreenPage(WindowFullscreenPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        // 初始化期间把闸门关上：设开关的 IsChecked 会触发 Checked/Unchecked → 存盘，
        // 那时另一个开关还没填上，会把它的值覆盖成默认（坑 53/58）。
        _windowFullscreenUiReady = false;
        try
        {
            InitializeWindowFullscreenUi();
        }
        finally
        {
            _windowFullscreenUiReady = true;
        }
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
