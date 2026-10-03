using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace MinecraftChatOverlay.Plugins.FpsOverlay;

/// <summary>
/// 读「只数帧」探针写出来的共享内存，并算出 FPS。
///
/// 字段偏移是写死的常量，打开时**必须**拿探针自己报出来的 probe[] 对照一遍 ——
/// 两边字段顺序不一致、而总大小恰好相同时，只比 structSize 是抓不出来的（DEV-NOTES 坑 1）。
/// </summary>
internal sealed class FpsProbeReader : IDisposable
{
    /// <summary>共享内存名前缀，后面拼 pid。要和 native/FpsProbe/fpsprobe.cpp 里一致。</summary>
    public const string MapNamePrefix = "Local\\McoFpsProbe_";

    private const uint ExpectedMagic   = 0x5350464Du;   // 'MFPS'
    private const uint ExpectedVersion = 1;
    private const uint ExpectedSize    = 96;

    // 下面这些偏移要和 native/FpsProbe/fpsprobe.h 的 Header 逐字段对上
    private const int OffMagic         = 0;
    private const int OffVersion       = 4;
    private const int OffStructSize    = 8;
    private const int OffTargetPid     = 12;
    private const int OffHookInstalled = 16;
    private const int OffSwapCount     = 20;
    private const int OffGdiHooked     = 24;
    private const int OffWglHooked     = 28;
    private const int OffQpcFrequency  = 32;
    private const int OffPatchedSlots  = 56;
    private const int OffPumpRuns      = 60;
    private const int OffProbe         = 72;

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;

    private readonly Queue<(uint Count, double Time)> _samples = new();

    private FpsProbeReader(MemoryMappedFile map, MemoryMappedViewAccessor view)
    {
        _map = map;
        _view = view;
    }

    /// <summary>探针报的帧计数（单调递增，uint32 回绕）。</summary>
    public uint SwapCount => _view.ReadUInt32(OffSwapCount);

    public uint HookInstalled => _view.ReadUInt32(OffHookInstalled);

    public uint GdiHooked => _view.ReadUInt32(OffGdiHooked);

    public uint WglHooked => _view.ReadUInt32(OffWglHooked);

    public uint PatchedSlots => _view.ReadUInt32(OffPatchedSlots);

    public uint PumpRuns => _view.ReadUInt32(OffPumpRuns);

    public uint TargetPid => _view.ReadUInt32(OffTargetPid);

    /// <summary>最近算出来的 FPS。</summary>
    public double CurrentFps { get; private set; }

    /// <summary>打开某个 pid 的探针共享内存。没注入过 / 探针还没起来都会返回 false。</summary>
    public static bool TryOpen(int pid, out FpsProbeReader? reader)
    {
        reader = null;
        if (pid <= 0)
        {
            return false;
        }

        MemoryMappedFile? map = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            map = MemoryMappedFile.OpenExisting(MapNamePrefix + pid, MemoryMappedFileRights.Read);
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            if (view.ReadUInt32(OffMagic) != ExpectedMagic ||
                view.ReadUInt32(OffVersion) != ExpectedVersion ||
                view.ReadUInt32(OffStructSize) != ExpectedSize)
            {
                view.Dispose();
                map.Dispose();
                return false;
            }

            // 偏移自检：探针报出来的 sizeof 和 swapCount 偏移都得跟我们这边一致
            if (view.ReadUInt32(OffProbe + 0) != ExpectedSize ||
                view.ReadUInt32(OffProbe + 4) != (uint)OffSwapCount)
            {
                view.Dispose();
                map.Dispose();
                return false;
            }

            reader = new FpsProbeReader(map, view);
            return true;
        }
        catch
        {
            view?.Dispose();
            map?.Dispose();
            return false;
        }
    }

    /// <summary>
    /// 采一次样。用「最近 windowMs 内的帧数 / 实际经过的时间」算 FPS ——
    /// 比逐次差分稳，又比长期平均跟手。
    /// </summary>
    public void Tick(int windowMs)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var count = SwapCount;

        _samples.Enqueue((count, now));

        // 至少留两个点；顺手把窗口外的旧点丢掉
        while (_samples.Count > 2 && (now - _samples.Peek().Time) * 1000.0 > windowMs)
        {
            _samples.Dequeue();
        }

        if (_samples.Count < 2)
        {
            return;
        }

        var first = _samples.Peek();
        var last = count;
        var total = now - first.Time;

        if (total <= 0.05)
        {
            return;
        }

        // 无符号差值：uint32 回绕时结果依然正确
        var delta = (uint)(last - first.Count);
        CurrentFps = delta / total;
    }

    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
    }
}
