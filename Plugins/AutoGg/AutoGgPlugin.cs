using System.Text.RegularExpressions;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.AutoGg;

/// <summary>
/// 「自动 GG」—— 原本是主程序内置功能，现在整体搬成插件（页面也搬了，用自绘页面方式）。
///
/// 逻辑和原来一模一样：收到聊天消息 → 匹配触发正则 → 模拟按键打开聊天栏 → 粘贴/输入 → 回车。
/// 触发前会检查"启用、正则合法、5 秒内不重复、上一次还没发完"。
/// </summary>
public sealed class AutoGgPlugin : IPlugin
{
    private readonly AutoGgKeySender _sender = new();
    private AutoGgSettings _settings = new();
    private AutoGgPage? _page;
    private bool _sending;
    private DateTime _lastTriggerAt = DateTime.MinValue;

    public string Id => "goldiamond.autogg";

    public string DisplayName => "自动 GG";

    /// <summary>页面里要用（读设置、存盘、发提示）。</summary>
    public IPluginHost Host { get; private set; } = null!;

    public AutoGgSettings Settings => _settings;

    public void Initialize(IPluginHost host)
    {
        Host = host;
        _settings = PluginSettingsFile.Load<AutoGgSettings>(host.PluginDirectory);
        _lastTriggerAt = _settings.LastTriggerAt;

        host.ChatLineReceived += OnChatLine;

        host.RegisterPage(new PluginPage
        {
            Title = DisplayName,
            Description = "检测到胜利消息后，自动打开聊天栏并发送 gg。页面是插件自绘的（原来那块界面搬过来了）。",
            // 故意不给 StatusText：页头下面那行「启用中 · 上次触发 …」太抢眼，
            // 启用状态看页面里的开关就行、上次触发时间页面底部也有（见 AutoGgLastTriggerText）。
            ContentFactory = () =>
            {
                _page = new AutoGgPage(this);
                return _page;
            },
        });

        host.Log($"[自动GG] 已加载，配置目录：{host.PluginDirectory}（当前：{(_settings.Enabled ? "启用" : "关闭")}，触发 /{_settings.TriggerPattern}/，发送「{_settings.Text}」）");
    }

    public void Shutdown()
    {
        try
        {
            Host.ChatLineReceived -= OnChatLine;
        }
        catch
        {
            // 关的时候出问题不该影响宿主
        }
    }

    /// <summary>页面里改完设置后调这个存盘。</summary>
    public void SaveSettings()
    {
        try
        {
            PluginSettingsFile.Save(Host.PluginDirectory, _settings);
        }
        catch (Exception ex)
        {
            Host.Log("[自动GG] 保存设置失败：" + ex.Message);
        }
    }

    private void OnChatLine(ChatLine line)
    {
        TryAutoGg(line.Raw);
    }

    /// <summary>和主程序原来的 TryAutoGg 一致。</summary>
    private void TryAutoGg(string message)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.TriggerPattern))
        {
            return;
        }

        if (_sending)
        {
            return;
        }

        if ((DateTime.Now - _lastTriggerAt).TotalSeconds < 5)
        {
            return;
        }

        try
        {
            if (!Regex.IsMatch(message, _settings.TriggerPattern, RegexOptions.IgnoreCase))
            {
                return;
            }
        }
        catch (ArgumentException)
        {
            Host.Log("[自动GG] 触发正则不合法，已跳过");
            return;
        }

        _sending = true;
        _lastTriggerAt = DateTime.Now;
        _settings.LastTriggerAt = _lastTriggerAt;
        SaveSettings();
        _page?.UpdateLastTriggerText();

        _ = SendAsync();
    }

    private async Task SendAsync()
    {
        try
        {
            await _sender.SendAsync(_settings, Host.Log);
        }
        catch (Exception ex)
        {
            Host.Log("[自动GG] 发送出错：" + ex.Message);
        }
        finally
        {
            _sending = false;
        }
    }
}