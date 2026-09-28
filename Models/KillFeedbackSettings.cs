using System.Collections.Generic;

namespace MinecraftChatOverlay.Models;

/// <summary>
/// 击杀反馈的设置。
///
/// 分两块：
///   * <b>效果</b> —— 视觉上怎么反馈（这一版做完了，带实时预览）；
///   * <b>匹配</b> —— 怎么认出"这条击杀是我干的"（下一步接上真实聊天流）。
/// </summary>
public sealed class KillFeedbackSettings
{
    /// <summary>总开关。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 效果类型，存成字符串而不是枚举 ——
    /// 枚举被序列化成数字以后，改一次枚举顺序老配置就错位了，字符串不会。
    /// 取值见 <see cref="Services.KillFeedback.KillFeedbackEffect"/>。
    /// 默认是 gold_ 挑的「缩放脉冲」。
    /// </summary>
    public string Effect { get; set; } = "ZoomPunch";

    /// <summary>强度 0 ~ 5.0（界面显示 0 ~ 500%）。旧字段，仅用于迁移到多效果配置。</summary>
    public double Strength { get; set; } = 0.6;

    /// <summary>单次反馈持续多久（毫秒）。所有效果共用。</summary>
    public double DurationMs { get; set; } = 380;

    // ---------- 多效果配置（v4） ----------
    // Strength 统一存 0 ~ 5.0，界面上按百分比显示（0 ~ 500%）。

    /// <summary>是否已经把旧单效果配置迁移到下面的多效果字段。</summary>
    public bool MultiEffectConfigured { get; set; }

    public bool EdgeEnabled { get; set; } = true;
    public double EdgeStrength { get; set; } = 0.6;

    /// <summary>边缘脉冲颜色，格式 #RRGGBB。</summary>
    public string EdgeColor { get; set; } = "#FFF5E0";

    public bool FlashEnabled { get; set; }
    public double FlashStrength { get; set; } = 0.6;

    public bool ShockwaveEnabled { get; set; }
    public double ShockwaveStrength { get; set; } = 0.6;

    public bool GlitchEnabled { get; set; }
    public double GlitchStrength { get; set; } = 0.6;

    public bool VignetteEnabled { get; set; }
    public double VignetteStrength { get; set; } = 0.6;

    public bool ChromaticEnabled { get; set; }
    public double ChromaticStrength { get; set; } = 0.6;

    public bool ZoomEnabled { get; set; }
    public double ZoomStrength { get; set; } = 0.6;

    public bool ShakeEnabled { get; set; }
    public double ShakeStrength { get; set; } = 0.6;

    // ---------- 击杀图标（原「击杀横幅」，v7 起整体换成图标） ----------
    //
    // v8 起软件**不再附带内置图标**，全部由用户导入。
    // 所以这里存的不再是"内置 key 还是自定义"的二选一，而是一个用户图片列表 + 勾选状态。

    /// <summary>是否在屏幕上显示击杀确认图标。</summary>
    public bool BannerEnabled { get; set; }

    /// <summary>
    /// 用户导入的图标（绝对路径）。列表里的顺序就是界面上的顺序。
    /// </summary>
    public List<string> BannerIconFiles { get; set; } = new();

    /// <summary>
    /// 参与「随机抽取」的图标 —— 存的是 <see cref="BannerIconFiles"/> 里的路径子集。
    ///
    /// 为什么不直接在列表项上挂一个 bool：列表是路径字符串的集合，挂 bool 就得
    /// 额外维护一份和路径一一对应的状态，删一项 / 换一项都要同步两个地方，容易错位。
    /// 单独存一份"被勾中的路径"，增删时只做集合运算，不会有对不齐的问题。
    /// </summary>
    public List<string> BannerIconPicked { get; set; } = new();

    /// <summary>开了之后每次击杀从勾选的图标里随机抽一个；关掉就用列表中选中的那张。</summary>
    public bool BannerIconRandom { get; set; }

    /// <summary>
    /// 图标在屏幕上的横向位置，0 ~ 1（0 = 最左，0.5 = 水平居中，1 = 最右）。
    /// </summary>
    public double BannerPosX { get; set; } = 0.5;

    /// <summary>
    /// 图标在屏幕上的纵向位置，0 ~ 1（0 = 最上，0.5 = 垂直居中，1 = 最下）。
    /// 默认 0.58 —— 准星正下方一点，和 CS / 瓦 那种"击杀确认"落点一致。
    /// </summary>
    public double BannerPosY { get; set; } = 0.58;

    /// <summary>图标边长（像素）。</summary>
    public double BannerIconSize { get; set; } = 96;

    /// <summary>
    /// 强调色，格式 #RRGGBB。v8 起用户导入的图片保持原色，这个字段只用来给
    /// 图标垫一层外发光 / 描边，方便在亮背景上也能看清。
    /// </summary>
    public string BannerIconColor { get; set; } = "#FFFFFF";

    /// <summary>淡入时长（毫秒）。三段之一，见 <see cref="BannerHoldMs"/> 的注释。</summary>
    public double BannerFadeInMs { get; set; } = 150;

    /// <summary>停留时长（毫秒）。</summary>
    public double BannerHoldMs { get; set; } = 600;

    /// <summary>淡出时长（毫秒）。</summary>
    public double BannerFadeOutMs { get; set; } = 200;

    /// <summary>
    /// 图标从出现到完全消失的总时长（三段相加）。
    /// 这是算出来的，不存配置 —— 存了就会出现"三个分段加起来和总时长对不上"的脏状态。
    /// </summary>
    public double BannerTotalMs => BannerFadeInMs + BannerHoldMs + BannerFadeOutMs;

    /// <summary>
    /// 旧的「用哪个图标」字段（v7）。保留只为了让老配置能读进来、把里面的内置 key
    /// 丢掉并迁到新的列表模型，新代码不再写它。
    /// </summary>
    public string BannerIcon { get; set; } = "";

    /// <summary>旧的自定义图片路径（v7）。迁移时会并进 <see cref="BannerIconFiles"/>。</summary>
    public string BannerCustomImagePath { get; set; } = "";

    /// <summary>旧的停留毫秒（v7）。迁移时按比例摊到淡入 / 停留 / 淡出。</summary>
    public double BannerDurationMs { get; set; } = 900;

    /// <summary>
    /// 旧的横幅文字（v7 之前用）。保留字段只为了让老配置能读进来，新代码不再显示它。
    /// </summary>
    public string BannerText { get; set; } = "击杀！";

    // ---------- 击杀提示音 ----------

    /// <summary>是否在匹配到自己的击杀时播放提示音。</summary>
    public bool SoundEnabled { get; set; }

    /// <summary>
    /// 用户导入的击杀提示音（绝对路径）。v8 起软件不再附带内置音效，这里是唯一来源。
    /// </summary>
    public List<string> SoundFiles { get; set; } = new();

    /// <summary>参与随机抽取的提示音（<see cref="SoundFiles"/> 的子集）。语义同图标那边。</summary>
    public List<string> SoundPicked { get; set; } = new();

    /// <summary>开了之后每次击杀从勾选的提示音里随机抽一个；关掉就用列表中选中的那个。</summary>
    public bool SoundRandom { get; set; }

    /// <summary>
    /// 旧字段（v7）：当前选中的那一个声音文件。保留是为了迁移，新代码用 <see cref="SoundFiles"/>。
    /// </summary>
    public string SoundFile { get; set; } = "";

    /// <summary>
    /// 旧字段（v7）：用户加过的文件。迁移时并进 <see cref="SoundFiles"/>。
    /// </summary>
    public List<string> SoundUserFiles { get; set; } = new();

    /// <summary>
    /// 连杀合并窗口（秒）。这段时间内连着击杀时合并成一次反馈、强度递增，
    /// 而不是响好几次 —— 团战里不合并会变成屏幕一直在闪，反而看不清敌人。
    /// </summary>
    public double MergeWindowSeconds { get; set; } = 1.5;

    // ---------- 匹配 ----------

    /// <summary>你自己的游戏 ID，按日志里长什么样填（裸名字）。</summary>
    public string PlayerId { get; set; } = "";

    /// <summary>从日志里复制的一条"你击杀别人"的示例消息。</summary>
    public string SampleMessage { get; set; } = "";

    /// <summary>示例消息里那个敌方名字 —— **只当切点用**，用来认出哪一段是名字，不写进规则。</summary>
    public string SampleEnemyId { get; set; } = "";

    /// <summary>
    /// 匹配规则，**一行一条**，命中任意一条就算。
    ///
    /// 一局里近战 / 远程 / 爆炸的击杀消息往往不是同一个模板，所以允许写多条。
    /// 规则是"一段固定文本"，不锚定行首、不含敌方名字、不含收尾 ——
    /// 所以你的 ID 在前在后、收尾怎么变都不影响。
    /// </summary>
    public string MatchRules { get; set; } = "";

    /// <summary>
    /// false（默认）= 每条规则当纯文本，软件自动转义（<c>.</c> <c>[</c> <c>(</c> 都只是普通符号）；
    /// true = 当正则用。
    /// </summary>
    public bool MatchRulesAreRegex { get; set; }

    /// <summary>
    /// 匹配前先把 [vip1] / [25阶1003⚝] / 【称号】 这类方括号前缀剥掉，私用区字符（VIP 图标）也一并去掉。
    /// 默认开 —— 不然称号和 VIP 标识会让规则一直失效。
    /// </summary>
    public bool StripBrackets { get; set; } = true;
}
