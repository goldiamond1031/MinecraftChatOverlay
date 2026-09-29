using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Services;
using MinecraftChatOverlay.Services.BorderlessWindow;

namespace MinecraftChatOverlay;

/// <summary>
/// 「窗口全屏」面板的逻辑（MainWindow 的另一个 partial 文件）。
///
/// 干的事就一件：把游戏窗口改成无边框全屏，让悬浮窗能盖在游戏画面上。
/// 真正的窗口操作在 <see cref="GameWindowFullscreen"/> 里，这里只负责界面和配置。
/// </summary>
public partial class MainWindow
{
    private bool _windowFullscreenUiReady;
    private DispatcherTimer? _windowFullscreenTimer;

    /// <summary>窗口加载时调用（见 MainWindow_Loaded）。</summary>
    private void InitializeWindowFullscreenUi()
    {
        _windowFullscreenUiReady = false;

        var settings = _settings.WindowFullscreen;
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

            var settings = _settings.WindowFullscreen;

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

        _settings.WindowFullscreen.TargetProcessName = target.ProcessName;
        _settings.WindowFullscreen.TargetWindowTitle = target.Title;
        SaveWindowFullscreenSettings();

        WindowFullscreenStatusText.Text = message;
        AppendDebugLog("[窗口全屏] " + message);

        // 游戏窗口刚铺满整块屏幕，把悬浮窗再抬一次，避免被新窗口挤下去
        _overlay?.ReassertTopmost();

        ShowToast(GameWindowFullscreen.IsApplied ? "游戏窗口已切成无边框全屏" : "窗口已处理");
    }

    private void WindowFullscreenRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (!GameWindowFullscreen.TryRestore(out var message))
        {
            WindowFullscreenStatusText.Text = message;
            return;
        }

        WindowFullscreenStatusText.Text = message;
        AppendDebugLog("[窗口全屏] " + message);
        ShowToast("游戏窗口已还原");
    }

    // ===================== 配置 =====================

    private void WindowFullscreenOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!_windowFullscreenUiReady)
        {
            return;
        }

        _settings.WindowFullscreen.CoverTaskbar = WindowFullscreenCoverTaskbarCheckBox.IsChecked == true;
        _settings.WindowFullscreen.RestoreOnExit = WindowFullscreenRestoreOnExitCheckBox.IsChecked == true;
        SaveWindowFullscreenSettings();
    }

    private void SaveWindowFullscreenSettings()
    {
        try
        {
            SettingsService.Save(_settings);
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

    /// <summary>关闭软件时调用（见 Window_Closing）。</summary>
    private void RestoreGameWindowOnExit()
    {
        try
        {
            _windowFullscreenTimer?.Stop();
        }
        catch
        {
        }

        try
        {
            if (!_settings.WindowFullscreen.RestoreOnExit || !GameWindowFullscreen.IsApplied)
            {
                return;
            }

            if (GameWindowFullscreen.TryRestore(out var message))
            {
                AppendDebugLog("[窗口全屏] 退出时自动还原：" + message);
            }
        }
        catch
        {
            // 退出流程里出问题也不能拦住关闭
        }
    }
}