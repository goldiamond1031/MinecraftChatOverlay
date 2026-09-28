using System;
using System.IO;
using System.Linq;
using System.Threading;
using MinecraftChatOverlay.Services.GameMotionBlur;

namespace MinecraftChatOverlay.Tools;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    return List();

                case "inject":
                    return Inject(args);

                case "off":
                    return SetEnabled(false, 0f);

                case "on":
                    return SetEnabled(true, args.Length > 1 ? float.Parse(args[1]) : 0.55f);

                case "dump":
                    return Dump(args);

                case "status":
                    return Status(1);

                case "watch":
                    return Status(20);

                case "reset":
                    using (var control = new MotionBlurControl())
                    {
                        control.RequestResetHistory();
                        Console.WriteLine("已请求清空历史帧");
                    }
                    return 0;

                case "reinstall":
                    using (var control = new MotionBlurControl())
                    {
                        control.RequestReinstall();
                        Console.WriteLine("已请求重新装钩子");
                    }
                    return 0;

                case "kill":
                    return Kill(args);

                case "killall":
                    return KillAll(args);

                case "unhook":
                    using (var control = new MotionBlurControl())
                    {
                        control.RequestUnhook();
                        Console.WriteLine("已请求卸载钩子（游戏里的 IAT 槽位会还原）");
                    }
                    return 0;

                default:
                    Usage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("出错：" + ex.Message);
            return 2;
        }
    }

    private static void Usage()
    {
        Console.WriteLine(@"用法：
  gmblur list                     列出有窗口的进程（找游戏 PID）
  gmblur inject <pid> [dll路径]    把钩子 DLL 注入进程
  gmblur on [强度0~0.95]           开启帧混合（默认 0.55）
  gmblur off                      关闭帧混合
  gmblur dump <目录> [帧数]        把接下来几帧导出成 BMP
  gmblur status                   看一次状态
  gmblur watch                    每 0.5 秒刷一次状态（Ctrl+C 退出）
  gmblur reset                    清空历史帧
  gmblur unhook                   卸载钩子（还原 IAT 槽位）
  gmblur reinstall                重新装钩子（卸载过、又不想重启游戏时用）
  gmblur kill [强度0~1] [时长ms]   发一次击杀反馈事件（默认 0.6 / 380ms）
  gmblur killall <缩放%> <边缘%> <暗角%> <色差%> <抖动%> [时长ms]
  gmblur killall <缩放%> <边缘%> <暗角%> <色差%> <抖动%> <闪光%> <冲击环%> <故障%> [时长ms]
                                    多效果同时触发，每个效果独立大小（0~500）");
    }

    /// <summary>
    /// 发一次击杀反馈事件，然后跟着看一秒，确认 DLL 真的接住了。
    /// 这是"不靠游戏就能验收事件通道"的手段 ——
    /// 光发出请求不算数，要看到 DLL 那边的累计次数涨了才算通。
    /// </summary>
    private static int Kill(string[] args)
    {
        var strength = args.Length > 1 ? float.Parse(args[1]) : 0.6f;
        var duration = args.Length > 2 ? uint.Parse(args[2]) : 380u;

        using (var control = new MotionBlurControl())
        {
            string error;
            if (!control.VerifyLayout(out error))
            {
                Console.WriteLine("结构体自检失败：" + error);
                return 3;
            }

            var before = control.GetStatus();

            control.RequestKillFeedback(KillFeedbackEffectKind.ZoomPunch, strength, duration);

            Console.WriteLine("已发出击杀事件：强度 " + strength + " 时长 " + duration +
                              "ms（发之前 DLL 累计放过 " + before.EventPlayCount + " 次）");

            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(700, duration + 400));
            var sawActive = false;
            var peakZoom = 0u;

            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(40);

                var status = control.GetStatus();
                if (status.EventActiveMs > 0)
                {
                    sawActive = true;
                }
                if (status.EventZoomMilli > peakZoom)
                {
                    peakZoom = status.EventZoomMilli;
                }
                if (sawActive && status.EventActiveMs == 0)
                {
                    break;
                }
            }

            var after = control.GetStatus();

            Console.WriteLine(string.Format(
                "DLL 累计放过 {0} 次（之前 {1} 次）；本次峰值缩放 {2:F3}x；当前剩余 {3}ms",
                after.EventPlayCount, before.EventPlayCount, peakZoom / 1000.0, after.EventActiveMs));

            if (after.EventPlayCount > before.EventPlayCount)
            {
                Console.WriteLine("结论：事件送到了，DLL 那边确实画了");
                return 0;
            }

            Console.WriteLine("结论：DLL 没接住 —— 多半是没注入，或者游戏里那份还是旧 DLL（协议对不上）");
            return 5;
        }
    }

    /// <summary>
    /// 多效果验收：一次性写入效果配置并触发，然后看 DLL 的累计次数。
    /// 旧格式：5 个百分比 + 可选时长；新格式：9 个百分比 + 可选时长。
    /// </summary>
    private static int KillAll(string[] args)
    {
        if (args.Length < 6)
        {
            Usage();
            return 1;
        }

        var kinds = new[]
        {
            KillFeedbackEffectKind.ZoomPunch,
            KillFeedbackEffectKind.EdgePulse,
            KillFeedbackEffectKind.VignettePulse,
            KillFeedbackEffectKind.Chromatic,
            KillFeedbackEffectKind.Shake,
            KillFeedbackEffectKind.Flash,
            KillFeedbackEffectKind.Shockwave,
            KillFeedbackEffectKind.Glitch,
        };

        // 旧格式：5 个效果（长度 6）或 5 个效果 + 时长（长度 7）。
        // 新格式：>=8 个参数时，前 8 个按新效果顺序解析，最后可选一个时长。
        var oldFormat = args.Length <= 7;
        var effectCount = oldFormat ? 5 : Math.Min(kinds.Length, Math.Max(0, args.Length - 1));
        var configs = new System.Collections.Generic.List<KillEffectConfig>();
        for (var i = 0; i < effectCount; i++)
        {
            var percent = float.Parse(args[i + 1]);
            if (percent <= 0.0f)
            {
                continue;
            }

            configs.Add(new KillEffectConfig
            {
                Kind = (uint)kinds[i],
                Strength = Math.Clamp(percent / 100.0f, 0.0f, 5.0f),
                Enabled = 1u,
                Reserved = kinds[i] == KillFeedbackEffectKind.EdgePulse ? 0xFFF5E0u : 0u,
            });
        }

        var durationIndex = oldFormat ? 6 : effectCount + 1;
        var duration = args.Length > durationIndex ? uint.Parse(args[durationIndex]) : 380u;

        using (var control = new MotionBlurControl())
        {
            string error;
            if (!control.VerifyLayout(out error))
            {
                Console.WriteLine("结构体自检失败：" + error);
                return 3;
            }

            var before = control.GetStatus();
            control.WriteKillEffects(configs);
            control.RequestKillFeedbackEffects(duration);

            Console.WriteLine("已发出多效果事件：启用 " + configs.Count + " 个，时长 " + duration +
                              "ms（发之前 DLL 累计放过 " + before.EventPlayCount + " 次）");

            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(700, duration + 400));
            var sawActive = false;
            var peakZoom = 0u;

            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(40);
                var status = control.GetStatus();
                if (status.EventActiveMs > 0)
                {
                    sawActive = true;
                }
                if (status.EventZoomMilli > peakZoom)
                {
                    peakZoom = status.EventZoomMilli;
                }
                if (sawActive && status.EventActiveMs == 0)
                {
                    break;
                }
            }

            var after = control.GetStatus();
            Console.WriteLine(string.Format(
                "DLL 累计放过 {0} 次（之前 {1} 次）；本次峰值缩放 {2:F3}x；当前剩余 {3}ms",
                after.EventPlayCount, before.EventPlayCount, peakZoom / 1000.0, after.EventActiveMs));

            if (after.EventPlayCount > before.EventPlayCount)
            {
                Console.WriteLine("结论：多效果事件送到了，DLL 那边确实画了");
                return 0;
            }

            Console.WriteLine("结论：DLL 没接住 —— 多半是没注入，或者游戏里那份还是旧 DLL（协议对不上）");
            return 5;
        }
    }

    private static string DefaultDllPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "GameMotionBlurHook.dll"),
            // 注意层级：这个文件在 tools\GmBlurCli\bin\Debug\net8.0-windows\ 下，
            // 要往上是 5 级才到仓库根（少一级会落到 tools\native\dist，找不到）
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "native", "dist", "GameMotionBlurHook.dll")),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private static int List()
    {
        var processes = GameProcessInjector.FindCandidateProcesses();
        Console.WriteLine("PID      进程                      窗口标题");
        foreach (var p in processes)
        {
            Console.WriteLine(p.Id.ToString().PadRight(8) + p.Name.PadRight(26) + p.Title);
        }
        return 0;
    }

    private static int Inject(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("要给个 PID");
            return 1;
        }

        var pid = int.Parse(args[1]);
        var dll = args.Length > 2 ? args[2] : DefaultDllPath();

        using (var control = new MotionBlurControl())
        {
            // 先把控制块建好，DLL 一进去就能看到参数
            control.Apply(false, 0.55f, 0, 0, "");
            Console.WriteLine("控制块已就绪：" + MotionBlurControl.MapName);
        }

        Console.WriteLine("注入 " + dll + " -> PID " + pid + " ...");
        GameProcessInjector.Inject(pid, dll);
        Console.WriteLine("注入完成");

        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(200);
            using (var control = new MotionBlurControl())
            {
                string error;
                if (!control.VerifyLayout(out error))
                {
                    Console.WriteLine("结构体自检失败：" + error);
                    return 3;
                }

                var status = control.GetStatus();
                if (status.HookInstalled || i >= 9)
                {
                    Console.WriteLine("钩子已安装=" + status.HookInstalled + " 设备=" + status.DeviceKind +
                                      " 出帧=" + status.PresentCount);
                    Console.WriteLine("状态：" + status.StatusText);
                    return status.HookInstalled ? 0 : 4;
                }
            }
        }

        return 0;
    }

    private static int SetEnabled(bool enable, float strength)
    {
        using (var control = new MotionBlurControl())
        {
            string error;
            if (!control.VerifyLayout(out error))
            {
                Console.WriteLine("结构体自检失败：" + error);
                return 3;
            }

            control.SetEnabled(enable, strength);
            Console.WriteLine(enable ? ("已开启帧混合，强度 " + strength) : "已关闭帧混合");
        }
        return 0;
    }

    private static int Dump(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("要给个目录");
            return 1;
        }

        var dir = Path.GetFullPath(args[1]);
        var frames = args.Length > 2 ? uint.Parse(args[2]) : 3u;
        Directory.CreateDirectory(dir);

        using (var control = new MotionBlurControl())
        {
            control.RequestDump(dir, frames);
            Console.WriteLine("已请求导出 " + frames + " 帧到 " + dir);

            // 等文件出现
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(150);
                if (Directory.GetFiles(dir, "*.bmp").Length >= frames)
                {
                    break;
                }
            }

            var files = Directory.GetFiles(dir, "*.bmp").OrderBy(f => f).ToArray();
            Console.WriteLine("实际导出 " + files.Length + " 个文件");
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                Console.WriteLine("  " + Path.GetFileName(file) + "  " + info.Length + " 字节");
            }
        }
        return 0;
    }

    private static int Status(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            using (var control = new MotionBlurControl())
            {
                var status = control.GetStatus();
                var device = status.DeviceKind == MotionBlurDeviceKind.OpenGL ? "OpenGL"
                    : status.DeviceKind.ToString();

                Console.WriteLine(string.Format(
                    "钩子={0} 设备={1} 出帧={2} 混合={3} 尺寸={4}x{5} fps={6:F1} | {7}",
                    status.HookInstalled, device, status.PresentCount, status.BlendCount,
                    status.Width, status.Height, status.Fps, status.StatusText));
            }

            if (i + 1 < rounds)
            {
                Thread.Sleep(500);
            }
        }
        return 0;
    }
}