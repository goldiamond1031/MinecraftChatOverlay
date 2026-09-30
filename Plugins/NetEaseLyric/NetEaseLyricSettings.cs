namespace MinecraftChatOverlay.Plugins.NetEaseLyric;

/// <summary>位置数据从哪来。</summary>
public enum DataSourceKind
{
    /// <summary>优先本地桥接（InfLink 给的精确进度），拿不到就退回 SMTC。</summary>
    Auto,

    /// <summary>只用本地桥接。</summary>
    BridgeOnly,

    /// <summary>只用系统媒体会话（SMTC）。</summary>
    SmtcOnly,
}

/// <summary>
/// 插件的全部可调项。存到插件目录的 settings.json（由 PluginSettingsFile 负责读写）。
/// 外观那一组是照参考项目来的：主文字 + 一层影子文字，颜色/透明度/模糊/偏移都能调。
/// </summary>
public sealed class NetEaseLyricSettings
{
    /// <summary>总开关：关掉就完全不跑（不动系统里的东西）。</summary>
    public bool Enabled { get; set; } = true;

    // ---------- 数据来源 ----------

    /// <summary>位置数据来源。</summary>
    public DataSourceKind DataSource { get; set; } = DataSourceKind.Auto;

    /// <summary>本地桥接监听的端口（InfLink 桥接插件默认往 27431 发）。</summary>
    public int BridgePort { get; set; } = 27431;

    // ---------- 外观 ----------

    /// <summary>字号。</summary>
    public double FontSize { get; set; } = 30;

    /// <summary>字体。默认微软雅黑（中文歌词和日文假名都不会缺字）。</summary>
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    public bool Bold { get; set; }

    /// <summary>主文字颜色（#RRGGBB 或 #AARRGGBB）。</summary>
    public string TextColor { get; set; } = "#FFFFFFFF";

    /// <summary>影子文字的颜色。</summary>
    public string ShadowColor { get; set; } = "#FF000000";

    /// <summary>影子透明度 0~255（0 = 不要影子）。</summary>
    public double ShadowOpacity { get; set; } = 200;

    /// <summary>影子模糊半径（0 = 硬边描边感，越大越柔）。</summary>
    public double ShadowBlur { get; set; } = 0;

    /// <summary>影子偏移（像素）。默认往右下各 2px，像常见的"重影"打法。</summary>
    public double ShadowOffsetX { get; set; } = 2;

    public double ShadowOffsetY { get; set; } = 2;

    /// <summary>底色透明度 0~255（0 = 完全透明，只有字）。</summary>
    public double BackgroundOpacity { get; set; } = 0;

    // ---------- 显示内容 ----------

    /// <summary>是否显示下一句预告（单行/双行的开关）。</summary>
    public bool ShowNextLine { get; set; } = true;

    /// <summary>下一句的字号相对主文字的比例。</summary>
    public double NextLineScale { get; set; } = 0.62;

    /// <summary>是否显示官方翻译（有翻译才显示）。</summary>
    public bool ShowTranslation { get; set; }

    /// <summary>显示歌名歌手那一行。</summary>
    public bool ShowTrackInfo { get; set; } = true;

    // ---------- 行为 ----------

    /// <summary>锁定：锁定后鼠标点击穿透到下面的窗口（玩游戏用），解锁时可以用鼠标拖动。</summary>
    public bool Locked { get; set; }

    /// <summary>暂停时把整个歌词窗藏起来。</summary>
    public bool HideWhenPaused { get; set; }

    /// <summary>找不到歌词时的提示文字。</summary>
    public string NoLyricText { get; set; } = "（这首歌没有歌词）";

    /// <summary>窗口位置（-9999 = 还没定过，第一次用默认位置）。</summary>
    public double WindowX { get; set; } = -9999;

    public double WindowY { get; set; } = -9999;

    public double WindowWidth { get; set; } = 1100;

    public double WindowHeight { get; set; } = 190;

    // ---------- 数据源 ----------

    /// <summary>指定要跟随哪个媒体会话（空 = 自动优先网易云）。</summary>
    public string PreferredSource { get; set; } = "";

    /// <summary>歌词提前量（毫秒）：画面要跟人声对齐，通常得比 SMTC 给的进度稍微提前一点。</summary>
    public double LyricOffsetMs { get; set; } = 0;

    /// <summary>字幕跟不上/有延迟时，这里给用户一个整体微调的量（正数 = 歌词显示得更早）。</summary>
    public double RefreshIntervalMs { get; set; } = 120;
}
