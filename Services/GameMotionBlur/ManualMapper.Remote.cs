using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MinecraftChatOverlay.Services.GameMotionBlur;

/// <summary>
/// 手动映射的**远程版**：把修好的镜像铺进目标进程，然后用"线程劫持 + Stub"调入口点。
///
/// 和本地版的区别只有两处：
///   1. 导入表不能填本地地址 —— 要按"远程模块基址 + (本地函数地址 − 本地宿主模块基址)"换算
///      （api-ms-win-crt-* 那些转发出去的真身是 ucrtbase，得按它算）。
///   2. TLS 索引必须在**目标进程里**分配 —— TlsAlloc 是每进程的，拿宿主机的索引去游戏里用
///      会指到别人的 TLS 槽。所以这部分塞进 Stub，在目标里执行。
/// </summary>
public static partial class ManualMapper
{
    public const uint ProcessAllAccess = 0x1F0FFF;

    private const uint MemRelease = 0x8000;
    private const uint PageExecuteRead = 0x20;
    private const uint PageReadWrite = 0x04;
    private const uint Th32CsSnapshotModule = 0x08;
    private const uint Th32CsSnapshotThread = 0x04;
    private const uint ThreadAllAccess = 0x1F03FF;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type, uint prot);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtectEx(IntPtr proc, IntPtr addr, UIntPtr size, uint prot, out uint oldProt);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Module32FirstW(IntPtr snap, ref ModuleEntryW me);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Module32NextW(IntPtr snap, ref ModuleEntryW me);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32First(IntPtr snap, ref ThreadEntry te);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Thread32Next(IntPtr snap, ref ThreadEntry te);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr h, IntPtr ctx);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadContext(IntPtr h, IntPtr ctx);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntryW
    {
        public uint Size; public uint ModuleId; public uint ProcessId; public uint GlobalUsage; public uint ProcessUsage;
        public IntPtr Base; public uint BaseSize; public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry { public uint Size; public uint Usage; public uint ThreadId; public uint OwnerPid; public int BasePri; public int DeltaPri; public uint Flags; }

    private sealed class ModuleInfo { public string Name = ""; public long Base; public long Size; }

    // ---------------------------------------------------------------- 模块列表
    private static List<ModuleInfo> LocalModules()
    {
        var list = new List<ModuleInfo>();
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
        {
            list.Add(new ModuleInfo { Name = m.ModuleName, Base = m.BaseAddress.ToInt64(), Size = m.ModuleMemorySize });
        }

        return list;
    }

    private static List<ModuleInfo> RemoteModules(IntPtr hProcess)
    {
        var list = new List<ModuleInfo>();
        var snap = CreateToolhelp32Snapshot(Th32CsSnapshotModule, GetProcessId(hProcess));
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
        {
            return list;
        }

        try
        {
            var me = new ModuleEntryW { Size = (uint)Marshal.SizeOf<ModuleEntryW>() };
            if (Module32FirstW(snap, ref me))
            {
                do
                {
                    list.Add(new ModuleInfo { Name = me.Module, Base = me.Base.ToInt64(), Size = me.BaseSize });
                    me.Size = (uint)Marshal.SizeOf<ModuleEntryW>();
                }
                while (Module32NextW(snap, ref me));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return list;
    }

    [DllImport("kernel32.dll")] private static extern int GetProcessId(IntPtr handle);

    /// <summary>本地某个函数地址 → 目标进程里的地址。</summary>
    private static long ToRemoteAddress(long localFn, List<ModuleInfo> local, List<ModuleInfo> remote, MappedModule mod)
    {
        var owner = local.FirstOrDefault(m => localFn >= m.Base && localFn < m.Base + m.Size);
        if (owner is null)
        {
            mod.Notes.Add($"!! 0x{localFn:X} 不属于任何本地模块，只能原样用");
            return localFn;
        }

        var same = remote.FirstOrDefault(m => string.Equals(m.Name, owner.Name, StringComparison.OrdinalIgnoreCase))
                   ?? remote.FirstOrDefault(m => m.Name.StartsWith(owner.Name.Replace(".dll", ""), StringComparison.OrdinalIgnoreCase));
        if (same is null)
        {
            mod.Notes.Add($"!! 目标进程里没有 {owner.Name}（本地 0x{localFn:X} 无法换算）");
            return localFn;
        }

        return same.Base + (localFn - owner.Base);
    }

    // ---------------------------------------------------------------- 镜像构建 + 修复
    private static byte[] BuildImage(byte[] file, out ulong imageBase, out int sizeOfImage, out int sizeOfHeaders,
        out int entryRva, out int numSections, out int optSize, out int nt, out int[] dirs)
    {
        ParseHeaders(file, out imageBase, out sizeOfImage, out sizeOfHeaders, out entryRva,
                     out numSections, out optSize, out dirs, out _, new MappedModule());
        nt = BitConverter.ToInt32(file, 0x3C);
        var img = new byte[sizeOfImage];
        Array.Copy(file, 0, img, 0, sizeOfHeaders);
        var secTable = nt + 4 + 20 + optSize;
        for (var i = 0; i < numSections; i++)
        {
            var sh = secTable + i * 40;
            var vSize = BitConverter.ToInt32(file, sh + 8);
            var vAddr = BitConverter.ToInt32(file, sh + 12);
            var rawSize = BitConverter.ToInt32(file, sh + 16);
            var rawPtr = BitConverter.ToInt32(file, sh + 20);
            var copy = Math.Min(vSize == 0 ? rawSize : vSize, rawSize);
            if (copy > 0 && rawPtr > 0)
            {
                Array.Copy(file, rawPtr, img, vAddr, copy);
            }
        }

        return img;
    }

    private static long R64(byte[] b, int off) => BitConverter.ToInt64(b, off);
    private static int R32(byte[] b, int off) => BitConverter.ToInt32(b, off);
    private static void W64(byte[] b, int off, long v) => BitConverter.GetBytes(v).CopyTo(b, off);
    private static void W32(byte[] b, int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);
    private static string Str(byte[] b, int off)
    {
        var end = off;
        while (end < b.Length && b[end] != 0) end++;
        return Encoding.ASCII.GetString(b, off, end - off);
    }

    private static void RelocateImage(byte[] img, long delta, int relocRva, MappedModule mod)
    {
        if (delta == 0 || relocRva == 0) return;
        var cur = relocRva;
        var count = 0;
        while (true)
        {
            var pageRva = R32(img, cur);
            var blockSize = R32(img, cur + 4);
            if (pageRva == 0 || blockSize == 0) break;
            var entries = (blockSize - 8) / 2;
            for (var i = 0; i < entries; i++)
            {
                var item = (ushort)BitConverter.ToInt16(img, cur + 8 + i * 2);
                var type = item >> 12;
                var off = item & 0x0FFF;
                var patch = pageRva + off;
                if (type == 0xA) { W64(img, patch, R64(img, patch) + delta); count++; }
                else if (type == 0x3) { W32(img, patch, R32(img, patch) + (int)delta); count++; }
            }

            cur += blockSize;
        }

        mod.RelocCount = count;
    }

    private static void FixImportsRemote(byte[] img, int importRva, List<ModuleInfo> local, List<ModuleInfo> remote, MappedModule mod)
    {
        if (importRva == 0) return;
        var desc = importRva;
        while (true)
        {
            var oft = R32(img, desc);
            var nameRva = R32(img, desc + 12);
            var ft = R32(img, desc + 16);
            if (oft == 0 && nameRva == 0 && ft == 0) break;

            var dll = Str(img, nameRva);
            var h = LoadLibraryW(dll);
            mod.ImportDllCount++;
            if (h == IntPtr.Zero)
            {
                mod.Notes.Add($"!! 本地加载 {dll} 失败");
                desc += 20;
                continue;
            }

            var intRva = oft != 0 ? oft : ft;
            for (var i = 0; ; i++)
            {
                var thunk = R64(img, intRva + i * 8);
                if (thunk == 0) break;

                IntPtr fn;
                if (thunk < 0) fn = GetProcAddress(h, (IntPtr)(thunk & 0xFFFF));
                else fn = GetProcAddress(h, Str(img, (int)(thunk & 0x7FFFFFFF) + 2));

                if (fn == IntPtr.Zero) { mod.Notes.Add($"!! 找不到 {dll} 第 {i} 个导入"); }
                W64(img, ft + i * 8, ToRemoteAddress(fn.ToInt64(), local, remote, mod));
                mod.ImportFuncCount++;
            }

            desc += 20;
        }
    }

    // ---------------------------------------------------------------- Stub 生成
    private sealed class StubBuilder
    {
        private readonly List<byte> _b = new();
        private readonly List<(int at, int target)> _fix = new();

        public int Pos => _b.Count;

        public void Raw(params int[] bytes)
        {
            foreach (var x in bytes) _b.Add((byte)x);
        }

        /// <summary>rip 相对的 [rip+disp32] 操作数位置，留到后面回填。</summary>
        private void RipRef(int paramOffset)
        {
            var at = _b.Count;
            Raw(0, 0, 0, 0);
            _fix.Add((at, paramOffset));   // 调用方传进来的就是绝对偏移（0x100 起），别再叠加
        }

        public const int ParamBase = 0x100;

        public void PushAll()
        {
            Raw(0x9C);                 // pushfq
            Raw(0x50);                 // push rax
            Raw(0x51);                 // push rcx
            Raw(0x52);                 // push rdx
            Raw(0x41, 0x50);           // push r8
            Raw(0x41, 0x51);           // push r9
            Raw(0x41, 0x52);           // push r10
            Raw(0x41, 0x53);           // push r11
            // 保存区栈指针**存内存**，别用寄存器 —— 中间要 call DllMain，
            // 易失寄存器（rax/rcx/rdx/r8-r11）都可能被被调方冲掉（r11 踩过这个坑：
            // 用 r11 存栈指针，DllMain 里的 CreateThread 把它冲了，收尾的 pop 直接访问冲突）
            StoreRsp(SavedRspOffset);
            Raw(0x48, 0x83, 0xE4, 0xF0);   // and rsp, -16
            Raw(0x48, 0x83, 0xEC, 0x20);   // sub rsp, 0x20
        }

        public void PopAll()
        {
            LoadRsp(SavedRspOffset);
            Raw(0x41, 0x5B); Raw(0x41, 0x5A); Raw(0x41, 0x59); Raw(0x41, 0x58);
            Raw(0x5A); Raw(0x59); Raw(0x58);
            Raw(0x9D);                 // popfq
        }

        public void LoadRcx(int paramOff) { Raw(0x48, 0x8B, 0x0D); RipRef(paramOff); }
        public void LoadRdx(int paramOff) { Raw(0x48, 0x8B, 0x15); RipRef(paramOff); }
        public void LoadRax(int paramOff) { Raw(0x48, 0x8B, 0x05); RipRef(paramOff); }
        public void StoreRax(int paramOff) { Raw(0x48, 0x89, 0x05); RipRef(paramOff); }
        public void SetRdxImm(int v) { Raw(0xBA); Raw(v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF, (v >> 24) & 0xFF); }
        public void ZeroR8() { Raw(0x45, 0x31, 0xC0); }
        public void CallRax() { Raw(0xFF, 0xD0); }
        /// <summary>mov [rcx], rax —— 把结果写回镜像里的某个绝对地址。</summary>
        public void StoreRaxToRcx() { Raw(0x48, 0x89, 0x01); }
        public const int SavedRspOffset = 0x150;

        /// <summary>mov [rip+d], rsp —— 把当前栈指针存进参数块。</summary>
        public void StoreRsp(int paramOff) { Raw(0x48, 0x89, 0x25); RipRef(paramOff); }

        /// <summary>mov rsp, [rip+d] —— 从参数块恢复栈指针。</summary>
        public void LoadRsp(int paramOff) { Raw(0x48, 0x8B, 0x25); RipRef(paramOff); }

        public void JmpRip(int paramOff) { Raw(0xFF, 0x25); RipRef(paramOff); }

        public byte[] Build(int baseAddress)
        {
            var arr = _b.ToArray();
            foreach (var (at, target) in _fix)
            {
                var next = at + 4;
                var disp = target - next;
                BitConverter.GetBytes(disp).CopyTo(arr, at);
            }

            return arr;
        }
    }

    // ---------------------------------------------------------------- 远程映射
    public sealed class RemoteResult
    {
        public long RemoteBase;
        public long StubAddress;
        public int DllMainReturn;
        public int TlsIndex;
        public int HijackedThreadId;
        public List<string> Log = new();
    }

    /// <summary>把 DLL 手动映射进目标进程。<paramref name="invokeEntry"/> = 是否顺便调入口点（劫持线程）。</summary>
    public static MappedModule MapRemote(int pid, string dllPath, bool invokeEntry, out RemoteResult result)
    {
        result = new RemoteResult();
        var mod = new MappedModule();
        var file = File.ReadAllBytes(dllPath);

        var hProcess = OpenProcess(ProcessAllAccess, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenProcess 失败（pid={pid}），错误码 {Marshal.GetLastWin32Error()}");
        }

        try
        {
            var img = BuildImage(file, out var imageBase, out var sizeOfImage, out _, out var entryRva,
                                 out var numSections, out var optSize, out var nt, out var dirs);

            // 1) 目标进程里分配
            var remoteBase = VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)(uint)sizeOfImage, MemCommit | MemReserve, PageExecuteReadWrite);
            if (remoteBase == IntPtr.Zero)
            {
                throw new InvalidOperationException("VirtualAllocEx 失败，错误码 " + Marshal.GetLastWin32Error());
            }

            result.RemoteBase = remoteBase.ToInt64();
            mod.Base = remoteBase;
            mod.SizeOfImage = sizeOfImage;
            var delta = remoteBase.ToInt64() - (long)imageBase;
            result.Log.Add($"目标内基址 0x{remoteBase.ToInt64():X}（编译时 0x{imageBase:X}，delta=0x{delta:X}）");

            // 2) 重定位 + 导入表
            RelocateImage(img, delta, dirs[5], mod);
            var local = LocalModules();
            var remote = RemoteModules(hProcess);
            result.Log.Add($"本地模块 {local.Count} 个 / 目标模块 {remote.Count} 个");
            FixImportsRemote(img, dirs[1], local, remote, mod);

            mod.EntryPoint = remoteBase + entryRva;

            // 3) 铺进目标
            var written = UIntPtr.Zero;
            if (!WriteProcessMemory(hProcess, remoteBase, img, (UIntPtr)(uint)img.Length, out written))
            {
                throw new InvalidOperationException("WriteProcessMemory 失败，错误码 " + Marshal.GetLastWin32Error());
            }

            result.Log.Add($"镜像已写入 {written.ToUInt64()} 字节");

            // 4) 节区保护（.text → RX，其余 → RW，先一律 RX/RW 就够）
            var secTable = nt + 4 + 20 + optSize;
            for (var i = 0; i < numSections; i++)
            {
                var sh = secTable + i * 40;
                var vSize = BitConverter.ToInt32(file, sh + 8);
                var vAddr = BitConverter.ToInt32(file, sh + 12);
                var chars = BitConverter.ToInt32(file, sh + 36);
                var executable = (chars & 0x20000000) != 0;
                var prot = executable ? PageExecuteRead : PageReadWrite;
                VirtualProtectEx(hProcess, remoteBase + vAddr, (UIntPtr)(uint)Math.Max(vSize, 0x1000), prot, out _);
            }

            // 5) 入口点（可选）
            if (invokeEntry)
            {
                InvokeEntryPoint(hProcess, pid, remoteBase, entryRva, dirs[9], result, mod);
            }

            return mod;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    // ---------------------------------------------------------------- 劫持
    private static void InvokeEntryPoint(IntPtr hProcess, int pid, IntPtr remoteBase, int entryRva, int tlsRva,
        RemoteResult result, MappedModule mod)
    {
        // TLS：在目标里分配索引 + 给被劫持线程铺模板（这两步都在 Stub 里做）
        var addrOfIndex = 0L;
        var tlsBlock = IntPtr.Zero;
        if (tlsRva != 0)
        {
            var tlsDir = ReadRemote(hProcess, remoteBase + tlsRva, 40);
            // 注意：TLS 目录里这三个**已经是虚拟地址**了（重定位已经把 ImageBase 换成真实基址），
            // 绝对不能再加一遍 remoteBase —— 那是"加两次"，会算出野地址、把游戏内存写坏。
            var rawStart = R64(tlsDir, 0);
            var rawEnd = R64(tlsDir, 8);
            addrOfIndex = R64(tlsDir, 16);

            // 保险：这三个地址必须落在我们刚铺的那块镜像里，不在就说明算错了 —— 宁可报错也别劫持
            var lo = remoteBase.ToInt64();
            var hi = lo + mod.SizeOfImage;
            if (addrOfIndex < lo || addrOfIndex >= hi || rawStart < lo || rawStart > hi)
            {
                throw new InvalidOperationException($"TLS 目录地址不合理（rawStart=0x{rawStart:X} addrOfIndex=0x{addrOfIndex:X} 镜像=[0x{lo:X},0x{hi:X})），已放弃劫持");
            }
            var rawSize = (int)(rawEnd - rawStart);
            tlsBlock = VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)(uint)Math.Max(rawSize, 16), MemCommit | MemReserve, PageReadWrite);
            if (rawSize > 0 && tlsBlock != IntPtr.Zero)
            {
                var template = ReadRemote(hProcess, (IntPtr)rawStart, rawSize);
                WriteProcessMemory(hProcess, tlsBlock, template, (UIntPtr)(uint)rawSize, out _);
            }

            result.Log.Add($"TLS 模板 {rawSize} 字节 → 目标内 0x{tlsBlock.ToInt64():X}（索引由 Stub 里 TlsAlloc 分配）");
        }

        var kernel32Local = LoadLibraryW("kernel32.dll");
        var tlsAllocLocal = GetProcAddress(kernel32Local, "TlsAlloc").ToInt64();
        var tlsSetLocal = GetProcAddress(kernel32Local, "TlsSetValue").ToInt64();
        var local = LocalModules();
        var remote = RemoteModules(hProcess);
        var tlsAllocRemote = ToRemoteAddress(tlsAllocLocal, local, remote, mod);
        var tlsSetRemote = ToRemoteAddress(tlsSetLocal, local, remote, mod);

        // Stub + 参数块
        var stubMem = VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)0x1000, MemCommit | MemReserve, PageExecuteReadWrite);
        if (stubMem == IntPtr.Zero) throw new InvalidOperationException("给 Stub 分配内存失败");
        result.StubAddress = stubMem.ToInt64();

        var P_HINST = 0x100; var P_ENTRY = 0x108; var P_RET = 0x110; var P_RIP = 0x118;
        var P_TLSIDX = 0x120; var P_TLSPTR = 0x128; var P_TLSADDR = 0x130;
        var P_TLSALLOC = 0x138; var P_TLSSET = 0x140;

        var sb = new StubBuilder();
        sb.PushAll();
        // ① TlsAlloc() → 写进镜像的 AddressOfIndex，同时留在参数块
        sb.LoadRax(P_TLSALLOC);
        sb.CallRax();
        sb.StoreRax(P_TLSIDX);
        sb.LoadRcx(P_TLSADDR);
        sb.StoreRaxToRcx();
        // ② TlsSetValue(索引, 我铺的模板)
        sb.LoadRcx(P_TLSIDX);
        sb.LoadRdx(P_TLSPTR);
        sb.LoadRax(P_TLSSET);
        sb.CallRax();
        // ③ DllMain(hinstDLL, DLL_PROCESS_ATTACH, NULL)
        sb.LoadRcx(P_HINST);
        sb.SetRdxImm(1);
        sb.ZeroR8();
        sb.LoadRax(P_ENTRY);
        sb.CallRax();
        sb.StoreRax(P_RET);
        sb.PopAll();
        sb.JmpRip(P_RIP);

        var stub = sb.Build(0);
        result.Log.Add($"Stub {stub.Length} 字节");

        // 参数块
        var parms = new byte[0x100];
        void Set(int off, long v) => BitConverter.GetBytes(v).CopyTo(parms, off - 0x100);
        Set(P_HINST, remoteBase.ToInt64());
        Set(P_ENTRY, remoteBase.ToInt64() + entryRva);
        Set(P_TLSIDX, 0);
        Set(P_TLSPTR, tlsBlock.ToInt64());
        Set(P_TLSADDR, addrOfIndex);
        Set(P_TLSALLOC, tlsAllocRemote);
        Set(P_TLSSET, tlsSetRemote);

        var page = new byte[0x1000];
        Array.Copy(stub, 0, page, 0, stub.Length);
        Array.Copy(parms, 0, page, 0x100, parms.Length);
        WriteProcessMemory(hProcess, stubMem, page, (UIntPtr)(uint)page.Length, out _);
        VirtualProtectEx(hProcess, stubMem, (UIntPtr)0x1000, PageExecuteReadWrite, out _);

        // 挑一个线程劫持（跳过线程 0 和当前进程自己的）
        var tid = PickBestThread(pid);
        if (tid == 0) throw new InvalidOperationException("目标进程里没找到可用线程");
        result.HijackedThreadId = (int)tid;

        var hThread = OpenThread(ThreadAllAccess, false, tid);
        if (hThread == IntPtr.Zero) throw new InvalidOperationException($"OpenThread({tid}) 失败，错误码 {Marshal.GetLastWin32Error()}");

        try
        {
            var ctx = Marshal.AllocHGlobal(1264);
            try
            {
                for (var i = 0; i < 1264; i++) Marshal.WriteByte(ctx, i, 0);
                Marshal.WriteInt32(ctx, 0x30, 0x0010000B);       // ContextFlags = CONTROL|INTEGER|SEGMENTS
                if (SuspendThread(hThread) == unchecked((uint)-1))
                {
                    throw new InvalidOperationException("SuspendThread 失败");
                }

                try
                {
                    if (!GetThreadContext(hThread, ctx)) throw new InvalidOperationException("GetThreadContext 失败");

                    var oldRip = Marshal.ReadInt64(ctx, 0xF8);
                    Set(P_RIP, oldRip);
                    Array.Copy(parms, 0, page, 0x100, parms.Length);
                    WriteProcessMemory(hProcess, stubMem, page, (UIntPtr)(uint)page.Length, out _);

                    Marshal.WriteInt64(ctx, 0xF8, stubMem.ToInt64());
                    if (!SetThreadContext(hThread, ctx)) throw new InvalidOperationException("SetThreadContext 失败");
                    result.Log.Add($"已把线程 {tid} 的 RIP 从 0x{oldRip:X} 改到 Stub 0x{stubMem.ToInt64():X}");
                }
                finally
                {
                    ResumeThread(hThread);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ctx);
            }

            // 等 Stub 里的 DllMain 返回
            var buf = new byte[8];
            for (var i = 0; i < 100; i++)
            {
                System.Threading.Thread.Sleep(50);
                if (ReadProcessMemory(hProcess, stubMem + P_RET, buf, (UIntPtr)8, out _))
                {
                    var ret = BitConverter.ToInt64(buf, 0);
                    if (ret != 0)
                    {
                        result.DllMainReturn = (int)ret;
                        break;
                    }
                }
            }

            if (ReadProcessMemory(hProcess, stubMem + P_TLSIDX, buf, (UIntPtr)8, out _))
            {
                result.TlsIndex = (int)BitConverter.ToInt64(buf, 0);
            }

            result.Log.Add(result.DllMainReturn != 0
                ? $"DllMain 返回 {result.DllMainReturn}（TRUE）→ 入口点调用成功"
                : "等不到 DllMain 返回值（超时 5 秒）");
        }
        finally
        {
            CloseHandle(hThread);
        }
    }

    private static byte[] ReadRemote(IntPtr hProcess, IntPtr addr, int len)
    {
        var buf = new byte[len];
        ReadProcessMemory(hProcess, addr, buf, (UIntPtr)(uint)len, out _);
        return buf;
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>
    /// 优先挑"拥有主窗口的那个线程" —— 它就是游戏主循环，一直热着，RIP 一改马上就会执行到；
    /// 随便挑的第一个线程很可能是停在 Wait 里的（改了 RIP 也要等它被唤醒才轮到跑）。
    /// </summary>
    private static uint PickBestThread(int pid)
    {
        var tid = 0u;
        try
        {
            EnumWindows((h, _) =>
            {
                uint p;
                var t = GetWindowThreadProcessId(h, out p);
                if (p == pid && t != 0) { tid = t; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        return tid != 0 ? tid : PickThread(pid);
    }


    private static uint PickThread(int pid)
    {
        var snap = CreateToolhelp32Snapshot(Th32CsSnapshotThread, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
        try
        {
            var te = new ThreadEntry { Size = (uint)Marshal.SizeOf<ThreadEntry>() };
            if (Thread32First(snap, ref te))
            {
                do
                {
                    if (te.OwnerPid == pid) return te.ThreadId;
                    te.Size = (uint)Marshal.SizeOf<ThreadEntry>();
                }
                while (Thread32Next(snap, ref te));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return 0;
    }
}
