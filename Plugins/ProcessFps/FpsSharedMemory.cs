using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace MinecraftChatOverlay.Plugins.ProcessFps;

/// <summary>一次采样拿到的结果。</summary>
/// <param name="State">连接状态，界面按它决定显示什么。</param>
/// <param name="Fps">实测帧率（只有 State == Ok 时才有意义）。</param>
/// <param name="PresentCount">累计出帧次数，用来判断"数字是不是在动"。</param>
/// <param name="Width">钩子看到的画面宽（0 = 还不知道）。</param>
/// <param name="Height">钩子看到的画面高。</param>
/// <param name="Detail">出问题时用来说明原因的一行字。</param>
internal readonly record struct FpsSample(
    FpsLinkState State,
    double Fps,
    uint PresentCount,
    uint Width,
    uint Height,
    string Detail);

/// <summary>帧率来源的连接状态。</summary>
internal enum FpsLinkState
{
    /// <summary>正常拿到数据。</summary>
    Ok,

    /// <summary>没找到共享内存：目标进程还没被注入过。</summary>
    NotFound,

    /// <summary>共享内存存在，但不是本软件的（或布局改了）。</summary>
    Incompatible,

    /// <summary>钩子没挂上，或者目标进程一直没出帧。</summary>
    NoFrames,
}

/// <summary>
/// 读宿主动态模糊钩子写进共享内存的实测帧率。
///
/// ⚠ 这是**契约外**的数据通道：<c>IPluginHost</c> 没有暴露帧率，这里是按宿主控制块的
///    内存布局直接读的（字段顺序对齐宿主 <c>Services\GameMotionBlur\MotionBlurControlBlock.cs</c>
///    里的 <c>MotionBlurControlHeader</c>）。布局一变就可能读到错的数字 ——
///    所以打开之后第一件事是校验，对不上就报"不兼容"，**绝不拿一个可疑的数字当好数据**。
///
/// 校验依据（原生侧 ControlWriteProbes 会把这几项写进 probe 数组）：
///   probe[3] = offsetof(lastPresentQpc) —— 它前面所有字段的布局指纹
///   probe[5] = 0xDEADBEEF              —— 哨兵，验证 probe 区本身没串位
///   StructSize                          —— 宿主那边的 sizeof(ControlHeader)，必须够长
///
/// 已知盲区：宿主若在 qpcFrequency 和 fpsMilli **之间**插字段，上面的指纹查不出来
/// （DEV-NOTES 坑 1：顺序变了但大小恰好相同的情况抓不到）。按项目规矩，
/// 改共享内存字段要两边一起改并升 kVersion，所以那种改动本来就会先报协议不兼容。
/// </summary>
internal sealed class FpsSharedMemory : IDisposable
{
    /// <summary>和宿主一致的魔数，'GMLB'。</summary>
    private const uint ExpectedMagic = 0x424C4D47u;

    /// <summary>probe[5] 的哨兵值。</summary>
    private const uint ProbeSentinel = 0xDEADBEEFu;

    /// <summary>共享内存名。宿主允许用环境变量 GMBLUR_MAP 改名做隔离测试，这里跟着走。</summary>
    private const string DefaultMapName = @"Local\GameMotionBlurCtrl_v1";

    /// <summary>
    /// 宿主控制块头部。只抄到需要用到的地方为止 —— 再往后的字符串区 / 事件通道
    /// 不影响前面这些字段的偏移。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ControlHeader
    {
        public uint Magic;
        public uint Version;
        public uint StructSize;
        public uint Seq;

        public uint Enable;
        public float Strength;
        public uint Flags;
        public uint DumpFrames;

        public uint HookInstalled;
        public uint DeviceKind;
        public uint PresentCount;
        public uint BlendCount;
        public uint Width;
        public uint Height;
        public uint Format;
        public uint LastHresult;
        public uint State;

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
        public uint BlendFrames;
    }

    /// <summary>本端算出来的 lastPresentQpc 偏移，用来和宿主的 probe[3] 对。</summary>
    private static readonly uint LocalQpcOffset =
        (uint)Marshal.OffsetOf(typeof(ControlHeader), nameof(ControlHeader.LastPresentQpc)).ToInt64();

    /// <summary>本端算出来的 presentCount 偏移，用来和宿主的 probe[7] 对。</summary>
    private static readonly uint LocalPresentOffset =
        (uint)Marshal.OffsetOf(typeof(ControlHeader), nameof(ControlHeader.PresentCount)).ToInt64();

    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;

    /// <summary>本轮读到的关键原始字段，只给「排查用」那张卡片看 —— 数字不对时靠它对账。</summary>
    public string LastRawSummary { get; private set; } = "（还没读过）";

    /// <summary>上一次打开失败的原因（给界面用）。</summary>
    public string LastOpenError { get; private set; } = "";

    /// <summary>布局校验失败时的说明。</summary>
    public string LayoutError { get; private set; } = "";

    /// <summary>共享内存名（诊断用）。</summary>
    public static string MapName
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable("GMBLUR_MAP");
            return string.IsNullOrWhiteSpace(custom) ? DefaultMapName : custom;
        }
    }

    /// <summary>按需打开共享内存。已经打开就返回 true；打不开会记下原因。</summary>
    private bool EnsureOpen()
    {
        if (_view is not null)
        {
            return true;
        }

        try
        {
            _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            LastOpenError = "";
            LayoutError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastOpenError = ex.Message;
            Close();

            // 可能是目标进程刚退出，也可能是控制块刚被建出来 —— 都退回"没连上"，
            // 下一次采样会再试一遍。
            return false;
        }
    }

    /// <summary>采一次。任何异常都转成状态，不往外抛（采样线程不该被一个读内存的问题搞崩）。</summary>
    public FpsSample Sample()
    {
        if (!EnsureOpen())
        {
            return new FpsSample(FpsLinkState.NotFound, 0, 0, 0, 0,
                "没连上动态模糊控制块（" + MapName + "）：" + (LastOpenError.Length > 0 ? LastOpenError : "共享内存不存在"));
        }

        try
        {
            _view!.Read(0, out ControlHeader header);

            LastRawSummary = string.Format(
                "magic=0x{0:X8}  version={1}  sizeof={2}  probe3={3}/本端{4}  probe7={5}/本端{6}  hookInstalled={7}  present={8}  {9}×{10}  fpsMilli={11}",
                header.Magic, header.Version, header.StructSize,
                header.Probe3, LocalQpcOffset, header.Probe7, LocalPresentOffset,
                header.HookInstalled, header.PresentCount,
                header.Width, header.Height, header.FpsMilli);

            if (header.Magic != ExpectedMagic)
            {
                return new FpsSample(FpsLinkState.Incompatible, 0, 0, 0, 0,
                    string.Format("共享内存不是本软件的动态模糊控制块（magic=0x{0:X8}）", header.Magic));
            }

            // ⚠ 顺序很重要：probe 区是**目标进程里的 DLL** 写的（原生 ControlWriteProbes），
            //   宿主进程自己这边永远是一片 0。所以"还没注入"时必须先判 hookInstalled，
            //   不能拿全 0 的 probe 去下"布局不兼容"的结论（实测踩过：没注入时一律被误判成不兼容）。
            if (header.HookInstalled == 0)
            {
                return new FpsSample(FpsLinkState.NoFrames, 0, header.PresentCount, header.Width, header.Height,
                    "钩子没挂上：目标进程还没被注入过动态模糊（去「游戏动态模糊」页点【注入并接管】）");
            }

            // 能走到这儿，说明 DLL 已经在目标进程里跑过一轮了 —— probe 才有意义。

            // probe 区位置对不对：拐个弯验证"结构体前 100 字节"的排布没变。
            if (header.Probe5 != ProbeSentinel)
            {
                return new FpsSample(FpsLinkState.Incompatible, 0, 0, 0, 0,
                    string.Format("probe 哨兵不符（0x{0:X8}，应为 0xDEADBEEF）—— 控制块布局和本插件预期的不一样",
                        header.Probe5));
            }

            if (header.Probe3 != LocalQpcOffset || header.Probe7 != LocalPresentOffset)
            {
                LayoutError = string.Format(
                    "宿主 probe[3]={0}/probe[7]={1}，本插件算的是 {2}/{3} —— 动态模糊改过共享内存布局，插件需要跟着更新",
                    header.Probe3, header.Probe7, LocalQpcOffset, LocalPresentOffset);
                return new FpsSample(FpsLinkState.Incompatible, 0, 0, 0, 0, LayoutError);
            }

            if (header.StructSize < LocalQpcOffset + 16 + 4)
            {
                LayoutError = string.Format("宿主 StructSize={0}，比 read 帧率需要的长度还短", header.StructSize);
                return new FpsSample(FpsLinkState.Incompatible, 0, 0, 0, 0, LayoutError);
            }

            if (header.PresentCount == 0)
            {
                return new FpsSample(FpsLinkState.NoFrames, 0, 0, header.Width, header.Height,
                    "钩子已挂上，但还没看到出帧");
            }

            return new FpsSample(FpsLinkState.Ok, header.FpsMilli / 1000.0,
                header.PresentCount, header.Width, header.Height, "");
        }
        catch (Exception ex)
        {
            // 目标进程没了 / 控制块被拆了，都会走到这儿 —— 关掉重来，下一轮会重新打开。
            Close();
            return new FpsSample(FpsLinkState.NotFound, 0, 0, 0, 0, "读控制块失败：" + ex.Message);
        }
    }

    private void Close()
    {
        try { _view?.Dispose(); } catch { }
        try { _map?.Dispose(); } catch { }
        _view = null;
        _map = null;
    }

    public void Dispose() => Close();
}
