using System.Collections.Generic;
using System.IO;

namespace MinecraftChatOverlay.Models;

/// <summary>Minecraft 聊天悬浮窗的本地配置。</summary>
public sealed class AppSettings
{
    /// <summary>Minecraft 日志文件路径（通常是 .minecraft/logs/latest.log）。</summary>
    public string LogPath { get; set; } = GetDefaultLogPath();

    /// <summary>日志编码：Auto / UTF-8 / GBK。部分网易/国服启动器会把中文写成 GBK。</summary>
    public string LogEncoding { get; set; } = "Auto";

    /// <summary>主题开关</summary>
    public bool IsDarkMode { get; set; }

    /// <summary>悬浮窗最大宽度（像素）。</summary>
    public double OverlayWidth { get; set; } = 420;

    /// <summary>消息超过多少字符后换行（按字符数近似，不是像素宽度）。</summary>
    public int WrapLength { get; set; } = 40;

    /// <summary>是否启用鼠标点击穿透。</summary>
    public bool ClickThrough { get; set; }

    /// <summary>悬浮窗整体不透明度 0.1 ~ 1。</summary>
    public double OverlayOpacity { get; set; } = 0.9;

    /// <summary>背景不透明度 0.1 ~ 1，只影响背景，不影响聊天文字。</summary>
    public double BackgroundOpacity { get; set; } = 1.0;

    /// <summary>悬浮窗最大高度/长度（像素），超过后内部滚动。</summary>
    public double OverlayMaxHeight { get; set; } = 620;

    /// <summary>记忆的悬浮窗位置。</summary>
    public double? OverlayLeft { get; set; }

    /// <summary>记忆的悬浮窗位置。</summary>
    public double? OverlayTop { get; set; }

    /// <summary>
    /// 记忆的悬浮窗"停靠底边"（DIP）。悬浮窗只向上生长的位置补偿靠它还原，
    /// 重启后悬浮窗会回到用户放置的位置，而不是长高之后的顶边。
    /// </summary>
    public double? OverlayAnchorBottom { get; set; }

    /// <summary>显示字体名称。</summary>
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    /// <summary>字体大小。</summary>
    public double FontSize { get; set; } = 16;

    /// <summary>字体粗细：Normal / SemiBold / Bold 等。</summary>
    public string FontWeight { get; set; } = "Normal";

    /// <summary>聊天文字颜色。</summary>
    public string TextColor { get; set; } = "White";

    /// <summary>玩家发言内容颜色（“&gt; 后面的内容”），为空表示使用默认文字颜色。</summary>
    public string PlayerContentColor { get; set; } = "";

    /// <summary>发言玩家 ID 颜色（分隔符前面的最后一个 ID），为空表示使用默认文字颜色。</summary>
    public string PlayerNameColor { get; set; } = "";

    /// <summary>悬浮窗背景颜色。</summary>
    public string BackgroundColor { get; set; } = "#99000000";

    /// <summary>是否显示文字阴影。</summary>
    public bool TextShadow { get; set; } = true;

    /// <summary>悬浮窗最多保留的消息条数。</summary>
    public int MaxMessages { get; set; } = 200;

    /// <summary>是否显示消息时间。</summary>
    public bool ShowTimestamp { get; set; } = true;

    /// <summary>是否启用后端调试日志输出（默认关闭，普通用户不需要看）。</summary>
    public bool EnableDebugLog { get; set; }

    /// <summary>是否允许选中悬浮窗文本进行复制。需要关闭鼠标穿透才能用鼠标操作。</summary>
    public bool EnableTextSelection { get; set; }

    /// <summary>是否合并连续重复消息，避免刷屏。</summary>
    public bool MergeDuplicateMessages { get; set; }

    /// <summary>是否启用新消息滑入动画。</summary>
    public bool EnableMessageAnimation { get; set; } = true;

    /// <summary>彩色渲染规则。</summary>
    public List<TextColorRule> ColorRules { get; set; } = new();

    /// <summary>文本替换规则。</summary>
    public List<TextReplaceRule> ReplaceRules { get; set; } = new();

    /// <summary>屏蔽关键词：只要聊天内容包含其中任意一个，就不显示到悬浮窗，但后端日志仍会输出。</summary>
    public List<BlockKeywordItem> BlockKeywords { get; set; } = new();


    // ---------- AI 识图（把截图发给 AI，自动分配队伍色）----------

    /// <summary>是否开启：一有新截图就自动发给 AI 识别并分配。</summary>
    public bool AiAutoAssign { get; set; }

    /// <summary>接口类型："chat" = 对话模型；"zhipuOcr" = 智谱 GLM-OCR 的 layout_parsing。</summary>
    public string AiApiMode { get; set; } = "chat";

    /// <summary>识图接口地址（OpenAI 兼容），例如 https://api.openai.com/v1</summary>
    public string AiBaseUrl { get; set; } = "";

    /// <summary>模型 ID，必须是支持图片的模型。</summary>
    public string AiModelId { get; set; } = "";

    /// <summary>API Key。只存在本机这个配置文件里（明文）。</summary>
    public string AiApiKey { get; set; } = "";

    /// <summary>截图目录。留空就自动按日志目录推（.minecraft\screenshots）。</summary>
    public string AiScreenshotDir { get; set; } = "";

    /// <summary>
    /// 识别前先用 TAB 补全收集玩家 ID 作参考（默认开）。
    /// 会短暂打开聊天栏，之后自动按 Esc 关闭；全程不按回车。
    /// </summary>
    public bool AiCollectIds { get; set; } = true;

    /// <summary>第一个 ID 要按几次 TAB。</summary>
    public int AiTabFirstPresses { get; set; } = 2;

    /// <summary>每往后一个 ID，TAB 次数再多几下（默认 1 → 2、3、4、5……）。</summary>
    public int AiTabStep { get; set; } = 1;

    /// <summary>每次按键之间的间隔（毫秒）。越小越快，太小游戏会漏键。</summary>
    public int AiTabStepDelayMs { get; set; } = 35;

    /// <summary>最多收集几轮，防死循环。</summary>
    public int AiTabMaxRounds { get; set; } = 20;

    /// <summary>
    /// 额外合并进请求体的 JSON，用来关闭思考模式等。
    /// 智谱/火山：{"thinking":{"type":"disabled"}}；通义/硅基流动：{"enable_thinking":false}
    /// </summary>
    public string AiExtraBodyJson { get; set; } = "";

    /// <summary>上传前是否压缩图片（默认 false = 原图直传，保真度最高）。</summary>
    public bool AiCompressImage { get; set; }

    /// <summary>发给 AI 的提示词。</summary>
    public string AiPrompt { get; set; } = "识别图片中部偏上的黑色覆盖层内的文字信息，仅输出X队 | XXX的格式";

    // ---------- 消息提示音 ----------

    /// <summary>是否开启：有玩家说话就播提示音。</summary>
    public bool EnableSoundNotify { get; set; }

    /// <summary>两条提示音之间至少隔多少毫秒，防连发刷屏。</summary>
    public int SoundNotifyMinIntervalMs { get; set; } = 800;

    /// <summary>
    /// 用户导入的提示音（绝对路径）。v8 起软件不再附带内置音效，这里是唯一来源。
    /// </summary>
    public List<string> SoundNotifyFiles { get; set; } = new();

    /// <summary>参与随机播放的提示音（<see cref="SoundNotifyFiles"/> 的子集）。</summary>
    public List<string> SoundNotifyPicked { get; set; } = new();

    /// <summary>开了之后每次说话从勾选的提示音里随机抽一个；关掉就用列表中选中的那个。</summary>
    public bool SoundNotifyRandom { get; set; }

    /// <summary>旧字段（v7）：当前选中的提示音。保留是为了迁移，新代码用 <see cref="SoundNotifyFiles"/>。</summary>
    public string SoundNotifyFile { get; set; } = "";

    /// <summary>
    /// 旧字段（v7）：用户加过的声音文件。迁移时并进 <see cref="SoundNotifyFiles"/>。
    /// </summary>
    public List<string> SoundNotifyUserFiles { get; set; } = new();

    // ---------- 游戏帧混合动态模糊 ----------

    /// <summary>帧混合动态模糊的设置（注入钩子 + 共享内存实时调参数）。</summary>
    public MotionBlurSettings MotionBlur { get; set; } = new();

    // ---------- 击杀反馈 ----------

    /// <summary>击杀反馈的设置（效果 + 匹配规则）。</summary>
    public KillFeedbackSettings KillFeedback { get; set; } = new();

    /// <summary>「快捷命令」：把特定格式的聊天消息变成可点击、一键发命令。</summary>




    // ---------- 区域放大 ----------

    /// <summary>「区域放大」页的设置（框选游戏窗口的一小块，放大挂到屏幕别处）。</summary>

    /// <summary>被用户禁用的插件 id 列表（插件是运行时装载的独立 dll，这里只记「不许装」）。</summary>
    /// <summary>插件市场（清单地址等）。</summary>
    public PluginMarketSettings PluginMarket { get; set; } = new();

    public List<string> DisabledPlugins { get; set; } = new();

    /// <summary>
    /// 已经弹过窗的公告 id。启动时拉到公告后，只有 id 和这里不一样才弹
    /// —— 否则同一条公告每次开机都弹，很快就成了骚扰。
    /// </summary>
    public string LastSeenAnnouncementId { get; set; } = "";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    private static string GetDefaultLogPath()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, ".minecraft", "logs", "latest.log");
        }
        catch
        {
            return @"C:\Users\Public\.minecraft\logs\latest.log";
        }
    }
}
