using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Plugin;
using MinecraftChatOverlay.Services;
using MinecraftChatOverlay.Services.Plugins;
using MinecraftChatOverlay.Services.Plugins.Market;

namespace MinecraftChatOverlay;

/// <summary>
/// 插件板块里的「插件市场」子视图。
///
/// 市场没有服务器：清单是仓库里的 market/index.json，插件包是仓库里的 zip。
/// 所以这里只做三件事 —— 拉清单（失败退回缓存）、列出来、下载完交给现有的 ImportZip 安装。
/// 安装确认框、zip 校验、apiVersion 检查、dll 被占用的排队逻辑，全部复用插件页那一套。
/// </summary>
public partial class MainWindow
{
    private MarketIndex? _marketIndex;
    private string _marketSourceUrl = "";
    private DateTime? _marketFetchedAt;
    private string _marketNotice = "";
    private string _marketSearch = "";
    private bool _marketBusy;
    private bool _marketUiReady;

    // ------------------------------------------------------------ 初始化

    private void InitializePluginMarketUi()
    {
        if (_marketUiReady)
        {
            return;
        }

        _marketUiReady = true;

        try
        {
            PluginMarketUrlTextBox.Text = EnsureMarketSettings().IndexUrl;

            var cached = PluginMarketCache.TryLoad();
            if (cached.Index is not null)
            {
                _marketIndex = cached.Index;
                _marketSourceUrl = cached.SourceUrl;
                _marketFetchedAt = cached.FetchedAt;
            }

            RebuildMarketList();
            UpdateMarketStatus();

            // 后台预热：没有缓存、或缓存超过 6 小时，就悄悄刷一次 —— 等你点开市场时列表已经在了。
            if (_marketFetchedAt is null || DateTime.Now - _marketFetchedAt.Value > TimeSpan.FromHours(6))
            {
                _ = RefreshMarketAsync(false);
            }
        }
        catch (Exception ex)
        {
            AppendDebugLog("[市场] 初始化失败：" + ex.Message);
        }
    }

    private PluginMarketSettings EnsureMarketSettings()
    {
        var settings = _settings.PluginMarket ??= new PluginMarketSettings();

        if (string.IsNullOrWhiteSpace(settings.IndexUrl))
        {
            settings.IndexUrl = PluginMarketSettings.DefaultIndexUrl;
        }

        if (string.IsNullOrWhiteSpace(settings.FallbackUrl))
        {
            settings.FallbackUrl = PluginMarketSettings.DefaultFallbackUrl;
        }

        return settings;
    }

    // ------------------------------------------------------------ 子视图切换

    private void PluginMarketButton_Click(object sender, RoutedEventArgs e) => ShowPluginMarket();

    private void PluginMarketBackButton_Click(object sender, RoutedEventArgs e) => ShowPluginList();

    private void ShowPluginMarket()
    {
        try
        {
            _openPluginId = null;
            PluginDetailContent.Children.Clear();
            PluginDetailView.Visibility = Visibility.Collapsed;
            PluginsListView.Visibility = Visibility.Collapsed;
            PluginMarketView.Visibility = Visibility.Visible;

            if (_marketIndex is null || _marketFetchedAt is null)
            {
                _ = RefreshMarketAsync(false);
            }
            else
            {
                RebuildMarketList();
                UpdateMarketStatus();
            }
        }
        catch (Exception ex)
        {
            AppendDebugLog("[市场] 打开失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------ 刷新

    private async void PluginMarketRefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshMarketAsync(true);

    private async Task RefreshMarketAsync(bool manual)
    {
        if (_marketBusy)
        {
            return;
        }

        _marketBusy = true;
        _marketNotice = "";
        UpdateMarketStatus();

        try
        {
            var settings = EnsureMarketSettings();
            var result = await PluginMarketClient.FetchIndexAsync(settings.IndexUrl, settings.FallbackUrl, CancellationToken.None);

            if (!result.Ok)
            {
                _marketNotice = "连不上市场：" + result.Error;
                AppendDebugLog("[市场] 刷新失败：" + result.Error);

                if (manual)
                {
                    ShowToast("市场刷新失败，详情见调试后台");
                }

                return;
            }

            _marketIndex = result.Index;
            _marketSourceUrl = result.SourceUrl;
            _marketFetchedAt = DateTime.Now;
            PluginMarketCache.Save(result.RawJson, result.SourceUrl, _marketFetchedAt.Value);

            settings.LastRefreshAt = _marketFetchedAt;
            SettingsService.Save(_settings);

            AppendDebugLog($"[市场] 刷新成功：{result.Index!.Plugins.Count} 个插件（源 {result.SourceUrl}）");
            RebuildMarketList();

            if (manual)
            {
                ShowToast($"市场已更新：{result.Index.Plugins.Count} 个插件");
            }
        }
        catch (Exception ex)
        {
            _marketNotice = "刷新出错：" + ex.Message;
            AppendDebugLog("[市场] 刷新异常：" + ex.Message);
        }
        finally
        {
            _marketBusy = false;
            UpdateMarketStatus();
        }
    }

    private void UpdateMarketStatus()
    {
        try
        {
            var parts = new List<string>();

            if (_marketBusy)
            {
                parts.Add("正在连接市场…");
            }
            else
            {
                parts.Add(_marketIndex is null ? "还没有市场数据" : $"{_marketIndex.Plugins.Count} 个插件");

                if (!string.IsNullOrWhiteSpace(_marketNotice))
                {
                    parts.Add(_marketNotice);
                }

                if (_marketFetchedAt is { } fetched)
                {
                    parts.Add("上次刷新 " + fetched.ToString("MM-dd HH:mm"));
                }

                if (!string.IsNullOrWhiteSpace(_marketSourceUrl))
                {
                    parts.Add("源 " + ShortHost(_marketSourceUrl));
                }
            }

            PluginMarketStatusText.Text = string.Join("  ·  ", parts);
            PluginMarketRefreshButton.IsEnabled = !_marketBusy;
        }
        catch
        {
            // 状态行刷不出来不该影响别的
        }
    }

    private static string ShortHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    // ------------------------------------------------------------ 列表

    private void PluginMarketSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _marketSearch = PluginMarketSearchBox.Text?.Trim() ?? "";
        RebuildMarketList();
    }

    private void RebuildMarketList()
    {
        if (!_marketUiReady)
        {
            return;
        }

        try
        {
            PluginMarketListPanel.Children.Clear();

            var index = _marketIndex;
            if (index is null)
            {
                PluginMarketEmptyText.Text = "还没有市场数据。点右上角【刷新】试试；连不上时会显示上次缓存的列表。";
                PluginMarketEmptyText.Visibility = Visibility.Visible;
                return;
            }

            var items = index.Plugins
                .Where(p => MatchesSearch(p, _marketSearch))
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCulture)
                .ToList();

            PluginMarketEmptyText.Text = index.Plugins.Count == 0
                ? @"市场清单是空的。把插件 zip 放进仓库的 market\packages\，再跑一次 tools\rebuild-market-index.ps1 重建清单。"
                : "没有匹配的插件。";
            PluginMarketEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var plugin in items)
            {
                PluginMarketListPanel.Children.Add(BuildMarketCard(plugin));
            }
        }
        catch (Exception ex)
        {
            AppendDebugLog("[市场] 渲染列表失败：" + ex.Message);
        }
    }

    private static bool MatchesSearch(MarketPlugin plugin, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return plugin.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Author.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>市场卡片：名字/版本/作者 + 简介 + 能力 + 状态 + 安装按钮。外观和插件列表卡片同一套。</summary>
    private Border BuildMarketCard(MarketPlugin plugin)
    {
        var border = new Border { Margin = new Thickness(0, 0, 12, 12) };
        border.SetResourceReference(FrameworkElement.StyleProperty, "PluginCardStyle");

        var card = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleStack = new StackPanel();

        var titleText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        titleText.Inlines.Add(new Run(plugin.DisplayName) { FontSize = 14.5, FontWeight = FontWeights.Bold });
        titleStack.Children.Add(titleText);

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(plugin.Version))
        {
            meta.Add("v" + plugin.Version.Trim().TrimStart('v', 'V'));
        }

        if (!string.IsNullOrWhiteSpace(plugin.Author))
        {
            meta.Add(plugin.Author);
        }

        if (plugin.Tags.Count > 0)
        {
            meta.Add(string.Join(" / ", plugin.Tags));
        }

        var metaText = PluginSecondary(meta.Count > 0 ? string.Join("  ·  ", meta) : "（作者没写版本和作者）", 12);
        metaText.Margin = new Thickness(0, 2, 0, 0);
        titleStack.Children.Add(metaText);

        header.Children.Add(titleStack);

        var button = BuildMarketActionButton(plugin);
        button.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(button, 1);
        header.Children.Add(button);

        card.Children.Add(header);

        var description = PluginText(
            string.IsNullOrWhiteSpace(plugin.Description) ? "（作者没写简介）" : plugin.Description,
            "CardDescStyle", true);
        description.Margin = new Thickness(0, 8, 0, 0);
        card.Children.Add(description);

        if (plugin.Capabilities.Count > 0)
        {
            var capabilities = PluginText("它会做的事：" + string.Join("、", plugin.Capabilities), "HintStyle", true);
            capabilities.Margin = new Thickness(0, 6, 0, 0);
            card.Children.Add(capabilities);
        }

        var state = PluginText(DescribeMarketState(plugin), "HintStyle", true);
        state.Margin = new Thickness(0, 6, 0, 0);
        card.Children.Add(state);

        border.Child = card;
        return border;
    }

    private string DescribeMarketState(MarketPlugin plugin)
    {
        if (plugin.ApiVersion != PluginApi.Version)
        {
            return $"需要的契约版本是 v{plugin.ApiVersion}，本程序是 v{PluginApi.Version} —— 装不了，先更新本程序";
        }

        var installed = FindInstalledPlugin(plugin.Id);
        if (installed is null)
        {
            return "未安装";
        }

        var installedVersion = installed.Manifest.Version;
        return MarketPlugin.CompareVersions(plugin.Version, installedVersion) > 0
            ? $"已安装 v{installedVersion}，可更新到 v{plugin.Version}"
            : $"已安装 v{installedVersion}（已是最新）";
    }

    private PluginEntry? FindInstalledPlugin(string id) =>
        _pluginManager?.Entries.FirstOrDefault(e => string.Equals(e.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));

    private Button BuildMarketActionButton(MarketPlugin plugin)
    {
        var installed = FindInstalledPlugin(plugin.Id);
        var incompatible = plugin.ApiVersion != PluginApi.Version;
        var upToDate = installed is not null && MarketPlugin.CompareVersions(plugin.Version, installed.Manifest.Version) <= 0;

        var button = new Button { MinWidth = 78, Margin = new Thickness(10, 0, 0, 0) };

        if (incompatible)
        {
            button.Content = "版本不符";
            button.IsEnabled = false;
            button.SetResourceReference(FrameworkElement.StyleProperty, "SmallGhostButtonStyle");
        }
        else if (upToDate)
        {
            button.Content = "已装最新";
            button.IsEnabled = false;
            button.SetResourceReference(FrameworkElement.StyleProperty, "SmallGhostButtonStyle");
        }
        else
        {
            button.Content = installed is null ? "安装" : "更新";
            button.SetResourceReference(FrameworkElement.StyleProperty, "SmallPrimaryButtonStyle");
            button.Click += async (_, _) => await InstallMarketPluginAsync(plugin);
        }

        return button;
    }

    // ------------------------------------------------------------ 下载 + 安装

    private async Task InstallMarketPluginAsync(MarketPlugin plugin)
    {
        if (_marketBusy)
        {
            return;
        }

        var url = plugin.ResolveDownloadUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowToast("这条记录的下载地址不对，装不了");
            return;
        }

        _marketBusy = true;
        _marketNotice = "";
        UpdateMarketStatus();

        try
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "MinecraftChatOverlay-market");
            System.IO.Directory.CreateDirectory(tempDirectory);

            var tempZip = Path.Combine(tempDirectory, MakeSafeFileName(plugin.Id + "-" + plugin.Version) + ".zip");

            PluginMarketStatusText.Text = $"正在下载 {plugin.DisplayName} …";
            var (ok, error) = await PluginMarketClient.DownloadAsync(url, tempZip, plugin.Size, plugin.Sha256, CancellationToken.None);

            if (!ok)
            {
                _marketNotice = "下载失败：" + error;
                AppendDebugLog($"[市场] 下载 {plugin.Id} 失败：{error}");
                ShowToast("下载失败：" + error);
                return;
            }

            var bytes = new FileInfo(tempZip).Length;
            AppendDebugLog($"[市场] 已下载 {plugin.Id} v{plugin.Version}（{bytes} 字节）→ 交给插件导入流程");

            // 后面的确认框、zip 校验、apiVersion 检查、装载、刷新列表全在 ImportPluginZip 里
            // （和手动导入 zip 走的是同一条路，不另开安装逻辑）
            ImportPluginZip(tempZip);
        }
        catch (Exception ex)
        {
            _marketNotice = "安装出错：" + ex.Message;
            AppendDebugLog("[市场] 安装异常：" + ex.Message);
        }
        finally
        {
            _marketBusy = false;
            RebuildMarketList();
            UpdateMarketStatus();
        }
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "plugin" : cleaned;
    }

    // ------------------------------------------------------------ 市场源设置

    private void PluginMarketUrlSaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = EnsureMarketSettings();
            var url = PluginMarketUrlTextBox.Text?.Trim() ?? "";
            settings.IndexUrl = string.IsNullOrWhiteSpace(url) ? PluginMarketSettings.DefaultIndexUrl : url;
            PluginMarketUrlTextBox.Text = settings.IndexUrl;
            SettingsService.Save(_settings);
            _ = RefreshMarketAsync(true);
        }
        catch (Exception ex)
        {
            ShowToast("保存市场地址失败：" + ex.Message);
        }
    }

    private void PluginMarketUrlResetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = EnsureMarketSettings();
            settings.IndexUrl = PluginMarketSettings.DefaultIndexUrl;
            settings.FallbackUrl = PluginMarketSettings.DefaultFallbackUrl;
            PluginMarketUrlTextBox.Text = settings.IndexUrl;
            SettingsService.Save(_settings);
            _ = RefreshMarketAsync(true);
        }
        catch (Exception ex)
        {
            AppendDebugLog("[市场] 恢复默认地址失败：" + ex.Message);
        }
    }
}