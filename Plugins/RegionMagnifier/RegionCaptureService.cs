using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace MinecraftChatOverlay.Plugins.RegionMagnifier;

/// <summary>
/// 原生调用集中在这一层。
///
/// 坑 44（务必记住）：抓硬件加速窗口（OpenGL / D3D，比如 Minecraft）必须从
/// 屏幕 DC（GetDC(IntPtr.Zero)）抓，并且带上 CAPTUREBLT —— 用窗口 DC 抓通常全黑。
/// </summary>
internal static class RegionNative
{
    public const int GwlExStyle = -20;

    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;

    /// <summary>不排除抓屏。</summary>
    public const uint WdaNone = 0x00000000;

    /// <summary>把自己排除在抓屏之外（否则放大窗会拍到自己，无限递归）。</summary>
    public const uint WdaExcludeFromCapture = 0x00000011;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;

    public const uint SrCopy = 0x00CC0020;
    public const uint CaptureBlt = 0x40000000;

    public const uint DibRgbColors = 0;

    public static readonly IntPtr HwndTopmost = new(-1);

    public static readonly IntPtr HwndNoTopmost = new(-2);

    private const uint GwOwner = 4;

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSource, int xSource, int ySource, uint rasterOp);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hDc, IntPtr hBitmap, uint startScan, uint scanLines, byte[] bits, ref BitmapInfoHeader header, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    /// <summary>窗口标题（读不到就空串）。</summary>
    public static string GetTitle(IntPtr hWnd)
    {
        try
        {
            var length = GetWindowTextLength(hWnd);
            if (length <= 0)
            {
                return "";
            }

            var buffer = new StringBuilder(length + 2);
            GetWindowText(hWnd, buffer, buffer.Capacity);
            return buffer.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>读扩展样式。</summary>
    public static int GetExStyle(IntPtr hWnd) => GetWindowLong(hWnd, GwlExStyle);

    /// <summary>加/去一个扩展样式位。</summary>
    public static void UpdateExStyle(IntPtr hWnd, int flag, bool on)
    {
        try
        {
            var current = GetWindowLong(hWnd, GwlExStyle);
            var next = on ? current | flag : current & ~flag;
            if (next != current)
            {
                SetWindowLong(hWnd, GwlExStyle, next);
            }
        }
        catch
        {
            // 改样式失败不该让窗口崩掉
        }
    }

    /// <summary>把窗口拎到最顶层（不抢焦点）。</summary>
    public static void RaiseTopmost(IntPtr hWnd)
    {
        try
        {
            SetWindowPos(hWnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>有 owner 的窗口（弹窗之类）不算独立窗口。</summary>
    public static bool HasOwner(IntPtr hWnd) => GetWindow(hWnd, GwOwner) != IntPtr.Zero;
}

/// <summary>一个候选游戏窗口（下拉框里显示 ToString）。</summary>
public sealed class RegionTargetWindow
{
    public IntPtr Handle { get; init; }

    public int ProcessId { get; init; }

    public string ProcessName { get; init; } = "";

    public string Title { get; init; } = "";

    public int Width { get; init; }

    public int Height { get; init; }

    public override string ToString() => $"{ProcessName}  (PID {ProcessId})  {Title}";
}

/// <summary>找游戏窗口 / 换算坐标。</summary>
public static class RegionWindowFinder
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    private const int MinimumWidth = 160;
    private const int MinimumHeight = 120;

    /// <summary>列出候选窗口：可见、有标题、不是工具窗、不是本程序自己；同进程只留最大的那个。</summary>
    public static List<RegionTargetWindow> FindTargets()
    {
        var best = new Dictionary<int, RegionTargetWindow>();
        var self = Environment.ProcessId;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!RegionNative.IsWindowVisible(hWnd))
                    {
                        return true;
                    }

                    if (RegionNative.HasOwner(hWnd))
                    {
                        return true;
                    }

                    if ((RegionNative.GetExStyle(hWnd) & RegionNative.WsExToolWindow) != 0)
                    {
                        return true;
                    }

                    if (!RegionNative.GetWindowRect(hWnd, out var rect))
                    {
                        return true;
                    }

                    if (rect.Width < MinimumWidth || rect.Height < MinimumHeight)
                    {
                        return true;
                    }

                    var title = RegionNative.GetTitle(hWnd);
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        return true;
                    }

                    RegionNative.GetWindowThreadProcessId(hWnd, out var pid);
                    if (pid == self)
                    {
                        return true;
                    }

                    string processName;
                    try
                    {
                        processName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                    }
                    catch
                    {
                        processName = "?";
                    }

                    var info = new RegionTargetWindow
                    {
                        Handle = hWnd,
                        ProcessId = (int)pid,
                        ProcessName = processName,
                        Title = title,
                        Width = rect.Width,
                        Height = rect.Height,
                    };

                    if (best.TryGetValue(info.ProcessId, out var existing))
                    {
                        if ((long)info.Width * info.Height > (long)existing.Width * existing.Height)
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
            // 枚举失败就当没有
        }

        var list = best.Values.ToList();
        list.Sort((a, b) =>
        {
            var rankA = Rank(a.ProcessName);
            var rankB = Rank(b.ProcessName);
            return rankA != rankB ? rankA.CompareTo(rankB) : string.CompareOrdinal(a.ProcessName, b.ProcessName);
        });
        return list;
    }

    /// <summary>
    /// 按"进程名 + 标题关键字"重新找到那个窗口（游戏重启换 PID 也能找回）。
    /// titleHint 为空就只按进程名挑；都找不到返回 IntPtr.Zero。
    /// </summary>
    public static IntPtr ResolveTarget(string processName, string titleHint, out string title)
    {
        title = "";

        var targets = FindTargets();
        if (targets.Count == 0)
        {
            return IntPtr.Zero;
        }

        RegionTargetWindow? match = null;

        if (!string.IsNullOrWhiteSpace(titleHint))
        {
            match = targets.FirstOrDefault(t => MatchesProcess(t, processName) && t.Title.Contains(titleHint.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (match is null)
        {
            match = targets.FirstOrDefault(t => MatchesProcess(t, processName));
        }

        if (match is null && !string.IsNullOrWhiteSpace(titleHint))
        {
            match = targets.FirstOrDefault(t => t.Title.Contains(titleHint.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (match is null)
        {
            return IntPtr.Zero;
        }

        title = match.Title;
        return match.Handle;
    }

    private static bool MatchesProcess(RegionTargetWindow target, string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return true;
        }

        var want = processName.Trim();
        return target.ProcessName.Equals(want, StringComparison.OrdinalIgnoreCase)
            || target.ProcessName.Contains(want, StringComparison.OrdinalIgnoreCase)
            || want.Contains(target.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 游戏客户区在屏幕上的位置（物理像素）。窗口最小化 / 拿不到就返回 false。
    /// 注意：进程没声明 DPI 感知时这里拿到的是被系统虚拟化过的坐标（另一个坑）。
    /// </summary>
    public static bool TryGetClientRectOnScreen(IntPtr hWnd, out int x, out int y, out int width, out int height)
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;

        try
        {
            if (hWnd == IntPtr.Zero || !RegionNative.IsWindow(hWnd) || !RegionNative.IsWindowVisible(hWnd))
            {
                return false;
            }

            if (!RegionNative.GetClientRect(hWnd, out var client))
            {
                return false;
            }

            if (client.Width <= 0 || client.Height <= 0)
            {
                return false;
            }

            var origin = new RegionNative.Point { X = 0, Y = 0 };
            if (!RegionNative.ClientToScreen(hWnd, ref origin))
            {
                return false;
            }

            if (origin.X < -30000 || origin.Y < -30000)
            {
                return false;
            }

            x = origin.X;
            y = origin.Y;
            width = client.Width;
            height = client.Height;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>整个窗口（含边框标题栏）在屏幕上的位置（物理像素）。</summary>
    public static bool TryGetWindowRectOnScreen(IntPtr hWnd, out int x, out int y, out int width, out int height)
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;

        try
        {
            if (hWnd == IntPtr.Zero || !RegionNative.IsWindow(hWnd))
            {
                return false;
            }

            if (!RegionNative.GetWindowRect(hWnd, out var rect))
            {
                return false;
            }

            if (rect.Width <= 0 || rect.Height <= 0 || rect.Left < -30000 || rect.Top < -30000)
            {
                return false;
            }

            x = rect.Left;
            y = rect.Top;
            width = rect.Width;
            height = rect.Height;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>javaw / minecraft 类排前面，方便用户一眼看到游戏。</summary>
    private static int Rank(string processName)
    {
        var name = processName.ToLowerInvariant();
        if (name.Contains("javaw") || name.Contains("minecraft"))
        {
            return 0;
        }

        return name.Contains("java") ? 1 : 2;
    }
}

/// <summary>
/// 抓屏器：从屏幕 DC + CAPTUREBLT 抓一块区域，转成托管字节数组喂给 WriteableBitmap。
///
/// 坑 44：必须屏幕 DC（窗口 DC 抓硬件加速画面会全黑）；判"是不是黑屏"只能看
/// 非黑像素比例，不能看颜色种数（纯色区域只有 1 种颜色，会被误判成黑屏）。
/// </summary>
public sealed class RegionFrameGrabber : IDisposable
{
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _previousBitmap;
    private int _width;
    private int _height;
    private byte[] _buffer = Array.Empty<byte>();

    /// <summary>上一帧非黑像素占比（0~1）。</summary>
    public double LastNonBlackRatio { get; private set; }

    /// <summary>上一帧里出现过的颜色种数（只用于日志/诊断）。</summary>
    public int LastColorCount { get; private set; }

    /// <summary>画面看起来有内容（不是全黑）。</summary>
    public bool HasContent => LastNonBlackRatio > 0.02;

    public int Width => _width;

    public int Height => _height;

    /// <summary>尺寸变了就重建内存 DC / 位图。</summary>
    public bool EnsureSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (_memoryDc != IntPtr.Zero && _bitmap != IntPtr.Zero && width == _width && height == _height)
        {
            return true;
        }

        Release();

        var screenDc = RegionNative.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            _memoryDc = RegionNative.CreateCompatibleDC(screenDc);
            _bitmap = RegionNative.CreateCompatibleBitmap(screenDc, width, height);
            if (_memoryDc == IntPtr.Zero || _bitmap == IntPtr.Zero)
            {
                Release();
                return false;
            }

            _previousBitmap = RegionNative.SelectObject(_memoryDc, _bitmap);
            _width = width;
            _height = height;
            _buffer = new byte[width * height * 4];
            return true;
        }
        catch
        {
            Release();
            return false;
        }
        finally
        {
            RegionNative.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>抓屏幕上 (x, y) 起 width×height 的一块（物理像素）。</summary>
    public bool Capture(int x, int y, int width, int height)
    {
        if (!EnsureSize(width, height))
        {
            return false;
        }

        var screenDc = RegionNative.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var ok = RegionNative.BitBlt(
                _memoryDc, 0, 0, width, height,
                screenDc, x, y,
                RegionNative.SrCopy | RegionNative.CaptureBlt);

            if (!ok)
            {
                return false;
            }

            ReadPixels();
            UpdateStats();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            RegionNative.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>把上一帧写进 WriteableBitmap（Bgr32）。</summary>
    public bool CopyTo(WriteableBitmap bitmap)
    {
        if (bitmap is null || _width <= 0 || _height <= 0 || _buffer.Length == 0)
        {
            return false;
        }

        try
        {
            bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, _width, _height), _buffer, _width * 4, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ReadPixels()
    {
        var header = new RegionNative.BitmapInfoHeader
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<RegionNative.BitmapInfoHeader>(),
            Width = _width,
            Height = -_height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        RegionNative.GetDIBits(_memoryDc, _bitmap, 0, (uint)_height, _buffer, ref header, RegionNative.DibRgbColors);
    }

    /// <summary>抽样统计，别每帧遍历几十万像素。</summary>
    private void UpdateStats()
    {
        try
        {
            var total = _width * _height;
            if (total <= 0 || _buffer.Length < total * 4)
            {
                LastNonBlackRatio = 0;
                LastColorCount = 0;
                return;
            }

            var step = Math.Max(1, total / 4096);
            var sampled = 0;
            var nonBlack = 0;
            var colors = new HashSet<int>();

            for (var pixel = 0; pixel < total; pixel += step)
            {
                var offset = pixel * 4;
                var b = _buffer[offset];
                var g = _buffer[offset + 1];
                var r = _buffer[offset + 2];
                sampled++;

                if (b > 8 || g > 8 || r > 8)
                {
                    nonBlack++;
                }

                if (colors.Count < 512)
                {
                    colors.Add((r << 16) | (g << 8) | b);
                }
            }

            LastNonBlackRatio = sampled > 0 ? (double)nonBlack / sampled : 0;
            LastColorCount = colors.Count;
        }
        catch
        {
            LastNonBlackRatio = 0;
            LastColorCount = 0;
        }
    }

    public void Dispose() => Release();

    private void Release()
    {
        try
        {
            if (_memoryDc != IntPtr.Zero && _previousBitmap != IntPtr.Zero)
            {
                RegionNative.SelectObject(_memoryDc, _previousBitmap);
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (_bitmap != IntPtr.Zero)
            {
                RegionNative.DeleteObject(_bitmap);
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (_memoryDc != IntPtr.Zero)
            {
                RegionNative.DeleteDC(_memoryDc);
            }
        }
        catch
        {
            // 忽略
        }

        _bitmap = IntPtr.Zero;
        _memoryDc = IntPtr.Zero;
        _previousBitmap = IntPtr.Zero;
        _width = 0;
        _height = 0;
        _buffer = Array.Empty<byte>();
    }
}
