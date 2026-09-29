using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.PlayerQuery;

/// <summary>
/// 「玩家查询」—— 原本是主程序里的内置页，现在是一个标准插件。
///
/// 页面是**自绘**的（原来那块 `PlayerQueryPanel` 整块搬过来了）——
/// 里面有拖拽排序、字段下拉、按需生成的结果卡片，表单式那套表达不了。
/// </summary>
public sealed class PlayerQueryPlugin : IPlugin
{
    private IPluginHost _host = null!;
    private PlayerQuerySettings _settings = new();
    private PlayerQueryPage? _page;

    public string Id => "goldiamond.playerquery";

    public string DisplayName => "玩家查询";

    /// <summary>页面要用（读设置、存盘、弹提示、写日志）。</summary>
    public IPluginHost Host => _host;

    public PlayerQuerySettings Settings => _settings;

    public void Initialize(IPluginHost host)
    {
        _host = host;

        _settings = PluginSettingsFile.Load<PlayerQuerySettings>(host.PluginDirectory);
        _settings.Fields ??= new();

        host.RegisterPage(new PluginPage
        {
            Title = DisplayName,
            Description = "输入布吉岛 API KEY，查询起床战争 / 空岛战争战绩。显示哪些字段可以自己挑，也能拖着排序。",
            ContentFactory = () =>
            {
                _page = new PlayerQueryPage(this);
                return _page;
            },
        });

        host.Log($"[玩家查询] 已加载，配置目录：{host.PluginDirectory}（{_settings.Fields.Count} 个显示字段）");
    }

    public void Shutdown()
    {
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
            _host.Log("[玩家查询] 保存设置失败：" + ex.Message);
        }
    }
}
