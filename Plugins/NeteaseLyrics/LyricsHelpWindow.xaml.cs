using System.Windows;
using System.Windows.Media;

// WPF + WinForms 同时开着，Application / Color / FontFamily 这些名字会二义，全部钉死成 WPF 那套
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace MinecraftChatOverlay.Plugins.NeteaseLyrics;

/// <summary>
/// 【使用说明】弹窗 —— 外观照抄软件本体的公告窗（AnnouncementWindow）/ B站扫码窗：
/// 同一套 ModernControls.xaml 主题、SizeToContent=Height、居中于主窗口。
///
/// 为什么要在构造函数里手动挂主题字典：
/// 宿主把 ModernControls.xaml 挂在**主窗口**的 Window.Resources 上（App.xaml 的
/// Application.Resources 是空的），插件自己 new 出来的窗口不在那棵树上，一个键都查不到。
/// 所以这里先挂字典、再 InitializeComponent；XAML 里一律用 DynamicResource，
/// 万一字典没挂上也只是变回系统默认样式，不会当场崩掉。
/// </summary>
public partial class LyricsHelpWindow : Window
{
    private readonly NeteaseLyricsPlugin _plugin;

    public LyricsHelpWindow(NeteaseLyricsPlugin plugin)
    {
        _plugin = plugin;

        AttachHostTheme();
        InitializeComponent();
        ApplyFallbacks();
        _plugin.Log("[歌词] 使用说明窗主题字典：" + (TryFindResource("WindowBgBrush") is null ? "没挂上（会用系统默认外观）" : "已挂上"));

        SourceText.Text = "数据来源：窗口标题 + MCOBridge 中继";

        var result = plugin.RelayInstallResult;
        if (!string.IsNullOrWhiteSpace(result))
        {
            StatusText.Text = result;
            StatusBox.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 把宿主的主题字典挂到自己身上：
    /// 先用跨程序集的 pack URI 重新加载一份；拿不到就直接借用主窗口已经挂好的那些字典实例。
    /// </summary>
    private void AttachHostTheme()
    {
        try
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/MinecraftChatOverlay;component/ModernControls.xaml", UriKind.Absolute),
            });
            return;
        }
        catch
        {
            _plugin.Log("[歌词] 使用说明窗：跨程序集加载 ModernControls.xaml 失败，改用主窗口那份字典");
            // 加载不了就退到下面借主窗口那份
        }

        try
        {
            var main = Application.Current?.MainWindow;
            if (main is null)
            {
                return;
            }

            foreach (var dictionary in main.Resources.MergedDictionaries)
            {
                if (!Resources.MergedDictionaries.Contains(dictionary))
                {
                    Resources.MergedDictionaries.Add(dictionary);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>字典也没挂上的极端情况：至少别让窗口变成一片白、字看不见。</summary>
    private void ApplyFallbacks()
    {
        try
        {
            if (TryFindResource("WindowBgBrush") is null)
            {
                Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC));
            }
        }
        catch
        {
        }
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        try
        {
            StatusText.Text = _plugin.InstallRelay();
        }
        catch (Exception ex)
        {
            StatusText.Text = "装不上：" + ex.Message;
        }
        finally
        {
            InstallButton.IsEnabled = true;
            StatusBox.Visibility = Visibility.Visible;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
