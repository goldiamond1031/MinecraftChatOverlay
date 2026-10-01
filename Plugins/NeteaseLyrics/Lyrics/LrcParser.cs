using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftChatOverlay.Plugins.NeteaseLyrics.Lyrics;

/// <summary>逐字歌词的一个时间点：到 <paramref name="Time"/> 时，这一行已经唱到第 <paramref name="Chars"/> 个字（含）。</summary>
public sealed record KaraokeMark(double Time, int Chars);

/// <summary>一行歌词（时间 + 原文 + 翻译 + 罗马音 + 逐字时间轴）。</summary>
public sealed record LyricLine(
    TimeSpan Time,
    string Text,
    string? Translation,
    string? Romaji = null,
    IReadOnlyList<KaraokeMark>? Marks = null)
{
    public string Display => Translation is null ? Text : Text + "\n" + Translation;

    /// <summary>这一行有没有逐字时间轴。</summary>
    public bool HasKaraoke => Marks is { Count: > 1 };
}

/// <summary>
/// LRC 解析：支持一行多个时间标签、[mm:ss.xx] / [mm:ss:xx]，
/// 并把翻译（tlyric）、罗马音（romalrc）、逐字（klyric）按时间戳合进来。
/// </summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)>", RegexOptions.Compiled)]
    private static partial Regex InnerTag();

    /// <summary>yrc 行首时间标签：[毫秒,毫秒]</summary>
    [GeneratedRegex(@"^\[(\d+),(\d+)\]", RegexOptions.Compiled)]
    private static partial Regex YrcHead();

    public static List<LyricLine> Parse(string? lrc, string? translationLrc = null, string? romajiLrc = null, string? karaokeLrc = null, string? yrcLrc = null)
    {
        var items = new List<(TimeSpan Time, string Text)>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return new List<LyricLine>();
        }

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var matches = TimeTag().Matches(line);
            if (matches.Count == 0)
            {
                continue;
            }

            var text = TimeTag().Replace(line, "").Trim();
            if (text.Length == 0)
            {
                continue;
            }

            foreach (Match match in matches)
            {
                items.Add((TimeFromTag(match), text));
            }
        }

        if (items.Count == 0)
        {
            return new List<LyricLine>();
        }

        var translations = ParseAux(translationLrc);
        var romaji = ParseAux(romajiLrc);
        // 逐字优先用新的 yrc 格式，其次才是旧的 klyric
        var karaoke = ParseYrc(yrcLrc);
        if (karaoke.Count == 0)
        {
            karaoke = ParseKaraoke(karaokeLrc);
        }

        return items
            .OrderBy(item => item.Time)
            .Select(item =>
            {
                var key = (long)Math.Round(item.Time.TotalSeconds);
                karaoke.TryGetValue(key, out var marks);

                // 逐字的时间轴必须和这一行的文字对得上；对不上就当没有（避免错位染色）
                if (marks is not null && marks.Count > 0 && marks[^1].Chars != item.Text.Length)
                {
                    marks = null;
                }

                return new LyricLine(
                    item.Time,
                    item.Text,
                    translations.TryGetValue(key, out var translation) ? translation : null,
                    romaji.TryGetValue(key, out var r) ? r : null,
                    marks);
            })
            .ToList();
    }

    [GeneratedRegex(@"\((\d+),(\d+),\d+\)([^(]*)", RegexOptions.Compiled)]
    private static partial Regex YrcSegment();

    /// <summary>
    /// 新逐字格式（yrc）：一行形如
    ///   [行开始ms,行时长ms](字开始ms,字时长ms,0)文字(下一个字开始ms,…)文字…
    /// 解析成「到某个时间点已唱了多少个字」。
    /// </summary>
    private static Dictionary<long, List<KaraokeMark>> ParseYrc(string? yrcLrc)
    {
        var map = new Dictionary<long, List<KaraokeMark>>();
        if (string.IsNullOrWhiteSpace(yrcLrc))
        {
            return map;
        }

        foreach (var raw in yrcLrc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var head = YrcHead().Match(line);
            if (!head.Success)
            {
                continue;
            }

            // 行首是 [毫秒,毫秒]，用第一个数字（行开始时间）当这一行的 key
            var lineKey = (long)Math.Round(long.Parse(head.Groups[1].Value) / 1000.0);
            var body = line[head.Length..];

            var marks = new List<KaraokeMark>();
            var chars = 0;
            foreach (Match seg in YrcSegment().Matches(body))
            {
                var startMs = long.Parse(seg.Groups[1].Value);

                // 这个标记出现时，前面那些字已经唱完了
                marks.Add(new KaraokeMark(startMs / 1000.0, chars));
                chars += seg.Groups[3].Value.Length;
            }

            if (chars == 0)
            {
                continue;
            }

            marks.Insert(0, new KaraokeMark(marks[0].Time, 0));
            marks.Add(new KaraokeMark(double.MaxValue, chars));   // 行尾
            map[lineKey] = marks;
        }

        return map;
    }
    /// <summary>翻译 / 罗马音都是「同样时间戳 + 文本」的 LRC，共用一套解析。</summary>
    private static Dictionary<long, string> ParseAux(string? lrc)
    {
        var dict = new Dictionary<long, string>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return dict;
        }

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = TimeTag().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var text = TimeTag().Replace(line, "").Trim();
            if (text.Length == 0)
            {
                continue;
            }

            dict[(long)Math.Round(TimeFromTag(match).TotalSeconds)] = text;
        }

        return dict;
    }

    /// <summary>
    /// 逐字：一行形如 [00:12.34]字&lt;00:12.50&gt;词&lt;00:12.72&gt;…
    /// 解析成「到某个时间点已唱了多少个字」。
    /// </summary>
    private static Dictionary<long, List<KaraokeMark>> ParseKaraoke(string? karaokeLrc)
    {
        var map = new Dictionary<long, List<KaraokeMark>>();
        if (string.IsNullOrWhiteSpace(karaokeLrc))
        {
            return map;
        }

        foreach (var raw in karaokeLrc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var head = TimeTag().Match(line);
            if (!head.Success)
            {
                continue;
            }

            var lineKey = (long)Math.Round(TimeFromTag(head).TotalSeconds);
            var body = line[head.Length..];

            var marks = new List<KaraokeMark>();
            var builder = new StringBuilder();
            var chars = 0;
            var index = 0;

            while (index < body.Length)
            {
                if (body[index] == '<')
                {
                    var end = body.IndexOf('>', index);
                    if (end < 0)
                    {
                        break;
                    }

                    var stamp = body.Substring(index + 1, end - index - 1);
                    var inner = InnerTag().Match("<" + stamp + ">");
                    if (inner.Success)
                    {
                        chars += builder.Length;
                        builder.Clear();
                        marks.Add(new KaraokeMark(SecondsFromParts(inner.Groups[1].Value, inner.Groups[2].Value), chars));
                    }

                    index = end + 1;
                }
                else
                {
                    builder.Append(body[index]);
                    index++;
                }
            }

            var tail = builder.ToString().Trim();
            if (tail.Length > 0)
            {
                chars += tail.Length;
            }

            if (chars == 0)
            {
                continue;
            }

            if (marks.Count > 0)
            {
                marks.Insert(0, new KaraokeMark(marks[0].Time, 0));   // 第一个字之前算"还没唱"
            }

            // 行尾补一个终标记：没有它，最后几个字永远染不上色，
            // 也会被"逐字字数必须和整行文字长度一致"的校验判定为坏了、整行丢掉。
            marks.Add(new KaraokeMark(double.MaxValue, chars));
            map[lineKey] = marks;
        }

        return map;
    }

    private static TimeSpan TimeFromTag(Match match) =>
        new(0, 0, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), Milliseconds(match.Groups[3].Value));

    private static double SecondsFromParts(string minutes, string rest)
    {
        var parts = rest.Split('.', ':');
        var seconds = int.Parse(parts[0]);
        var ms = parts.Length > 1 ? Milliseconds(parts[1]) : 0;
        return int.Parse(minutes) * 60 + seconds + ms / 1000.0;
    }

    private static int Milliseconds(string fraction)
    {
        if (string.IsNullOrEmpty(fraction))
        {
            return 0;
        }

        return fraction.Length switch
        {
            1 => int.Parse(fraction) * 100,
            2 => int.Parse(fraction) * 10,
            _ => int.Parse(fraction.Length >= 3 ? fraction[..3] : fraction),
        };
    }

    /// <summary>当前应该显示第几行（返回下标，-1 表示还没到第一行）。</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, double positionSeconds)
    {
        if (lines.Count == 0)
        {
            return -1;
        }

        var index = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time.TotalSeconds <= positionSeconds + 0.05)
            {
                index = i;
            }
            else
            {
                break;
            }
        }

        return index;
    }

    /// <summary>某一行的逐字进度：当前唱到第几个字（0 = 还没开始，文字长度 = 唱完）。</summary>
    public static int SungChars(LyricLine line, double positionSeconds)
    {
        if (!line.HasKaraoke || line.Marks is null)
        {
            return -1;
        }

        var sung = 0;
        foreach (var mark in line.Marks)
        {
            if (mark.Time <= positionSeconds + 0.02)
            {
                sung = mark.Chars;
            }
            else
            {
                break;
            }
        }

        return Math.Clamp(sung, 0, line.Text.Length);
    }
}
