namespace MinecraftChatOverlay.Plugins.ProcessFps;

/// <summary>FPS 悬浮窗的全部设置。改完立刻存盘。</summary>
public sealed class ProcessFpsSettings
{
    // ---- 开关 ----
    public bool Enabled { get; set; } = true;

    // ---- 目标（只用来展示和确认进程还在，帧率本身来自共享内存）----
    public string TargetProcessName { get; set; } = "";
    public string TargetWindowTitle { get; set; } = "";
    public int TargetProcessId { get; set; }

    // ---- 文本 ----
    public string Prefix { get; set; } = "FPS ";
    public string Suffix { get; set; } = "";
    public int Decimals { get; set; }
    public int RefreshMs { get; set; } = 250;
    public string NoDataText { get; set; } = "--";

    // ---- 字体与颜色 ----
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 28;
    public bool Bold { get; set; } = true;
    public string TextColor { get; set; } = "#FFFFFFFF";

    // ---- 阴影 ----
    public bool ShadowEnabled { get; set; } = true;
    public string ShadowColor { get; set; } = "#FF000000";
    public double ShadowBlur { get; set; } = 6;
    public double ShadowOpacity { get; set; } = 0.8;
    public double ShadowOffset { get; set; } = 2;
    public double ShadowDirection { get; set; } = 45;

    // ---- 背景 ----
    public string BackgroundColor { get; set; } = "#00000000";
    public double CornerRadius { get; set; } = 10;
    public double PaddingX { get; set; } = 12;
    public double PaddingY { get; set; } = 4;

    // ---- 行为 ----
    public bool ClickThrough { get; set; }
    public bool HideWhenNoData { get; set; } = true;

    // ---- 位置 ----
    public double WindowLeft { get; set; } = 40;
    public double WindowTop { get; set; } = 40;
}
