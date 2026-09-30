using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MinecraftChatOverlay.Plugins.NetEaseLyric;

/// <summary>一首歌的歌词（含来源信息）。<c>Ok=false</c> 表示这首歌确实没有歌词。</summary>
public sealed record LyricDocument(
    bool Ok,
    long SongId,
    string SongName,
    string Artist,
    string Lrc,
    string TranslationLrc)
{
    public static LyricDocument None(long songId = 0, string songName = "", string artist = "") =>
        new(false, songId, songName, artist, "", "");
}

/// <summary>
/// 网易云公开接口客户端：搜歌 → 取歌词。
///
/// 两个接口都不需要登录：
///   搜歌 POST https://music.163.com/api/search/get/web   （s=关键词&amp;type=1&amp;offset=0&amp;limit=N）
///   歌词 GET  https://music.163.com/api/song/lyric?id=X&amp;lv=-1&amp;kv=-1&amp;tv=-1
/// 都要带一个像浏览器的 UA 和 Referer，否则容易被挡。
///
/// 结果按「歌名+歌手」缓存到插件目录 cache\ 下：同一首歌不重复联网，
/// 没歌词的也缓存（记一条 negative），免得每次切回来都白跑一趟。
/// </summary>
public sealed class NeteaseLyricClient
{
    private const string SearchUrl = "https://music.163.com/api/search/get/web";
    private const string LyricUrl = "https://music.163.com/api/song/lyric";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateHttp();

    private readonly string _cacheDirectory;

    public NeteaseLyricClient(string pluginDirectory)
    {
        _cacheDirectory = Path.Combine(pluginDirectory, "cache");
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
        catch
        {
            // 缓存目录建不出来就算了，退化成不缓存
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");
        return http;
    }

    /// <summary>
    /// 拿一首歌的歌词（先查缓存）。失败返回 null（网络问题，调用方可以下次再试）；
    /// 确定"没有歌词"返回 <see cref="LyricDocument.None"/>。
    /// </summary>
    public async Task<LyricDocument?> GetAsync(string title, string artist, CancellationToken token)
    {
        var cachePath = CachePath(title, artist);
        var cached = TryLoadCache(cachePath);
        if (cached is not null)
        {
            return cached;
        }

        try
        {
            var songId = await SearchSongIdAsync(title, artist, token).ConfigureAwait(false);
            if (songId <= 0)
            {
                return null; // 搜不到：可能是网络问题，也可能是歌名对不上 —— 不写负缓存
            }

            var (lrc, translation, ok) = await FetchLyricAsync(songId, token).ConfigureAwait(false);
            var document = ok
                ? new LyricDocument(true, songId, title, artist, lrc, translation)
                : LyricDocument.None(songId, title, artist);

            SaveCache(cachePath, document);
            return document;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 直接按网易云歌曲 id 取歌词（本地桥接会给 id，比按歌名搜更准）。先查缓存。
    /// </summary>
    public async Task<LyricDocument?> GetBySongIdAsync(long songId, string title, string artist, CancellationToken token)
    {
        if (songId <= 0)
        {
            return await GetAsync(title, artist, token).ConfigureAwait(false);
        }

        var cachePath = CachePathById(songId);
        var cached = TryLoadCache(cachePath);
        if (cached is not null)
        {
            return cached;
        }

        try
        {
            var (lrc, translation, ok) = await FetchLyricAsync(songId, token).ConfigureAwait(false);
            var document = ok
                ? new LyricDocument(true, songId, title, artist, lrc, translation)
                : LyricDocument.None(songId, title, artist);

            SaveCache(cachePath, document);
            return document;
        }
        catch
        {
            return null;
        }
    }

    private string CachePathById(long songId) => Path.Combine(_cacheDirectory, $"id_{songId}.json");

    // ------------------------------------------------------------ 搜索

    private static async Task<long> SearchSongIdAsync(string title, string artist, CancellationToken token)
    {
        var keyword = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["s"] = keyword,
            ["type"] = "1",
            ["offset"] = "0",
            ["limit"] = "10",
        });

        using var response = await Http.PostAsync(SearchUrl, form, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        long bestId = 0;
        var bestScore = int.MinValue;

        foreach (var song in songs.EnumerateArray())
        {
            var id = song.TryGetProperty("id", out var idElement) ? idElement.GetInt64() : 0;
            if (id <= 0)
            {
                continue;
            }

            var name = song.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
            var songArtists = song.TryGetProperty("artists", out var artistsElement) && artistsElement.ValueKind == JsonValueKind.Array
                ? string.Join("/", artistsElement.EnumerateArray()
                    .Select(a => a.TryGetProperty("name", out var an) ? an.GetString() ?? "" : ""))
                : "";

            var score = ScoreMatch(title, artist, name, songArtists);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = id;
            }
        }

        // 分数太低说明搜出来的都不是同一首歌（比如只唱了一句的翻唱），宁可不用
        return bestScore >= 60 ? bestId : 0;
    }

    /// <summary>
    /// 给候选打匹配分：歌名完全一致 100 分，包含关系 70 分；歌手对得上再加 30 分。
    /// SMTC 里的歌名有时带「(Live)」「- 现场版」之类后缀，所以退一步用包含判断。
    /// </summary>
    private static int ScoreMatch(string wantTitle, string wantArtist, string gotTitle, string gotArtists)
    {
        var a = Normalize(wantTitle);
        var b = Normalize(gotTitle);

        int score;
        if (a.Length > 0 && a == b)
        {
            score = 100;
        }
        else if (a.Length > 0 && (b.Contains(a, StringComparison.Ordinal) || a.Contains(b, StringComparison.Ordinal)))
        {
            score = 70;
        }
        else
        {
            score = 0;
        }

        var wanted = SplitArtists(wantArtist);
        var got = SplitArtists(gotArtists);
        if (wanted.Count > 0 && got.Count > 0 && wanted.Any(w => got.Any(g =>
                g.Contains(w, StringComparison.Ordinal) || w.Contains(g, StringComparison.Ordinal))))
        {
            score += 30;
        }

        return score;
    }

    /// <summary>歌名归一化：去掉括号备注、空白、大小写差异，方便比对。</summary>
    private static string Normalize(string text)
    {
        var builder = new StringBuilder();
        var depth = 0;

        foreach (var ch in text)
        {
            if (ch is '（' or '(' or '[' or '【')
            {
                depth++;
                continue;
            }

            if (ch is '）' or ')' or ']' or '】')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (depth > 0 || char.IsWhiteSpace(ch))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString().Trim();
    }

    private static List<string> SplitArtists(string artists) =>
        artists.Split(new[] { '/', ',', '、', '&', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Normalize(s))
            .Where(s => s.Length > 0)
            .ToList();

    // ------------------------------------------------------------ 歌词

    private static async Task<(string Lrc, string Translation, bool Ok)> FetchLyricAsync(long songId, CancellationToken token)
    {
        var url = $"{LyricUrl}?id={songId}&lv=-1&kv=-1&tv=-1";
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ("", "", false);
        }

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        // 纯音乐 / 还没收录歌词：网易云会给 nolyric / uncollected = true
        if (doc.RootElement.TryGetProperty("nolyric", out var noLyric) && noLyric.ValueKind == JsonValueKind.True)
        {
            return ("", "", false);
        }

        if (doc.RootElement.TryGetProperty("uncollected", out var uncollected) && uncollected.ValueKind == JsonValueKind.True)
        {
            return ("", "", false);
        }

        var lrc = ReadLyricText(doc.RootElement, "lrc");
        var translation = ReadLyricText(doc.RootElement, "tlyric");
        return (lrc, translation, !string.IsNullOrWhiteSpace(lrc));
    }

    private static string ReadLyricText(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("lyric", out var lyric) ||
            lyric.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return lyric.GetString() ?? "";
    }

    // ------------------------------------------------------------ 缓存

    private sealed record CacheRecord(
        bool Ok,
        long SongId,
        string Lrc,
        string TranslationLrc,
        DateTime FetchedAt);

    private string CachePath(string title, string artist)
    {
        var key = title + "\u0001" + artist;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var safe = new string(key.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).Take(40).ToArray());
        return Path.Combine(_cacheDirectory, $"{safe}_{hash}.json");
    }

    private static LyricDocument? TryLoadCache(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // 缓存放一周就够了：歌词可能被修正，留太久反而看到旧版
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(7))
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<CacheRecord>(File.ReadAllText(path));
            if (record is null)
            {
                return null;
            }

            return record.Ok
                ? new LyricDocument(true, record.SongId, "", "", record.Lrc, record.TranslationLrc)
                : LyricDocument.None(record.SongId);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(string path, LyricDocument document)
    {
        try
        {
            var record = new CacheRecord(document.Ok, document.SongId, document.Lrc, document.TranslationLrc, DateTime.UtcNow);
            File.WriteAllText(path, JsonSerializer.Serialize(record));
        }
        catch
        {
            // 缓存写失败不影响使用
        }
    }

    /// <summary>清掉所有缓存（插件页上的「清空歌词缓存」按钮用）。</summary>
    public int ClearCache()
    {
        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "*.json");
            foreach (var file in files)
            {
                File.Delete(file);
            }

            return files.Length;
        }
        catch
        {
            return 0;
        }
    }
}
