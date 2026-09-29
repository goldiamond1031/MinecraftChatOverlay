using System.Runtime.InteropServices;
using System.Windows;

namespace MinecraftChatOverlay.Plugins.AutoGg;

/// <summary>
/// 往游戏里"打字"的那部分：模拟按键 + 剪贴板粘贴。
///
/// 两个关键点（都是从主程序那边原样搬过来的，踩过坑）：
///  1) 发送期间挂一个低级键盘钩子把**真人按键**吞掉（只放行注入的），
///     否则玩家按着的键会混进聊天栏，把 gg 打成乱码；
///  2) 默认走剪贴板粘贴（Ctrl+V）而不是逐字敲 —— 中文输入法会把字母吞掉、
///     或者把回车吃掉导致发不出去。
/// </summary>
internal sealed class AutoGgKeySender
{
    private const int WhKeyboardLl = 13;
    private const int LlkhfInjected = 0x00000010;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;

    private IntPtr _hookId = IntPtr.Zero;
    private HookProc? _hookProc;
    private readonly HashSet<int> _keysDownBeforeBlock = new();

    /// <summary>发完主文字后、接着发 /again 之前等多久（等游戏把聊天栏关掉再敲第二次）。</summary>
    private const int BetweenSendsDelayMs = 350;

    /// <summary>要接着发的第二条：固定就是 /again。</summary>
    private const string AgainCommand = "/again";

    /// <summary>检测到触发消息后发送一次（开了「发送 GG 后自动发送 /again」就再发一条）。</summary>
    public async Task SendAsync(AutoGgSettings settings, Action<string> log)
    {
        string? oldClipboardText = null;
        var useClipboardPaste = false;

        try
        {
            log("[自动GG] 检测到胜利消息，准备发送 " + settings.Text + " ...");
            await Task.Delay(200);

            if (settings.UseClipboard)
            {
                try
                {
                    oldClipboardText = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
                    useClipboardPaste = true;
                    log("[自动GG] 已使用剪贴板模式，避免中文输入法干扰");
                }
                catch
                {
                    useClipboardPaste = false;
                    log("[自动GG] 剪贴板暂不可用，自动改用直接输入模式");
                }
            }

            // 整段（含可能有的第二条）都在同一次键盘阻断里，中途不放开，
            // 否则玩家按着的键会趁机混进聊天栏。
            StartKeyboardBlock();

            await TypeAndSendAsync(settings.ChatKey, settings.Text, useClipboardPaste, log);

            if (settings.SendAgainAfterGg)
            {
                await Task.Delay(BetweenSendsDelayMs);
                await TypeAndSendAsync(settings.ChatKey, AgainCommand, useClipboardPaste, log);
            }
        }
        catch (Exception ex)
        {
            log("[自动GG] 发送失败：" + ex.Message);
        }
        finally
        {
            StopKeyboardBlock();

            if (useClipboardPaste)
            {
                try
                {
                    if (oldClipboardText != null)
                    {
                        System.Windows.Clipboard.SetText(oldClipboardText);
                    }
                    else
                    {
                        System.Windows.Clipboard.Clear();
                    }
                }
                catch
                {
                    // 恢复剪贴板失败不影响游戏内发送
                }
            }
        }
    }

    /// <summary>一次完整的「打开聊天栏 → 输入 → 回车」。</summary>
    private static async Task TypeAndSendAsync(string chatKeySetting, string text, bool useClipboardPaste, Action<string> log)
    {
        var chatKey = chatKeySetting.Trim();
        if (string.Equals(chatKey, "enter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(chatKey, "回车", StringComparison.Ordinal))
        {
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        }
        else if (!string.IsNullOrEmpty(chatKey))
        {
            System.Windows.Forms.SendKeys.SendWait(chatKey);
        }

        await Task.Delay(250);

        if (useClipboardPaste)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                System.Windows.Forms.SendKeys.SendWait("^v");
            }
            catch
            {
                // 剪贴板这会儿写不进去（被别的程序占着）就退回逐字敲
                System.Windows.Forms.SendKeys.SendWait(text);
            }
        }
        else
        {
            System.Windows.Forms.SendKeys.SendWait(text);
        }

        await Task.Delay(100);
        System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        log("[自动GG] 已发送：" + text);
    }

    private void StartKeyboardBlock()
    {
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        _keysDownBeforeBlock.Clear();
        for (var key = 0x08; key <= 0xFE; key++)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
            {
                _keysDownBeforeBlock.Add(key);
            }
        }

        _hookProc = KeyboardHookCallback;
        _hookId = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
    }

    private void StopKeyboardBlock()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        _hookProc = null;
        _keysDownBeforeBlock.Clear();
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hook = Marshal.PtrToStructure<KeyboardLowLevelHookStruct>(lParam);
            var isInjected = (hook.Flags & LlkhfInjected) != 0;

            if (!isInjected)
            {
                var isKeyUp = wParam == (IntPtr)WmKeyUp || wParam == (IntPtr)WmSysKeyUp;

                if (isKeyUp && _keysDownBeforeBlock.Remove(hook.VirtualKeyCode))
                {
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardLowLevelHookStruct
    {
        public int VirtualKeyCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}