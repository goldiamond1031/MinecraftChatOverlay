namespace MinecraftChatOverlay.Plugins.RegionMagnifier;

/// <summary>
/// 「区域放大」：把游戏窗口里框出来的一小块，实时放大显示到屏幕别处
/// （例如把物品栏最后一格放大挂到画面右上角）。
///
/// 坐标约定：选区一律用**物理像素**存（跟抓屏坐标系一致，避免多 DPI 下换算错位）；
/// 位置/大小用 DIP 存（WPF 窗口坐标）。RelativeToWindow=true 时选区的原点是
/// 游戏窗口**客户区左上角**，所以窗口移动、改分辨率之后选区还跟着走。
/// </summary>
public sealed class RegionMagnifierSettings
{
    /// <summary>总开关：关掉就不抓屏（省 CPU）。</summary>
    public bool Enabled { get; set; }

    /// <summary>目标进程名（javaw / java / 别的）；空则只按标题找。</summary>
    public string TargetProcessName { get; set; } = "javaw";

    /// <summary>标题关键字（可空）。同进程有多个窗口时用它挑。</summary>
    public string TargetTitleHint { get; set; } = "";

    /// <summary>true=选区相对游戏窗口客户区（推荐）；false=相对整个屏幕。</summary>
    public bool RelativeToWindow { get; set; } = true;

    /// <summary>选区左上角（相对客户区或屏幕，物理像素）。</summary>
    public int RegionX { get; set; }

    /// <summary>选区左上角 Y（相对客户区或屏幕，物理像素）。</summary>
    public int RegionY { get; set; }

    /// <summary>选区宽（物理像素）。</summary>
    public int RegionWidth { get; set; }

    /// <summary>选区高（物理像素）。</summary>
    public int RegionHeight { get; set; }

    /// <summary>
    /// 显示窗口左上角 X（DIP）。默认值故意写得偏右：首次显示时窗口会被
    /// ClampIntoScreen 拉到屏幕右上角（放大镜的常见摆法）；用户拖动过之后就记真实位置了。
    /// </summary>
    public double DisplayLeft { get; set; } = 1900;

    /// <summary>显示窗口左上角 Y（DIP）。</summary>
    public double DisplayTop { get; set; } = 40;

    /// <summary>放大倍数（1-8）。显示窗口大小 = 选区大小 × 倍数 ÷ DPI 缩放。</summary>
    public double Zoom { get; set; } = 4;

    /// <summary>抓取帧率上限（5-60）。只是上限，抓不动就自然掉帧。</summary>
    public int Fps { get; set; } = 20;

    /// <summary>鼠标穿透（开了就不能拖窗口，但不会挡住游戏点击）。</summary>
    public bool ClickThrough { get; set; }

    /// <summary>平滑放大（像素画建议关掉，用最近邻更清楚）。</summary>
    public bool SmoothScale { get; set; }

    /// <summary>显示窗口带一圈边框（好认边界）。</summary>
    public bool ShowBorder { get; set; } = true;

    /// <summary>
    /// 把显示窗口从抓屏里排除（Windows 10 2004+ 的 WDA_EXCLUDEFROMCAPTURE）。
    /// 不做的话，选区一旦和显示窗口重叠就会自己拍自己。
    /// </summary>
    public bool ExcludeFromCapture { get; set; } = true;
}
