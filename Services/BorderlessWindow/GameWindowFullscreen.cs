using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MinecraftChatOverlay.Services.BorderlessWindow;

/// <summary>一个候选窗口（下拉框直接显示 <see cref="ToString"/>）。</summary>
public sealed class GameWindowCandidate
{
    public IntPtr Handle { get; init; }

    public int Pid { get; init; }

    public string ProcessName { get; init; } = "";

    public string Title { get; init; } = "";

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>看起来已经铺满整块屏幕而且没有边框（游戏自己的全屏，或已经改过一次）。</summary>
    public bool LooksFullscreen { get; init; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(ProcessName).Append("  (PID ").Append(Pid).Append(')');

        if (!string.IsNullOrWhiteSpace(Title))
        {
            sb.Append("  ").Append(Title);
        }

        if (Width > 0 && Height > 0)
        {
            sb.Append("  [").Append(Width).Append('×').Append(Height).Append(']');
        }

        if (LooksFullscreen)
        {
            sb.Append("  ← 看起来已经是全屏");
        }

        return sb.ToString();
    }
}

/// <summary>
/// 把游戏窗口变成「无边框全屏」（borderless windowed fullscreen）。
///
/// 为什么要这么做：游戏用自己的全屏（独占全屏 / F11 全屏）时，画面不走桌面合成，
/// 别的窗口再怎么写 Topmost 也盖不上去，悬浮窗自然就"消失"了。
/// 把窗口样式里的标题栏、边框、最大化按钮去掉，再把它铺满显示器并压回普通层
/// （顺手摘掉 WS_EX_TOPMOST），它就是一块普通的、走桌面合成的无边框窗口，
/// 悬浮窗（HWND_TOPMOST）就能正常盖在上面。
///
/// 只动窗口样式和位置，不碰游戏进程内存、不注入、不改画面，随时可以还原。
/// 进程退出后这些改动也跟着消失。
///
/// 全部是静态方法：同一时刻只接管一个窗口（够用，也免得用户改了一堆窗口最后忘了还原）。
/// </summary>
public static class GameWindowFullscreen
{
    /// <summary>当前是否已经改过某个窗口。</summary>
    public static bool IsApplied { get; private set; }

    /// <summary>被改的窗口句柄（未接管时为 <see cref="IntPtr.Zero"/>）。</summary>
    public static IntPtr AppliedHandle { get; private set; }

    /// <summary>被改窗口的进程名，给状态文字用。</summary>
    public static string AppliedProcessName { get; private set; } = "";

    /// <summary>被改窗口的标题，给状态文字用。</summary>
    public static string AppliedTitle { get; private set; } = "";

    /// <summary>被改的窗口是不是还活着（游戏关掉后句柄就失效了）。</summary>
    public static bool IsAppliedWindowAlive => IsApplied && _saved is { } s && IsWindow(s.Hwnd);

    // ===================== 状态 =====================

    /// <summary>接管之前的窗口原样，还原全靠它。</summary>
    private struct SavedWindow
    {
        public IntPtr Hwnd;
        public long Style;
        public long ExStyle;
        public bool WasTopmost;
        public WINDOWPLACEMENT Placement;
        public string ProcessName;
        public string Title;
    }

    private static SavedWindow? _saved;

    /// <summary>游戏自己关掉了，或者窗口已经没了：把状态清掉，让界面回到"没改过"。</summary>
    public static void ForgetAppliedState()
    {
        IsApplied = false;
        AppliedHandle = IntPtr.Zero;
        AppliedProcessName = "";
        AppliedTitle = "";
        _saved = null;
    }

    // ===================== 枚举候选窗口 =====================

    /// <summary>
    /// 列出所有"像个正常程序主窗口"的顶层窗口。
    /// 过滤掉不可见、由别的窗口拥有（对话框）、没有标题、工具窗口，以及本软件自己。
    /// 同一个进程只留最大的那个窗口（游戏一般就一个主窗口）。
    /// </summary>
    public static List<GameWindowCandidate> FindCandidates()
    {
        var found = new List<GameWindowCandidate>();
        var selfPid = Process.GetCurrentProcess().Id;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd))
                    {
                        return true;
                    }

                    if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                    {
                        return true;
                    }

                    if (GetWindowTextLengthW(hwnd) <= 0)
                    {
                        return true;
                    }

                    var exStyle = GetWindowStyle(hwnd, GwlExStyle);
                    if ((exStyle & WsExToolWindow) != 0)
                    {
                        return true;
                    }

                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == 0 || pid == (uint)selfPid)
                    {
                        return true;
                    }

                    if (!GetWindowRect(hwnd, out var rect))
                    {
                        return true;
                    }

                    var width = rect.Right - rect.Left;
                    var height = rect.Bottom - rect.Top;
                    if (width <= 0 || height <= 0)
                    {
                        return true;
                    }

                    var name = GetProcessName((int)pid);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        return true;
                    }

                    found.Add(new GameWindowCandidate
                    {
                        Handle = hwnd,
                        Pid = (int)pid,
                        ProcessName = name,
                        Title = GetWindowTitle(hwnd),
                        Width = width,
                        Height = height,
                        LooksFullscreen = LooksLikeFullscreen(hwnd)
                    });
                }
                catch
                {
                    // 枚举过程中某个窗口查不动就跳过，别让整张列表挂掉
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举整体失败就返回已经拿到的部分
        }

        return found
            .GroupBy(c => c.Pid)
            .Select(g => g.OrderByDescending(c => (long)c.Width * c.Height).First())
            .OrderByDescending(c => IsGameProcessName(c.ProcessName))
            .ThenBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsGameProcessName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Equals("javaw", StringComparison.OrdinalIgnoreCase)
               || name.Equals("java", StringComparison.OrdinalIgnoreCase)
               || name.Contains("java", StringComparison.OrdinalIgnoreCase)
               || name.Contains("minecraft", StringComparison.OrdinalIgnoreCase)
               || name.Contains("lwjgl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>窗口是否已经铺满整块显示器且没有边框（游戏自己的全屏，或者刚被我们改过）。</summary>
    public static bool LooksLikeFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var rect) || !TryGetMonitorRect(hwnd, true, out var monitorRect))
        {
            return false;
        }

        var coversMonitor = rect.Left <= monitorRect.Left && rect.Top <= monitorRect.Top
                            && rect.Right >= monitorRect.Right && rect.Bottom >= monitorRect.Bottom;

        var style = GetWindowStyle(hwnd, GwlStyle);
        var noFrame = (style & WsCaption) == 0 && (style & WsThickFrame) == 0;

        return coversMonitor && noFrame;
    }

    /// <summary>窗口是否还在（游戏没关）。</summary>
    public static bool IsWindowAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    // ===================== 变成无边框全屏 =====================

    public static bool TryApply(IntPtr hwnd, bool coverTaskbar, out string message)
    {
        message = "";

        if (!IsWindowAlive(hwnd))
        {
            message = "找不到这个窗口了（游戏可能刚关掉或重开过），点【刷新列表】重新选一个。";
            return false;
        }

        var sameWindow = IsApplied && _saved is { } currentState && currentState.Hwnd == hwnd;

        // 换了目标窗口：先把上一个还原，别让旧窗口一直保持改过的样子
        if (IsApplied && !sameWindow)
        {
            TryRestore(out _);
        }

        long originalStyle;
        long originalExStyle;
        bool wasTopmost;
        WINDOWPLACEMENT placement;
        string processName;
        string title;

        if (sameWindow)
        {
            // 同一个窗口再点一次（比如改了"铺满整个屏幕"）：沿用最早存下的原样，
            // 绝不能把"已经改过的样子"当成原样记下来，否则就永远还原不回去了。
            var saved = _saved!.Value;
            originalStyle = saved.Style;
            originalExStyle = saved.ExStyle;
            wasTopmost = saved.WasTopmost;
            placement = saved.Placement;
            processName = saved.ProcessName;
            title = saved.Title;
        }
        else
        {
            originalStyle = GetWindowStyle(hwnd, GwlStyle);
            originalExStyle = GetWindowStyle(hwnd, GwlExStyle);
            wasTopmost = (originalExStyle & WsExTopmost) != 0;

            // 位置和最大化/最小化状态用 WINDOWPLACEMENT 一起记，还原时最省事
            placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref placement))
            {
                if (!GetWindowRect(hwnd, out var now))
                {
                    message = "拿不到窗口位置，改不了。";
                    return false;
                }

                placement.rcNormalPosition = now;
                placement.showCmd = SwShownormal;
            }

            processName = GetProcessNameOfWindow(hwnd);
            title = GetWindowTitle(hwnd);
        }

        if (!TryGetMonitorRect(hwnd, coverTaskbar, out var target))
        {
            message = "拿不到显示器信息，改不了。";
            return false;
        }

        var width = target.Right - target.Left;
        var height = target.Bottom - target.Top;

        // 去边框：标题栏、可调边框、系统菜单、最大/最小化按钮全都摘掉
        var newStyle = originalStyle & ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu | WsBorder | WsDlgFrame);
        // 3D 边框（窗口边缘那道凸起）也摘掉，看着才是真的无边框；
        // 顺便摘掉 WS_EX_TOPMOST —— 游戏全屏时常常自己就是置顶窗口，不摘掉悬浮窗永远在它下面。
        var newExStyle = originalExStyle & ~(WsExWindowEdge | WsExClientEdge | WsExStaticEdge | WsExDlgModalFrame | WsExTopmost);

        _saved = new SavedWindow
        {
            Hwnd = hwnd,
            Style = originalStyle,
            ExStyle = originalExStyle,
            WasTopmost = wasTopmost,
            Placement = placement,
            ProcessName = processName,
            Title = title
        };
        IsApplied = true;
        AppliedHandle = hwnd;
        AppliedProcessName = processName;
        AppliedTitle = title;

        // 已经正好是目标状态就什么都不做：免得每点一次都闪一下
        if (IsExactlyTargetState(hwnd, target, newStyle, newExStyle))
        {
            message = "这个窗口已经符合当前设置（" + width + "×" + height +
                      (coverTaskbar ? "，盖住任务栏" : "，保留任务栏") + "），没有改动。";
            return true;
        }

        // 最小化 / 最大化状态下 SetWindowPos 会被系统忽略（或改动被吃掉），先恢复成普通状态
        if (IsIconic(hwnd) || IsZoomed(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        SetWindowStyle(hwnd, GwlExStyle, newExStyle);
        SetWindowStyle(hwnd, GwlStyle, newStyle);

        SetWindowPos(hwnd, HwndNotTopmost,
                     target.Left, target.Top, width, height,
                     SwpFrameChanged | SwpShowWindow | SwpNoOwnerZOrder);

        // 校验：有些启动器/整合包会自己把窗口尺寸改回去，这时候要如实告诉用户，别让他干等
        if (IsExactlyTargetState(hwnd, target, newStyle, newExStyle))
        {
            message = "已生效：" + processName + " 现在是 " + width + "×" + height +
                      " 的无边框全屏，悬浮窗应该能盖在游戏上了。";
        }
        else
        {
            GetWindowRect(hwnd, out var actual);
            message = "窗口样式已经改好，但游戏把大小改回去了（期望 " + width + "×" + height +
                      "，实际 " + (actual.Right - actual.Left) + "×" + (actual.Bottom - actual.Top) + "）。" +
                      "先在游戏里按 F11 切回窗口化，再点一次这个按钮。";
        }

        return true;
    }

    /// <summary>窗口当前是不是已经正好是我们要的样子（位置、大小、样式都对）。</summary>
    private static bool IsExactlyTargetState(IntPtr hwnd, RECT target, long style, long exStyle)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var rectOk = Math.Abs(rect.Left - target.Left) <= 2
                     && Math.Abs(rect.Top - target.Top) <= 2
                     && Math.Abs((rect.Right - rect.Left) - (target.Right - target.Left)) <= 2
                     && Math.Abs((rect.Bottom - rect.Top) - (target.Bottom - target.Top)) <= 2;

        return rectOk
               && GetWindowStyle(hwnd, GwlStyle) == style
               && GetWindowStyle(hwnd, GwlExStyle) == exStyle;
    }

    // ===================== 还原 =====================

    public static bool TryRestore(out string message)
    {
        message = "";

        if (!IsApplied || _saved is not { } saved)
        {
            message = "现在没有改过任何窗口，不需要还原。";
            return false;
        }

        // 先清状态：还原过程中就算出异常，也不会留下"以为还改着"的僵状态
        ForgetAppliedState();

        if (!IsWindow(saved.Hwnd))
        {
            message = "游戏窗口已经关掉了，不用还原（重新启动游戏就是正常窗口）。";
            return true;
        }

        SetWindowStyle(saved.Hwnd, GwlStyle, saved.Style);
        SetWindowStyle(saved.Hwnd, GwlExStyle, saved.ExStyle);

        // WINDOWPLACEMENT 里带着原来的位置、大小和"当时是最大化/最小化还是普通"，一次还原
        var placement = saved.Placement;
        placement.length = Marshal.SizeOf<WINDOWPLACEMENT>();
        if (!SetWindowPlacement(saved.Hwnd, ref placement))
        {
            var r = placement.rcNormalPosition;
            SetWindowPos(saved.Hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                         SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        // 边框样式改完要让系统重算窗口框架，顺便把原来的置顶状态还回去
        SetWindowPos(saved.Hwnd, saved.WasTopmost ? HwndTopmost : HwndNotTopmost,
                     0, 0, 0, 0,
                     SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);

        message = "已把 " + saved.ProcessName + " 还原成原来的窗口。";
        return true;
    }

    // ===================== 小工具 =====================

    private static bool TryGetMonitorRect(IntPtr hwnd, bool coverTaskbar, out RECT rect)
    {
        rect = default;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        // rcMonitor = 整块显示器（含任务栏区域）；rcWork = 工作区（不含任务栏）
        rect = coverTaskbar ? info.rcMonitor : info.rcWork;
        return rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static string GetProcessNameOfWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == 0 ? "" : GetProcessName((int)pid);
        }
        catch
        {
            return "";
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        try
        {
            var length = GetWindowTextLengthW(hwnd);
            if (length <= 0)
            {
                return "";
            }

            var sb = new StringBuilder(length + 1);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    // 32 位 / 64 位都要能用：64 位走 GetWindowLongPtr，32 位只有 GetWindowLong
    private static long GetWindowStyle(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtrW(hwnd, index).ToInt64()
            : GetWindowLongW(hwnd, index);
    }

    private static void SetWindowStyle(IntPtr hwnd, int index, long value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtrW(hwnd, index, new IntPtr(value));
        }
        else
        {
            SetWindowLongW(hwnd, index, unchecked((int)value));
        }
    }

    // ===================== Win32 =====================

    private const uint GwOwner = 4;

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    private const long WsBorder = 0x00800000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsDlgFrame = 0x00400000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;

    private const long WsExDlgModalFrame = 0x00000001L;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExWindowEdge = 0x00000100L;
    private const long WsExClientEdge = 0x00000200L;
    private const long WsExStaticEdge = 0x00020000L;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;

    private const int SwShownormal = 1;
    private const int SwRestore = 9;

    private const uint MonitorDefaultToNearest = 2;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
}