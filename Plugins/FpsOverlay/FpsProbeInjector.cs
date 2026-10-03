using System.IO;
using MinecraftChatOverlay.Services.GameMotionBlur;

namespace MinecraftChatOverlay.Plugins.FpsOverlay;

/// <summary>
/// 把「只数帧」的探针注入到目标进程里。
///
/// 为什么不用 <see cref="GameProcessInjector.Inject"/>：它内部有一本"这个 pid 我注入过"的账，
/// 而那本账是**主程序**的 —— 如果用户已经用「游戏动态模糊」注入了同一个游戏，
/// 插件这里的注入会被直接跳过，探针根本进不去。
/// 所以这里直接调 <see cref="ManualMapper.MapRemote"/>，并且用"共享内存能不能打开"自己查重。
/// </summary>
internal static class FpsProbeInjector
{
    public const string DllName = "FpsProbeHook.dll";

    /// <summary>探针 DLL 就在插件自己的安装目录里。</summary>
    public static string ResolveDllPath()
    {
        var dir = Path.GetDirectoryName(typeof(FpsProbeInjector).Assembly.Location) ?? "";
        return Path.Combine(dir, DllName);
    }

    public static bool IsProbeAlive(int pid) => FpsProbeReader.TryOpen(pid, out _);

    /// <summary>
    /// 确保目标进程里有探针在跑。返回一句给人看的说明。
    /// 已经跑着就直接返回，不会重复注入（重复注入会有两个钩子抢同一个 IAT 槽位）。
    /// </summary>
    public static string EnsureInjected(int pid)
    {
        if (pid <= 0)
        {
            return "还没选目标进程。";
        }

        if (IsProbeAlive(pid))
        {
            return "探针已经在跑了。";
        }

        var dll = ResolveDllPath();
        if (!File.Exists(dll))
        {
            return "找不到 " + DllName + "。它应该跟插件放在一起：" + dll;
        }

        try
        {
            var mapped = ManualMapper.MapRemote(pid, dll, invokeEntry: true, out _);
            return "注入完成：镜像基址 " + mapped.Base.ToString("X")
                 + "，重定位 " + mapped.RelocCount + " 处"
                 + "，导入 " + mapped.ImportDllCount + " 个 DLL / " + mapped.ImportFuncCount + " 个函数。";
        }
        catch (Exception ex)
        {
            return "注入失败：" + ex.Message;
        }
    }
}
