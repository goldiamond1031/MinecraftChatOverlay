using System.Collections.Generic;

namespace MinecraftChatOverlay.Models;

/// <summary>B站弹幕模块的配置（独立文件 bili.json，与 MCO 主配置互不干扰）。</summary>
public sealed class BiliSettings
{
    public string RoomId { get; set; } = "";

    /// <summary>B站登录 Cookie（可选）。粘贴浏览器里的完整 Cookie 可显示真实 UID/头像，并提高弹幕完整度。</summary>
    public string BiliCookie { get; set; } = "";

    public double OverlayOpacity { get; set; } = 0.9;

    public double OverlayWidth { get; set; } = 440;

    /// <summary>弹幕区高度上限（像素），超过后弹幕区内部滚动。实际值还会按屏幕工作区再收紧。</summary>
    public double DanmakuMaxHeight { get; set; } = 620;

    public bool ClickThrough { get; set; }

    /// <summary>顶部（房间标题 / 主播名 / 人数）字号。</summary>
    public double HeaderFontSize { get; set; } = 11;

    /// <summary>弹幕区字号（弹幕正文；用户名按它的 0.8 倍显示）。</summary>
    public double FontSize { get; set; } = 16;

    /// <summary>弹幕窗整体使用的字体，顶部 / 弹幕区 / 底部共用。</summary>
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    /// <summary>弹幕里用户名相对弹幕字号的比例（1 = 和正文一样大）。</summary>
    public double NameFontScale { get; set; } = 0.8;

    public int MaxDanmakuLines { get; set; } = 200;

    // ---------- 分区显示：SC / 礼物 / 进场 各自独立 ----------
    // 「记录」= 内存里保留多少条历史；「显示」= 同时能看到几行（更早的用滚轮翻）。

    /// <summary>SC 区最多保留条数。</summary>
    public int MaxSuperChatLines { get; set; } = 200;

    /// <summary>SC 区同时显示几行。</summary>
    public int MaxSuperChatVisibleLines { get; set; } = 2;

    /// <summary>SC 区字号。</summary>
    public double SuperChatFontSize { get; set; } = 12;

    /// <summary>礼物区最多保留条数（礼物 + 大航海）。</summary>
    public int MaxGiftLines { get; set; } = 200;

    /// <summary>礼物区同时显示几行。</summary>
    public int MaxGiftVisibleLines { get; set; } = 2;

    /// <summary>礼物区字号。</summary>
    public double GiftFontSize { get; set; } = 12;

    /// <summary>进场区最多保留条数（进场 / 关注 / 分享）。</summary>
    public int MaxEntryLines { get; set; } = 200;

    /// <summary>进场区同时显示几行。</summary>
    public int MaxEntryVisibleLines { get; set; } = 1;

    /// <summary>进场区字号。</summary>
    public double EntryFontSize { get; set; } = 11;

    /// <summary>是否显示「进入直播间」通知。人多时进场消息会刷屏（实测 70 秒 86 条），默认关闭。</summary>
    public bool ShowEntryNotice { get; set; }

    /// <summary>是否显示礼物投喂通知。</summary>
    public bool ShowGiftNotice { get; set; } = true;

    /// <summary>是否显示醒目留言（SC）通知。</summary>
    public bool ShowSuperChatNotice { get; set; } = true;

    /// <summary>是否显示大航海（舰长/提督/总督）通知。</summary>
    public bool ShowGuardNotice { get; set; } = true;

    /// <summary>弹幕超过多少个字符时换行/分行（按东亚字符宽度粗略计算）。</summary>
    public int DanmakuWrapLength { get; set; } = 40;

    public bool ShowOnline { get; set; } = true;

    public bool ShowRoomTitle { get; set; } = true;

    /// <summary>悬浮窗顶部显示什么数字：
    /// 0=人气值（B站官方口径，加权指标，会明显偏大）；
    /// 1=看过人数（本场累计真实观众，但不是在线人数）；
    /// 2=互动人数（本场投喂/发弹幕人数）；
    /// 3=看过 + 人气；
    /// 4=在线人数（真实并发人数，来自弹幕流 ONLINE_RANK_COUNT，仅直连模式可用）；
    /// 5=在线人数 + 人气。
    /// 拿不到对应口径时会自动回退显示人气。</summary>
    public int OnlineDisplayMode { get; set; } = 4;

    public bool DanmakuShadow { get; set; } = true;

    public string OnlineColor { get; set; } = "White";

    /// <summary>进场区（进场 / 关注 / 分享）文字颜色。</summary>
    public string NoticeColor { get; set; } = "#CCCCCC";

    /// <summary>礼物区（礼物 / 大航海）里「XX投喂了」这类文字的颜色。</summary>
    public string GiftTextColor { get; set; } = "#CCCCCC";

    public string DanmakuTextColor { get; set; } = "White";

    /// <summary>
    /// 是否显示弹幕自带的颜色（B站彩色弹幕用）。
    /// 关闭后弹幕正文统一用「弹幕文字颜色」。
    /// 只影响弹幕区；SC / 礼物 / 舰长等走的是通知区，不受这个开关影响。
    /// </summary>
    public bool ShowDanmakuOriginalColor { get; set; } = true;

    public string ShadowColor { get; set; } = "Black";

    public string BackgroundColor { get; set; } = "#66000000";

    /// <summary>
    /// 背景不透明度 0~1。它乘在 BackgroundColor 自带的 alpha 上，
    /// 所以「背景颜色」里的透明度是底色本身，「背景不透明度」是整体再压一层。
    /// </summary>
    public double BackgroundOpacity { get; set; } = 1.0;

    /// <summary>
    /// 礼物名称（或关键词）对应的颜色，按礼物名做「包含」匹配（不区分大小写）。
    /// 例：「舰长:#FF3333」。
    /// 顺序按价格从高到低，颜色按价格分档，方便一眼看出贵重程度。
    /// </summary>
    public Dictionary<string, string> GiftColors { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // 大航海
        ["舰长"] = "#FF3B30",
        ["提督"] = "#FF9500",
        ["总督"] = "#FFD700",

        // 2 万电池以上（金色）
        ["bilibili世界"] = "#FFD700",
        ["梦幻游乐园"] = "#FFD700",
        ["小电视飞船"] = "#FFD700",
        ["探索者启航"] = "#FFD700",

        // 1 万电池档（橙色）
        ["次元之城"] = "#FF9500",
        ["bilibili星跃"] = "#FF9500",

        // 5000 电池档（橙红）
        ["任意门"] = "#FF6B35",
        ["星轨列车"] = "#FF6B35",
        ["原地求婚"] = "#FF6B35",
        ["为你摘星"] = "#FF6B35",
        ["飞星环游"] = "#FF6B35",

        // 2000 电池档（玫红）
        ["月桂星冠"] = "#FF3B7F",
        ["梦幻邮轮"] = "#FF3B7F",
        ["梦游仙境"] = "#FF3B7F",
        ["发红包"] = "#FF3B7F",
        ["落日飞车"] = "#FF3B7F",
        ["告白气球"] = "#FF3B7F",

        // 1000 电池档（紫色）
        ["处女座娃娃"] = "#C86BFF",
        ["爱的乐章"] = "#C86BFF",
        ["舰长一号"] = "#C86BFF",
        ["星愿水晶球"] = "#C86BFF",
        ["极速超跑"] = "#C86BFF",
        ["私人飞机"] = "#C86BFF",

        // 300~999 电池（蓝色）
        ["旋转木马"] = "#4DA6FF",
        ["灿烂烟花"] = "#4DA6FF",
        ["羁绊宝盒"] = "#4DA6FF",

        // 50~299 电池（绿色）
        ["冰晶吊坠"] = "#4CD964",
        ["流星雨"] = "#4CD964",
        ["花式夸夸"] = "#4CD964",
        ["告白花束"] = "#4CD964",
        ["钻石戒指"] = "#4CD964",
        ["心动盲盒"] = "#4CD964",
        ["喜欢你"] = "#4CD964",
        ["撒花"] = "#4CD964",
        ["做我的小猫"] = "#4CD964",
        ["捏捏小脸"] = "#4CD964",
        ["音乐盒"] = "#4CD964",
        ["水晶鞋"] = "#4CD964",
        ["欧气盲盒"] = "#4CD964",
        ["千纸鹤"] = "#4CD964",
        ["情书"] = "#4CD964",
        ["幸运盲盒"] = "#4CD964",
        ["心动时刻"] = "#4CD964",
        ["甜滋滋"] = "#4CD964",

        // 50 电池以下（淡蓝）
        ["你真好看"] = "#7CC7FF",
        ["比心"] = "#7CC7FF",
        ["666"] = "#7CC7FF",
        ["送花花"] = "#7CC7FF",
        ["鼓鼓掌"] = "#7CC7FF",
        ["打Call"] = "#66CCFF",
        ["小花花"] = "#FF69B4",
        ["人气票"] = "#7CC7FF",
        ["牛哇牛哇"] = "#7CC7FF",
        ["粉丝团灯牌"] = "#FFE066",
        ["粉丝手幅"] = "#7CC7FF",

        // 其他常见礼物
        ["辣条"] = "#FF6B6B",
        ["亿圆"] = "#4CD964",
        ["摩天大楼"] = "#BFBFBF"
    };

    /// <summary>
    /// 是否已经用内置礼物表补全过一次（防止用户删掉的礼物每次启动又被加回来）。
    /// </summary>
    public bool GiftColorsSeeded { get; set; }


    /// <summary>B站悬浮窗记忆位置（DIP）。与本体的聊天悬浮窗各自独立。</summary>
    public double? OverlayLeft { get; set; }

    /// <summary>B站悬浮窗记忆位置（DIP）。</summary>
    public double? OverlayTop { get; set; }

    /// <summary>B站悬浮窗的停靠底边（DIP），用于底边锚定收缩。</summary>
    public double? OverlayAnchorBottom { get; set; }

    public BiliSettings Clone() => (BiliSettings)MemberwiseClone();
}
