using System.Globalization;
using System.Text.RegularExpressions;

namespace MinecraftChatOverlay.Plugins.NetEaseLyric;

/// <summary>一行歌词。</summary>
/// <param name="Time">这句开始的时间。</param>
/// <param name="Text">原文。</param>
/// <param name="Translation">官方翻译（可能为空）。</param>
public sealed record LyricLine(TimeSpan Time, string Text, string Translation);

/// <summary>
/// LRC 解析 + 翻译合并。
///
/// 网易云返回的 LRC 长这样（时间戳 + 内容）：
///     [00:00.00] 作词 : Aimer
///     [00:21.34]誰が袖に咲く幻花（げんか）
/// 外加一份 tlyric，时间戳和原文一一对应，是官方翻译。
/// </summary>
public static class LrcParser
{
    /// <summary>匹配一行前面的时间戳，支持 [mm:ss]、[mm:ss.xx]、[mm:ss.xxx]，一行可以有多个。</summary>
    private static readonly Regex TimeTagRegex = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    /// <summary>
    /// 制作信息行。网易云会把「作词 : Aimer」这种也带上时间戳塞进 LRC 里，
    /// 不滤掉的话开头几秒会显示"作词 : xxx"，很难看。
    /// </summary>
    private static readonly Regex CreditRegex = new(
        @"^\s*(作词|作曲|编曲|混音|母带|制作人|监制|出品|录音|和声|配唱|吉他|贝斯|鼓|键盘|弦乐|管乐|人声|统筹|策划|发行|插画|设计|OP|SP|词|曲)\s*[:：]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>解析 LRC 文本；<paramref name="translationLrc"/> 是同格式的翻译，可空。</summary>
    public static List<LyricLine> Parse(string? lrc, string? translationLrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return new List<LyricLine>();
        }

        var translations = new Dictionary<TimeSpan, string>();
        foreach (var (time, text) in ParseRaw(translationLrc))
        {
            if (text.Length > 0)
            {
                translations[time] = text;
            }
        }

        var lines = new List<LyricLine>();

        foreach (var (time, text) in ParseRaw(lrc))
        {
            if (text.Length == 0 || CreditRegex.IsMatch(text))
            {
                continue;
            }

            translations.TryGetValue(time, out var translation);
            lines.Add(new LyricLine(time, text, translation ?? ""));
        }

        // 时间升序（LRC 一般本来就是，但不保证）；同时间的只留第一条
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));

        var result = new List<LyricLine>(lines.Count);
        foreach (var line in lines)
        {
            if (result.Count > 0 && result[^1].Time == line.Time)
            {
                continue;
            }

            result.Add(line);
        }

        return result;
    }

    /// <summary>把 LRC 拆成 (时间, 文本) 列表；一行多个时间戳就展开成多条。</summary>
    private static List<(TimeSpan Time, string Text)> ParseRaw(string? lrc)
    {
        var list = new List<(TimeSpan, string)>();
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return list;
        }

        foreach (var rawLine in lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var matches = TimeTagRegex.Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            // 内容 = 最后一个时间戳之后的部分
            var text = rawLine[(matches[^1].Index + matches[^1].Length)..].Trim();

            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fractionText = match.Groups[3].Value;

                var milliseconds = fractionText.Length switch
                {
                    0 => 0,
                    1 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 10,
                    _ => int.Parse(fractionText[..3], CultureInfo.InvariantCulture),
                };

                var time = new TimeSpan(0, 0, minutes, seconds, milliseconds);
                list.Add((time, text));
            }
        }

        return list;
    }

    /// <summary>
    /// 找出"当前时间该显示第几句"。
    /// 返回 -1 表示还没到第一句（前奏）。
    /// </summary>
    public static int FindIndex(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        if (lines.Count == 0)
        {
            return -1;
        }

        var low = 0;
        var high = lines.Count - 1;
        var found = -1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (lines[mid].Time <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }
}
