using System.IO;
using System.Text.Json;

namespace MinecraftChatOverlay.Services.Plugins;

/// <summary>
/// 插件包里的 plugin.json。作者手写，所以解析要宽容（大小写不敏感、允许注释和尾逗号）。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>稳定唯一 id，形如 goldiamond.regionmagnifier。决定安装目录名，也是"禁用列表"里的键。</summary>
    public string Id { get; set; } = "";

    /// <summary>界面显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>插件自己的版本号（作者自己定）。</summary>
    public string Version { get; set; } = "";

    /// <summary>作者。</summary>
    public string Author { get; set; } = "";

    /// <summary>一句话说明。</summary>
    public string Description { get; set; } = "";

    /// <summary>要求的契约版本，必须等于 PluginApi.Version，否则拒装。</summary>
    public int ApiVersion { get; set; }

    /// <summary>入口类型全名（可选）。不写就在 dll 里自动找唯一实现 IPlugin 的公开类。</summary>
    public string? Entry { get; set; }

    /// <summary>这个插件会做什么（读日志/联网/操作窗口……）。安装时给用户看，是"知情同意"用的。</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>插件主 dll 的文件名（相对插件目录）。不写就取目录里第一个非契约程序集的 dll。</summary>
    public string? Assembly { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>从文件读；读不出来返回 null（调用方负责报错）。</summary>
    public static PluginManifest? TryLoad(string manifestPath)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>给界面用的简短描述。</summary>
    public string Describe()
    {
        var by = string.IsNullOrWhiteSpace(Author) ? "" : $"  ·  {Author}";
        var ver = string.IsNullOrWhiteSpace(Version) ? "" : $" v{Version}";
        return $"{Name}{ver}{by}";
    }
}