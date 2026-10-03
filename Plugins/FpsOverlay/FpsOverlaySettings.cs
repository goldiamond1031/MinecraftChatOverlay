namespace MinecraftChatOverlay.Plugins.FpsOverlay;

/// <summary>
/// FPS 悬浮窗的全部可配置项。
/// 加了新的引用类型字段记得在加载后兜底（老配置里没有这个键会反序列化成 null）。
/// </summary>
public sealed class FpsOverlaySettings
{
    // ===================== 目标进程 =====================

    /// <summary>上次选的目标 pid（0 = 还没选）。</summary>
    public int TargetProcessId { get; set; }

    public string TargetProcessName { get; set; } = "";

    public string TargetWindowTitle { get; set; } = "";

    // ===================== 显示内容 =====================

    /// <summary>数字前面的文字，例如 "FPS "。</summary>
    public string Prefix { get; set; } = "FPS ";

    /// <summary>数字后面的文字，例如 " 帧"。</summary>
    public string Suffix { get; set; } = "";

    /// <summary>小数位：0 / 1 / 2。</summary>
    public int Decimals { get; set; }

    // ===================== 文字 =====================

    public string FontFamily { get; set; } = "Consolas";

    public double FontSize { get; set; } = 22;

    /// <summary>Normal / SemiBold / Bold / ExtraBold / Black。</summary>
    public string FontWeight { get; set; } = "Bold";

    /// <summary>#RRGGBB。</summary>
    public string Foreground { get; set; } = "#FFFFFF";

    // ===================== 文字阴影 =====================

    public bool ShadowEnabled { get; set; } = true;

    public string ShadowColor { get; set; } = "#000000";

    public double ShadowBlur { get; set; } = 3;

    public double ShadowOffsetX { get; set; } = 1;

    public double ShadowOffsetY { get; set; } = 1;

    public double ShadowOpacity { get; set; } = 0.85;

    // ===================== 背景 =====================

    public bool BackgroundEnabled { get; set; } = true;

    public string BackgroundColor { get; set; } = "#000000";

    public double BackgroundOpacity { get; set; } = 0.35;

    public double CornerRadius { get; set; } = 6;

    public double PaddingX { get; set; } = 10;

    public double PaddingY { get; set; } = 4;

    // ===================== 位置 =====================

    /// <summary>NaN = 还没定过位（用默认的屏幕右上角）。</summary>
    public double Left { get; set; } = double.NaN;

    public double Top { get; set; } = double.NaN;

    // ===================== 行为 =====================

    /// <summary>鼠标穿透，默认开 —— 免得挡住游戏里的点击。</summary>
    public bool ClickThrough { get; set; } = true;

    /// <summary>悬浮窗是不是显示着。</summary>
    public bool OverlayVisible { get; set; } = true;

    // ===================== 采样 =====================

    /// <summary>多久读一次帧计数（毫秒）。</summary>
    public int SampleIntervalMs { get; set; } = 100;

    /// <summary>算 FPS 用的滑动窗口（毫秒）。窗口越长越稳，越短越跟手。</summary>
    public int WindowMs { get; set; } = 600;
}
