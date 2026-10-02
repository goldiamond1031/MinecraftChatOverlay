using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MinecraftChatOverlay.Services.GameMotionBlur;

/// <summary>一个候选目标进程（能在任务栏上看到窗口的那种）。</summary>
public sealed class GameProcessInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";

    public override string ToString() => $"{Name}  (PID {Id})  {Title}";
}

/// <summary>
/// 把钩子 DLL 注入到目标进程。
///
/// 现在走的是**手动映射**：自己解析 PE、铺镜像、修重定位、填导入表、在目标里分配 TLS，
/// 再劫持一个线程跑一段 Stub 调 DllMain（详见 ManualMapper.cs / ManualMapper.Remote.cs）。
/// 老的 CreateRemoteThread + LoadLibraryW 那条路已经删掉了。
/// 注入之后 DLL 自己去找 OpenGL 的 SwapBuffers 调用方，游戏不用重启。
/// </summary>
public static class GameProcessInjector
{

    /// <summary>
    /// 优先用手动映射（自己当加载器，不走 LoadLibrary）：模块不出现在目标进程的模块表里、
    /// 磁盘上没有对应的加载痕迹。失败会自动回退到老的 LoadLibraryW 注入 —— 功能不能因为新路径挂掉。
    /// </summary>
    public static bool PreferManualMapping { get; set; } = true;

    /// <summary>手动映射过的 pid → 镜像基址。手动映射的模块不在模块表里，IsModuleLoaded 查不到，只能自己记。</summary>
    private static readonly Dictionary<int, long> ManualMapped = new();
    private static readonly object ManualMappedLock = new();

    private static bool IsManuallyMapped(int pid)
    {
        lock (ManualMappedLock)
        {
            if (!ManualMapped.ContainsKey(pid))
            {
                return false;
            }

            try
            {
                using var alive = System.Diagnostics.Process.GetProcessById(pid);
                return true;
            }
            catch
            {
                ManualMapped.Remove(pid);   // 进程没了，记录作废
                return false;
            }
        }
    }

    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint LIST_MODULES_ALL = 0x03;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize,
                                                    IntPtr startAddress, IntPtr parameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr thread, out UIntPtr exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool isWow64);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, uint size, out uint needed, uint filter);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleBaseNameW(IntPtr process, IntPtr module, [Out] char[] name, uint size);

    /// <summary>列出所有"有可见窗口"的进程，给界面上的下拉框用。</summary>
    public static List<GameProcessInfo> FindCandidateProcesses()
    {
        var result = new List<GameProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id <= 4 || process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                var title = process.MainWindowTitle;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                result.Add(new GameProcessInfo
                {
                    Id = process.Id,
                    Name = process.ProcessName + ".exe",
                    Title = title
                });
            }
            catch
            {
                // 有些系统进程拿属性会抛，忽略
            }
            finally
            {
                process.Dispose();
            }
        }

        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 把 dllPath 注入到 pid。失败时抛 InvalidOperationException，消息是给人看的。
    /// </summary>
    public static void Inject(int pid, string dllPath)
    {
        // 已经有一份手动映射的在跑了？别再来一份（两个钩子抢同一个后备缓冲会花屏/崩）
        // 已经有一份手动映射的在跑了？别再来一份（两个钩子抢同一个后备缓冲会花屏/崩）
        if (IsManuallyMapped(pid))
        {
            return;
        }

        if (!File.Exists(dllPath))
        {
            throw new InvalidOperationException("找不到钩子 DLL：" + dllPath + "\n先跑一下 native\\build.ps1 把它编出来。");
        }

        // 只走手动映射。原来的 CreateRemoteThread + LoadLibraryW 那条路已经删掉了：
        // 它会往目标进程的模块表里留记录、磁盘上留加载痕迹 —— 既然全程改成手动映射，
        // 就不该再有一条"会留下痕迹"的注入方式摆在代码里（也避免哪天被误用）。
        // 代价：手动映射失败就是注入失败，界面会直接把原因显示出来，没有兜底可退。
        try
        {
            var mapped = ManualMapper.MapRemote(pid, dllPath, invokeEntry: true, out _);
            lock (ManualMappedLock)
            {
                ManualMapped[pid] = mapped.Base.ToInt64();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("手动映射注入失败：" + ex.Message, ex);
        }
    }

    /// <summary>
    /// 目标进程里是不是已经加载了某个模块。
    /// 用来区分"没注入过"和"DLL 已在里面、但钩子被卸载过"——
    /// 后一种情况下 LoadLibrary 不会再跑 DllMain，光靠"再注入一次"是救不回来的。
    /// </summary>
    public static bool IsModuleLoaded(int pid, string moduleName)
    {
        // 手动映射进去的镜像不在模块表里，EnumProcessModules 查不到 —— 但它确实在跑，这里必须认
        if (IsManuallyMapped(pid))
        {
            return true;
        }

        if (pid <= 0 || string.IsNullOrWhiteSpace(moduleName))
        {
            return false;
        }

        IntPtr process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return FindRemoteModuleBase(process, moduleName) != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static void EnsureSameBitness(IntPtr process, int pid)
    {
        bool targetIsWow64;
        if (!IsWow64Process(process, out targetIsWow64))
        {
            return;
        }

        var selfIs64 = IntPtr.Size == 8;

        if (!selfIs64)
        {
            throw new InvalidOperationException("本程序是 32 位的，没法给 64 位游戏注入。请用 64 位版本。");
        }

        if (targetIsWow64)
        {
            throw new InvalidOperationException("PID " + pid + " 是 32 位进程，而钩子 DLL 是 64 位的，注入不了。");
        }
    }

    /// <summary>
    /// 拿到"目标进程里" LoadLibraryW 的地址。
    /// kernel32.dll 在每个进程里的基址通常一样，但不能假设，所以按模块枚举算偏移。
    /// </summary>
    private static IntPtr GetRemoteLoadLibraryAddress(IntPtr process)
    {
        var localKernel = GetModuleHandleW("kernel32.dll");
        var localLoadLibrary = GetProcAddress(localKernel, "LoadLibraryW");
        if (localKernel == IntPtr.Zero || localLoadLibrary == IntPtr.Zero)
        {
            throw new InvalidOperationException("本进程里找不到 kernel32!LoadLibraryW。");
        }

        var remoteKernel = FindRemoteModuleBase(process, "kernel32.dll");
        if (remoteKernel == IntPtr.Zero)
        {
            throw new InvalidOperationException("在目标进程里找不到 kernel32.dll。");
        }

        var offset = (long)localLoadLibrary - (long)localKernel;
        return (IntPtr)((long)remoteKernel + offset);
    }

    private static IntPtr FindRemoteModuleBase(IntPtr process, string moduleName)
    {
        var modules = new IntPtr[1024];
        uint needed;
        if (!EnumProcessModulesEx(process, modules, (uint)(modules.Length * IntPtr.Size), out needed, LIST_MODULES_ALL))
        {
            return IntPtr.Zero;
        }

        var count = (int)(needed / (uint)IntPtr.Size);
        if (count > modules.Length)
        {
            count = modules.Length;
        }

        var buffer = new char[260];
        for (var i = 0; i < count; i++)
        {
            if (modules[i] == IntPtr.Zero)
            {
                continue;
            }

            var length = GetModuleBaseNameW(process, modules[i], buffer, (uint)buffer.Length);
            if (length == 0)
            {
                continue;
            }

            var name = new string(buffer, 0, (int)length);
            if (string.Equals(name, moduleName, StringComparison.OrdinalIgnoreCase))
            {
                return modules[i];
            }
        }

        return IntPtr.Zero;
    }
}
