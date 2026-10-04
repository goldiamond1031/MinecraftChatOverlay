using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MinecraftChatOverlay.Plugins.KeyDisplay;

/// <summary>
/// 按键读取方式。
///
/// 这个插件支持两种读法，各有各的适用面，用户在设置页里自己选：
///
///   · <see cref="Polling"/> —— <c>GetAsyncKeyState</c> 轮询。
///     读的是 Windows 消息队列里的按键状态。对系统输入链路**零介入**，
///     不带权限、不被杀软盯、卸载就是什么都不剩。代价是**读不到走 Raw Input 的游戏**：
///     游戏引擎直接读键盘驱动的原始数据、不往消息队列塞 WM_KEYDOWN，
///     操作系统根本不知道键被按了，轮询就永远是"没按"。
///     绝区零、原神这类引擎自己管输入的游戏就是这种情况。
///
///   · <see cref="Hook"/> —— <c>WH_KEYBOARD_LL</c> 低级键盘钩子。
///     钩子挂在系统输入链路上，Raw Input 也会经过它，所以**游戏里能读到**。
///     代价是全程介入每次按键（回调里只做一次字典写，微秒级）、需要宿主提权
///     （否则受 UIPI 限制，依然读不到提权进程的按键）、且是杀软会多看两眼的经典手法。
///
/// 两者都**只读**：不记录历史、不写任何文件、不知道用户打了什么字。
/// 切换即时生效，不需要重启宿主。
/// </summary>
public enum KeyInputMode
{
    /// <summary>GetAsyncKeyState 轮询。默认 —— 零介入，但读不到 Raw Input 游戏。</summary>
    Polling = 0,

    /// <summary>WH_KEYBOARD_LL 低级键盘钩子。能读游戏，代价是需要提权、介入输入链路。</summary>
    Hook = 1,
}

/// <summary>
/// 一个"按键状态来源"。
///
/// 上层（悬浮窗刷新、CPS 采样、自检）只认这个接口的 <see cref="IsDown"/>，
/// 换实现不动上层一行 —— 这是双模式能干净共存的关键。
/// </summary>
internal interface IKeyStateSource : IDisposable
{
    /// <summary>这个源当前是否可用（钩子装失败时是 false）。</summary>
    bool IsActive { get; }

    /// <summary>这个源的人话名字，用来打日志和显示在设置页。</summary>
    string Name { get; }

    /// <summary>查一个虚拟键当前有没有按下。查询失败一律当"没按"，绝不抛异常。</summary>
    bool IsDown(int virtualKey);
}

/// <summary>
/// 轮询源：把原有的 <see cref="VirtualKeys.IsDownPolling"/> 包一层，接口跟钩子源对齐。
///
/// 这个类故意做得"薄" —— 它不持有任何状态、没有线程、没有句柄，
/// <see cref="Dispose"/> 也就没什么可收的。之所以还要实现 IDisposable，
/// 是为了让上层「切换源 = Dispose 旧的 + 建新的」这条逻辑对两种源一视同仁。
/// </summary>
internal sealed class PollingKeyStateSource : IKeyStateSource
{
    public bool IsActive => true;

    public string Name => "GetAsyncKeyState 轮询";

    public bool IsDown(int virtualKey) => VirtualKeys.IsDownPolling(virtualKey);

    public void Dispose()
    {
        // 无状态，没什么可收的
    }
}

/// <summary>
/// 低级钩子源（<c>WH_KEYBOARD_LL</c> + <c>WH_MOUSE_LL</c>）。
///
/// ⚠ **键盘和鼠标是两个独立的钩子，必须各装一个。** 只装键盘的话，
///   任何走 Raw Input 的游戏里鼠标键都读不到 —— 表现为"键盘格子亮了、鼠标格子不亮"。
///   两个钩子共用同一张虚拟键码状态位图，所以上层接口仍然只有一个 IsDown。
///
/// ────────────────────────────────────────────────────────────────────
/// 为什么这里可以用钩子（以及和 AutoGg 的区别）
/// ────────────────────────────────────────────────────────────────────
/// 项目里 AutoGg 也用钩子，但它要 <c>return 1</c> **吞掉**按键做键盘锁定 —— 那是"拦截"。
/// 这里不一样：回调**只记下"哪个键现在是按下/松开"，然后立刻 CallNextHookEx 放行**。
/// 按键该去哪去哪，我们只是"顺便看了一眼"。所以它对输入的影响是零：
/// 没有延迟、不会丢键、不会改键行为。
///
/// ────────────────────────────────────────────────────────────────────
/// 为什么能读到游戏（而轮询不行）
/// ────────────────────────────────────────────────────────────────────
/// WH_KEYBOARD_LL 挂的位置比消息队列**更靠近硬件**：键盘驱动产生的事件先过一遍
/// 所有低级键盘钩子，然后才被分发给前台窗口 / Raw Input。所以哪怕游戏用 Raw Input
/// 绕过消息系统，钩子也照样能看到。
///
/// ⚠ 但**要求宿主以管理员权限运行**：Windows 的 UIPI 机制不允许低完整性级别的进程
///   接收高完整性级别进程的输入。绝区零由米哈游启动器提权拉起，宿主不提权的话，
///   钩子依然收不到它的按键 —— 这一点在设置页要向用户说明。
///
/// ────────────────────────────────────────────────────────────────────
/// 几个必须守住的点（都是血的教训换来的常识）
/// ────────────────────────────────────────────────────────────────────
///  1. **回调必须尽快返回**。系统给钩子回调的超时是 300ms（Win10+ 是 LowLevelHooksTimeout，
///     注册表可调）。超时一次，Windows 会**静默摘掉**这个钩子且不通知我们 ——
///     表现就是"用着用着突然不亮了"，且没有任何报错。所以回调里只做一次字典写，
///     不取锁、不做 IO、不碰 UI。
///  2. **回调要 try/catch 全包**。回调里抛异常会一路穿到系统，后果不可控。
///     宁可丢一次状态更新，也不能让异常跑出去。而且异常本身也会导致钩子被摘。
///  3. **必须 CallNextHookEx 放行**。忘了这一步 = 吞掉全系统的按键，用户机器直接没法打字。
///     这是本文件最不能出错的一行。
///  4. **装钩子的线程必须有消息循环**。低级钩子靠宿主线程的消息泵来派发回调，
///     在没有消息循环的线程上装（比如纯后台线程）会装成功但永远收不到回调。
///     我们默认在 UI 线程装 —— WPF 的 UI 线程天然有 Dispatcher 消息循环。
///  5. **要能检测"钩子被摘"**。上面第 1 条说了系统摘钩子不通知。所以加一个
///     <see cref="EnsureAlive"/> 健康检查：发现钩子句柄失效就重装，并且每次
///     按键事件都刷新时间戳，长时间收不到事件时主动探活。
///
/// ────────────────────────────────────────────────────────────────────
/// 隐私边界（为什么代码这样写）
/// ────────────────────────────────────────────────────────────────────
///   · 回调里**只记按键的"按下/松开"状态**，而且只记二进制位；
///   · 不记时间戳之外的任何东西（时间戳只用于判断钩子还活着）；
///   · 不区分来源进程、不记窗口标题、不记按键序列；
///   · 不写任何文件；卸载时钩子句柄释放、状态表清空。
/// </summary>
internal sealed class HookKeyStateSource : IKeyStateSource
{
    // ---- Win32 ----

    //
    // ⚠ 键盘和鼠标是**两个独立的钩子**，必须各装一个 —— 只装键盘的话，
    //   鼠标键在任何走 Raw Input 的游戏里都读不到（gold_ 实测报过：钩子模式下
    //   绝区零里键盘通了、鼠标格子还是不亮，就是漏了这个 WH_MOUSE_LL）。
    //
    // 两个钩子共用同一张按下状态位图（虚拟键码 0x01 左键 / 0x02 右键 / 0x04 中键 …），
    // 因为用户配置里鼠标键和键盘键是用同一套"虚拟键码"表示的，读取接口也只有一个。

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    // 键盘消息
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    // 鼠标消息 —— 注意回调返回后，这几种"按下/抬起"消息还会被系统转成
    // 虚拟键码事件（WM_LBUTTONDOWN → VK_LBUTTON 等），但那条路径不保证在游戏里走到，
    // 所以我们直接在低级钩子这一层把它映射成虚拟键码自己记。
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    /// <summary>鼠标侧键（XButton）在 MSLLHOOKSTRUCT.mouseData 的高位里，1=侧键1 2=侧键2。</summary>
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;

    /// <summary>低级键盘钩子的回调（必须是静态方法引用，否则会被 GC 回收 —— 经典坑）。</summary>
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>低级鼠标钩子的回调。结构和键盘那条不同，所以是另一个委托类型。</summary>
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>KBDLLHOOKSTRUCT —— 键盘钩子回调收到的东西。只取我们需要的 vkCode，其余不碰。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    /// <summary>
    /// MSLLHOOKSTRUCT —— 鼠标钩子回调收到的东西。
    ///
    /// ⚠ 布局和 KBDLLHOOKSTRUCT 完全不同，别混用（混了会读到垃圾值，
    ///   表现是"键位状态乱七八糟"）。这里只取 mouseData（判侧键用），
    ///   坐标那两个字段**刻意不读** —— 键位显示不需要知道鼠标在哪，
    ///   少读一样就少一份隐私面。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public int PtX;
        public int PtY;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    // ---- 状态 ----

    /// <summary>
    /// 当前按下的键。
    ///
    /// 用位图而不是 HashSet 有几个好处：读写都是单条指令之外的操作、没有哈希、
    /// 扩容不发生、GC 为零 —— 因为这是**在系统输入回调里被高频访问**的东西，
    /// 任何分配/锁都可能把回调拖过 300ms 超时线。
    ///
    /// 256 位 = 4 个 ulong，正好覆盖全部虚拟键码（0x00~0xFF）。
    /// </summary>
    /// <summary>按下的状态位图，见字段注释。</summary>
    private readonly ulong[] _downBits = new ulong[4];

    /// <summary>自检用：回调一共处理过多少次事件（键盘 + 鼠标）。</summary>
    internal long EventCount { get; private set; }

    /// <summary>回调写、外部读，用 volatile 保证可见性（不用锁 —— 见类注释第 1 条）。</summary>
    private volatile bool _active;

    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;   // 强引用，防止被 GC 回收

    /// <summary>鼠标钩子句柄 + 它的回调强引用（同样是防 GC 用的）。</summary>
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private LowLevelMouseProc? _mouseProc;

    /// <summary>最近一次收到按键事件的时间（UTC tick）。用于探活。</summary>
    private long _lastEventTicks;

    /// <summary>当前钩子的"代数"，重装一次 +1。日志用。</summary>
    private int _generation;

    /// <summary>安装失败的原因（成功时为空）。设置页会拿它给用户看。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>钩子被系统摘掉后重装的次数。一直涨说明环境有问题（被杀软拦之类）。</summary>
    public int ReinstallCount { get; private set; }

    public bool IsActive => _active && (_hookHandle != IntPtr.Zero || _mouseHookHandle != IntPtr.Zero);

    public string Name => "WH_KEYBOARD_LL + WH_MOUSE_LL 低级钩子";

    /// <summary>
    /// 装钩子。**键盘和鼠标各装一个** —— 只装键盘的话游戏里鼠标键读不到。
    ///
    /// ⚠ 必须在**有消息循环的线程**上调用（见类注释第 4 条）。插件在 UI 线程调用它。
    ///
    /// 容错策略：两个里**任意一个**装成功就算成功（另一个记日志）。
    /// 不因为鼠标钩子装不上就把整个钩子模式判死 —— 那样用户连键盘也用不了了，
    /// 比"只有键盘能用"糟得多。
    /// </summary>
    public bool Install()
    {
        if (_hookHandle != IntPtr.Zero && _mouseHookHandle != IntPtr.Zero)
        {
            return true;    // 两个都装好了
        }

        var keyboardOk = _hookHandle != IntPtr.Zero;
        var mouseOk = _mouseHookHandle != IntPtr.Zero;

        try
        {
            // —— 键盘 ——
            if (!keyboardOk)
            {
                _proc ??= KeyboardCallback;   // 委托实例必须留字段，否则被 GC 回调野指针（第 ① 条）

                _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
                keyboardOk = _hookHandle != IntPtr.Zero;

                if (!keyboardOk)
                {
                    LastError = $"键盘钩子失败（Win32 错误 {Marshal.GetLastWin32Error()}）";
                }
            }

            // —— 鼠标 ——
            // ⚠ 必须单独装。低级钩子里键盘和鼠标互不相干，装了键盘不等于鼠标也过我们的回调
            //   （2026-10-03 实测：钩子模式下绝区零里键盘亮了、鼠标不亮，就是漏了这一半）。
            if (!mouseOk)
            {
                _mouseProc ??= MouseCallback;

                _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
                mouseOk = _mouseHookHandle != IntPtr.Zero;

                if (!mouseOk)
                {
                    LastError = $"鼠标钩子失败（Win32 错误 {Marshal.GetLastWin32Error()}）";
                }
            }

            if (!keyboardOk && !mouseOk)
            {
                _active = false;
                return false;
            }

            ClearAll();
            _generation++;
            _lastEventTicks = DateTime.UtcNow.Ticks;
            _active = true;

            ReturnToCallerLog?.Invoke(
                $"[按键显示] 钩子已安装：键盘={keyboardOk} 鼠标={mouseOk}"
                + (keyboardOk && mouseOk ? "" : "（有一个没装上，那一半会退回轮询）"));

            if (keyboardOk && mouseOk)
            {
                LastError = "";
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = "装钩子异常：" + ex.Message;
            _active = false;
            return false;
        }
    }

    /// <summary>卸钩子。幂等 —— 重复调用安全，没装过也安全。</summary>
    public void Uninstall()
    {
        _active = false;

        var handle = _hookHandle;
        _hookHandle = IntPtr.Zero;

        if (handle != IntPtr.Zero)
        {
            try
            {
                UnhookWindowsHookEx(handle);
            }
            catch
            {
                // 卸载路径绝不抛 —— Shutdown 必须是幂等的
            }
        }

        var mouseHandle = _mouseHookHandle;
        _mouseHookHandle = IntPtr.Zero;

        if (mouseHandle != IntPtr.Zero)
        {
            try
            {
                UnhookWindowsHookEx(mouseHandle);
            }
            catch
            {
            }
        }

        ClearAll();
    }

    /// <summary>
    /// 健康检查：钩子还在不在。系统摘钩子不通知，只能自己探。
    ///
    /// 探法：发现句柄失效就重装。重装代价极小（两次 Win32 调用），
    /// 而且能 100% 修复"被摘"的情况，所以这里不做复杂的判活，直接重装。
    /// </summary>
    public bool EnsureAlive()
    {
        if (!_active)
        {
            return Install();
        }

        if (_hookHandle != IntPtr.Zero && _mouseHookHandle != IntPtr.Zero)
        {
            return true;
        }

        // 有句柄没了但还标着 active —— 被摘了，重装
        ReinstallCount++;
        ReturnToCallerLog?.Invoke(
            $"[按键显示] 钩子失效（键盘={_hookHandle != IntPtr.Zero} 鼠标={_mouseHookHandle != IntPtr.Zero}），"
            + $"正在重装（第 {ReinstallCount} 次）");

        if (Install())
        {
            return true;
        }

        ReturnToCallerLog?.Invoke($"[按键显示] 钩子重装失败：{LastError}");
        return false;
    }

    /// <summary>诊断输出用。插件注入进来，避免这里直接依赖宿主。</summary>
    internal Action<string>? ReturnToCallerLog { get; set; }

    /// <summary>自检用：距上次收到按键事件过去了多久（秒）。</summary>
    internal double SecondsSinceLastEvent
    {
        get
        {
            var ticks = _lastEventTicks;
            if (ticks == 0)
            {
                return double.MaxValue;
            }

            return (DateTime.UtcNow.Ticks - ticks) / (double)TimeSpan.TicksPerSecond;
        }
    }

    /// <summary>
    /// 键盘钩子回调。**这是整个插件里最不能出错的一段代码。**
    ///
    /// 规矩（顺序都不能换）：
    ///   1. nCode &lt; 0 必须无条件放行（系统约定）；
    ///   2. 认不认得的消息一律放行；
    ///   3. 处理逻辑整段 try/catch 包住，绝不往外抛；
    ///   4. 最后必须 CallNextHookEx —— 漏了这行，全系统键盘就废了。
    /// </summary>
    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var msg = wParam.ToInt32();

                // 只关心"按下/松开"这四种（含 Alt 系）
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN ||
                    msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                    var vk = (int)info.VkCode;

                    var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

                    SetBit(vk, isDown);

                    _lastEventTicks = DateTime.UtcNow.Ticks;
                    EventCount++;
                }
            }
        }
        catch
        {
            // 吞掉 —— 回调绝不能往外抛（见类注释第 2 条）
        }

        // ★ 必须放行。按键显示只是"顺便看一眼"，不做任何拦截。
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>
    /// 鼠标钩子回调。规矩和键盘那条完全一样（超时意识、异常吞掉、必须放行）。
    ///
    /// 唯一不同的是**消息到虚拟键码的映射**：
    /// 鼠标消息的 wParam 是 WM_LBUTTONDOWN 这类，不是虚拟键码，
    /// 所以要自己转成 VK_LBUTTON(0x01) / VK_RBUTTON(0x02) / VK_MBUTTON(0x04) /
    /// VK_XBUTTON1(0x05) / VK_XBUTTON2(0x06) —— 这样上层拿到的就是统一的虚拟键码，
    /// 跟键盘、跟配置里的存法全都对得上。
    /// </summary>
    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var msg = wParam.ToInt32();

                int? vk = msg switch
                {
                    WM_LBUTTONDOWN or WM_LBUTTONUP => 0x01,
                    WM_RBUTTONDOWN or WM_RBUTTONUP => 0x02,
                    WM_MBUTTONDOWN or WM_MBUTTONUP => 0x04,
                    _ => null,
                };

                // 侧键要读 mouseData 才知道是哪个 —— 所以这两个消息单独处理
                if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
                {
                    var xInfo = Marshal.PtrToStructure<MsLlHookStruct>(lParam);

                    // mouseData 高位是侧键编号（1 = XBUTTON1，2 = XBUTTON2）
                    var which = (xInfo.MouseData >> 16) & 0xFFFF;

                    vk = which switch
                    {
                        XButton1 => 0x05,
                        XButton2 => 0x06,
                        _ => null,
                    };
                }

                if (vk is int code)
                {
                    var isDown = msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN
                                      or WM_MBUTTONDOWN or WM_XBUTTONDOWN;

                    SetBit(code, isDown);

                    _lastEventTicks = DateTime.UtcNow.Ticks;
                    EventCount++;
                }
            }
        }
        catch
        {
            // 吞掉，理由同键盘回调
        }

        // ★ 放行。鼠标也一样：我们只看，不动。
        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    // ---- 位图读写 ----

    private void SetBit(int vk, bool down)
    {
        if (vk < 0 || vk > 0xFF)
        {
            return;
        }

        var word = vk >> 6;            // /64
        var bit = 1UL << (vk & 0x3F);  // %64

        if (down)
        {
            _downBits[word] |= bit;
        }
        else
        {
            _downBits[word] &= ~bit;
        }
    }

    private void ClearAll()
    {
        _downBits[0] = 0;
        _downBits[1] = 0;
        _downBits[2] = 0;
        _downBits[3] = 0;
    }

    public bool IsDown(int virtualKey)
    {
        if (!IsActive || virtualKey < 0 || virtualKey > 0xFF)
        {
            // 钩子没装上就退回轮询 —— 至少桌面场景还能用，不能整个功能哑掉
            return VirtualKeys.IsDownPolling(virtualKey);
        }

        // ⚠ 鼠标键和键盘键分开判"该信谁"：
        //   万一只有键盘钩子装上了、鼠标钩子没装上，鼠标键必须走轮询，
        //   否则那半边永远是"没按"（位图里没人写它）。
        //   桌面场景下轮询读鼠标本来就没问题，所以这个降级是安全的。
        if (IsMouseKey(virtualKey) && _mouseHookHandle == IntPtr.Zero)
        {
            return VirtualKeys.IsDownPolling(virtualKey);
        }

        if (!IsMouseKey(virtualKey) && _hookHandle == IntPtr.Zero)
        {
            return VirtualKeys.IsDownPolling(virtualKey);
        }

        var word = virtualKey >> 6;
        var bit = 1UL << (virtualKey & 0x3F);
        return (_downBits[word] & bit) != 0;
    }

    /// <summary>是不是鼠标键（虚拟键码 0x01~0x06）。</summary>
    private static bool IsMouseKey(int virtualKey) => virtualKey is >= 0x01 and <= 0x06;

    public void Dispose() => Uninstall();
}
