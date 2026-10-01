using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MinecraftChatOverlay.Plugin;
using MinecraftChatOverlay.Services;
using MinecraftChatOverlay.Services.Plugins;

namespace MinecraftChatOverlay;

/// <summary>
/// 插件板块：装载/管理插件，并把插件注册的页面画出来。
///
/// 设计选择：插件页面由**宿主渲染**（插件只交字段：开关/滑块/下拉/文本/按钮）。
/// 这样插件页面和内置页面同一个外观、同一套主题，也不会因为宿主改样式而白屏；
/// 插件代码碰宿主 UI 的每一个入口都包了 try/catch，一个烂插件只烂它自己。
/// </summary>
public partial class MainWindow
{
    private PluginManager? _pluginManager;
    private readonly List<PluginPageHost> _pluginPageHosts = new();
    private DispatcherTimer? _pluginRefreshTimer;
    private string? _openPluginId;   // 现在正在看哪个插件的页面（null = 在列表）

    /// <summary>一个插件页面在宿主这边的全部接线。</summary>
    private sealed class PluginPageHost
    {
        public required PluginEntry Entry { get; init; }

        public required PluginPage Page { get; init; }

        public required StackPanel Content { get; init; }

        public TextBlock? StatusText { get; set; }

        public List<PluginFieldBinding> Fields { get; } = new();

        public List<PluginActionBinding> Actions { get; } = new();
    }

    private sealed class PluginFieldBinding
    {
        public required PluginField Field { get; init; }

        public FrameworkElement? Element { get; set; }

        /// <summary>每秒刷新时调用（下拉框重取候选项、只读文本更新）。</summary>
        public Action? Refresh { get; set; }

        public DateTime NextOptionsRefresh { get; set; }
    }

    private sealed class PluginActionBinding
    {
        public required PluginAction Action { get; init; }

        public FrameworkElement? Element { get; set; }
    }

    // ------------------------------------------------------------------ 初始化

    private void InitializePluginsUi()
    {
        try
        {
            var portable = Path.Combine(AppContext.BaseDirectory, "plugins");
            var user = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MinecraftChatOverlay",
                "plugins");

            Directory.CreateDirectory(user);

            _pluginManager = new PluginManager(portable, user, _settings.DisabledPlugins ?? new List<string>());
            _pluginManager.PluginLog += AppendDebugLog;
            _pluginManager.PluginToast += ShowToast;
            _pluginManager.PluginOverlayMessage += OnPluginOverlayMessage;
            _pluginManager.PluginOverlayTopmostRequested += OnPluginOverlayTopmostRequested;
            _pluginManager.Changed += OnPluginsChanged;

            // 只报用户插件目录：内置插件根（exe 旁 plugins\）留着备用，但从来没放过东西，
            // 报出来只会让人以为那儿还有一份插件。
            PluginsRootText.Text = "用户插件目录：" + user;

            // 支持把 zip 直接拖到这一页
            PluginsPanel.AllowDrop = true;
            PluginsPanel.DragOver += PluginsPanel_DragOver;
            PluginsPanel.Drop += PluginsPanel_Drop;

            _pluginManager.LoadAll();
            RebuildPluginPages();
            RefreshPluginsManagerList();
        }
        catch (Exception ex)
        {
            AppendDebugLog("[插件] 初始化失败：" + ex.Message);
        }
    }

    private void ShutdownPlugins()
    {
        try
        {
            _pluginRefreshTimer?.Stop();
            _pluginRefreshTimer = null;
            _pluginManager?.ShutdownAll();
        }
        catch (Exception ex)
        {
            AppendDebugLog("[插件] 退出清理出错：" + ex.Message);
        }
    }

    /// <summary>插件要往悬浮窗发消息：显示悬浮窗并追加一条。</summary>
    private void OnPluginOverlayMessage(PluginEntry entry, string text)
    {
        try
        {
            ShowOverlay();
            // 插件注入的消息也过一遍「快捷命令」匹配：命中就一样可以点击发送
            _overlay?.AddMessage(text);
        }
        catch (Exception ex)
        {
            AppendDebugLog($"[插件] 发送到悬浮窗失败：{ex.Message}");
        }
    }

    /// <summary>插件请求把悬浮窗重新置顶（「窗口全屏」把游戏窗口铺满后会叫一下）。</summary>
    private void OnPluginOverlayTopmostRequested()
    {
        try
        {
            _overlay?.ReassertTopmost();
        }
        catch (Exception ex)
        {
            AppendDebugLog("[插件] 抬悬浮窗失败：" + ex.Message);
        }
    }

    /// <summary>宿主收到一条聊天消息 → 投给所有插件。</summary>
    private void NotifyPluginsChatLine(string chatMessage)
    {
        try
        {
            _pluginManager?.OnChatLine(chatMessage);
        }
        catch (Exception ex)
        {
            AppendDebugLog("[插件] 投递聊天消息出错：" + ex.Message);
        }
    }

    private void OnPluginsChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RebuildPluginPages();
            RefreshPluginsManagerList();
        }));
    }

    // ------------------------------------------------------------------ 导航项与页面

    /// <summary>按当前插件列表重建动态导航项和面板。</summary>
    private void RebuildPluginPages()
    {
        if (_pluginManager is null)
        {
            return;
        }

        _pluginPageHosts.Clear();

        foreach (var entry in _pluginManager.Entries)
        {
            if (entry.State != PluginState.Loaded || entry.Page is null)
            {
                continue;
            }

            try
            {
                _pluginPageHosts.Add(CreatePluginPageHost(entry, entry.Page));
            }
            catch (Exception ex)
            {
                AppendDebugLog($"[插件] {entry.Manifest.Id} 页面创建失败：{ex.Message}");
            }
        }

        if (_pluginRefreshTimer is null)
        {
            _pluginRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pluginRefreshTimer.Tick += (_, _) => UpdatePluginPages();
            _pluginRefreshTimer.Start();
        }

        // 正在看的插件被禁用/卸载了 → 退回列表；还在就重新挂上它的页面
        if (_openPluginId is not null)
        {
            var open = _pluginPageHosts.FirstOrDefault(h => h.Entry.Manifest.Id == _openPluginId);
            if (open is null)
            {
                ShowPluginList();
            }
            else
            {
                ShowPluginPage(open);
            }
        }

        MoveIndicatorToSelected();
    }
    /// <summary>点插件卡片：进它的主页面。</summary>
    private void OpenPluginPage(PluginEntry entry)
    {
        var host = _pluginPageHosts.FirstOrDefault(h => h.Entry.Manifest.Id == entry.Manifest.Id);
        if (host is null)
        {
            ShowToast($"{entry.DisplayName} 现在没在运行（被禁用或装载失败）");
            return;
        }

        ShowPluginPage(host);
    }

    /// <summary>把插件页面挂到详情区（插件板块内部切换，不占导航栏）。</summary>
    private void ShowPluginPage(PluginPageHost host)
    {
        try
        {
            _openPluginId = host.Entry.Manifest.Id;
            PluginDetailContent.Children.Clear();
            PluginDetailContent.Children.Add(host.Content);
            PluginsListView.Visibility = Visibility.Collapsed;
            PluginDetailView.Visibility = Visibility.Visible;
            UpdatePluginPages();
        }
        catch (Exception ex)
        {
            AppendDebugLog("[插件] 打开插件页面失败：" + ex.Message);
        }
    }

    /// <summary>返回插件列表。</summary>
    private void ShowPluginList()
    {
        try
        {
            _openPluginId = null;
            PluginDetailContent.Children.Clear();
            PluginDetailView.Visibility = Visibility.Collapsed;
            PluginMarketView.Visibility = Visibility.Collapsed;
            PluginsListView.Visibility = Visibility.Visible;
        }
        catch
        {
            // 切不回来也不该崩
        }
    }

    private void PluginBackButton_Click(object sender, RoutedEventArgs e) => ShowPluginList();


    private PluginPageHost CreatePluginPageHost(PluginEntry entry, PluginPage page)
    {
        var host = new PluginPageHost
        {
            Entry = entry,
            Page = page,
            Content = new StackPanel { Margin = new Thickness(0, 0, 0, 8) },
        };

        var stack = host.Content;

        // 页头
        var header = new StackPanel();
        header.Children.Add(PluginText(page.Title, "PanelTitleStyle"));
        if (!string.IsNullOrWhiteSpace(page.Description))
        {
            header.Children.Add(PluginText(page.Description!, "PanelDescStyle", wrap: true));
        }

        stack.Children.Add(header);

        // 状态行（插件每秒给一次文字）。
        // 插件没给 StatusText 就整行不建 —— 有些插件的状态在页面里已经表达清楚了，
        // 页头下面再挂一行文字反而碍眼（自动 GG 就是这种）。
        if (page.StatusText is not null)
        {
            var status = PluginText("", "HintStyle", wrap: true);
            status.Margin = new Thickness(0, 8, 0, 0);
            host.StatusText = status;
            stack.Children.Add(status);
        }

        System.Windows.FrameworkElement? customContent = null;
        if (page.ContentFactory is not null)
        {
            try
            {
                customContent = page.ContentFactory() as System.Windows.FrameworkElement;
            }
            catch (Exception ex)
            {
                AppendDebugLog($"[插件] {entry.Manifest.Id} 自绘页面创建失败：{ex.Message}");
                customContent = null;
            }
        }

        if (customContent is not null)
        {
            stack.Children.Add(customContent);
            AppendDebugLog($"[插件] 已注册页面「{page.Title}」（{entry.Manifest.Id}·自绘）");
            return host;
        }

        if (page.Fields.Count > 0)
        {
            stack.Children.Add(BuildFieldsCard(host));
        }

        if (page.Actions.Count > 0)
        {
            stack.Children.Add(BuildActionsCard(host));
        }

        AppendDebugLog($"[插件] 已注册页面「{page.Title}」（{entry.Manifest.Id}）");
        return host;
    }

    private Border BuildFieldsCard(PluginPageHost host)
    {
        var card = new Border { Margin = new Thickness(0, 14, 0, 0) };
        card.SetResourceReference(Border.StyleProperty, "CardStyle");

        var stack = new StackPanel();
        stack.Children.Add(PluginText("设置", "CardTitleStyle"));

        foreach (var field in host.Page.Fields)
        {
            try
            {
                var element = BuildField(host, field);
                if (element is not null)
                {
                    stack.Children.Add(element);
                }
            }
            catch (Exception ex)
            {
                AppendDebugLog($"[插件] 字段「{field.Label}」创建失败：{ex.Message}");
            }
        }

        card.Child = stack;
        return card;
    }

    private Border BuildActionsCard(PluginPageHost host)
    {
        var card = new Border { Margin = new Thickness(0, 14, 0, 0) };
        card.SetResourceReference(Border.StyleProperty, "CardStyle");

        var stack = new StackPanel();
        stack.Children.Add(PluginText("操作", "CardTitleStyle"));

        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var action in host.Page.Actions)
        {
            var button = new Button
            {
                Content = action.Label,
                Margin = new Thickness(0, 0, 8, 8),
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, action.Primary ? "SmallPrimaryButtonStyle" : "SmallButtonStyle");
            button.Click += (_, _) => RunPluginAction(host, action);
            wrap.Children.Add(button);
            host.Actions.Add(new PluginActionBinding { Action = action, Element = button });
        }

        stack.Children.Add(wrap);
        card.Child = stack;
        return card;
    }
    // ------------------------------------------------------------------ 字段渲染

    private FrameworkElement? BuildField(PluginPageHost host, PluginField field)
    {
        switch (field.Kind)
        {
            case PluginFieldKind.Toggle:
            {
                var inner = new StackPanel();
                inner.Children.Add(new TextBlock { Text = field.Label, FontSize = 13, FontWeight = FontWeights.Bold });
                if (!string.IsNullOrWhiteSpace(field.Hint))
                {
                    inner.Children.Add(PluginSecondary(field.Hint!, 11.5));
                }

                var check = new CheckBox
                {
                    Content = inner,
                    IsChecked = field.Bool,
                    Margin = new Thickness(0, 10, 0, 0),
                };
                check.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitchStyle");
                check.Checked += (_, _) =>
                {
                    field.Bool = true;
                    OnPluginFieldChanged(host, field);
                };
                check.Unchecked += (_, _) =>
                {
                    field.Bool = false;
                    OnPluginFieldChanged(host, field);
                };

                host.Fields.Add(new PluginFieldBinding { Field = field, Element = check });
                return check;
            }

            case PluginFieldKind.Slider:
            {
                var box = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

                var labelRow = new Grid();
                labelRow.Children.Add(new TextBlock { Text = field.Label, FontSize = 13, FontWeight = FontWeights.Bold });
                var valueText = new TextBlock
                {
                    Text = FormatFieldNumber(field),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                labelRow.Children.Add(valueText);

                var slider = new Slider
                {
                    Minimum = field.Minimum,
                    Maximum = field.Maximum,
                    Value = Math.Clamp(field.Number, field.Minimum, field.Maximum),
                    TickFrequency = Math.Max(0.0001, field.Step),
                    IsSnapToTickEnabled = true,
                    SmallChange = field.Step,
                    LargeChange = field.Step * 5,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                slider.ValueChanged += (_, e) =>
                {
                    field.Number = e.NewValue;
                    valueText.Text = FormatFieldNumber(field);
                    OnPluginFieldChanged(host, field);
                };

                box.Children.Add(labelRow);
                box.Children.Add(slider);
                if (!string.IsNullOrWhiteSpace(field.Hint))
                {
                    box.Children.Add(PluginSecondary(field.Hint!, 11.5));
                }

                host.Fields.Add(new PluginFieldBinding
                {
                    Field = field,
                    Element = box,
                    Refresh = () => valueText.Text = FormatFieldNumber(field),
                });
                return box;
            }

            case PluginFieldKind.Select:
            {
                var box = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                box.Children.Add(new TextBlock { Text = field.Label, FontSize = 13, FontWeight = FontWeights.Bold });

                var combo = new ComboBox
                {
                    DisplayMemberPath = "Text",
                    SelectedValuePath = "Key",
                    MinWidth = 220,
                    Margin = new Thickness(0, 6, 0, 0),
                };

                void Reload()
                {
                    try
                    {
                        var options = field.OptionsProvider?.Invoke() ?? new List<PluginOption>();
                        combo.ItemsSource = options;
                        if (field.SelectedKey is not null
                            && options.Any(o => string.Equals(o.Key, field.SelectedKey, StringComparison.Ordinal)))
                        {
                            combo.SelectedValue = field.SelectedKey;
                        }
                        else
                        {
                            combo.SelectedIndex = -1;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendDebugLog($"[插件] 下拉框「{field.Label}」取候选项失败：{ex.Message}");
                    }
                }

                Reload();
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is PluginOption option)
                    {
                        field.SelectedKey = option.Key;
                        OnPluginFieldChanged(host, field);
                    }
                };

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(combo);

                if (field.OptionsProvider is not null)
                {
                    var refresh = new Button
                    {
                        Content = "刷新",
                        Margin = new Thickness(8, 6, 0, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    refresh.SetResourceReference(FrameworkElement.StyleProperty, "SmallGhostButtonStyle");
                    refresh.Click += (_, _) => Reload();
                    Grid.SetColumn(refresh, 1);
                    row.Children.Add(refresh);
                }

                box.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(field.Hint))
                {
                    box.Children.Add(PluginSecondary(field.Hint!, 11.5));
                }

                host.Fields.Add(new PluginFieldBinding
                {
                    Field = field,
                    Element = box,
                    Refresh = Reload,
                    NextOptionsRefresh = DateTime.UtcNow.AddSeconds(Math.Max(1, field.OptionsRefreshSeconds)),
                });
                return box;
            }

            case PluginFieldKind.Text:
            {
                var box = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
                box.Children.Add(new TextBlock { Text = field.Label, FontSize = 13, FontWeight = FontWeights.Bold });

                var textBox = new TextBox { Text = field.Text, Margin = new Thickness(0, 6, 0, 0) };
                textBox.LostFocus += (_, _) =>
                {
                    field.Text = textBox.Text;
                    OnPluginFieldChanged(host, field);
                };

                box.Children.Add(textBox);
                if (!string.IsNullOrWhiteSpace(field.Hint))
                {
                    box.Children.Add(PluginSecondary(field.Hint!, 11.5));
                }

                host.Fields.Add(new PluginFieldBinding { Field = field, Element = box });
                return box;
            }

            default:
            {
                var text = PluginSecondary(field.Text, 12);
                text.Margin = new Thickness(0, 8, 0, 0);
                host.Fields.Add(new PluginFieldBinding
                {
                    Field = field,
                    Element = text,
                    Refresh = () => text.Text = field.Text,
                });
                return text;
            }
        }
    }

    /// <summary>字段值变了：通知插件，然后马上刷一次页面（状态行跟着更新）。</summary>
    private void OnPluginFieldChanged(PluginPageHost host, PluginField field)
    {
        try
        {
            field.Changed?.Invoke(field);
        }
        catch (Exception ex)
        {
            AppendDebugLog($"[插件 {host.Entry.Manifest.Id}] 字段「{field.Label}」回调出错：{ex.Message}");
        }

        UpdatePluginPages();
    }

    private void RunPluginAction(PluginPageHost host, PluginAction action)
    {
        try
        {
            var message = action.Invoke?.Invoke();
            if (!string.IsNullOrWhiteSpace(message))
            {
                ShowToast(message!);
            }
        }
        catch (Exception ex)
        {
            AppendDebugLog($"[插件 {host.Entry.Manifest.Id}] 操作「{action.Label}」出错：{ex.GetType().Name}: {ex.Message}");
            ShowToast("插件操作失败：" + ex.Message);
        }

        UpdatePluginPages();
    }

    /// <summary>每秒一次：状态行、字段可用性、下拉项自动刷新。</summary>
    private void UpdatePluginPages()
    {
        foreach (var host in _pluginPageHosts)
        {
            if (host.StatusText is not null)
            {
                try
                {
                    host.StatusText.Text = host.Page.StatusText?.Invoke() ?? "";
                }
                catch (Exception ex)
                {
                    host.StatusText.Text = "插件状态取不到：" + ex.Message;
                }
            }

            foreach (var binding in host.Fields)
            {
                try
                {
                    if (binding.Element is not null)
                    {
                        binding.Element.IsEnabled = binding.Field.Enabled;
                    }

                    if (binding.Field.Kind == PluginFieldKind.ReadOnly)
                    {
                        binding.Refresh?.Invoke();
                        continue;
                    }

                    if (binding.Field.Kind == PluginFieldKind.Select
                        && binding.Field.OptionsProvider is not null
                        && binding.Field.OptionsRefreshSeconds > 0
                        && DateTime.UtcNow >= binding.NextOptionsRefresh)
                    {
                        binding.NextOptionsRefresh = DateTime.UtcNow.AddSeconds(binding.Field.OptionsRefreshSeconds);
                        binding.Refresh?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    AppendDebugLog($"[插件] 刷新字段「{binding.Field.Label}」出错：{ex.Message}");
                }
            }

            foreach (var action in host.Actions)
            {
                if (action.Element is not null)
                {
                    action.Element.IsEnabled = action.Action.Enabled;
                }
            }
        }
    }

    // ------------------------------------------------------------------ 插件管理页

    private void RefreshPluginsManagerList()
    {
        if (_pluginManager is null)
        {
            return;
        }

        try
        {
            PluginsListPanel.Children.Clear();
            var entries = _pluginManager.Entries;
            PluginsEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var entry in entries)
            {
                PluginsListPanel.Children.Add(BuildPluginRow(entry));
            }
        }
        catch (Exception ex)
        {
            AppendDebugLog("[插件] 刷新插件列表出错：" + ex.Message);
        }
    }

    /// <summary>
    /// 插件卡片：名字 / 版本 · 作者 / 简介 + 右侧启用开关与【卸载】。
    /// 点卡片进它的页面；点开关 / 【卸载】各做各的事，不会顺手把插件页面也打开。
    /// 「打开目录」这类低频操作还在右键菜单里。
    /// </summary>
    private Border BuildPluginRow(PluginEntry entry)
    {
        // 外观（底色/描边/圆角/内边距/外边距/鼠标指针）+ 悬停反馈（阴影加深、描边变亮、上浮 2px）
        // 全部交给 PluginCardStyle —— 和大卡片 CardStyle 同一套悬停语言，改一处两边都跟着动
        var border = new Border
        {
            ToolTip = "点卡片进入插件页面 · 右键更多操作",
        };
        border.SetResourceReference(FrameworkElement.StyleProperty, "PluginCardStyle");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- 标题行：插件名（粗） + 版本号 · 作者（次要色）----
        var title = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.Inlines.Add(new Run(entry.DisplayName) { FontSize = 14.5, FontWeight = FontWeights.Bold });

        var metaParts = new List<string>
        {
            string.IsNullOrWhiteSpace(entry.Manifest.Version) ? "v—" : "v" + entry.Manifest.Version,
            string.IsNullOrWhiteSpace(entry.Manifest.Author) ? "（未署名）" : entry.Manifest.Author,
        };
        var metaRun = new Run("  " + string.Join(" · ", metaParts)) { FontSize = 11.5 };
        metaRun.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
        title.Inlines.Add(metaRun);
        grid.Children.Add(title);

        // ---- 简介：最多两行，超出显示省略号 ----
        var body = new StackPanel { Margin = new Thickness(0, 7, 0, 0), VerticalAlignment = VerticalAlignment.Top };

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Manifest.Description) ? "（作者没写简介）" : entry.Manifest.Description!,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 36,
        };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        body.Children.Add(desc);

        // 装载失败的插件：开关点了也没用，得让人看见原因
        if (entry.State == PluginState.Failed && !string.IsNullOrWhiteSpace(entry.Error))
        {
            var error = new TextBlock
            {
                Text = "装载失败：" + entry.Error,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            };
            error.SetResourceReference(TextBlock.ForegroundProperty, "AmberBrush");
            body.Children.Add(error);
        }

        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        // ---- 右侧竖排：上面是启用开关，下面是【卸载】----
        //
        // 卸载原来收在右键菜单里 —— 功能是有的，但没人知道它在那儿，等于没有。
        // 现在直接摆在卡片上；两个控件共用一个 StackPanel，右列只占一列宽，
        // 卡片高度也不会因为多出来一个按钮而变形。
        var side = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        // ---- 开关：开 = 启用，关 = 禁用 ----
        var toggle = new CheckBox
        {
            IsChecked = entry.State == PluginState.Loaded,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = entry.State == PluginState.Loaded ? "点一下禁用这个插件" : "点一下启用这个插件",
        };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "InlineToggleStyle");
        toggle.Checked += (_, _) => TogglePluginEnabled(entry, true);
        toggle.Unchecked += (_, _) => TogglePluginEnabled(entry, false);
        side.Children.Add(toggle);

        // ---- 卸载：和详情页头部那个按钮走同一条路（同一个确认框）----
        var uninstall = new Button
        {
            Content = "卸载",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "把这个插件从本机删掉：插件目录和它自己的配置目录会一起删",
        };
        uninstall.SetResourceReference(FrameworkElement.StyleProperty, "SmallDangerButtonStyle");
        uninstall.Click += (_, _) => UninstallPlugin(entry);
        side.Children.Add(uninstall);

        Grid.SetColumn(side, 1);
        Grid.SetRowSpan(side, 2);
        grid.Children.Add(side);

        border.Child = grid;
        border.ContextMenu = BuildPluginCardMenu(entry);
        border.MouseLeftButtonUp += (_, e) =>
        {
            // 点开关不该把插件页面也一起打开
            if (IsInsideInteractiveChild(e.OriginalSource))
            {
                return;
            }

            OpenPluginPage(entry);
        };
        return border;
    }

    /// <summary>卡片右上角的开关：开 = 重新装载并启用，关 = 禁用。</summary>
    private void TogglePluginEnabled(PluginEntry entry, bool enabled)
    {
        if (_pluginManager is null)
        {
            return;
        }

        // 装载失败的插件在界面上显示为"关"，用户点一下就是想再试一次 —— 所以只有状态真的变了才动手
        if ((entry.State == PluginState.Loaded) == enabled)
        {
            return;
        }

        _pluginManager.SetEnabled(entry, enabled, out var message);
        SavePluginsDisabledState();
        ShowToast(message);
        RefreshPluginsManagerList();
    }

    /// <summary>右键菜单：卡片上放不下的管理操作都收在这里。</summary>
    private ContextMenu BuildPluginCardMenu(PluginEntry entry)
    {
        var menu = new ContextMenu();

        var openPage = new MenuItem { Header = "进入插件页面" };
        openPage.Click += (_, _) => OpenPluginPage(entry);
        menu.Items.Add(openPage);

        var openFolder = new MenuItem { Header = "打开这个插件的目录" };
        openFolder.Click += (_, _) => OpenFolder(entry.Directory);
        menu.Items.Add(openFolder);

        menu.Items.Add(new Separator());

        var uninstall = new MenuItem { Header = "卸载这个插件" };
        uninstall.Click += (_, _) => UninstallPlugin(entry);
        menu.Items.Add(uninstall);

        return menu;
    }

    /// <summary>卸载插件（带确认框）。卡片右侧的【卸载】和详情页头部那个按钮都走这里。</summary>
    private void UninstallPlugin(PluginEntry entry)
    {
        if (_pluginManager is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"确定卸载 {entry.DisplayName} 吗？\n\n插件自己的配置目录会一起删掉。",
            "卸载插件",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _pluginManager.Uninstall(entry, out var message);
        SavePluginsDisabledState();
        // 文件被占用那种情况会明说"要重启一次才彻底删掉"：这种必须用弹窗，
        // 一个飘过去的 toast 用户看不到，回头还得来问"为什么文件夹还在"。
        if (message.Contains("重启"))
        {
            MessageBox.Show(this, message, "卸载插件", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            ShowToast(message);
        }
        RefreshPluginsManagerList();
    }

    /// <summary>
    /// 插件详情页头部的【卸载这个插件】。
    /// 和卡片上那个【卸载】走完全同一条路（同一个确认框、同一个 UninstallPlugin），
    /// 只是给"点进去看过、觉得不合适"的人少一步回头找卡片。
    /// 卸载完 RefreshPluginsManagerList → OnPluginsChanged 发现 _openPluginId 已经不在列表里，
    /// 自己会把详情页退回列表。
    /// </summary>
    private void PluginDetailUninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pluginManager is null || _openPluginId is null)
        {
            return;
        }

        var entry = _pluginManager.Entries.FirstOrDefault(x => x.Manifest.Id == _openPluginId);
        if (entry is null)
        {
            ShowPluginList();
            return;
        }

        UninstallPlugin(entry);
    }

    /// <summary>
    /// 事件源是不是卡片里的开关 / 按钮（沿视觉树上找 <see cref="System.Windows.Controls.Primitives.ButtonBase"/>）。
    ///
    /// 卡片本身点一下要进插件页面，但开关和【卸载】得各管各的 —— 它们自己的 Click 已经在干活了，
    /// 鼠标事件不该再往上冒成"顺手把插件页面也打开"。
    /// ToggleButton 也是 ButtonBase，所以判一个类型就够，不用分两种。
    /// </summary>
    private static bool IsInsideInteractiveChild(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private void PluginImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择插件包",
            Filter = "插件包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            ImportPluginZip(dialog.FileName);
        }
    }

    private void PluginOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(_pluginManager?.UserRoot ?? "");
    }

    private void PluginRescanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pluginManager is null)
        {
            return;
        }

        _pluginManager.LoadAll();
        RebuildPluginPages();
        RefreshPluginsManagerList();
        ShowToast("已重新扫描插件目录");
    }

    private void PluginsPanel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void PluginsPanel_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var file in files.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    ImportPluginZip(file);
                }
            }
        }
        catch (Exception ex)
        {
            ShowToast("拖进来的东西处理不了：" + ex.Message);
        }
    }

    private void ImportPluginZip(string path)
    {
        if (_pluginManager is null)
        {
            return;
        }

        try
        {
            var entry = _pluginManager.ImportZip(path, ConfirmPluginInstall, out var message);
            ShowToast(message);
            SavePluginsDisabledState();
            RebuildPluginPages();
            RefreshPluginsManagerList();

            if (entry is not null)
            {
                AppendDebugLog($"[插件] 导入 {entry.Manifest.Id} → {entry.State}（{entry.Directory}）");
            }
        }
        catch (Exception ex)
        {
            ShowToast("导入失败：" + ex.Message);
        }
    }

    /// <summary>安装确认框：把作者写的能力列出来，让用户知情。</summary>
    private bool ConfirmPluginInstall(PluginManifest manifest)
    {
        var capabilities = manifest.Capabilities.Count == 0
            ? "（作者没写能力说明）"
            : string.Join("、", manifest.Capabilities);

        var text =
            $"{manifest.Describe()}\n\n{manifest.Description}\n\n它会做的事：{capabilities}\n\n" +
            "插件跑在本程序里，权限和本程序一样。只安装信任来源的插件包。\n\n要安装吗？";

        return MessageBox.Show(this, text, "安装插件", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            == MessageBoxResult.OK;
    }

    private void SavePluginsDisabledState()
    {
        try
        {
            if (_pluginManager is null)
            {
                return;
            }

            _settings.DisabledPlugins = _pluginManager.DisabledIds.ToList();
            SettingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            AppendDebugLog("[插件] 保存禁用列表失败：" + ex.Message);
        }
    }

    /// <summary>在线插件开发文档（打开系统默认浏览器）。</summary>
    private void PluginDevDocButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // UseShellExecute 才会交给系统用默认浏览器打开
            Process.Start(new ProcessStartInfo(PluginDevDocUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast("打不开文档：" + ex.Message);
            AppendDebugLog("[插件] 打开开发文档失败：" + ex.Message);
        }
    }

    /// <summary>插件开发文档地址（GitHub Pages）。</summary>
    private const string PluginDevDocUrl = "https://goldiamond1031.github.io/MCO";

    private void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast("打不开目录：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 小工具

    private TextBlock PluginText(string text, string styleKey, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        block.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return block;
    }

    private TextBlock PluginSecondary(string text, double fontSize)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return block;
    }

    private static string FormatFieldNumber(PluginField field)
        => field.Number.ToString(field.NumberFormat, CultureInfo.InvariantCulture) + field.Suffix;
}
