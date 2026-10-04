namespace MinecraftChatOverlay.Plugins.NeteaseLyrics;

/// <summary>歌词插件的全部设置。</summary>
public sealed class NeteaseLyricsSettings
{
    // ---- 开关 ----
    public bool Enabled { get; set; } = true;
    public bool ShowTranslation { get; set; } = true;
    public bool ShowRomaji { get; set; }
    public bool ShowNextLine { get; set; } = true;
    public bool ShowTrackInfo { get; set; } = true;
    public bool ClickThrough { get; set; }

    /// <summary>
    /// OBS 直播模式：摘掉 TOOLWINDOW，让歌词窗能被 OBS「窗口捕获」抓到。
    /// 默认关 —— 开了之后窗口会出现在任务栏和 Alt+Tab 里（所有者被清了，压不住），
    /// 这个代价写在设置页文案里，直播的人自己权衡。
    /// </summary>
    public bool ObsMode { get; set; }

    // ---- 字体与文字 ----
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 30;
    public bool Bold { get; set; } = true;
    public string TextColor { get; set; } = "#FFFFFFFF";
    public double BackgroundOpacity { get; set; }

    // ---- 翻译 ----
    public string TranslationColor { get; set; } = "#FFDDEEFF";
    public double TranslationScale { get; set; } = 0.62;

    // ---- 罗马音（独立设置）----
    public string RomajiColor { get; set; } = "#CCBFE3FF";
    public double RomajiScale { get; set; } = 0.55;
    public bool RomajiItalic { get; set; } = true;

    // ---- 阴影 ----
    public string ShadowColor { get; set; } = "#FF000000";
    public double ShadowBlur { get; set; } = 6;
    public double ShadowOpacity { get; set; } = 0.8;
    public double ShadowOffset { get; set; } = 2;
    public double ShadowDirection { get; set; } = 45;

    // ---- 布局与同步 ----
    public double NextLineScale { get; set; } = 0.62;
    public double LyricOffsetMs { get; set; }
    public double WindowLeft { get; set; } = 80;
    public double WindowTop { get; set; } = 800;
    public double WindowWidth { get; set; } = 1100;

    /// <summary>中继文件路径；留空用默认。</summary>
    public string? RelayPath { get; set; }

    /// <summary>是不是已经弹过"首次使用引导"。</summary>
    public bool OnboardingShown { get; set; }

    /// <summary>逐字歌词（卡拉OK）开关。</summary>
    public bool Karaoke { get; set; } = true;

    /// <summary>逐字歌词里"已唱"部分的颜色（未唱部分自动用文字色的半透明）。</summary>
    public string KaraokeColor { get; set; } = "#FF7FD8FF";
}
