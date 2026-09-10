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

    /// <summary>是否启用自动 GG。</summary>
    public bool EnableAutoGg { get; set; }

    /// <summary>自动 GG 触发正则，默认匹配“恭喜! X队 获得胜利!”。</summary>
    public string AutoGgTriggerPattern { get; set; } = @"恭喜! .+? 获得胜利!";

    /// <summary>打开聊天栏的按键，例如 t 或 /。</summary>
    public string AutoGgChatKey { get; set; } = "t";

    /// <summary>自动发送的文字。</summary>
    public string AutoGgText { get; set; } = "gg";

    /// <summary>是否使用剪贴板粘贴方式发送，可避免中文输入法把字母吞掉。</summary>
    public bool AutoGgUseClipboard { get; set; } = true;

    /// <summary>彩色渲染规则。</summary>
    public List<TextColorRule> ColorRules { get; set; } = new();

    /// <summary>文本替换规则。</summary>
    public List<TextReplaceRule> ReplaceRules { get; set; } = new();

    /// <summary>屏蔽关键词：只要聊天内容包含其中任意一个，就不显示到悬浮窗，但后端日志仍会输出。</summary>
    public List<BlockKeywordItem> BlockKeywords { get; set; } = new();

    /// <summary>玩家查询：是否记住 API KEY。</summary>
    public bool PlayerQueryRememberKey { get; set; } = true;

    /// <summary>玩家查询：上次使用的 API KEY（仅在 PlayerQueryRememberKey 为 true 时保存）。</summary>
    public string PlayerQueryApiKey { get; set; } = "";

    /// <summary>玩家查询：上次查询的玩家 ID。</summary>
    public string PlayerQueryPlayerId { get; set; } = "";

    /// <summary>玩家查询：上次选择的游戏类型 bedwars / skywars。</summary>
    public string PlayerQueryGameType { get; set; } = "bedwars";

    /// <summary>玩家查询：上次选择的模式显示名。</summary>
    public string PlayerQueryMode { get; set; } = "总览";

    /// <summary>玩家查询：自定义显示字段。</summary>
    public List<PlayerQueryField> PlayerQueryFields { get; set; } = new();

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