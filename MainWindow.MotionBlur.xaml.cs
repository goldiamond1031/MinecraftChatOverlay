using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Services;
using MinecraftChatOverlay.Services.GameMotionBlur;

namespace MinecraftChatOverlay;

/// <summary>
/// 「游戏动态模糊」面板的逻辑（MainWindow 的另一个 partial 文件，
/// 这样就不用往 5000 多行的 MainWindow.xaml.cs 里继续堆代码）。
///
/// 面板里的控件都在 MainWindow.Modern.xaml 里用 x:Name 声明，同一个类里可以直接用。
/// </summary>
public partial class MainWindow
{
    private readonly GameMotionBlurService _motionBlur = new();
    private long _lastReinstallRequestAt;
    private bool _unhookRequestedByUser;
    private DispatcherTimer? _motionBlurTimer;
    private bool _motionBlurUiReady;
    /// <summary>窗口加载时调用（见 MainWindow_Loaded）。</summary>
    private void InitializeMotionBlurUi()
    {
        _motionBlurUiReady = false;

        var settings = _settings.MotionBlur;

        MotionBlurEnableCheckBox.IsChecked = settings.Enabled;
        MotionBlurStrengthSlider.Value = Math.Clamp(settings.Strength <= 0 ? 0.55 : settings.Strength, 0.05, 0.95);
        MotionBlurAutoInjectCheckBox.IsChecked = settings.AutoInject;
        _motionBlurUiReady = true;

        RefreshMotionBlurTargets(settings.TargetProcessId);

        // 共享内存结构体自检：C++ 和 C# 两边布局对不上的话，参数会写到错误的位置，
        // 这种情况要立刻报出来，而不是让用户点半天没反应。
        try
        {
            if (!_motionBlur.VerifyLayout(out var layoutError))
            {
                MotionBlurStatusText.Text = "共享内存结构体对不上：" + layoutError +
                                            "（common.h 与 MotionBlurControlBlock.cs 需要同步修改）";
            }
        }
        catch (Exception ex)
        {
            MotionBlurStatusText.Text = "共享内存控制块创建失败：" + ex.Message;
        }

        _motionBlurTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _motionBlurTimer.Tick += (_, _) => UpdateMotionBlurStatus();
        _motionBlurTimer.Start();

        Closing += (_, _) =>
        {
            try
            {
                _motionBlurTimer?.Stop();
                _motionBlur.Dispose();
            }
            catch
            {
                // 关窗时别影响退出流程
            }
        };

        if (settings.AutoInject && !string.IsNullOrWhiteSpace(settings.TargetProcessName))
        {
            _ = AutoInjectMotionBlurAsync(settings.TargetProcessName);
        }
    }

    // ===================== 列表与注入 =====================

    private void RefreshMotionBlurTargets(int preferProcessId)
    {
        try
        {
            var targets = _motionBlur.ListTargets();
            MotionBlurProcessComboBox.ItemsSource = targets;

            if (targets.Count == 0)
            {
                MotionBlurStatusText.Text = "没找到带窗口的进程。先启动游戏，再点【刷新列表】。";
                return;
            }

            var index = targets.FindIndex(p => p.Id == preferProcessId);
            if (index < 0 && !string.IsNullOrWhiteSpace(_settings.MotionBlur.TargetProcessName))
            {
                index = targets.FindIndex(p => string.Equals(p.Name, _settings.MotionBlur.TargetProcessName,
                                                              StringComparison.OrdinalIgnoreCase));
            }

            MotionBlurProcessComboBox.SelectedIndex = index >= 0 ? index : 0;
        }
        catch (Exception ex)
        {
            MotionBlurStatusText.Text = "枚举进程失败：" + ex.Message;
        }
    }

    private void MotionBlurRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshMotionBlurTargets(_settings.MotionBlur.TargetProcessId);
    }

    private void MotionBlurInjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (MotionBlurProcessComboBox.SelectedItem is not GameProcessInfo target)
        {
            MessageBox.Show(this, "先在列表里选一个游戏进程。", "游戏动态模糊",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _unhookRequestedByUser = false;
            MotionBlurInjectButton.IsEnabled = false;
            MotionBlurStatusText.Text = "正在注入 " + target.Name + "（PID " + target.Id + "）…";

            var status = _motionBlur.Inject(target.Id, _settings.MotionBlur.HookDllPath);

            _settings.MotionBlur.TargetProcessId = target.Id;
            _settings.MotionBlur.TargetProcessName = target.Name;
            SaveMotionBlurSettings();

            if (status.HookInstalled)
            {
                // 注入成功后把界面上的参数推过去，游戏那边立刻生效
                PushMotionBlurParameters();
                syncMotionBlurSettingsEnabled();
                UpdateMotionBlurStatus();

                if (status.PresentCount == 0)
                {
                    // 钩子挂上了但还没出帧（多半在加载画面），补一句提示，别让详细状态被顶掉
                    MotionBlurStatusText.Text = "钩子已装到 " + target.Name + "（PID " + target.Id +
                                                "），但还没出帧（进游戏/切一下窗口就会开始）。" +
                                                Environment.NewLine + MotionBlurStatusText.Text;
                }
            }
            else
            {
                MotionBlurStatusText.Text = "注入完成了，但钩子没挂上。" + Environment.NewLine + DescribeHookFailure();
            }

        }
        catch (Exception ex)
        {
            MotionBlurStatusText.Text = "注入失败：" + ex.Message;
            MessageBox.Show(this, ex.Message, "注入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            MotionBlurInjectButton.IsEnabled = true;
        }
    }

    private void syncMotionBlurSettingsEnabled()
    {
        _settings.MotionBlur.Enabled = MotionBlurEnableCheckBox.IsChecked == true;
    }

    private async Task AutoInjectMotionBlurAsync(string processName)
    {
        try
        {
            var target = await Task.Run(() => _motionBlur.ListTargets()
                .FirstOrDefault(p => string.Equals(p.Name, processName, StringComparison.OrdinalIgnoreCase)));

            if (target == null)
            {
                return;
            }

            var status = await Task.Run(() => _motionBlur.Inject(target.Id, _settings.MotionBlur.HookDllPath));

            await Dispatcher.BeginInvoke(new Action(() =>
            {
                _settings.MotionBlur.TargetProcessId = target.Id;
                PushMotionBlurParameters();
                UpdateMotionBlurStatus();
                MotionBlurStatusText.Text = status.HookInstalled
                    ? "已自动注入 " + target.Name + "（PID " + target.Id + "）"
                    : "自动注入完成，但钩子没挂上。";
            }));
        }
        catch (Exception ex)
        {
            await Dispatcher.BeginInvoke(new Action(() =>
            {
                MotionBlurStatusText.Text = "自动注入失败：" + ex.Message;
            }));
        }
    }

    // ===================== 参数 =====================

    private bool IsMotionBlurOn()
    {
        return MotionBlurEnableCheckBox.IsChecked == true;
    }

    private void PushMotionBlurParameters()
    {
        try
        {
            SyncMotionBlurSettingsFromUi();
            _motionBlur.SetEnabled(IsMotionBlurOn(), MotionBlurStrengthSlider.Value);
        }
        catch (Exception ex)
        {
            MotionBlurStatusText.Text = "下发参数失败：" + ex.Message;
        }
    }

    private void SyncMotionBlurSettingsFromUi()
    {
        var settings = _settings.MotionBlur;
        settings.Enabled = IsMotionBlurOn();
        settings.Strength = MotionBlurStrengthSlider.Value;
        settings.AutoInject = MotionBlurAutoInjectCheckBox.IsChecked == true;

        if (MotionBlurProcessComboBox.SelectedItem is GameProcessInfo target)
        {
            settings.TargetProcessId = target.Id;
            settings.TargetProcessName = target.Name;
        }
    }

    private void SaveMotionBlurSettings()
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

    private void MotionBlurEnableCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_motionBlurUiReady)
        {
            return;
        }

        PushMotionBlurParameters();
        SaveMotionBlurSettings();

        if (_motionBlur.TargetProcessId > 0)
        {
            MotionBlurStatusText.Text = IsMotionBlurOn()
                ? "帧混合已开启，游戏画面立刻生效"
                : "帧混合已关闭，游戏回到原生画面";
        }
    }

    private void MotionBlurStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_motionBlurUiReady)
        {
            return;
        }

        _settings.MotionBlur.Strength = e.NewValue;

        if (_motionBlur.TargetProcessId > 0)
        {
            try
            {
                _motionBlur.SetStrength(e.NewValue);
            }
            catch
            {
                // 拖滑块时的偶发失败忽略掉，下一次状态刷新会体现出来
            }
        }
    }

    private void MotionBlurAutoInjectCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_motionBlurUiReady)
        {
            return;
        }

        SyncMotionBlurSettingsFromUi();
        SaveMotionBlurSettings();
    }
    private void MotionBlurReinstallButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _unhookRequestedByUser = false;
            _lastReinstallRequestAt = Environment.TickCount64;
            _motionBlur.RequestReinstall();
            MotionBlurStatusText.Text = "已请游戏里的 DLL 重新装钩子，稍等一两秒…";
        }
        catch (Exception ex)
        {
            MotionBlurStatusText.Text = "重装钩子失败：" + ex.Message;
        }
    }
    private void MotionBlurUnhookButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 记住"是用户主动卸载的"：不然下面的自动重装会立刻把钩子装回去，
            // 看起来就像卸载没生效。
            _unhookRequestedByUser = true;
            _motionBlur.Unhook();
            MotionBlurStatusText.Text = "已请求卸载钩子。游戏里的 SwapBuffers 会还原成原生的，不用重启游戏。";
        }
        catch (Exception ex)
        {
            MotionBlurStatusText.Text = "卸载失败：" + ex.Message;
        }
    }

    // ===================== 状态刷新 =====================

    /// <summary>
    /// 把界面上的"开 / 强度"同步到控制块。
    /// 注入时只推一次参数，之后要是被别的东西改掉（或换了控制块），
    /// 光看状态是发现不了的 —— 于是会出现"显示接管成功但一直 0 次混合"。
    /// 只在真的不一致时才写，免得每 700ms 都去动一次 Seq。
    /// </summary>
    private void KeepMotionBlurParametersInSync(MotionBlurStatus status)
    {
        try
        {
            var wantEnabled = IsMotionBlurOn();
            var wantStrength = (float)MotionBlurStrengthSlider.Value;
            var currentStrength = status.StrengthMilli / 1000f;

            if (status.Enabled != wantEnabled || Math.Abs(currentStrength - wantStrength) > 0.02f)
            {
                _motionBlur.SetEnabled(wantEnabled, wantStrength);
            }
        }
        catch
        {
            // 同步失败下一拍会再试
        }
    }

    /// <summary>
    /// 目标进程活着、但控制块里说钩子不在。
    /// 先分清两种情况：
    ///   * DLL 不在进程里 → 是没注入（或游戏重启过），直接提示重新注入；
    ///   * DLL 已经在进程里 → 多半是之前【卸载钩子】过。手动映射不会重跑入口点，
    ///     所以再点注入没有用；改成请 DLL 自己把钩子装回去。
    /// </summary>
    private void HandleMissingHook()
    {
        var pid = _motionBlur.TargetProcessId;
        var dllName = System.IO.Path.GetFileName(_motionBlur.HookDllPath);

        if (_unhookRequestedByUser)
        {
            MotionBlurStatusText.Text = "已卸载钩子，游戏回到原生画面。" + Environment.NewLine +
                                        "想再开就点【注入并接管】或【重装钩子】。";
            return;
        }

        if (!GameProcessInjector.IsModuleLoaded(pid, dllName))
        {
            MotionBlurStatusText.Text = "目标进程在跑，但里面没有钩子 DLL。" + Environment.NewLine +
                                        "· 游戏重启过 → 重新点一次【注入并接管】" + Environment.NewLine +
                                        "· 点了几次都没用 → 看游戏目录里的 gmb_hook_*.log";
            return;
        }

        // DLL 在，但钩子没装上：请它重装（最多 3 秒一次）
        if (Environment.TickCount64 - _lastReinstallRequestAt > 3000)
        {
            _lastReinstallRequestAt = Environment.TickCount64;
            try
            {
                _motionBlur.RequestReinstall();
                MotionBlurStatusText.Text = "钩子不在，但 DLL 还在游戏进程里（上次卸载过？）。" +
                                            "已请它重新装钩子，稍等一两秒…";
                return;
            }
            catch (Exception ex)
            {
                MotionBlurStatusText.Text = "请求重装钩子失败：" + ex.Message;
                return;
            }
        }

        MotionBlurStatusText.Text = "正在请游戏里的 DLL 重新装钩子…";
    }

    private void UpdateMotionBlurStatus()
    {
        if (!_motionBlurUiReady)
        {
            return;
        }

        if (_motionBlur.TargetProcessId <= 0)
        {
            return;
        }

        if (!_motionBlur.IsTargetAlive())
        {
            MotionBlurStatusText.Text = "目标进程（PID " + _motionBlur.TargetProcessId +
                                        "）已经退出了。换一个进程重新注入即可。";
            return;
        }

        try
        {
            var status = _motionBlur.GetStatus();
            if (!status.HookInstalled)
            {
                HandleMissingHook();
                return;
            }

            // 保证游戏那边的开关/强度和界面一致
            KeepMotionBlurParametersInSync(status);

            var text = "钩子=已安装";
            switch (status.DeviceKind)
            {
                case MotionBlurDeviceKind.OpenGL:
                    text += "  设备=OpenGL（Java 版）";
                    break;
                default:
                    text += "  设备=" + status.DeviceKind;
                    break;
            }

            if (status.Width > 0)
            {
                text += "  画面=" + status.Width + "x" + status.Height;
            }

            if (status.Fps > 0.5)
            {
                text += "  " + status.Fps.ToString("F0") + " 帧/秒";
            }

            if (status.BlendFrames > 0)
            {
                text += "，当前平均 " + status.BlendFrames + " 帧";
            }

            text += "  帧混合=" + (status.Enabled ? "开" : "关");
            text += Environment.NewLine;
            text += "出帧 " + status.PresentCount + " 次，其中做了帧混合 " + status.BlendCount + " 次";

            if (status.BlendCount == 0 && status.PresentCount > 0)
            {
                text += status.Enabled
                    ? "  —— 在出帧但一次都没混合，检查游戏目录里的 gmb_hook_*.log"
                    : "  —— 帧混合开关是关的，所以不会混合（打开上面【开启帧混合】即可）";
            }

            if (!string.IsNullOrWhiteSpace(status.StatusText))
            {
                text += "  —— " + status.StatusText;
            }

            MotionBlurStatusText.Text = text;
        }
        catch (Exception ex)
        {
            MotionBlurStatusText.Text = "读状态失败：" + ex.Message;
        }
    }

    private string DescribeHookFailure()
    {
        var log = _motionBlur.ReadHookLog(12);
        if (string.IsNullOrWhiteSpace(log))
        {
            return "钩子 DLL 没留下日志 —— 它可能刚进去（还没开始写日志）就崩了。";
        }

        return "钩子日志末尾：" + Environment.NewLine + log;
    }
}