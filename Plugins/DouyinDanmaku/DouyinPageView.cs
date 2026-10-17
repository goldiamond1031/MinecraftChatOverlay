using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MinecraftChatOverlay.Plugin;

namespace DouyinDanmaku;

/// <summary>
/// 插件设置页的自绘视图。
///
/// 为什么要自绘：宿主把「操作」卡片固定渲染在「设置」卡片之后
/// （宿主 MainWindow.Plugins.xaml.cs:331-339），字段和按钮之间插不进东西，
/// 连接按钮永远落在整页最底下。只有让 PluginPage.ContentFactory 返回非 null 的
/// FrameworkElement，宿主才会整页只用我们自己的内容（同文件 :324-329 直接 return），
/// 连接区这样才放得到页面最上面。
///
/// 外观一律取宿主的 DynamicResource 键（CardStyle / CardTitleStyle / CardDescStyle /
/// ToggleSwitchStyle / SmallPrimaryButtonStyle / HintStyle / TextSecondaryBrush），
/// 跟着宿主的主题走，不自己写死颜色。
///
/// 用纯 C# 建控件、不写 XAML：XAML 编译出的 BAML 要在插件那个可卸载
/// AssemblyLoadContext 里按程序集名回查资源，容易踩坑；宿主自己渲染字段
/// （同文件 BuildField :401）也是纯 C# 建的，风格上更一致。
/// </summary>
internal sealed class DouyinPageView : StackPanel
{
    private static readonly PluginOption[] FilterOptions =
    {
        new("disabled", "不过滤"),
        new("blacklist", "黑名单（命中丢弃）"),
        new("whitelist", "白名单（不命中丢弃）"),
    };

    private readonly TextBox _roomIdBox = new() { Width = 300 };
    private readonly Button _connectButton = new();
    private readonly CheckBox _autoConnectCheck = new();

    private readonly CheckBox _chatCheck = new();
    private readonly CheckBox _likeCheck = new();
    private readonly CheckBox _giftCheck = new();
    private readonly CheckBox _memberCheck = new();
    private readonly CheckBox _roomStatsCheck = new();
    private readonly CheckBox _fansclubCheck = new();

    private readonly TextBox _chatTemplateBox = new();
    private readonly TextBox _likeTemplateBox = new();
    private readonly TextBox _giftTemplateBox = new();
    private readonly TextBox _memberTemplateBox = new();
    private readonly TextBox _roomStatsTemplateBox = new();
    private readonly TextBox _fansclubTemplateBox = new();

    private readonly ComboBox _filterCombo = new() { Width = 300, Height = 32 };
    private readonly TextBox _keywordsBox = new();

    /// <summary>程序回填控件值时会触发 Changed，用这道闸子压住，否则会把配置覆盖回默认值。</summary>
    private bool _loading;

    /// <summary>任意设置项被用户改动。</summary>
    public event Action? SettingsChanged;

    /// <summary>点了「连接 / 断开」。</summary>
    public event Action? ConnectRequested;

    public DouyinPageView()
    {
        // 卡片顺序 = 从上到下的视线顺序。连接区必须在第一张卡片。
        Children.Add(BuildConnectCard());
        Children.Add(BuildKindsCard());
        Children.Add(BuildTemplatesCard());
        Children.Add(BuildFilterCard());
        Children.Add(BuildCreditsCard());
    }

    // ---------- 对外接口 ----------

    /// <summary>把配置灌进控件。期间的事件被闸门压住。</summary>
    public void Bind(Settings s)
    {
        _loading = true;
        try
        {
            _roomIdBox.Text = s.WebRoomId ?? "";
            _autoConnectCheck.IsChecked = s.AutoConnect;

            _chatCheck.IsChecked = s.ShowChat;
            _likeCheck.IsChecked = s.ShowLike;
            _giftCheck.IsChecked = s.ShowGift;
            _memberCheck.IsChecked = s.ShowMember;
            _roomStatsCheck.IsChecked = s.ShowRoomStats;
            _fansclubCheck.IsChecked = s.ShowFansclub;

            _chatTemplateBox.Text = s.ChatTemplate ?? "";
            _likeTemplateBox.Text = s.LikeTemplate ?? "";
            _giftTemplateBox.Text = s.GiftTemplate ?? "";
            _memberTemplateBox.Text = s.MemberTemplate ?? "";
            _roomStatsTemplateBox.Text = s.RoomStatsTemplate ?? "";
            _fansclubTemplateBox.Text = s.FansclubTemplate ?? "";

            _filterCombo.ItemsSource = FilterOptions;
            _filterCombo.SelectedValue = s.FilterMode ?? "disabled";
            if (_filterCombo.SelectedIndex < 0)
            {
                _filterCombo.SelectedValue = "disabled";
            }

            _keywordsBox.Text = s.Keywords ?? "";
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>把控件里的值写回配置对象。</summary>
    public void ReadInto(Settings s)
    {
        s.WebRoomId = _roomIdBox.Text ?? "";
        s.AutoConnect = _autoConnectCheck.IsChecked == true;

        s.ShowChat = _chatCheck.IsChecked == true;
        s.ShowLike = _likeCheck.IsChecked == true;
        s.ShowGift = _giftCheck.IsChecked == true;
        s.ShowMember = _memberCheck.IsChecked == true;
        s.ShowRoomStats = _roomStatsCheck.IsChecked == true;
        s.ShowFansclub = _fansclubCheck.IsChecked == true;

        s.ChatTemplate = _chatTemplateBox.Text ?? "";
        s.LikeTemplate = _likeTemplateBox.Text ?? "";
        s.GiftTemplate = _giftTemplateBox.Text ?? "";
        s.MemberTemplate = _memberTemplateBox.Text ?? "";
        s.RoomStatsTemplate = _roomStatsTemplateBox.Text ?? "";
        s.FansclubTemplate = _fansclubTemplateBox.Text ?? "";

        s.FilterMode = _filterCombo.SelectedValue as string ?? "disabled";
        s.Keywords = _keywordsBox.Text ?? "";
    }

    /// <summary>按连接状态刷按钮与输入框。必须在 UI 线程调用。</summary>
    /// <param name="busyText">忙的时候按钮上显示什么（「连接中…」/「断开中…」/「取消中…」）。</param>
    /// <remarks>
    /// 忙的时候按钮**仍然可点**：连接最坏要跑 4 个端点 × 12 秒，把按钮禁用掉
    /// 等于让用户干等一分钟还退不出来。此时这一下点击就是「取消」。
    /// </remarks>
    public void SetState(bool connected, bool busy, string busyText = "连接中…")
    {
        _connectButton.Content = busy ? busyText : connected ? "断开" : "连接";
        _connectButton.IsEnabled = true;
        _roomIdBox.IsEnabled = !connected && !busy;
    }

    // ---------- 卡片 ----------

    private Border BuildConnectCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(Title("连接"));
        stack.Children.Add(Desc("填 live.douyin.com/ 后面那串数字（要正在直播的房间），点「连接」开始接收。"));

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
        };

        _roomIdBox.VerticalAlignment = VerticalAlignment.Center;
        _roomIdBox.LostFocus += (_, _) => RaiseChanged();
        _roomIdBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                RaiseConnect();
            }
        };
        row.Children.Add(_roomIdBox);

        _connectButton.Content = "连接";
        _connectButton.Margin = new Thickness(10, 0, 0, 0);
        _connectButton.VerticalAlignment = VerticalAlignment.Center;
        _connectButton.SetResourceReference(FrameworkElement.StyleProperty, "SmallPrimaryButtonStyle");
        _connectButton.Click += (_, _) => RaiseConnect();
        row.Children.Add(_connectButton);

        stack.Children.Add(row);
        stack.Children.Add(Secondary("在输入框里按回车也能连。", 11.5));

        stack.Children.Add(MakeToggle(_autoConnectCheck, "启动时自动连接", "MCO 下次启动时如果这里填着直播间号，就自己连上"));
        return Card(stack);
    }

    private Border BuildKindsCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(Title("消息类别"));
        stack.Children.Add(Desc("挑要转发到聊天悬浮窗的消息。关掉的类别直接丢弃，不占悬浮窗位置。"));

        stack.Children.Add(MakeToggle(_chatCheck, "弹幕", "观众发的文字消息"));
        stack.Children.Add(MakeToggle(_likeCheck, "点赞", "有人点赞时提示；同一人连续点赞会被抖音合并计数"));
        stack.Children.Add(MakeToggle(_giftCheck, "礼物", "送出礼物时提示，带礼物名和连击数"));
        stack.Children.Add(MakeToggle(_memberCheck, "入场", "有人进直播间时提示"));
        stack.Children.Add(MakeToggle(_roomStatsCheck, "房间统计", "当前在线人数，只在人数变化时才转发一次（默认关，变化频繁容易刷屏）"));
        stack.Children.Add(MakeToggle(_fansclubCheck, "粉丝团", "粉丝团成员的弹幕，模板里可以带上粉丝团等级"));
        return Card(stack);
    }

    private Border BuildTemplatesCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(Title("输出模板"));
        stack.Children.Add(Desc("发到悬浮窗的文字长什么样。${...} 是变量，渲染时会替换成真实内容，认不出来的变量会被清掉。"));
        stack.Children.Add(Secondary(
            "常用变量：${nickname} 昵称、${content} 文字内容、${giftName} 礼物名、${giftCombo} 连击数、"
            + "${count} 数量、${totalStr} 在线人数、${totalPvForAnchor} 累计观看、${fansClubLevel} 粉丝团等级。",
            11.5));

        AddTemplate(stack, "弹幕模板", _chatTemplateBox);
        AddTemplate(stack, "点赞模板", _likeTemplateBox);
        AddTemplate(stack, "礼物模板", _giftTemplateBox);
        AddTemplate(stack, "入场模板", _memberTemplateBox);
        AddTemplate(stack, "统计模板", _roomStatsTemplateBox);
        AddTemplate(stack, "粉丝团模板", _fansclubTemplateBox);
        return Card(stack);
    }

    private Border BuildFilterCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(Title("关键词过滤"));
        stack.Children.Add(Desc("按关键词丢掉一部分弹幕。留空表示不过滤。"));

        var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(new TextBlock { Text = "过滤模式", FontSize = 13, FontWeight = FontWeights.Bold });
        _filterCombo.DisplayMemberPath = "Text";
        _filterCombo.SelectedValuePath = "Key";
        _filterCombo.Margin = new Thickness(0, 6, 0, 0);
        _filterCombo.SelectionChanged += (_, _) => RaiseChanged();
        row.Children.Add(_filterCombo);
        stack.Children.Add(row);

        var keywordRow = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        keywordRow.Children.Add(new TextBlock { Text = "关键词", FontSize = 13, FontWeight = FontWeights.Bold });
        _keywordsBox.Margin = new Thickness(0, 6, 0, 0);
        _keywordsBox.LostFocus += (_, _) => RaiseChanged();
        keywordRow.Children.Add(_keywordsBox);
        keywordRow.Children.Add(Secondary("逗号分隔，不区分大小写。黑名单命中就丢弃，白名单不命中才丢弃。", 11.5));
        stack.Children.Add(keywordRow);

        return Card(stack);
    }

    // ---------- 建控件的零件（照抄宿主 BuildField 的风格） ----------

    private void AddTemplate(StackPanel parent, string label, TextBox box)
    {
        var row = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.Bold });
        box.Margin = new Thickness(0, 6, 0, 0);
        box.LostFocus += (_, _) => RaiseChanged();
        row.Children.Add(box);
        parent.Children.Add(row);
    }

    private CheckBox MakeToggle(CheckBox box, string label, string? hint)
    {
        var inner = new StackPanel();
        inner.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.Bold });
        if (!string.IsNullOrWhiteSpace(hint))
        {
            inner.Children.Add(Secondary(hint!, 11.5));
        }

        box.Content = inner;
        box.Margin = new Thickness(0, 14, 0, 0);
        box.SetResourceReference(FrameworkElement.StyleProperty, "ToggleSwitchStyle");
        box.Checked += (_, _) => RaiseChanged();
        box.Unchecked += (_, _) => RaiseChanged();
        return box;
    }

    private static Border Card(UIElement child)
    {
        var card = new Border { Margin = new Thickness(0, 14, 0, 0) };
        card.SetResourceReference(FrameworkElement.StyleProperty, "CardStyle");
        card.Child = child;
        return card;
    }

    private static TextBlock Title(string text)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(FrameworkElement.StyleProperty, "CardTitleStyle");
        return block;
    }

    private static TextBlock Desc(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        };
        block.SetResourceReference(FrameworkElement.StyleProperty, "CardDescStyle");
        return block;
    }

    private static TextBlock Secondary(string text, double fontSize)
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

    // ---------- 来源与致谢 ----------

    /// <summary>
    /// 署名卡片。协议层不是凭空想出来的——签名算法、握手参数、端点清单
    /// 都来自下面这些开源实现，这里如实列出来。
    ///
    /// 用纯文本而不是 Hyperlink：宿主没给插件暴露"打开浏览器"的接口，
    /// 做成可点却打不开反而更糟，直接给可复制的 URL 更实在。
    /// </summary>
    private Border BuildCreditsCard()
    {
        var stack = new StackPanel();
        stack.Children.Add(Title("来源与致谢"));
        stack.Children.Add(Desc(
            "本插件的抖音协议层不是逆向出来的原创成果，而是照着下面几个开源项目的实现重写成 C# 的。"
            + "算法与参数归他们，C# 移植和 MCO 适配归本插件。"));

        // 参考实现：一条一行，仓库名加粗、URL 换行可复制
        stack.Children.Add(Secondary("参考实现（协议流程与签名算法）", 12.5));
        stack.Children.Add(CreditEntry(
            "tiangalon/DyDanmaku",
            "Java + Nashorn 执行 webmssdk 签名。本插件的签名流程、浏览器环境桩写法、"
            + "六类消息的字段语义与模板变量命名都以它为准；插件内置的两个签名 JS "
            + "（runtime~client-entry.44b556b4.js、webmssdk.es5.js）也直接取自该仓库的 src/main/resources。"
            + "该仓库为「保留所有权利」（LICENSE 仅一句 All rights reserved），此处仅作非商业性兼容实现参考并注明出处。",
            "https://github.com/tiangalon/DyDanmaku"));
        stack.Children.Add(CreditEntry(
            "skmcj/dycast（抖音弹幕姬）",
            "TypeScript 实现。签名 base_string 模板与 byted_acrawler.frontierSign 入口、"
            + "推流端点、心跳与 ACK 时序，均与它交叉核对过。",
            "https://github.com/skmcj/dycast"));
        stack.Children.Add(CreditEntry(
            "saermart/DouyinLiveWebFetcher",
            "Python 实现。WebSocket 握手参数（cursor / internal_ext / 各 query 字段）的取值方式参照了它。"
            + "该项目为 AGPL-3.0：本插件未复制其代码，仅按其行为规格独立实现 C# 版本。",
            "https://github.com/saermart/DouyinLiveWebFetcher"));
        stack.Children.Add(CreditEntry(
            "zhonghangAlex/DySpider",
            "Python 实现。RENDER_DATA 解析与短号/抖音号形态的兼容思路来自它。",
            "https://github.com/zhonghangAlex/DySpider"));
        stack.Children.Add(CreditEntry(
            "XiaoMiku01/douyin-live-go",
            "Go 实现。protobuf 消息的字段名清单与它核对过（GPL-3.0，同样未复制代码）。",
            "https://github.com/XiaoMiku01/douyin-live-go"));

        stack.Children.Add(Secondary("第三方组件", 12.5));
        stack.Children.Add(CreditEntry(
            "Jint 4.17.0（BSD-2-Clause）",
            "纯托管的 JavaScript 引擎，用来在进程内执行抖音的 webmssdk 算 X-Bogus 签名。"
            + "没有它就只能依赖 WebView2 或 Node 子进程，那样插件就不再是「装上就能用」。",
            "https://github.com/sebastienros/jint"));
        stack.Children.Add(CreditEntry(
            "Acornima 1.7.0（BSD-3-Clause）",
            "Jint 的 JavaScript 解析器依赖，随插件一起分发。",
            "https://github.com/adams85/acornima"));

        stack.Children.Add(Secondary("插件契约", 12.5));
        stack.Children.Add(CreditEntry(
            "MinecraftChatOverlay（goldiamond1031）",
            "本插件运行在 MCO 里。Abstractions\\PluginApi.cs 是照官方插件契约复刻的编译期引用，"
            + "页面样式一律取宿主的 DynamicResource 键，跟随宿主主题。",
            "https://github.com/goldiamond1031/MinecraftChatOverlay"));

        stack.Children.Add(Secondary(
            "签名算法本身是抖音的商业资产，本插件只是调用其公开下发的 webmssdk，"
            + "不主张任何权利，也不对使用后果负责。若相关权利人认为不妥，请联系下架。",
            11.5));

        return Card(stack);
    }

    /// <summary>一条署名：标题行 + 说明 + 可复制的 URL。</summary>
    private static StackPanel CreditEntry(string name, string detail, string url)
    {
        var row = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.Bold });
        row.Children.Add(Secondary(detail, 11.5));

        // 只读文本框：能选中、能 Ctrl+C，比纯 TextBlock 实用，又不会像链接那样点了没反应
        var link = new TextBox
        {
            Text = url,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11.5,
            Cursor = Cursors.IBeam,
        };
        link.SetResourceReference(TextBox.ForegroundProperty, "AccentBrush");
        row.Children.Add(link);

        return row;
    }

    // ---------- 事件 ----------

    private void RaiseChanged()
    {
        if (_loading)
        {
            return;
        }

        SettingsChanged?.Invoke();
    }

    private void RaiseConnect()
    {
        // 用户可能刚敲完房间号就点按钮，先把当前值落一次盘再连
        RaiseChanged();
        ConnectRequested?.Invoke();
    }
}
