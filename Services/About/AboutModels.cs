using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftChatOverlay.Services.About;

/// <summary>
/// 「关于」页的鸣谢清单（仓库里的 about/about.json）。
/// 和市场清单同一套规矩：客户端只读，解析要宽容（大小写不敏感、允许注释和尾逗号）。
/// </summary>
public sealed class AboutIndex
{
    public int SchemaVersion { get; set; } = 1;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>打赏鸣谢。顺序就是 JSON 里的顺序（作者手工排的，客户端不重排）。</summary>
    public List<AboutRewardEntry> Rewards { get; set; } = new();

    /// <summary>插件开发鸣谢。顺序同样是 JSON 里的顺序。</summary>
    public List<AboutDevEntry> PluginDevs { get; set; } = new();

    /// <summary>这份清单是从哪个地址拿到的（只用来显示来源，不参与序列化）。</summary>
    [JsonIgnore]
    public string SourceUrl { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析清单；格式不对返回 null（调用方负责报错）。</summary>
    public static AboutIndex? TryParse(string json, string sourceUrl)
    {
        try
        {
            var index = JsonSerializer.Deserialize<AboutIndex>(json, Options);
            if (index is null)
            {
                return null;
            }

            index.SourceUrl = sourceUrl ?? "";
            index.Rewards ??= new List<AboutRewardEntry>();
            index.PluginDevs ??= new List<AboutDevEntry>();
            index.Rewards.RemoveAll(r => string.IsNullOrWhiteSpace(r.Name));
            index.PluginDevs.RemoveAll(d => string.IsNullOrWhiteSpace(d.Name));

            // 打赏榜按金额从大到小排（OrderByDescending 是稳定排序：金额相同/都读不出数字的，
            // 保持作者在 JSON 里写的相对顺序，所以没写金额的那些不会被搅乱）
            index.Rewards = index.Rewards
                .OrderByDescending(r => AboutRewardEntry.ParseAmount(r.Amount))
                .ToList();

            return index;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>打赏鸣谢里的一条。</summary>
public sealed class AboutRewardEntry
{
    public string Name { get; set; } = "";

    /// <summary>金额，写成什么样就显示什么样（比如 "¥20"）。</summary>
    public string Amount { get; set; } = "";

    /// <summary>留言（可选）。</summary>
    public string? Message { get; set; }

    /// <summary>时间（可选，随便写，比如 "2026-09-01"）。</summary>
    public string? Time { get; set; }

    /// <summary>
    /// 把金额读成数字，用来排序。
    /// 容错写法都认：`¥20`、`￥ 6.66`、`20元`、`1,000`；读不出来（没写、写"一杯奶茶"之类）返回
    /// <see cref="double.NegativeInfinity"/> —— 排序时统一沉到最后。
    /// </summary>
    public static double ParseAmount(string? amount)
    {
        if (string.IsNullOrWhiteSpace(amount))
        {
            return double.NegativeInfinity;
        }

        var digits = new string(amount.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NegativeInfinity;
    }
}

/// <summary>插件开发鸣谢里的一条。</summary>
public sealed class AboutDevEntry
{
    public string Name { get; set; } = "";

    /// <summary>做了什么插件 / 贡献（可选）。</summary>
    public string? Plugin { get; set; }

    /// <summary>主页 / 仓库链接（可选，界面上可以点开）。</summary>
    public string? Link { get; set; }

    /// <summary>备注（可选）。</summary>
    public string? Note { get; set; }
}
