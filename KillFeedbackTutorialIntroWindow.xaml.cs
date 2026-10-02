using System.Windows;

namespace MinecraftChatOverlay;

/// <summary>
/// 进「匹配规则 · 新手引导」之前的确认窗：说清进去会清空已填规则，
/// 给【进入教程】/【再等等】两个选项。
///
/// 引导本体做在主窗口里（页面底部浮层 + 高亮控件 + 替用户改规则框），这里只负责问一句 ——
/// 所以这个窗没有任何状态，DialogResult 就是它的全部输出。
/// </summary>
public partial class KillFeedbackTutorialIntroWindow : Window
{
    public KillFeedbackTutorialIntroWindow()
    {
        InitializeComponent();
    }

    private void EnterButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}