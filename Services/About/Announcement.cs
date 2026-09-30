using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftChatOverlay.Services.About;

/// <summary>
/// 公告（仓库里的 about/announcement.json）。
///
/// 没有服务器：软件启动时静默拉一次，拉到了且 id 和上次弹过的不一样，就弹窗显示。
/// 解析规矩和鸣谢清单一致（大小写不敏感、允许注释和尾逗号）。
/// </summary>
public sealed class Announcement
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// 公告 id。改内容时**一定要换个新 id**，软件靠它判断"这条弹过没有"。
    /// </summary>
    public string Id { get; set; } = "";

    public DateTime? UpdatedAt { get; set; }

    /// <summary>关掉就相当于没公告（留着内容下次再用）。</summary>
    public bool Enabled { get; set; } = true;

    public string Title { get; set; } = "";

    /// <summary>正文，写 \n 就能换行。</summary>
    public string Body { get; set; } = "";

    /// <summary>可选链接，界面上给一个「查看详情」按钮。</summary>
    public string? Link { get; set; }

    /// <summary>可选按钮文字（不写就显示「查看详情」）。</summary>
    public string? LinkText { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析公告；格式不对或内容为空返回 null（调用方什么都不做）。</summary>
    public static Announcement? TryParse(string json)
    {
        try
        {
            var announcement = JsonSerializer.Deserialize<Announcement>(json, Options);
            if (announcement is null || !announcement.Enabled)
            {
                return null;
            }

            // 没 id 或没正文的当没有公告处理：id 是"弹过没有"的依据，缺了会每次开机都弹。
            if (string.IsNullOrWhiteSpace(announcement.Id) ||
                (string.IsNullOrWhiteSpace(announcement.Title) && string.IsNullOrWhiteSpace(announcement.Body)))
            {
                return null;
            }

            return announcement;
        }
        catch
        {
            return null;
        }
    }
}
