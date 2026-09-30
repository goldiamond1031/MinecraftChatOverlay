using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MinecraftChatOverlay.Services.About;

namespace MinecraftChatOverlay;

/// <summary>
/// 「关于」页：开源声明 + 打赏二维码（编译进程序集的资源）+ 两份鸣谢名单。
/// 名单是仓库里的 about/about.json，和插件市场同一个套路拉下来（jsDelivr 主源 + GitHub raw 备用，
/// 谁的 updatedAt 新用谁）。没有磁盘缓存 —— 失败时保留内存里上一份就行，这页丢不了什么。
/// </summary>
public partial class MainWindow
{
    private AboutIndex? _aboutIndex;
    private DateTime? _aboutFetchedAt;
    private string _aboutNotice = "";
    private bool _aboutBusy;

    /// <summary>懒加载只做一次（拉到过数据就不再自动拉）。</summary>
    private bool _aboutAutoLoadStarted;

    // ------------------------------------------------------------ 加载

    private async Task EnsureAboutLoadedAsync()
    {
        if (_aboutAutoLoadStarted)
        {
            return;
        }

        _aboutAutoLoadStarted = true;

        if (_aboutIndex is null)
        {
            await RefreshAboutAsync(manual: false).ConfigureAwait(true);
        }
        else
        {
            RebuildAboutLists();
        }
    }

    private async void AboutRefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAboutAsync(manual: true);

    private async Task RefreshAboutAsync(bool manual)
    {
        if (_aboutBusy)
        {
            return;
        }

        _aboutBusy = true;
        _aboutNotice = "";
        UpdateAboutStatus();

        try
        {
            var result = await AboutClient.FetchAsync(AboutClient.DefaultIndexUrl, AboutClient.DefaultFallbackUrl, CancellationToken.None);

            if (!result.Ok)
            {
                _aboutNotice = "连不上名单：" + result.Error;
                AppendDebugLog("[关于] 刷新失败：" + result.Error);

                if (manual)
                {
                    ShowToast("鸣谢名单刷新失败，详情见调试后台");
                }

                return;
            }

            var index = result.Index!;
            _aboutIndex = index;
            _aboutFetchedAt = DateTime.Now;
            _aboutNotice = "";
            RebuildAboutLists();
            UpdateAboutStatus();

            if (manual)
            {
                ShowToast($"名单已更新：{index.Rewards.Count} 条打赏鸣谢 · {index.PluginDevs.Count} 位插件开发者");
            }
        }
        catch (Exception ex)
        {
            _aboutNotice = "刷新出错：" + ex.Message;
            AppendDebugLog("[关于] 刷新异常：" + ex.Message);
        }
        finally
        {
            _aboutBusy = false;
            UpdateAboutStatus();
        }
    }

    // ------------------------------------------------------------ 状态行

    private void UpdateAboutStatus()
    {
        var parts = new List<string>();
        if (_aboutFetchedAt is not null)
        {
            parts.Add("上次更新 " + _aboutFetchedAt.Value.ToString("yyyy-MM-dd HH:mm"));
        }

        if (_aboutBusy)
        {
            parts.Add("正在从 GitHub 拉名单…");
        }
        else if (!string.IsNullOrEmpty(_aboutNotice))
        {
            parts.Add(_aboutNotice);
        }
        else if (_aboutIndex is not null)
        {
            parts.Add("来源 " + ShortHost(_aboutIndex.SourceUrl));
        }

        var text = parts.Count > 0 ? string.Join("  ·  ", parts) : "还没拉到名单。";
        AboutRewardsStatusText.Text = text;
        AboutDevsStatusText.Text = _aboutBusy ? "正在从 GitHub 拉名单…" : "";
    }

    // ------------------------------------------------------------ 名单渲染

    private void RebuildAboutLists()
    {
        try
        {
            AboutRewardsList.Children.Clear();
            AboutDevsList.Children.Clear();

            var index = _aboutIndex;

            // 打赏鸣谢
            if (index is null || index.Rewards.Count == 0)
            {
                AboutRewardsEmptyText.Text = index is null
                    ? "还没有名单数据。点右上角【刷新名单】试试；连不上时会显示之前拉到过的列表。"
                    : "打赏鸣谢名单现在是空的（about.json 里 rewards 数组没有内容）。";
                AboutRewardsEmptyText.Visibility = Visibility.Visible;
            }
            else
            {
                AboutRewardsEmptyText.Visibility = Visibility.Collapsed;
                foreach (var reward in index.Rewards)
                {
                    AboutRewardsList.Children.Add(BuildRewardCard(reward));
                }
            }

            // 插件开发鸣谢
            if (index is null || index.PluginDevs.Count == 0)
            {
                AboutDevsEmptyText.Text = index is null
                    ? "还没有名单数据。点右上角【刷新名单】试试。"
                    : "插件开发鸣谢名单现在是空的（about.json 里 pluginDevs 数组没有内容）。";
                AboutDevsEmptyText.Visibility = Visibility.Visible;
            }
            else
            {
                AboutDevsEmptyText.Visibility = Visibility.Collapsed;
                foreach (var dev in index.PluginDevs)
                {
                    AboutDevsList.Children.Add(BuildDevCard(dev));
                }
            }
        }
        catch (Exception ex)
        {
            AppendDebugLog("[关于] 渲染名单失败：" + ex.Message);
        }
    }

    /// <summary>打赏鸣谢一行：名字 + 金额，下面跟留言和时间。</summary>
    private Border BuildRewardCard(AboutRewardEntry reward)
    {
        var card = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var header = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        header.Inlines.Add(new Run(reward.Name) { FontSize = 13.5, FontWeight = FontWeights.Bold });

        var amount = (reward.Amount ?? "").Trim();
        if (amount.Length > 0)
        {
            header.Inlines.Add(new Run("　" + amount)
            {
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = TryFindResource("AccentBrush") as Brush
                    ?? new SolidColorBrush(Color.FromRgb(0xE6, 0xA2, 0x3C))
            });
        }

        card.Children.Add(header);

        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(reward.Message))
        {
            extras.Add(reward.Message.Trim());
        }

        if (!string.IsNullOrWhiteSpace(reward.Time))
        {
            extras.Add(reward.Time.Trim());
        }

        if (extras.Count > 0)
        {
            var meta = new TextBlock
            {
                Text = string.Join("　—　", extras),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            meta.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
            card.Children.Add(meta);
        }

        return new Border
        {
            Child = card,
            Padding = new Thickness(0, 0, 0, 2),
            BorderBrush = TryFindResource("BorderBrush") as Brush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(0, 0, 0, 2)
        };
    }

    /// <summary>插件开发鸣谢一行：名字 +（插件/贡献），主页链接可以点开，再跟备注。</summary>
    private Border BuildDevCard(AboutDevEntry dev)
    {
        var card = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var header = new TextBlock { TextWrapping = TextWrapping.Wrap };
        header.Inlines.Add(new Run(dev.Name) { FontSize = 13.5, FontWeight = FontWeights.Bold });

        var plugin = (dev.Plugin ?? "").Trim();
        if (plugin.Length > 0)
        {
            header.Inlines.Add(new Run("　" + plugin)
            {
                FontSize = 12,
                Foreground = TryFindResource("TextSecondaryBrush") as Brush
            });
        }

        card.Children.Add(header);

        var link = (dev.Link ?? "").Trim();
        if (link.Length > 0 && Uri.TryCreate(link, UriKind.Absolute, out var linkUri))
        {
            var hyperlink = new Hyperlink(new Run(linkUri.Host + linkUri.AbsolutePath))
            {
                NavigateUri = linkUri,
                ToolTip = link
            };
            hyperlink.RequestNavigate += (_, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AppendDebugLog("[关于] 打开链接失败：" + ex.Message);
                }

                e.Handled = true;
            };

            var linkText = new TextBlock { Margin = new Thickness(0, 2, 0, 0) };
            linkText.Inlines.Add(hyperlink);
            card.Children.Add(linkText);
        }

        if (!string.IsNullOrWhiteSpace(dev.Note))
        {
            var note = new TextBlock
            {
                Text = dev.Note.Trim(),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            note.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
            card.Children.Add(note);
        }

        return new Border
        {
            Child = card,
            BorderBrush = TryFindResource("BorderBrush") as Brush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(0, 0, 0, 2)
        };
    }
}
