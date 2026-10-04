using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.KeyDisplay;

/// <summary>
/// 「按键显示」插件。
///
/// 做的事：按固定节奏查一遍你配置的那几个键现在有没有按下，把结果画到键位悬浮窗上。
///
/// ⚠ 只读，不监听：查按键的两种方式都**不拦截按键、不记录历史、不落盘**，
///    只读「此刻按着没有」这一个状态位（详见 <see cref="KeyInputMode"/> 与 <see cref="HookKeyStateSource"/>）。
///    默认走 GetAsyncKeyState 轮询 —— 对系统输入链路零介入；
///    加了一条可选的 WH_KEYBOARD_LL 低级钩子通道，专门用来读走 Raw Input 的游戏（绝区零这类），
///    钩子只观察、不吞键，且由用户主动开启。
/// </summary>
public sealed class KeyDisplayPlugin : IPlugin
{
    public const string PluginId = "goldiamond.keydisplay";

    /// <summary>插件页的副标题。</summary>
    private const string PageDescription = "高度自定义的按键显示，可选中单个按键进行设置";

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>取当前进程的主令牌。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    /// <summary>查令牌里那个"是不是提权了"的标志位。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation,
        int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    /// <summary>
    /// 当前进程是不是以管理员权限在跑。
    ///
    /// ⚠ 这个值在插件运行期间**不会变**（提权是进程级的，改不了），所以算一次就缓存。
    ///   设置页的提示文案要用它 —— 钩子模式下不提权是读不到绝区零的，得让用户知道。
    /// </summary>
    internal static bool IsProcessElevated
    {
        get
        {
            if (_elevatedChecked)
            {
                return _elevatedCached;
            }

            try
            {
                if (OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
                {
                    try
                    {
                        if (GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _))
                        {
                            _elevatedCached = elevated != 0;
                        }
                    }
                    finally
                    {
                        CloseHandle(token);
                    }
                }
            }
            catch
            {
            }

            _elevatedChecked = true;
            return _elevatedCached;
        }
    }

    private static bool _elevatedCached;
    private static bool _elevatedChecked;

    private const uint KeyeventfKeyup = 0x0002;

    /// <summary>自检专用的键：右 Shift —— 无副作用，游戏里基本不用。</summary>
    private const int SelfTestKey = 0xA1;

    private IPluginHost? _host;
    private KeyOverlayWindow? _window;

    /// <summary>
    /// 正在退出（宿主关窗 → 插件管理器调 Shutdown）。
    ///
    /// 为什么需要这个标志：悬浮窗的 Closing 被拦成「只藏不关」（防用户从任务栏右键把窗口
    /// 关废 —— WPF 不允许 Show 一个已 Close 的窗口）。但那个拦截**不能**在退出流程里生效：
    /// 宿主用的 ShutdownMode 是默认的 OnLastWindowClose，只要还有窗口没关，应用就不退出 →
    /// **宿主窗口关了、进程却驻留在后台**（2026-10-04 用户报的就是这个）。
    /// 所以 Shutdown 先把标志立起来，Closing 见到它就放行，让窗口真关。
    /// </summary>
    private bool _shuttingDown;
    private KeyDisplayPage? _page;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _cycleTimer;
    private DateTime _cycleStart = DateTime.UtcNow;
    private double _lastCycleProgress = -1;

    /// <summary>当前生效的钩子源（走轮询模式时是 null）。健康检查要用。</summary>
    private HookKeyStateSource? _hookSource;

    public string Id => PluginId;

    public string DisplayName => "按键显示";

    /// <summary>插件自己那本设置（页面也用它）。</summary>
    public KeyDisplaySettings Settings { get; private set; } = new();

    /// <summary>插件自己的目录（配置、日志都在这儿）。</summary>
    public string PluginDirectory => _host?.PluginDirectory ?? "";

    // ===================== 生命周期 =====================

    public void Initialize(IPluginHost host)
    {
        _host = host;

        try
        {
            Settings = PluginSettingsFile.Load<KeyDisplaySettings>(host.PluginDirectory);
        }
        catch
        {
            Settings = new KeyDisplaySettings();
        }

        if (Settings.Keys.Count == 0)
        {
            foreach (var cell in KeyDisplaySettings.DefaultLayout())
            {
                Settings.Keys.Add(cell);
            }
        }

        ClampSavedPosition();

        // 按键读取通道：按设置建好（轮询或钩子）
        ApplyInputMode();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(8, Settings.RefreshMs)),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // 彩色循环的推进定时器。
        //
        // ⚠ 间隔**必须跟配置页画布的 `_liveTimer` 一致（40ms）**，否则两条路径的流畅度天差地别：
        // 画布丝滑连续、悬浮窗一跳一跳（每格 60°），并排一看就像悬浮窗坏了。
        // 早先这里是 1 秒 + `DispatcherPriority.Background`（最低档），系统一忙还会被继续往后拖，
        // 表现就是"隔几秒才刷新一下颜色"（gold_ 报过）。
        //
        // 开销不用担心：`SetCycleProgress` 只给几个元素换画笔、不重建视觉树，25fps 毫无压力。
        _cycleStart = DateTime.UtcNow;
        _cycleTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _cycleTimer.Tick += (_, _) => TickCycle();
        _cycleTimer.Start();

        host.RegisterPage(new PluginPage
        {
            Title = "按键显示",
            Description = PageDescription,
            ContentFactory = () =>
            {
                var page = new KeyDisplayPage(this);
                _page = page;
                return page;
            },
            StatusText = BuildStatus,
        });

        LogLine($"已装载。按键数={Settings.Keys.Count} 刷新={Settings.RefreshMs}ms 穿透={Settings.ClickThrough}");
        LogLine($"按键读取方式：{VirtualKeys.Source.Name}");

        // 页面 XAML 是运行期解析的，编译过不代表能解析（DEV-NOTES 坑 34）
        try
        {
            _ = new KeyDisplayPage(this);
            LogLine("页面预检通过：自绘 XAML 解析正常。");
        }
        catch (Exception ex)
        {
            LogLine("页面预检失败（打开插件页会白屏）：" + ex);
        }

        PreflightHostStyles();

        RunColorDialogProbe();

        // 先把窗口摆出来，自检才能顺带验"按键会让悬浮窗真的变色"
        SyncVisibility();

        RunSelfTestIfRequested();

        // 开发期用：验"循环定时器在真实消息循环里到底有没有在推颜色"
        RunCycleTraceIfRequested();

        // 开发期用：把配置页渲染成 PNG，用来目视核对排版（见方法注释）
        RunUiSnapshotIfRequested();

        host.Log($"[按键显示] 已装载，{Settings.Keys.Count} 个按键，刷新 {Settings.RefreshMs}ms");
    }

    // ===================== 按键读取通道 =====================

    /// <summary>
    /// 按 <see cref="KeyDisplaySettings.InputMode"/> 建好当前该用的读取通道。
    ///
    /// ⚠ 装钩子必须在这个线程（UI 线程）上做 —— 低级钩子靠宿主线程的消息循环派发回调，
    ///   在没有消息循环的线程上装会"装成功但永远收不到回调"。
    ///   <see cref="Initialize"/> 就是在 UI 线程被宿主调的，所以这里直接装没问题。
    ///
    /// 幂等：重复调用只会在模式没变时不做事，模式变了才换。
    /// </summary>
    private void ApplyInputMode()
    {
        try
        {
            var wantHook = Settings.InputMode == KeyInputMode.Hook;

            // 模式没变，什么都不做（别每次设置变动都卸了重装，那会白白摘掉一次钩子）
            if (wantHook == (_hookSource is not null))
            {
                return;
            }

            if (wantHook)
            {
                var hook = new HookKeyStateSource
                {
                    // 把日志能力注入进去，钩子源自己不认识宿主
                    ReturnToCallerLog = msg => { try { LogLine(msg); } catch { } },
                };

                if (hook.Install())
                {
                    _hookSource = hook;
                    VirtualKeys.SwitchSource(hook);

                    LogLine(IsProcessElevated
                        ? "已切到键盘钩子模式。当前进程是管理员权限，游戏里应该能读到了。"
                        : "已切到键盘钩子模式。⚠ 当前进程**不是**管理员权限 —— "
                          + "绝区零这类提权运行的进程，它的按键读不到。要用管理员身份重启宿主。");
                }
                else
                {
                    // 装失败就老实退回轮询，并且把设置改回去 ——
                    // 不然用户看到开关是"钩子"、实际在轮询，会莫名其妙
                    hook.Dispose();
                    _hookSource = null;
                    VirtualKeys.SwitchSource(new PollingKeyStateSource());
                    Settings.InputMode = KeyInputMode.Polling;

                    LogLine($"键盘钩子装不上（{hook.LastError}），已退回轮询模式。");
                }
            }
            else
            {
                _hookSource = null;
                VirtualKeys.SwitchSource(new PollingKeyStateSource());
                LogLine("已切回 GetAsyncKeyState 轮询模式。");
            }
        }
        catch (Exception ex)
        {
            LogLine("切换按键读取方式失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 钩子模式下的定期健康检查。
    ///
    /// 为什么需要：系统给低级钩子回调 300ms 超时，超时就**静默摘掉**钩子且不通知。
    /// 表现是"用着用着突然不亮了"，还查不出原因。所以每 2 秒探一次，
    /// 发现句柄失效就重装。<see cref="HookKeyStateSource.EnsureAlive"/> 里说明得更细。
    /// </summary>
    private void CheckHookHealth()
    {
        var hook = _hookSource;

        if (hook is null || !hook.IsActive)
        {
            return;
        }

        hook.EnsureAlive();
    }

    /// <summary>钩子模式下的定期健康检查间隔（毫秒）。2 秒足够快，开销也可以忽略。</summary>
    private const int HookHealthCheckMs = 2000;

    private DateTime _nextHookCheck = DateTime.UtcNow;

    public void Shutdown()
    {
        // ★ 第一件事：放行窗口关闭（见 _shuttingDown 的注释）。
        // 必须放在最前面 —— 后面 _window?.Close() 要靠它绕过 Closing 拦截。
        _shuttingDown = true;

        // 先卸钩子 —— 这是最要紧的一步。留着一个钩子比留一个定时器危险得多：
        // 它挂在系统输入链路上，宿主都关了它还在跑的话，全系统按键都要过一遍我们的回调。
        try
        {
            _hookSource?.Dispose();
            _hookSource = null;

            // 顺手把全局源换回轮询（无状态）。这样万一还有别的地方在跑，
            // 也不会调到一个已经 Dispose 的钩子源上。
            VirtualKeys.SwitchSource(new PollingKeyStateSource());
        }
        catch
        {
        }

        try { _timer?.Stop(); } catch { }
        _timer = null;

        try { _cycleTimer?.Stop(); } catch { }
        _cycleTimer = null;

        try { _page?.StopTimers(); } catch { }
        _page = null;

        // CPS 采样线程也要收掉，别留着后台线程
        try { _cps.Dispose(); } catch { }

        try { _window?.Close(); } catch { }
        _window = null;

        LogLine("已卸载。");
        _host?.Log("[按键显示] 已卸载。");
        _host = null;
    }

    // ===================== 刷新循环 =====================

    private void Tick()
    {
        var window = _window;

        // 钩子模式的探活：每 2 秒查一次，被系统摘掉就重装。
        // 放在这个位置（而不是单开一个定时器）是故意的 —— 这个 Tick 本来就在跑，
        // 加个时间比较的开销是零，不用为了探活多养一个定时器。
        if (_hookSource is not null && DateTime.UtcNow >= _nextHookCheck)
        {
            _nextHookCheck = DateTime.UtcNow.AddMilliseconds(HookHealthCheckMs);
            CheckHookHealth();
        }

        // 防出屏：每帧钳一次窗口位置。正常时候是同值赋值（不触发事件、零开销），
        // 只在「分辨率变了/拔了副屏/窗口尺寸变了」导致出界时把它拉回屏幕内 ——
        // 16ms 一次的几条数值比较，可以忽略不计。
        try
        {
            _window?.ClampToVirtualScreen();
        }
        catch
        {
        }

        // 没显示就别白刷（省一点 CPU）；下次显示前会 InvalidateStates 重刷一遍
        if (window is null || !window.IsVisible)
        {
            return;
        }

        try
        {
            window.Refresh(Settings);
            UpdateCps();
        }
        catch (Exception ex)
        {
            LogLine("刷新异常：" + ex.Message);
        }
    }

    // ===================== 彩色循环 =====================

    /// <summary>
    /// 推进彩色循环：按真实流逝的时间算出现在该转到哪，交给悬浮窗刷颜色。
    ///
    /// **为什么按真实时间算而不是每次 +30°**：`DispatcherTimer` 的实际间隔会被消息循环拉长，
    /// 按次数累加的话界面一忙循环就变慢，看着一顿一顿的。按时间算就始终匀速。
    ///
    /// 这里只读 <see cref="CycleProgressNow"/>、从不写基准 —— 时间轴归插件所有，
    /// 悬浮窗只是"使用者"（见 <see cref="KeyOverlayWindow.SetCycleProgress"/>）。
    /// </summary>
    private void TickCycle()
    {
        var window = _window;

        if (window is null || !window.IsVisible || !Settings.AnyColorCycle)
        {
            return;
        }

        try
        {
            var progress = CycleProgressNow;

            // 40ms 一刷，但「循环一圈」最长能设到 60 秒 —— 那一帧也就动 0.24°，肉眼根本看不出。
            // 进度几乎没变就别白刷了；变化够一格再推（1/360 ≈ 每度推一次，够细腻也不浪费）。
            if (Math.Abs(progress - _lastCycleProgress) < 1.0 / 360.0)
            {
                return;
            }

            _lastCycleProgress = progress;
            window.SetCycleProgress(progress, Settings);
        }
        catch (Exception ex)
        {
            LogLine("彩色循环刷新异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 当前循环进度（0~1 = 沿 RGB 光谱走完一圈的比例）。速度来自「循环一圈」设置。
    ///
    /// **这是全插件唯一的时间轴** —— 悬浮窗、配置页画布、每一处开了循环的颜色，
    /// 拿到的都是这同一个值。所以"什么时候打开开关"完全不影响相位：
    /// 12:00 开的和 12:05 开的，在 12:06 这一刻颜色是一样的。
    /// 基准 <see cref="_cycleStart"/> 只在插件装载时定一次，**开关动作一律不动它**。
    /// </summary>
    internal double CycleProgressNow
    {
        get
        {
            var seconds = Math.Clamp(Settings.CycleSeconds, 1, 60);
            var elapsed = (DateTime.UtcNow - _cycleStart).TotalSeconds;

            if (elapsed < 0)
            {
                elapsed = 0;
            }

            return (elapsed / seconds) % 1.0;
        }
    }

    // ===================== CPS =====================

    private readonly CpsSampler _cps = new();

    /// <summary>
    /// 告诉采样器现在要数哪几个键，然后把数字刷到悬浮窗上。
    ///
    /// 真正的采样在 <see cref="CpsSampler"/> 的**后台线程**里跑（5 毫秒一次）——
    /// 这里只负责"要不要采样"和"把结果取出来显示"。
    /// 采样不能挂在 UI 线程的定时器上：那个间隔受消息循环限制、还会被悬浮窗刷新拖慢，
    /// 一粗就漏，表现是"点得快反而数得少"。
    /// </summary>
    private void UpdateCps()
    {
        var wantsLeft = false;
        var wantsRight = false;

        foreach (var cell in Settings.Keys)
        {
            if (!cell.ShowCps)
            {
                continue;
            }

            if (cell.VirtualKey == 0x01)
            {
                wantsLeft = true;
            }
            else if (cell.VirtualKey == 0x02)
            {
                wantsRight = true;
            }
        }

        _cps.SetWanted(wantsLeft, wantsRight);
        _window?.UpdateCps(_cps.LeftCps, _cps.RightCps);
    }

    /// <summary>自检用：当前刷新定时器的实际间隔（毫秒）。</summary>
    internal double TimerIntervalMs => _timer?.Interval.TotalMilliseconds ?? 0;

    /// <summary>所有设置恢复成默认值（页面上的「恢复默认」调这个）。</summary>
    public void ResetToDefaults()
    {
        Settings = new KeyDisplaySettings();

        foreach (var cell in KeyDisplaySettings.DefaultLayout())
        {
            Settings.Keys.Add(cell);
        }

        _cps.SetWanted(false, false);

        SaveSettings();
        OnSettingsChanged();

        LogLine("设置已恢复为默认值。");
    }

    // ===================== 悬浮窗 =====================

    private KeyOverlayWindow CreateWindow()
    {
        var window = new KeyOverlayWindow();

        // 关闭只许"藏起来"：真 Close 掉之后 WPF 不允许再 Show 同一个实例
        // （用户从任务栏右键"关闭窗口"→ 拦成 Hide，实例保留）。
        // ⚠ 但退出流程里必须放行，否则宿主关窗后进程退不掉（见 _shuttingDown 的注释）。
        window.Closing += (_, args) =>
        {
            if (_shuttingDown)
            {
                return;   // 退出中：真关，让应用能正常结束
            }

            args.Cancel = true;
            window.Hide();
        };

        window.LocationChanged += (_, _) =>
        {
            try
            {
                window.RememberCurrentPosition(Settings);
                SaveSettings();
            }
            catch
            {
            }
        };

        window.Rebuild(Settings);
        window.ClickThrough = Settings.ClickThrough;
        window.ClampEnabled = Settings.ClampToScreen;

        // OBS 直播模式：窗口创建流程的末尾（DetachFromOwner 在 OnSourceInitialized 里已跑过）
        // 摘掉 TOOLWINDOW，让 OBS 能枚举到这个窗口
        window.SetObsMode(Settings.ObsMode);
        window.ApplyPosition(Settings);
        return window;
    }

    /// <summary>让窗口的存在状态和设置对齐（该显示就显示，该藏就藏）。</summary>
    private void SyncVisibility()
    {
        var wantShow = Settings.Enabled && Settings.Keys.Count > 0;

        if (wantShow)
        {
            if (_window is null)
            {
                _window = CreateWindow();
            }

            if (!_window.IsVisible)
            {
                _window.InvalidateStates();
                try
                {
                    _window.Show();
                    _window.DetachFromOwner();

                    LogLine(string.Format(
                        "悬浮窗已显示：系统可见={0} 尺寸 {1:0}×{2:0} 位置 {3:0},{4:0}",
                        _window.IsReallyVisible(), _window.ActualWidth, _window.ActualHeight,
                        _window.Left, _window.Top));
                }
                catch (Exception ex)
                {
                    LogLine("显示悬浮窗失败：" + ex.Message);
                }
            }
        }
        else if (_window is not null && _window.IsVisible)
        {
            try { _window.Hide(); } catch { }
        }
    }

    /// <summary>
    /// 只更新悬浮窗**外观**（页面上拖滑块走这条）。不重建视觉树，所以能跟手。
    /// 结构变了（增删键、改格子大小）才需要走 <see cref="OnSettingsChanged"/>。
    /// </summary>
    public void ApplyAppearanceNow()
    {
        try
        {
            _window?.ApplyAppearance(Settings);
        }
        catch (Exception ex)
        {
            LogLine("应用外观失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 同步那些**不属于外观**的设置：显示开关、鼠标穿透、刷新间隔。
    ///
    /// 为什么单独有这么一条：页面上拖滑块走的是"只刷外观"的轻路径（不重建视觉树，才跟手），
    /// 但上面这三项改外观根本管不着 —— 显示开关要 Show/Hide，穿透要改 Win32 扩展样式位，
    /// 刷新率要改定时器间隔。早先只调 ApplyAppearanceNow，结果就是"把开关关了没反应"。
    /// 这几个都只是设几个属性，调一次开销可以忽略，所以可以跟着滑块一起走。
    /// </summary>
    public void SyncWindowState()
    {
        try
        {
            SyncVisibility();

            if (_window is not null)
            {
                _window.ClickThrough = Settings.ClickThrough;
                _window.ClampEnabled = Settings.ClampToScreen;
                _window.SetObsMode(Settings.ObsMode);
                _window.UpdateTransparent();
            }

            var interval = TimeSpan.FromMilliseconds(Math.Max(8, Settings.RefreshMs));
            if (_timer is not null && _timer.Interval != interval)
            {
                _timer.Interval = interval;
            }
        }
        catch (Exception ex)
        {
            LogLine("同步窗口状态失败：" + ex.Message);
        }
    }

    /// <summary>设置变了：重建窗口内容 + 对齐显示状态 + 调刷新节奏 + 换读取通道。</summary>
    public void OnSettingsChanged()
    {
        try
        {
            // 读取方式可能刚被改过，先对齐通道（幂等，模式没变时不做事）
            ApplyInputMode();

            var window = _window;
            if (window is not null)
            {
                window.Rebuild(Settings);
                window.InvalidateStates();
            }

            // 穿透 / 显示开关 / 刷新率统一走那一条，别在两处各写一遍
            SyncWindowState();
        }
        catch (Exception ex)
        {
            LogLine("应用设置失败：" + ex.Message);
        }
    }

    // ===================== 状态与诊断 =====================

    public string BuildStatus()
    {
        var keys = Settings.Keys.Count;

        if (keys == 0)
        {
            return "还没添加任何按键。去「按键显示」页点【添加按键】再按一下键盘。";
        }

        if (!Settings.Enabled)
        {
            return $"已关闭显示（{keys} 个按键的设置还留着）";
        }

        var pressed = Settings.Keys.Count(k => VirtualKeys.IsDown(k.VirtualKey));
        var through = Settings.ClickThrough ? "穿透开" : "穿透关";
        var mode = Settings.InputMode == KeyInputMode.Hook ? "钩子" : "轮询";

        return $"正在显示 {keys} 个按键 · 当前按下 {pressed} 个 · 刷新 {Settings.RefreshMs}ms · {through} · 读取={mode}";
    }

    /// <summary>
    /// 自检期间禁止写盘的总闸。
    ///
    /// 为什么要有这个：自检会**临时篡改** Settings 里的值来量尺寸 / 验颜色（比如把
    /// OverallScale 改成 0.5 量完再还回来）。这些改动**绝不能落进用户的 settings.json** ——
    /// 一旦中间某步抛异常跳过还原，或者页面那边正好有个攒着的 ScheduleSave 落下来，
    /// 用户下次打开就会发现"我的配置被改了"。这个坑真的踩过（2026-10-03：100% 被自检刷成 50%）。
    /// 所以：自检一开始就把这道闸关上，跑完再打开。
    /// </summary>
    private bool _suppressSave;

    public void SaveSettings()
    {
        // 自检跑的时候一律不落盘 —— 那时 Settings 里是临时探针值，不是用户的配置
        if (_suppressSave)
        {
            return;
        }

        try
        {
            if (_host is not null)
            {
                PluginSettingsFile.Save(_host.PluginDirectory, Settings);
            }
        }
        catch
        {
        }
    }

    // ===================== 自检 =====================

    /// <summary>
    /// 自检用：把缩放设成 <paramref name="scale"/> 重建一次，看窗口是不是真的容得下内容。
    ///
    /// 判据：窗口尺寸 ≥ 内容包围盒（算上留白）。容不下就是内容会被窗口边缘裁掉 ——
    /// 这正是"放大后被一圈像边框的东西挡住"那个 bug。
    /// </summary>
    private bool ContainedInScaleTest(KeyOverlayWindow window, KeyDisplaySettings settings, double scale)
    {
        settings.OverallScale = scale;
        window.Rebuild(settings);

        double contentW = 0;
        double contentH = 0;

        foreach (var cell in settings.Keys)
        {
            contentW = Math.Max(contentW, (cell.X + cell.Width) * scale);
            contentH = Math.Max(contentH, (cell.Y + cell.Height) * scale);
        }

        // 给 1px 容差（DPI 取整的影响）
        return window.Width + 1 >= contentW && window.Height + 1 >= contentH;
    }

    /// <summary>
    /// 数据链路自检：设了 <c>MCO_KEY_SELFTEST=1</c> 时才会跑。
    ///
    /// 做法是**发一个真实的按键**（右 Shift，无副作用；DEV-NOTES 里验证输入类插件的既定做法），
    /// 然后看当前读取通道能不能读到它 —— 这验证的是"读到的是真实按键状态"，而不是在读自己的缓存。
    /// 检测前后都会把按键抬起来，不留残留。
    ///
    /// **两种通道都会验**（不管当前设的是哪个）：钩子模式发的是真实按键，
    /// 钩子回调会收到，所以这一套对钩子同样有效 —— 不装钩子的模式会被跳过并说明。
    /// </summary>
    private void RunSelfTestIfRequested()
    {
        if (Environment.GetEnvironmentVariable("MCO_KEY_SELFTEST") != "1")
        {
            return;
        }

        LogLine("=== 自检开始（发一个真实的右 Shift，看当前读取通道读不读得到）===");
        LogLine($"当前通道：{VirtualKeys.Source.Name}；进程提权：{(IsProcessElevated ? "是" : "否")}");

        // ⚠ 自检全程**禁止写盘**。自检会临时篡改 Settings 来量尺寸 / 验颜色，
        // 那些是探针值、不是用户的配置，绝不能落进 settings.json。
        // 这里关上闸，跑完在 finally 里**从磁盘重载**（而不是保存）——
        // 这样就算哪个自检忘了还原、或还原前抛了异常，内存里也不会残留探针值。
        _suppressSave = true;
        try
        {
            RunSelfTestBody();
        }
        finally
        {
            _suppressSave = false;

            // 从磁盘重载：磁盘上永远是用户自己的配置（写盘闸一直关着，没人能污染它），
            // 拿它覆盖内存，等于把现场还原到自检开始之前。
            if (_host is not null)
            {
                try
                {
                    Settings = PluginSettingsFile.Load<KeyDisplaySettings>(_host.PluginDirectory);
                }
                catch
                {
                    // 重载失败极罕见（自检开始前刚加载成功过）。留着内存现状总比拿空配置覆盖强。
                }
            }
        }
    }

    /// <summary>自检主体。由 <see cref="RunSelfTestIfRequested"/> 包着写盘闸调用。</summary>
    private void RunSelfTestBody()
    {

        // 默认配置核对：gold_ 调好的那套固化进代码之后，这些数字应该对得上他的配置
        try
        {
            var defaults = new KeyDisplaySettings();
            var layout = KeyDisplaySettings.DefaultLayout();

            LogLine(string.Format(
                "默认配置核对：字号={0} 圆角={1} 边框={2} 阴影模糊={3} 阴影偏移={4} 阴影方向={5} 果冻力度={6} 网格={7} 刷新={8}ms 默认按键={9} 个",
                defaults.FontSize, defaults.CornerRadius, defaults.BorderThickness,
                defaults.ShadowBlur, defaults.ShadowOffset, defaults.ShadowDirection,
                defaults.DynamicForce, defaults.GridSize, defaults.RefreshMs, layout.Count));
        }
        catch (Exception defEx)
        {
            LogLine("默认配置核对异常：" + defEx);
        }

        // ---- 钩子通道专项自检 ----
        //
        // 单独拉出来试一次钩子，**不管当前设的是哪个模式**。
        // 理由：将来用户报"绝区零里还是不亮"时，需要能一条命令分清是
        // "钩子本身没装上" 还是 "钩子装上了但权限不够/游戏没走这条路"。
        try
        {
            RunHookSelfTest();
        }
        catch (Exception hookEx)
        {
            LogLine("钩子自检异常：" + hookEx.Message);
        }

        try
        {
            if (VirtualKeys.IsDown(SelfTestKey))
            {
                LogLine("自检跳过：右 Shift 当前就是按下状态，现在测不出结论。松开它再启动一次。");
                return;
            }

            keybd_event((byte)SelfTestKey, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);

            var down = VirtualKeys.IsDown(SelfTestKey);

            keybd_event((byte)SelfTestKey, 0, KeyeventfKeyup, UIntPtr.Zero);
            Thread.Sleep(80);

            var up = VirtualKeys.IsDown(SelfTestKey);

            LogLine(down && !up
                ? "自检通过：按下时读到 true，抬起后读到 false —— 按键读取链路是通的。"
                : $"自检失败：按下时={down}（应为 true），抬起后={up}（应为 false）。");

            // 再验一步：按键真的会让悬浮窗变色吗？
            // 临时塞一个右 Shift 格子（只在内存里，绝不存盘），发键、看 Refresh 报不报变化，验完撤掉。
            var window = _window;
            if (window is null || !window.IsVisible)
            {
                LogLine("窗口变色自检跳过：悬浮窗当前没显示。");
                return;
            }

            var probe = new KeyCell { VirtualKey = SelfTestKey, X = 0, Y = 0, Width = 20, Height = 20 };
            Settings.Keys.Add(probe);
            window.Rebuild(Settings);

            keybd_event((byte)SelfTestKey, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            var changed = window.Refresh(Settings);

            keybd_event((byte)SelfTestKey, 0, KeyeventfKeyup, UIntPtr.Zero);
            Thread.Sleep(80);
            window.Refresh(Settings);

            Settings.Keys.Remove(probe);
            window.Rebuild(Settings);

            LogLine(changed
                ? "窗口变色自检通过：按下时 Refresh 报出了状态变化 —— 悬浮窗确实会跟着亮。"
                : "窗口变色自检失败：按下时 Refresh 没报变化。");

            // 再验一步：动感（按下弹一下）有没有真的把动画挂到格子上
            try
            {
                var wasDynamic = Settings.DynamicEnabled;
                var wasForce = Settings.DynamicForce;

                Settings.DynamicEnabled = true;
                Settings.DynamicForce = 90;   // 力度拉高，压扁量更明显、也更好判断

                var popProbe = new KeyCell { VirtualKey = SelfTestKey, X = 0, Y = 0, Width = 20, Height = 20 };
                Settings.Keys.Add(popProbe);
                window.Rebuild(Settings);

                keybd_event((byte)SelfTestKey, 0, 0, UIntPtr.Zero);
                window.Refresh(Settings);   // 按下 → Squash（压扁并保持）
                Thread.Sleep(30);

                var squashing = window.IsPopping(SelfTestKey);
                var target = window.LastPopTarget;

                // 再松开一次，看回弹动画有没有挂上去
                keybd_event((byte)SelfTestKey, 0, KeyeventfKeyup, UIntPtr.Zero);
                window.Refresh(Settings);   // 松开 → Release（弹性回弹）
                Thread.Sleep(30);

                var releasing = window.IsPopping(SelfTestKey);

                Thread.Sleep(30);

                Settings.Keys.Remove(popProbe);
                Settings.DynamicEnabled = wasDynamic;
                Settings.DynamicForce = wasForce;
                window.Rebuild(Settings);

                LogLine("果冻曲线：" + window.LastPopCurve);

                LogLine(squashing && releasing && target < 0.95
                    ? string.Format("果冻自检通过：按住时压到 {0:0.00} 倍并保持，松开也挂上了回弹动画。", target)
                    : string.Format("果冻自检失败：按下={0} 松开={1} 压扁倍数={2:0.00}（应小于 0.95）。",
                        squashing, releasing, target));
            }
            catch (Exception popEx)
            {
                LogLine("果冻自检异常：" + popEx);
            }

            // 位置补偿自检：改阴影大小（会改变窗口留白）之后，内容在屏幕上不该移位
            try
            {
                var wasBlur = Settings.ShadowBlur;

                window.SyncPositionToContent(Settings);
                var before = window.ContentOrigin;

                Settings.ShadowBlur = wasBlur + 15;   // 让留白明显变大
                window.Rebuild(Settings);
                var after = window.ContentOrigin;

                Settings.ShadowBlur = wasBlur;
                window.Rebuild(Settings);

                LogLine(Math.Abs(before.X - after.X) < 0.5 && Math.Abs(before.Y - after.Y) < 0.5
                    ? string.Format("位置补偿自检通过：改阴影大小后内容仍在 ({0:0},{1:0})，没有移位。", after.X, after.Y)
                    : string.Format("位置补偿自检失败：内容从 ({0:0},{1:0}) 漂到了 ({2:0},{3:0})。",
                        before.X, before.Y, after.X, after.Y));
            }
            catch (Exception posEx)
            {
                LogLine("位置补偿自检异常：" + posEx);
            }

            // 防出屏自检：把窗口位置故意扔到屏幕外四个方向，看 ClampToVirtualScreen
            // 能不能都拉回来。位置探针值由防污染三件套兜底（_suppressSave + 磁盘重载），
            // 不会污染用户配置。
            // 开关两个方向都验：开着→能钳；关着→真的不钳（开关没接好就是"关了还钳"）。
            try
            {
                var wasClampEnabled = window.ClampEnabled;
                var vsLeft = SystemParameters.VirtualScreenLeft;
                var vsTop = SystemParameters.VirtualScreenTop;
                var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

                var wasLeft = window.Left;
                var wasTop = window.Top;

                bool pass = true;
                string detail = "";

                // —— 开关关着：扔出去就不该动 ——
                window.ClampEnabled = false;
                window.Left = vsLeft - 5000;
                window.ClampToVirtualScreen();
                var offByPass = Math.Abs(window.Left - (vsLeft - 5000)) < 0.5;
                pass &= offByPass;
                if (!offByPass) detail += $" 关掉时仍被钳到({window.Left:0})；";

                // —— 开关开着：左上方向出界要拉回 ——
                window.ClampEnabled = true;
                window.Left = vsLeft - 5000;
                window.Top = vsTop - 5000;
                window.ClampToVirtualScreen();
                var leftTopOk = window.Left >= vsLeft - 0.5 && window.Top >= vsTop - 0.5;
                pass &= leftTopOk;
                if (!leftTopOk) detail += $" 左上出界后钳到({window.Left:0},{window.Top:0})；";

                // —— 右下方向出界 ——
                window.Left = vsRight + 5000;
                window.Top = vsBottom + 5000;
                window.ClampToVirtualScreen();
                var rightBottomOk = window.Left + window.ActualWidth <= vsRight + 0.5
                                    && window.Top + window.ActualHeight <= vsBottom + 0.5;
                pass &= rightBottomOk;
                if (!rightBottomOk) detail += $" 右下出界后钳到({window.Left:0},{window.Top:0})+{window.ActualWidth:0}×{window.ActualHeight:0}；";

                // —— 放大到超出屏幕（缩放窗口到比屏幕大 —— 对齐左上角，不许死循环/抛异常）——
                var savedScale = Settings.OverallScale;
                try
                {
                    Settings.OverallScale = 8.0;   // 大到必超屏幕
                    window.Rebuild(Settings);
                    window.ClampToVirtualScreen();
                    var hugeOk = window.Left >= vsLeft - 0.5 && window.Top >= vsTop - 0.5;
                    pass &= hugeOk;
                    if (!hugeOk) detail += $" 超大窗口钳到({window.Left:0},{window.Top:0})；";
                }
                finally
                {
                    Settings.OverallScale = savedScale;
                    window.Rebuild(Settings);
                }

                // 还原（自检结束还有磁盘重载兜底，这里先物归原主）
                window.ClampEnabled = wasClampEnabled;
                window.Left = wasLeft;
                window.Top = wasTop;

                LogLine(pass
                    ? "防出屏自检通过：开关关着不动、开着时左上/右下/超大窗口三种出界都拉回屏幕内。"
                    : $"防出屏自检失败：{detail}");
            }
            catch (Exception clampEx)
            {
                LogLine("防出屏自检异常：" + clampEx);
            }

            // 局部外观自检：给一格单独设字号，看它是不是只影响那一格
            try
            {
                var probeCell = Settings.Keys.FirstOrDefault(k => k.VirtualKey == SelfTestKey)
                    ?? Settings.Keys.FirstOrDefault();

                if (probeCell is not null)
                {
                    var globalSize = Settings.FontSize;

                    // ⚠ 比的是**渲染出来的字号**，它已经乘过整体缩放；而 Settings.FontSize 是原始值。
                    // 不乘的话，只要用户把「整体大小」拉离 100%，这条自检就会假失败
                    // （2026-10-03 实际遇到过：他把整体大小调到 50%，这里就报了"覆盖后=20 清除后=9"）。
                    var expected = globalSize * KeyOverlayWindow.OverallScaleOf(Settings);

                    probeCell.FontSizeOverride = 40;
                    window.Rebuild(Settings);
                    var overridden = window.GetCellFontSize(probeCell.VirtualKey);

                    probeCell.FontSizeOverride = -1;
                    window.Rebuild(Settings);
                    var restored = window.GetCellFontSize(probeCell.VirtualKey);

                    var othersUntouched = Settings.Keys
                        .Where(k => !ReferenceEquals(k, probeCell))
                        .All(k => Math.Abs(window.GetCellFontSize(k.VirtualKey) - expected) < 0.5);

                    LogLine(Math.Abs(overridden - (40 * KeyOverlayWindow.OverallScaleOf(Settings))) < 0.5
                            && Math.Abs(restored - expected) < 0.5
                            && othersUntouched
                        ? string.Format("局部外观自检通过：单独设的那格是 {0:0.#}（40×缩放 {1:0.##}），清除后回到全局 {2:0.#}，其它格一直是 {2:0.#}。",
                            overridden, KeyOverlayWindow.OverallScaleOf(Settings), restored)
                        : string.Format("局部外观自检失败：覆盖后={0:0.#} 清除后={1:0.#}（期望 {2:0.#}）其它格未受影响={3}。",
                            overridden, restored, expected, othersUntouched));
                }
            }
            catch (Exception lookEx)
            {
                LogLine("局部外观自检异常：" + lookEx);
            }

            // CPS 显示自检：给鼠标键开上 CPS，喂一个值，看格子下面那行有没有更新。
            // 只验显示链路 —— 计数那部分要真点鼠标，按规矩不自动测（会真点下去）。
            try
            {
                var cpsCell = Settings.Keys.FirstOrDefault(k => k.VirtualKey is 0x01 or 0x02);

                if (cpsCell is null)
                {
                    LogLine("CPS 显示自检跳过：配置里没有鼠标左/右键的格子。");
                }
                else
                {
                    var wasShowCps = cpsCell.ShowCps;
                    cpsCell.ShowCps = true;
                    window.Rebuild(Settings);

                    window.UpdateCps(13, 7);
                    var shown = window.GetCellCpsText(cpsCell.VirtualKey);

                    cpsCell.ShowCps = wasShowCps;
                    window.Rebuild(Settings);

                    LogLine(shown == "13"
                        ? string.Format("CPS 显示自检通过：喂 13 进去，格子下面显示「{0}」。", shown)
                        : string.Format("CPS 显示自检失败：喂 13 进去，显示的是「{0}」。", shown));
                }
            }
            catch (Exception cpsEx)
            {
                LogLine("CPS 显示自检异常：" + cpsEx);
            }

            // 行为开关自检：显示开关 / 鼠标穿透 / 刷新率。
            // 这三项不是"外观"，页面上那条只刷外观的轻路径管不着它们 —— 正是他报的"关了没反应"。
            try
            {
                var wasEnabled = Settings.Enabled;
                var wasThrough = Settings.ClickThrough;
                var wasRefresh = Settings.RefreshMs;

                // 显示开关：关掉要能真的藏起来，打开要能回来
                Settings.Enabled = false;
                SyncWindowState();
                var canHide = !window.IsReallyVisible();

                Settings.Enabled = true;
                SyncWindowState();
                var canShow = window.IsReallyVisible();

                // 鼠标穿透：直接读 Win32 扩展样式位
                Settings.ClickThrough = true;
                SyncWindowState();
                var throughOn = window.IsClickThroughActive();

                Settings.ClickThrough = false;
                SyncWindowState();
                var throughOff = !window.IsClickThroughActive();

                // 刷新率
                Settings.RefreshMs = 50;
                SyncWindowState();
                var interval = TimerIntervalMs;

                Settings.Enabled = wasEnabled;
                Settings.ClickThrough = wasThrough;
                Settings.RefreshMs = wasRefresh;
                SyncWindowState();

                LogLine(canHide && canShow && throughOn && throughOff && Math.Abs(interval - 50) < 0.5
                    ? "行为开关自检通过：显示开关能藏能显、穿透能开能关、刷新率确实改到了 50ms。"
                    : string.Format("行为开关自检失败：隐藏={0} 显示={1} 穿透开={2} 穿透关={3} 间隔={4:0}ms。",
                        canHide, canShow, throughOn, throughOff, interval));
            }
            catch (Exception swEx)
            {
                LogLine("行为开关自检异常：" + swEx);
            }

            // CPS 计数逻辑自检：模拟「闲置 → 点一下 → 按住不放 → 松开 → 再点 → 过了统计窗口」。
            // 计数对不对不依赖真实鼠标点击 —— 按规矩自检不能发鼠标键（会真点下去），
            // 所以把这段逻辑抽成了 CpsSampler.CountClicks 单独验。
            try
            {
                var stamps = new List<DateTime>();
                var wasDown = false;
                var t0 = DateTime.UtcNow;

                var actual = new[]
                {
                    CpsSampler.CountClicks(stamps, false, ref wasDown, t0, 1000),
                    CpsSampler.CountClicks(stamps, true, ref wasDown, t0.AddMilliseconds(400), 1000),
                    CpsSampler.CountClicks(stamps, true, ref wasDown, t0.AddMilliseconds(450), 1000),
                    CpsSampler.CountClicks(stamps, false, ref wasDown, t0.AddMilliseconds(500), 1000),
                    CpsSampler.CountClicks(stamps, true, ref wasDown, t0.AddMilliseconds(600), 1000),
                    CpsSampler.CountClicks(stamps, false, ref wasDown, t0.AddMilliseconds(1701), 1000),
                };

                var expected = new[] { 0, 1, 1, 1, 2, 0 };

                LogLine(actual.SequenceEqual(expected)
                    ? "CPS 计数逻辑自检通过：闲置=0 → 点一下=1 → 按住不放不加 → 松开 → 再点=2 → 过了窗口归 0。"
                    : string.Format("CPS 计数逻辑自检失败：期望 [{0}]，实际 [{1}]。",
                        string.Join(",", expected), string.Join(",", actual)));
            }
            catch (Exception countEx)
            {
                LogLine("CPS 计数逻辑自检异常：" + countEx);
            }

            // CPS 采样线程自检：要采样时线程起来，不要时收掉 — 不留后台开销
            try
            {
                _cps.SetWanted(false, false);
                var runningWhenIdle = _cps.IsRunning;

                _cps.SetWanted(true, false);
                Thread.Sleep(80);
                var runningWhenNeeded = _cps.IsRunning;

                _cps.SetWanted(false, false);
                var stoppedAfterUse = !_cps.IsRunning;

                LogLine(!runningWhenIdle && runningWhenNeeded && stoppedAfterUse
                    ? "CPS 采样线程自检通过：需要采样时线程起来，不需要时收掉，不留后台开销。"
                    : string.Format("CPS 采样线程自检失败：闲置时={0} 启动后={1} 停掉后={2}。",
                        runningWhenIdle, runningWhenNeeded, stoppedAfterUse));
            }
            catch (Exception threadEx)
            {
                LogLine("CPS 采样线程自检异常：" + threadEx);
            }
            // 整体大小自检：缩放要真的把格子尺寸和窗口尺寸都按比例改掉，
            // 而且**不能改坏原始布局数据**（滑块来回拖不该累积误差）
            try
            {
                var scaleWindow = _window;
                if (scaleWindow is null)
                {
                    LogLine("整体大小自检跳过：悬浮窗还没建出来。");
                }
                else
                {
                    var wasScale = Settings.OverallScale;
                    var probeKey = Settings.Keys.Count > 0 ? Settings.Keys[0] : null;

                    if (probeKey is null)
                    {
                        LogLine("整体大小自检跳过：没有按键格子。");
                    }
                    else
                    {
                        // ⚠ 整段必须包在 try/finally 里。这里会把 OverallScale 改成 1.0 / 2.0 / 0.5 来量尺寸，
                        // 中间任何一步抛异常（Rebuild 失败、取尺寸越界…）都会**跳过还原**，
                        // 结果 OverallScale 就永久卡在 0.5 —— 用户下次打开发现"悬浮窗莫名小了一半"。
                        // 这个坑真的踩过（2026-10-03），所以还原动作一律放 finally。
                        try
                        {
                            var rawW = probeKey.Width;
                            var rawX = probeKey.X;

                            // 100% 时的基准
                            Settings.OverallScale = 1.0;
                            scaleWindow.Rebuild(Settings);
                            var size100 = new Size(scaleWindow.Width, scaleWindow.Height);
                            var cell100 = scaleWindow.FirstCellSize();

                            // 200%
                            Settings.OverallScale = 2.0;
                            scaleWindow.Rebuild(Settings);
                            var size200 = new Size(scaleWindow.Width, scaleWindow.Height);
                            var cell200 = scaleWindow.FirstCellSize();

                            // 50%
                            Settings.OverallScale = 0.5;
                            scaleWindow.Rebuild(Settings);
                            var size50 = new Size(scaleWindow.Width, scaleWindow.Height);
                            var cell50 = scaleWindow.FirstCellSize();

                            // 数据没被改坏
                            var dataIntact = Math.Abs(probeKey.Width - rawW) < 0.001
                                             && Math.Abs(probeKey.X - rawX) < 0.001;

                            // 格子尺寸按比例（余量不参与缩放，所以只验格子本身）
                            var cellRatio = cell100.Width > 1 ? cell200.Width / cell100.Width : 0;
                            var cellRatioSmall = cell100.Width > 1 ? cell50.Width / cell100.Width : 0;

                            // ⚠ 窗口必须容得下缩放后的内容。这条是补的 —— 早先 ResizeToContent
                            // 用原始坐标算包围盒，放大后内容超出窗口边界被裁掉，
                            // 表现是"放大之后有一圈像边框的东西把内容挡住"。
                            var fits200 = ContainedInScaleTest(scaleWindow, Settings, 2.0);
                            var fits50 = ContainedInScaleTest(scaleWindow, Settings, 0.5);

                            var ok = Math.Abs(cellRatio - 2.0) < 0.05
                                     && Math.Abs(cellRatioSmall - 0.5) < 0.05
                                     && size200.Width > size100.Width
                                     && size100.Width > size50.Width
                                     && dataIntact
                                     && fits200
                                     && fits50;

                            LogLine(ok
                                ? string.Format(
                                    "整体大小自检通过：格子 50%→{0:0.#} / 100%→{1:0.#} / 200%→{2:0.#} px（按比例）；" +
                                    "窗口 50%→{3:0} / 100%→{4:0} / 200%→{5:0} px；窗口容得下内容；原始布局数据未被改写。",
                                    cell50.Width, cell100.Width, cell200.Width,
                                    size50.Width, size100.Width, size200.Width)
                                : string.Format(
                                    "整体大小自检失败：格子 50%→{0:0.#} / 100%→{1:0.#} / 200%→{2:0.#} px（比值 {3:0.00} / {4:0.00}，应约 0.5 / 2.0）；" +
                                    "窗口 50%→{5:0} / 100%→{6:0} / 200%→{7:0} px；容得下内容={8}/{9}；原始布局数据完好={10}。",
                                    cell50.Width, cell100.Width, cell200.Width, cellRatioSmall, cellRatio,
                                    size50.Width, size100.Width, size200.Width, fits50, fits200, dataIntact));
                        }
                        finally
                        {
                            Settings.OverallScale = wasScale;
                            scaleWindow.Rebuild(Settings);
                        }
                    }
                }
            }
            catch (Exception scaleEx)
            {
                LogLine("整体大小自检异常：" + scaleEx);
            }
            // 颜色格式自检：hex 格式跟宿主对齐（纯色省 A），而且**两种格式都要能解析回来**。
            // 这条是防"省掉 A 之后解析退化成白色" —— 那种 bug 肉眼是"颜色突然全白"，很难查。
            try
            {
                var opaque = ColorPickerWindow.FormatHex(Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6));
                var translucent = ColorPickerWindow.FormatHex(Color.FromArgb(0x80, 0x00, 0x00, 0x00));

                var roundOpaque = ColorPickerWindow.ParseForTest(opaque);
                var roundTranslucent = ColorPickerWindow.ParseForTest(translucent);

                var formatOk = opaque == "#3B82F6" && translucent == "#80000000";
                var parseOk = roundOpaque.A == 0xFF && roundOpaque.R == 0x3B && roundOpaque.G == 0x82 && roundOpaque.B == 0xF6
                              && roundTranslucent.A == 0x80 && roundTranslucent.R == 0x00;

                LogLine(formatOk && parseOk
                    ? string.Format("颜色格式自检通过：纯色写作 {0}（省 A）、半透明写作 {1}；两种都能解析回原值。", opaque, translucent)
                    : string.Format("颜色格式自检失败：纯色={0}（期望 #3B82F6）、半透明={1}（期望 #80000000）；" +
                                    "回解析 纯色 A={2:X2}/R={3:X2} 半透明 A={4:X2}。",
                        opaque, translucent, roundOpaque.A, roundOpaque.R, roundTranslucent.A));
            }
            catch (Exception colorEx)
            {
                LogLine("颜色格式自检异常：" + colorEx);
            }

            // 彩色循环自检：走的是**完整 RGB 光谱**，要验这几件事 ——
            //   1) 光谱路径对：0% = 纯红、1/3 圈 = 纯绿、1/2 圈 = 纯青、2/3 圈 = 纯蓝（加色通道的本来面目）
            //   2) 首尾接得上：进度 1.0 回到纯红（接缝处不"跳"）
            //   3) **不看源色**：白 / 黑 / 灰当源色也照样跑出彩色（gold_ 明确要求 —— 否则默认的白字、
            //      黑阴影打开开关"看着没反应"，会被当成功能坏了）
            //   4) A 通道原样带过去
            try
            {
                var accent = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);   // 源色故意用**纯黑**
                var white = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);    // 纯白（默认文字色）
                var gray = Color.FromArgb(0x80, 0x80, 0x80, 0x80);     // 半透明纯灰

                // 不管源色是什么，取的都是同一条光谱 —— 下面用纯黑源色验路径
                var start = ColorCycle.Shift(accent, 0.0);
                var atGreen = ColorCycle.Shift(accent, 1.0 / 3.0);
                var atCyan = ColorCycle.Shift(accent, 0.5);
                var atBlue = ColorCycle.Shift(accent, 2.0 / 3.0);
                var fullTurn = ColorCycle.Shift(accent, 1.0);

                bool Near(Color c, int r, int g, int b, int tol = 3) =>
                    Math.Abs(c.R - r) <= tol && Math.Abs(c.G - g) <= tol && Math.Abs(c.B - b) <= tol;

                bool IsColorful(Color c) =>
                    c.R != c.G || c.G != c.B;   // 三通道不全等 = 是彩色

                var startsAtRed = Near(start, 255, 0, 0);
                var hitsGreen = Near(atGreen, 0, 255, 0);
                var hitsCyan = Near(atCyan, 0, 255, 255);
                var hitsBlue = Near(atBlue, 0, 0, 255);
                var fullIsRed = Near(fullTurn, 255, 0, 0);

                // 黑白灰也必须跑起来（这是本轮的核心改动）
                var whiteRuns = IsColorful(ColorCycle.Shift(white, 0.25));
                var blackRuns = IsColorful(ColorCycle.Shift(accent, 0.25));
                var grayRuns = IsColorful(ColorCycle.Shift(gray, 0.25));

                // A 通道原样保留
                var alphaKept = atGreen.A == accent.A && ColorCycle.Shift(gray, 0.25).A == gray.A && whiteRuns;

                var cycleOk = startsAtRed && hitsGreen && hitsCyan && hitsBlue && fullIsRed
                              && whiteRuns && blackRuns && grayRuns && alphaKept;

                LogLine(cycleOk
                    ? string.Format(
                        "彩色循环自检通过：RGB 光谱 红→绿(1/3)→青(1/2)→蓝(2/3)→红(整圈)，首尾接得上；" +
                        "白/黑/灰当源色也照样跑出彩色（白→{0}）—— 开了就一定能看见在变；A 保持。",
                        Describe(ColorCycle.Shift(white, 0.25)))
                    : string.Format(
                        "彩色循环自检失败：0%={0} 绿{1} 青{2} 蓝{3} 整圈=红{4}；" +
                        "白跑起来={5} 黑跑起来={6} 灰跑起来={7} A保持={8}。",
                        startsAtRed, hitsGreen, hitsCyan, hitsBlue, fullIsRed,
                        whiteRuns, blackRuns, grayRuns, alphaKept));
            }
            catch (Exception cycleEx)
            {
                LogLine("彩色循环自检异常：" + cycleEx);
            }

            // 循环开关联动自检：把全局的循环打开，看悬浮窗上那一格的颜色是不是真的跟着进度变了。
            // 这条验的是"开关 → 渲染"这条链路通不通 —— 光验 ColorCycle 算法对不算数，
            // 还得验它确实接到了窗口的画笔上。
            try
            {
                if (_window is null)
                {
                    LogLine("彩色循环联动自检跳过：悬浮窗还没建出来。");
                }
                else
                {
                    var wasCycle = Settings.TextColorCycle;
                    var wasIdleText = Settings.IdleTextColor;
                    var wasProgress = _cycleStart;

                    try
                    {
                        // 源色给个纯白也照样能测 —— 现在循环**不看源色**，无条件走光谱
                        // （早先那版是"对齐源色色相"，纯白 S=0 转不动，才需要特意喂个饱和色）
                        Settings.IdleTextColor = "#FFFFFFFF";
                        Settings.TextColorCycle = true;

                        _window.Rebuild(Settings);
                        _window.SetCycleProgress(0.0, Settings);
                        var atZero = SnapshotFirstTextColor();

                        _window.SetCycleProgress(0.25, Settings);
                        var atQuarter = SnapshotFirstTextColor();

                        var linked = atZero is Color c0 && atQuarter is Color c1 && c0 != c1;

                        LogLine(linked
                            ? string.Format(
                                "彩色循环联动自检通过：全局文字循环打开后，进度 0%={0} vs 25%={1} —— 开关确实接到了画笔上。",
                                Describe(atZero), Describe(atQuarter))
                            : string.Format(
                                "彩色循环联动自检失败：进度 0%={0} 25%={1}，期望两者不同。",
                                Describe(atZero), Describe(atQuarter)));
                    }
                    finally
                    {
                        // 复原：开关、颜色、进度基准全部还回去，别动他的配置
                        Settings.TextColorCycle = wasCycle;
                        Settings.IdleTextColor = wasIdleText;
                        _cycleStart = wasProgress;
                        _window.Rebuild(Settings);
                    }
                }
            }
            catch (Exception linkEx)
            {
                LogLine("彩色循环联动自检异常：" + linkEx);
            }

            // ===================== 边框/阴影循环自检（复现"只变一次就固定"） =====================
            //
            // 这条是用来守住 2026-10-03 那个 bug 的：
            // 彩色循环的高频刷新走的是 SetCycleProgress → ApplyColors，
            // 而边框色和阴影色当时只在 Rebuild / ApplyAppearance 里算 —— 于是
            // "打开开关时刷成一次光谱色，之后永远不动"。
            // 现在验证：**只推进度、不重建**，边框色和阴影色也必须跟着变。
            try
            {
                if (_window is null)
                {
                    LogLine("边框/阴影循环自检跳过：悬浮窗还没建出来。");
                }
                else
                {
                    var wasBorderCycle = Settings.BorderColorCycle;
                    var wasShadowCycle = Settings.ShadowColorCycle;
                    var wasShadowEnabled = Settings.ShadowEnabled;
                    var wasBorderColor = Settings.BorderColor;
                    var wasShadowColor = Settings.ShadowColor;
                    var wasTextCycle = Settings.TextColorCycle;

                    try
                    {
                        // 只开边框 + 阴影循环，文字循环关掉 —— 这样变的一定是边框/阴影，不是文字带出来的错觉
                        Settings.TextColorCycle = false;
                        Settings.BorderColorCycle = true;
                        Settings.ShadowColorCycle = true;
                        Settings.ShadowEnabled = true;
                        Settings.BorderColor = "#FFFFFFFF";
                        Settings.ShadowColor = "#FF000000";

                        _window.Rebuild(Settings);

                        // ⚠ 关键：只推进度，**不重建**。重建能把颜色算对，但推不动——那正是这个 bug
                        _window.SetCycleProgress(0.0, Settings);
                        var border0 = _window.BorderColorAt(0);
                        var shadow0 = _window.ShadowColorAt(0);

                        _window.SetCycleProgress(0.33, Settings);
                        var border1 = _window.BorderColorAt(0);
                        var shadow1 = _window.ShadowColorAt(0);

                        var borderMoves = border0 is Color b0 && border1 is Color b1 && b0 != b1;
                        var shadowMoves = shadow0 is Color s0 && shadow1 is Color s1 && s0 != s1;

                        LogLine(borderMoves && shadowMoves
                            ? string.Format(
                                "边框/阴影循环自检通过：只推进度（不重建）时边框 {0}→{1}、阴影 {2}→{3} —— 两者都跟着循环在变。",
                                Describe(border0), Describe(border1), Describe(shadow0), Describe(shadow1))
                            : string.Format(
                                "边框/阴影循环自检失败：边框 {0}→{1}（{2}）、阴影 {3}→{4}（{5}）——" +
                                "两者都必须随进度变，不动就说明高频路径漏刷了边框/阴影的颜色。",
                                Describe(border0), Describe(border1), borderMoves ? "在动" : "没动",
                                Describe(shadow0), Describe(shadow1), shadowMoves ? "在动" : "没动"));
                    }
                    finally
                    {
                        Settings.BorderColorCycle = wasBorderCycle;
                        Settings.ShadowColorCycle = wasShadowCycle;
                        Settings.ShadowEnabled = wasShadowEnabled;
                        Settings.BorderColor = wasBorderColor;
                        Settings.ShadowColor = wasShadowColor;
                        Settings.TextColorCycle = wasTextCycle;
                        _window.Rebuild(Settings);
                    }
                }
            }
            catch (Exception bsEx)
            {
                LogLine("边框/阴影循环自检异常：" + bsEx);
            }

            // ===================== 共用时间轴自检 =====================
            // 这条验的是"**不同对象之间不会错位**"，跟上面那条验的不是一回事 ——
            // 上面只能证明"开关接到了画笔上"，证明不了"两个对象在相同时刻颜色一致"。
            //
            // 错位是怎么来的：只要有人在开关/改速度时把时间基准（_cycleStart）归零，
            // 整圈已经走过的相位就被抹掉，**后开的对象从 0% 起跑、先开的那个跳回去**，
            // 于是两者永远差着一段。所以这里要同时验两件事：
            //   1) 基准**不会被**开关动作改动（归零/重置都会当场露馅）
            //   2) 两个**不同源色**的格子，在同一 progress 下走过的相位一致（各自对齐到自己源色后，角度差恒定）
            try
            {
                var before = _cycleStart;

                // 1) 反复开开关、改速度，基准必须纹丝不动
                var wasText = Settings.TextColorCycle;
                var wasSeconds = Settings.CycleSeconds;
                try
                {
                    Settings.TextColorCycle = !wasText;
                    Settings.CycleSeconds = wasSeconds >= 20 ? 5 : wasSeconds + 3;
                    Settings.TextColorCycle = wasText;
                    Settings.CycleSeconds = wasSeconds;
                }
                finally
                {
                    Settings.TextColorCycle = wasText;
                    Settings.CycleSeconds = wasSeconds;
                }

                var baseHeld = _cycleStart == before;

                // 2) 同一 progress、两个不同源色 → 各自的相位推进量必须相等
                //    取"红"和"半亮蓝"两个源色，比较 progress 从 0.1 到 0.6 时**色相**各自前进了多少度。
                var red = Color.FromArgb(0xFF, 0xFF, 0x00, 0x00);
                var blue = Color.FromArgb(0xFF, 0x00, 0x00, 0x80);

                var redStep = HueDelta(ColorCycle.Shift(red, 0.1), ColorCycle.Shift(red, 0.6));
                var blueStep = HueDelta(ColorCycle.Shift(blue, 0.1), ColorCycle.Shift(blue, 0.6));

                // 两者的"前进步长"应当相等（同一段时间走同一段光谱），允许几度的取整误差
                var inPhase = Math.Abs(redStep - blueStep) <= 5.0;

                var axisOk = baseHeld && inPhase;

                LogLine(axisOk
                    ? string.Format(
                        "共用时间轴自检通过：开关/改速度后基准未被改动（{0}）；" +
                        "两个不同源色在同一 progress 下步长一致（红走 {1:0.#}°、蓝走 {2:0.#}°）—— 不会错位。",
                        baseHeld ? "纹丝不动" : "被改了", redStep, blueStep)
                    : string.Format(
                        "共用时间轴自检失败：基准保持不变={0}（期望 true）；" +
                        "同段 progress 步长 红={1:0.#}° 蓝={2:0.#}°（期望两者差 ≤5°）。",
                        baseHeld, redStep, blueStep));
            }
            catch (Exception axisEx)
            {
                LogLine("共用时间轴自检异常：" + axisEx);
            }

            // ===================== 循环真实运转自检（复现"运行时不变色"） =====================
            // 上面两条都是"手动塞 progress 进去"验的，验不出"定时器到底有没有在推、推进的值有没有被用上"。
            // 这条**真的让定时器跑起来**：开循环 → 记下颜色 → 等 3 个 tick → 再记一次，必须不同。
            // 另外把每个格子的开关状态和 AnyColorCycle 一起打出来，一眼看出"到底开没开"。
            try
            {
                var any = Settings.AnyColorCycle;
                LogLine(string.Format(
                    "循环状态快照：AnyColorCycle={0}；全局 文字={1} 背景={2} 按文={3} 按背={4} 边框={5} 阴影={6}；" +
                    "每键覆盖 {7} 个非 null；CycleSeconds={8}；窗口IsVisible={9}",
                    any,
                    Settings.TextColorCycle, Settings.BackgroundColorCycle,
                    Settings.PressedTextColorCycle, Settings.PressedBackgroundColorCycle,
                    Settings.BorderColorCycle, Settings.ShadowColorCycle,
                    CountCellOverrides(), Settings.CycleSeconds,
                    _window?.IsVisible.ToString() ?? "(无窗口)"));

                if (_window is not null)
                {
                    var wasIdleText = Settings.IdleTextColor;
                    var wasText = Settings.TextColorCycle;

                    try
                    {
                        // 挑一个真有颜色的源色，否则纯白按设计转不动
                        Settings.IdleTextColor = "#FFFF0000";
                        Settings.TextColorCycle = true;
                        _window.Rebuild(Settings);

                        // 真的把进度推进器跑起来：直接调 TickCycle 三次，中间 sleep 让时间流逝
                        var t0 = SnapshotFirstTextColor();
                        TickCycle();
                        var s0 = SnapshotFirstTextColor();

                        Thread.Sleep(1200);
                        TickCycle();
                        var s1 = SnapshotFirstTextColor();

                        Thread.Sleep(1200);
                        TickCycle();
                        var s2 = SnapshotFirstTextColor();

                        var moves = s0 is Color c0 && s1 is Color c1 && c0 != c1;
                        var keepsMoving = s1 is Color d1 && s2 is Color d2 && d1 != d2;

                        LogLine(moves && keepsMoving
                            ? string.Format(
                                "循环真实运转自检通过：TickCycle 连推三次得到 {0} → {1} → {2}，颜色在动。",
                                Describe(s0), Describe(s1), Describe(s2))
                            : string.Format(
                                "循环真实运转自检失败：三次快照 {0} / {1} / {2}（起始 {3}）" +
                                "，颜色{4}。progress 现在={5:0.###}。",
                                Describe(s0), Describe(s1), Describe(s2), Describe(t0),
                                moves ? "第二次后就不动了" : "压根没动",
                                CycleProgressNow));
                    }
                    finally
                    {
                        Settings.IdleTextColor = wasIdleText;
                        Settings.TextColorCycle = wasText;
                        _window.Rebuild(Settings);
                    }
                }
            }
            catch (Exception liveEx)
            {
                LogLine("循环真实运转自检异常：" + liveEx);
            }

            // ===================== 读取方式切换链路自检 =====================
            // 验"下拉框 → 设置 → 真的装了/卸了钩子"整条链路。
            // ⚠ 验完必须切回原来的模式：这个自检会真的装、卸钩子，
            //   不改回去的话用户跑一次自检就把模式悄悄换了（性能/权限行为都跟着变）。
            try
            {
                var page = new KeyDisplayPage(this);
                var wasMode = Settings.InputMode;

                try
                {
                    LogLine("读取方式诊断：" + page.InputModeComboInfo);

                    // 切到钩子
                    page.ProbeInputModeComboBox();
                    var afterHook = Settings.InputMode;
                    var hookLive = VirtualKeys.Source.Name;

                    LogLine(afterHook == KeyInputMode.Hook
                        ? $"读取方式切换自检通过：切到钩子后设置变成了 Hook，当前通道={hookLive}。"
                        : $"读取方式切换自检失败：切到钩子后设置是 {afterHook}（应为 Hook）。");

                    // 切回轮询
                    page.ProbeInputModeBackToPolling();
                    var afterPoll = Settings.InputMode;
                    var pollLive = VirtualKeys.Source.Name;

                    LogLine(afterPoll == KeyInputMode.Polling
                        ? $"读取方式回切自检通过：切回轮询后设置是 Polling，当前通道={pollLive}。"
                        : $"读取方式回切自检失败：设置是 {afterPoll}（应为 Polling）。");
                }
                finally
                {
                    // 恢复用户原本的模式（走设置 + 插件通道，别只改字段）
                    Settings.InputMode = wasMode;
                    page.ProbeInputModeBackToPolling();
                    if (wasMode == KeyInputMode.Hook)
                    {
                        page.ProbeInputModeComboBox();
                    }
                }
            }
            catch (Exception modeEx)
            {
                LogLine("读取方式切换链路自检异常：" + modeEx);
            }

            // ===================== UI 开关链路自检（复现"点了开关没反应"） =====================
            // 上面所有自检都是**直接改 Settings** 验的，绕过了 UI。这条走真实的 UI 路径：
            // new 一个页面 → 找到那个复选框 → 真的把它 IsChecked 置 true（模拟用户点击），
            // 看 _settings.TextColorCycle 有没有跟着变。断了就说明"复选框 → 设置"这一段坏了。
            try
            {
                var page = new KeyDisplayPage(this);

                var was = Settings.TextColorCycle;

                try
                {
                    // ⚠ 先把开关**掰成 false** 再测 —— 否则它本来就是 true 的话，
                    // "置 true 后没变"会被误判成"复选框没接上"（假失败）。
                    Settings.TextColorCycle = false;
                    page.ProbeCycleCheckBox();
                    var after = Settings.TextColorCycle;
                    var uiChanged = after;

                    LogLine(string.Format("UI 开关诊断：{0}；页面 _loading={1}",
                        page.CycleCheckBoxInfo, page.LoadingGate));

                    LogLine(uiChanged
                        ? "UI 开关链路自检通过：把「没按下 · 文字」的彩色循环复选框置 true 后，" +
                          "设置从 False 变成了 True —— 复选框确实接到了设置上。"
                        : "UI 开关链路自检失败：先置 False、再把复选框置 true 后，设置仍是 False —— " +
                          "复选框没接到设置上。");
                }
                finally
                {
                    Settings.TextColorCycle = was;
                }
            }
            catch (Exception uiEx)
            {
                LogLine("UI 开关链路自检异常：" + uiEx);
            }
        }
        catch (Exception ex)
        {
            LogLine("自检异常：" + ex);
        }
    }

    /// <summary>自检用：读悬浮窗上第一个格子当前文字用的颜色（验循环有没有真的接到画笔上）。</summary>
    private Color? SnapshotFirstTextColor() => _window?.FirstTextColor();

    /// <summary>
    /// 钩子通道专项自检。
    ///
    /// 目的只有一个：将来用户报"绝区零里还是不亮"时，能一条命令分清是哪一层的问题 ——
    ///   · 钩子压根装不上（<see cref="HookKeyStateSource.Install"/> 返回 false，通常是权限/被杀软拦）；
    ///   · 钩子装上了、但回调收不到按键（宿主线程没消息循环，或回调超时被摘）；
    ///   · 钩子工作和权限都正常，那就是游戏那条路我们确实读不到（反作弊挡了低级钩子）。
    ///
    /// ⚠ 这个自检**不受当前模式影响**：不管用户设的是轮询还是钩子，都会临时装一次钩子试。
    ///   试完立刻卸掉、把全局源换回去 —— 绝不留下一个用户没要的钩子挂着。
    /// </summary>
    private void RunHookSelfTest()
    {
        // 钩子已经装着（用户本来就选的钩子模式）→ 直接在现成的上面验，不要再装一个
        var existing = _hookSource;

        if (existing is not null)
        {
            LogLine("钩子自检：在已安装的钩子上直接验（不重复安装）。");
            ProbeHook(existing, "现有钩子");
            return;
        }

        LogLine("钩子自检：临时装一个钩子试通路，验完立刻卸掉。");

        var probe = new HookKeyStateSource();

        if (!probe.Install())
        {
            LogLine($"钩子自检失败：装不上。{probe.LastError}"
                    + "（常见原因：宿主的钩子链被安全软件拦了）");
            probe.Dispose();
            return;
        }

        try
        {
            ProbeHook(probe, "临时钩子");
        }
        finally
        {
            probe.Dispose();
            LogLine("钩子自检：临时钩子已卸载。");
        }
    }

    /// <summary>
    /// 发一个真实按键，看钩子回调收不收得到。验完把键抬起来。
    ///
    /// ⚠ 只验键盘（右 Shift）。鼠标键**不能发** —— 按项目规矩自检不发鼠标键
    ///   （会真的点下去，可能点坏用户桌面上的东西）。所以鼠标那一半只能靠
    ///   "钩子装上了" + 用户实测来确认，这里把"装没装上"如实打出来。
    /// </summary>
    private void ProbeHook(HookKeyStateSource hook, string label)
    {
        var before = hook.EventCount;

        keybd_event((byte)SelfTestKey, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        keybd_event((byte)SelfTestKey, 0, KeyeventfKeyup, UIntPtr.Zero);
        Thread.Sleep(60);

        var got = hook.EventCount - before;
        var readDown = hook.IsDown(SelfTestKey);   // 已经抬起来了，应该是 false

        LogLine($"{label}：句柄有效={hook.IsActive}（含鼠标钩子），本次收到 {got} 个键盘事件，"
                + $"抬起后读到={readDown}（应为 false）。");

        if (got >= 2)
        {
            LogLine($"{label}自检通过：发一对按下/抬起，回调收到 {got} 个事件 —— 钩子回调链路是通的。"
                    + "（鼠标钩子无法自动验 —— 自检不发鼠标键，请在游戏里实测左右键。）"
                    + (IsProcessElevated
                        ? "进程已是管理员权限，游戏里应该能读到。"
                        : "⚠ 但进程**不是**管理员权限 —— 提权运行的游戏（绝区零）按键依然读不到，"
                          + "需要用管理员身份重启宿主。"));
        }
        else if (got == 0)
        {
            LogLine($"{label}自检失败：发了一对按键，回调一个事件都没收到。"
                    + "多半是钩子被系统摘了（回调超时）或被杀软拦了。");
        }
        else
        {
            LogLine($"{label}自检部分通过：收到 {got} 个事件（期望 2），可能需要更长等待时间。");
        }
    }

    /// <summary>自检用：数有多少个格子设了循环覆盖（不为 null）。</summary>
    private int CountCellOverrides()
    {
        var n = 0;

        foreach (var cell in Settings.Keys)
        {
            if (cell.TextColorCycleOverride is not null
                || cell.BackgroundColorCycleOverride is not null
                || cell.PressedTextColorCycleOverride is not null
                || cell.PressedBackgroundColorCycleOverride is not null
                || cell.BorderColorCycleOverride is not null
                || cell.ShadowColorCycleOverride is not null)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// 自检用：两个颜色的色相角之差（归一化 0~360）。用来判断"同一段时间各自走了多远"。
    /// </summary>
    private static double HueDelta(Color from, Color to)
    {
        static double Hue(Color c)
        {
            var r = c.R / 255.0;
            var g = c.G / 255.0;
            var b = c.B / 255.0;

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var d = max - min;

            if (d <= 1e-9)
            {
                return 0;
            }

            double h;
            if (Math.Abs(max - r) < 1e-9)
            {
                h = 60 * (((g - b) / d) % 6);
            }
            else if (Math.Abs(max - g) < 1e-9)
            {
                h = 60 * (((b - r) / d) + 2);
            }
            else
            {
                h = 60 * (((r - g) / d) + 4);
            }

            return ((h % 360) + 360) % 360;
        }

        var delta = Hue(to) - Hue(from);
        return ((delta % 360) + 360) % 360;
    }

    private static string Describe(Color? color) =>
        color is Color c ? string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B) : "(无)";

    /// <summary>
    /// 开发期用：设了 <c>MCO_CYCLE_TRACE=1</c> 时，在**真实消息循环**里隔几秒记一行悬浮窗颜色，
    /// 用来验"循环定时器到底有没有在推颜色"。
    ///
    /// 为什么不并进自检：自检是在 <c>Initialize</c> 里同步跑的，那时消息循环还没启动，
    /// `DispatcherTimer` 一次都不会触发 —— 只能手动调 <c>TickCycle()</c>，验不出"定时器自己会不会跑"。
    /// 这个方法用 `Dispatcher.BeginInvoke` 把采样排进 UI 队列，等消息循环转起来之后才真正执行。
    /// </summary>
    private void RunCycleTraceIfRequested()
    {
        if (Environment.GetEnvironmentVariable("MCO_CYCLE_TRACE") != "1")
        {
            return;
        }

        LogLine(string.Format("循环轨迹开始记录：AnyColorCycle={0}，每 100ms 采一次，共 30 次（约 3 秒）。",
            Settings.AnyColorCycle));

        var remaining = 30;

        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };

        timer.Tick += (_, _) =>
        {
            var color = SnapshotFirstTextColor();
            var progress = CycleProgressNow;

            LogLine(string.Format("循环轨迹 #{0:00}：progress={1:0.###} 悬浮窗第一格文字={2}",
                31 - remaining, progress, Describe(color)));

            if (--remaining <= 0)
            {
                timer.Stop();
                LogLine("循环轨迹记录结束。");
            }
        };

        timer.Start();
    }

    /// <summary>
    /// 开发期用：设了 <c>MCO_UI_SNAPSHOT=1</c> 时，把配置页离屏渲染成 PNG 存到插件目录，
    /// 用来**目视核对排版** —— 哪些控件挤在一起、边框有没有吃掉输入框的内容，
    /// 光看 XAML 是看不出来的，必须渲出来看。
    ///
    /// 关键一步：宿主主题字典挂在**主窗口**的 Resources 上，页面单独渲染时 DynamicResource 解析不到，
    /// 渲出来会是一堆没样式的裸控件，没有参考价值。所以这里把主窗口的合并字典并进页面自己的
    /// Resources 再渲染 —— 只读宿主资源，不碰宿主的视觉树。
    /// </summary>
    private void RunUiSnapshotIfRequested()
    {
        if (Environment.GetEnvironmentVariable("MCO_UI_SNAPSHOT") != "1")
        {
            return;
        }

        KeyDisplayPage? page = null;

        try
        {
            page = new KeyDisplayPage(this);

            var main = Application.Current?.MainWindow;
            if (main is not null)
            {
                foreach (var dictionary in main.Resources.MergedDictionaries)
                {
                    page.Resources.MergedDictionaries.Add(dictionary);
                }
            }

            Brush background = Brushes.White;
            try
            {
                if (main?.TryFindResource("WindowBgBrush") is Brush found)
                {
                    background = found;
                }
            }
            catch
            {
            }

            // 把「选中格子」的状态也渲出来 —— 属性区默认是折叠的，不选中就看不见输入框那一片
            page.SelectFirstCellForSnapshot();

            var frame = new Border
            {
                Background = background,
                Padding = new Thickness(24, 20, 24, 20),
                Child = page,
            };

            const double width = 960;
            frame.Measure(new Size(width, double.PositiveInfinity));
            frame.Arrange(new Rect(0, 0, width, frame.DesiredSize.Height));
            frame.UpdateLayout();

            // 离线渲染不会触发 Loaded（元素没挂进真正的 PresentationSource），
            // 这里手动补一次画布尺寸计算，模拟真实运行时的时机 —— 不然快照上会看到
            // 画布停在兜底尺寸上，跟"重载后画布偏小"那个 bug 长得一样，没法区分
            page.UpdateCanvasSize();
            frame.UpdateLayout();

            var height = Math.Max(1, frame.DesiredSize.Height);
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(frame);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var dir = _host?.PluginDirectory ?? Path.GetTempPath();
            var path = Path.Combine(dir, "ui-snapshot.png");
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            LogLine(string.Format("界面快照已保存：{0}（{1:0}×{2:0}）", path, width, height));

            // 顺带把开关的实际勾选状态记一笔：填充控件值时那个 _loading 闸门要是搞反了，
            // 光看图不容易分辨，但日志里一眼就能看出来
            LogLine(string.Format("快照时控件状态：显示={0} 穿透={1} 吸附={2} 刷新档={3}",
                page.EnabledCheckBox.IsChecked, page.ClickThroughCheckBox.IsChecked,
                page.SnapCheckBox.IsChecked, page.RefreshComboBox.SelectedIndex));

            LogLine(string.Format("果冻控件：开关={0}，力度滑块可见性={1}",
                page.DynamicCheckBox.IsChecked, page.DynamicForcePanel.Visibility));

            // 显隐两个分支都要走一遍：他关着果冻时滑块必须藏起来，开着才出现
            try
            {
                var wasChecked = page.DynamicCheckBox.IsChecked;

                page.DynamicCheckBox.IsChecked = false;
                var hiddenWhenOff = page.DynamicForcePanel.Visibility;

                page.DynamicCheckBox.IsChecked = true;
                var shownWhenOn = page.DynamicForcePanel.Visibility;

                page.DynamicCheckBox.IsChecked = wasChecked;

                LogLine(hiddenWhenOff == Visibility.Collapsed && shownWhenOn == Visibility.Visible
                    ? "果冻滑块显隐自检通过：关掉果冻时力度滑块藏起来，打开才出现。"
                    : string.Format("果冻滑块显隐自检失败：关={0}，开={1}。", hiddenWhenOff, shownWhenOn));
            }
            catch (Exception dynEx)
            {
                LogLine("果冻滑块显隐自检异常：" + dynEx.Message);
            }

            LogLine(string.Format("透明度滑块核对：全局[文字={0}% 背景={1}% 按下文字={2}% 按下背景={3}% 边框={4}%] 属性区[文字={5}% 背景={6}%]",
                page.IdleTextAlphaSlider.Value, page.IdleBgAlphaSlider.Value,
                page.PressedTextAlphaSlider.Value, page.PressedBgAlphaSlider.Value,
                page.BorderAlphaSlider.Value, page.CellTextAlphaSlider.Value, page.CellBgAlphaSlider.Value));

            LogLine(string.Format("局部外观数值显示核对：字号「{0}」圆角「{1}」边框「{2}」阴影模糊「{3}」偏移「{4}」浓淡「{5}」方向「{6}」",
                page.CellFontSizeText.Text, page.CellCornerText.Text, page.CellBorderThicknessText.Text,
                page.CellShadowBlurText.Text, page.CellShadowOffsetText.Text,
                page.CellShadowOpacityText.Text, page.CellShadowDirectionText.Text));

            // 键位下拉要「全面覆盖」：项数应该和虚拟键码区间一样多
            LogLine(string.Format("键位下拉自检：共 {0} 项（虚拟键 0x{1:X2}~0x{2:X2} 是 {3} 个）",
                page.CommonKeyComboBox.Items.Count, VirtualKeys.MinCode, VirtualKeys.MaxCode,
                VirtualKeys.MaxCode - VirtualKeys.MinCode + 1));

            // 画布上的格子要带右键菜单
            try
            {
                var firstCell = page.DesignCanvas.Children.OfType<Border>().FirstOrDefault();
                var cellMenu = firstCell?.ContextMenu;

                LogLine(cellMenu is not null && cellMenu.Items.Count > 0
                    ? string.Format("右键菜单自检通过：格子带 {0} 个菜单项（第一个是「{1}」）。",
                        cellMenu.Items.Count, (cellMenu.Items[0] as MenuItem)?.Header)
                    : "右键菜单自检失败：格子没挂上 ContextMenu。");
            }
            catch (Exception menuEx)
            {
                LogLine("右键菜单自检异常：" + menuEx.Message);
            }

            // 再来一张画布区域的 2 倍放大图 —— 网格线、滚动条、把手这些细节，整页图上看不清
            try
            {
                var zoomTarget = page.CanvasScroll;
                var zoomWidth = zoomTarget.ActualWidth;
                var zoomHeight = zoomTarget.ActualHeight;

                if (zoomWidth > 1 && zoomHeight > 1)
                {
                    var zoomBitmap = new RenderTargetBitmap(
                        (int)Math.Ceiling(zoomWidth * 2), (int)Math.Ceiling(zoomHeight * 2),
                        192, 192, PixelFormats.Pbgra32);
                    zoomBitmap.Render(zoomTarget);

                    var zoomEncoder = new PngBitmapEncoder();
                    zoomEncoder.Frames.Add(BitmapFrame.Create(zoomBitmap));

                    var zoomPath = Path.Combine(dir, "ui-snapshot-zoom.png");
                    using (var stream = File.Create(zoomPath))
                    {
                        zoomEncoder.Save(stream);
                    }

                    LogLine(string.Format("画布放大图已保存：{0}（{1:0}×{2:0} @2x）", zoomPath, zoomWidth, zoomHeight));
                }
            }
            catch (Exception zoomEx)
            {
                LogLine("画布放大图失败：" + zoomEx.Message);
            }

            // 取色窗是个独立 Window，不在这张页面里，得单独渲一张 —— 用户最先问的就是它
            try
            {
                var picker = new ColorPickerWindow("#FF3B82F6");

                if (picker.Content is FrameworkElement pickerContent)
                {
                    // 按**自然宽度**量（无限宽）：这样渲出来的宽度就是"色板需要多宽"，
                    // 一眼能看出它在窗口里放不放得下
                    pickerContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    pickerContent.Arrange(new Rect(0, 0, pickerContent.DesiredSize.Width, pickerContent.DesiredSize.Height));
                    pickerContent.UpdateLayout();

                    var pickerWidth = pickerContent.ActualWidth;
                    var pickerHeight = pickerContent.ActualHeight;

                    var pickerBitmap = new RenderTargetBitmap(
                        (int)Math.Ceiling(pickerWidth * 2), (int)Math.Ceiling(pickerHeight * 2),
                        192, 192, PixelFormats.Pbgra32);
                    pickerBitmap.Render(pickerContent);

                    var pickerEncoder = new PngBitmapEncoder();
                    pickerEncoder.Frames.Add(BitmapFrame.Create(pickerBitmap));

                    var pickerPath = Path.Combine(dir, "ui-snapshot-picker.png");
                    using (var stream = File.Create(pickerPath))
                    {
                        pickerEncoder.Save(stream);
                    }

                    LogLine(string.Format("取色窗快照已保存：{0}（{1:0}×{2:0} @2x）", pickerPath, pickerWidth, pickerHeight));
                }
            }
            catch (Exception pickerEx)
            {
                LogLine("取色窗快照失败：" + pickerEx.Message);
            }
        }
        catch (Exception ex)
        {
            LogLine("界面快照失败：" + ex);
        }
        finally
        {
            page?.StopTimers();
        }
    }

    /// <summary>
    /// 自检：宿主同款调色盘（<c>System.Windows.Forms.ColorDialog</c>）在插件的加载上下文里能不能用。
    ///
    /// 这条必须验：网易云歌词插件当年就在这儿栽过（调不起来，表现是"点了选色没反应、
    /// 色块还是原来的"）。我们的取色是「系统调色盘优先、自绘窗兜底」，所以知道实际走哪条路很重要。
    /// </summary>
    private void RunColorDialogProbe()
    {
        try
        {
            using var probe = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
            };

            LogLine("调色盘自检：System.Windows.Forms.ColorDialog 可用 —— 点色块会弹宿主同款调色盘。");
        }
        catch (Exception ex)
        {
            LogLine("调色盘自检失败，会自动退回自绘的 RGB/A 滑块窗：" + ex.Message);
        }
    }

    /// <summary>
    /// 自检：页面上用的宿主样式键还在不在。
    /// DynamicResource 找不到键是**静默**退化的（退回默认外观、不报错），
    /// 所以只能靠这样主动查一遍宿主主窗口的资源字典（只读，不改任何东西）。
    /// </summary>
    private void PreflightHostStyles()
    {
        string[] keys =
        {
            "CardStyle", "CardTitleStyle", "CardDescStyle", "FieldLabelStyle", "HintStyle",
            "SmallButtonStyle", "SmallPrimaryButtonStyle", "SmallGhostButtonStyle", "ToggleSwitchStyle",
            "TextSecondaryBrush", "BorderStrongBrush", "BorderBrush", "Surface2Brush", "AccentBrush",
        };

        try
        {
            var main = Application.Current?.MainWindow;
            if (main is null)
            {
                LogLine("样式自检：跳过（拿不到宿主主窗口）。");
                return;
            }

            var missing = keys.Where(k => !main.Resources.Contains(k)).ToList();
            LogLine(missing.Count == 0
                ? $"样式自检通过：{keys.Length} 个宿主样式键全部可用。"
                : "样式自检：宿主里找不到这些键（页面外观会退化）→ " + string.Join(", ", missing));
        }
        catch (Exception ex)
        {
            LogLine("样式自检失败：" + ex.Message);
        }
    }

    internal void LogLine(string message)
    {
        try
        {
            var dir = _host?.PluginDirectory;
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "plugin.log");

            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }

            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    /// <summary>按 100% 缩放写死的位置在 150% 下会跑到屏幕外（DEV-NOTES 坑 46），显示前纠一次。</summary>
    private void ClampSavedPosition()
    {
        try
        {
            var area = SystemParameters.WorkArea;
            var left = Settings.WindowLeft;
            var top = Settings.WindowTop;

            var outside = left < area.Left - 40 || left > area.Right - 40 ||
                          top < area.Top - 40 || top > area.Bottom - 40;

            if (outside)
            {
                Settings.WindowLeft = area.Left + 60;
                Settings.WindowTop = area.Top + 60;
            }
        }
        catch
        {
        }
    }
}
