using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace MinecraftChatOverlay.Services.GameMotionBlur;

/// <summary>
/// 手动映射：不走 LoadLibrary，自己当一次 Windows 加载器。
///
/// 干的事（按顺序）：
///   1. 把 PE 文件读进来，解析 DOS / NT / 节表
///   2. VirtualAlloc 一块 SizeOfImage 的私有内存
///   3. 复制头部 + 按节表展开各个节（min(VirtualSize, SizeOfRawData)）
///   4. 修基址重定位（新地址 = 旧地址 + delta）
///   5. 填导入表（自己 LoadLibrary + GetProcAddress 查地址，写进 IAT）
///   6. 处理 TLS（TlsAlloc + 写回 AddressOfIndex + 给当前线程铺一份 TLS 数据）
///   7. 调 TLS 回调，再调入口点（DllMain / DLL_PROCESS_ATTACH）
///
/// 好处：系统里没有这个模块的任何记录（没有文件名、不在 PEB 模块链表里）。
/// 代价：GetModuleFileNameW 这类"问加载器"的 API 对它无效；不能 FreeLibrary；
///       异常目录（.pdata）不会被注册，模块内抛异常没有展开信息。
/// </summary>
public static partial class ManualMapper
{
    public const uint DllProcessAttach = 1;
    public const uint DllProcessDetach = 0;

    public sealed class MappedModule
    {
        public IntPtr Base;
        public int SizeOfImage;
        public IntPtr EntryPoint;
        public int TlsIndex = -1;
        public int RelocCount;
        public int ImportDllCount;
        public int ImportFuncCount;
        public List<string> ImportDlls = new();
        public List<string> Notes = new();
        public override string ToString() =>
            $"base=0x{Base.ToInt64():X} size=0x{SizeOfImage:X} entry=0x{EntryPoint.ToInt64():X} " +
            $"重定位={RelocCount} 条 导入={ImportDllCount} 个 DLL/{ImportFuncCount} 个函数 TLS索引={TlsIndex}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr addr, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int TlsAlloc();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TlsSetValue(int index, IntPtr value);

    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint PageExecuteReadWrite = 0x40;

    private const int DirImport = 1;
    private const int DirReloc = 5;
    private const int DirTls = 9;

    /// <summary>把 DLL 手动映射到**当前进程**（第 0 步离线验证用；远程版复用同一套解析逻辑）。</summary>
    public static MappedModule MapLocal(string filePath)
    {
        var file = File.ReadAllBytes(filePath);
        return MapLocal(file, Path.GetFileName(filePath));
    }

    public static MappedModule MapLocal(byte[] file, string name)
    {
        var mod = new MappedModule();

        // ---- 1. PE 头 ----
        ulong imageBase;
        int sizeOfImage, sizeOfHeaders, entryRva, numSections, optSize;
        int[] dirs;
        var sectionVa = new int[0];
        ParseHeaders(file, out imageBase, out sizeOfImage, out sizeOfHeaders, out entryRva,
                     out numSections, out optSize, out dirs, out sectionVa, mod);

        // ---- 2. 分配 ----
        var baseAddr = VirtualAlloc(IntPtr.Zero, (UIntPtr)(uint)sizeOfImage, MemCommit | MemReserve, PageExecuteReadWrite);
        if (baseAddr == IntPtr.Zero)
        {
            throw new InvalidOperationException("VirtualAlloc 失败，错误码 " + Marshal.GetLastWin32Error());
        }

        mod.Base = baseAddr;
        mod.SizeOfImage = sizeOfImage;
        var delta = (long)baseAddr.ToInt64() - (long)imageBase;
        mod.Notes.Add($"编译时 ImageBase=0x{imageBase:X}，实际=0x{baseAddr.ToInt64():X}，delta=0x{delta:X}");

        // ---- 3. 铺字节：头部 + 各节 ----
        Marshal.Copy(file, 0, baseAddr, sizeOfHeaders);       // 头部（DOS+NT+节表）原样过去
        var sectionHeaders = new List<(int vSize, int vAddr, int rawSize, int rawPtr, string name)>();
        var secTable = 0x3C;                                  // 复用：下面重新算
        var nt = BitConverter.ToInt32(file, 0x3C);
        secTable = nt + 4 + 20 + optSize;
        for (var i = 0; i < numSections; i++)
        {
            var sh = secTable + i * 40;
            var secName = System.Text.Encoding.ASCII.GetString(file, sh, 8).TrimEnd('\0');
            var vSize = BitConverter.ToInt32(file, sh + 8);
            var vAddr = BitConverter.ToInt32(file, sh + 12);
            var rawSize = BitConverter.ToInt32(file, sh + 16);
            var rawPtr = BitConverter.ToInt32(file, sh + 20);
            sectionHeaders.Add((vSize, vAddr, rawSize, rawPtr, secName));

            var copy = Math.Min(vSize == 0 ? rawSize : vSize, rawSize);
            if (copy > 0 && rawPtr > 0)
            {
                Marshal.Copy(file, rawPtr, baseAddr + vAddr, copy);
            }
        }

        // ---- 4. 重定位 ----
        ApplyRelocations(baseAddr, delta, dirs[DirReloc], mod);

        // ---- 5. 导入表 ----
        ResolveImportsLocal(baseAddr, dirs[DirImport], mod);

        // ---- 6. TLS ----
        SetupTls(baseAddr, dirs[DirTls], mod);

        // ---- 7. TLS 回调 + 入口点 ----
        CallTlsCallbacks(baseAddr, dirs[DirTls], mod);

        mod.EntryPoint = baseAddr + entryRva;
        var entry = Marshal.GetDelegateForFunctionPointer<DllEntry>(mod.EntryPoint);
        var ok = entry(baseAddr, DllProcessAttach, IntPtr.Zero);
        mod.Notes.Add($"入口点 0x{mod.EntryPoint.ToInt64():X} 返回 " + ok);
        return mod;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint DllEntry(IntPtr hinst, uint reason, IntPtr reserved);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint TlsCallback(IntPtr hinst, uint reason, IntPtr reserved);

    private static void ParseHeaders(byte[] f, out ulong imageBase, out int sizeOfImage, out int sizeOfHeaders,
        out int entryRva, out int numSections, out int optSize, out int[] dirs, out int[] sectionVa, MappedModule mod)
    {
        if (f.Length < 0x40 || BitConverter.ToUInt16(f, 0) != 0x5A4D)
        {
            throw new BadImageFormatException("不是 PE 文件（缺 MZ）");
        }

        var nt = BitConverter.ToInt32(f, 0x3C);
        if (BitConverter.ToUInt32(f, nt) != 0x00004550)
        {
            throw new BadImageFormatException("不是 PE 文件（缺 PE00）");
        }

        var machine = BitConverter.ToUInt16(f, nt + 4);
        if (machine != 0x8664)
        {
            throw new BadImageFormatException($"只支持 x64（Machine=0x{machine:X}）");
        }

        numSections = BitConverter.ToUInt16(f, nt + 6);
        optSize = BitConverter.ToUInt16(f, nt + 20);
        var opt = nt + 24;
        var magic = BitConverter.ToUInt16(f, opt);
        if (magic != 0x20B)
        {
            throw new BadImageFormatException($"只支持 PE32+（Magic=0x{magic:X}）");
        }

        imageBase = BitConverter.ToUInt64(f, opt + 24);
        sizeOfImage = BitConverter.ToInt32(f, opt + 56);
        sizeOfHeaders = BitConverter.ToInt32(f, opt + 60);
        entryRva = BitConverter.ToInt32(f, opt + 16);

        var numDirs = BitConverter.ToInt32(f, opt + 108);
        dirs = new int[16];
        for (var i = 0; i < Math.Min(numDirs, 16); i++)
        {
            dirs[i] = BitConverter.ToInt32(f, opt + 112 + i * 8);
        }

        sectionVa = new int[numSections];
        mod.Notes.Add($"x64 / 节数={numSections} / SizeOfImage=0x{sizeOfImage:X} / 入口RVA=0x{entryRva:X}");
    }

    private static void ApplyRelocations(IntPtr baseAddr, long delta, int relocRva, MappedModule mod)
    {
        if (delta == 0 || relocRva == 0)
        {
            mod.Notes.Add(delta == 0 ? "delta=0，不用重定位" : "没有重定位表");
            return;
        }

        var size = Marshal.ReadInt32(baseAddr + relocRva + 4);
        var cur = baseAddr + relocRva;
        var count = 0;
        while (true)
        {
            var pageRva = Marshal.ReadInt32(cur);
            var blockSize = Marshal.ReadInt32(cur + 4);
            if (pageRva == 0 || blockSize == 0)
            {
                break;
            }

            var entries = (blockSize - 8) / 2;
            for (var i = 0; i < entries; i++)
            {
                var item = (ushort)Marshal.ReadInt16(cur + 8 + i * 2);
                var type = item >> 12;
                var off = item & 0x0FFF;
                var patch = baseAddr + pageRva + off;
                switch (type)
                {
                    case 0xA:   // IMAGE_REL_BASED_DIR64
                        var v = Marshal.ReadInt64(patch) + delta;
                        Marshal.WriteInt64(patch, v);
                        count++;
                        break;
                    case 0x3:   // IMAGE_REL_BASED_HIGHLOW
                        var v32 = Marshal.ReadInt32(patch) + (int)delta;
                        Marshal.WriteInt32(patch, v32);
                        count++;
                        break;
                }
            }

            cur += blockSize;
        }

        mod.RelocCount = count;
    }

    private static void ResolveImportsLocal(IntPtr baseAddr, int importRva, MappedModule mod)
    {
        if (importRva == 0)
        {
            return;
        }

        var desc = baseAddr + importRva;
        while (true)
        {
            var originalFirstThunk = Marshal.ReadInt32(desc);
            var nameRva = Marshal.ReadInt32(desc + 12);
            var firstThunk = Marshal.ReadInt32(desc + 16);
            if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
            {
                break;
            }

            var dllName = Marshal.PtrToStringAnsi(baseAddr + nameRva) ?? "";
            var h = LoadLibraryW(dllName);
            mod.ImportDllCount++;
            mod.ImportDlls.Add(dllName);
            if (h == IntPtr.Zero)
            {
                mod.Notes.Add($"!! 加载 {dllName} 失败，错误码 {Marshal.GetLastWin32Error()}");
                desc += 20;
                continue;
            }

            var intPtr = baseAddr + (originalFirstThunk != 0 ? originalFirstThunk : firstThunk);
            var iatPtr = baseAddr + firstThunk;
            var idx = 0;
            while (true)
            {
                var thunk = Marshal.ReadInt64(intPtr + idx * 8);
                if (thunk == 0)
                {
                    break;
                }

                IntPtr fn;
                if (thunk < 0)
                {
                    fn = GetProcAddress(h, (IntPtr)(thunk & 0xFFFF));
                }
                else
                {
                    var fnName = Marshal.PtrToStringAnsi(baseAddr + (int)(thunk & 0x7FFFFFFF) + 2) ?? "";
                    fn = GetProcAddress(h, fnName);
                }

                if (fn == IntPtr.Zero)
                {
                    mod.Notes.Add($"!! 找不到 {dllName} 里的第 {idx} 个导入");
                }

                Marshal.WriteInt64(iatPtr + idx * 8, fn.ToInt64());
                mod.ImportFuncCount++;
                idx++;
            }

            desc += 20;
        }
    }

    private static void SetupTls(IntPtr baseAddr, int tlsRva, MappedModule mod)
    {
        if (tlsRva == 0)
        {
            return;
        }

        var tls = baseAddr + tlsRva;
        var rawStart = Marshal.ReadInt64(tls);          // 已经是虚拟地址（重定位之后）
        var rawEnd = Marshal.ReadInt64(tls + 8);
        var addrOfIndex = Marshal.ReadInt64(tls + 16);
        var sizeOfZeroFill = Marshal.ReadInt32(tls + 32);

        var rawSize = (int)(rawEnd - rawStart);
        var index = TlsAlloc();
        mod.TlsIndex = index;
        Marshal.WriteInt32((IntPtr)addrOfIndex, index);

        // 给当前线程铺一份 TLS 数据副本（模板 + 补零区）
        var total = rawSize + sizeOfZeroFill;
        if (total > 0)
        {
            var block = VirtualAlloc(IntPtr.Zero, (UIntPtr)(uint)total, MemCommit | MemReserve, 0x04 /*PAGE_READWRITE*/);
            if (rawSize > 0)
            {
                CopyMemory(block, (IntPtr)rawStart, (uint)rawSize);
            }

            TlsSetValue(index, block);
            mod.Notes.Add($"TLS: 索引={index}，模板 {rawSize} 字节 + 补零 {sizeOfZeroFill}，当前线程已铺好");
        }
        else
        {
            mod.Notes.Add($"TLS: 索引={index}（模板为空）");
        }
    }

    private static void CallTlsCallbacks(IntPtr baseAddr, int tlsRva, MappedModule mod)
    {
        if (tlsRva == 0)
        {
            return;
        }

        var addrOfCallbacks = Marshal.ReadInt64(baseAddr + tlsRva + 24);
        if (addrOfCallbacks == 0)
        {
            return;
        }

        var p = (IntPtr)addrOfCallbacks;
        var n = 0;
        while (true)
        {
            var cb = Marshal.ReadInt64(p);
            if (cb == 0)
            {
                break;
            }

            var fn = Marshal.GetDelegateForFunctionPointer<TlsCallback>((IntPtr)cb);
            fn(baseAddr, DllProcessAttach, IntPtr.Zero);
            p += 8;
            n++;
        }

        mod.Notes.Add($"调了 {n} 个 TLS 回调");
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, uint len);
}
