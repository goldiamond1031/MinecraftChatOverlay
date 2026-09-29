using System.IO;
using System.Text;
using System.Text.Json;

namespace MinecraftChatOverlay.Services.Plugins.Market;

/// <summary>
/// 市场清单的本地缓存：断网、源抽风、GitHub 被墙时，至少还能看到上次的列表。
/// 存在 %AppData%\MinecraftChatOverlay\market\ 下，和插件目录分开。
/// </summary>
public static class PluginMarketCache
{
    private sealed class Meta
    {
        public string SourceUrl { get; set; } = "";
        public DateTime FetchedAt { get; set; }
    }

    private static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MinecraftChatOverlay", "market");

    private static string IndexPath => Path.Combine(Directory, "index.json");

    private static string MetaPath => Path.Combine(Directory, "index.meta.json");

    /// <summary>存原始 JSON（不做二次加工），顺带记来源和抓取时间。</summary>
    public static void Save(string rawJson, string sourceUrl, DateTime fetchedAt)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(IndexPath, rawJson, utf8);
            File.WriteAllText(MetaPath, JsonSerializer.Serialize(new Meta { SourceUrl = sourceUrl, FetchedAt = fetchedAt }), utf8);
        }
        catch
        {
            // 缓存写不进去不该影响主流程
        }
    }

    /// <summary>读缓存。没有/坏了都返回空，不抛。</summary>
    public static (MarketIndex? Index, string SourceUrl, DateTime? FetchedAt) TryLoad()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                return (null, "", null);
            }

            var json = File.ReadAllText(IndexPath, Encoding.UTF8);
            Meta? meta = null;
            if (File.Exists(MetaPath))
            {
                try
                {
                    meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath, Encoding.UTF8));
                }
                catch
                {
                    // meta 坏了不影响清单
                }
            }

            var index = MarketIndex.TryParse(json, meta?.SourceUrl ?? "");
            return (index, meta?.SourceUrl ?? "", meta?.FetchedAt);
        }
        catch
        {
            return (null, "", null);
        }
    }
}