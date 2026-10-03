using System;
using System.Windows.Threading;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.FpsOverlay;

/// <summary>
/// 「FPS 悬浮窗」插件。
///
/// 数据来源是 native\FpsProbe 编出来的那个探针 DLL —— 它会往目标进程里手动映射一份，
/// 在 IAT 上把 SwapBuffers / wglSwapBuffers 换成自己的 thunk，每出一次帧就把计数加一，
/// 写进 Local\McoFpsProbe_&lt;pid&gt;。这里只负责：注入、读计数、算出 FPS、画在自己那个悬浮窗上。
///
/// 它**不碰画面**：不读后备缓冲、不改渲染状态、不做任何效果。跟「游戏动态模糊」区别就在这。
/// </summary>
public sealed class FpsOverlayPlugin : IPlugin
{
    private IPluginHost _host = null!;
    private FpsOverlaySettings _settings = new();
    private FpsOverlayPage? _page;
    private FpsOverlayWindow? _window;

    private FpsProbeReader? _reader;
    private DispatcherTimer? _sampleTimer;
    private int _pendingPid;
    private int _waitTicks;

    public string Id => "goldiamond.fpsoverlay";

    public string DisplayName => "FPS 悬浮窗";

    public IPluginHost Host => _host;

    public FpsOverlaySettings Settings => _settings;

    internal FpsProbeReader? Reader => _reader;

    public bool IsRunning => _sampleTimer != null;

    // ===================== 生命周期 =====================

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = PluginSettingsFile.Load<FpsOverlaySettings>(host.PluginDirectory);

        // 老配置里缺键会反序列化成 null，这里兜一遍（DEV-NOTES 坑 28）
        _settings.TargetProcessName ??= "";
        _settings.TargetWindowTitle ??= "";
        _settings.Prefix ??= "";
        _settings.Suffix ??= "";
        _settings.FontFamily ??= "Consolas";
        _settings.FontWeight ??= "Bold";
        _settings.Foreground ??= "#FFFFFF";
        _settings.ShadowColor ??= "#000000";
        _settings.BackgroundColor ??= "#000000";

        host.RegisterPage(new PluginPage
        {
            Title = DisplayName,
            Description = "把目标游戏的实时帧率显示在一个外观可自定义的悬浮窗上。"
                        + "做法是往你选定的游戏进程里注入一个只数帧的小探针 —— 它只记录 SwapBuffers "
                        + "被调用了多少次，不读像素、不改画面、不限帧；显示层是另一个独立窗口，不碰游戏。",
            ContentFactory = () =>
            {
                _page = new FpsOverlayPage(this);
                return _page;
            },
        });

        host.Log("[FPS 悬浮窗] 已加载。配置目录：" + host.PluginDirectory);
        host.Log("[FPS 悬浮窗] 探针 DLL：" + FpsProbeInjector.ResolveDllPath());
    }

    public void Shutdown()
    {
        try
        {
            _page?.StopStatusTimer();
            StopSampling();
            _window?.Shutdown();
            _window = null;
        }
        catch
        {
            // 退出流程里出问题也不能拦住关闭
        }
    }

    // ===================== 启停 =====================

    /// <summary>注入探针并开始显示。返回一句给人看的说明。</summary>
    public string StartProbe()
    {
        var pid = _settings.TargetProcessId;
        if (pid <= 0)
        {
            return "还没选目标进程。";
        }

        if (IsRunning && _reader != null)
        {
            return "已经在跑了。";
        }

        var message = FpsProbeInjector.EnsureInjected(pid);

        _pendingPid = pid;
        _waitTicks = 0;

        EnsureWindow();
        _window!.ApplySettings(_settings);
        if (_settings.OverlayVisible)
        {
            _window.ShowOverlay();
        }
        else
        {
            _window.HideOverlay();
        }

        StartSampling();

        // 注入返回时 DllMain 才刚起线程，探针里还有 120ms 的缓冲，
        // 所以共享内存不一定马上就在 —— 采样定时器会自己重试，这里不阻塞界面。
        return message;
    }

    public void StopProbe()
    {
        StopSampling();
        _window?.HideOverlay();
        _settings.OverlayVisible = false;
        SaveSettings();
    }

    private void StartSampling()
    {
        StopSampling();

        _sampleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.SampleIntervalMs, 50, 1000)),
        };
        _sampleTimer.Tick += OnSampleTick;
        _sampleTimer.Start();
    }

    private void StopSampling()
    {
        try
        {
            _sampleTimer?.Stop();
        }
        catch
        {
            // 忽略
        }

        _sampleTimer = null;
        _pendingPid = 0;
        _waitTicks = 0;

        _reader?.Dispose();
        _reader = null;
    }

    private void OnSampleTick(object? sender, EventArgs e)
    {
        // 还没连上探针：重试，最多 30 次（约 3 秒）
        if (_reader == null)
        {
            if (_pendingPid <= 0)
            {
                StopSampling();
                return;
            }

            if (FpsProbeReader.TryOpen(_pendingPid, out var reader) && reader != null)
            {
                _reader = reader;
                _host.Log("[FPS 悬浮窗] 已连上探针 pid=" + _pendingPid);
            }
            else if (++_waitTicks > 30)
            {
                var pid = _pendingPid;
                StopSampling();
                _window?.HideOverlay();
                _host.ShowToast("连不上探针的共享内存，看 %TEMP%\\mco_fpsprobe_" + pid + ".log");
            }

            return;
        }

        _reader.Tick(_settings.WindowMs);
        UpdateOverlayText();
    }

    // ===================== 窗口 =====================

    private void EnsureWindow()
    {
        if (_window != null)
        {
            return;
        }

        _window = new FpsOverlayWindow();
        _window.PositionChanged += (left, top) =>
        {
            _settings.Left = left;
            _settings.Top = top;
            SaveSettings();
        };
    }

    /// <summary>设置页改完外观后调这个：重新应用样式、按需显隐、顺手刷新一下采样间隔。</summary>
    public void RefreshOverlay()
    {
        if (_sampleTimer != null)
        {
            _sampleTimer.Interval =
                TimeSpan.FromMilliseconds(Math.Clamp(_settings.SampleIntervalMs, 50, 1000));
        }

        if (_window == null)
        {
            return;
        }

        _window.ApplySettings(_settings);

        if (_settings.OverlayVisible && IsRunning)
        {
            _window.ShowOverlay();
        }
        else
        {
            _window.HideOverlay();
        }

        UpdateOverlayText();
    }

    private void UpdateOverlayText()
    {
        if (_window == null || _reader == null)
        {
            return;
        }

        var decimals = Math.Clamp(_settings.Decimals, 0, 2);
        var text = (_settings.Prefix ?? "")
                 + _reader.CurrentFps.ToString("F" + decimals)
                 + (_settings.Suffix ?? "");

        _window.UpdateText(text);
    }

    // ===================== 配置 =====================

    public void SaveSettings()
    {
        try
        {
            PluginSettingsFile.Save(_host.PluginDirectory, _settings);
        }
        catch (Exception ex)
        {
            _host.Log("[FPS 悬浮窗] 保存设置失败：" + ex.Message);
        }
    }

    /// <summary>整份换掉配置（设置页的「恢复默认外观」用）。</summary>
    public void ReplaceSettings(FpsOverlaySettings fresh)
    {
        _settings = fresh;
    }
}
