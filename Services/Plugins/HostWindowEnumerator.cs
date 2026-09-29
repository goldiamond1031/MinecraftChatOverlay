using System.Runtime.InteropServices;
using System.Text;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Services.Plugins;

/// <summary>
/// 给插件用的"当前有哪些游戏窗口"。
/// 过滤规则和「区域放大 / 窗口全屏」里面那套一致：可见、有标题、不是工具窗、不是本程序自己。
/// </summary>
internal static class HostWindowEnumerator
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    /// <summary>返回候选窗口，javaw/java/minecraft 类排在最前面。</summary>
    public static List<GameWindowInfo> FindGameWindows()
    {
        var best = new Dictionary<int, GameWindowInfo>();
        var self = Environment.ProcessId;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    if (GetWindow(hWnd, GwOwner) != IntPtr.Zero) return true;
                    if (GetWindowTextLengthW(hWnd) <= 0) return true;
                    if ((GetWindowLongW(hWnd, GwlExStyle) & WsExToolWindow) != 0) return true;
                    if (!GetWindowRect(hWnd, out var rect)) return true;

                    var width = rect.Right - rect.Left;
                    var height = rect.Bottom - rect.Top;
                    if (width < 160 || height < 120) return true;

                    GetWindowThreadProcessId(hWnd, out var pid);
                    if (pid == self) return true;

                    var title = new StringBuilder(512);
                    GetWindowTextW(hWnd, title, title.Capacity);

                    string processName;
                    try
                    {
                        processName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                    }
                    catch
                    {
                        processName = "?";
                    }

                    var info = new GameWindowInfo(hWnd, (int)pid, processName, title.ToString(), width, height);

                    // 同一个进程只留面积最大的那个窗口
                    if (best.TryGetValue(info.ProcessId, out var existing))
                    {
                        if ((long)width * height > (long)existing.Width * existing.Height)
                        {
                            best[info.ProcessId] = info;
                        }
                    }
                    else
                    {
                        best[info.ProcessId] = info;
                    }
                }
                catch
                {
                    // 单个窗口出事不影响整体
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败就返回空列表
        }

        var result = best.Values.ToList();
        result.Sort((a, b) =>
        {
            var rankA = Rank(a.ProcessName);
            var rankB = Rank(b.ProcessName);
            return rankA != rankB ? rankA.CompareTo(rankB) : string.CompareOrdinal(a.ProcessName, b.ProcessName);
        });
        return result;
    }

    private static int Rank(string processName)
    {
        var name = processName.ToLowerInvariant();
        if (name.Contains("javaw") || name.Contains("minecraft")) return 0;
        if (name.Contains("java")) return 1;
        return 2;
    }
}