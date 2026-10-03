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
/// ⚠ 只读，不监听：全程用 <c>GetAsyncKeyState</c> 轮询自己关心的那几个键，
///    **不安装任何键盘钩子、不拦截按键、不记录、不落盘**（详见 VirtualKeys 的注释）。
///    这个插件对系统输入链路是零侵入的 —— 它做的事情和你在任务管理器里看 CPU 占用是一类事。
/// </summary>
public sealed class KeyDisplayPlugin : IPlugin
{
    public const string PluginId = "goldiamond.keydisplay";

    /// <summary>插件页的副标题。</summary>
    private const string PageDescription = "高度自定义的按键显示，可选中单个按键进行设置";

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KeyeventfKeyup = 0x0002;

    /// <summary>自检专用的键：右 Shift —— 无副作用，游戏里基本不用。</summary>
    private const int SelfTestKey = 0xA1;

    private IPluginHost? _host;
    private KeyOverlayWindow? _window;
    private KeyDisplayPage? _page;
    private DispatcherTimer? _timer;

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

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(8, Settings.RefreshMs)),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

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
        LogLine("按键读取方式：GetAsyncKeyState 轮询（未安装任何键盘钩子）");

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

        // 开发期用：把配置页渲染成 PNG，用来目视核对排版（见方法注释）
        RunUiSnapshotIfRequested();

        host.Log($"[按键显示] 已装载，{Settings.Keys.Count} 个按键，刷新 {Settings.RefreshMs}ms");
    }

    public void Shutdown()
    {
        try { _timer?.Stop(); } catch { }
        _timer = null;

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
        window.Closing += (_, args) =>
        {
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

    /// <summary>设置变了：重建窗口内容 + 对齐显示状态 + 调刷新节奏。</summary>
    public void OnSettingsChanged()
    {
        try
        {
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

        return $"正在显示 {keys} 个按键 · 当前按下 {pressed} 个 · 刷新 {Settings.RefreshMs}ms · {through}";
    }

    public void SaveSettings()
    {
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
    /// 数据链路自检：设了 <c>MCO_KEY_SELFTEST=1</c> 时才会跑。
    ///
    /// 做法是**发一个真实的按键**（右 Shift，无副作用；DEV-NOTES 里验证输入类插件的既定做法），
    /// 然后看轮询能不能读到它 —— 这验证的是"读到的是真实按键状态"，而不是在读自己的缓存。
    /// 检测前后都会把按键抬起来，不留残留。
    /// </summary>
    private void RunSelfTestIfRequested()
    {
        if (Environment.GetEnvironmentVariable("MCO_KEY_SELFTEST") != "1")
        {
            return;
        }

        LogLine("=== 自检开始（发一个真实的右 Shift，看轮询读不读得到）===");

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

            // 局部外观自检：给一格单独设字号，看它是不是只影响那一格
            try
            {
                var probeCell = Settings.Keys.FirstOrDefault(k => k.VirtualKey == SelfTestKey)
                    ?? Settings.Keys.FirstOrDefault();

                if (probeCell is not null)
                {
                    var globalSize = Settings.FontSize;

                    probeCell.FontSizeOverride = 40;
                    window.Rebuild(Settings);
                    var overridden = window.GetCellFontSize(probeCell.VirtualKey);

                    probeCell.FontSizeOverride = -1;
                    window.Rebuild(Settings);
                    var restored = window.GetCellFontSize(probeCell.VirtualKey);

                    var othersUntouched = Settings.Keys
                        .Where(k => !ReferenceEquals(k, probeCell))
                        .All(k => Math.Abs(window.GetCellFontSize(k.VirtualKey) - globalSize) < 0.5);

                    LogLine(overridden > 39.5 && Math.Abs(restored - globalSize) < 0.5 && othersUntouched
                        ? string.Format("局部外观自检通过：单独设的那格是 {0:0}，清除后回到全局 {1:0}，其它格一直是 {1:0}。",
                            overridden, restored)
                        : string.Format("局部外观自检失败：覆盖后={0:0} 清除后={1:0} 其它格未受影响={2}。",
                            overridden, restored, othersUntouched));
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
        }
        catch (Exception ex)
        {
            LogLine("自检异常：" + ex);
        }
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
