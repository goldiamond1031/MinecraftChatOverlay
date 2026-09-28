namespace MinecraftChatOverlay.Models;

/// <summary>
/// 游戏帧混合动态模糊的配置。
///
/// 说明：这部分只保存"用户的选择"，真正的参数是通过共享内存实时推给
/// 游戏进程里的钩子 DLL 的（改一次发一次，不用重启游戏）。
/// </summary>
public sealed class MotionBlurSettings
{
    /// <summary>程序启动后是否自动尝试注入上一次选的目标进程（默认关，避免误注入）。</summary>
    public bool AutoInject { get; set; }

    /// <summary>上一次选中的目标进程名，例如 javaw.exe / Minecraft.Windows.exe。</summary>
    public string TargetProcessName { get; set; } = "";

    /// <summary>上一次选中的 PID（只用于界面上找回上一次的选择）。</summary>
    public int TargetProcessId { get; set; }

    /// <summary>
    /// 钩子 DLL 路径。留空 = 用程序目录下的 GameMotionBlurHook.dll，
    /// 找不到就往上找 native\dist\GameMotionBlurHook.dll（开发时方便）。
    /// </summary>
    public string HookDllPath { get; set; } = "";

    /// <summary>是否开启帧混合。</summary>
    public bool Enabled { get; set; }

    /// <summary>强度：历史帧权重，0.05~0.95。越大拖影越长。</summary>
    public double Strength { get; set; } = 0.55;

    /// <summary>导出帧的目录（调试用；留空则用程序的 dumps 子目录）。</summary>
    public string DumpDirectory { get; set; } = "";

    /// <summary>导出多少帧。</summary>
    public int DumpFrames { get; set; } = 5;
}