using System;

namespace MinecraftChatOverlay.Models;

/// <summary>
/// 一条醒目留言（SC）。和普通弹幕一样带用户信息，所以弹幕窗里可以按「头像 + 各种标识 + 用户名 + 内容」的样式单独成区显示。
/// </summary>
public sealed class SuperChatItem
{
    public long UserId { get; init; }

    public string UserName { get; init; } = "";

    /// <summary>头像地址。</summary>
    public string Face { get; init; } = "";

    /// <summary>佩戴的粉丝牌名称（没戴为空）。</summary>
    public string? MedalName { get; init; }

    /// <summary>粉丝牌等级。</summary>
    public int MedalLevel { get; init; }

    /// <summary>舰长等级：0=无，1=总督，2=提督，3=舰长。</summary>
    public int GuardLevel { get; init; }

    /// <summary>金额（元）。B站下发的 price 是千分之一元，解析时已经除以 1000。</summary>
    public double Amount { get; init; }

    /// <summary>留言内容。</summary>
    public string Message { get; init; } = "";

    /// <summary>SC 在直播间停留的秒数（B站下发的 time 字段）。</summary>
    public int DurationSeconds { get; init; }

    public DateTime Time { get; init; } = DateTime.Now;
}
