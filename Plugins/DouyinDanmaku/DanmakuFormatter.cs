using DouyinDanmaku.Protocol;

namespace DouyinDanmaku;

/// <summary>
/// 模板渲染与关键词过滤。语义对齐 DyDanmaku：
/// <c>${变量名}</c> 占位符替换、未赋值的占位符自动清除、
/// 关键词三模式（disabled / blacklist / whitelist）且不区分大小写。
/// </summary>
internal static class DanmakuFormatter
{
    /// <summary>把一条事件渲染成悬浮窗文本。返回 null 表示被过滤掉。</summary>
    public static string? Render(DanmakuEvent ev, Settings s)
    {
        var text = RenderTemplate(ev, s);
        if (text == "")
        {
            return null;
        }

        var keywords = SplitKeywords(s.Keywords);
        if (keywords.Length == 0 || s.FilterMode == "disabled")
        {
            return text;
        }

        bool hit = false;
        foreach (var k in keywords)
        {
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase))
            {
                hit = true;
                break;
            }
        }

        return s.FilterMode switch
        {
            "blacklist" => hit ? null : text,
            "whitelist" => hit ? text : null,
            _ => text,
        };
    }

    private static string RenderTemplate(DanmakuEvent ev, Settings s)
    {
        var template = ev.Kind switch
        {
            DanmakuKind.Chat => s.ChatTemplate,
            DanmakuKind.Like => s.LikeTemplate,
            DanmakuKind.Gift => s.GiftTemplate,
            DanmakuKind.Member => s.MemberTemplate,
            DanmakuKind.RoomStats => s.RoomStatsTemplate,
            DanmakuKind.Fansclub => s.FansclubTemplate,
            _ => "",
        };

        if (template.Trim() == "")
        {
            return "";
        }

        // 逐个替换已知变量，最后统一清掉残留的未赋值占位符
        var text = template
            .Replace("${nickname}", Ev(ev.Nickname))
            .Replace("${payGradeLevel}", Ev(PayGradeText(ev)))
            .Replace("${fansClubLevel}", Ev(FansClubText(ev)))
            .Replace("${content}", Ev(ev.Content))
            .Replace("${count}", Ev(NumText(ev.Count)))
            .Replace("${totalStr}", Ev(NumText(ev.Total)))
            .Replace("${totalPvForAnchor}", Ev(NumText(ev.TotalPvForAnchor)))
            .Replace("${memberCount}", Ev(NumText(ev.MemberCount)))
            .Replace("${actionDescription}", Ev(ev.ActionDescription))
            .Replace("${userId}", Ev(NumText(ev.UserId)))
            .Replace("${giftName}", Ev(ev.GiftName))
            .Replace("${giftCombo}", Ev(NumText(ev.GiftCombo)))
            .Replace("${comboCount}", Ev(NumText(ev.ComboCount)))
            .Replace("${repeatCount}", Ev(NumText(ev.RepeatCount)))
            .Replace("${giftId}", Ev(NumText(ev.GiftId)))
            .Replace("${giftDiamondCount}", Ev(NumText(ev.GiftDiamondCount)))
            .Replace("${giftDescribe}", Ev(ev.GiftDiamondCount > 0 ? $"{ev.GiftDiamondCount} 钻" : ""))
            .Replace("${groupCount}", Ev(NumText(ev.GroupCount)))
            .Replace("${fansclub}", Ev(ev.FansclubText));

        return ClearPlaceholders(text).Trim();
    }

    /// <summary>变量有值才替换，否则留空占位（后面统一清除）。</summary>
    private static string Ev(string value) => value == "" ? "" : value;

    private static string NumText(long v) => v <= 0 ? "" : v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string PayGradeText(DanmakuEvent ev) =>
        ev.PayGradeLevel < 0 ? "" : ev.PayGradeLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string FansClubText(DanmakuEvent ev) =>
        ev.FansClubLevel < 0 ? "" : ev.FansClubLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>清除所有 ${...} 占位符，并清理留下的多余空白与标点。</summary>
    private static string ClearPlaceholders(string text)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                int close = text.IndexOf('}', i + 2);
                if (close > 0)
                {
                    i = close + 1;
                    continue;
                }
            }

            sb.Append(text[i]);
            i++;
        }

        var result = sb.ToString();

        // 占位符被清掉后可能留下「送出  ×3」「进入 」这类残留空白
        while (result.Contains("  "))
        {
            result = result.Replace("  ", " ");
        }

        // 若整个方括号里只有被清掉的占位符，连括号一起去掉
        while (result.Contains("[]"))
        {
            result = result.Replace("[]", "");
        }

        // 去掉空括号可能又产生连续空白，再收一次尾
        while (result.Contains("  "))
        {
            result = result.Replace("  ", " ");
        }

        return result
            .Replace(" ×0", "")
            .Replace(" ：", "：")
            .Replace("： ：", "：")
            .Trim();
    }

    private static string[] SplitKeywords(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw
            .Split(new[] { ',', '，', '\n', '\r', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
