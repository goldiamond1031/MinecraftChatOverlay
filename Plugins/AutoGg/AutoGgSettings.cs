namespace MinecraftChatOverlay.Plugins.AutoGg;

/// <summary>自动 GG 的配置（原本在主程序 AppSettings 里，现在归插件自己管）。</summary>
public sealed class AutoGgSettings
{
    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; }

    /// <summary>触发正则（默认匹配"恭喜! xxx 获得胜利!"）。</summary>
    public string TriggerPattern { get; set; } = @"恭喜! .+? 获得胜利!";

    /// <summary>打开聊天栏的按键（默认 t；填 enter 表示直接敲回车）。</summary>
    public string ChatKey { get; set; } = "t";

    /// <summary>要发送的文字（默认 gg）。</summary>
    public string Text { get; set; } = "gg";

    /// <summary>用剪贴板粘贴的方式发送（推荐，避免中文输入法吞字母）。</summary>
    public bool UseClipboard { get; set; } = true;

    /// <summary>发完上面那段文字后，隔一小会儿再自动发送一次 /again（方便接着再开一局）。</summary>
    public bool SendAgainAfterGg { get; set; }

    /// <summary>上次触发时间（只给界面看，不算配置）。</summary>
    public DateTime LastTriggerAt { get; set; } = DateTime.MinValue;
}