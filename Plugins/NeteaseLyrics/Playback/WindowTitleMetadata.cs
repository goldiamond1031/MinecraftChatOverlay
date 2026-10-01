using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MinecraftChatOverlay.Plugins.NeteaseLyrics.Playback;

/// <summary>
/// 从网易云客户端的窗口标题拿「歌名 - 歌手」。
///
/// 这是实测可用的兜底：客户端主窗口（OrpheusBrowserHost / 迷你播放器 / 任务栏缩略图）的标题
/// 就是当前播放的歌名和歌手。中继插件只管进度，歌名歌手用这条更省事。
/// </summary>
public static class WindowTitleMetadata
{
    private const string ProcessName = "cloudmusic";

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>返回 (歌名, 歌手)；拿不到返回 (null, null)。</summary>
    public static (string? Title, string? Artist) TryRead()
    {
        try
        {
            var ids = new HashSet<uint>();
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                ids.Add((uint)process.Id);
                process.Dispose();
            }

            if (ids.Count == 0)
            {
                return (null, null);
            }

            string? best = null;
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var pid);
                if (!ids.Contains(pid))
                {
                    return true;
                }

                var buffer = new StringBuilder(512);
                if (GetWindowText(hWnd, buffer, buffer.Capacity) <= 0)
                {
                    return true;
                }

                var text = buffer.ToString().Trim();

                // 只认「歌名 - 歌手」这种形状；可见的那个优先
                if (text.Contains(" - ", StringComparison.Ordinal) && !text.StartsWith("MediaPlayer", StringComparison.OrdinalIgnoreCase))
                {
                    if (best is null || (IsWindowVisible(hWnd) && !text.Equals(best, StringComparison.Ordinal)))
                    {
                        best = text;
                    }
                }

                return true;
            }, IntPtr.Zero);

            if (string.IsNullOrWhiteSpace(best))
            {
                return (null, null);
            }

            return Split(best);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>把「歌名 - 歌手」拆开（在第一个 " - " 处切，歌手里的 " / " 保留）。</summary>
    public static (string? Title, string? Artist) Split(string windowTitle)
    {
        var index = windowTitle.IndexOf(" - ", StringComparison.Ordinal);
        if (index <= 0)
        {
            return (windowTitle.Trim(), null);
        }

        var title = windowTitle[..index].Trim();
        var artist = windowTitle[(index + 3)..].Trim();
        return (string.IsNullOrWhiteSpace(title) ? null : title, string.IsNullOrWhiteSpace(artist) ? null : artist);
    }
}
