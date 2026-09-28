using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftChatOverlay.Services;

/// <summary>TAB 补全收集 ID 的参数。不同服务器/客户端的补全行为不一样，所以都做成可调。</summary>
public sealed class ChatIdCollectOptions
{
    /// <summary>第一个 ID 需要按几次 TAB（默认 2）。</summary>
    public int FirstTabPresses { get; init; } = 2;

    /// <summary>
    /// 每往后一个 ID，TAB 次数再多几下（默认 1）。
    /// 第 k 个 ID 要按 FirstTabPresses + Step×(k-1) 下：2、3、4、5……
    /// 万一某个服务器是固定次数，把这个设成 0 就行。
    /// </summary>
    public int TabStep { get; init; } = 1;

    /// <summary>最多收集多少轮，防死循环（默认 20）。</summary>
    public int MaxRounds { get; init; } = 20;

    /// <summary>
    /// 每次按键之间等多久（毫秒）。越小越快，但太小游戏可能漏键。
    /// 20ms 大约一帧多，是速度和稳的平衡点；觉得稳可以再往下试 10、甚至 5。
    /// </summary>
    public int StepDelayMs { get; init; } = 20;
}

/// <summary>
/// 用聊天栏的 TAB 补全"问"出本局所有玩家 ID。
///
/// 聊天栏只开一次、关一次；中间每轮的动作是：
///   1. 按 TAB × (2、3、4、5……)  ← 从空输入框开始，补出第 k 个候选
///   2. Ctrl+A 全选 → Ctrl+C 复制 → Delete 清空输入框
///   3. 读剪贴板得到名字
///   4. 补出来的名字和之前重复 → 说明绕完一圈，停止
///
/// **为什么复制完要按 Delete**：把输入框清空，
/// 于是每一轮都从干净状态重新开始，递增次数才不会错位。
/// 用复制的话输入框会一直累积，补全游标越走越偏，越往后越容易漏人。
///
/// 全程**只按 Esc 关闭聊天栏，绝不按回车**（按回车会真的把这一串发到服务器）。
/// </summary>
public static class ChatIdCollector
{
    private const byte VkT = 0x54;
    private const byte VkTab = 0x09;
    private const byte VkA = 0x41;
    private const byte VkC = 0x43;
    private const byte VkX = 0x58;
    private const byte VkDelete = 0x2E;
    private const byte VkEscape = 0x1B;
    private const byte VkControl = 0x11;
    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>
    /// 每轮读之前先把剪贴板写成这个值。写进去之后，"剪贴板变了"才是"复制成功"的可信信号；
    /// 否则上一轮读到的旧名字会一直躺在剪贴板里，被误当成新读到的名字 → 假的"重复 ID"→ 提前退出。
    /// 用 # 和中文，玩家名里不可能出现，绝不会撞车。
    /// </summary>
    private const string ClipboardSentinel = "#MinecraftChatOverlay#读取中";

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>前台是不是游戏窗口。不是的话绝不发按键 —— 否则会打进别的程序里。</summary>
    public static bool IsGameForeground()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            GetWindowThreadProcessId(handle, out var pid);
            if (pid == 0)
            {
                return false;
            }

            var name = Process.GetProcessById((int)pid).ProcessName;
            return name.Contains("java", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("minecraft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 收集本局玩家 ID。
    /// </summary>
    /// <param name="readClipboard">读剪贴板（必须由 UI 线程执行，所以由调用方传进来）。</param>
    /// <param name="trace">进度输出。</param>
    public static async Task<List<string>> CollectAsync(
        ChatIdCollectOptions options,
        Func<string> readClipboard,
        Action<string>? writeClipboard,
        Action<string> trace,
        CancellationToken token)
    {
        var result = new List<string>();

        if (!IsGameForeground())
        {
            trace?.Invoke("前台不是游戏窗口，跳过 ID 收集（按键会打到别的程序里）");
            return result;
        }

        var delay = Math.Clamp(options.StepDelayMs, 5, 400);
        var rounds = Math.Clamp(options.MaxRounds, 2, 60);

        try
        {
            // 打开聊天栏 —— 只开这一次，后面靠 Ctrl+X 清空输入框来复位
            TapKey(VkT);
            await Task.Delay(delay, token).ConfigureAwait(false);

            string? previous = null;
            var consecutiveSame = 0;

            for (var round = 1; round <= rounds; round++)
            {
                token.ThrowIfCancellationRequested();

                // 第 k 个 ID 按 FirstTabPresses + Step×(k-1) 下（2、3、4、5……）
                var presses = Math.Clamp(
                    options.FirstTabPresses + options.TabStep * (round - 1), 1, 30);

                for (var i = 0; i < presses; i++)
                {
                    TapKey(VkTab);
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }

                // 剪切出来的通常就是一个名字，用 LastId 再稳一层（防剪贴板带多余空白）
                var current = LastId(await CopyClearAndReadAsync(readClipboard, writeClipboard, delay, token, trace).ConfigureAwait(false));

                if (string.IsNullOrEmpty(current))
                {
                    trace?.Invoke($"第 {round} 轮没读到内容（TAB 补全没生效、或剪贴板没更新），停止收集");
                    break;
                }

                // 主条件：补到已经在名单里的 ID —— 这才是"绕完一圈"的真正标志
                if (result.Contains(current, StringComparer.Ordinal))
                {
                    trace?.Invoke($"第 {round} 轮补到重复的 {current}，说明已经绕完一圈，停止（共 {result.Count} 个）");
                    break;
                }

                // 偶尔和上一轮同名不算结束（补全可能只是卡了一下），连续 3 次才认为是真卡住
                if (previous is not null && string.Equals(current, previous, StringComparison.Ordinal))
                {
                    consecutiveSame++;
                    previous = current;

                    if (consecutiveSame >= 3)
                    {
                        trace?.Invoke($"连续 {consecutiveSame} 轮都补到 {current}，补全像是卡住了，先停（共 {result.Count} 个）");
                        break;
                    }

                    trace?.Invoke($"第 {round} 轮又补到 {current}（连续 {consecutiveSame} 次），继续往下试");
                    continue;
                }

                consecutiveSame = 0;
                result.Add(current);
                previous = current;
                trace?.Invoke($"第 {round} 个 ID：{current}（按了 {presses} 下 TAB）");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            trace?.Invoke("收集 ID 出错：" + ex.Message);
        }
        finally
        {
            // 无论如何都要把聊天栏关掉，而且只用 Esc —— 绝不按回车
            TapKey(VkEscape);
            await Task.Delay(60).ConfigureAwait(false);
            TapKey(VkEscape);
        }

        return result;
    }

    /// <summary>
    /// 读出输入框里的名字，并**把输入框清空**，让下一轮从干净状态重新补全：
    ///   Ctrl+A 全选 → Ctrl+X 剪切（名字进剪贴板，输入框同时被清空）→ Delete（保险，输入框空了就是空操作）
    ///
    /// 当前用的是 **Ctrl+X 剪切**（一步搞定，比"复制 + 手动清空"少一个动作）。
    /// 后面那条 Delete 是保险：万一剪切只复制没删掉，它也能把输入框清干净。
    /// 剪贴板里先写了哨兵值，所以"到底有没有写进去"是可验证的，不会把旧名字误当成新读到的。
    /// </summary>
    private static async Task<string> CopyClearAndReadAsync(
        Func<string> readClipboard,
        Action<string>? writeClipboard,
        int delay,
        CancellationToken token,
        Action<string>? trace)
    {
        // 先把剪贴板写成哨兵值（写不进去就退回"和旧内容比"的老办法）
        var sentinelOk = false;
        var baseline = "";

        if (writeClipboard is not null)
        {
            try
            {
                writeClipboard(ClipboardSentinel);
                sentinelOk = true;
            }
            catch
            {
                sentinelOk = false;
            }
        }

        if (!sentinelOk)
        {
            baseline = SafeRead(readClipboard);
        }

        var text = "";
        var got = false;

        // 最多试两次：剪贴板偶尔会被游戏短暂占用，重试一次基本就好了
        for (var attempt = 1; attempt <= 2 && !got; attempt++)
        {
            CtrlChord(VkA);
            await Task.Delay(Math.Max(delay, 25), token).ConfigureAwait(false);

            // 剪切：一步同时完成"进剪贴板"和"清空输入框"。
            // 如果 MC 不支持剪切，哨兵值会原封不动留在剪贴板里 → 下面的读取判定会发现 → 明确报出来。
            CtrlChord(VkX);

            // 先安静等一会儿再开始读：读剪贴板要独占它，太早去读会把游戏那边的复制挤掉。
            await Task.Delay(Math.Max(delay, 60), token).ConfigureAwait(false);

            var deadline = Environment.TickCount64 + Math.Max(300, delay * 8);
            while (Environment.TickCount64 < deadline)
            {
                await Task.Delay(15, token).ConfigureAwait(false);

                // 读取失败（被别占用）或读到空，都只是"还没好"，继续等 —— 不能当成复制成功
                if (!TryReadClipboard(readClipboard, out var candidate) || string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                var sameAsSentinel = sentinelOk &&
                                     string.Equals(candidate, ClipboardSentinel, StringComparison.Ordinal);
                var sameAsBaseline = !sentinelOk &&
                                     string.Equals(candidate, baseline, StringComparison.Ordinal);
                if (sameAsSentinel || sameAsBaseline)
                {
                    continue;
                }

                text = candidate;
                got = true;
                break;
            }
        }

        // 清空输入框（关键一步：删掉选中的内容，下一轮从空开始）
        TapKey(VkDelete);
        await Task.Delay(Math.Max(delay, 15), token).ConfigureAwait(false);

        if (!got)
        {
            trace?.Invoke("没能从剪贴板读到名字（剪切没生效，或剪贴板被别的程序占用），停止收集");
            return "";
        }

        return text;
    }

    /// <summary>读剪贴板；失败（被别的程序占用等）返回 false，和"读到空字符串"区分开。</summary>
    private static bool TryReadClipboard(Func<string> readClipboard, out string text)
    {
        try
        {
            text = readClipboard() ?? "";
            return true;
        }
        catch
        {
            text = "";
            return false;
        }
    }

    private static string SafeRead(Func<string> readClipboard)
    {
        try
        {
            return readClipboard() ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>取一串空格分隔的文本里最后一个 ID（剪切出来的通常只有一个，留着更稳）。</summary>
    private static string LastId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var parts = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "" : parts[^1].Trim();
    }

    /// <summary>把一串文本里的所有 ID 按出现顺序去重（备用工具）。</summary>
    public static List<string> SplitIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        return text
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length is >= 2 and <= 24)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void TapKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static void CtrlChord(byte vk)
    {
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        TapKey(vk);
        keybd_event(VkControl, 0, KeyEventKeyUp, UIntPtr.Zero);
    }
}
