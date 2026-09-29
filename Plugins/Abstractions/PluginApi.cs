using System.Text.Json;

namespace MinecraftChatOverlay.Plugin;

/// <summary>契约版本。宿主只加载 <see cref="Version"/> 与自己的完全相同的插件，不匹配就拒装并说明原因。</summary>
public static class PluginApi
{
    /// <summary>当前契约版本。改动接口时要同步想清楚：已发布的插件会不会碎。</summary>
    public const int Version = 1;
}

/// <summary>
/// 插件入口。宿主在 plugins\&lt;id&gt;\ 目录里找到实现这个接口的类，new 出来调用 <see cref="Initialize"/>。
/// 实现类必须是 public、非抽象、有公开无参构造函数。
/// </summary>
public interface IPlugin
{
    /// <summary>稳定唯一的 id，形如 goldiamond.regionmagnifier。装插件、存配置、判断重复都用它。</summary>
    string Id { get; }

    /// <summary>界面上显示的名字。</summary>
    string DisplayName { get; }

    /// <summary>装载时调用一次。异常会被宿主吞掉并把插件标成"加载失败"，不会影响主程序。</summary>
    void Initialize(IPluginHost host);

    /// <summary>禁用/卸载/退出时调用。插件自己的窗口、定时器、线程、事件订阅都要在这里收干净。</summary>
    void Shutdown();
}

/// <summary>宿主给插件的能力。每一项都由宿主实现，插件只能用这里有的东西。</summary>
public interface IPluginHost
{
    /// <summary>宿主的契约版本，正常情况下等于 <see cref="PluginApi.Version"/>。</summary>
    int ApiVersion { get; }

    /// <summary>插件自己的目录（可读可写）。放配置、图片、日志都行，卸载时整个目录会被删掉。</summary>
    string PluginDirectory { get; }

    /// <summary>所有插件的根目录（一般不需要，除非做插件之间共享的东西）。</summary>
    string PluginsRootDirectory { get; }

    /// <summary>注册一个导航页。一个插件只能注册一个，重复调用以最后一次为准。</summary>
    void RegisterPage(PluginPage page);

    /// <summary>写一行日志到宿主的调试后台页（用户开了"启用后端日志"才会显示）。</summary>
    void Log(string message);

    /// <summary>往宿主的聊天悬浮窗发一条消息（插件自己的输出、播报结果都走这里）。</summary>
    void SendToOverlay(string text);

    /// <summary>
    /// 把聊天悬浮窗重新抬到最前面（不抢焦点、不改尺寸）。
    /// 插件动了别的窗口的层级之后叫一下 —— 比如「窗口全屏」把游戏窗口铺满屏幕之后，
    /// 不让悬浮窗被新窗口挤到下面去。没开悬浮窗时调用是安全的（什么也不会发生）。
    /// </summary>
    void ReassertOverlayTopmost();

    /// <summary>弹一条右下角提示。</summary>
    void ShowToast(string message);

    /// <summary>收到一条聊天消息。事件在宿主的 UI 线程上触发，别在这里做耗时操作。</summary>
    event Action<ChatLine>? ChatLineReceived;

    /// <summary>最近收到的聊天消息（最多 200 条），插件初始化时可以用它回填状态。</summary>
    IReadOnlyList<ChatLine> RecentChatLines { get; }

    /// <summary>当前可见的游戏类窗口列表（宿主已经帮你过滤掉工具窗、无标题窗和本程序自己）。</summary>
    IReadOnlyList<GameWindowInfo> GetGameWindows();
}

/// <summary>一条聊天消息。</summary>
/// <param name="Raw">原始文本（已按日志编码解码、已去掉时间戳和日志前缀）。</param>
/// <param name="Time">收到的时间。</param>
public sealed record ChatLine(string Raw, DateTime Time);

/// <summary>一个候选游戏窗口。</summary>
public sealed record GameWindowInfo(IntPtr Handle, int ProcessId, string ProcessName, string Title, int Width, int Height)
{
    /// <summary>下拉框里显示的文本。</summary>
    public override string ToString()
    {
        var name = string.IsNullOrWhiteSpace(Title) ? ProcessName : $"{ProcessName}  {Title}";
        return $"{name}  (PID {ProcessId})  {Width}×{Height}";
    }
}

/// <summary>
/// 插件的一个导航页。宿主负责画：页头、状态行、字段、按钮，插件只交数据。
/// 这样可以保证所有插件页面长一个样，也不会因为宿主改样式而白屏。
/// </summary>
public sealed class PluginPage
{
    /// <summary>左侧导航栏上的文字。</summary>
    public required string Title { get; init; }

    /// <summary>页头下面那句说明。</summary>
    public string? Description { get; init; }

    /// <summary>设置字段（按顺序显示在同一张卡片里）。</summary>
    public List<PluginField> Fields { get; } = new();

    /// <summary>操作按钮。</summary>
    public List<PluginAction> Actions { get; } = new();

    /// <summary>
    /// 自绘页面（可选，进阶用法）。
    /// 返回一个 WPF 控件（FrameworkElement，宿主会做类型检查）；适合"表单式表达不了"的功能
    /// （比如规则列表、拖拽排序、实时预览）。返回 null 或留着不写就走表单式。
    ///
    /// 注意：这个控件跑在宿主的 UI 线程上，抛异常会被宿主拦住并把页面显示成出错信息；
    /// 想要和内置页面一样的外观，可以用 DynamicResource 引用宿主的主题资源
    /// （AccentBrush / SurfaceBrush / BorderBrush / TextSecondaryBrush / CardStyle / SmallButtonStyle …）。
    /// </summary>
    public Func<object>? ContentFactory { get; set; }
    /// <summary>
    /// 页头状态行的文字，宿主每秒钟问一次；**留着不写就不显示这一行**（页面里已经表达清楚状态的插件可以不要它）。
    /// 写了但这一次返回 null，那一行会是空的。
    /// </summary>
    public Func<string>? StatusText { get; set; }
}

/// <summary>字段类型。</summary>
public enum PluginFieldKind
{
    /// <summary>开关。</summary>
    Toggle,

    /// <summary>滑块（配 Minimum / Maximum / Step / Suffix）。</summary>
    Slider,

    /// <summary>下拉框（配 OptionsProvider）。</summary>
    Select,

    /// <summary>单行输入框。</summary>
    Text,

    /// <summary>只读文本（说明用）。</summary>
    ReadOnly,
}

/// <summary>一个字段。值变了宿主会回调 <see cref="Changed"/>，插件自己存盘。</summary>
public sealed class PluginField
{
    /// <summary>插件内部用的键，随便起，只要自己认得。</summary>
    public required string Key { get; init; }

    /// <summary>界面上显示的名字。</summary>
    public string Label { get; init; } = "";

    /// <summary>字段类型。</summary>
    public PluginFieldKind Kind { get; init; } = PluginFieldKind.Toggle;

    /// <summary>字段下面那行小字说明。</summary>
    public string? Hint { get; init; }

    /// <summary>Toggle 用。</summary>
    public bool Bool { get; set; }

    /// <summary>Slider 用。</summary>
    public double Number { get; set; }

    /// <summary>Slider 下限。</summary>
    public double Minimum { get; init; }

    /// <summary>Slider 上限。</summary>
    public double Maximum { get; init; } = 100;

    /// <summary>Slider 步进。</summary>
    public double Step { get; init; } = 1;

    /// <summary>Slider 数值后面跟的单位，例如 "×"、" fps"。</summary>
    public string Suffix { get; init; } = "";

    /// <summary>Slider 数值格式，默认 0.#（整数不显示小数点）。</summary>
    public string NumberFormat { get; init; } = "0.#";

    /// <summary>Text / ReadOnly 用。</summary>
    public string Text { get; set; } = "";

    /// <summary>Select 用：当前选中项的 Key。</summary>
    public string? SelectedKey { get; set; }

    /// <summary>Select 用：候选项从哪来。宿主会在渲染时、以及每隔 <see cref="OptionsRefreshSeconds"/> 秒问一次。</summary>
    public Func<IReadOnlyList<PluginOption>>? OptionsProvider { get; init; }

    /// <summary>Select 用：自动刷新候选项的间隔（秒）。0 = 只在渲染时取一次。</summary>
    public double OptionsRefreshSeconds { get; init; }

    /// <summary>值变化时回调（Toggle / Slider / Select / Text 都会调）。</summary>
    public Action<PluginField>? Changed { get; init; }

    /// <summary>字段是否可用。宿主每秒钟按这个值刷新一次（例如"没选目标就禁用"）。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Select 的一个候选项。</summary>
/// <param name="Key">插件自己定义的值。</param>
/// <param name="Text">界面上显示的文本。</param>
public sealed record PluginOption(string Key, string Text);

/// <summary>一个操作按钮。</summary>
public sealed class PluginAction
{
    /// <summary>界面上显示的按钮文字。</summary>
    public required string Label { get; init; }

    /// <summary>true = 主按钮（高亮），false = 普通按钮。</summary>
    public bool Primary { get; init; }

    /// <summary>点了之后做什么。返回非空字符串就弹一条提示；抛出的异常会被宿主拦住并提示失败原因。</summary>
    public Func<string?>? Invoke { get; init; }

    /// <summary>按钮是否可用（宿主每秒钟按这个值刷新）。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 插件存自己配置的小工具（省得每个插件都写一遍 JSON 读写）。
/// 文件落在插件自己的目录里，卸载插件时一起删掉。
/// </summary>
public static class PluginSettingsFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>读配置；文件不存在或坏了就返回一份默认值。</summary>
    public static T Load<T>(string pluginDirectory, string fileName = "settings.json") where T : new()
    {
        try
        {
            var path = Path.Combine(pluginDirectory, fileName);
            if (!File.Exists(path))
            {
                return new T();
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    /// <summary>写配置。写失败不抛异常（配置丢了不该让插件崩）。</summary>
    public static void Save<T>(string pluginDirectory, T value, string fileName = "settings.json")
    {
        try
        {
            Directory.CreateDirectory(pluginDirectory);
            File.WriteAllText(Path.Combine(pluginDirectory, fileName), JsonSerializer.Serialize(value, Options));
        }
        catch
        {
            // 忽略
        }
    }
}