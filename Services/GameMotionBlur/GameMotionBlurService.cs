using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MinecraftChatOverlay.Models;

namespace MinecraftChatOverlay.Services.GameMotionBlur;

/// <summary>
/// 界面用的门面：把"注入 / 开关 / 调参 / 导出帧 / 看状态"包成几个方法。
///
/// 整条链路是：
///   界面 → 共享内存控制块（本类）→ 游戏进程里的钩子 DLL → OpenGL SwapBuffers
/// 游戏进程只读共享内存，所以调参数是"改一次立刻生效"，不用重新注入。
/// </summary>
public sealed class GameMotionBlurService : IDisposable
{
    private MotionBlurControl? _control;
    private int _targetPid;

    /// <summary>当前是不是已经往某个进程里注入过。</summary>
    public int TargetProcessId => _targetPid;

    /// <summary>钩子 DLL 最终用的路径（成功解析后非空）。</summary>
    public string HookDllPath { get; private set; } = "";

    /// <summary>列出所有"有可见窗口"的进程，供界面选择。</summary>
    public List<GameProcessInfo> ListTargets() => GameProcessInjector.FindCandidateProcesses();

    /// <summary>
    /// 找一个能用的钩子 DLL：
    ///   1. 用户指定的路径；
    ///   2. 程序目录（发布版会把 DLL 复制到这里）；
    ///   3. 向上找 native\dist（开发时直接跑 bin\Debug 也能用）。
    /// </summary>
    public static string ResolveHookDllPath(string configuredPath)
    {
        const string fileName = "GameMotionBlurHook.dll";

        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "native", "dist", fileName)
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir != null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, "native", "dist", fileName));
            candidates.Add(Path.Combine(dir.FullName, fileName));
            dir = dir.Parent;
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, fileName);
    }

    /// <summary>
    /// 注入到目标进程，然后等钩子真正装好（DLL 是异步装钩子的，要等一小会儿）。
    /// 失败会抛 InvalidOperationException，消息可以直接弹给用户看。
    /// </summary>
    public MotionBlurStatus Inject(int pid, string configuredDllPath)
    {
        if (pid <= 0)
        {
            throw new InvalidOperationException("请先选择一个目标游戏进程。");
        }

        var dll = ResolveHookDllPath(configuredDllPath);
        HookDllPath = dll;

        if (!File.Exists(dll))
        {
            throw new InvalidOperationException(
                "找不到钩子 DLL：" + dll + Environment.NewLine +
                "先编译一下原生部分：pwsh -File native\\build.ps1（见 native\\README.md）");
        }

        // 控制块要先建好，DLL 一进去就能看到参数
        _control ??= new MotionBlurControl();
        _control.Apply(false, 0.55f, 0, 0, null);

        GameProcessInjector.Inject(pid, dll);
        _targetPid = pid;

        // 等 DLL 把钩子挂上（它是在自己的线程里做的）
        var status = WaitForHook(TimeSpan.FromSeconds(4));
        return status;
    }

    /// <summary>轮询等钩子装好。</summary>
    public MotionBlurStatus WaitForHook(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = new MotionBlurStatus();

        while (DateTime.UtcNow < deadline)
        {
            last = GetStatus();

            // 钩子挂上就算接管成功。
            // 原来看的是"既挂了钩子又已经出帧"，于是游戏还在加载画面/窗口没刷的时候
            // 会被判成"注入失败"，用户只好反复点注入。出不出帧交给状态栏另说。
            if (last.HookInstalled)
            {
                return last;
            }

            System.Threading.Thread.Sleep(120);
        }

        return last;
    }

    public MotionBlurStatus GetStatus()
    {
        _control ??= new MotionBlurControl();
        return _control.GetStatus();
    }

    /// <summary>校验 C++ / C# 两边的结构体布局是否一致。</summary>
    public bool VerifyLayout(out string error)
    {
        _control ??= new MotionBlurControl();
        return _control.VerifyLayout(out error);
    }

    /// <summary>把设置推给钩子（开关 + 强度）。</summary>
    public void Apply(MotionBlurSettings settings)
    {
        _control ??= new MotionBlurControl();

        var strength = (float)Math.Clamp(settings.Strength, 0.05, 0.95);
        _control.SetEnabled(settings.Enabled, strength);
    }

    public void SetEnabled(bool enabled, double strength)
    {
        _control ??= new MotionBlurControl();
        _control.SetEnabled(enabled, (float)Math.Clamp(strength, 0.05, 0.95));
    }

    public void SetStrength(double strength)
    {
        _control ??= new MotionBlurControl();
        _control.SetStrength((float)Math.Clamp(strength, 0.05, 0.95));
    }

    /// <summary>清空历史帧（切场景 / 传送之后想立刻清掉拖影时用）。</summary>
    public void ResetHistory()
    {
        _control ??= new MotionBlurControl();
        _control.RequestResetHistory();
    }

    /// <summary>把接下来几帧导成 BMP（调试 / 验收用）。</summary>
    public void RequestDump(string directory, int frames)
    {
        _control ??= new MotionBlurControl();
        _control.RequestDump(directory, (uint)Math.Clamp(frames, 1, 600));
    }

    /// <summary>请游戏里的 DLL 重新装钩子（卸载过之后不想重启游戏时用）。</summary>
    public void RequestReinstall()
    {
        _control ??= new MotionBlurControl();
        _control.RequestReinstall();
    }

    /// <summary>
    /// 通知游戏进程里的钩子：刚发生了一次击杀，播一次视觉反馈。
    ///
    /// 和帧混合完全解耦 —— 帧混合关着也照样能放（原生侧会各自判断）。
    /// 还没注入的话这次调用只会写进共享内存，没人读，无副作用。
    /// </summary>
    public void RequestKillFeedback(KillFeedbackEffectKind kind, double strength, double durationMs)
    {
        _control ??= new MotionBlurControl();
        _control.RequestKillFeedback(
            kind,
            (float)Math.Clamp(strength, 0.0, 5.0),
            (uint)Math.Clamp(durationMs, 60.0, 5000.0));
    }

    /// <summary>写入多效果配置（每个效果独立大小，0 ~ 5.0 = 0 ~ 500%）。</summary>
    public void WriteKillEffects(System.Collections.Generic.IReadOnlyList<KillEffectConfig> effects)
    {
        _control ??= new MotionBlurControl();
        _control.WriteKillEffects(effects);
    }

    /// <summary>触发一次"按当前多效果配置播放"的击杀反馈。</summary>
    public void RequestKillFeedbackEffects(double durationMs)
    {
        _control ??= new MotionBlurControl();
        _control.RequestKillFeedbackEffects((uint)Math.Clamp(durationMs, 60.0, 5000.0));
    }

    /// <summary>卸载钩子：游戏进程里的 IAT 槽位会被还原，不用重启游戏。</summary>
    public void Unhook()
    {
        _control ??= new MotionBlurControl();
        _control.RequestUnhook();
    }

    /// <summary>目标进程是不是还活着。</summary>
    public bool IsTargetAlive()
    {
        if (_targetPid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(_targetPid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读钩子日志的末尾若干行（DLL 把日志写在它自己旁边）。</summary>
    public string ReadHookLog(int lines)
    {
        if (_targetPid <= 0 || string.IsNullOrEmpty(HookDllPath))
        {
            return "";
        }

        try
        {
            var directory = Path.GetDirectoryName(HookDllPath) ?? AppContext.BaseDirectory;
            var logPath = Path.Combine(directory, "gmb_hook_" + _targetPid + ".log");
            if (!File.Exists(logPath))
            {
                return "";
            }

            // DLL 一直开着这个文件（我们只读，用 FileShare.ReadWrite 才能读到）
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var all = reader.ReadToEnd().Split('\n');
            return string.Join(Environment.NewLine, all.TakeLast(lines)).TrimEnd();
        }
        catch
        {
            return "";
        }
    }

    public string DescribeHookLogPath()
    {
        if (string.IsNullOrEmpty(HookDllPath))
        {
            return "";
        }

        var directory = Path.GetDirectoryName(HookDllPath) ?? "";
        return Path.Combine(directory, "gmb_hook_" + _targetPid + ".log");
    }

    public void Dispose()
    {
        _control?.Dispose();
        _control = null;
    }
}