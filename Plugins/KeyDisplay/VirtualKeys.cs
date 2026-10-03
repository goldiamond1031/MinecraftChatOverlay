using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MinecraftChatOverlay.Plugins.KeyDisplay;

/// <summary>
/// 只读地查一个虚拟键当前有没有按下。
///
/// ⚠ 这里刻意用 <c>GetAsyncKeyState</c> 轮询，**不安装任何键盘钩子**（不用 WH_KEYBOARD_LL）。
///   原因：按键显示只需要「读」状态，不需要「拦」按键 —— 而低级键盘钩子是挂在系统输入链路上的，
///   每次按键都要过我们的回调，只为显示而付那个代价（输入延迟 / 被杀软盯上）不值得。
///   （项目里 AutoGg 用钩子是因为它要 <c>return 1</c> 吞掉按键做键盘锁定，那是「拦截」，轮询做不到。）
///
/// 这里只查**调用方指定的那几个键**的当前状态：不记录历史、不写任何文件、不知道用户打了什么字。
///
/// 已知特性：<c>GetAsyncKeyState</c> 返回的是**全局**按键状态，不区分来源进程 ——
/// 别的窗口里按键这里也会亮。对键位显示来说这通常正是想要的（切出去查东西时键位状态还在）。
/// </summary>
internal static class VirtualKeys
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>扫描范围的起点（0x00 不是合法键码）。</summary>
    public const int MinCode = 0x01;

    /// <summary>扫描范围的终点。</summary>
    public const int MaxCode = 0xFE;

    /// <summary>
    /// 当前是否按着（bit 15）。查询失败一律当"没按"，绝不抛异常影响刷新循环。
    ///
    /// ⚠ 数 CPS 也用它，**不要**改成读 bit 0（"自上次查询以来按过"）——
    /// 那一位是**全系统共享**的，任何进程调一次 GetAsyncKeyState 就把它清掉了，
    /// 而宿主自己也在查（状态栏的按键显示），所以我们读到的永远是 0。详见 <see cref="CpsSampler"/>。
    /// </summary>
    public static bool IsDown(int virtualKey)
    {
        if (virtualKey < MinCode || virtualKey > MaxCode)
        {
            return false;
        }

        try
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static readonly Dictionary<int, string> SpecialNames = new()
    {
        // 鼠标（PVP 里常用，和键盘键一样当"键"处理）
        [0x01] = "LMB",
        [0x02] = "RMB",
        [0x04] = "MMB",
        [0x05] = "MB4",
        [0x06] = "MB5",

        // 编辑与控制
        [0x08] = "Back",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x13] = "Pause",
        [0x14] = "Caps",
        [0x1B] = "Esc",
        [0x20] = "Space",
        [0x21] = "PgUp",
        [0x22] = "PgDn",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "←",
        [0x26] = "↑",
        [0x27] = "→",
        [0x28] = "↓",
        [0x2C] = "PrtSc",
        [0x2D] = "Ins",
        [0x2E] = "Del",
        [0x5D] = "Menu",

        // 修饰键
        [0x10] = "Shift",
        [0x11] = "Ctrl",
        [0x12] = "Alt",
        [0x5B] = "Win",
        [0x5C] = "RWin",
        [0xA0] = "LShift",
        [0xA1] = "RShift",
        [0xA2] = "LCtrl",
        [0xA3] = "RCtrl",
        [0xA4] = "LAlt",
        [0xA5] = "RAlt",

        // 锁定键
        [0x90] = "NumLock",
        [0x91] = "ScrLock",

        // 小键盘符号
        [0x6A] = "Num*",
        [0x6B] = "Num+",
        [0x6D] = "Num-",
        [0x6E] = "Num.",
        [0x6F] = "Num/",

        // 标点
        [0xBA] = ";",
        [0xBB] = "=",
        [0xBC] = ",",
        [0xBD] = "-",
        [0xBE] = ".",
        [0xBF] = "/",
        [0xC0] = "`",
        [0xDB] = "[",
        [0xDC] = "\\",
        [0xDD] = "]",
        [0xDE] = "'",
    };

    /// <summary>虚拟键码 → 人看的名字（W 键就是「W」）。</summary>
    public static string Name(int virtualKey)
    {
        if (SpecialNames.TryGetValue(virtualKey, out var special))
        {
            return special;
        }

        if (virtualKey >= 0x30 && virtualKey <= 0x39)
        {
            return ((char)virtualKey).ToString();          // 0-9
        }

        if (virtualKey >= 0x41 && virtualKey <= 0x5A)
        {
            return ((char)virtualKey).ToString();          // A-Z
        }

        if (virtualKey >= 0x60 && virtualKey <= 0x69)
        {
            return "Num" + (virtualKey - 0x60);            // 小键盘数字
        }

        if (virtualKey >= 0x70 && virtualKey <= 0x7B)
        {
            return "F" + (virtualKey - 0x6F);              // F1-F12
        }

        return "VK" + virtualKey.ToString("X2");
    }
}

/// <summary>
/// 捕捉"用户刚按下的那个键"，给配置页的「添加按键」用。
///
/// 实现同样是**全键盘轮询**（扫 0x01..0xFE 找按下沿），不是键盘钩子。
/// 只在页面上点了「添加按键」之后、由页面自己的定时器驱动，捕到一个立刻停。
/// </summary>
internal sealed class KeyCapture
{
    private readonly HashSet<int> _lastDown = new();
    private DateTime _readyAt = DateTime.MinValue;

    /// <summary>true = 正在等用户按键。</summary>
    public bool Armed { get; private set; }

    /// <summary>
    /// 开始捕获。<paramref name="ignoreFor"/> 是"忽略期"：
    /// 点「添加按键」按钮那一下的鼠标左键会被这段时间吃掉，不会误当成用户想添加的键。
    /// </summary>
    public void Start(TimeSpan ignoreFor)
    {
        Armed = true;
        _lastDown.Clear();
        _readyAt = DateTime.UtcNow + ignoreFor;
    }

    public void Stop() => Armed = false;

    /// <summary>扫一遍。有新按下的键就返回它的虚拟键码，否则 null。</summary>
    public int? Scan()
    {
        if (!Armed)
        {
            return null;
        }

        var now = new HashSet<int>();
        for (var vk = VirtualKeys.MinCode; vk <= VirtualKeys.MaxCode; vk++)
        {
            if (VirtualKeys.IsDown(vk))
            {
                now.Add(vk);
            }
        }

        // 忽略期里只维护基线，不报任何键
        if (DateTime.UtcNow < _readyAt)
        {
            _lastDown.Clear();
            foreach (var key in now)
            {
                _lastDown.Add(key);
            }

            return null;
        }

        int? found = null;
        foreach (var key in now)
        {
            if (!_lastDown.Contains(key))
            {
                found = key;
                break;
            }
        }

        _lastDown.Clear();
        foreach (var key in now)
        {
            _lastDown.Add(key);
        }

        if (found is not null)
        {
            Armed = false;
        }

        return found;
    }
}
