using System.Windows;

namespace MinecraftChatOverlay;

/// <summary>
/// 「击杀反馈 · 匹配规则怎么填」的教程弹窗。
///
/// 为什么单独开个窗而不是在页面里展开：这页的设置项本来就密，把教程塞进卡片会把
/// 「示例消息 / 敌方名字 / 规则框」挤散。弹窗可以随时关掉，不占常驻位置。
///
/// 外观和公告窗 / B站扫码登录窗一个套路：自己合并同一套主题资源（ModernControls.xaml）、
/// 居中于主窗口 —— 弹窗不在主窗口的视觉树上，宿主资源查不到，必须自己挂一份。
/// </summary>
public partial class KillFeedbackHelpWindow : Window
{
    public KillFeedbackHelpWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}