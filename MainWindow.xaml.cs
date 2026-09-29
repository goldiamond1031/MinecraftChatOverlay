using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Services;
using System.Windows.Media.Effects;  // 提供 DropShadowEffect

namespace MinecraftChatOverlay;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MinecraftLogWatcher _watcher = new();
    private readonly ObservableCollection<TextColorRule> _colorRules = new();
    private readonly ObservableCollection<TextReplaceRule> _replaceRules = new();
    private readonly ObservableCollection<BlockKeywordItem> _blockKeywords = new();

    private readonly List<FontItem> _fontItems = new();
    private OverlayWindow? _overlay;

    // ---------- B站弹幕模块（独立弹幕窗，与游戏聊天窗口互不干扰） ----------
    private readonly BiliSettings _biliSettings;
    private readonly ObservableCollection<GiftColorItem> _biliGiftColors = new();
    private readonly List<string> _biliLogBuffer = new();
    private ILiveClient? _biliClient;
    private BiliOverlayWindow? _biliOverlay;
    private FaceCache? _biliFaceCache;
    private string _biliLastRoomSnapshot = "";
    private int _biliRealOnline;
    private RoomInfo? _biliCurrentRoom;
    private string _biliGiftEditColor = "#FFFFFF";

    /// <summary>B站面板正在从配置回填控件时为 true，避免回填过程触发一次无谓的保存。</summary>
    private bool _biliLoading = true;
    private bool _loading = true;

    // ---------- AI 识图 ----------
    private ScreenshotWatcher? _screenshotWatcher;
    private bool _aiBusy;
    private string _colorRuleColor = "#FFFF0000";
    private string _colorRuleMatchColor = "";
    private readonly MediaPlayer _cialloPlayer = new();
    private bool _cialloBusy;

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowIcon();
        LoadBrandAssets();
        _settings = SettingsService.Load();

        // 卡片的"锁定态"要用绑定读设置对象，所以 DataContext 必须是窗口自己。
        // 放在 Load 之后：绑定的 getter 会解引用 _settings，早于赋值会空引用。
        DataContext = this;

        InitializeComboBoxes();
        LoadRuleCollections();
        LoadUiFromSettings();
        LoadAiUiFromSettings();


        _loading = false;
        SubscribeImmediateApply();

        // 上次退出时如果勾着"启用自动识别"，这里要真的把监听器启动起来
        // （回填控件时 _loading 还是 true，那次的保存被跳过了，不会启动监听）
        UpdateScreenshotWatcher();

        // B站弹幕模块：独立配置文件 + 独立悬浮窗
        _biliSettings = BiliSettingsService.Load();
        BiliGiftColorListBox.ItemsSource = _biliGiftColors;
        LoadBiliUiFromSettings();
        SubscribeBiliImmediateApply();
        BiliLog(Copy.BiliConfigLoaded + BiliSettingsService.ConfigPath);

        Loaded += MainWindow_Loaded;
        LogStatus(Copy.ConfigLoaded + SettingsService.ConfigPath);
    }

private void MainWindow_Loaded(object sender, RoutedEventArgs e)
{
        MoveIndicatorToSelected();
        InitializeMotionBlurUi();
        InitializeSoundNotifyUi();
        InitializeKillFeedUi();
        InitializeKillSoundUi();
        InitializePluginsUi();
        InitializePluginMarketUi();

        AppVersionText.Text = Copy.AppVersionPrefix + Copy.AppVersion;

        // 每次打开软件顺手查一次更新（失败不弹窗）
        _ = AutoCheckUpdateAsync();

        // 启动时检查 B站登录态：Cookie 会过期，早提醒比连不上才发现好
        _ = CheckBiliLoginStateAsync();
    
    // 加载主题
    _isDarkMode = _settings.IsDarkMode;
    ApplyTheme(_isDarkMode);
    UpdateListeningIndicator(_watcher.IsRunning);
    
    if (!_watcher.IsRunning && !string.IsNullOrWhiteSpace(_settings.LogPath))
    {
        StartListening();
    }
}

    private void LoadWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "MinecraftChatOverlay.ico");
            if (File.Exists(iconPath))
            {
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath, UriKind.Absolute));
            }
        }
        catch { }
    }

    private void LoadBrandAssets()
    {
        try
        {
            var logoPath = ResolveAssetPath("LOGO256x.png");
            if (File.Exists(logoPath))
            {
                TitleLogoImage.Source = new BitmapImage(new Uri(logoPath, UriKind.Absolute));
            }
        }
        catch
        {
        }

        try
        {
            var imagePath = ResolveAssetPath("res", "ciallo.png");
            if (File.Exists(imagePath))
            {
                CialloImage.Source = new BitmapImage(new Uri(imagePath, UriKind.Absolute));
            }
        }
        catch
        {
        }

        try
        {
            // 优先使用 mp3：Windows 的 MediaPlayer 对 mp3 支持最稳定，ogg 可能因缺少解码器无声。
            var audioPath = ResolveAssetPath("res", "ciallo.mp3");
            if (!File.Exists(audioPath))
            {
                audioPath = ResolveAssetPath("res", "ciallo.ogg");
            }
            if (!File.Exists(audioPath))
            {
                audioPath = ResolveAssetPath("ciallo.mp3");
            }

            if (File.Exists(audioPath))
            {
                _cialloPlayer.Volume = 1.0;
                _cialloPlayer.Open(new Uri(audioPath, UriKind.Absolute));
            }
        }
        catch
        {
        }
    }

    private static string ResolveAssetPath(params string[] relativeParts)
    {
        var relativePath = Path.Combine(relativeParts);
        var basePath = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (File.Exists(basePath))
        {
            return basePath;
        }

        var currentPath = Path.Combine(Environment.CurrentDirectory, relativePath);
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        // 开发/Vs 调试时，工作目录或输出目录可能不是项目根目录；向上查找一层，
        // 保证 LOGO256x.png、res/ciallo.* 能被找到。
        foreach (var startDirectory in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(startDirectory);
            for (var i = 0; i < 6 && directory != null; i++, directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return basePath;
    }

    private void InitializeComboBoxes()
    {
        _fontItems.Clear();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            var source = family.Source;
            var localized = GetLocalizedFontName(family);
            var display = string.IsNullOrEmpty(localized) || string.Equals(localized, source, StringComparison.OrdinalIgnoreCase)
                ? source
                : $"{localized} ({source})";
            _fontItems.Add(new FontItem(display, source));
        }

        _fontItems.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
        FontFamilyComboBox.ItemsSource = _fontItems;

        FontWeightComboBox.Items.Add("Normal");
        FontWeightComboBox.Items.Add("SemiBold");
        FontWeightComboBox.Items.Add("Bold");
        FontWeightComboBox.Items.Add("Light");

        LogEncodingComboBox.Items.Add("Auto");
        LogEncodingComboBox.Items.Add("UTF-8");
        LogEncodingComboBox.Items.Add("GBK");

        ColorRuleWeightComboBox.Items.Add("Normal");
        ColorRuleWeightComboBox.Items.Add("Thin");
        ColorRuleWeightComboBox.Items.Add("Light");
        ColorRuleWeightComboBox.Items.Add("SemiBold");
        ColorRuleWeightComboBox.Items.Add("Bold");
        ColorRuleWeightComboBox.SelectedItem = "Light";
    }

    private void ComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox &&
            comboBox.Template.FindName("PART_Popup", comboBox) is System.Windows.Controls.Primitives.Popup popup)
        {
            popup.PlacementTarget = comboBox;
            popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
    }




private bool _isDarkMode = false;

private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
{
    _isDarkMode = !_isDarkMode;
    ApplyTheme(_isDarkMode);
    UpdateListeningIndicator(_watcher.IsRunning);
    SaveThemePreference(_isDarkMode);
    LogStatus(_isDarkMode ? Copy.SwitchedToDark : Copy.SwitchedToLight);
}

private void ApplyTheme(bool darkMode)
{
    // 颜色定义
    Color primaryColor, primaryHoverColor, primaryPressedColor;
    Color accentColor, sidebarBgColor, contentBgColor, cardBgColor, borderColor;
    Color textPrimaryColor, textSecondaryColor, textOnPrimaryColor;
    Color switchTrackColor, switchTrackBorderColor, switchThumbColor;
    Color hoverBgColor, pressedBgColor;

    if (darkMode)
    {
        // 夜间模式：来自 GUI.html 的深色主题变量
        primaryColor = Color.FromRgb(0x8A, 0x8A, 0x96);
        primaryHoverColor = Color.FromRgb(0xA8, 0xA8, 0xB6);
        primaryPressedColor = Color.FromRgb(0x72, 0x72, 0x7E);
        accentColor = Color.FromRgb(0xB0, 0x8A, 0x9C);
        sidebarBgColor = Color.FromRgb(0x1F, 0x1F, 0x22);
        contentBgColor = Color.FromRgb(0x23, 0x23, 0x26);
        cardBgColor = Color.FromRgb(0x2A, 0x2A, 0x2E);
        borderColor = Color.FromRgb(0x3A, 0x3A, 0x41);
        textPrimaryColor = Color.FromRgb(0xF1, 0xF1, 0xF3);
        textSecondaryColor = Color.FromRgb(0xA9, 0xA9, 0xB4);
        textOnPrimaryColor = Colors.White;
        switchTrackColor = Color.FromRgb(0x4A, 0x4A, 0x52);
        switchTrackBorderColor = Color.FromRgb(0x4B, 0x4B, 0x54);
        switchThumbColor = Color.FromRgb(0xF1, 0xF1, 0xF3);
        hoverBgColor = Color.FromRgb(0x33, 0x33, 0x38);
        pressedBgColor = Color.FromRgb(0x3E, 0x3E, 0x45);
    }
    else
    {
        // 日间模式：来自 GUI.html 的浅色主题变量
        primaryColor = Color.FromRgb(0x7C, 0x6C, 0xF0);
        primaryHoverColor = Color.FromRgb(0xA1, 0x8C, 0xFF);
        primaryPressedColor = Color.FromRgb(0x5F, 0x4A, 0xD6);
        accentColor = Color.FromRgb(0xFF, 0x8F, 0xBE);
        sidebarBgColor = Color.FromRgb(0xFB, 0xFB, 0xFC);
        contentBgColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
        cardBgColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
        borderColor = Color.FromRgb(0xE7, 0xE7, 0xEC);
        textPrimaryColor = Color.FromRgb(0x2A, 0x2A, 0x31);
        textSecondaryColor = Color.FromRgb(0x6E, 0x6E, 0x7A);
        textOnPrimaryColor = Colors.White;
        switchTrackColor = Color.FromRgb(0xE4, 0xE4, 0xEA);
        switchTrackBorderColor = Color.FromRgb(0xD4, 0xD4, 0xDB);
        switchThumbColor = Colors.White;
        hoverBgColor = Color.FromRgb(0xF4, 0xF4, 0xF7);
        pressedBgColor = Color.FromRgb(0xEA, 0xEA, 0xEF);
    }

    // 替换资源字典里的画刷对象。
    // XAML 中对应引用已改为 DynamicResource，因此切换主题时控件会自动跟随新画刷。
    SetResourceBrush("PrimaryBrush", primaryColor);
    SetResourceBrush("PrimaryHoverBrush", primaryHoverColor);
    SetResourceBrush("PrimaryPressedBrush", primaryPressedColor);
    SetResourceBrush("AccentBrush", accentColor);
    SetResourceBrush("SidebarBgBrush", sidebarBgColor);
    SetResourceBrush("ContentBgBrush", contentBgColor);
    SetResourceBrush("CardBgBrush", cardBgColor);
    SetResourceBrush("BorderBrush", borderColor);
    SetResourceBrush("TextPrimaryBrush", textPrimaryColor);
    SetResourceBrush("TextSecondaryBrush", textSecondaryColor);
    SetResourceBrush("TextOnPrimaryBrush", textOnPrimaryColor);
    SetResourceBrush("HoverBgBrush", hoverBgColor);
    SetResourceBrush("PressedBgBrush", pressedBgColor);
    SetResourceBrush("NavHoverBrush", darkMode ? hoverBgColor : Color.FromRgb(0xF5, 0xF6, 0xFA));
    SetResourceBrush("SwitchTrackBrush", switchTrackColor);
    SetResourceBrush("SwitchTrackBorderBrush", switchTrackBorderColor);
    SetResourceBrush("SwitchThumbBrush", switchThumbColor);

    // GUI.html 里新增的语义色，ModernControls.xaml 中的控件会通过 DynamicResource 自动跟随。
    var surface2Color = darkMode ? Color.FromRgb(0x33, 0x33, 0x38) : Color.FromRgb(0xF4, 0xF4, 0xF7);
    var surface3Color = darkMode ? Color.FromRgb(0x3E, 0x3E, 0x45) : Color.FromRgb(0xEA, 0xEA, 0xEF);
    var borderStrongColor = darkMode ? Color.FromRgb(0x4B, 0x4B, 0x54) : Color.FromRgb(0xD4, 0xD4, 0xDB);
    var textTertiaryColor = darkMode ? Color.FromRgb(0x7D, 0x7D, 0x89) : Color.FromRgb(0x9B, 0x9B, 0xA7);
    var trackColor = darkMode ? Color.FromRgb(0x4A, 0x4A, 0x52) : Color.FromRgb(0xE4, 0xE4, 0xEA);
    var windowBgColor = darkMode ? Color.FromRgb(0x14, 0x14, 0x16) : Color.FromRgb(0xED, 0xED, 0xF1);
    var surfaceColor = cardBgColor;
    var primarySoftColor = Color.FromArgb(darkMode ? (byte)0x2E : (byte)0x1F, primaryColor.R, primaryColor.G, primaryColor.B);
    var primarySofterColor = Color.FromArgb(darkMode ? (byte)0x1A : (byte)0x12, primaryColor.R, primaryColor.G, primaryColor.B);
    var accentSoftColor = Color.FromArgb(darkMode ? (byte)0x26 : (byte)0x26, darkMode ? (byte)0xB0 : (byte)0xFF, darkMode ? (byte)0x8A : (byte)0x8F, darkMode ? (byte)0x9C : (byte)0xBE);
    var mintSoftColor = Color.FromArgb(0x26, darkMode ? (byte)0x5B : (byte)0x3F, darkMode ? (byte)0xBF : (byte)0xCF, darkMode ? (byte)0x9A : (byte)0xA9);
    var dangerSoftColor = Color.FromArgb(darkMode ? (byte)0x29 : (byte)0x21, darkMode ? (byte)0xD4 : (byte)0xFF, darkMode ? (byte)0x82 : (byte)0x6B, darkMode ? (byte)0x8E : (byte)0x81);

    SetResourceBrush("WindowBgBrush", windowBgColor);
    SetResourceBrush("SurfaceBrush", surfaceColor);
    SetResourceBrush("Surface2Brush", surface2Color);
    SetResourceBrush("Surface3Brush", surface3Color);
    SetResourceBrush("ContentBgBrush", contentBgColor);
    SetResourceBrush("BorderStrongBrush", borderStrongColor);
    SetResourceBrush("TextTertiaryBrush", textTertiaryColor);
    SetResourceBrush("PrimarySoftBrush", primarySoftColor);
    SetResourceBrush("PrimarySofterBrush", primarySofterColor);
    SetResourceBrush("AccentSoftBrush", accentSoftColor);
    SetResourceBrush("MintSoftBrush", mintSoftColor);
    SetResourceBrush("MintBrush", darkMode ? Color.FromRgb(0x5B, 0xBF, 0x9A) : Color.FromRgb(0x3F, 0xCF, 0xA9));
    SetResourceBrush("DangerBrush", darkMode ? Color.FromRgb(0xD4, 0x82, 0x8E) : Color.FromRgb(0xFF, 0x6B, 0x81));
    SetResourceBrush("DangerSoftBrush", dangerSoftColor);
    SetResourceBrush("TrackBrush", trackColor);
    SetResourceBrush("AmberBrush", darkMode ? Color.FromRgb(0xC9, 0xA8, 0x6A) : Color.FromRgb(0xFF, 0xB5, 0x47));
    SetResourceBrush("SkyBrush", darkMode ? Color.FromRgb(0x6F, 0xA8, 0xC9) : Color.FromRgb(0x57, 0xC5, 0xF2));
    SetResourceBrush("ConsoleBgBrush", Color.FromRgb(0x12, 0x10, 0x1F));
    SetResourceBrush("ConsoleTextBrush", Color.FromRgb(0xCF, 0xC9, 0xF5));
    SetResourceBrush("ConsoleMutedBrush", Color.FromRgb(0x6F, 0x6A, 0x9A));
    SetResourceBrush("ConsoleChatBrush", Color.FromRgb(0x9B, 0xE7, 0xC4));
    SetResourceBrush("ConsoleInfoBrush", Color.FromRgb(0xB4, 0xA8, 0xFF));
    SetResourceBrush("ConsoleWarnBrush", Color.FromRgb(0xFF, 0xC0, 0x69));

    Resources["GlowShadow"] = new DropShadowEffect
    {
        BlurRadius = 16,
        ShadowDepth = 0,
        Opacity = darkMode ? 0.42 : 0.35,
        Color = primaryColor
    };

    // 覆盖 WPF 默认控件使用的系统颜色，否则 ComboBox 的下拉 Popup
    // 和 ComboBoxItem 的选中/高亮状态仍然会用日间系统色。
    UpdateSystemColorResources(cardBgColor, borderColor, textPrimaryColor, textSecondaryColor, primaryColor, textOnPrimaryColor);

    // 让没有单独设置 Foreground 的 TextBlock 继承窗口前景色，
    // 避免卡片背景已变深但默认文字仍是日间黑色。
    Foreground = new SolidColorBrush(textPrimaryColor);
    System.Windows.Documents.TextElement.SetForeground(this, new SolidColorBrush(textPrimaryColor));
    if (Content is System.Windows.Controls.DockPanel rootPanel)
    {
        System.Windows.Documents.TextElement.SetForeground(rootPanel, new SolidColorBrush(textPrimaryColor));
    }

    // 更新主题切换按钮图标
    ThemeToggleButton.Content = darkMode ? "☀️" : "🌙";
    ThemeToggleButton.Foreground = new SolidColorBrush(textPrimaryColor);

    // 更新窗口背景
    Background = new SolidColorBrush(contentBgColor);

    // 更新标题栏
    TitleBarBorder.Background = new SolidColorBrush(cardBgColor);
    TitleBarBorder.BorderBrush = new SolidColorBrush(borderColor);
    TitleTextBlock.Foreground = darkMode ? new SolidColorBrush(textPrimaryColor) : new SolidColorBrush(primaryColor);

    // 更新侧边栏
    SidebarBorder.Background = new SolidColorBrush(sidebarBgColor);
    SidebarBorder.BorderBrush = new SolidColorBrush(borderColor);
    NavIndicator.Background = darkMode ? new SolidColorBrush(textPrimaryColor) : new SolidColorBrush(accentColor);
    NavIndicator.Effect = new DropShadowEffect
    {
        BlurRadius = 10,
        ShadowDepth = 0,
        Opacity = darkMode ? 0.35 : 0.45,
        Color = darkMode ? textPrimaryColor : accentColor
    };

    // 更新主内容区
    MainContentGrid.Background = new SolidColorBrush(contentBgColor);

    // 更新顶部工具栏
    ToolbarBorder.Background = new SolidColorBrush(cardBgColor);
    ToolbarBorder.BorderBrush = new SolidColorBrush(borderColor);

    // 更新底部状态栏
    StatusBarBorder.Background = new SolidColorBrush(cardBgColor);
    StatusBarBorder.BorderBrush = new SolidColorBrush(borderColor);
    StatusTextBlock.Foreground = new SolidColorBrush(textSecondaryColor);

    // 更新按钮样式资源（阴影等）。
    UpdateButtonStyles(darkMode, primaryColor, primaryHoverColor, primaryPressedColor, 
                       textPrimaryColor, textOnPrimaryColor, borderColor, hoverBgColor, pressedBgColor);

    // 文本框、列表、开关、卡片和导航按钮的 XAML 样式内部都使用 DynamicResource，
    // 上面的 SetResourceBrush 已经替换了画刷，因此不再用代码重建模板。
}

private void RefreshLoadedStyles()
{
    if (Resources[typeof(ListBoxItem)] is Style listBoxItemStyle)
    {
        ApplyStyleToAll<ListBoxItem>(listBoxItemStyle);
    }

    if (Resources[typeof(ListBox)] is Style listBoxStyle)
    {
        ApplyStyleToAll<ListBox>(listBoxStyle);
    }

    if (Resources[typeof(TextBox)] is Style textBoxStyle)
    {
        ApplyTextBoxStyleToDirectTextBoxes(textBoxStyle);
    }
}

private void ApplyTextBoxStyleToDirectTextBoxes(Style style)
{
    ApplyTextBoxStyleRecursive(this, style, new HashSet<DependencyObject>());
}

private void ApplyTextBoxStyleRecursive(DependencyObject parent, Style style, HashSet<DependencyObject> visited)
{
    if (!visited.Add(parent))
    {
        return;
    }

    foreach (var child in GetLogicalAndVisualChildren(parent))
    {
        if (child is TextBox textBox && textBox.TemplatedParent == null)
        {
            textBox.Style = style;
        }

        ApplyTextBoxStyleRecursive(child, style, visited);
    }
}

private void ApplyStyleToAll<T>(Style style) where T : FrameworkElement
{
    ApplyStyleRecursive<T>(this, style, new HashSet<DependencyObject>());
}

private void ApplyStyleRecursive<T>(DependencyObject parent, Style style, HashSet<DependencyObject> visited) where T : FrameworkElement
{
    if (!visited.Add(parent))
    {
        return;
    }

    foreach (var child in GetLogicalAndVisualChildren(parent))
    {
        if (child is T element)
        {
            element.Style = style;
        }

        ApplyStyleRecursive<T>(child, style, visited);
    }
}

private void ApplyComboBoxThemeToAll(Style style)
{
    ApplyComboBoxThemeRecursive(this, style, new HashSet<DependencyObject>());
}

private void ApplyComboBoxThemeRecursive(DependencyObject parent, Style style, HashSet<DependencyObject> visited)
{
    if (!visited.Add(parent))
    {
        return;
    }

    foreach (var child in GetLogicalAndVisualChildren(parent))
    {
        if (child is ComboBox comboBox)
        {
            comboBox.Style = style;

            // 直接设置本地值，保证默认 ComboBox 模板的主选框区域也跟随主题。
            if (Resources["CardBgBrush"] is Brush cardBg)
            {
                comboBox.Background = cardBg;
            }
            if (Resources["TextPrimaryBrush"] is Brush textPrimary)
            {
                comboBox.Foreground = textPrimary;
            }
            if (Resources["BorderBrush"] is Brush borderBrush)
            {
                comboBox.BorderBrush = borderBrush;
            }

            // 默认 ComboBox 模板内部会从控件自己的 Resources 里查找系统颜色，
            // 这里给每个 ComboBox 单独复制一份，确保主选框和 Popup 都使用主题色。
            ApplySystemColorResourcesToComboBox(comboBox);
            ApplyComboBoxTextBoxStyle(comboBox, Resources["TextPrimaryBrush"] as Brush);
        }

        ApplyComboBoxThemeRecursive(child, style, visited);
    }
}

private void ApplySystemColorResourcesToComboBox(ComboBox comboBox)
{
    var keys = new object[]
    {
        SystemColors.WindowBrushKey,
        SystemColors.WindowTextBrushKey,
        SystemColors.ControlBrushKey,
        SystemColors.ControlTextBrushKey,
        SystemColors.ControlDarkBrushKey,
        SystemColors.ControlLightBrushKey,
        SystemColors.ControlLightLightBrushKey,
        SystemColors.ControlDarkDarkBrushKey,
        SystemColors.HighlightBrushKey,
        SystemColors.HighlightTextBrushKey,
        SystemColors.InactiveSelectionHighlightBrushKey,
        SystemColors.InactiveSelectionHighlightTextBrushKey,
        SystemColors.GrayTextBrushKey,
        SystemColors.MenuBrushKey,
        SystemColors.MenuTextBrushKey
    };

    foreach (var key in keys)
    {
        if (Resources[key] is Brush brush)
        {
            comboBox.Resources[key] = brush;
        }
    }
}

private void ApplyComboBoxTextBoxStyle(ComboBox comboBox, Brush? textPrimary)
{
    if (textPrimary == null)
    {
        return;
    }

    // 可编辑 ComboBox 内部有一个 TextBox，单独给它一个透明、无边框的样式，
    // 避免主选框区域出现日间白色输入框。
    var style = new Style(typeof(TextBox));
    style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
    style.Setters.Add(new Setter(Control.ForegroundProperty, textPrimary));
    style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
    style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
    style.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0)));
    style.Setters.Add(new Setter(TextBox.CaretBrushProperty, textPrimary));
    comboBox.Resources[typeof(TextBox)] = style;
}

private static IEnumerable<DependencyObject> GetLogicalAndVisualChildren(DependencyObject parent)
{
    var seen = new HashSet<DependencyObject>();

    foreach (var child in LogicalTreeHelper.GetChildren(parent))
    {
        if (child is DependencyObject dependencyObject && seen.Add(dependencyObject))
        {
            yield return dependencyObject;
        }
    }

    if (parent is Visual)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (seen.Add(child))
            {
                yield return child;
            }
        }
    }
}

private void ApplyCardStyleToLoadedBorders(Style style)
{
    ApplyCardStyleRecursive(this, style);
}

private void ApplyCardStyleRecursive(DependencyObject parent, Style style)
{
    var count = VisualTreeHelper.GetChildrenCount(parent);
    for (var i = 0; i < count; i++)
    {
        var child = VisualTreeHelper.GetChild(parent, i);
        if (child is Border border && border.Style != null)
        {
            // 这个窗口里有具名 Style 的 Border 都是卡片（CardStyle）。
            border.Style = style;
        }

        ApplyCardStyleRecursive(child, style);
    }
}

private void UpdateSystemColorResources(Color cardBgColor, Color borderColor, Color textPrimaryColor,
                                        Color textSecondaryColor, Color primaryColor, Color textOnPrimaryColor)
{
    // 让 WPF 默认模板里使用的 SystemColors.* 也跟随主题，
    // 主要解决 ComboBox 下拉 Popup、ComboBoxItem 选中/高亮仍是日间色的问题。
    void SetSystemColor(object key, Color color)
    {
        var brush = new SolidColorBrush(color);
        Resources[key] = brush;
        if (Application.Current != null)
        {
            Application.Current.Resources[key] = brush;
        }
    }

    SetSystemColor(SystemColors.WindowBrushKey, cardBgColor);
    SetSystemColor(SystemColors.WindowTextBrushKey, textPrimaryColor);
    SetSystemColor(SystemColors.ControlBrushKey, cardBgColor);
    SetSystemColor(SystemColors.ControlTextBrushKey, textPrimaryColor);
    SetSystemColor(SystemColors.ControlDarkBrushKey, borderColor);
    SetSystemColor(SystemColors.ControlLightBrushKey, borderColor);
    SetSystemColor(SystemColors.ControlLightLightBrushKey, cardBgColor);
    SetSystemColor(SystemColors.ControlDarkDarkBrushKey, borderColor);
    SetSystemColor(SystemColors.HighlightBrushKey, primaryColor);
    SetSystemColor(SystemColors.HighlightTextBrushKey, textOnPrimaryColor);
    SetSystemColor(SystemColors.InactiveSelectionHighlightBrushKey, primaryColor);
    SetSystemColor(SystemColors.InactiveSelectionHighlightTextBrushKey, textOnPrimaryColor);
    SetSystemColor(SystemColors.GrayTextBrushKey, textSecondaryColor);
    SetSystemColor(SystemColors.MenuBrushKey, cardBgColor);
    SetSystemColor(SystemColors.MenuTextBrushKey, textPrimaryColor);
}

private void SetResourceBrush(string key, Color color)
{
    // XAML/BAML 中的 Freezable 资源可能已被冻结，不能原地改 Color；
    // 所以这里直接放入一个全新的可变 SolidColorBrush，
    // 并依靠 DynamicResource 让已加载控件自动更新。
    Resources[key] = new SolidColorBrush(color);
}

private void UpdateButtonStyles(bool darkMode, Color primaryColor, Color primaryHoverColor, 
                                 Color primaryPressedColor, Color textPrimaryColor, 
                                 Color textOnPrimaryColor, Color borderColor, 
                                 Color hoverBgColor, Color pressedBgColor)
{
    // 画刷已在 ApplyTheme 里通过 SetResourceBrush 替换为新的可变画刷，
    // 这里只需更新非画刷资源（颜色值、阴影效果）。
    Resources["PrimaryColor"] = primaryColor;

    // HoverShadow 同样可能是已冻结的 Freezable，因此直接整体替换。
    Resources["HoverShadow"] = new DropShadowEffect
    {
        BlurRadius = 16,
        ShadowDepth = 4,
        Opacity = darkMode ? 0.3 : 0.15,
        Color = Colors.Black
    };
}

private void UpdateTextBoxStyles(bool darkMode, Color cardBgColor, Color borderColor, 
                                  Color textPrimaryColor, Color primaryColor)
{
    // 更新所有 TextBox 的样式
    var textBoxStyle = new Style(typeof(TextBox));
    textBoxStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, new SolidColorBrush(cardBgColor)));
    textBoxStyle.Setters.Add(new Setter(TextBox.BorderBrushProperty, new SolidColorBrush(borderColor)));
    textBoxStyle.Setters.Add(new Setter(TextBox.ForegroundProperty, new SolidColorBrush(textPrimaryColor)));
    textBoxStyle.Setters.Add(new Setter(TextBox.CaretBrushProperty, new SolidColorBrush(textPrimaryColor)));
    textBoxStyle.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(1)));
    textBoxStyle.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(8, 4, 8, 4)));
    textBoxStyle.Setters.Add(new Setter(TextBox.MarginProperty, new Thickness(4)));
    textBoxStyle.Setters.Add(new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));
    
    // 更新模板
    var template = new ControlTemplate(typeof(TextBox));
    var borderElement = new FrameworkElementFactory(typeof(Border));
    borderElement.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
    borderElement.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(TextBox.BorderBrushProperty));
    borderElement.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(TextBox.BorderThicknessProperty));
    borderElement.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
    borderElement.SetValue(Border.PaddingProperty, new TemplateBindingExtension(TextBox.PaddingProperty));
    
    var scrollViewer = new FrameworkElementFactory(typeof(ScrollViewer));
    scrollViewer.Name = "PART_ContentHost";
    borderElement.AppendChild(scrollViewer);
    
    template.VisualTree = borderElement;
    textBoxStyle.Setters.Add(new Setter(TextBox.TemplateProperty, template));
    
    Resources[typeof(TextBox)] = textBoxStyle;
}

private void UpdateToggleSwitchStyles(bool darkMode, Color switchTrackColor, Color switchTrackBorderColor, 
                                       Color switchThumbColor, Color primaryColor, Color textPrimaryColor)
{
    // 更新 ToggleSwitchStyle
    var toggleStyle = new Style(typeof(CheckBox));
    toggleStyle.Setters.Add(new Setter(CheckBox.ForegroundProperty, new SolidColorBrush(textPrimaryColor)));
    toggleStyle.Setters.Add(new Setter(CheckBox.CursorProperty, Cursors.Hand));
    
    var template = new ControlTemplate(typeof(CheckBox));
    var stackPanel = new FrameworkElementFactory(typeof(StackPanel));
    stackPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
    stackPanel.SetValue(StackPanel.BackgroundProperty, Brushes.Transparent);
    
    var grid = new FrameworkElementFactory(typeof(Grid));
    grid.SetValue(Grid.WidthProperty, 44.0);
    grid.SetValue(Grid.HeightProperty, 24.0);
    grid.SetValue(Grid.VerticalAlignmentProperty, VerticalAlignment.Center);
    
    var track = new FrameworkElementFactory(typeof(Border));
    track.Name = "track";
    track.SetValue(Border.BackgroundProperty, new SolidColorBrush(switchTrackColor));
    track.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
    track.SetValue(Border.BorderBrushProperty, new SolidColorBrush(switchTrackBorderColor));
    track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
    
    var thumb = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
    thumb.Name = "thumb";
    thumb.SetValue(System.Windows.Shapes.Ellipse.WidthProperty, 18.0);
    thumb.SetValue(System.Windows.Shapes.Ellipse.HeightProperty, 18.0);
    thumb.SetValue(System.Windows.Shapes.Ellipse.FillProperty, new SolidColorBrush(switchThumbColor));
    thumb.SetValue(System.Windows.Shapes.Ellipse.HorizontalAlignmentProperty, HorizontalAlignment.Left);
    thumb.SetValue(System.Windows.Shapes.Ellipse.MarginProperty, new Thickness(2, 0, 0, 0));
    thumb.SetValue(System.Windows.Shapes.Ellipse.VerticalAlignmentProperty, VerticalAlignment.Center);
    thumb.SetValue(System.Windows.Shapes.Ellipse.EffectProperty, new DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.3, Color = Colors.Black });
    
    grid.AppendChild(track);
    grid.AppendChild(thumb);
    stackPanel.AppendChild(grid);
    
    var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
    contentPresenter.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 0, 0));
    contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
    contentPresenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
    stackPanel.AppendChild(contentPresenter);
    
    template.VisualTree = stackPanel;
    
    // 添加触发器
    var checkedTrigger = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
    // 直接设置颜色，不使用空 BeginStoryboard（否则 Style 密封时会抛异常）
    checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(primaryColor), "track"));
    checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(primaryColor), "track"));
    checkedTrigger.Setters.Add(new Setter(System.Windows.Shapes.Ellipse.MarginProperty, new Thickness(22, 0, 0, 0), "thumb"));
    template.Triggers.Add(checkedTrigger);
    
    var mouseOverTrigger = new Trigger { Property = CheckBox.IsMouseOverProperty, Value = true };
    mouseOverTrigger.Setters.Add(new Setter(Border.EffectProperty, new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.2, Color = Colors.Black }, "track"));
    template.Triggers.Add(mouseOverTrigger);
    
    toggleStyle.Setters.Add(new Setter(CheckBox.TemplateProperty, template));
    Resources["ToggleSwitchStyle"] = toggleStyle;
}

private void UpdateCardStyles(Color cardBgColor, Color borderColor)
{
    var cardStyle = new Style(typeof(Border));
    cardStyle.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(cardBgColor)));
    cardStyle.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(borderColor)));
    cardStyle.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
    cardStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(12)));
    cardStyle.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(16)));
    cardStyle.Setters.Add(new Setter(Border.MarginProperty, new Thickness(0, 0, 0, 12)));
    cardStyle.Setters.Add(new Setter(Border.EffectProperty, new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.08, Color = Colors.Black }));
    Resources["CardStyle"] = cardStyle;
}

private void UpdateNavRadioStyles(bool darkMode, Color textSecondaryColor, Color hoverBgColor, Color textPrimaryColor)
{
    var navStyle = new Style(typeof(RadioButton));
    navStyle.Setters.Add(new Setter(RadioButton.ForegroundProperty, new SolidColorBrush(textSecondaryColor)));
    navStyle.Setters.Add(new Setter(RadioButton.FontSizeProperty, 14.0));
    navStyle.Setters.Add(new Setter(RadioButton.FontWeightProperty, FontWeights.SemiBold));
    navStyle.Setters.Add(new Setter(RadioButton.MarginProperty, new Thickness(0, 2, 0, 2)));
    navStyle.Setters.Add(new Setter(RadioButton.CursorProperty, Cursors.Hand));
    
    var template = new ControlTemplate(typeof(RadioButton));
    var border = new FrameworkElementFactory(typeof(Border));
    border.Name = "border";
    border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
    border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
    border.SetValue(Border.PaddingProperty, new Thickness(14, 10, 14, 10));
    border.SetValue(Border.MarginProperty, new Thickness(5, 0, 0, 0));
    
    var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
    contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
    border.AppendChild(contentPresenter);
    
    template.VisualTree = border;
    
    var mouseOverTrigger = new Trigger { Property = RadioButton.IsMouseOverProperty, Value = true };
    mouseOverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hoverBgColor), "border"));
    mouseOverTrigger.Setters.Add(new Setter(Border.EffectProperty, new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.15, Color = Colors.Black }, "border"));
    template.Triggers.Add(mouseOverTrigger);
    
    navStyle.Setters.Add(new Setter(RadioButton.TemplateProperty, template));
    Resources["NavRadioButtonStyle"] = navStyle;
}

private void UpdateListAndComboBoxStyles(bool darkMode, Color cardBgColor, Color borderColor, Color textPrimaryColor)
{
    // 更新 ListBox 样式
    var listBoxStyle = new Style(typeof(ListBox));
    listBoxStyle.Setters.Add(new Setter(ListBox.BackgroundProperty, new SolidColorBrush(cardBgColor)));
    listBoxStyle.Setters.Add(new Setter(ListBox.BorderBrushProperty, new SolidColorBrush(borderColor)));
    listBoxStyle.Setters.Add(new Setter(ListBox.ForegroundProperty, new SolidColorBrush(textPrimaryColor)));
    Resources[typeof(ListBox)] = listBoxStyle;

    var listBoxItemStyle = new Style(typeof(ListBoxItem));
    listBoxItemStyle.Setters.Add(new Setter(ListBoxItem.ForegroundProperty, new SolidColorBrush(textPrimaryColor)));
    Resources[typeof(ListBoxItem)] = listBoxItemStyle;
}

private void SaveThemePreference(bool darkMode)
{
    try
    {
        _settings.EnableDebugLog = _settings.EnableDebugLog; // 触发保存
        _settings.IsDarkMode = darkMode;
        SettingsService.Save(_settings);
    }
    catch { }
}

    private void LoadRuleCollections()
    {
        _colorRules.Clear();
        foreach (var rule in _settings.ColorRules) _colorRules.Add(rule);

        _replaceRules.Clear();
        foreach (var rule in _settings.ReplaceRules) _replaceRules.Add(rule);

        _blockKeywords.Clear();
        foreach (var keyword in _settings.BlockKeywords) _blockKeywords.Add(keyword);

        ColorRuleListBox.ItemsSource = _colorRules;
        ReplaceRuleListBox.ItemsSource = _replaceRules;
        BlockKeywordListBox.ItemsSource = _blockKeywords;
    }

    private void LoadUiFromSettings()
    {
        LogPathTextBox.Text = _settings.LogPath;
        var logEncodingItem = LogEncodingComboBox.Items.Cast<object>()
            .FirstOrDefault(x => string.Equals(x?.ToString(), _settings.LogEncoding, StringComparison.OrdinalIgnoreCase));
        if (logEncodingItem != null)
        {
            LogEncodingComboBox.SelectedItem = logEncodingItem;
        }
        else if (LogEncodingComboBox.Items.Count > 0)
        {
            LogEncodingComboBox.SelectedIndex = 0;
        }
        OverlayWidthTextBox.Text = _settings.OverlayWidth.ToString("0.#");
        OpacitySlider.Value = Math.Clamp(_settings.OverlayOpacity, 0.1, 1.0);
        BackgroundOpacitySlider.Value = Math.Clamp(_settings.BackgroundOpacity, 0.0, 1.0);
        OverlayMaxHeightTextBox.Text = _settings.OverlayMaxHeight.ToString("0.#");
        WrapLengthTextBox.Text = _settings.WrapLength.ToString();
        MaxMessagesTextBox.Text = _settings.MaxMessages.ToString();
        ClickThroughCheckBox.IsChecked = _settings.ClickThrough;
        ShowTimestampCheckBox.IsChecked = _settings.ShowTimestamp;
        ShadowCheckBox.IsChecked = _settings.TextShadow;
        var fontItem = _fontItems.FirstOrDefault(x => string.Equals(x.Source, _settings.FontFamily, StringComparison.OrdinalIgnoreCase));
        if (fontItem != null)
        {
            FontFamilyComboBox.SelectedItem = fontItem;
        }
        else
        {
            fontItem = new FontItem(_settings.FontFamily, _settings.FontFamily);
            _fontItems.Insert(0, fontItem);
            FontFamilyComboBox.ItemsSource = null;
            FontFamilyComboBox.ItemsSource = _fontItems;
            FontFamilyComboBox.SelectedItem = fontItem;
        }

        FontSizeTextBox.Text = _settings.FontSize.ToString("0.#");
        FontWeightComboBox.SelectedItem = FontWeightComboBox.Items.Cast<string>().FirstOrDefault(x => string.Equals(x, _settings.FontWeight, StringComparison.OrdinalIgnoreCase));
        TextColorPreview.Background = ParseBrush(_settings.TextColor, Brushes.White);
        BackgroundColorPreview.Background = ParseBrush(_settings.BackgroundColor, new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
        PlayerContentColorPreview.Background = string.IsNullOrWhiteSpace(_settings.PlayerContentColor) ? Brushes.Transparent : ParseBrush(_settings.PlayerContentColor, Brushes.White);
        PlayerNameColorPreview.Background = string.IsNullOrWhiteSpace(_settings.PlayerNameColor) ? Brushes.Transparent : ParseBrush(_settings.PlayerNameColor, Brushes.White);

        TextColorHexText.Text = FormatHex(_settings.TextColor);
        BackgroundColorHexText.Text = FormatHex(_settings.BackgroundColor);
        PlayerContentColorHexText.Text = FormatHex(_settings.PlayerContentColor);
        PlayerNameColorHexText.Text = FormatHex(_settings.PlayerNameColor);

        EnableDebugLogCheckBox.IsChecked = _settings.EnableDebugLog;
        EnableTextSelectionCheckBox.IsChecked = _settings.EnableTextSelection;
        MergeDuplicateMessagesCheckBox.IsChecked = _settings.MergeDuplicateMessages;
        EnableMessageAnimationCheckBox.IsChecked = _settings.EnableMessageAnimation;
        UpdateDebugLogVisibility();
        UpdateDebugLogCount();
    }

    private void SubscribeImmediateApply()
    {
        OpacitySlider.ValueChanged += (_, _) => SaveSettingsFromUi(false);
        BackgroundOpacitySlider.ValueChanged += (_, _) => SaveSettingsFromUi(false);
        ClickThroughCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        ClickThroughCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        ShowTimestampCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        ShowTimestampCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        ShadowCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        ShadowCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        EnableTextSelectionCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        EnableTextSelectionCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        MergeDuplicateMessagesCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        MergeDuplicateMessagesCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        EnableMessageAnimationCheckBox.Checked += (_, _) => SaveSettingsFromUi(false);
        EnableMessageAnimationCheckBox.Unchecked += (_, _) => SaveSettingsFromUi(false);
        LogPathTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        OverlayWidthTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        OverlayMaxHeightTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        WrapLengthTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        MaxMessagesTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        FontFamilyComboBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        FontSizeTextBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        FontWeightComboBox.SelectionChanged += (_, _) => SaveSettingsFromUi(false);
        FontWeightComboBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        LogEncodingComboBox.SelectionChanged += (_, _) => SaveSettingsFromUi(false);
        LogEncodingComboBox.LostFocus += (_, _) => SaveSettingsFromUi(false);
        FontFamilyComboBox.SelectionChanged += (_, _) => SaveSettingsFromUi(false);

    }

    private void SaveSettingsFromUi(bool log = true)
    {
        if (_loading) return;

        _settings.LogPath = LogPathTextBox.Text.Trim();
        var logEncoding = LogEncodingComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(logEncoding))
        {
            logEncoding = "Auto";
        }
        var logEncodingChanged = !string.Equals(_settings.LogEncoding, logEncoding, StringComparison.OrdinalIgnoreCase);
        _settings.LogEncoding = logEncoding;
        _settings.OverlayWidth = ParseDouble(OverlayWidthTextBox.Text, 420, 120, 2000);
        _settings.OverlayOpacity = OpacitySlider.Value;
        _settings.BackgroundOpacity = BackgroundOpacitySlider.Value;
        _settings.OverlayMaxHeight = ParseDouble(OverlayMaxHeightTextBox.Text, 620, 100, 2000);
        _settings.WrapLength = ParseInt(WrapLengthTextBox.Text, 40, 5, 2000);
        _settings.MaxMessages = ParseInt(MaxMessagesTextBox.Text, 200, 1, 2000);
        _settings.ClickThrough = ClickThroughCheckBox.IsChecked == true;
        _settings.ShowTimestamp = ShowTimestampCheckBox.IsChecked == true;
        _settings.TextShadow = ShadowCheckBox.IsChecked == true;
        if (FontFamilyComboBox.SelectedItem is FontItem selectedFont) _settings.FontFamily = selectedFont.Source;
        else _settings.FontFamily = string.IsNullOrWhiteSpace(FontFamilyComboBox.Text) ? "Microsoft YaHei UI" : FontFamilyComboBox.Text.Trim();
        _settings.FontSize = ParseDouble(FontSizeTextBox.Text, 16, 8, 96);
        _settings.FontWeight = FontWeightComboBox.SelectedItem as string ?? "Normal";
        _settings.EnableDebugLog = EnableDebugLogCheckBox.IsChecked == true;
        _settings.EnableTextSelection = EnableTextSelectionCheckBox.IsChecked == true;
        _settings.MergeDuplicateMessages = MergeDuplicateMessagesCheckBox.IsChecked == true;
        _settings.EnableMessageAnimation = EnableMessageAnimationCheckBox.IsChecked == true;
        _settings.ColorRules = _colorRules.ToList();
        _settings.ReplaceRules = _replaceRules.ToList();
        _settings.BlockKeywords = _blockKeywords.ToList();

        SettingsService.Save(_settings);
        _overlay?.ApplySettings();

        if (logEncodingChanged && _watcher.IsRunning)
        {
            _watcher.UpdateEncoding(_settings.LogEncoding);
            _overlay?.ClearMessages();
            AppendDebugLog("[编码] 已切换为 " + _settings.LogEncoding + "，已清空悬浮窗旧消息");
        }

        if (log) LogStatus(Copy.ConfigSaved);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Minecraft 日志文件",
            Filter = "Minecraft 日志 (*.log)|*.log|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            LogPathTextBox.Text = dialog.FileName;
            if (_watcher.IsRunning)
            {
                StopListening();
        _screenshotWatcher?.Dispose();
        _screenshotWatcher = null;
        SaveAiSettingsFromUi();
                StartListening();
            }
            else SaveSettingsFromUi();
        }
    }

    private void ToggleListenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_watcher.IsRunning) StopListening();
        else StartListening();
    }

    private void StartListening()
    {
        SaveSettingsFromUi();
        if (string.IsNullOrWhiteSpace(_settings.LogPath))
        {
            // 先让按钮晃一下表示"不行哦"，再弹原因 —— 比直接弹框温柔，也更符合轻声提醒的语气。
            // MessageBox 会阻塞 UI 线程，所以必须等晃动播完再弹，否则动画根本渲染不出来。
            Motion.Shake(ToggleListenButton);
            var prompt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.ShakeMs + 40) };
            prompt.Tick += (_, _) =>
            {
                prompt.Stop();
                MessageBox.Show(this, Copy.NeedLogPath, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            };
            prompt.Start();
            return;
        }

        _watcher.ChatLineReceived += Watcher_ChatLineReceived;
        _watcher.StatusChanged += Watcher_StatusChanged;
        _watcher.DebugLineReceived += Watcher_DebugLineReceived;
        _watcher.Start(_settings.LogPath, _settings.LogEncoding);
        ToggleListenButton.Content = "停止监听";
        UpdateListeningIndicator(true);
        ShowOverlay();
        LogStatus(Copy.WatchStarted + _settings.LogPath);
        AppendDebugLog("== 开始监听 ==");
        AppendDebugLog("日志文件: " + _settings.LogPath);
        AppendDebugLog("日志编码: " + _settings.LogEncoding);
    }

    private void StopListening()
    {
        _watcher.ChatLineReceived -= Watcher_ChatLineReceived;
        _watcher.StatusChanged -= Watcher_StatusChanged;
        _watcher.DebugLineReceived -= Watcher_DebugLineReceived;
        _watcher.Stop();
        ToggleListenButton.Content = "开始监听";
        UpdateListeningIndicator(false);
        LogStatus(Copy.ListeningStopped);
        AppendDebugLog("== 已停止监听 ==");
    }

    private void Watcher_ChatLineReceived(string chatMessage)
    {
        Dispatcher.InvokeAsync(() =>
        {
            NotifyPluginsChatLine(chatMessage);
            var activeReplaceRules = _replaceRules.Where(r => r.IsEnabled).ToList();
            var activeBlockKeywords = _blockKeywords.Where(k => k.IsEnabled).ToList();
            var replacedForCheck = ChatTextProcessor.ApplyReplacements(chatMessage, activeReplaceRules);
            var blocked = ChatTextProcessor.IsBlocked(replacedForCheck, activeBlockKeywords);

            if (blocked)
            {
                AppendDebugLog("[屏蔽] 已屏蔽，不显示到悬浮窗：" + replacedForCheck);
                return;
            }

            NotifySoundForChat(replacedForCheck);
            NotifyKillFeedback(replacedForCheck);
            ShowOverlay();
            _overlay?.AddMessage(chatMessage);
        });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            else WindowState = WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized) return;
        DragMove();
    }

    private void Watcher_StatusChanged(string status)
    {
        Dispatcher.InvokeAsync(() =>
        {
            UpdateListeningIndicator(_watcher.IsRunning);
            LogStatus(status);
            AppendDebugLog("[状态] " + status);
        });
    }

    private void Watcher_DebugLineReceived(string decodedLine, string hex)
    {
        Dispatcher.InvokeAsync(() =>
        {
            AppendDebugLog("[行] " + decodedLine);
            AppendDebugLog("[HEX] " + hex);
        });
    }

    private void ShowOverlay()
    {
        if (_overlay == null)
        {
            _overlay = new OverlayWindow(_settings);
            _overlay.Closed += (_, _) =>
            {
                _overlay = null;
                ToggleOverlayButton.Content = "显示悬浮窗";
            };
        }

        if (!_overlay.IsVisible)
        {
            _overlay.Show();
        }

        ToggleOverlayButton.Content = "隐藏悬浮窗";
    }

    private void HideOverlay()
    {
        _overlay?.Hide();
        ToggleOverlayButton.Content = "显示悬浮窗";
    }

    private void ToggleOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay?.IsVisible == true)
        {
            HideOverlay();
            LogStatus(Copy.OverlayHidden);
        }
        else
        {
            ShowOverlay();
            LogStatus(Copy.OverlayShown);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _overlay?.ClearMessages();
        LogStatus(Copy.OverlayCleared);
    }

    private void ResetOverlayPositionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay == null)
        {
            // 悬浮窗还没创建过，清掉记忆位置即可，下次显示就是默认位置。
            _settings.OverlayLeft = null;
            _settings.OverlayTop = null;
            _settings.OverlayAnchorBottom = null;
            SettingsService.Save(_settings);
        }
        else
        {
            _overlay.ResetPosition();
        }

        LogStatus(Copy.OverlayPositionReset);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        // 成功后轻轻"跳"一下就行，不需要任何文字提示
        Motion.Heartbeat(SaveButton);
    }

    private void ResetOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        OverlayWidthTextBox.Text = "420";
        OverlayMaxHeightTextBox.Text = "620";
        WrapLengthTextBox.Text = "40";
        MaxMessagesTextBox.Text = "200";
        OpacitySlider.Value = 0.9;
        BackgroundOpacitySlider.Value = 1.0;
        var defaultFont = _fontItems.FirstOrDefault(x => string.Equals(x.Source, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase))
                          ?? _fontItems.FirstOrDefault();
        if (defaultFont != null)
        {
            FontFamilyComboBox.SelectedItem = defaultFont;
        }
        FontSizeTextBox.Text = "16";
        FontWeightComboBox.SelectedItem = "SemiBold";

        _settings.TextColor = "#FFFFFF";
        _settings.BackgroundColor = "#0A0912";
        _settings.PlayerContentColor = "#BFE9FF";
        _settings.PlayerNameColor = "#FFD166";

        TextColorPreview.Background = ParseBrush(_settings.TextColor, Brushes.White);
        BackgroundColorPreview.Background = ParseBrush(_settings.BackgroundColor, new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
        PlayerContentColorPreview.Background = ParseBrush(_settings.PlayerContentColor, Brushes.White);
        PlayerNameColorPreview.Background = ParseBrush(_settings.PlayerNameColor, Brushes.White);
        TextColorHexText.Text = FormatHex(_settings.TextColor);
        BackgroundColorHexText.Text = FormatHex(_settings.BackgroundColor);
        PlayerContentColorHexText.Text = FormatHex(_settings.PlayerContentColor);
        PlayerNameColorHexText.Text = FormatHex(_settings.PlayerNameColor);

        SaveSettingsFromUi(false);
        LogStatus(Copy.ResetOverlay);
    }

    private void CialloButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cialloBusy)
        {
            return;
        }

        _cialloBusy = true;
        CialloButton.IsEnabled = false;

        if (CialloImage.Source == null)
        {
            LoadBrandAssets();
        }

        try
        {
            _cialloPlayer.Stop();
            _cialloPlayer.Position = TimeSpan.Zero;
            _cialloPlayer.Play();
        }
        catch
        {
        }

        CialloOverlay.Visibility = Visibility.Visible;
        CialloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CialloScale.ScaleX = 0;

        var expand = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(640))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        expand.Completed += (_, _) =>
        {
            var holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            holdTimer.Tick += (_, _) =>
            {
                holdTimer.Stop();
                var retract = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(520))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                retract.Completed += (_, _) =>
                {
                    CialloOverlay.Visibility = Visibility.Collapsed;
                    CialloScale.ScaleX = 0;
                    CialloButton.IsEnabled = true;
                    _cialloBusy = false;
                };
                CialloScale.BeginAnimation(ScaleTransform.ScaleXProperty, retract);
            };
            holdTimer.Start();
        };
        CialloScale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
    }

    private void EnableDebugLogCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateDebugLogVisibility();
        SaveSettingsFromUi(false);
    }

    private void UpdateDebugLogVisibility()
    {
        DebugLogGroup.Visibility = EnableDebugLogCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateDebugLogCount();
    }

    private void UpdateDebugLogCount()
    {
        if (DebugLogTextBox == null || DebugLogCountText == null)
        {
            return;
        }

        var text = DebugLogTextBox.Text ?? string.Empty;
        var lines = 0;
        if (text.Length > 0)
        {
            lines = text.Count(c => c == '\n');
            if (!text.EndsWith('\n'))
            {
                lines++;
            }
        }

        DebugLogCountText.Text = $"共 {lines} 行";
    }

    private void CopyDebugLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DebugLogTextBox.Text);
            LogStatus(Copy.DebugCopied);
        }
        catch
        {
            LogStatus(Copy.CopyFailed);
        }
    }

    private void ClearDebugLogButton_Click(object sender, RoutedEventArgs e)
    {
        DebugLogTextBox.Clear();
        UpdateDebugLogCount();
        LogStatus(Copy.DebugCleared);
    }

    private void SendManualMessageButton_Click(object sender, RoutedEventArgs e)
    {
        var text = ManualMessageTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ShowOverlay();
        _overlay?.AddMessage(text);
        ManualMessageTextBox.Clear();
        LogStatus(Copy.OverlaySentTo);
    }

    private void AppendDebugLog(string line)
    {
        if (!_settings.EnableDebugLog)
        {
            return;
        }

        DebugLogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        DebugLogTextBox.ScrollToEnd();
        UpdateDebugLogCount();
    }

    // ---------- 规则导入/导出 ----------

    private void ExportRulesButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi(false);
        var dialog = new SaveFileDialog
        {
            Title = "导出文本处理规则",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = "minecraft-chat-text-rules.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var export = new TextRuleExport
        {
            ColorRules = _settings.ColorRules.ToList(),
            ReplaceRules = _settings.ReplaceRules.ToList(),
            BlockKeywords = _settings.BlockKeywords.ToList()
        };

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
            LogStatus(Copy.RulesExported + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Copy.ExportFailed + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportRulesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入文本处理规则",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var export = JsonSerializer.Deserialize<TextRuleExport>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (export == null)
            {
                MessageBox.Show(this, Copy.ImportEmpty, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.ColorRules = export.ColorRules ?? new List<TextColorRule>();
            _settings.ReplaceRules = export.ReplaceRules ?? new List<TextReplaceRule>();
            _settings.BlockKeywords = export.BlockKeywords ?? new List<BlockKeywordItem>();
            _colorRules.Clear();
            foreach (var rule in _settings.ColorRules)
            {
                _colorRules.Add(rule);
            }

            _replaceRules.Clear();
            foreach (var rule in _settings.ReplaceRules)
            {
                _replaceRules.Add(rule);
            }

            _blockKeywords.Clear();
            foreach (var keyword in _settings.BlockKeywords)
            {
                _blockKeywords.Add(keyword);
            }

            SaveSettingsFromUi(false);
            LogStatus(Copy.RulesImported + dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Copy.ImportFailed + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RegexTutorialButton_Click(object sender, RoutedEventArgs e)
    {
        const string tutorial =
            "正则匹配简单教程\n\n" +
            "1. 普通文字直接填写，例如：红队\n" +
            "2. \\d 表示数字，\\d+ 表示一个或多个数字\n" +
            "   例：游戏还有 (\\d+) 秒开始\n" +
            "3. .+? 表示任意内容（尽量短）\n" +
            "   例：玩家 (.+?) 退出了游戏！\n" +
            "4. 括号 () 用于捕获内容\n" +
            "   - 颜色规则“高亮第几组填 1”只染第一个括号里的内容\n" +
            "   - 替换规则里可用 $1 引用第一个括号内容\n" +
            "5. 想同时给整句上色，在颜色规则里设置“整句颜色”\n\n" +
            "示例：让“游戏还有 X 秒开始”中的 X 变浅红、其他字变金色\n" +
            "正则：游戏还有 (\\d+) 秒开始\n" +
            "高亮组：1\n" +
            "颜色：浅红\n" +
            "整句颜色：金色";
        MessageBox.Show(this, tutorial, "正则教程", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- 颜色选择 ----------

    private void TextColorButton_Click(object sender, RoutedEventArgs e)
    {
        var hex = PickColorHex(_settings.TextColor);
        if (hex == null)
        {
            return;
        }

        _settings.TextColor = hex;
        TextColorPreview.Background = ParseBrush(hex, Brushes.White);
        TextColorHexText.Text = FormatHex(hex);
        SaveSettingsFromUi(false);
    }

    private void BackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        var hex = PickColorHex(_settings.BackgroundColor);
        if (hex == null)
        {
            return;
        }

        _settings.BackgroundColor = hex;
        BackgroundColorPreview.Background = ParseBrush(hex, new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));
        BackgroundColorHexText.Text = FormatHex(hex);
        SaveSettingsFromUi(false);
    }

    private void PlayerContentColorButton_Click(object sender, RoutedEventArgs e)
    {
        var current = string.IsNullOrWhiteSpace(_settings.PlayerContentColor) ? "#FFFFFF" : _settings.PlayerContentColor;
        var hex = PickColorHex(current);
        if (hex == null)
        {
            return;
        }

        _settings.PlayerContentColor = hex;
        PlayerContentColorPreview.Background = ParseBrush(hex, Brushes.White);
        PlayerContentColorHexText.Text = FormatHex(hex);
        SaveSettingsFromUi(false);
    }

    private void ClearPlayerContentColorButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PlayerContentColor = "";
        PlayerContentColorPreview.Background = Brushes.Transparent;
        PlayerContentColorHexText.Text = FormatHex("");
        SaveSettingsFromUi(false);
    }

    private void PlayerNameColorButton_Click(object sender, RoutedEventArgs e)
    {
        var current = string.IsNullOrWhiteSpace(_settings.PlayerNameColor) ? "#FFFFFF" : _settings.PlayerNameColor;
        var hex = PickColorHex(current);
        if (hex == null)
        {
            return;
        }

        _settings.PlayerNameColor = hex;
        PlayerNameColorPreview.Background = ParseBrush(hex, Brushes.White);
        PlayerNameColorHexText.Text = FormatHex(hex);
        SaveSettingsFromUi(false);
    }

    private void ClearPlayerNameColorButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PlayerNameColor = "";
        PlayerNameColorPreview.Background = Brushes.Transparent;
        PlayerNameColorHexText.Text = FormatHex("");
        SaveSettingsFromUi(false);
    }

    private string? PickColorHex(string currentHex)
    {
        try
        {
            var currentColor = ParseColor(currentHex, Colors.White);
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                Color = System.Drawing.Color.FromArgb(currentColor.A, currentColor.R, currentColor.G, currentColor.B)
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                return null;
            }

            var c = dialog.Color;
            var alpha = currentColor.A;
            var mediaColor = Color.FromArgb(alpha, c.R, c.G, c.B);
            return mediaColor.A == 0xFF
                ? $"#{mediaColor.R:X2}{mediaColor.G:X2}{mediaColor.B:X2}"
                : $"#{mediaColor.A:X2}{mediaColor.R:X2}{mediaColor.G:X2}{mediaColor.B:X2}";
        }
        catch
        {
            return null;
        }
    }

    // ---------- 文本彩色渲染规则 ----------

    private void ColorRuleListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorRuleListBox.SelectedItem is not TextColorRule rule)
        {
            return;
        }

        ColorRuleTextTextBox.Text = rule.Text;
        _colorRuleColor = rule.Color;
        ColorRuleColorPreview.Background = ParseBrush(rule.Color, Brushes.Red);
        ColorRuleWeightComboBox.SelectedItem = rule.FontWeight;
        ColorRuleRegexCheckBox.IsChecked = rule.UseRegex;
        ColorRuleRegexGroupTextBox.Text = rule.RegexGroup.ToString();
        _colorRuleMatchColor = rule.MatchColor ?? "";
        ColorRuleMatchColorPreview.Background = string.IsNullOrWhiteSpace(_colorRuleMatchColor)
            ? Brushes.Transparent
            : ParseBrush(_colorRuleMatchColor, Brushes.Gold);
        ColorRuleEnabledCheckBox.IsChecked = rule.IsEnabled;
    }

    private void ChooseColorRuleColorButton_Click(object sender, RoutedEventArgs e)
    {
        var hex = PickColorHex(_colorRuleColor);
        if (hex == null)
        {
            return;
        }

        _colorRuleColor = hex;
        ColorRuleColorPreview.Background = ParseBrush(hex, Brushes.Red);
    }

    private void ChooseColorRuleMatchColorButton_Click(object sender, RoutedEventArgs e)
    {
        var current = string.IsNullOrWhiteSpace(_colorRuleMatchColor) ? "#FFD700" : _colorRuleMatchColor;
        var hex = PickColorHex(current);
        if (hex == null)
        {
            return;
        }

        _colorRuleMatchColor = hex;
        ColorRuleMatchColorPreview.Background = ParseBrush(hex, Brushes.Gold);
    }

    private void ClearColorRuleMatchColorButton_Click(object sender, RoutedEventArgs e)
    {
        _colorRuleMatchColor = "";
        ColorRuleMatchColorPreview.Background = Brushes.Transparent;
    }

    // ==================== ID 对应颜色（玩家 → 队伍色） ====================

    /// <summary>粘贴内容一变就显示识别结果，不用先点分配才知道认没认对。</summary>
    private void TeamAssignTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TeamAssignStatusText is null)
        {
            return;
        }

        var parsed = TeamColorTable.Parse(TeamAssignTextBox.Text);
        TeamAssignStatusText.Text = parsed.Count == 0
            ? ""
            : Copy.TeamAssignPreview(parsed.Count, DescribeTeamBreakdown(parsed));
    }

    private static string DescribeTeamBreakdown(List<(string Team, string Name)> parsed) =>
        string.Join("  ", parsed
            .GroupBy(x => x.Team)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}{g.Count()}"));

    private void AssignTeamColorsButton_Click(object sender, RoutedEventArgs e)
    {
        var parsed = TeamColorTable.Parse(TeamAssignTextBox.Text);
        if (parsed.Count == 0)
        {
            TeamAssignStatusText.Text = Copy.TeamAssignNoMatch;
            return;
        }

        AssignTeamColors(parsed, "手动");
    }

    /// <summary>统一入口：清掉上一批队色规则，再按解析结果重新分配。</summary>
    private void AssignTeamColors(List<(string Team, string Name)> parsed, string source)
    {
        // 先把上一次分配的队色规则清掉，反复分配不会越堆越多
        RemoveTeamColorRules();

        foreach (var (team, name) in parsed)
        {
            var color = TeamColorTable.ColorOf(team);
            if (string.IsNullOrEmpty(color))
            {
                continue;
            }

            AddTeamColorRule(name, color);

            // 名字里可能被 OCR 塞了空格（实际名单里没有），补一条去掉空格的版本
            var compact = name.Replace(" ", "");
            if (!string.Equals(compact, name, StringComparison.Ordinal) && compact.Length > 0)
            {
                AddTeamColorRule(compact, color);
            }
        }

        SaveSettingsFromUi(false);
        var message = Copy.TeamAssigned(parsed.Count);
        TeamAssignStatusText.Text = message;
        LogStatus($"{message}（{source}）");
    }

    // ==================== AI 识图：截图 → 自动分配 ====================

    /// <summary>
    /// AI 相关的提示一律走这里：卡片上的状态行 + 底部状态栏（+ 出错时弹窗），
    /// 保证不会出现"点了没反应、什么提示都没有"的情况。
    /// </summary>
    private void SetAiStatus(string message, bool isError = false, bool popup = false)
    {
        // 这个可能被后台线程调到（截图监听器、HTTP 回调），先切回 UI 线程再动控件
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetAiStatus(message, isError, popup));
            return;
        }

        if (AiStatusText is not null)
        {
            AiStatusText.Text = message;
            AiStatusText.Foreground = isError
                ? (TryFindResource("DangerBrush") as Brush ?? Brushes.OrangeRed)
                : (TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray);
        }

        LogStatus("AI：" + message);

        // 同时写进「调试后台」的日志，方便复制出来排查
        AppendDebugLog("AI：" + message);

        if (popup)
        {
            try
            {
                MessageBox.Show(this, message, "AI 自动识别", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
            }
        }
    }

    private void LoadAiUiFromSettings()
    {
        AiAutoAssignCheckBox.IsChecked = _settings.AiAutoAssign;
        AiScreenshotDirTextBox.Text = ResolveScreenshotDir();
        AiApiModeComboBox.SelectedIndex = string.Equals(_settings.AiApiMode, "zhipuOcr", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AiBaseUrlTextBox.Text = _settings.AiBaseUrl;
        AiModelIdTextBox.Text = _settings.AiModelId;
        AiApiKeyBox.Password = _settings.AiApiKey;
        AiPromptTextBox.Text = _settings.AiPrompt;
        AiExtraBodyTextBox.Text = _settings.AiExtraBodyJson;
        AiCompressImageCheckBox.IsChecked = _settings.AiCompressImage;
        AiCollectIdsCheckBox.IsChecked = _settings.AiCollectIds;
        AiTabFirstTextBox.Text = _settings.AiTabFirstPresses.ToString();
        AiTabNextTextBox.Text = _settings.AiTabStep.ToString();
        AiTabMaxRoundsTextBox.Text = _settings.AiTabMaxRounds.ToString();
        AiTabDelayTextBox.Text = _settings.AiTabStepDelayMs.ToString();
        AiStatusText.Text = _settings.AiAutoAssign ? "已开启，等新截图…" : "未开启";

        UpdateAiAutoAssignVisibility();
    }

    /// <summary>
    /// 未开启自动识别时，把详细设置整体收起，只留总开关。
    ///
    /// 理由：这块设置又长又密（目录、接口、Key、提示词、TAB 参数…），
    /// 没开这个功能的人根本用不上，摊在那儿就是白占大半屏。
    /// 收起只是隐藏，控件都还在 —— 开关一打开，之前填的值原样回来。
    /// </summary>
    private void UpdateAiAutoAssignVisibility()
    {
        if (AiAutoAssignDetailsPanel == null)
        {
            return;
        }

        AiAutoAssignDetailsPanel.Visibility = AiAutoAssignCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SaveAiSettingsFromUi()
    {
        if (_loading || AiAutoAssignCheckBox is null)
        {
            return;
        }

        _settings.AiAutoAssign = AiAutoAssignCheckBox.IsChecked == true;
        _settings.AiScreenshotDir = AiScreenshotDirTextBox.Text.Trim();
        _settings.AiApiMode = AiApiModeComboBox.SelectedIndex == 1 ? "zhipuOcr" : "chat";
        _settings.AiBaseUrl = AiBaseUrlTextBox.Text.Trim();
        _settings.AiModelId = AiModelIdTextBox.Text.Trim();
        _settings.AiApiKey = AiApiKeyBox.Password;
        _settings.AiPrompt = AiPromptTextBox.Text.Trim();
        _settings.AiExtraBodyJson = AiExtraBodyTextBox.Text.Trim();
        _settings.AiCompressImage = AiCompressImageCheckBox.IsChecked == true;
        _settings.AiCollectIds = AiCollectIdsCheckBox.IsChecked == true;
        _settings.AiTabFirstPresses = ParseInt(AiTabFirstTextBox.Text, 2, 1, 10);
        _settings.AiTabStep = ParseInt(AiTabNextTextBox.Text, 1, 0, 5);
        _settings.AiTabMaxRounds = ParseInt(AiTabMaxRoundsTextBox.Text, 20, 2, 60);
        _settings.AiTabStepDelayMs = ParseInt(AiTabDelayTextBox.Text, 20, 5, 300);
        SettingsService.Save(_settings);
        UpdateScreenshotWatcher();

        UpdateAiAutoAssignVisibility();
    }

    private void AiSetting_Changed(object sender, RoutedEventArgs e) => SaveAiSettingsFromUi();

    private void AiApiMode_Changed(object sender, SelectionChangedEventArgs e) => SaveAiSettingsFromUi();

    private void AiSetting_LostFocus(object sender, RoutedEventArgs e) => SaveAiSettingsFromUi();

    /// <summary>截图目录：用户填了就用用户的，没填就从日志目录推（.minecraft\screenshots）。</summary>
    private string ResolveScreenshotDir()
    {
        if (!string.IsNullOrWhiteSpace(_settings.AiScreenshotDir))
        {
            return _settings.AiScreenshotDir.Trim();
        }

        try
        {
            var logsDir = Path.GetDirectoryName(_settings.LogPath);
            var gameDir = string.IsNullOrEmpty(logsDir) ? null : Path.GetDirectoryName(logsDir);
            if (!string.IsNullOrEmpty(gameDir))
            {
                var guess = Path.Combine(gameDir, "screenshots");
                if (Directory.Exists(guess))
                {
                    return guess;
                }
            }
        }
        catch
        {
        }

        return "";
    }

    private void BrowseAiScreenshotDirButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选截图目录（一般是 .minecraft 里的 screenshots）" };
        var current = ResolveScreenshotDir();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            AiScreenshotDirTextBox.Text = dialog.FolderName;
            SaveAiSettingsFromUi();
        }
    }

    /// <summary>按开关状态启动/停止截图监听。</summary>
    private void UpdateScreenshotWatcher()
    {
        if (!_settings.AiAutoAssign)
        {
            _screenshotWatcher?.Stop();
            return;
        }

        var dir = ResolveScreenshotDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            SetAiStatus(Copy.AiNeedDir + "（当前：" + (string.IsNullOrEmpty(dir) ? "空" : dir) + "）", true, true);
            _screenshotWatcher?.Stop();
            return;
        }

        if (_screenshotWatcher is null)
        {
            _screenshotWatcher = new ScreenshotWatcher();
            _screenshotWatcher.NewScreenshot += path => _ = RunAiRecognizeAsync(path);
        }

        _screenshotWatcher.Start(dir);
        SetAiStatus("在盯着：" + dir);
    }

    private void RecognizeLatestScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = ResolveScreenshotDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            SetAiStatus(Copy.AiNeedDir + "（当前：" + (string.IsNullOrEmpty(dir) ? "空" : dir) + "）", true, true);
            return;
        }

        FileInfo? newest;
        try
        {
            newest = new DirectoryInfo(dir).EnumerateFiles("*.png")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            SetAiStatus("读截图目录失败：" + ex.Message, true, true);
            return;
        }

        if (newest is null)
        {
            SetAiStatus(Copy.AiNoScreenshot + "（" + dir + "）", true, true);
            return;
        }

        _ = RunAiRecognizeAsync(newest.FullName);
    }

    /// <summary>线程守卫：截图监听器是在后台线程触发事件的，先用它切回 UI 线程。</summary>
    private Task RunAiRecognizeAsync(string imagePath)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => RunAiRecognizeCoreAsync(imagePath)).Task.Unwrap();
        }

        return RunAiRecognizeCoreAsync(imagePath);
    }

    private async Task RunAiRecognizeCoreAsync(string imagePath)
    {
        if (_aiBusy)
        {
            SetAiStatus("上一次识别还没结束，等它跑完", true, true);
            return;
        }

        // 先把当前生效的配置回显出来，配置有没有被读到一眼就能看出来
        SetAiStatus($"配置：接口={Show(_settings.AiBaseUrl)} 模型={Show(_settings.AiModelId)} Key={(_settings.AiApiKey.Length > 0 ? "已填" : "空")}");

        if (string.IsNullOrWhiteSpace(_settings.AiBaseUrl))
        {
            SetAiStatus(Copy.AiNeedBaseUrl + "（输入框填完记得点到别处，让它保存）", true, true);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.AiModelId))
        {
            SetAiStatus(Copy.AiNeedModel + "（输入框填完记得点到别处，让它保存）", true, true);
            return;
        }

        _aiBusy = true;
        var name = Path.GetFileName(imagePath);

        try
        {
            // ① 需要的话，先用 TAB 补全把本局玩家 ID 问出来当参考
            var ids = new List<string>();
            if (_settings.AiCollectIds)
            {
                SetAiStatus("正在用 TAB 补全收集玩家 ID（会短暂打开聊天栏）…");
                ids = await ChatIdCollector.CollectAsync(
                    BuildCollectOptions(),
                    ReadClipboardOnUiThread,
                    WriteClipboardOnUiThread,
                    step => SetAiStatus(step),
                    System.Threading.CancellationToken.None);
            }

            // ② 拼进提示词：让 AI 从候选里挑，而不是纯靠像素猜
            var prompt = BuildPromptWithIds(_settings.AiPrompt, ids);

            // 发出去之前先记下"发了什么"，和后面的返回原文配成一对
            LogAiRequest(imagePath, prompt);

            var text = await AiVisionClient.RecognizeAsync(
                new AiVisionOptions
                {
                    BaseUrl = _settings.AiBaseUrl,
                    ModelId = _settings.AiModelId,
                    ApiKey = _settings.AiApiKey,
                    Prompt = prompt,
                    Compress = _settings.AiCompressImage,
                    ExtraBodyJson = _settings.AiExtraBodyJson,
                    ApiMode = _settings.AiApiMode
                },
                imagePath,
                System.Threading.CancellationToken.None,
                step => SetAiStatus(step));

            LogStatus("AI 识图完成：" + name);
            ApplyAiRecognizedText(text, name);
        }
        catch (Exception ex)
        {
            SetAiStatus("识别失败：" + ex.Message, true, true);
            LogStatus("AI 识图失败：" + ex.Message);
            AiResponseLog.Append("识别失败", ex.Message, 0);
        }
        finally
        {
            _aiBusy = false;
        }

        static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "空！" : value;
    }

    /// <summary>把这次发送的接口、模型、参数、图片、完整提示词写进 AI 记录文件。</summary>
    private void LogAiRequest(string imagePath, string prompt)
    {
        try
        {
            var sizeText = "?";
            try
            {
                var info = new FileInfo(imagePath);
                if (info.Exists)
                {
                    sizeText = (info.Length / 1024) + " KB";
                }
            }
            catch
            {
            }

            AiResponseLog.AppendRequest(Path.GetFileName(imagePath), new[]
            {
                "接口类型：" + (_settings.AiApiMode == "zhipuOcr"
                    ? "智谱 GLM-OCR（/layout_parsing）"
                    : "对话模型（/chat/completions）"),
                "BaseUrl：" + _settings.AiBaseUrl,
                "模型：" + _settings.AiModelId,
                "额外请求参数：" + (string.IsNullOrWhiteSpace(_settings.AiExtraBodyJson)
                    ? "（无）"
                    : _settings.AiExtraBodyJson),
                "图片：" + Path.GetFileName(imagePath)
                    + "（文件 " + sizeText + "，"
                    + (_settings.AiCompressImage ? "压缩后上传" : "原图直传") + "）",
                "TAB 收集 ID：" + (_settings.AiCollectIds
                    ? "已开启（" + _settings.AiTabFirstPresses + "/+" + _settings.AiTabStep
                      + "，最多 " + _settings.AiTabMaxRounds + " 轮）"
                    : "未开启"),
                "",
                "完整提示词：",
                prompt
            });
        }
        catch
        {
            // 记录失败不影响识别
        }
    }

    private ChatIdCollectOptions BuildCollectOptions() => new()
    {
        FirstTabPresses = _settings.AiTabFirstPresses,
        TabStep = _settings.AiTabStep,
        MaxRounds = _settings.AiTabMaxRounds,
        StepDelayMs = _settings.AiTabStepDelayMs
    };

    /// <summary>剪贴板只能在 UI 线程读，收集器跑在后台线程，所以这里做一次调度。</summary>
    private string ReadClipboardOnUiThread() =>
        Dispatcher.CheckAccess()
            ? Clipboard.GetText()
            : Dispatcher.Invoke(Clipboard.GetText);

    /// <summary>
    /// 写剪贴板（同样只能在 UI 线程）。收集器每轮读完会先写一个哨兵值，
    /// 用来判断"复制到底有没有生效" —— 否则旧名字会一直被误当成新读到的名字。
    /// </summary>
    private void WriteClipboardOnUiThread(string text)
    {
        void Write() => Clipboard.SetText(text);

        if (Dispatcher.CheckAccess())
        {
            Write();
        }
        else
        {
            Dispatcher.Invoke(Write);
        }
    }

    /// <summary>把候选 ID 拼到提示词后面，让 AI 优先从这些里面挑。</summary>
    private static string BuildPromptWithIds(string basePrompt, List<string> ids)
    {
        if (ids.Count == 0)
        {
            return basePrompt;
        }

        return basePrompt
               + "\n\n参考：本局玩家 ID 可能是这些（识别时优先从里面挑，允许大小写差异）："
               + string.Join("、", ids)
               + "\n\n直接输出结果，只按「X队 | XXX」的格式每行一个，"
               + "不要输出思考过程、推理、分析、标题或任何解释文字。";
    }

    // ---------- 一键填常见服务商的"关闭思考"参数 ----------

    private void AiPresetNoExtra_Click(object sender, RoutedEventArgs e) => ApplyAiExtraBody("");

    private void AiPresetThinkingDisabled_Click(object sender, RoutedEventArgs e) =>
        ApplyAiExtraBody("{\"thinking\":{\"type\":\"disabled\"}}");

    private void AiPresetEnableThinkingFalse_Click(object sender, RoutedEventArgs e) =>
        ApplyAiExtraBody("{\"enable_thinking\":false}");

    private void ApplyAiExtraBody(string json)
    {
        AiExtraBodyTextBox.Text = json;
        SaveAiSettingsFromUi();
        SetAiStatus(json.Length == 0
            ? "已清空额外请求参数"
            : "已填入额外请求参数：" + json);
    }

    /// <summary>打开 AI 返回记录的日志文件，回查"那次为什么没认出来"。</summary>
    private void OpenAiResponseLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!System.IO.File.Exists(AiResponseLog.FilePath))
            {
                SetAiStatus("还没有识别记录（记录文件：" + AiResponseLog.FilePath + "）");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AiResponseLog.FilePath)
            {
                UseShellExecute = true
            });
            SetAiStatus("已打开记录：" + AiResponseLog.FilePath);
        }
        catch (Exception ex)
        {
            SetAiStatus("打不开记录文件：" + ex.Message + "（路径：" + AiResponseLog.FilePath + "）", true, true);
        }
    }

    /// <summary>只收集 ID，不识别 —— 用来试"按几次 TAB"这个参数。</summary>
    private async void CollectIdsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetAiStatus("开始收集玩家 ID（游戏窗口要在前台）…");
            var ids = await ChatIdCollector.CollectAsync(
                BuildCollectOptions(),
                ReadClipboardOnUiThread,
                WriteClipboardOnUiThread,
                step => SetAiStatus(step),
                System.Threading.CancellationToken.None);

            SetAiStatus(ids.Count == 0
                ? "没收集到 ID —— 确认游戏窗口在前台，或调整上面两个 TAB 次数"
                : $"收集到 {ids.Count} 个：{string.Join("、", ids)}");
        }
        catch (Exception ex)
        {
            SetAiStatus("收集失败：" + ex.Message, true, true);
        }
    }

    /// <summary>
    /// AI 返回的文字：先填进粘贴框（方便看到原文、必要时手改），再逐行解析分配。
    /// 一条都没认出来时不动现有规则 —— 避免接口返回异常把颜色全清掉。
    /// </summary>
    private void ApplyAiRecognizedText(string text, string source)
    {
        TeamAssignTextBox.Text = text;

        var parsed = TeamColorTable.Parse(text);

        // 不管认没认出来，都把原文记下来：既写进「调试后台」，也落到文件里
        // （偶发问题需要回查，而调试后台的日志只有开了开关才看得到）
        AppendDebugLog($"===== AI 识图返回原文（{source}，{text.Length} 字，认出 {parsed.Count} 条）=====");
        AppendDebugLog(string.IsNullOrWhiteSpace(text) ? "(空)" : text.Trim());
        AppendDebugLog("===== 原文结束 =====");
        AiResponseLog.Append(source, text, parsed.Count);

        if (parsed.Count == 0)
        {
            var preview = string.IsNullOrWhiteSpace(text)
                ? "(返回内容为空)"
                : text.Replace("\r", " ").Replace("\n", " / ").Trim();
            if (preview.Length > 160)
            {
                preview = preview[..160] + "…";
            }

            SetAiStatus(Copy.AiNothingRecognized + " AI 返回：" + preview, true, true);
            LogStatus("AI 原文已存到 " + AiResponseLog.FilePath);
            return;
        }

        AssignTeamColors(parsed, "AI：" + source);
        SetAiStatus(Copy.TeamAssigned(parsed.Count));
    }

    private void AddTeamColorRule(string name, string color)
    {
        // 用户要求：只要这个 ID 出现了就染色，不附加任何前提。
        // 所以用普通文本匹配（IndexOf），不用整词正则 —— 名字紧贴别的字也能命中。
        _colorRules.Add(new TextColorRule
        {
            Text = name,
            Color = color,
            FontWeight = "Normal",
            UseRegex = false,
            RegexGroup = 0,
            MatchColor = "",
            IsEnabled = true,
            FromTeamTable = true
        });
    }

    private int RemoveTeamColorRules()
    {
        var stale = _colorRules.Where(r => r.FromTeamTable).ToList();
        foreach (var rule in stale)
        {
            _colorRules.Remove(rule);
        }

        return stale.Count;
    }

    private void ClearTeamColorsButton_Click(object sender, RoutedEventArgs e)
    {
        var removed = RemoveTeamColorRules();
        if (removed == 0)
        {
            TeamAssignStatusText.Text = Copy.TeamColorsNoneToClear;
            return;
        }

        SaveSettingsFromUi(false);
        TeamAssignStatusText.Text = Copy.TeamColorsCleared(removed);
        LogStatus(Copy.TeamColorsCleared(removed));
    }

    private void AddColorRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var text = ColorRuleTextTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            MessageBox.Show(this, Copy.NeedColorText, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rule = new TextColorRule
        {
            Text = text,
            Color = _colorRuleColor,
            FontWeight = ColorRuleWeightComboBox.SelectedItem as string ?? "Light",
            UseRegex = ColorRuleRegexCheckBox.IsChecked == true,
            RegexGroup = ParseInt(ColorRuleRegexGroupTextBox.Text, 0, 0, 100),
            MatchColor = _colorRuleMatchColor,
            IsEnabled = true
        };
        _colorRules.Add(rule);
        ColorRuleListBox.SelectedItem = rule;
        SaveSettingsFromUi(false);
    }

    private void UpdateColorRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ColorRuleListBox.SelectedItem is not TextColorRule rule)
        {
            MessageBox.Show(this, Copy.PickColorRule, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        rule.Text = ColorRuleTextTextBox.Text.Trim();
        rule.Color = _colorRuleColor;
        rule.FontWeight = ColorRuleWeightComboBox.SelectedItem as string ?? "Light";
        rule.UseRegex = ColorRuleRegexCheckBox.IsChecked == true;
        rule.RegexGroup = ParseInt(ColorRuleRegexGroupTextBox.Text, 0, 0, 100);
        rule.MatchColor = _colorRuleMatchColor;
        ColorRuleListBox.Items.Refresh();
        SaveSettingsFromUi(false);
    }

    private void DeleteColorRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ColorRuleListBox.SelectedItem is TextColorRule rule)
        {
            _colorRules.Remove(rule);
            SaveSettingsFromUi(false);
        }
    }

    private void ColorRuleEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ColorRuleListBox.SelectedItem is TextColorRule rule)
        {
            rule.IsEnabled = ColorRuleEnabledCheckBox.IsChecked == true;
            ColorRuleListBox.Items.Refresh();
            ColorRuleListBox.SelectedItem = rule;
            SaveSettingsFromUi(false);
        }
    }

    // ---------- 文本替换 ----------

    private void ReplaceRuleListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReplaceRuleListBox.SelectedItem is not TextReplaceRule rule)
        {
            return;
        }

        ReplaceFindTextBox.Text = rule.FindText;
        ReplaceWithTextBox.Text = rule.ReplaceText;
        ReplaceOnlyPlayerContentCheckBox.IsChecked = rule.OnlyPlayerContent;
        ReplaceRegexCheckBox.IsChecked = rule.UseRegex;
        ReplaceRuleEnabledCheckBox.IsChecked = rule.IsEnabled;
    }

    private void AddReplaceRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var find = ReplaceFindTextBox.Text;
        if (string.IsNullOrEmpty(find))
        {
            MessageBox.Show(this, Copy.NeedFindText, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rule = new TextReplaceRule
        {
            FindText = find,
            ReplaceText = ReplaceWithTextBox.Text ?? string.Empty,
            OnlyPlayerContent = ReplaceOnlyPlayerContentCheckBox.IsChecked == true,
            UseRegex = ReplaceRegexCheckBox.IsChecked == true,
            IsEnabled = true
        };
        _replaceRules.Add(rule);
        ReplaceRuleListBox.SelectedItem = rule;
        SaveSettingsFromUi(false);
    }

    private void UpdateReplaceRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReplaceRuleListBox.SelectedItem is not TextReplaceRule rule)
        {
            MessageBox.Show(this, Copy.PickReplaceRule, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        rule.FindText = ReplaceFindTextBox.Text;
        rule.ReplaceText = ReplaceWithTextBox.Text ?? string.Empty;
        rule.OnlyPlayerContent = ReplaceOnlyPlayerContentCheckBox.IsChecked == true;
        rule.UseRegex = ReplaceRegexCheckBox.IsChecked == true;
        ReplaceRuleListBox.Items.Refresh();
        SaveSettingsFromUi(false);
    }

    private void DeleteReplaceRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReplaceRuleListBox.SelectedItem is TextReplaceRule rule)
        {
            _replaceRules.Remove(rule);
            SaveSettingsFromUi(false);
        }
    }

    private void ReplaceRuleEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ReplaceRuleListBox.SelectedItem is TextReplaceRule rule)
        {
            rule.IsEnabled = ReplaceRuleEnabledCheckBox.IsChecked == true;
            ReplaceRuleListBox.Items.Refresh();
            ReplaceRuleListBox.SelectedItem = rule;
            SaveSettingsFromUi(false);
        }
    }

    // ---------- 屏蔽关键词 ----------

    private void BlockKeywordListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BlockKeywordListBox.SelectedItem is BlockKeywordItem item)
        {
            BlockKeywordTextBox.Text = item.Keyword;
            BlockKeywordEnabledCheckBox.IsChecked = item.IsEnabled;
            BlockKeywordOnlyPlayerContentCheckBox.IsChecked = item.OnlyPlayerContent;
        }
    }

    private void AddBlockKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        var keyword = BlockKeywordTextBox.Text.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return;
        }

        var item = new BlockKeywordItem
        {
            Keyword = keyword,
            IsEnabled = true
        };
        _blockKeywords.Add(item);
        BlockKeywordTextBox.Clear();
        SaveSettingsFromUi(false);
    }

    private void UpdateBlockKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockKeywordListBox.SelectedItem is not BlockKeywordItem item)
        {
            MessageBox.Show(this, Copy.PickKeyword, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var keyword = BlockKeywordTextBox.Text.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            MessageBox.Show(this, Copy.NeedKeyword, "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        item.Keyword = keyword;
        BlockKeywordListBox.Items.Refresh();
        SaveSettingsFromUi(false);
    }

    private void DeleteBlockKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (BlockKeywordListBox.SelectedItem is BlockKeywordItem item)
        {
            _blockKeywords.Remove(item);
            SaveSettingsFromUi(false);
        }
    }

    private void BlockKeywordEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (BlockKeywordListBox.SelectedItem is BlockKeywordItem item)
        {
            item.IsEnabled = BlockKeywordEnabledCheckBox.IsChecked == true;
            BlockKeywordListBox.Items.Refresh();
            BlockKeywordListBox.SelectedItem = item;
            SaveSettingsFromUi(false);
        }
    }

    /// <summary>
    /// "仅玩家发送消息生效"开关。
    /// 勾上 = 这个词只在玩家发言的内容段里比对（系统消息不算）。
    /// </summary>
    private void BlockKeywordOnlyPlayerContentCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (BlockKeywordListBox.SelectedItem is BlockKeywordItem item)
        {
            item.OnlyPlayerContent = BlockKeywordOnlyPlayerContentCheckBox.IsChecked == true;

            // 列表里那行要跟着显示 / 去掉 [仅玩家内容] 标记
            BlockKeywordListBox.Items.Refresh();
            BlockKeywordListBox.SelectedItem = item;
            SaveSettingsFromUi(false);
        }
    }

    // ---------- 窗口控制按钮事件 ----------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeButton.Content = "□";
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeButton.Content = "❐";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ---------- 导航指示条动画 ----------

    private void NavRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            MoveIndicatorToSelected();
        }
    }

    private void MoveIndicatorToSelected()
    {
        RadioButton? selected = null;
        if (NavOverlayDisplay.IsChecked == true) selected = NavOverlayDisplay;
        else if (NavTextRules.IsChecked == true) selected = NavTextRules;
        else if (NavBili.IsChecked == true) selected = NavBili;
        else if (NavMotionBlur.IsChecked == true) selected = NavMotionBlur;
        else if (NavKillFeed.IsChecked == true) selected = NavKillFeed;
        else if (NavPlugins.IsChecked == true) selected = NavPlugins;
        else if (NavDebug.IsChecked == true) selected = NavDebug;

        if (selected == null || NavIndicator == null || IndicatorTranslate == null)
        {
            return;
        }

        var parentGrid = (UIElement)NavIndicator.Parent;
        Point relativePoint = selected.TranslatePoint(new Point(0, 0), parentGrid);

        double targetY = relativePoint.Y + selected.ActualHeight / 2 - NavIndicator.ActualHeight / 2;

        var animation = new DoubleAnimation
        {
            To = targetY,
            Duration = TimeSpan.FromMilliseconds(Motion.IndicatorMs),
            // 带一点惯性的落位：轻轻过冲再收回来，而不是硬生生停住
            EasingFunction = Motion.Settle()
        };

        IndicatorTranslate.BeginAnimation(TranslateTransform.YProperty, animation);
        AnimateActivePanel();
    }

    private void AnimateActivePanel()
    {
        FrameworkElement? activePanel = null;
        if (NavOverlayDisplay.IsChecked == true) activePanel = OverlayPanel;
        else if (NavTextRules.IsChecked == true) activePanel = RulesPanel;
        else if (NavBili.IsChecked == true) activePanel = BiliPanel;
        else if (NavMotionBlur.IsChecked == true) activePanel = MotionBlurPanel;
        else if (NavKillFeed.IsChecked == true) activePanel = KillFeedPanel;
        else if (NavPlugins.IsChecked == true) activePanel = PluginsPanel;
        else if (NavDebug.IsChecked == true) activePanel = DebugPanel;

        if (activePanel == null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            // 样板阶段：只在"悬浮窗显示"这一个面板上启用错峰浮入，方便和其它面板对比观感。
            // 验收满意后把这段判断删掉，五个面板就都走新动效。
            if (ReferenceEquals(activePanel, OverlayPanel))
            {
                Motion.StaggerIn(activePanel, TryFindResource("CardStyle") as Style);
                return;
            }

            // 旧行为：整个面板当一个单位动（没有错峰，观感偏"整体平移"）
            activePanel.Opacity = 0;
            var translate = new TranslateTransform(0, 18);
            activePanel.RenderTransform = translate;

            activePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(460))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }));
    }

    // ---------- 其它 ----------

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSettingsFromUi(false);

        ShutdownPlugins();
        StopListening();
        _overlay?.Close();

        // 击杀图标窗口是常驻的，退出时得显式关掉，否则进程退不干净。
        _killBanner?.Close();
        _killBanner = null;

        // B站模块：先存配置，再断开连接、关掉悬浮窗
        try
        {
            SaveBiliSettingsFromUi(false);
        }
        catch
        {
        }

        _biliOverlay?.Close();
        _biliOverlay = null;

        if (_biliClient != null)
        {
            var client = _biliClient;
            _biliClient = null;
            try
            {
                client.Dispose();
            }
            catch
            {
            }
        }

        _biliFaceCache?.Dispose();
        _biliFaceCache = null;
    }

    private void UpdateListeningIndicator(bool listening)
    {
        var brush = (TryFindResource(listening ? "MintBrush" : "TextTertiaryBrush") as Brush)
                    ?? (listening ? Brushes.MediumSeaGreen : Brushes.Gray);

        var miniDot = MiniStatusDot;
        var statusDot = StatusDot;

        if (miniDot != null)
        {
            miniDot.Fill = brush;
        }

        if (statusDot != null)
        {
            statusDot.Fill = brush;
        }

        if (miniDot != null && statusDot != null)
        {
            if (listening)
            {
                var pulse = new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(820))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                miniDot.BeginAnimation(OpacityProperty, pulse);
                statusDot.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                miniDot.BeginAnimation(OpacityProperty, null);
                statusDot.BeginAnimation(OpacityProperty, null);
                miniDot.Opacity = 1;
                statusDot.Opacity = 1;
            }
        }

        if (MiniStatusText != null)
        {
            MiniStatusText.Text = listening ? "监听中" : "未连接";
        }

        if (ToggleListenButton != null)
        {
            var style = TryFindResource(listening ? "SmallDangerButtonStyle" : "SmallPrimaryButtonStyle") as Style;
            if (style != null)
            {
                ToggleListenButton.Style = style;
            }
        }
    }

    private void LogStatus(string message)
    {
        if (StatusTextBlock != null)
        {
            StatusTextBlock.Text = message;
        }

        if (IsLoaded && !string.IsNullOrWhiteSpace(message))
        {
            ShowToast(message);
        }
    }

    private void ShowToast(string message)
    {
        if (ToastHost == null)
        {
            return;
        }

        var surface = (TryFindResource("SurfaceBrush") as Brush) ?? Brushes.White;
        var accent = (TryFindResource("MintBrush") as Brush) ?? Brushes.MediumSeaGreen;
        var textBrush = (TryFindResource("TextPrimaryBrush") as Brush) ?? Brushes.Black;
        var shadow = TryFindResource("CardHoverShadow") as Effect;

        var toast = new Border
        {
            Background = surface,
            BorderBrush = accent,
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(15, 11, 17, 11),
            Margin = new Thickness(0, 0, 0, 10),
            Effect = shadow,
            Opacity = 0,
            RenderTransform = new TranslateTransform(34, 0),
            Child = new TextBlock
            {
                Text = message,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        ToastHost.Children.Add(toast);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        toast.BeginAnimation(OpacityProperty, fade);

        if (toast.RenderTransform is TranslateTransform translate)
        {
            var slide = new DoubleAnimation(34, 0, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
            };
            translate.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ToastHost.Children.Remove(toast);
        };
        timer.Start();
    }

    private static double ParseDouble(string? text, double defaultValue, double min, double max)
    {
        if (!double.TryParse(text, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static int ParseInt(string? text, int defaultValue, int min, int max)
    {
        if (!int.TryParse(text, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static string FormatHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "默认";
        }

        try
        {
            if (ColorConverter.ConvertFromString(value) is Color color)
            {
                return color.A == 0xFF
                    ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                    : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            }
        }
        catch
        {
        }

        return value.Trim().ToUpperInvariant();
    }

    private static Brush ParseBrush(string? value, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            var brush = new BrushConverter().ConvertFromString(value) as Brush;
            return brush ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var color = ColorConverter.ConvertFromString(value) as Color?;
                if (color.HasValue)
                {
                    return color.Value;
                }
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static string? GetLocalizedFontName(FontFamily family)
    {
        foreach (var pair in family.FamilyNames)
        {
            var tag = pair.Key.IetfLanguageTag;
            if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private sealed class FontItem
    {
        public string Display { get; }

        public string Source { get; }

        public FontItem(string display, string source)
        {
            Display = display;
            Source = source;
        }

        public override string ToString() => Display;
    }

    // ==================== B站弹幕模块 ====================

    /// <summary>把 B站配置回填到界面控件上。</summary>
    private void LoadBiliUiFromSettings()
    {
        _biliLoading = true;
        try
        {
            BiliRoomIdTextBox.Text = _biliSettings.RoomId;
            BiliOverlayWidthSlider.Value = Math.Clamp(_biliSettings.OverlayWidth, 320, 900);
            BiliOverlayHeightSlider.Value = Math.Clamp(_biliSettings.DanmakuMaxHeight, 120, 1200);
            BiliOpacitySlider.Value = Math.Clamp(_biliSettings.OverlayOpacity, 0.1, 1.0);
            BiliBackgroundOpacitySlider.Value = Math.Clamp(_biliSettings.BackgroundOpacity, 0.0, 1.0);
            BiliFontSizeSlider.Value = Math.Clamp(_biliSettings.FontSize, 10, 30);
            BiliWrapLengthTextBox.Text = _biliSettings.DanmakuWrapLength.ToString();
            BiliMaxDanmakuTextBox.Text = _biliSettings.MaxDanmakuLines.ToString();
            BiliSuperChatMaxTextBox.Text = _biliSettings.MaxSuperChatLines.ToString();
            BiliSuperChatVisibleTextBox.Text = _biliSettings.MaxSuperChatVisibleLines.ToString();
            BiliSuperChatFontSizeTextBox.Text = _biliSettings.SuperChatFontSize.ToString("0.#");
            BiliGiftMaxTextBox.Text = _biliSettings.MaxGiftLines.ToString();
            BiliGiftVisibleTextBox.Text = _biliSettings.MaxGiftVisibleLines.ToString();
            BiliGiftFontSizeTextBox.Text = _biliSettings.GiftFontSize.ToString("0.#");
            BiliEntryMaxTextBox.Text = _biliSettings.MaxEntryLines.ToString();
            BiliEntryVisibleTextBox.Text = _biliSettings.MaxEntryVisibleLines.ToString();
            BiliEntryFontSizeTextBox.Text = _biliSettings.EntryFontSize.ToString("0.#");
            BiliNameScaleTextBox.Text = _biliSettings.NameFontScale.ToString("0.##");
            BiliHeaderFontSizeSlider.Value = Math.Clamp(_biliSettings.HeaderFontSize, 8, 30);
            BiliDanmakuOriginalColorCheckBox.IsChecked = _biliSettings.ShowDanmakuOriginalColor;
            LoadBiliFontCombo();
            BiliClickThroughCheckBox.IsChecked = _biliSettings.ClickThrough;
            BiliShowRoomTitleCheckBox.IsChecked = _biliSettings.ShowRoomTitle;
            BiliShowOnlineCheckBox.IsChecked = _biliSettings.ShowOnline;
            BiliShadowCheckBox.IsChecked = _biliSettings.DanmakuShadow;
            BiliOnlineModeComboBox.SelectedIndex = Math.Clamp(_biliSettings.OnlineDisplayMode, 0, 5);
            BiliGiftNoticeCheckBox.IsChecked = _biliSettings.ShowGiftNotice;
            BiliSuperChatNoticeCheckBox.IsChecked = _biliSettings.ShowSuperChatNotice;
            BiliGuardNoticeCheckBox.IsChecked = _biliSettings.ShowGuardNotice;
            BiliEntryNoticeCheckBox.IsChecked = _biliSettings.ShowEntryNotice;

            _biliGiftColors.Clear();
            foreach (var pair in _biliSettings.GiftColors)
            {
                _biliGiftColors.Add(new GiftColorItem(pair.Key, pair.Value));
            }

            UpdateBiliColorPreviews();
        }
        finally
        {
            _biliLoading = false;
        }
    }

    /// <summary>面板上的设置一改就立即保存并应用到 B站悬浮窗，不需要点保存按钮。</summary>
    private void SubscribeBiliImmediateApply()
    {
        BiliOverlayWidthSlider.ValueChanged += (_, _) => SaveBiliSettingsFromUi(false);
        BiliOverlayHeightSlider.ValueChanged += (_, _) => SaveBiliSettingsFromUi(false);
        BiliOpacitySlider.ValueChanged += (_, _) => SaveBiliSettingsFromUi(false);
        BiliBackgroundOpacitySlider.ValueChanged += (_, _) => SaveBiliSettingsFromUi(false);
        BiliFontSizeSlider.ValueChanged += (_, _) => SaveBiliSettingsFromUi(false);
        BiliOnlineModeComboBox.SelectionChanged += (_, _) => SaveBiliSettingsFromUi(false);
        BiliWrapLengthTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliMaxDanmakuTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliSuperChatMaxTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliSuperChatVisibleTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliSuperChatFontSizeTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliGiftMaxTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliGiftVisibleTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliGiftFontSizeTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliEntryMaxTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliEntryVisibleTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliEntryFontSizeTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliNameScaleTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
        BiliHeaderFontSizeSlider.ValueChanged += (_, _) => SaveBiliSettingsFromUi(false);
        BiliFontFamilyComboBox.SelectionChanged += (_, _) => SaveBiliSettingsFromUi(false);
        BiliRoomIdTextBox.LostFocus += (_, _) => SaveBiliSettingsFromUi(false);
    }

    /// <summary>B站弹幕窗的字体下拉：复用主界面已经枚举好的系统字体列表。</summary>
    private void LoadBiliFontCombo()
    {
        BiliFontFamilyComboBox.ItemsSource = _fontItems;

        var item = _fontItems.FirstOrDefault(
            x => string.Equals(x.Source, _biliSettings.FontFamily, StringComparison.OrdinalIgnoreCase));

        if (item == null && !string.IsNullOrWhiteSpace(_biliSettings.FontFamily))
        {
            // 配置里的字体不在系统列表里（换机器/删字体），补一项让下拉能显示出来
            item = new FontItem(_biliSettings.FontFamily, _biliSettings.FontFamily);
            _fontItems.Insert(0, item);
            BiliFontFamilyComboBox.ItemsSource = _fontItems;
        }

        BiliFontFamilyComboBox.SelectedItem = item ?? _fontItems.FirstOrDefault();
    }

    /// <summary>XAML 里 CheckBox 的 Checked/Unchecked 统一走这里。</summary>
    private void BiliImmediate_Changed(object sender, RoutedEventArgs e) => SaveBiliSettingsFromUi(false);

    private void SaveBiliSettingsFromUi(bool log)
    {
        if (_biliLoading)
        {
            return;
        }

        _biliSettings.RoomId = BiliRoomIdTextBox.Text.Trim();
        _biliSettings.OverlayWidth = Math.Clamp(BiliOverlayWidthSlider.Value, 320, 900);
        _biliSettings.DanmakuMaxHeight = Math.Clamp(BiliOverlayHeightSlider.Value, 120, 1200);
        _biliSettings.OverlayOpacity = Math.Clamp(BiliOpacitySlider.Value, 0.1, 1.0);
        _biliSettings.BackgroundOpacity = Math.Clamp(BiliBackgroundOpacitySlider.Value, 0.0, 1.0);
        _biliSettings.FontSize = Math.Clamp(BiliFontSizeSlider.Value, 10, 30);
        _biliSettings.DanmakuWrapLength = ParseInt(BiliWrapLengthTextBox.Text, 40, 5, 200);
        _biliSettings.MaxDanmakuLines = ParseInt(BiliMaxDanmakuTextBox.Text, 200, 1, 500);
        _biliSettings.MaxSuperChatLines = ParseInt(BiliSuperChatMaxTextBox.Text, 200, 1, 2000);
        _biliSettings.MaxSuperChatVisibleLines = ParseInt(BiliSuperChatVisibleTextBox.Text, 2, 1, 50);
        _biliSettings.SuperChatFontSize = ParseDouble(BiliSuperChatFontSizeTextBox.Text, 12, 8, 30);
        _biliSettings.MaxGiftLines = ParseInt(BiliGiftMaxTextBox.Text, 200, 1, 2000);
        _biliSettings.MaxGiftVisibleLines = ParseInt(BiliGiftVisibleTextBox.Text, 2, 1, 50);
        _biliSettings.GiftFontSize = ParseDouble(BiliGiftFontSizeTextBox.Text, 12, 8, 30);
        _biliSettings.MaxEntryLines = ParseInt(BiliEntryMaxTextBox.Text, 200, 1, 2000);
        _biliSettings.MaxEntryVisibleLines = ParseInt(BiliEntryVisibleTextBox.Text, 1, 1, 50);
        _biliSettings.EntryFontSize = ParseDouble(BiliEntryFontSizeTextBox.Text, 11, 8, 30);
        _biliSettings.NameFontScale = double.TryParse(BiliNameScaleTextBox.Text, out var nameFontScale)
            ? Math.Clamp(nameFontScale, 0.5, 1.5)
            : 0.8;
        _biliSettings.HeaderFontSize = Math.Clamp(BiliHeaderFontSizeSlider.Value, 8, 30);
        _biliSettings.ShowDanmakuOriginalColor = BiliDanmakuOriginalColorCheckBox.IsChecked == true;
        if (BiliFontFamilyComboBox.SelectedItem is FontItem biliFont)
        {
            _biliSettings.FontFamily = biliFont.Source;
        }
        _biliSettings.ClickThrough = BiliClickThroughCheckBox.IsChecked == true;
        _biliSettings.ShowRoomTitle = BiliShowRoomTitleCheckBox.IsChecked == true;
        _biliSettings.ShowOnline = BiliShowOnlineCheckBox.IsChecked == true;
        _biliSettings.DanmakuShadow = BiliShadowCheckBox.IsChecked == true;
        _biliSettings.OnlineDisplayMode = Math.Clamp(BiliOnlineModeComboBox.SelectedIndex, 0, 5);
        _biliSettings.ShowGiftNotice = BiliGiftNoticeCheckBox.IsChecked == true;
        _biliSettings.ShowSuperChatNotice = BiliSuperChatNoticeCheckBox.IsChecked == true;
        _biliSettings.ShowGuardNotice = BiliGuardNoticeCheckBox.IsChecked == true;
        _biliSettings.ShowEntryNotice = BiliEntryNoticeCheckBox.IsChecked == true;
        _biliSettings.GiftColors = _biliGiftColors
            .Where(x => !string.IsNullOrWhiteSpace(x.GiftName))
            .GroupBy(x => x.GiftName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.IsNullOrWhiteSpace(g.Last().Color) ? "#FFFFFF" : g.Last().Color.Trim(), StringComparer.OrdinalIgnoreCase);

        BiliSettingsService.Save(_biliSettings);
        _biliOverlay?.ApplySettings();
        if (log)
        {
            BiliLog("B站配置保存完成");
        }
    }

    private void UpdateBiliColorPreviews()
    {
        BiliBackgroundColorPreview.Background = ParseBrush(_biliSettings.BackgroundColor, Brushes.Black);
        BiliShadowColorPreview.Background = ParseBrush(_biliSettings.ShadowColor, Brushes.Black);
        BiliDanmakuColorPreview.Background = ParseBrush(_biliSettings.DanmakuTextColor, Brushes.White);
        BiliNoticeColorPreview.Background = ParseBrush(_biliSettings.NoticeColor, Brushes.White);
        BiliGiftTextColorPreview.Background = ParseBrush(_biliSettings.GiftTextColor, Brushes.White);
        BiliOnlineColorPreview.Background = ParseBrush(_biliSettings.OnlineColor, Brushes.White);
        BiliGiftColorPreview.Background = ParseBrush(_biliGiftEditColor, Brushes.White);
    }

    // ---------- 颜色选色 ----------

    private void BiliChooseBackgroundColorButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBiliColor(PickColorHex(_biliSettings.BackgroundColor), hex => _biliSettings.BackgroundColor = hex, "背景颜色");

    private void BiliChooseShadowColorButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBiliColor(PickColorHex(_biliSettings.ShadowColor), hex => _biliSettings.ShadowColor = hex, "阴影颜色");

    private void BiliChooseDanmakuColorButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBiliColor(PickColorHex(_biliSettings.DanmakuTextColor), hex => _biliSettings.DanmakuTextColor = hex, "弹幕文字颜色");

    private void BiliChooseNoticeColorButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBiliColor(PickColorHex(_biliSettings.NoticeColor), hex => _biliSettings.NoticeColor = hex, "通知文字颜色");

    private void BiliChooseGiftTextColorButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBiliColor(PickColorHex(_biliSettings.GiftTextColor), hex => _biliSettings.GiftTextColor = hex, "礼物区文字颜色");

    private void BiliChooseOnlineColorButton_Click(object sender, RoutedEventArgs e) =>
        ApplyBiliColor(PickColorHex(_biliSettings.OnlineColor), hex => _biliSettings.OnlineColor = hex, "顶部文字颜色");

    private void ApplyBiliColor(string? hex, Action<string> apply, string label)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return;
        }

        apply(hex);
        UpdateBiliColorPreviews();
        SaveBiliSettingsFromUi(false);
        BiliLog($"{label} → {hex}");
    }

    // ---------- 礼物 / 粉丝牌颜色 ----------

    private void BiliGiftColorListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_biliLoading || BiliGiftColorListBox.SelectedItem is not GiftColorItem item)
        {
            return;
        }

        BiliGiftNameTextBox.Text = item.GiftName;
        _biliGiftEditColor = item.Color;
        UpdateBiliColorPreviews();
    }

    private void BiliChooseGiftColorButton_Click(object sender, RoutedEventArgs e)
    {
        var hex = PickColorHex(_biliGiftEditColor);
        if (string.IsNullOrEmpty(hex))
        {
            return;
        }

        _biliGiftEditColor = hex;

        // 已经选中某一行时顺手把它改掉，符合直觉
        if (BiliGiftColorListBox.SelectedItem is GiftColorItem selected)
        {
            selected.Color = hex;
            SaveBiliSettingsFromUi(false);
        }

        UpdateBiliColorPreviews();
    }

    private void BiliAddGiftColorButton_Click(object sender, RoutedEventArgs e)
    {
        var name = BiliGiftNameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            LogStatus(Copy.BiliGiftNameEmpty);
            return;
        }

        var existing = _biliGiftColors.FirstOrDefault(
            x => string.Equals(x.GiftName.Trim(), name, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Color = _biliGiftEditColor;
            BiliGiftColorListBox.SelectedItem = existing;
        }
        else
        {
            var item = new GiftColorItem(name, _biliGiftEditColor);
            _biliGiftColors.Add(item);
            BiliGiftColorListBox.SelectedItem = item;
        }

        SaveBiliSettingsFromUi(false);
        BiliLog($"礼物颜色：{name} → {_biliGiftEditColor}");
    }

    private void BiliUpdateGiftColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (BiliGiftColorListBox.SelectedItem is not GiftColorItem item)
        {
            LogStatus(Copy.BiliGiftNotSelected);
            return;
        }

        var name = BiliGiftNameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            LogStatus(Copy.BiliGiftNameEmpty);
            return;
        }

        item.GiftName = name;
        item.Color = _biliGiftEditColor;
        SaveBiliSettingsFromUi(false);
        BiliLog($"礼物颜色已更新：{name} → {_biliGiftEditColor}");
    }

    private void BiliRemoveGiftColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (BiliGiftColorListBox.SelectedItem is not GiftColorItem item)
        {
            LogStatus(Copy.BiliGiftNotSelected);
            return;
        }

        _biliGiftColors.Remove(item);
        SaveBiliSettingsFromUi(false);
        BiliLog($"礼物颜色已删除：{item.GiftName}");
    }

    // ---------- 连接 / 断开 ----------

    private async void BiliToggleConnectButton_Click(object sender, RoutedEventArgs e)
    {
        SaveBiliSettingsFromUi(false);

        if (_biliClient != null)
        {
            await DisconnectBiliClientAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(_biliSettings.RoomId))
        {
            MessageBox.Show(this, "请输入直播间号或短号。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BiliToggleConnectButton.IsEnabled = false;
        try
        {
            BiliLog("正在连接直播间 " + _biliSettings.RoomId + " ...");
            _biliRealOnline = 0;
            _biliCurrentRoom = null;
            _biliLastRoomSnapshot = "";

            _biliClient = new BilibiliLiveClient(_biliSettings.BiliCookie);
            _biliClient.RoomInfoUpdated += BiliClient_RoomInfoUpdated;
            _biliClient.DanmakuReceived += BiliClient_DanmakuReceived;
            _biliClient.NoticeReceived += BiliClient_NoticeReceived;
            _biliClient.SuperChatReceived += BiliClient_SuperChatReceived;
            _biliClient.StatusChanged += BiliClient_StatusChanged;
            _biliClient.ErrorOccurred += BiliClient_ErrorOccurred;
            _biliClient.OnlineCountUpdated += BiliClient_OnlineCountUpdated;

            await _biliClient.ConnectAsync(_biliSettings.RoomId);
            ShowBiliOverlay();
            BiliToggleConnectButton.Content = "断开连接";
            SetBiliConnStatus(true, "已连接：" + _biliSettings.RoomId);
        }
        catch (Exception ex)
        {
            BiliLog("连接失败：" + ex.Message);
            if (_biliClient != null)
            {
                var failed = _biliClient;
                _biliClient = null;
                try
                {
                    failed.Dispose();
                }
                catch
                {
                }
            }

            BiliToggleConnectButton.Content = "连接直播间";
            SetBiliConnStatus(false, "连接失败");
        }
        finally
        {
            BiliToggleConnectButton.IsEnabled = true;
        }
    }

    private async Task DisconnectBiliClientAsync()
    {
        var client = _biliClient;
        _biliClient = null;
        if (client != null)
        {
            try
            {
                await client.DisconnectAsync();
            }
            catch
            {
            }
            finally
            {
                client.RoomInfoUpdated -= BiliClient_RoomInfoUpdated;
                client.DanmakuReceived -= BiliClient_DanmakuReceived;
                client.NoticeReceived -= BiliClient_NoticeReceived;
                client.SuperChatReceived -= BiliClient_SuperChatReceived;
                client.StatusChanged -= BiliClient_StatusChanged;
                client.ErrorOccurred -= BiliClient_ErrorOccurred;
                client.OnlineCountUpdated -= BiliClient_OnlineCountUpdated;
                client.Dispose();
            }
        }

        BiliToggleConnectButton.Content = "连接直播间";
        SetBiliConnStatus(false, "未连接");
        BiliLog("已断开连接");
    }

    private void BiliClient_RoomInfoUpdated(RoomInfo room)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // 房间信息每 60 秒刷新一次，只在悬浮窗还没创建时才自动显示，
            // 否则用户手动隐藏的悬浮窗会被定时刷新反复弹回来。
            if (_biliOverlay == null)
            {
                ShowBiliOverlay();
            }

            _biliCurrentRoom = room;
            _biliOverlay?.SetRoomTitle(room.Title, room.UserName);
            _biliOverlay?.SetOnline(room, _biliRealOnline);

            var snapshot = $"{room.RoomId}|{room.Title}|{room.UserName}|{room.Online}|{room.LiveStatus}";
            if (snapshot != _biliLastRoomSnapshot)
            {
                _biliLastRoomSnapshot = snapshot;
                BiliLog($"房间：{room}（room_id={room.RoomId}，{(room.LiveStatus ? "直播中" : "未开播")}，人气 {room.Online}）");
            }
        });
    }

    private void BiliClient_DanmakuReceived(DanmakuItem danmaku)
    {
        Dispatcher.InvokeAsync(() => _biliOverlay?.AddDanmaku(danmaku));
    }

    private void BiliClient_SuperChatReceived(SuperChatItem superChat)
    {
        if (!_biliSettings.ShowSuperChatNotice)
        {
            return;
        }

        Dispatcher.InvokeAsync(() => _biliOverlay?.AddSuperChat(superChat));
    }

    private void BiliClient_NoticeReceived(NoticeItem notice)
    {
        // 进场消息量极大（实测 70 秒 86 条），会把礼物/SC 冲掉，所以按类型过滤。
        if (!IsBiliNoticeEnabled(notice.Kind))
        {
            return;
        }

        Dispatcher.InvokeAsync(() => _biliOverlay?.AddNotice(notice));
    }

    private bool IsBiliNoticeEnabled(NoticeKind kind) => kind switch
    {
        NoticeKind.Entry => _biliSettings.ShowEntryNotice,
        NoticeKind.Follow => _biliSettings.ShowEntryNotice,
        NoticeKind.Share => _biliSettings.ShowEntryNotice,
        NoticeKind.Gift => _biliSettings.ShowGiftNotice,
        // SC 不再走通知路径，它有自己的 SuperChatReceived 事件和 SC 区
        NoticeKind.Guard => _biliSettings.ShowGuardNotice,
        _ => true
    };

    private void BiliClient_OnlineCountUpdated(int onlineCount)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _biliRealOnline = onlineCount;
            if (_biliCurrentRoom != null)
            {
                _biliOverlay?.SetOnline(_biliCurrentRoom, _biliRealOnline);
            }
        });
    }

    private void BiliClient_StatusChanged(string message) => Dispatcher.InvokeAsync(() => BiliLog(message));

    private void BiliClient_ErrorOccurred(Exception ex) => Dispatcher.InvokeAsync(() => BiliLog("错误：" + ex.Message));

    // ---------- 弹幕窗显示控制 ----------

    private void ShowBiliOverlay()
    {
        if (_biliOverlay == null)
        {
            _biliFaceCache ??= new FaceCache();
            _biliOverlay = new BiliOverlayWindow(_biliSettings, _biliFaceCache);
            _biliOverlay.Closed += (_, _) =>
            {
                _biliOverlay = null;
                BiliToggleOverlayButton.Content = "显示弹幕窗";
            };
        }

        if (!_biliOverlay.IsVisible)
        {
            _biliOverlay.Show();
        }

        BiliToggleOverlayButton.Content = "隐藏弹幕窗";
    }

    private void HideBiliOverlay()
    {
        _biliOverlay?.Hide();
        BiliToggleOverlayButton.Content = "显示弹幕窗";
    }

    private void BiliToggleOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_biliOverlay?.IsVisible == true)
        {
            HideBiliOverlay();
            LogStatus(Copy.BiliOverlayHidden);
        }
        else
        {
            ShowBiliOverlay();
            LogStatus(Copy.BiliOverlayShown);
        }
    }

    private void BiliClearOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        _biliOverlay?.ClearMessages();
        BiliLog("B站弹幕窗已清空");
    }

    private void BiliResetOverlayPositionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_biliOverlay == null)
        {
            _biliSettings.OverlayLeft = null;
            _biliSettings.OverlayTop = null;
            _biliSettings.OverlayAnchorBottom = null;
            BiliSettingsService.Save(_biliSettings);
        }
        else
        {
            _biliOverlay.ResetPosition();
        }

        BiliLog("B站弹幕窗位置已重置");
    }

    // ---------- 扫码登录 ----------

    private void BiliQrLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BiliQrLoginWindow { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Cookie))
        {
            return;
        }

        _biliSettings.BiliCookie = dialog.Cookie;
        BiliSettingsService.Save(_biliSettings);
        BiliLog("扫码登录成功，登录凭据已保存到本机");
        _ = CheckBiliLoginStateAsync();
    }

    /// <summary>检查登录态是否还有效；失效就提醒重新扫码。</summary>
    private async Task CheckBiliLoginStateAsync()
    {
        if (string.IsNullOrWhiteSpace(_biliSettings.BiliCookie))
        {
            SetBiliLoginStatus("未登录", false);
            return;
        }

        var info = await BiliLoginService.VerifyLoginAsync(_biliSettings.BiliCookie, System.Threading.CancellationToken.None);
        SetBiliLoginStatus(info.Message, info.IsLogin);

        if (info.IsLogin)
        {
            BiliLog("B站登录态正常：" + info.Message);
        }
        else
        {
            BiliLog("B站登录态已失效，请重新扫码登录（" + info.Message + "）");
        }
    }

    private void SetBiliLoginStatus(string message, bool isLogin)
    {
        if (BiliLoginStatusText == null)
        {
            return;
        }

        BiliLoginStatusText.Text = message;
        BiliLoginStatusText.Foreground = isLogin
            ? (TryFindResource("MintBrush") as Brush ?? Brushes.MediumSeaGreen)
            : (TryFindResource("TextTertiaryBrush") as Brush ?? Brushes.Gray);
    }

    // ---------- 连接状态（显示在「调试后台」面板里） ----------

    private void SetBiliConnStatus(bool connected, string text)
    {
        if (BiliConnDot != null)
        {
            BiliConnDot.Fill = (TryFindResource(connected ? "MintBrush" : "TextTertiaryBrush") as Brush)
                               ?? (connected ? Brushes.MediumSeaGreen : Brushes.Gray);
        }

        if (BiliConnStatusText != null)
        {
            BiliConnStatusText.Text = text;
        }
    }

    private void BiliLog(string message)
    {
        _biliLogBuffer.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (_biliLogBuffer.Count > 2000)
        {
            _biliLogBuffer.RemoveRange(0, _biliLogBuffer.Count - 2000);
        }

        if (BiliLogTextBox != null)
        {
            BiliLogTextBox.Text = string.Join("\r\n", _biliLogBuffer);
            BiliLogTextBox.ScrollToEnd();
        }

        if (BiliLogCountText != null)
        {
            BiliLogCountText.Text = $"共 {_biliLogBuffer.Count} 行";
        }
    }

    // ==================== 版本与检查更新 ====================

    /// <summary>Releases 页面，点「获取更新」时用默认浏览器直接打开这里，少一次点击。</summary>
    private const string ReleasesUrl = "https://github.com/goldiamond1031/MinecraftChatOverlay/releases";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/goldiamond1031/MinecraftChatOverlay/releases/latest";

    private const string TagsApi =
        "https://api.github.com/repos/goldiamond1031/MinecraftChatOverlay/tags";

    /// <summary>检查到的远程版本号（空 = 还没查或没查到）。</summary>
    private string _latestVersion = "";

    /// <summary>当前是否处于「已发现有新版本」的状态，此时按钮变成「获取更新」。</summary>
    private bool _updateAvailable;

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        // 已经发现有新版本了，这一下就是去下载
        if (_updateAvailable)
        {
            OpenReleasesPage();
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = Copy.CheckingUpdate;
        try
        {
            var latest = await FetchLatestVersionAsync();
            if (string.IsNullOrEmpty(latest))
            {
                CheckUpdateButton.Content = Copy.CheckUpdate;
                NotifyCheckFailed("没读到版本号（可能还没发布 Release，或者网络不通）");
                return;
            }

            // 注意：这里比「远程是不是更新」，不是比「一样不一样」。
            // 如果按「不一样就提示」，本地版本领先于线上 Release 时也会一直提示去下载，反而误导。
            ApplyUpdateState(latest);
            if (_updateAvailable)
            {
                LogStatus($"{Copy.UpdateAvailable}{Normalize(latest)}（当前 v{Copy.AppVersion}），点按钮去下载");
            }
            else
            {
                LogStatus(IsSameVersion(latest, Copy.AppVersion)
                    ? Copy.AlreadyLatest + "（v" + Copy.AppVersion + "）"
                    : $"线上最新的是 v{Normalize(latest)}，不比当前 v{Copy.AppVersion} 新");
            }
        }
        catch (Exception ex)
        {
            CheckUpdateButton.Content = Copy.CheckUpdate;
            NotifyCheckFailed(ex.Message);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 取远程最新版本号：优先用最新 Release 的 tag；
    /// 如果仓库还没建过 Release，就退回用第一个 tag。
    /// </summary>
    private static async Task<string> FetchLatestVersionAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub 的 API 要求带 User-Agent，不带会直接 403
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "MinecraftChatOverlay");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        var json = await TryGetStringAsync(http, LatestReleaseApi);
        if (json != null)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tag) &&
                tag.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(tag.GetString()))
            {
                return tag.GetString()!.Trim();
            }
        }

        json = await TryGetStringAsync(http, TagsApi);
        if (json != null)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name) &&
                        name.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(name.GetString()))
                    {
                        return name.GetString()!.Trim();
                    }
                }
            }
        }

        return "";
    }

    private static async Task<string?> TryGetStringAsync(HttpClient http, string url)
    {
        try
        {
            using var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把检查结果反映到界面上：有新版就换成「获取更新」并亮起小红点。
    /// </summary>
    private void ApplyUpdateState(string latest)
    {
        _latestVersion = latest;

        if (IsNewerVersion(latest, Copy.AppVersion))
        {
            _updateAvailable = true;
            CheckUpdateButton.Content = Copy.GetUpdate;
            UpdateDot.Visibility = Visibility.Visible;
        }
        else
        {
            _updateAvailable = false;
            CheckUpdateButton.Content = Copy.CheckUpdate;
            UpdateDot.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>手动检查失败时明确告诉用户（状态栏 + 弹窗），不要只是悄悄写一行日志。</summary>
    private void NotifyCheckFailed(string reason)
    {
        var message = Copy.CheckUpdateFailed + reason;
        LogStatus(message);
        MessageBox.Show(this, message, Copy.CheckUpdateTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 启动时自动查一次更新：有新版就把按钮换成「获取更新」并亮小红点。
    /// 失败保持安静 —— 这只是后台顺手一查，不该每次开软件都弹提示；
    /// 用户手动点「检查更新」失败时才会弹窗。
    /// </summary>
    private async Task AutoCheckUpdateAsync()
    {
        try
        {
            var latest = await FetchLatestVersionAsync();
            if (string.IsNullOrEmpty(latest))
            {
                return;
            }

            ApplyUpdateState(latest);
            if (_updateAvailable)
            {
                LogStatus($"{Copy.UpdateAvailable}{Normalize(latest)}（当前 v{Copy.AppVersion}），点左下角按钮去下载");
            }
        }
        catch
        {
            // 自动检查失败不打扰用户
        }
    }

    /// <summary>
    /// 远程版本是不是比本地新。能解析成 x.y.z 就按数字逐段比（1.0.10 &gt; 1.0.9 才对）；
    /// 解析不出来（比如 tag 不叫版本号）就退回「字符串不一致」。
    /// </summary>
    private static bool IsNewerVersion(string remote, string local)
    {
        var remoteParts = ParseVersionParts(remote);
        var localParts = ParseVersionParts(local);

        if (remoteParts == null || localParts == null)
        {
            return !IsSameVersion(remote, local);
        }

        for (var i = 0; i < Math.Max(remoteParts.Length, localParts.Length); i++)
        {
            var remotePart = i < remoteParts.Length ? remoteParts[i] : 0;
            var localPart = i < localParts.Length ? localParts[i] : 0;
            if (remotePart != localPart)
            {
                return remotePart > localPart;
            }
        }

        return false;
    }

    /// <summary>把 "V1.0.4-beta" 解析成 [1,0,4]；解析不了返回 null。</summary>
    private static int[]? ParseVersionParts(string version)
    {
        var parts = Normalize(version).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            // 只取每段开头的数字，兼容 "4-beta" 这种后缀
            var digits = new string(parts[i].TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0 || !int.TryParse(digits, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    /// <summary>只比「是不是同一个版本」，忽略大小写与开头的 v。</summary>
    private static bool IsSameVersion(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string version)
    {
        var text = version.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        return text.Trim();
    }

    private void OpenReleasesPage()
    {
        try
        {
            // UseShellExecute 才会交给系统用默认浏览器打开
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReleasesUrl)
            {
                UseShellExecute = true
            });
            LogStatus(Copy.ReleasesOpened);
        }
        catch (Exception ex)
        {
            LogStatus(Copy.BrowserFailed + ex.Message);
        }
    }

    private void BiliCopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BiliLogTextBox.Text);
            LogStatus(Copy.BiliLogCopied);
        }
        catch
        {
            // 剪贴板被别的程序占用时忽略即可
        }
    }

    private void BiliClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        _biliLogBuffer.Clear();
        BiliLogTextBox.Clear();
        BiliLogCountText.Text = "共 0 行";
    }
}

/// <summary>把规则中的颜色字符串转换为画刷，用于规则列表里的圆点。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Brushes.Transparent;
        }

        try
        {
            return new BrushConverter().ConvertFromString(text) as Brush ?? Brushes.Transparent;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
