using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MinecraftChatOverlay.Services.KillFeedback;

/// <summary>
/// 击杀匹配：把"我这条击杀消息"变成一条（或多条）规则，并在真实聊天流上做匹配。
///
/// <b>规则是"一段固定文本"，命中任意一条就算。</b> 三个关键决定：
///
///   1. <b>不锚定开头</b>。同一局的击杀消息格式可能不止一种 ——
///      有的服务器是「你 击败了 某人」，有的是「某人 被 你 击败了」，
///      你的 ID 一会儿在前一会儿在后。所以规则只要求"这段文本出现过"，
///      不要求出现在行首。
///   2. <b>不含敌方名字、不含收尾</b>。只留「你的 ID + 紧挨着的那一小段固定词」。
///      收尾格式（<c>!</c> / <c>，达成连杀！</c> / <c> 还剩2点血</c>）怎么写都不影响。
///   3. <b>一行一条，可以写多条</b>。一局里近战 / 远程 / 爆炸的击杀消息往往不是同一个模板，
///      每种粘一条就行。空行和 <c>#</c> 开头的行会被跳过（方便写注释）。
///
/// 关于 <see cref="StripDecorations"/>：击杀消息里会混进 <c>[vip1]</c>、<c>[25阶1003⚝]</c>、
/// <c>【称号】</c> 这类装饰。VIP 图标在日志里本来是**私用区字符**（<c>U+E0F8..U+E0FF</c>），
/// 日志走 GBK 路径时会被 LogTextDecoder 在字节层换成 <c>[vipN]</c>，走 UTF-8 路径时原样保留。
/// 所以剥离要同时处理：半角方括号块、全角方括号块、私用区字符。
/// 剥完敌人名字可能被削一段 —— 无所谓，它只用于显示，不参与判断。
/// </summary>
public sealed class KillFeedbackMatcher
{
    /// <summary>半角 / 全角方括号块。用正则而不是逐字符配对，是为了"括号不成对"时只跳过那一段、不吃掉后面全部。</summary>
    private static readonly Regex BracketBlock = new(@"\[[^\]]*\]|【[^】]*】", RegexOptions.Compiled);

    /// <summary>Unicode 私用区 —— VIP 图标的原始形态。</summary>
    private static readonly Regex PrivateUseArea = new(@"[\uE000-\uF8FF]", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    private List<(string Rule, Regex Compiled)> _compiled = new();
    private string _compiledFrom = "\u0000";

    /// <summary>
    /// 匹配规则，**一行一条**。空行和以 <c>#</c> 开头的行会跳过。
    /// </summary>
    public string Rules { get; set; } = "";

    /// <summary>
    /// false（默认）= 每条规则当**纯文本**，软件自动转义 —— 里面的 <c>.</c> <c>[</c> <c>(</c>
    /// 都只是普通符号，不用操心。
    /// true = 当正则用（想写"任意名字"这类模式时再打开）。
    /// </summary>
    public bool RulesAreRegex { get; set; }

    /// <summary>匹配前是否剥离方括号前缀和私用区字符。</summary>
    public bool StripDecorationsOn { get; set; } = true;

    /// <summary>看过多少条聊天 —— 用来区分"没收到消息"和"规则不对"。</summary>
    public int SeenCount { get; private set; }

    /// <summary>命中多少次。</summary>
    public int MatchedCount { get; private set; }

    /// <summary>最近一次命中的文本（剥离之后的）。</summary>
    public string LastMatchText { get; private set; } = "";

    /// <summary>最近一次是哪条规则命中的。</summary>
    public string LastMatchedRule { get; private set; } = "";

    public DateTime? LastMatchAt { get; private set; }

    /// <summary>最近一次出问题（规则不合法等）的说明，正常时为 <see cref="string.Empty"/>。</summary>
    public string LastWarning { get; private set; } = "";

    /// <summary>有效规则条数。</summary>
    public int RuleCount => GetCompiled().Count;

    /// <summary>一条有效规则都没有时返回 false。</summary>
    public bool IsReady => GetCompiled().Count > 0;

    /// <summary>
    /// 拿一条聊天文本试匹配：命中任意一条规则就算。
    /// </summary>
    /// <param name="text">已经过替换规则的文本（也就是用户实际看到的、悬浮窗里显示的那一份）。</param>
    public bool TryMatch(string text)
    {
        SeenCount++;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var rules = GetCompiled();
        if (rules.Count == 0)
        {
            return false;
        }

        var subject = StripDecorationsOn ? StripDecorations(text) : text;

        foreach (var (rule, regex) in rules)
        {
            if (!regex.IsMatch(subject))
            {
                continue;
            }

            MatchedCount++;
            LastMatchText = subject;
            LastMatchedRule = rule;
            LastMatchAt = DateTime.Now;
            return true;
        }

        return false;
    }

    /// <summary>界面上【试试】用：只看结果，不动计数。</summary>
    public bool TestMatch(
        string rules, bool rulesAreRegex, string text, bool stripDecorations, out string message)
    {
        message = "";

        var temp = new KillFeedbackMatcher
        {
            Rules = rules,
            RulesAreRegex = rulesAreRegex,
            StripDecorationsOn = stripDecorations,
        };

        var compiled = temp.GetCompiled();
        if (compiled.Count == 0)
        {
            message = string.IsNullOrWhiteSpace(rules)
                ? "还没有规则，先在下面写一条。"
                : "没有可用的规则（" + temp.LastWarning + "）";
            return false;
        }

        var subject = stripDecorations ? StripDecorations(text) : text;

        if (stripDecorations && !string.Equals(subject, text, StringComparison.Ordinal))
        {
            message = "剥离后：" + subject + Environment.NewLine;
        }

        foreach (var (rule, regex) in compiled)
        {
            if (regex.IsMatch(subject))
            {
                message += $"命中了规则「{rule}」";
                return true;
            }
        }

        message += $"没命中（现有 {compiled.Count} 条规则都没匹配上）。";
        return false;
    }

    /// <summary>
    /// 把多行规则编译成 <see cref="Regex"/> 列表。规则文本没变就直接返回缓存 ——
    /// 每条聊天都重编译一遍正则的话开销不小。
    /// </summary>
    private List<(string Rule, Regex Compiled)> GetCompiled()
    {
        var key = (RulesAreRegex ? "R\u0001" : "L\u0001") + (Rules ?? "");
        if (string.Equals(_compiledFrom, key, StringComparison.Ordinal))
        {
            return _compiled;
        }

        var list = new List<(string, Regex)>();
        var invalid = new List<string>();

        foreach (var raw in (Rules ?? "").Split('\n'))
        {
            var rule = raw.Trim();
            if (rule.Length == 0 || rule.StartsWith('#'))
            {
                continue;
            }

            // 纯文本模式下自动转义：这样规则里的 . [ ( 都只是普通符号，用户不用操心
            var pattern = RulesAreRegex ? rule : Regex.Escape(rule);

            try
            {
                list.Add((rule, new Regex(pattern, RegexOptions.IgnoreCase)));
            }
            catch (ArgumentException ex)
            {
                invalid.Add($"「{rule}」{ex.Message}");
            }
        }

        _compiled = list;
        _compiledFrom = key;
        LastWarning = invalid.Count > 0 ? "有规则写坏了，已跳过：" + string.Join("；", invalid) : "";

        return _compiled;
    }

    // ===================== 剥离 =====================

    /// <summary>
    /// 剥掉三类装饰：半角方括号块、全角方括号块、私用区字符（VIP 图标）。
    /// 之后把连续空白收成一个空格，免得剥离后留下「  击败了 」这种空档。
    /// </summary>
    public static string StripDecorations(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var result = BracketBlock.Replace(text, "");
        result = PrivateUseArea.Replace(result, "");

        // 半角方括号剥掉以后可能和相邻的空格连成一片，收一下
        result = WhitespaceRun.Replace(result, " ").Trim();

        // 兜底：全是装饰的话剥完就空了，这时宁可保留原文，也不要把消息吞掉
        return result.Length > 0 ? result : text;
    }

    // ===================== 从示例生成一条规则 =====================

    /// <summary>
    /// 从「自己的 ID + 一条示例消息 + 示例里的敌方名字」切出**一段固定文本**当规则。
    ///
    /// 不假设你的 ID 在前面：先把敌方名字在示例里的位置找出来，
    ///   * 敌方名字在你前面 → 取它**之后**的部分（例如「被 纉襫 击败了」）
    ///   * 敌方名字在你后面 → 取它**之前**的部分（例如「纉襫 击败了」）
    /// 敌方名字本身只是"切点标记"，不会写进规则。
    /// </summary>
    public static bool TryBuildRule(
        string? playerId,
        string? sampleMessage,
        string? sampleEnemyId,
        bool stripDecorations,
        out string rule,
        out string description,
        out string error)
    {
        rule = "";
        description = "";
        error = "";

        var id = (playerId ?? "").Trim();
        if (id.Length == 0)
        {
            error = "先填你自己的 ID。";
            return false;
        }

        var rawSample = (sampleMessage ?? "").Trim();
        if (rawSample.Length == 0)
        {
            error = "先粘一条你的击杀消息。";
            return false;
        }

        var sample = stripDecorations ? StripDecorations(rawSample) : rawSample;

        // 用户很可能直接把日志里那一整段（带称号 / VIP 图标）复制进来 ——
        // 剥离开着的时候，把 ID 本身也先剥一遍再找，两种填法都能用。
        var idKey = stripDecorations ? StripDecorations(id) : id;
        if (idKey.Length == 0)
        {
            idKey = id;
        }

        var selfIndex = sample.IndexOf(idKey, StringComparison.OrdinalIgnoreCase);
        if (selfIndex < 0)
        {
            error = $"消息里找不到你的 ID「{id}」。"
                    + (stripDecorations
                        ? "（开着「剥离方括号前缀」时，这里填裸名字就行 —— 检查一下是不是名字打错了）"
                        : "（「剥离方括号前缀」是关着的，所以要连 [vip1] / 【称号】 这些前缀一起照抄）");
            return false;
        }

        var enemyKey = (sampleEnemyId ?? "").Trim();
        if (enemyKey.Length == 0)
        {
            error = "填一下消息里敌方名字那一截 —— 软件靠它认出哪一段是名字（不会写进规则）。";
            return false;
        }

        // 敌方名字同样可能带前缀（[vip2]Alex），剥离开着时一并处理
        var enemyKeySearch = stripDecorations ? StripDecorations(enemyKey) : enemyKey;
        if (enemyKeySearch.Length == 0)
        {
            enemyKeySearch = enemyKey;
        }

        var enemyIndex = sample.IndexOf(enemyKeySearch, StringComparison.OrdinalIgnoreCase);
        if (enemyIndex < 0)
        {
            error = $"消息里找不到敌方名字「{enemyKey}」。照抄消息里那一截。";
            return false;
        }

        string how;
        if (enemyIndex < selfIndex)
        {
            rule = sample[(enemyIndex + enemyKeySearch.Length)..].Trim();
            how = "敌方名字在你前面，所以取它之后那一段";
        }
        else
        {
            rule = sample[..enemyIndex].Trim();
            how = "敌方名字在你后面，所以取它之前那一段";
        }

        if (rule.Length == 0)
        {
            error = "切出来是空的 —— 检查一下示例消息和两个名字填得对不对。";
            return false;
        }

        description = $"规则「{rule}」（{how}）。不含敌方名字，也不要求出现在行首，所以你的 ID 在前在后都适用。";
        return true;
    }
}
