using System.Windows.Media;

namespace MinecraftChatOverlay.Plugins.KeyDisplay;

/// <summary>
/// 彩色循环：让颜色沿**完整 RGB 光谱**跑（红→橙→黄→绿→青→蓝→紫→红）。
///
/// **它跟"色相旋转"的区别**（也是重做这一版的原因）：
/// 色相旋转是拿用户选的颜色当起点、只换"是什么颜色"，明暗和饱和度捆绑在源色上；
/// RGB 循环走的是**固定的加色通道路径** —— 也就是屏幕发光的原始方式，
/// 能扫到纯红、纯绿、纯蓝这些色相旋转里不会出现的原色，观感更"冲"。
///
/// **⚠ 打开开关后不再以原色为基准**（gold_ 明确要求）：
/// 早先那版把光谱"对齐到源色色相"、还按源色明度缩放，结果是 ——
/// 用户选了纯白文字或纯黑阴影（默认配色就是这一套），打开开关**几乎看不出在动**
/// （S=0 的色相无从谈起、明度又全是 0 或满），直接理解成"这功能坏了"。
/// 现在：**只要开着循环，就无条件从光谱上取色** ——
/// 纯白会跑成红/橙/黄/绿…，纯黑阴影也会跑成暗色彩，
/// **开了就一定能看见在变**。原色只用来决定"什么时候回原样"（见下）。
///
/// **不改数据**：这里只负责算"当前该用什么颜色"，配置里存的 hex 一个字都不动。
/// 关掉开关立刻回到原色，来回切不累积误差。
///
/// **首尾接得上**：进度 0 落在光谱起点（纯红），1.0 也绕回起点，
/// 所以循环是连续的、不会在接缝处"跳"一下。
/// </summary>
internal static class ColorCycle
{
    /// <summary>
    /// 按 <paramref name="progress"/>（0~1 = 走完一整圈的比例）算出这条 RGB 光谱上的颜色。
    ///
    /// 只保留源色的 **Alpha**（透明度是用户的意图，跟颜色无关，必须带过去）；
    /// RGB 三个通道**完全由光谱决定**，与源色无关。
    /// </summary>
    public static Color Shift(Color color, double progress)
    {
        // 完全透明的东西本来就看不见，别浪费力气算，也免得把它"点亮"了
        if (color.A == 0)
        {
            return color;
        }

        var hue = NormalizedProgress(progress) * 360.0;

        SpectrumToRgb(hue, out var r, out var g, out var b);

        return Color.FromArgb(color.A, ToByte(r), ToByte(g), ToByte(b));
    }

    /// <summary>
    /// 把进度归一化到 0~1（负数、超范围、NaN 一律收进来）。
    /// </summary>
    private static double NormalizedProgress(double progress)
    {
        if (double.IsNaN(progress) || double.IsInfinity(progress))
        {
            return 0;
        }

        var p = progress % 1.0;
        return p < 0 ? p + 1.0 : p;
    }

    /// <summary>
    /// RGB 光谱：给定色相角（0~360）返回该处**满亮度、满饱和**的纯色分量（0~1）。
    ///
    /// 这就是加色通道此消彼长的本来面目 —— 每一段都是两条通道线性交叉，
    /// 360° 拆成 6 段、每段 60°：
    ///   红→黄（R 满、G 涨）→ 黄→绿（R 落、G 满）→ 绿→青（G 满、B 涨）→
    ///   青→蓝（G 落、B 满）→ 蓝→紫（B 满、R 涨）→ 紫→红（B 落、R 满）
    /// 走完正好回到起点，所以循环是连续的。
    /// </summary>
    private static void SpectrumToRgb(double hue, out double r, out double g, out double b)
    {
        hue = ((hue % 360) + 360) % 360;

        var sector = (int)Math.Floor(hue / 60.0) % 6;
        var t = (hue / 60.0) - Math.Floor(hue / 60.0);

        switch (sector)
        {
            case 0: r = 1; g = t; b = 0; break;            // 红 → 黄
            case 1: r = 1 - t; g = 1; b = 0; break;        // 黄 → 绿
            case 2: r = 0; g = 1; b = t; break;            // 绿 → 青
            case 3: r = 0; g = 1 - t; b = 1; break;        // 青 → 蓝
            case 4: r = t; g = 0; b = 1; break;            // 蓝 → 紫
            default: r = 1; g = 0; b = 1 - t; break;       // 紫 → 红
        }
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
