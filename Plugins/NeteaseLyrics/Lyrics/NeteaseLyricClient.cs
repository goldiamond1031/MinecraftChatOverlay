using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftChatOverlay.Plugins.NeteaseLyrics.Lyrics;

/// <summary>一首歌的歌词。</summary>
public sealed record LyricDocument(
    string SongId,
    string Title,
    string Artist,
    string Album,
    IReadOnlyList<LyricLine> Lines,
    bool HasTranslation);

/// <summary>
/// 网易云歌词接口客户端（公开的 web 接口，不需要登录）。
///
/// 流程：歌名 + 歌手 一起搜 → 挑最匹配的一条（防止命中翻唱）→ 取 lrc + tlyric。
/// 结果按歌 id 缓存在插件自己的目录里，同一首歌不重复请求。
/// </summary>
public sealed class NeteaseLyricClient : IDisposable
{
    private const string SearchUrl = "https://music.163.com/api/search/get/web";
    private const string LyricUrl = "https://music.163.com/api/song/lyric";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly Dictionary<string, LyricDocument?> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cacheDirectory;

    public NeteaseLyricClient(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        PurgeOutdatedCache();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    /// <summary>
    /// 缓存格式版本：只要 LyricLine 里"要缓存的东西"变了（比如补上 Marks 逐字 / Romaji 罗马音），
    /// 就把这个数字 +1 —— 老缓存会被直接清掉重取。
    /// 不然用户会一直拿着几个月前写下的老数据：没罗马音、没逐字，而且缓存是永不过期的。
    /// </summary>
    private const int CacheSchema = 2;

    /// <summary>缓存格式对不上就整目录清掉（缓存而已，重取一次就好）。</summary>
    private void PurgeOutdatedCache()
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var stamp = Path.Combine(_cacheDirectory, "schema.txt");
            if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == CacheSchema.ToString())
            {
                return;
            }

            foreach (var file in Directory.GetFiles(_cacheDirectory, "*.json"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                }
            }

            File.WriteAllText(stamp, CacheSchema.ToString());
        }
        catch
        {
        }
    }


    /// <summary>按歌名 + 歌手找歌词；找不到返回 null。不会抛异常。</summary>
    public async Task<LyricDocument?> FindAsync(string title, string? artist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var key = (title.Trim() + "|" + (artist ?? "").Trim()).ToLowerInvariant();
        if (_memory.TryGetValue(key, out var cached))
        {
            return cached;
        }

        LyricDocument? document = null;
        try
        {
            document = await FindFromDiskAsync(key, token).ConfigureAwait(false)
                       ?? await FindFromNetworkAsync(title.Trim(), artist?.Trim(), token).ConfigureAwait(false);
        }
        catch
        {
            document = null;
        }

        _memory[key] = document;
        return document;
    }

    private async Task<LyricDocument?> FindFromNetworkAsync(string title, string? artist, CancellationToken token)
    {
        var keyword = string.IsNullOrWhiteSpace(artist) ? title : title + " " + artist;
        var search = $"{SearchUrl}?csrf_token=&s={Uri.EscapeDataString(keyword)}&type=1&offset=0&total=true&limit=10";
        var searchJson = await _http.GetStringAsync(search, token).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<SearchResponse>(searchJson, JsonOptions);
        var songs = result?.Result?.Songs;
        if (songs is null || songs.Count == 0)
        {
            return null;
        }

        var best = PickBest(songs, title, artist);
        if (best is null)
        {
            return null;
        }

        var lyricJson = await _http.GetStringAsync(
            $"{LyricUrl}?id={best.Id}&lv=-1&kv=-1&tv=-1&rv=-1&yv=-1", token).ConfigureAwait(false);
        var lyric = JsonSerializer.Deserialize<LyricResponse>(lyricJson, JsonOptions);

        var lines = LrcParser.Parse(lyric?.Lrc?.Lyric, lyric?.Tlyric?.Lyric, lyric?.Romalrc?.Lyric, lyric?.Klyric?.Lyric, lyric?.Yrc?.Lyric);
        if (lines.Count == 0)
        {
            return null;
        }

        var document = new LyricDocument(
            best.Id.ToString(),
            best.Name ?? title,
            best.Artists is { Count: > 0 } ? string.Join(" / ", best.Artists.Select(a => a.Name)) : artist ?? "",
            best.Album?.Name ?? "",
            lines,
            lines.Any(l => l.Translation is not null));

        await SaveToDiskAsync(best.Id.ToString(), document, token).ConfigureAwait(false);
        return document;
    }

    private static Song? PickBest(List<Song> songs, string title, string? artist)
    {
        Song? fallback = null;
        foreach (var song in songs)
        {
            if (song.Id <= 0)
            {
                continue;
            }

            var sameTitle = Normalize(song.Name) == Normalize(title);
            var songArtists = song.Artists is { Count: > 0 }
                ? string.Join(" / ", song.Artists.Select(a => a.Name))
                : "";

            if (!sameTitle)
            {
                continue;
            }

            fallback ??= song;

            if (string.IsNullOrWhiteSpace(artist))
            {
                return song;
            }

            var wanted = Normalize(artist);
            var got = Normalize(songArtists);
            if (got.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(got, StringComparison.Ordinal))
            {
                return song;
            }
        }

        return fallback;
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (!char.IsWhiteSpace(ch) && ch != '(' && ch != ')' && ch != '（' && ch != '）' && ch != '[' && ch != ']')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private string CachePath(string id) => Path.Combine(_cacheDirectory, id + ".json");

    private async Task<LyricDocument?> FindFromDiskAsync(string key, CancellationToken token)
    {
        // 内存缓存已经查过；磁盘缓存按"歌名|歌手"再建一个索引文件，避免重复搜索
        var index = Path.Combine(_cacheDirectory, "index.json");
        try
        {
            if (!File.Exists(index))
            {
                return null;
            }

            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(index, token).ConfigureAwait(false));
            if (map is null || !map.TryGetValue(key, out var id) || !File.Exists(CachePath(id)))
            {
                return null;
            }

            var cached = JsonSerializer.Deserialize<CachedLyric>(await File.ReadAllTextAsync(CachePath(id), token).ConfigureAwait(false));
            if (cached is null || cached.Lines.Count == 0)
            {
                return null;
            }

            return new LyricDocument(
                cached.SongId, cached.Title, cached.Artist, cached.Album,
                cached.Lines.Select(Resurrect).ToList(), cached.HasTranslation);
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveToDiskAsync(string id, LyricDocument document, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var payload = new CachedLyric(
                document.SongId,
                document.Title,
                document.Artist,
                document.Album,
                document.HasTranslation,
                document.Lines.Select(l => new CachedLine(l.Time.TotalSeconds, l.Text, l.Translation, l.Romaji, l.Marks?.ToList())).ToList());
            await File.WriteAllTextAsync(
                CachePath(id),
                JsonSerializer.Serialize(payload),
                token).ConfigureAwait(false);

            var index = Path.Combine(_cacheDirectory, "index.json");
            var map = new Dictionary<string, string>();
            if (File.Exists(index))
            {
                try
                {
                    map = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(index, token).ConfigureAwait(false)) ?? new();
                }
                catch
                {
                    map = new Dictionary<string, string>();
                }
            }

            map[(document.Title + "|" + document.Artist).ToLowerInvariant()] = id;
            await File.WriteAllTextAsync(index, JsonSerializer.Serialize(map), token).ConfigureAwait(false);
        }
        catch
        {
            // 缓存写不进去不该影响播放
        }
    }

    private static LyricLine Resurrect(CachedLine line) =>
        new(TimeSpan.FromSeconds(line.Time), line.Text, line.Translation, line.Romaji, line.Marks);

    public void Dispose() => _http.Dispose();

    // ---- 接口 DTO ----
    private sealed class SearchResponse
    {
        [JsonPropertyName("result")] public SearchResult? Result { get; set; }
    }

    private sealed class SearchResult
    {
        [JsonPropertyName("songs")] public List<Song>? Songs { get; set; }
    }

    private sealed class Song
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("artists")] public List<Artist>? Artists { get; set; }
        [JsonPropertyName("album")] public Album? Album { get; set; }
    }

    private sealed class Artist
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class Album
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class LyricResponse
    {
        [JsonPropertyName("lrc")] public LyricText? Lrc { get; set; }
        [JsonPropertyName("tlyric")] public LyricText? Tlyric { get; set; }
        [JsonPropertyName("romalrc")] public LyricText? Romalrc { get; set; }
        [JsonPropertyName("klyric")] public LyricText? Klyric { get; set; }
        [JsonPropertyName("yrc")] public LyricText? Yrc { get; set; }
    }

    private sealed class LyricText
    {
        [JsonPropertyName("lyric")] public string? Lyric { get; set; }
    }

    private sealed record CachedLine(double Time, string Text, string? Translation, string? Romaji, List<KaraokeMark>? Marks);

    private sealed record CachedLyric(
        string SongId, string Title, string Artist, string Album, bool HasTranslation, List<CachedLine> Lines);
}
