namespace MinecraftChatOverlay.Models;

/// <summary>
/// 「窗口全屏」页的设置：把游戏窗口改成无边框全屏，这样悬浮窗才能盖在游戏画面上。
///
/// 注意这里只存"用户的选择"，不存运行时状态（改过的窗口句柄之类的都在
/// <see cref="Services.BorderlessWindow.GameWindowFullscreen"/> 里，退出即失效）。
/// </summary>
public sealed class WindowFullscreenSettings
{
    /// <summary>上次选中的窗口进程名（一般是 javaw.exe），刷新列表时自动选中。</summary>
    public string TargetProcessName { get; set; } = "";

    /// <summary>上次选中的窗口标题，进程名有多个候选时用它挑回同一个窗口。</summary>
    public string TargetWindowTitle { get; set; } = "";

    /// <summary>true = 铺满整个显示器（连任务栏一起盖住）；false = 只铺满工作区，任务栏留着。</summary>
    public bool CoverTaskbar { get; set; } = true;

    /// <summary>关软件时自动把游戏窗口还原成原来的样子（默认开，免得游戏一直是改过的状态）。</summary>
    public bool RestoreOnExit { get; set; } = true;
}