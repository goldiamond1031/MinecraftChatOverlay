using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.ProcessFps;

/// <summary>
/// 「游戏 FPS」插件。
///
/// 做的事就一件：按固定节奏读一次宿主动态模糊钩子写出的实测帧率，拼成
/// 「前缀 + 数字 + 后缀」丢进自己的悬浮窗；外观、位置、刷新节奏全部可配。
///
/// 为什么帧率要绕道动态模糊的共享内存，而不是插件自己测：
/// 插件跑在宿主进程里，**看不到目标进程出了多少帧**，而 <c>IPluginHost</c> 也没有注入能力。
/// 宿主本来就已经在目标进程里挂了一个 OpenGL 出帧钩子（数 SwapBuffers），
/// 那个数字是最准的，直接读它是唯一不改宿主又能拿到真值的办法。
/// 代价是：目标进程必须先注入过（见 <see cref="FpsSharedMemory"/> 的说明）。
/// </summary>
public sealed class ProcessFpsPlugin : IPlugin
{
    public const string PluginId = "goldiamond.processfps";

    private IPluginHost? _host;
    private FpsSharedMemory? _source;
    private FpsOverlayWindow? _window;
    private ProcessFpsPage? _page;
    private DispatcherTimer? _timer;

    private FpsSample _lastSample;

    /// <summary>上一次的连接状态（空串 = 正常）。用来判断"状态变了没"，别每 250 毫秒刷一条日志。</summary>
    private string _lastNote = "";

    public string Id => PluginId;

    public string DisplayName => "游戏 FPS";

    /// <summary>插件自己那本设置。</summary>
    public ProcessFpsSettings Settings { get; private set; } = new();

    // ===================== 生命周期 =====================

    public void Initialize(IPluginHost host)
    {
        _host = host;

        try
        {
            Settings = PluginSettingsFile.Load<ProcessFpsSettings>(host.PluginDirectory);
        }
        catch
        {
            Settings = new ProcessFpsSettings();
        }

        ClampSavedPosition();

        _source = new FpsSharedMemory();

        RunSelfTestIfRequested();

        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(50, Settings.RefreshMs)),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        host.RegisterPage(new PluginPage
        {
            Title = "游戏 FPS",
            Description = "把目标进程的帧率显示成一个可自定义的悬浮窗。帧率读自动态模糊注入的钩子，目标进程需要先注入过一次。",
            ContentFactory = () =>
            {
                var page = new ProcessFpsPage(this);
                _page = page;
                return page;
            },
            StatusText = BuildStatus,
        });

        // 先采一轮，让窗口有机会立刻出来
        Tick();

        LogLine($"已装载。共享内存={FpsSharedMemory.MapName} 刷新={Settings.RefreshMs}ms 目标={DescribeTarget().TrimStart(' ', '·')}");
        LogLine("首轮读数：" + BuildStatus());

        // 页面 XAML 是**运行期**解析的，编译过不代表能解析（DEV-NOTES 坑 34）。
        // 这里提前构造一次，失败就记进 plugin.log —— 省得用户点进那一页才发现白屏。
        try
        {
            _ = new ProcessFpsPage(this);
            LogLine("页面预检通过：自绘 XAML 解析正常。");
        }
        catch (Exception ex)
        {
            LogLine("页面预检失败（打开插件页会白屏）：" + ex);
        }

        PreflightHostStyles();

        host.Log($"[游戏 FPS] 已装载。共享内存={FpsSharedMemory.MapName}，刷新={Settings.RefreshMs}ms");
    }

    /// <summary>
    /// 自检：设了环境变量 <c>MCO_FPS_SELFTEST=1</c> 才会跑。
    ///
    /// 做法是往控制块里写一组**已知的绝对偏移**数据，再走一遍真实的 <see cref="FpsSharedMemory.Sample"/>，
    /// 看能不能读回那个值。关键在于写入用的是**从 native\common.h 手算出来的偏移**（40 / 80 / 88 / 96 / 120），
    /// 而不是本端结构体算出来的偏移 —— 两边要是对不上，自检就会失败，而不是"自己验自己"（DEV-NOTES 坑 37）。
    ///
    /// 排查/回归时配合 GMBLUR_MAP 指一个隔离的名字用，免得污染真实控制块。
    /// </summary>
    private void RunSelfTestIfRequested()
    {
        if (Environment.GetEnvironmentVariable("MCO_FPS_SELFTEST") != "1" || _source is null)
        {
            return;
        }

        LogLine("=== 自检开始（写入端用 common.h 手算的绝对偏移）===");

        try
        {
            using var map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(
                FpsSharedMemory.MapName, 4096);
            using var view = map.CreateViewAccessor();

            view.Write(0, 0x424C4D47u);      // magic
            view.Write(4, 6u);               // version
            view.Write(8, 184u);             // structSize（= 宿主报的真值）
            view.Write(12, 9u);              // seq
            view.Write(32, 1u);              // hookInstalled
            view.Write(36, 1u);              // deviceKind = OpenGL
            view.Write(40, 654321u);         // presentCount
            view.Write(44, 400000u);         // blendCount
            view.Write(48, 1920u);           // width
            view.Write(52, 1080u);           // height
            view.Write(80, 104u);            // probe[3] = offsetof(lastPresentQpc)
            view.Write(88, 0xDEADBEEFu);     // probe[5] = 哨兵
            view.Write(96, 40u);             // probe[7] = offsetof(presentCount)
            view.Write(104, 987654321ul);    // lastPresentQpc
            view.Write(112, 10000000ul);     // qpcFrequency
            view.Write(120, 143700u);        // fpsMilli = 143.7
            view.Write(124, 4u);             // blendFrames

            var sample = _source.Sample();
            var pass = sample.State == FpsLinkState.Ok && Math.Abs(sample.Fps - 143.7) < 0.001;

            LogLine(pass
                ? $"自检通过：读回 {sample.Fps} fps，画面 {sample.Width}×{sample.Height}，累计出帧 {sample.PresentCount}"
                : $"自检失败：状态={sample.State} 读到={sample.Fps} 详情={sample.Detail}");

            LogLine("自检原始字段：" + _source.LastRawSummary);
        }
        catch (Exception ex)
        {
            LogLine("自检异常：" + ex);
        }
    }

    /// <summary>
    /// 自检：页面上用的那些宿主样式键还在不在。
    ///
    /// 为什么要这一步：<c>DynamicResource</c> 找不到键是**静默**的（退回默认外观，不抛异常），
    /// 宿主一旦改名，页面就会变得又丑又不报错，很难查。这里只**读**宿主主窗口的资源字典
    /// （不改任何东西），把结果写进 plugin.log，下次一出问题就能一眼看到是哪个键没了。
    /// </summary>
    private void PreflightHostStyles()
    {
        string[] keys =
        {
            "CardStyle", "CardTitleStyle", "CardDescStyle", "FieldLabelStyle", "HintStyle",
            "SmallButtonStyle", "SmallPrimaryButtonStyle", "SmallGhostButtonStyle", "ToggleSwitchStyle",
            "TextSecondaryBrush", "BorderStrongBrush",
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

    public void Shutdown()
    {
        try
        {
            _timer?.Stop();
        }
        catch
        {
        }

        _timer = null;

        try
        {
            _window?.Close();
        }
        catch
        {
        }

        _window = null;
        _page = null;

        try
        {
            _source?.Dispose();
        }
        catch
        {
        }

        _source = null;
        LogLine("已卸载。");
        _host?.Log("[游戏 FPS] 已卸载。");
        _host = null;
    }

    // ===================== 采样循环 =====================

    private void Tick()
    {
        var source = _source;
        if (source is null)
        {
            return;
        }

        FpsSample sample;
        try
        {
            sample = source.Sample();
        }
        catch (Exception ex)
        {
            // Sample 自己会兜住异常，这里是最后一道保险：采样线程绝不能被搞挂
            LogLine("采样异常：" + ex.Message);
            return;
        }

        _lastSample = sample;
        ApplySampleText(sample);

        try
        {
            _page?.OnSample(sample, BuildStatus());
        }
        catch
        {
        }

        // 目标进程换了 / 退出了，只在状态变化时说一次，别每 250 毫秒刷屏
        var note = sample.State switch
        {
            FpsLinkState.Ok => "",
            FpsLinkState.NotFound => "没连上动态模糊控制块",
            FpsLinkState.Incompatible => "控制块布局不兼容",
            FpsLinkState.NoFrames => "钩子在，但还没出帧",
            _ => "",
        };

        if (note != _lastNote)
        {
            _lastNote = note;

            // 连上的那一刻也记一笔 —— 排查时最想看到的就是"什么时候开始有数的"
            var text = note.Length > 0
                ? sample.Detail
                : $"已连上，开始读数：{FormatFps(sample.Fps)}（画面 {sample.Width}×{sample.Height}，累计出帧 {sample.PresentCount}）";

            LogLine("[状态变化] " + text + " ｜ " + (_source?.LastRawSummary ?? ""));
            _host?.Log("[游戏 FPS] " + text);
        }
    }

    /// <summary>按采样结果决定：显示什么文字、窗口该不该露脸。</summary>
    private void ApplySampleText(FpsSample sample)
    {
        var s = Settings;

        if (!s.Enabled)
        {
            HideOverlay();
            return;
        }

        if (sample.State == FpsLinkState.Ok)
        {
            _window ??= CreateWindow();
            _window.SetText(FormatFps(sample.Fps));
            ShowOverlay();
            return;
        }

        if (s.HideWhenNoData)
        {
            HideOverlay();
            return;
        }

        _window ??= CreateWindow();
        _window.SetText(s.NoDataText);
        ShowOverlay();
    }

    private string FormatFps(double fps)
    {
        var decimals = Math.Clamp(Settings.Decimals, 0, 2);
        var text = fps.ToString("F" + decimals);
        return Settings.Prefix + text + Settings.Suffix;
    }

    // ===================== 悬浮窗 =====================

    private FpsOverlayWindow CreateWindow()
    {
        var window = new FpsOverlayWindow();

        // 关闭只许"藏起来"：窗口一旦真 Close 掉，WPF 不允许再 Show 同一个实例
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
                _page?.RefreshPositionText();
                SaveSettings();
            }
            catch
            {
            }
        };

        window.ApplySettings(Settings);
        return window;
    }

    private void ShowOverlay()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        if (!window.IsVisible)
        {
            try
            {
                window.Show();
                window.DetachFromOwner();
            }
            catch (Exception ex)
            {
                _host?.Log("[游戏 FPS] 显示悬浮窗失败：" + ex.Message);
            }
        }
    }

    private void HideOverlay()
    {
        var window = _window;
        if (window is null)
        {
            return;
        }

        if (window.IsVisible)
        {
            try
            {
                window.Hide();
            }
            catch
            {
            }
        }
    }

    /// <summary>悬浮窗当前实际宽度（页面里"移到右上角"要用）。</summary>
    public double OverlayWidth
    {
        get
        {
            try
            {
                return _window?.ActualWidth ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    // ===================== 页面回调 =====================

    /// <summary>设置里任何一项变了：立刻应用（外观 / 启停 / 刷新节奏）并让窗口跟上。</summary>
    public void OnSettingsChanged()
    {
        try
        {
            var window = _window;
            if (window is not null)
            {
                window.ApplySettings(Settings);
            }

            var interval = TimeSpan.FromMilliseconds(Math.Max(50, Settings.RefreshMs));
            if (_timer is not null && _timer.Interval != interval)
            {
                _timer.Interval = interval;
            }

            if (!Settings.Enabled)
            {
                HideOverlay();
            }
            else if (window is not null && _lastSample.State == FpsLinkState.Ok)
            {
                ShowOverlay();
            }
            else if (Settings.Enabled)
            {
                // 窗口还没建过（之前一直没数据）——让 Tick 去建
                Tick();
            }
        }
        catch (Exception ex)
        {
            _host?.Log("[游戏 FPS] 应用设置失败：" + ex.Message);
        }
    }

    /// <summary>页面按了"移到左上/右上角"。</summary>
    public void MoveOverlay()
    {
        try
        {
            if (_window is null)
            {
                return;
            }

            _window.Left = Settings.WindowLeft;
            _window.Top = Settings.WindowTop;
        }
        catch
        {
        }
    }

    /// <summary>目标换了。</summary>
    public void NoteTargetChanged()
    {
        _host?.Log($"[游戏 FPS] 目标改为 {Settings.TargetProcessName} (PID {Settings.TargetProcessId})");
    }

    /// <summary>转发宿主的游戏窗口列表（页面上的下拉框用）。</summary>
    public IReadOnlyList<GameWindowInfo> GetGameWindows()
    {
        try
        {
            return _host?.GetGameWindows() ?? (IReadOnlyList<GameWindowInfo>)Array.Empty<GameWindowInfo>();
        }
        catch
        {
            return Array.Empty<GameWindowInfo>();
        }
    }

    // ===================== 状态与诊断 =====================

    /// <summary>页头状态行（宿主每秒问一次）。</summary>
    public string BuildStatus()
    {
        var sample = _lastSample;
        var target = DescribeTarget();

        switch (sample.State)
        {
            case FpsLinkState.Ok:
                var size = sample.Width > 0 ? $"，画面 {sample.Width}×{sample.Height}" : "";
                return $"{FormatFps(sample.Fps)}{size} · 已出帧 {sample.PresentCount}{target}";

            case FpsLinkState.NotFound:
                return "未连接：还没读到动态模糊的控制块（去「游戏动态模糊」页点【注入并接管】）";

            case FpsLinkState.Incompatible:
                return "控制块布局不兼容：" + sample.Detail;

            case FpsLinkState.NoFrames:
                return sample.Detail;

            default:
                return "";
        }
    }

    private string DescribeTarget()
    {
        if (Settings.TargetProcessId <= 0)
        {
            return "";
        }

        var name = string.IsNullOrWhiteSpace(Settings.TargetProcessName)
            ? "目标进程"
            : Settings.TargetProcessName;

        return $" · 目标 {name} (PID {Settings.TargetProcessId})";
    }

    /// <summary>「排查用」那张卡片的正文。</summary>
    public string BuildDiagnostics()
    {
        var lines = new List<string>
        {
            "共享内存名：" + FpsSharedMemory.MapName,
            "连接状态：" + _lastSample.State,
            "原始字段：" + (_source?.LastRawSummary ?? "（无）"),
            "读到帧率：" + (_lastSample.State == FpsLinkState.Ok ? FormatFps(_lastSample.Fps) : "不可用"),
            "目标进程：" + DescribeTarget().TrimStart(' ', '·'),
            "刷新间隔：" + Settings.RefreshMs + " 毫秒",
        };

        if (_lastSample.Detail.Length > 0)
        {
            lines.Add("说明：" + _lastSample.Detail);
        }

        if ((_source?.LayoutError.Length ?? 0) > 0)
        {
            lines.Add("布局校验：" + _source!.LayoutError);
        }

        if (_lastNote.Length > 0)
        {
            lines.Add("最近一次状态提示：" + _lastNote);
        }

        lines.Add("");
        lines.Add("钩子每 250 毫秒会写一次 fpsMilli（用 QPC 时间差算 SwapBuffers 增量）。");
        lines.Add("如果 hookInstalled=0，说明那个进程还没被注入过；present=0 说明钩子挂上了但游戏还没出帧。");

        return string.Join("\n", lines);
    }

    /// <summary>把诊断信息写到插件目录，方便贴给别人看。</summary>
    public string DumpDiagnostics()
    {
        var dir = _host?.PluginDirectory ?? Path.GetTempPath();
        var path = Path.Combine(dir, "diagnostics.txt");

        var text = string.Join("\n", new[]
        {
            "游戏 FPS 插件诊断",
            "时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "宿主契约版本：" + (_host?.ApiVersion.ToString() ?? "?"),
            "",
            BuildDiagnostics(),
            "",
            "---- 本插件设置 ----",
            "开关=" + Settings.Enabled
                + "  刷新=" + Settings.RefreshMs + "ms"
                + "  前缀=「" + Settings.Prefix + "」"
                + "  后缀=「" + Settings.Suffix + "」"
                + "  小数位=" + Settings.Decimals,
            "字体=" + Settings.FontFamily + "  " + Settings.FontSize + "px  粗体=" + Settings.Bold
                + "  文字色=" + Settings.TextColor,
            "阴影=" + Settings.ShadowEnabled + " 色=" + Settings.ShadowColor
                + " 模糊=" + Settings.ShadowBlur + " 偏移=" + Settings.ShadowOffset
                + " 不透明度=" + Settings.ShadowOpacity + " 方向=" + Settings.ShadowDirection,
            "背景=" + Settings.BackgroundColor + "  圆角=" + Settings.CornerRadius
                + "  内边距=" + Settings.PaddingX + "," + Settings.PaddingY,
            "位置=" + Settings.WindowLeft + "," + Settings.WindowTop
                + "  穿透=" + Settings.ClickThrough
                + "  无数据时隐藏=" + Settings.HideWhenNoData
                + "  占位=「" + Settings.NoDataText + "」",
        });

        File.WriteAllText(path, text);
        return path;
    }

    // ===================== 杂项 =====================

    /// <summary>
    /// 按 100% 缩放写死的位置，在 150% / 200% 下会整块跑到屏幕外（DEV-NOTES 坑 46）——
    /// 显示之前统一纠一次。
    /// </summary>
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
                Settings.WindowLeft = area.Left + 40;
                Settings.WindowTop = area.Top + 40;
            }
        }
        catch
        {
        }
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

    /// <summary>
    /// 往插件目录写一行 plugin.log。
    /// 为什么落盘而不是只写宿主的调试后台：那个要用户自己开「启用后端日志」才看得见，
    /// 而落盘的日志用户能直接贴过来，也能拿来做自动验收。
    /// </summary>
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

            // 让它别无限涨
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
}
