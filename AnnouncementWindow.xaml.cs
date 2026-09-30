using System.Diagnostics;
using System.Windows;
using MinecraftChatOverlay.Services.About;

namespace MinecraftChatOverlay;

/// <summary>
/// 公告弹窗：启动时从仓库拉到公告就弹一次（同一条只弹一次，详见 AppSettings.LastSeenAnnouncementId）。
/// 外观和扫码登录那个窗一个套路：合并同一套主题资源、居中于主窗口。
/// </summary>
public partial class AnnouncementWindow : Window
{
    private readonly string? _link;

    public AnnouncementWindow(Announcement announcement)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(announcement.Title) ? "公告" : announcement.Title.Trim();
        TitleText.Text = announcement.Title?.Trim() ?? "";

        // 正文里的 \n 直接当换行用
        BodyText.Text = (announcement.Body ?? "").Replace("\r\n", "\n").Trim();

        var date = announcement.UpdatedAt;
        DateText.Text = date is null ? "" : date.Value.ToString("yyyy-MM-dd");

        _link = (announcement.Link ?? "").Trim();
        if (_link.Length > 0 && Uri.TryCreate(_link, UriKind.Absolute, out _))
        {
            LinkButton.Content = string.IsNullOrWhiteSpace(announcement.LinkText)
                ? "查看详情"
                : announcement.LinkText.Trim();
            LinkButton.Visibility = Visibility.Visible;
        }
    }

    private void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_link))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_link) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，不影响关窗
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
