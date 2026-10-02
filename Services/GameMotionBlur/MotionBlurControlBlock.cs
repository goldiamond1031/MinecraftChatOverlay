using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace MinecraftChatOverlay.Services.GameMotionBlur;

// ============================================================================
//  与 native\GameMotionBlur\common.h 里的 gmblur::ControlHeader 一一对应。
//
//  ⚠ 改字段必须两边一起改，并把 common.h 里的 kVersion 加一（这里同步改 Version）。
//    C# 端启动时用 VerifyLayout() 比对大小/偏移，不一致会直接报错，
//    所以不用担心"悄悄错位"。
//
//  为什么要拆成"纯数值头部 + 另外两段字符串区"：
//  C# 结构体里只要出现 string（哪怕标了 ByValTStr），整个结构体就不再是
//  blittable，Marshal.SizeOf / MemoryMappedViewAccessor 会直接拒绝。
//  所以字符串区按固定偏移单独读写，头部保持纯数值。
//
//  这个文件刻意只用很老的 C# 语法（不用字符串插值 / 表达式体成员 / nameof），
//  这样 PowerShell 的 Add-Type 也能直接编译它，方便脱离界面做联调。
// ============================================================================
/// <summary>
/// 一个击杀反馈效果配置。对应 native\GameMotionBlur\common.h 里的
/// <c>gmblur::KillEffectConfig</c>。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct KillEffectConfig
{
    public uint Kind;
    public float Strength;
    public uint Enabled;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct MotionBlurControlHeader
{
    // ---------- 头部 ----------
    public uint Magic;
    public uint Version;
    public uint StructSize;
    public uint Seq;

    // ---------- 控制端（C#）写入 ----------
    public uint Enable;
    public float Strength;
    public uint Flags;
    public uint DumpFrames;

    // ---------- 钩子端（DLL）写入 ----------
    public uint HookInstalled;
    public uint DeviceKind;
    public uint PresentCount;
    public uint BlendCount;
    public uint Width;
    public uint Height;
    public uint Format;
    public uint LastHresult;
    public uint State;

    // probe 故意拆成 8 个字段而不是 uint[]：数组是引用类型，会破坏 blittable。
    public uint Probe0;
    public uint Probe1;
    public uint Probe2;
    public uint Probe3;
    public uint Probe4;
    public uint Probe5;
    public uint Probe6;
    public uint Probe7;

    public ulong LastPresentQpc;
    public ulong QpcFrequency;
    public uint FpsMilli;

    /// <summary>当前实际平均了几帧（2~8）；0 = 没在混合。</summary>
    public uint BlendFrames;

    // ---------- 字符串区的位置 ----------
    public uint StatusChars;
    public uint DumpDirChars;
    public uint StatusOffset;
    public uint DumpDirOffset;

    // ==========================================================================
    //  击杀反馈事件通道（v3 新增）
    //
    //  ⚠ 字段顺序必须和 common.h 里的 ControlHeader 完全一致。
    //    两边都加了同样的字段、宽度也对得上时 sizeof 才相等，probe 自检会验证这一点。
    //
    //  用"序号"而不是"标志位"：标志位只能表达"有一个待处理事件"，
    //  连杀时会被合并掉；序号天然能表达"又来了一个"。
    // ==========================================================================

    // ---------- 控制端（C#）写入 ----------
    public uint EventSeq;
    public uint EventKind;
    public uint EventDurationMs;
    public float EventStrength;

    // ---------- 钩子端（DLL）写入 ----------
    public uint EventHandledSeq;
    public uint EventActiveMs;
    public uint EventPlayCount;
    public uint EventZoomMilli;

    // ---------- 多效果配置（v4 新增） ----------
    /// <summary>有效配置条数（0 ~ MotionBlurControl.MaxKillEffects）。</summary>
    public uint EffectConfigCount;

    /// <summary>写入配置时的协议版本，诊断用。</summary>
    public uint EffectConfigVersion;

    public static MotionBlurControlHeader CreateDefault()
    {
        MotionBlurControlHeader header = new MotionBlurControlHeader();
        header.Magic = MotionBlurControl.Magic;
        header.Version = MotionBlurControl.Version;
        header.StructSize = (uint)Marshal.SizeOf(typeof(MotionBlurControlHeader));
        header.Strength = 0.55f;
        header.StatusChars = MotionBlurControl.StatusChars;
        header.DumpDirChars = MotionBlurControl.DumpDirChars;
        header.EffectConfigCount = 0;
        header.EffectConfigVersion = MotionBlurControl.Version;
        header.StatusOffset = header.StructSize + MotionBlurControl.EffectConfigBytes;
        header.DumpDirOffset = header.StatusOffset + header.StatusChars * 2;
        return header;
    }
}

/// <summary>控制块里的 flags 位。</summary>
[Flags]
public enum MotionBlurFlags : uint
{
    /// <summary>导出若干帧到 DumpDir（调试 / 验证用）。</summary>
    DumpFrames = 1u << 0,

    /// <summary>把 IAT 槽位还原（不重启游戏就能退出本功能）。</summary>
    Unhook = 1u << 1,

    /// <summary>丢掉历史帧，下一帧重新以当前帧为起点。</summary>
    ResetHistory = 1u << 2,

    /// <summary>要求重新装钩子（DLL 已在本进程里、但钩子被卸载过时用）。</summary>
    Reinstall = 1u << 3
}

/// <summary>出帧背后的图形 API。</summary>
public enum MotionBlurDeviceKind : uint
{
    Unknown = 0,

    /// <summary>OpenGL（Minecraft Java 版 / LWJGL / GLFW）。</summary>
    OpenGL = 3
}

/// <summary>
/// 击杀反馈的效果种类。对应 native 侧 <c>gmblur::KillEffectKind</c>。
/// 留成"种类"而不是写死一个效果，是为了以后加效果不用再改共享内存协议。
/// </summary>
public enum KillFeedbackEffectKind : uint
{
    /// <summary>取消当前效果。</summary>
    None = 0,

    /// <summary>缩放脉冲：整幅画面放大一点点再弹回。</summary>
    ZoomPunch = 1,

    /// <summary>边缘脉冲：四周亮起一圈光带。</summary>
    EdgePulse = 2,

    /// <summary>暗角脉冲：四周短暂压暗。</summary>
    VignettePulse = 3,

    /// <summary>色差分离：红蓝沿径向错开。</summary>
    Chromatic = 4,

    /// <summary>抖动：整幅画面小幅高频错位。</summary>
    Shake = 5,

    /// <summary>按配置数组播放所有已启用效果（多效果共存）。</summary>
    Configured = 6,

    /// <summary>全屏闪光：整个画面短暂变亮。</summary>
    Flash = 7,

    /// <summary>中心冲击环：一圈光从画面中心向外扩散。</summary>
    Shockwave = 8,

    /// <summary>故障撕裂：横向块位移 + 红蓝分离。
    /// （原 9 号「径向模糊」已移除，10 号前移补位 —— 协议语义变了，Version 已升到 6。）</summary>
    Glitch = 9
}

/// <summary>给界面/命令行看的一份只读状态。</summary>
public sealed class MotionBlurStatus
{
    public bool HookInstalled { get; set; }

    /// <summary>控制块里"帧混合开关"的当前值（0=关，钩子照样在，但不会混合）。</summary>
    public bool Enabled { get; set; }
    public MotionBlurDeviceKind DeviceKind { get; set; }
    public uint PresentCount { get; set; }
    public uint BlendCount { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint Format { get; set; }
    public uint State { get; set; }
    public uint LastHresult { get; set; }
    public string StatusText { get; set; }
    public uint StrengthMilli { get; set; }
    public uint BlendFrames { get; set; }
    public double Fps { get; set; }

    /// <summary>DLL 已经处理到哪个事件序号（用来确认事件真的送到了）。</summary>
    public uint EventHandledSeq { get; set; }

    /// <summary>DLL 那边当前效果还剩多少毫秒（0 = 没在放）。</summary>
    public uint EventActiveMs { get; set; }

    /// <summary>DLL 累计放过几次击杀效果。</summary>
    public uint EventPlayCount { get; set; }

    /// <summary>DLL 当前实际用的缩放（x1000，1000 = 原样）。诊断用。</summary>
    public uint EventZoomMilli { get; set; }

    public MotionBlurStatus()
    {
        StatusText = string.Empty;
    }
}

/// <summary>
/// 跨进程共享内存控制块的 C# 端：
///   * 界面进程创建它；
///   * 游戏进程里被注入的钩子 DLL 打开同一个名字，读参数、写状态。
/// </summary>
public sealed class MotionBlurControl : IDisposable
{
    public const uint Magic = 0x424C4D47u;   // 'GMLB'
    public const uint Version = 6;           // v6：移除径向模糊（故障撕裂 10→9）
    public const uint MappingSize = 4096;
    public const uint StatusChars = 160;
    public const uint DumpDirChars = 260;
    public const int MaxKillEffects = 8;

    /// <summary>配置区紧跟在头部之后，C++ / C# 必须用同一个偏移。</summary>
    public static uint EffectConfigOffset
    {
        get { return (uint)Marshal.SizeOf(typeof(MotionBlurControlHeader)); }
    }

    public static uint EffectConfigSize
    {
        get { return (uint)Marshal.SizeOf(typeof(KillEffectConfig)); }
    }

    public static uint EffectConfigBytes
    {
        get { return (uint)(MaxKillEffects * EffectConfigSize); }
    }
    /// <summary>
    /// 共享内存名。想同时跑两份（或做隔离测试）时设环境变量 GMBLUR_MAP
    /// 成别的名字即可；游戏进程和界面进程要用同一个名字。
    /// </summary>
    public static string MapName
    {
        get
        {
            // GetEnvironmentVariable 的返回是 string? —— 显式标成可空，
            // 免得编译器把 null 塞进 string 局部变量（CS8600）。
            string? custom = Environment.GetEnvironmentVariable("GMBLUR_MAP");
            return string.IsNullOrWhiteSpace(custom) ? @"Local\GameMotionBlurCtrl_v1" : custom;
        }
    }

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _headerSize;

    public MotionBlurControl()
    {
        _headerSize = Marshal.SizeOf(typeof(MotionBlurControlHeader));
        _map = MemoryMappedFile.CreateOrOpen(MapName, MappingSize);
        _view = _map.CreateViewAccessor();

        // 只有头部不对时才初始化，避免把钩子已经写好的状态抹掉。
        MotionBlurControlHeader current = ReadHeader();
        if (current.Magic != Magic ||
            current.Version != Version ||
            current.StructSize != (uint)_headerSize ||
            current.StatusOffset == 0)
        {
            WriteHeader(MotionBlurControlHeader.CreateDefault());
        }
    }

    public int HeaderSize
    {
        get { return _headerSize; }
    }

    // ---------- 头部读写 ----------

    public MotionBlurControlHeader ReadHeader()
    {
        MotionBlurControlHeader header;
        _view.Read(0, out header);
        return header;
    }

    public void WriteHeader(MotionBlurControlHeader header)
    {
        header.Magic = Magic;
        header.Version = Version;
        header.StructSize = (uint)_headerSize;
        if (header.StatusOffset == 0)
        {
            header.StatusCharSetDefault();
        }
        _view.Write(0, ref header);
    }

    // ---------- 字符串区读写 ----------

    private string ReadString(uint offset, uint chars)
    {
        if (offset == 0 || chars == 0)
        {
            return string.Empty;
        }

        byte[] bytes = new byte[chars * 2];
        _view.ReadArray(offset, bytes, 0, bytes.Length);

        int length = 0;
        while (length < chars && !(bytes[length * 2] == 0 && bytes[length * 2 + 1] == 0))
        {
            length++;
        }

        return Encoding.Unicode.GetString(bytes, 0, length * 2);
    }

    private void WriteString(uint offset, uint chars, string? value)
    {
        if (offset == 0 || chars == 0)
        {
            return;
        }

        byte[] bytes = new byte[chars * 2];
        if (!string.IsNullOrEmpty(value))
        {
            byte[] raw = Encoding.Unicode.GetBytes(value);
            int copy = Math.Min(raw.Length, bytes.Length - 2);
            Buffer.BlockCopy(raw, 0, bytes, 0, copy);
        }

        _view.WriteArray(offset, bytes, 0, bytes.Length);
    }

    // ---------- 控制端 API ----------

    /// <summary>
    /// 告诉 DLL"输出目录"在哪儿（钩子日志就写这里；导出帧共用这一段字符串）。
    /// 必须在注入之前调用 —— DLL 一起来就按这个目录建日志文件。
    /// 目录不存在时由 DLL 那边建（它建不出来会退回游戏 exe 旁边）。
    /// </summary>
    public void SetOutputDirectory(string? directory)
    {
        MotionBlurControlHeader header = ReadHeader();
        WriteString(header.DumpDirOffset, header.DumpDirChars, directory);
    }

    /// <summary>改参数（保留钩子写回来的状态字段）。</summary>
    public void Apply(bool enable, float strength, uint flags, uint dumpFrames, string? dumpDir)
    {
        MotionBlurControlHeader header = ReadHeader();

        // 先写字符串区，再写头部：这样钩子看到 dump 标志时，目录一定已经就位。
        if (!string.IsNullOrEmpty(dumpDir))
        {
            WriteString(header.DumpDirOffset, header.DumpDirChars, dumpDir);
        }

        header.Enable = enable ? 1u : 0u;
        header.Strength = strength;
        header.Flags = flags;
        header.DumpFrames = dumpFrames;
        header.Seq = header.Seq + 1;
        WriteHeader(header);
    }

    public void SetEnabled(bool enable, float strength)
    {
        MotionBlurControlHeader header = ReadHeader();
        Apply(enable, strength, header.Flags, header.DumpFrames, null);
    }

    public void SetStrength(float strength)
    {
        MotionBlurControlHeader header = ReadHeader();
        Apply(header.Enable != 0, strength, header.Flags, header.DumpFrames, null);
    }

    public void RequestDump(string directory, uint frames)
    {
        MotionBlurControlHeader header = ReadHeader();
        Apply(header.Enable != 0, header.Strength, header.Flags | (uint)MotionBlurFlags.DumpFrames, frames, directory);
    }

    public void RequestUnhook()
    {
        MotionBlurControlHeader header = ReadHeader();
        Apply(false, header.Strength, header.Flags | (uint)MotionBlurFlags.Unhook, header.DumpFrames, null);
    }

    /// <summary>请 DLL 把钩子重新装上（用于"卸载过又不想重启游戏"）。</summary>
    public void RequestReinstall()
    {
        MotionBlurControlHeader header = ReadHeader();
        Apply(header.Enable != 0, header.Strength, header.Flags | (uint)MotionBlurFlags.Reinstall, header.DumpFrames, null);
    }

    public void RequestResetHistory()
    {
        MotionBlurControlHeader header = ReadHeader();
        Apply(header.Enable != 0, header.Strength, header.Flags | (uint)MotionBlurFlags.ResetHistory, header.DumpFrames, null);
    }

    /// <summary>
    /// 通知游戏进程里的 DLL「刚发生了一次击杀」。
    ///
    /// 靠"序号加一"传递，不靠标志位 —— 连杀时标志位会被合并掉，序号不会。
    /// 这个调用不会动帧混合的任何参数（序号之外的字段原样保留）。
    /// </summary>
    public void RequestKillFeedback(KillFeedbackEffectKind kind, float strength, uint durationMs)
    {
        MotionBlurControlHeader header = ReadHeader();

        header.EventSeq = header.EventSeq + 1;
        header.EventKind = (uint)kind;
        header.EventStrength = strength;
        header.EventDurationMs = durationMs;
        header.Seq = header.Seq + 1;

        WriteHeader(header);
    }

    /// <summary>
    /// 写入多效果配置。每个元素包含效果种类、强度（0 ~ 5.0，界面显示 0 ~ 500%）
    /// 和启用位；随后调用 <see cref="RequestKillFeedbackEffects"/> 触发一次。
    /// </summary>
    public void WriteKillEffects(System.Collections.Generic.IReadOnlyList<KillEffectConfig> effects)
    {
        MotionBlurControlHeader header = ReadHeader();
        uint offset = EffectConfigOffset;
        int count = effects == null ? 0 : effects.Count;
        if (count > MaxKillEffects)
        {
            count = MaxKillEffects;
        }

        KillEffectConfig empty = new KillEffectConfig();
        for (int i = 0; i < MaxKillEffects; i++)
        {
            KillEffectConfig cfg = i < count ? effects![i] : empty;
            _view.Write(offset + (uint)(i * EffectConfigSize), ref cfg);
        }

        header.EffectConfigCount = (uint)count;
        header.EffectConfigVersion = Version;
        header.Seq = header.Seq + 1;
        WriteHeader(header);
    }

    /// <summary>
    /// 触发一次"按当前多效果配置播放"的事件。持续时间对所有效果共用。
    /// </summary>
    public void RequestKillFeedbackEffects(uint durationMs)
    {
        MotionBlurControlHeader header = ReadHeader();
        header.EventSeq = header.EventSeq + 1;
        header.EventKind = (uint)KillFeedbackEffectKind.Configured;
        header.EventDurationMs = durationMs;
        header.EventStrength = 1.0f;
        header.Seq = header.Seq + 1;
        WriteHeader(header);
    }

    public MotionBlurStatus GetStatus()
    {
        MotionBlurControlHeader header = ReadHeader();

        MotionBlurStatus status = new MotionBlurStatus();
        status.HookInstalled = header.HookInstalled != 0;
        status.Enabled = header.Enable != 0;
        status.DeviceKind = (MotionBlurDeviceKind)header.DeviceKind;
        status.PresentCount = header.PresentCount;
        status.BlendCount = header.BlendCount;
        status.Width = header.Width;
        status.Height = header.Height;
        status.Format = header.Format;
        status.State = header.State;
        status.LastHresult = header.LastHresult;
        status.StrengthMilli = (uint)(header.Strength * 1000.0f + 0.5f);
        status.BlendFrames = header.BlendFrames;
        status.Fps = header.FpsMilli / 1000.0;
        status.StatusText = ReadString(header.StatusOffset, header.StatusChars);

        status.EventHandledSeq = header.EventHandledSeq;
        status.EventActiveMs = header.EventActiveMs;
        status.EventPlayCount = header.EventPlayCount;
        status.EventZoomMilli = header.EventZoomMilli;

        return status;
    }

    /// <summary>
    /// 比对 C++ / C# 两边的结构体布局。不一致说明有一边改了字段却没同步，
    /// 这种情况下的参数读写会错位，必须报出来而不是硬跑。
    /// </summary>
    public bool VerifyLayout(out string error)
    {
        error = string.Empty;
        MotionBlurControlHeader header = ReadHeader();

        if (header.Magic != Magic)
        {
            error = "控制块 magic 不对（0x" + header.Magic.ToString("X8") + "），钩子可能还没起来";
            return false;
        }

        // 钩子还没写入 probe 时全是 0，此时不算错。
        if (header.Probe5 != 0xDEADBEEFu)
        {
            return true;
        }

        uint size = (uint)Marshal.SizeOf(typeof(MotionBlurControlHeader));
        if (header.Probe0 != size)
        {
            error = "结构体大小不一致：C++=" + header.Probe0 + " C#=" + size;
            return false;
        }

#pragma warning disable SYSLIB0050
        uint probeOffset = (uint)Marshal.OffsetOf(typeof(MotionBlurControlHeader), "Probe0");
        uint qpcOffset = (uint)Marshal.OffsetOf(typeof(MotionBlurControlHeader), "LastPresentQpc");
        uint enableOffset = (uint)Marshal.OffsetOf(typeof(MotionBlurControlHeader), "Enable");
        uint presentOffset = (uint)Marshal.OffsetOf(typeof(MotionBlurControlHeader), "PresentCount");
#pragma warning restore SYSLIB0050

        if (header.Probe1 != probeOffset ||
            header.Probe3 != qpcOffset ||
            header.Probe6 != enableOffset ||
            header.Probe7 != presentOffset)
        {
            error = "字段偏移不一致：C++ probe=[" + header.Probe1 + "," + header.Probe3 + "," +
                    header.Probe6 + "," + header.Probe7 + "] C#=[" + probeOffset + "," + qpcOffset + "," +
                    enableOffset + "," + presentOffset + "]";
            return false;
        }

        uint expectedStatusOffset = header.StructSize + EffectConfigBytes;
        if (header.Probe2 != expectedStatusOffset || header.Probe4 != header.Probe2 + header.StatusChars * 2)
        {
            error = "字符串区偏移不一致：statusOffset=" + header.Probe2 + " 期望=" + expectedStatusOffset +
                    " dumpDirOffset=" + header.Probe4;
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_view != null)
        {
            _view.Dispose();
        }
        if (_map != null)
        {
            _map.Dispose();
        }
    }
}

internal static class MotionBlurHeaderExtensions
{
    public static void StatusCharSetDefault(this ref MotionBlurControlHeader header)
    {
        header.StatusChars = MotionBlurControl.StatusChars;
        header.DumpDirChars = MotionBlurControl.DumpDirChars;
        header.StatusOffset = (uint)Marshal.SizeOf(typeof(MotionBlurControlHeader)) + MotionBlurControl.EffectConfigBytes;
        header.DumpDirOffset = header.StatusOffset + header.StatusChars * 2;
    }
}