using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.Sample;

/// <summary>
/// 样例插件：演示一个插件能做的事情。
///
///  · 注册一个自己的导航页（页面由宿主渲染，插件只交字段）
///  · 收聊天消息（ChatLineReceived）
///  · 往悬浮窗发消息（SendToOverlay）
///  · 存自己的配置（PluginSettingsFile，落在插件目录里）
///  · 用宿主的日志 / 提示
/// </summary>
public sealed class SamplePlugin : IPlugin
{
    private IPluginHost _host = null!;
    private SampleSettings _settings = new();
    private int _chatCount;
    private string _lastLine = "";

    public string Id => "goldiamond.sample";

    public string DisplayName => "示例插件";

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = PluginSettingsFile.Load<SampleSettings>(host.PluginDirectory);

        host.ChatLineReceived += OnChatLine;

        var page = new PluginPage
        {
            Title = DisplayName,
            Description = "这是个示例插件，用来演示插件平台能做什么。源码在 Plugins\\SamplePlugin\\，可以直接照着改成你自己的功能。",
            StatusText = () =>
            {
                var tail = string.IsNullOrEmpty(_lastLine) ? "" : $" · 最近一条：{_lastLine}";
                return $"已收到 {_chatCount} 条聊天消息{tail}";
            },
        };

        page.Fields.Add(new PluginField
        {
            Key = "note",
            Label = "说明",
            Kind = PluginFieldKind.ReadOnly,
            Text = "下面改动会立刻存进插件自己的 settings.json（就在插件目录里）。",
        });

        page.Fields.Add(new PluginField
        {
            Key = "echo",
            Label = "把聊天消息转发到悬浮窗",
            Kind = PluginFieldKind.Toggle,
            Bool = _settings.EchoToOverlay,
            Hint = "勾上之后，游戏里每收到一条聊天，就往悬浮窗再发一条（前面加 [示例]）。",
            Changed = field =>
            {
                _settings.EchoToOverlay = field.Bool;
                Save();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "slider",
            Label = "示例滑块",
            Kind = PluginFieldKind.Slider,
            Number = _settings.SliderValue,
            Minimum = 0,
            Maximum = 10,
            Step = 0.5,
            Suffix = " 格",
            Hint = "演示滑块字段：拖动就存，重开软件还在。",
            Changed = field =>
            {
                _settings.SliderValue = field.Number;
                Save();
            },
        });

        page.Actions.Add(new PluginAction
        {
            Label = "发一条测试消息到悬浮窗",
            Primary = true,
            Invoke = () =>
            {
                _host.SendToOverlay("[示例] 你好，我是插件。");
                return "已发送";
            },
        });

        page.Actions.Add(new PluginAction
        {
            Label = "弹个提示",
            Invoke = () => "插件按钮点到了",
        });

        page.Actions.Add(new PluginAction
        {
            Label = "把最近 5 条聊天打到日志",
            Invoke = () =>
            {
                foreach (var line in _host.RecentChatLines.TakeLast(5))
                {
                    _host.Log("最近一条：" + line.Raw);
                }

                return "已写进调试后台";
            },
        });

        host.RegisterPage(page);
        host.Log($"[示例插件] 已加载，配置目录：{host.PluginDirectory}");
    }

    public void Shutdown()
    {
        _host.ChatLineReceived -= OnChatLine;
    }

    private void OnChatLine(ChatLine line)
    {
        _chatCount++;
        _lastLine = line.Raw.Length > 40 ? line.Raw[..40] + "…" : line.Raw;

        if (_settings.EchoToOverlay)
        {
            _host.SendToOverlay("[示例] " + line.Raw);
        }
    }

    private void Save() => PluginSettingsFile.Save(_host.PluginDirectory, _settings);

    /// <summary>插件自己的配置。放在插件目录里，跟主程序的设置完全分开。</summary>
    private sealed class SampleSettings
    {
        public bool EchoToOverlay { get; set; }

        public double SliderValue { get; set; } = 3;
    }
}