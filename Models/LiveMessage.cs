using System;
using System.Collections.Generic;

namespace MinecraftChatOverlay.Models;

/// <summary>一条弹幕显示项。</summary>
public sealed class DanmakuItem
{
    public required long UserId { get; init; }

    public required string UserName { get; init; }

    public required string Text { get; init; }

    public string Face { get; set; } = "";

    public string? MedalName { get; init; }

    public int MedalLevel { get; init; }

    /// <summary>舰长等级：0=无，1=总督，2=提督，3=舰长（B站字段习惯可能相反，显示时再做兼容）。</summary>
    public int GuardLevel { get; init; }

    public bool HasMedal => !string.IsNullOrEmpty(MedalName) && MedalLevel > 0;

    public DateTime Time { get; init; } = DateTime.Now;

    public string? DanmakuColor { get; init; }

    /// <summary>表情包弹幕的图片地址（非表情弹幕为空）。暂未渲染，预留给内联显示表情用。</summary>
    public string EmoteUrl { get; init; } = "";
}

/// <summary>通知消息中的一段文字。</summary>
public sealed class NoticeSegment
{
    public required string Text { get; init; }

    /// <summary>颜色值，例如 #FFFFFF / White。</summary>
    public string Color { get; init; } = "#CCCCCC";

    public bool Bold { get; init; }

    /// <summary>
    /// 用哪个文本去「礼物/粉丝牌颜色」配置里找颜色（通常是礼物名或大航海名）。
    /// 为空表示不使用配置颜色，直接用上面的 Color。
    /// 金额那一段也填同一个关键字，这样金额颜色会跟着礼物走。
    /// </summary>
    public string? GiftColorKey { get; init; }
}

/// <summary>礼物连击的合并规则与计算。</summary>
public static class GiftMerge
{
    /// <summary>
    /// 新通知能否并进「上一条通知」。
    /// 规则（用户要求）：必须是同一个人送的同一个礼物；中间夹了任何别的通知就不合并。
    /// </summary>
    public static bool CanMerge(string? previousGiftKey, string? incomingGiftKey) =>
        !string.IsNullOrEmpty(previousGiftKey) &&
        !string.IsNullOrEmpty(incomingGiftKey) &&
        string.Equals(previousGiftKey, incomingGiftKey, StringComparison.Ordinal);

    /// <summary>
    /// 把新的一条礼物并进已有的累计值。
    /// B站的 num 有两种口径：一次一件（恒为 1）或累计值（1,2,3…），
    /// 所以「新值比已有值大」就当作累计值直接采用，否则累加 —— 两种口径都能算对。
    /// </summary>
    public static (int Count, double Amount) Combine(int lastCount, double lastAmount, int newCount, double newAmount)
    {
        var mergedCount = newCount > lastCount ? newCount : lastCount + newCount;
        var unitAmount = newCount > 0 ? newAmount / newCount : 0;
        var mergedAmount = unitAmount > 0 ? unitAmount * mergedCount : lastAmount + newAmount;
        return (mergedCount, mergedAmount);
    }
}

/// <summary>通知类型，用于按类型过滤显示（进场消息量很大，默认关闭）。</summary>
public enum NoticeKind
{
    Unknown,
    Entry,
    Follow,
    Share,
    Gift,
    Guard,
    SuperChat
}

/// <summary>底部消息通知项。</summary>
public sealed class NoticeItem
{
    public required string Text { get; init; }

    /// <summary>通知类型。</summary>
    public NoticeKind Kind { get; init; } = NoticeKind.Unknown;

    /// <summary>可选：多段彩色文字；为空时整条使用 Text。</summary>
    public List<NoticeSegment>? Segments { get; init; }

    /// <summary>礼物合并键，例如 “A|小花花”。非礼物消息为空。</summary>
    public string? GiftKey { get; init; }

    /// <summary>礼物名称，用于合并时更新 xN。</summary>
    public string? GiftName { get; init; }

    /// <summary>礼物数量（用于连续礼物合并）。</summary>
    public int GiftCount { get; init; } = 1;

    /// <summary>礼物金额（元，用于合并显示）。</summary>
    public double GiftAmount { get; init; }

    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>大航海标准月费（元）。1=总督 2=提督 3=舰长。</summary>
    public static double GuardPrice(int guardLevel) => guardLevel switch
    {
        1 => 19998,
        2 => 1998,
        3 => 138,
        _ => 0
    };

    public static string GuardRoleName(int guardLevel) => guardLevel switch
    {
        1 => "总督",
        2 => "提督",
        3 => "舰长",
        _ => "大航海"
    };

    /// <summary>构造大航海通知（角色名 + 金额都用配置颜色）。</summary>
    public static NoticeItem CreateGuard(string userName, int guardLevel, int count = 1)
    {
        var role = GuardRoleName(guardLevel);
        var price = GuardPrice(guardLevel);
        var amountText = price > 0 ? $"（{price:0.##} CNY）" : "";
        var countText = count > 1 ? $" x{count}" : "";

        return new NoticeItem
        {
            Text = $"{userName}开通了{role}{countText}{amountText}",
            Kind = NoticeKind.Guard,
            Segments = new List<NoticeSegment>
            {
                new NoticeSegment { Text = $"{userName}开通了", Color = "#CCCCCC" },
                new NoticeSegment { Text = role + countText, Color = "#FF3B30", Bold = true, GiftColorKey = role },
                new NoticeSegment { Text = amountText, Color = "#AAAAAA", GiftColorKey = role }
            }
        };
    }

    /// <summary>构造礼物投喂通知（礼物名 + 金额都用配置颜色）。</summary>
    /// <param name="color">配置里匹配不到礼物名时使用的兜底颜色。</param>
    /// <param name="mergeKey">同一用户+礼物的合并键（用于连续礼物的连击合并）。</param>
    public static NoticeItem CreateGift(
        string userName, string giftName, int count, double amount, string color = "#FFE066", string? mergeKey = null)
    {
        var amountText = amount > 0 ? $"（{amount:0.##} CNY）" : "";
        return new NoticeItem
        {
            Text = $"{userName}投喂了{giftName} x{count}个{amountText}",
            Kind = NoticeKind.Gift,
            GiftKey = mergeKey,
            GiftName = giftName,
            GiftCount = count,
            GiftAmount = amount,
            Segments = new List<NoticeSegment>
            {
                new NoticeSegment { Text = $"{userName}投喂了", Color = "#CCCCCC" },
                new NoticeSegment { Text = $"{giftName} x{count}个", Color = color, Bold = true, GiftColorKey = giftName },
                new NoticeSegment { Text = amountText, Color = "#AAAAAA", GiftColorKey = giftName }
            }
        };
    }
}
