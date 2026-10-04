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

    // ---- 彩色循环（每键覆盖：null = 跟随全局）----
    //
    // ⚠ 用 bool? 而不是 bool：这三个状态必须分得开 ——
    //    · null  = 这个格子没设过，跟随全局开关（全局开了它就跟着转）
    //    · true  = 这个格子单独设为"开启"
    //    · false = 这个格子单独设为"关闭"（全局开着，就这一个不变色）
    // 这也跟上面那些覆盖项（空串 / -1 = 跟随全局）是同一套约定。

    /// <summary>没按下时的文字颜色跟着色相循环转。null = 跟随全局。</summary>
    public bool? TextColorCycleOverride { get; set; }

    /// <summary>没按下时的背景颜色跟着循环。null = 跟随全局。</summary>
    public bool? BackgroundColorCycleOverride { get; set; }

    /// <summary>按下时的文字颜色跟着循环。null = 跟随全局。</summary>
    public bool? PressedTextColorCycleOverride { get; set; }

    /// <summary>按下时的背景颜色跟着循环。null = 跟随全局。</summary>
    public bool? PressedBackgroundColorCycleOverride { get; set; }

    /// <summary>边框颜色跟着循环。null = 跟随全局。</summary>
    public bool? BorderColorCycleOverride { get; set; }

    /// <summary>阴影颜色跟着循环。null = 跟随全局。</summary>
    public bool? ShadowColorCycleOverride { get; set; }
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

    /// <summary>
    /// 按键读取方式：轮询（默认）还是键盘钩子。
    ///
    /// 默认轮询的理由：零介入、不碰权限、卸载后什么都不剩 —— 大多数场景（桌面、Minecraft
    /// 这类走正常消息队列的程序）够用。只有要读**走 Raw Input 的游戏**（绝区零这类）时，
    /// 才需要用户主动切到钩子。
    ///
    /// ⚠ 存的是 enum，JSON 里就是数字（0=轮询 / 1=钩子）。settings.json 是给人看的，
    ///   但这里存数字更稳：以后加模式不会因为字符串拼写差异读不出来。
    /// </summary>
    public KeyInputMode InputMode { get; set; } = KeyInputMode.Polling;

    /// <summary>
    /// 防出屏：悬浮窗拖动/缩放时不许跑出屏幕。
    ///
    /// 默认开 —— 这是纯保护性行为，对绝大多数用户只有好处；
    /// 想把窗口一半藏在屏幕边缘外面那种玩法，关掉这个开关就行。
    /// </summary>
    public bool ClampToScreen { get; set; } = true;

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

    /// <summary>
    /// 整体缩放（0.5 ~ 2.0）。
    ///
    /// 它是**显示缩放**，不是改数据：格子的 X/Y/宽/高 存的还是原始值，
    /// 每次重建时乘上这个倍数。所以滑块来回拖不会累积误差，用户摆好的布局也不会被改掉。
    /// 字号、圆角、边框、阴影这些**一起缩放** —— 要的是"整个按键显示看起来大了/小了"，
    /// 不是只把格子拉大而字还那么点。
    /// </summary>
    public double OverallScale { get; set; } = 1.0;

    // ---- 彩色循环 ----
    //
    // 只对"能换颜色的那几处"各给一个开关。开着就把这个位置渲染时用的颜色往色相上推着走，
    // **配置里存的 hex 一个字都不改** —— 关掉开关立刻回到用户原来选的颜色，来回切不累积误差
    // （跟 OverallScale 那套"显示缩放不改数据"是同一个思路）。

    /// <summary>没按下时的文字颜色跟着色相循环转。</summary>
    public bool TextColorCycle { get; set; }

    /// <summary>没按下时的背景颜色跟着循环。</summary>
    public bool BackgroundColorCycle { get; set; }

    /// <summary>按下时的文字颜色跟着循环。</summary>
    public bool PressedTextColorCycle { get; set; }

    /// <summary>按下时的背景颜色跟着循环。</summary>
    public bool PressedBackgroundColorCycle { get; set; }

    /// <summary>边框颜色跟着循环。</summary>
    public bool BorderColorCycle { get; set; }

    /// <summary>阴影颜色跟着循环。</summary>
    public bool ShadowColorCycle { get; set; }

    /// <summary>
    /// 循环速度：转一整圈要多少秒。
    ///
    /// 默认 6 秒 —— 快过 2 秒会像警灯闪、看着累；慢过 15 秒又几乎看不出在动。
    /// </summary>
    public double CycleSeconds { get; set; } = 6;

    /// <summary>
    /// 有没有任何一处开了循环。没有的话插件连重绘都不做 —— 省得白白每秒重画一遍。
    ///
    /// ⚠ <c>Keys?</c> 那个问号是必要的：配置是从 JSON 读出来的，
    ///   文件里写成 <c>"Keys": null</c> 就会把它冲成 null（初始化器只管新建的对象）。
    /// </summary>
    public bool AnyColorCycle =>
        TextColorCycle || BackgroundColorCycle || PressedTextColorCycle
        || PressedBackgroundColorCycle || BorderColorCycle || ShadowColorCycle
        || Keys?.Any(k => k.TextColorCycleOverride == true
                          || k.BackgroundColorCycleOverride == true
                          || k.PressedTextColorCycleOverride == true
                          || k.PressedBackgroundColorCycleOverride == true
                          || k.BorderColorCycleOverride == true
                          || k.ShadowColorCycleOverride == true) == true;

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
