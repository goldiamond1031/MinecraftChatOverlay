using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftChatOverlay.Services.Plugins.Market;

/// <summary>
/// 市场清单（仓库里的 market/index.json）。
/// 由 tools\rebuild-market-index.ps1 生成，客户端只读，解析要宽容（大小写不敏感、允许注释和尾逗号）。
/// </summary>
public sealed class MarketIndex
{
    public int SchemaVersion { get; set; } = 1;

    public DateTime? UpdatedAt { get; set; }

    public List<MarketPlugin> Plugins { get; set; } = new();

    /// <summary>这份清单是从哪个地址拿到的（下载相对路径时当基准，不写进缓存）。</summary>
    [JsonIgnore]
    public string SourceUrl { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析清单；格式不对返回 null（调用方负责报错）。</summary>
    public static MarketIndex? TryParse(string json, string sourceUrl)
    {
        try
        {
            var index = JsonSerializer.Deserialize<MarketIndex>(json, Options);
            if (index is null)
            {
                return null;
            }

            index.SourceUrl = sourceUrl ?? "";
            index.Plugins ??= new List<MarketPlugin>();
            index.Plugins.RemoveAll(p => string.IsNullOrWhiteSpace(p.Id));

            foreach (var plugin in index.Plugins)
            {
                plugin.SourceUrl = index.SourceUrl;
                plugin.Capabilities ??= new List<string>();
                plugin.Tags ??= new List<string>();
            }

            return index;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>市场里的一个插件条目。</summary>
public sealed class MarketPlugin
{
    /// <summary>插件 id，和 plugin.json 里的 id 一致（用来判断装没装）。</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>插件版本（以插件包里的 plugin.json 为准）。</summary>
    public string Version { get; set; } = "";

    public string Author { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>要求的契约版本。和本程序的 PluginApi.Version 不一致就不给装。</summary>
    public int ApiVersion { get; set; }

    /// <summary>要求的最低宿主版本（可选，例如 "1.1.0"）。</summary>
    public string? MinHostVersion { get; set; }

    /// <summary>作者声明的能力（安装确认框里给用户看）。</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>插件包地址。写相对路径（相对 index.json）最省事：换镜像、换分支都不用改。</summary>
    public string DownloadUrl { get; set; } = "";

    /// <summary>zip 的 SHA256（十六进制）。下载后校验，对不上就丢弃。</summary>
    public string? Sha256 { get; set; }

    /// <summary>zip 的字节数。</summary>
    public long? Size { get; set; }

    public List<string> Tags { get; set; } = new();

    public string? Homepage { get; set; }

    /// <summary>清单来源地址（解析时填上，用来算下载绝对地址）。</summary>
    [JsonIgnore]
    public string SourceUrl { get; set; } = "";

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>把 downloadUrl 解析成能直接下载的绝对地址。</summary>
    public string? ResolveDownloadUrl()
    {
        var raw = (DownloadUrl ?? "").Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (!Uri.TryCreate(SourceUrl, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        try
        {
            return new Uri(baseUri, raw).ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>比较版本号（宽松：解析不出来就按字符串比）。返回 &gt; 0 表示 a 更新。</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var left = (a ?? "").Trim().TrimStart('v', 'V');
        var right = (b ?? "").Trim().TrimStart('v', 'V');

        if (System.Version.TryParse(left, out var va) && System.Version.TryParse(right, out var vb))
        {
            return va.CompareTo(vb);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}