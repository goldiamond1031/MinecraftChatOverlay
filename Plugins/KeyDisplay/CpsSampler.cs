using System;
using System.Collections.Generic;
using System.Threading;

namespace MinecraftChatOverlay.Plugins.KeyDisplay;

/// <summary>
/// CPS 采样器：用一条**独立的后台线程**高频轮询鼠标左/右键。
///
/// ⚠ 为什么不能用 UI 线程的 DispatcherTimer 来数 CPS：
///   它的实际最小间隔受消息循环限制（通常 ≥10ms，界面忙的时候更久），而且悬浮窗刷新也挤在
///   同一个 Tick 里 —— 刷新一慢采样就跟着粗。数 CPS 靠的是"能不能在按下持续的那几十毫秒里
///   抓到状态"，采样一粗就漏，表现是"点得快了反而数得少"。
///
/// ⚠ 为什么不能用 GetAsyncKeyState 的 bit 0（"自上次查询以来按过"）：
///   那一位是**全系统共享**的 —— 任何进程调用 GetAsyncKeyState 都会把它清掉，
///   而宿主自己也在查（状态栏的按键显示）。所以我们读到的基本永远是 0，压根数不到。
///   bit 15（此刻是否按着）虽然得自己比较前后帧，但它不会被别人偷走。
///
/// 仍然是**只读**：只查状态，不装钩子、不拦截、不记录、不落盘。
/// 而且只有真的有格子要显示 CPS 时才启动这条线程，没人看就整个不跑。
/// </summary>
internal sealed class CpsSampler : IDisposable
{
    /// <summary>采样间隔（毫秒）。5 毫秒足够覆盖到 200 CPS，人手极限也就 15 左右。</summary>
    private const int SampleIntervalMs = 5;

    /// <summary>统计窗口：最近 1 秒内的点击数就是 CPS。</summary>
    private const int WindowMs = 1000;

    private readonly object _gate = new();
    private readonly List<DateTime> _left = new();
    private readonly List<DateTime> _right = new();

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _wantLeft;
    private volatile bool _wantRight;

    // 只被采样线程读写，不用锁
    private bool _leftWasDown;
    private bool _rightWasDown;

    public int LeftCps
    {
        get { lock (_gate) { return _left.Count; } }
    }

    public int RightCps
    {
        get { lock (_gate) { return _right.Count; } }
    }

    /// <summary>自检用：采样线程现在活着没有。</summary>
    internal bool IsRunning => _thread is not null && _running;

    /// <summary>告诉采样器现在要数哪几个键。两个都不需要就把线程停掉，一点开销都不留。</summary>
    public void SetWanted(bool left, bool right)
    {
        _wantLeft = left;
        _wantRight = right;

        if (left || right)
        {
            StartIfNeeded();
        }
        else
        {
            StopIfIdle();
        }
    }

    private void StartIfNeeded()
    {
        if (_thread is not null)
        {
            return;
        }

        _running = true;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "KeyDisplay-CPS",
        };

        _thread.Start();
    }

    private void StopIfIdle()
    {
        if (_thread is null)
        {
            return;
        }

        _running = false;

        try
        {
            _thread.Join(200);
        }
        catch
        {
        }

        _thread = null;

        lock (_gate)
        {
            _left.Clear();
            _right.Clear();
        }
    }

    private void Loop()
    {
        while (_running)
        {
            var now = DateTime.UtcNow;

            if (_wantLeft)
            {
                Sample(0x01, _left, ref _leftWasDown, now);
            }

            if (_wantRight)
            {
                Sample(0x02, _right, ref _rightWasDown, now);
            }

            Thread.Sleep(SampleIntervalMs);
        }
    }

    private void Sample(int virtualKey, List<DateTime> stamps, ref bool wasDown, DateTime now)
    {
        var down = VirtualKeys.IsDown(virtualKey);

        lock (_gate)
        {
            CountClicks(stamps, down, ref wasDown, now, WindowMs);
        }
    }

    /// <summary>
    /// 按下沿计数 + 维护时间窗 —— 返回窗口内的个数（也就是 CPS）。
    ///
    /// 抽成静态方法是为了能单独验：计数对不对不依赖真实鼠标点击，
    /// 而自检按规矩不能发鼠标键（会真点下去）。
    /// </summary>
    internal static int CountClicks(
        List<DateTime> stamps, bool isDown, ref bool wasDown, DateTime now, int windowMs)
    {
        if (isDown && !wasDown)
        {
            stamps.Add(now);
        }

        wasDown = isDown;

        stamps.RemoveAll(t => (now - t).TotalMilliseconds > windowMs);

        return stamps.Count;
    }

    public void Dispose()
    {
        _running = false;
        _wantLeft = false;
        _wantRight = false;

        try
        {
            _thread?.Join(300);
        }
        catch
        {
        }

        _thread = null;

        lock (_gate)
        {
            _left.Clear();
            _right.Clear();
        }
    }
}
