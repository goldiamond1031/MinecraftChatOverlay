using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.WindowFullscreen;

/// <summary>
/// 「窗口全屏」—— 原本是主程序里的内置页，现在是一个标准插件。
///
/// 干的事就一件：把游戏窗口改成无边框全屏，这样聊天悬浮窗才能盖在游戏画面上。
/// 真正的窗口操作在 <see cref="GameWindowFullscreen"/> 里，这里只管生命周期和存盘。
/// </summary>
public sealed class WindowFullscreenPlugin : IPlugin
{
    private IPluginHost _host = null!;
    private WindowFullscreenSettings _settings = new();
    private WindowFullscreenPage? _page;

    public string Id => "goldiamond.windowfullscreen";

    public string DisplayName => "窗口全屏";

    /// <summary>页面要用（读设置、存盘、弹提示、写日志、抬悬浮窗）。</summary>
    public IPluginHost Host => _host;

    public WindowFullscreenSettings Settings => _settings;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = PluginSettingsFile.Load<WindowFullscreenSettings>(host.PluginDirectory);

        host.RegisterPage(new PluginPage
        {
            Title = DisplayName,
            Description = "把游戏窗口改成无边框全屏，这样聊天悬浮窗才能盖在游戏画面上。只改窗口样式和位置，"
                        + "不注入进程、不碰游戏画面，随时可以还原。",
            ContentFactory = () =>
            {
                _page = new WindowFullscreenPage(this);
                return _page;
            },
        });

        host.Log($"[窗口全屏] 已加载，配置目录：{host.PluginDirectory}");
    }

    /// <summary>
    /// 插件被禁用 / 卸载 / 程序退出时调用：按设置把游戏窗口还原回去。
    /// 插件一停，它改过的窗口就没人负责还原了，所以这里必须兜住。
    /// </summary>
    public void Shutdown()
    {
        try
        {
            _page?.StopStatusTimer();

            if (_settings.RestoreOnExit && GameWindowFullscreen.TryRestore(out var message))
            {
                _host.Log("[窗口全屏] 退出时自动还原：" + message);
            }
        }
        catch
        {
            // 退出流程里出问题也不能拦住关闭
        }

        _page = null;
    }

    /// <summary>页面里改完设置后调这个存盘。</summary>
    public void SaveSettings()
    {
        try
        {
            PluginSettingsFile.Save(_host.PluginDirectory, _settings);
        }
        catch (Exception ex)
        {
            _host.Log("[窗口全屏] 保存设置失败：" + ex.Message);
        }
    }
}
