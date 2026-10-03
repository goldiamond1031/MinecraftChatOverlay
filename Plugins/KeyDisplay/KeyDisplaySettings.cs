namespace MinecraftChatOverlay.Plugins.KeyDisplay;

/// <summary>悬浮窗上的一个按键格子。</summary>
public sealed class KeyCell
{
    /// <summary>虚拟键码。鼠标键是 0x01 左 / 0x02 右 / 0x04 中 / 0x05 侧1 / 0x06 侧2。</summary>
    public int VirtualKey { get; set; }

    /// <summary>显示的文字。留空 = 用这个键的默认名字（W 键就是「W」）。</summary>
    public string DisplayText { get; set; } = "";

    /// <summary>
    /// 在文字下方显示 CPS（最近一秒的点击次数）。
    /// 只有鼠标左键（0x01）和右键（0x02）有意义 —— 其它键没有"每秒按几下"的概念。
    /// </summary>
    public bool ShowCps { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 46;
    public double Height { get; set; } = 46;

    // ---- 每键覆盖项：留空 / 负数 = 跟随下面的全局设置 ----

    public string TextColorOverride { get; set; } = "";
    public string BackgroundColorOverride { get; set; } = "";
    public string PressedTextColorOverride { get; set; } = "";
    public string PressedBackgroundColorOverride { get; set; } = "";

    /// <summary>字体名。空 = 跟随全局。</summary>
    public string FontFamilyOverride { get; set; } = "";

    /// <summary>字号。-1 = 跟随全局（用 -1 而不是 0，是为了让 0 能当合法的覆盖值，比如圆角设成直角）。</summary>
    public double FontSizeOverride { get; set; } = -1;

    /// <summary>粗体。-1 跟随 / 0 关 / 1 开。</summary>
    public int BoldOverride { get; set; } = -1;

    /// <summary>圆角。-1 = 跟随全局。</summary>
    public double CornerRadiusOverride { get; set; } = -1;

    /// <summary>边框粗细。-1 = 跟随全局。</summary>
    public double BorderThicknessOverride { get; set; } = -1;

    /// <summary>边框颜色。空 = 跟随全局。</summary>
    public string BorderColorOverride { get; set; } = "";

    /// <summary>阴影开关。-1 跟随 / 0 关 / 1 开。</summary>
    public int ShadowEnabledOverride { get; set; } = -1;

    /// <summary>阴影颜色。空 = 跟随全局。</summary>
    public string ShadowColorOverride { get; set; } = "";

    /// <summary>阴影模糊半径。-1 = 跟随全局。</summary>
    public double ShadowBlurOverride { get; set; } = -1;

    /// <summary>阴影偏移。-1 = 跟随全局。</summary>
    public double ShadowOffsetOverride { get; set; } = -1;

    /// <summary>阴影不透明度。-1 = 跟随全局。</summary>
    public double ShadowOpacityOverride { get; set; } = -1;

    /// <summary>阴影光照方向。-1 = 跟随全局。</summary>
    public double ShadowDirectionOverride { get; set; } = -1;
}

/// <summary>
/// 按键显示插件的全部设置。
///
/// ⚠ 下面这些默认值是 gold_ 自己调好的一整套（2026-10-03 固化的），
///   改默认值之前先问一句 —— 它决定了新用户第一次打开看到的样子。
/// </summary>
public sealed class KeyDisplaySettings
{
    // ---- 开关 ----
    public bool Enabled { get; set; } = true;

    /// <summary>轮询间隔（毫秒）。键位显示要跟手，默认 16 毫秒（约 60fps）。</summary>
    public int RefreshMs { get; set; } = 16;

    // ---- 全局外观 ----
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 18;
    public bool Bold { get; set; } = true;

    /// <summary>没按下时的文字颜色。</summary>
    public string IdleTextColor { get; set; } = "#FFFFFFFF";

    /// <summary>没按下时的背景（默认黑色半透明）。</summary>
    public string IdleBackgroundColor { get; set; } = "#80000000";

    /// <summary>按下时的文字颜色。</summary>
    public string PressedTextColor { get; set; } = "#FF1A1A1A";

    /// <summary>按下时的背景（默认接近白色的高亮）。</summary>
    public string PressedBackgroundColor { get; set; } = "#FFFFFFFF";

    public double CornerRadius { get; set; } = 10;
    public double BorderThickness { get; set; }
    public string BorderColor { get; set; } = "#33FFFFFF";

    // ---- 阴影 ----
    public bool ShadowEnabled { get; set; } = true;
    public string ShadowColor { get; set; } = "#FF000000";
    public double ShadowBlur { get; set; } = 10;
    public double ShadowOpacity { get; set; } = 0.7;
    public double ShadowOffset { get; set; }
    public double ShadowDirection { get; set; } = 260;

    // ---- 果冻动感 ----
    /// <summary>按下时整个格子缩一下、松手带过冲弹回来。</summary>
    public bool DynamicEnabled { get; set; } = true;

    /// <summary>Q 弹力度 0~100。越大缩得越狠、回弹抖得越久。</summary>
    public double DynamicForce { get; set; } = 35;

    // ---- 画布与网格 ----
    public bool SnapToGrid { get; set; } = true;
    public double GridSize { get; set; } = 5;

    // ---- 行为 ----
    /// <summary>鼠标穿透。开着时点不到这个窗口（按键显示盖在游戏上必须能穿透）。</summary>
    public bool ClickThrough { get; set; }

    public double WindowLeft { get; set; } = 752;
    public double WindowTop { get; set; } = 442;

    /// <summary>按键格子列表。</summary>
    public List<KeyCell> Keys { get; set; } = new();

    /// <summary>
    /// 默认按键布局：WASD + 空格 + 鼠标左右键，摆成 PVP 客户端常见的样子。
    /// 坐标和尺寸是 gold_ 自己拖好的那一套（空格拉宽、两个鼠标键并排放在下面）。
    /// </summary>
    public static List<KeyCell> DefaultLayout()
    {
        return new List<KeyCell>
        {
            // W 在上，ASD 在中间一排
            new KeyCell { VirtualKey = 0x57, X = 50, Y = 0, Width = 46, Height = 46 },
            new KeyCell { VirtualKey = 0x41, X = 0, Y = 50, Width = 46, Height = 46 },
            new KeyCell { VirtualKey = 0x53, X = 50, Y = 50, Width = 46, Height = 46 },
            new KeyCell { VirtualKey = 0x44, X = 100, Y = 50, Width = 46, Height = 46 },

            // 空格横着拉宽
            new KeyCell { VirtualKey = 0x20, X = 0, Y = 100, Width = 145, Height = 30 },

            // 鼠标左右键并排
            new KeyCell { VirtualKey = 0x01, X = 0, Y = 135, Width = 60, Height = 45 },
            new KeyCell { VirtualKey = 0x02, X = 85, Y = 135, Width = 60, Height = 45 },
        };
    }
}
