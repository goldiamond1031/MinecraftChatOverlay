using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.ViewModels;

namespace MinecraftChatOverlay.Services;

/// <summary>聊天文本处理：自动替换、屏蔽、彩色分段渲染。</summary>
public static class ChatTextProcessor
{
    public static string ApplyReplacements(string text, IReadOnlyList<TextReplaceRule> rules)
    {
        if (rules == null || rules.Count == 0)
        {
            return text;
        }

        foreach (var rule in rules)
        {
            if (!rule.IsEnabled || string.IsNullOrEmpty(rule.FindText))
            {
                continue;
            }

            if (rule.OnlyPlayerContent)
            {
                if (TrySplitPlayerSpeech(text, out var prefix, out var separator, out var content))
                {
                    var replacedContent = rule.UseRegex
                        ? SafeRegexReplace(content, rule.FindText, rule.ReplaceText)
                        : content.Replace(rule.FindText, rule.ReplaceText, StringComparison.OrdinalIgnoreCase);
                    text = prefix + " " + separator + " " + replacedContent;
                }
            }
            else
            {
                text = rule.UseRegex
                    ? SafeRegexReplace(text, rule.FindText, rule.ReplaceText)
                    : text.Replace(rule.FindText, rule.ReplaceText, StringComparison.OrdinalIgnoreCase);
            }
        }

        return text;
    }

    private static string SafeRegexReplace(string input, string pattern, string replacement)
    {
        try
        {
            return Regex.Replace(input, pattern, replacement, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            // 用户输入的正则可能不合法，此时保持原文，不让程序崩溃。
            return input;
        }
    }

    /// <summary>
    /// 尝试识别玩家发言格式：
    /// [vip1][Lv.11]玩家名 &gt; 内容
    /// 或 玩家名&gt;&gt;内容
    /// 返回前缀（不含分隔符）、分隔符（&gt; 或 &gt;&gt; 等）和内容。
    /// </summary>
    public static bool TrySplitPlayerSpeech(string text, out string prefix, out string separator, out string content)
    {
        prefix = string.Empty;
        separator = string.Empty;
        content = string.Empty;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var index = text.IndexOfAny(new[] { '>', '＞' });
        if (index < 0)
        {
            return false;
        }

        var end = index;
        while (end < text.Length && (text[end] == '>' || text[end] == '＞'))
        {
            end++;
        }

        prefix = text[..index].TrimEnd();
        separator = text[index..end];
        content = text[end..].TrimStart();
        return content.Length > 0;
    }

    public static bool IsBlocked(string text, IReadOnlyList<string> keywords)
    {
        if (keywords == null || keywords.Count == 0)
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            if (!string.IsNullOrEmpty(keyword) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static List<ChatSegmentViewModel> BuildColoredSegments(
        string text,
        IReadOnlyList<TextColorRule> rules,
        Brush defaultForeground,
        FontWeight defaultFontWeight)
    {
        var segments = new List<ChatSegmentViewModel>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        if (rules == null || rules.Count == 0)
        {
            segments.Add(new ChatSegmentViewModel(text, defaultForeground, defaultFontWeight));
            return segments;
        }

        var colorMatches = new List<ColorMatch>();
        foreach (var rule in rules)
        {
            if (!rule.IsEnabled || string.IsNullOrEmpty(rule.Text))
            {
                continue;
            }

            if (rule.UseRegex)
            {
                try
                {
                    var regex = new Regex(rule.Text, RegexOptions.IgnoreCase);
                    foreach (Match match in regex.Matches(text))
                    {
                        var groupIndex = Math.Max(0, rule.RegexGroup);
                        if (groupIndex >= match.Groups.Count)
                        {
                            groupIndex = 0;
                        }

                        var group = match.Groups[groupIndex];
                        if (!group.Success || group.Length <= 0)
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(rule.MatchColor) && groupIndex > 0)
                        {
                            // 整句染色：高亮组前面部分
                            if (group.Index > match.Index)
                            {
                                colorMatches.Add(new ColorMatch(match.Index, group.Index - match.Index, rule, rule.MatchColor));
                            }

                            // 高亮组本身
                            colorMatches.Add(new ColorMatch(group.Index, group.Length, rule, rule.Color));

                            // 整句染色：高亮组后面部分
                            var matchEnd = match.Index + match.Length;
                            var groupEnd = group.Index + group.Length;
                            if (groupEnd < matchEnd)
                            {
                                colorMatches.Add(new ColorMatch(groupEnd, matchEnd - groupEnd, rule, rule.MatchColor));
                            }
                        }
                        else
                        {
                            colorMatches.Add(new ColorMatch(group.Index, group.Length, rule, rule.Color));
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // 正则不合法时跳过该规则，不让程序崩溃。
                }
            }
            else
            {
                var index = 0;
                while ((index = text.IndexOf(rule.Text, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    colorMatches.Add(new ColorMatch(index, rule.Text.Length, rule, rule.Color));
                    index += rule.Text.Length;
                }
            }
        }

        if (colorMatches.Count == 0)
        {
            segments.Add(new ChatSegmentViewModel(text, defaultForeground, defaultFontWeight));
            return segments;
        }

        // 按位置排序，重叠时优先显示先出现/更长的匹配。
        colorMatches.Sort((a, b) =>
        {
            var byStart = a.Start.CompareTo(b.Start);
            return byStart != 0 ? byStart : b.Length.CompareTo(a.Length);
        });

        var position = 0;
        foreach (var match in colorMatches)
        {
            if (match.Start < position)
            {
                continue;
            }

            if (match.Start > position)
            {
                segments.Add(new ChatSegmentViewModel(
                    text[position..match.Start],
                    defaultForeground,
                    defaultFontWeight));
            }

            if (match.Length > 0)
            {
                var coloredSegment = new ChatSegmentViewModel(
                    text.Substring(match.Start, match.Length),
                    ParseBrush(match.ColorHex, defaultForeground),
                    ParseFontWeight(match.Rule.FontWeight, defaultFontWeight))
                {
                    IsUserColored = true
                };
                segments.Add(coloredSegment);
            }

            position = match.Start + match.Length;
        }

        if (position < text.Length)
        {
            segments.Add(new ChatSegmentViewModel(
                text[position..],
                defaultForeground,
                defaultFontWeight));
        }

        return segments;
    }

    /// <summary>
    /// 在已经按用户彩色规则分段的基础上，再把“玩家发言内容”和“发言玩家ID”设置为指定颜色。
    /// 用户彩色规则主动染色的片段不会被覆盖。
    /// </summary>
    public static List<ChatSegmentViewModel> ApplyPlayerColorOverrides(
        IReadOnlyList<ChatSegmentViewModel> source,
        string text,
        Brush defaultForeground,
        Brush? playerContentBrush,
        Brush? playerNameBrush)
    {
        if (playerContentBrush == null && playerNameBrush == null)
        {
            return source.ToList();
        }

        var separatorIndex = text.IndexOfAny(new[] { '>', '＞' });
        if (separatorIndex < 0)
        {
            return source.ToList();
        }

        var ranges = new List<(int Start, int End, Brush Brush)>();
        if (playerContentBrush != null)
        {
            ranges.Add((separatorIndex, text.Length, playerContentBrush));
        }

        if (playerNameBrush != null)
        {
            var header = text[..separatorIndex].TrimEnd();
            var nameStart = FindTrailingPlayerNameStart(header);
            if (nameStart >= 0 && nameStart < header.Length)
            {
                ranges.Add((nameStart, header.Length, playerNameBrush));
            }
        }

        if (ranges.Count == 0)
        {
            return source.ToList();
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

        var result = new List<ChatSegmentViewModel>();
        var offset = 0;
        foreach (var segment in source)
        {
            if (string.IsNullOrEmpty(segment.Text))
            {
                continue;
            }

            if (segment.IsUserColored)
            {
                result.Add(segment);
                offset += segment.Text.Length;
                continue;
            }

            var segmentStart = offset;
            var segmentEnd = offset + segment.Text.Length;
            var cursor = segmentStart;

            foreach (var range in ranges)
            {
                var rangeStart = Math.Max(cursor, range.Start);
                var rangeEnd = Math.Min(segmentEnd, range.End);
                if (rangeEnd <= rangeStart)
                {
                    continue;
                }

                if (rangeStart > cursor)
                {
                    result.Add(new ChatSegmentViewModel(
                        text[cursor..rangeStart],
                        defaultForeground,
                        segment.FontWeight,
                        segment.FontStyle));
                }

                result.Add(new ChatSegmentViewModel(
                    text[rangeStart..rangeEnd],
                    range.Brush,
                    segment.FontWeight,
                    segment.FontStyle));

                cursor = rangeEnd;
            }

            if (cursor < segmentEnd)
            {
                result.Add(new ChatSegmentViewModel(
                    text[cursor..segmentEnd],
                    defaultForeground,
                    segment.FontWeight,
                    segment.FontStyle));
            }

            offset = segmentEnd;
        }

        return result;
    }

    private static int FindTrailingPlayerNameStart(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return -1;
        }

        // 常见格式：[前缀]ID，ID 是最后一个 ] 后面的部分。
        var lastBracket = header.LastIndexOf(']');
        if (lastBracket >= 0 && lastBracket + 1 < header.Length)
        {
            return lastBracket + 1;
        }

        // 如果没有括号，则取最后一个空格后的部分作为 ID。
        var lastSpace = header.LastIndexOf(' ');
        return lastSpace + 1;
    }

    private sealed class ColorMatch
    {
        public int Start { get; }

        public int Length { get; }

        public TextColorRule Rule { get; }

        public string ColorHex { get; }

        public ColorMatch(int start, int length, TextColorRule rule, string colorHex)
        {
            Start = start;
            Length = length;
            Rule = rule;
            ColorHex = colorHex;
        }
    }

    private static Brush ParseBrush(string? value, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            var brush = new BrushConverter().ConvertFromString(value) as Brush;
            return brush ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static FontWeight ParseFontWeight(string? value, FontWeight fallback)
    {
        return value?.Trim() switch
        {
            "Thin" => FontWeights.Thin,
            "Light" => FontWeights.Light,
            "SemiBold" => FontWeights.SemiBold,
            "Bold" => FontWeights.Bold,
            _ => fallback
        };
    }
}
