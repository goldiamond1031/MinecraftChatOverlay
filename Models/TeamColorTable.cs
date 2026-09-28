using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinecraftChatOverlay.Models;

/// <summary>「队伍 → 颜色」表，以及把聊天文本解析成 (队伍, 玩家名) 的工具。</summary>
public static class TeamColorTable
{
    /// <summary>
    /// 队名 → 颜色。用的是 MC 标准颜色码的色值，所以和游戏内看起来一致。
    /// §c 红 / §9 蓝 / §a 绿 / §e 黄 / §b 青 / §5 紫 / §d 粉 / §f 白 / §8 黑 / §6 橙 / §7 灰
    /// </summary>
    public static readonly (string Team, string Color)[] Teams =
    {
        ("红队", "#FF5555"),
        ("蓝队", "#5555FF"),
        ("绿队", "#55FF55"),
        ("黄队", "#FFFF55"),
        ("青队", "#55FFFF"),
        ("紫队", "#AA00AA"),
        ("粉队", "#FF55FF"),
        ("白队", "#FFFFFF"),
        ("黑队", "#555555"),
        ("橙队", "#FFAA00"),
        ("灰队", "#AAAAAA")
    };

    /// <summary>
    /// 从整段文本里扫「X队 分隔符 玩家名」。
    /// 为什么不用"逐行判断行首"：模型经常把结果混在解释/思考文字里，
    /// 比如「- 第二行：白队 | JustReach（白色）」，行首根本不在队名上。
    /// 所以改成全文扫描；后面出现的覆盖前面的（模型的最终答案通常在最后）。
    /// </summary>
    private static readonly Regex PairPattern = new(
        @"(红|蓝|绿|黄|青|紫|粉|白|黑|橙|灰)队[ \t]*[|｜/:：,，、\-—–>＞《》「」【】()（）\[\]\{\}*]*[ \t]*([A-Za-z0-9_\u4e00-\u9fa5]{2,24})",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析出所有 (队伍, 玩家名)。
    /// 容错：队名和名字之间可以没有分隔符、可以是任意常见分隔符，
    /// 名字前面被 OCR 多认出来的"1"（其实是竖线）会被剥掉。
    /// </summary>
    public static List<(string Team, string Name)> Parse(string? text)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<(string, string)>();
        }

        // 先把每一行里的空格去掉：
        //   ① OCR「一 个 字 一 个 空 格」的输出才能认
        //   ② "Zhiyo 11" 这种被空格劈开的名字才能连回去
        // MC 的玩家名不含空格，所以这样做不会破坏真正的名字。
        var normalized = string.Join('\n', text
            .Split('\n')
            .Select(line => line.Replace(" ", "").Replace("\t", "")));

        foreach (Match match in PairPattern.Matches(normalized))
        {
            var team = match.Groups[1].Value + "队";
            var name = match.Groups[2].Value.Trim();

            // 「红队、蓝队、黄队」这种把队名当名字枚举的，跳过
            // （去掉空格后可能连成一串「蓝队黄队白队」，所以用 Contains 而不是相等）
            if (Teams.Any(x => name.Contains(x.Team, StringComparison.Ordinal)))
            {
                continue;
            }

            // 纯数字的名字（右边计分板上的分数）不要
            if (!name.Any(char.IsLetter))
            {
                continue;
            }

            // OCR 常把竖线认成 1：只在它后面不是 ASCII 名字字符时才剥
            if (name.Length >= 2 && name[0] == '1' && !IsAsciiNameChar(name[1]))
            {
                name = name[1..].Trim();
            }

            if (name.Length is >= 2 and <= 24)
            {
                found[name] = team;   // 后出现的覆盖前面的
            }
        }

        return found.Select(kv => (kv.Value, kv.Key)).ToList();
    }

    /// <summary>
    /// 玩家名里可能出现的字符，只看 ASCII。
    /// 注意不能用 char.IsLetterOrDigit —— 汉字也算 Letter，那样「1碎度了…」里的 1 就剥不掉了。
    /// </summary>
    private static bool IsAsciiNameChar(char c) =>
        c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' || c == '_';

    /// <summary>队名对应的颜色；不是队名就返回 null。</summary>
    public static string? ColorOf(string team) =>
        Teams.FirstOrDefault(t => string.Equals(t.Team, team, StringComparison.Ordinal)).Color;

    /// <summary>
    /// 生成"整词匹配"的正则：2B1 不会命中 2B12，也不会命中 12B1。
    /// （\p{L}\p{N} 同时覆盖英文、数字和汉字。）
    /// </summary>
    public static string BuildNamePattern(string name) =>
        $"(?<![\\p{{L}}\\p{{N}}_]){Regex.Escape(name)}(?![\\p{{L}}\\p{{N}}_])";
}
