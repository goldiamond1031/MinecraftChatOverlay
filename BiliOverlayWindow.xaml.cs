using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Services;
using MinecraftChatOverlay.ViewModels;

namespace MinecraftChatOverlay;

/// <summary>
/// B站弹幕独立悬浮窗。视觉样式沿用 bili-danmaku-overlay-v2 的原样，并补齐三件事：
///   1. 弹幕区 / 通知区各自独立的“不打扰式”滚动（鼠标停在该区看历史时不被新消息抢滚）；
///   2. 两个区域都能用滚轮上下翻看历史，鼠标移开后自动回到最新一条；
///   3. 独立的底边锚定、屏幕内夹取与位置记忆（与本体的聊天悬浮窗互不干扰）。
/// </summary>
public partial class BiliOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;

    private const int MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly BiliSettings _settings;
    private readonly FaceCache _faceCache;
    private readonly ObservableCollection<DanmakuRowViewModel> _danmakus = new();
    private readonly ObservableCollection<DanmakuRowViewModel> _superChats = new();
    private readonly ObservableCollection<NoticeItemViewModel> _gifts = new();
    private readonly ObservableCollection<NoticeItemViewModel> _entries = new();

    /// <summary>鼠标是否停在弹幕区上（停着说明用户在看历史，新弹幕不要抢滚动）。</summary>
    private bool _isMouseOverDanmaku;

    /// <summary>鼠标是否停在 SC 区上。</summary>
    private bool _isMouseOverSc;

    /// <summary>鼠标是否停在礼物区上。</summary>
    private bool _isMouseOverGift;

    /// <summary>鼠标是否停在进场区上。</summary>
    private bool _isMouseOverEntry;

    /// <summary>悬浮窗的停靠底边（DIP）：高度变化时保持底边不动。</summary>
    private double _anchorBottom = double.NaN;

    /// <summary>程序正在自行调整位置时为 true，此时不重算停靠点。</summary>
    private bool _suppressAnchor;

    public BiliOverlayWindow(BiliSettings settings, FaceCache faceCache)
    {
        InitializeComponent();
        _settings = settings;
        _faceCache = faceCache;
        DanmakuList.ItemsSource = _danmakus;
        ScList.ItemsSource = _superChats;
        GiftList.ItemsSource = _gifts;
        EntryList.ItemsSource = _entries;
        ApplySettings();

        if (_settings.OverlayLeft.HasValue)
        {
            Left = _settings.OverlayLeft.Value;
        }

        if (_settings.OverlayTop.HasValue)
        {
            Top = _settings.OverlayTop.Value;
        }

        if (_settings.OverlayAnchorBottom.HasValue)
        {
            _anchorBottom = _settings.OverlayAnchorBottom.Value;
        }

        LocationChanged += OverlayWindow_LocationChanged;
        SizeChanged += OverlayWindow_SizeChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateClickThrough();
        RefreshContentLimit();
        KeepOnScreen();
    }

    public void UpdateClickThrough()
    {
        if (!IsInitialized)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        if (_settings.ClickThrough)
        {
            SetWindowLong(handle, GwlExStyle, style | WsExTransparent);
        }
        else
        {
            SetWindowLong(handle, GwlExStyle, style & ~WsExTransparent);
        }
    }

    public void ApplySettings()
    {
        Width = Math.Clamp(_settings.OverlayWidth, 320, 900);
        RootBorder.Background = BuildBackgroundBrush();
        RootBorder.Opacity = Math.Clamp(_settings.OverlayOpacity, 0.1, 1.0);
        UpdateClickThrough();

        // 字体整窗共用；字号按顶部 / 弹幕区 / 底部三块分别设
        FontFamily = ParseFontFamily(_settings.FontFamily);

        // 顶部文字颜色同时作用于房间标题、主播名和人数
        var headerBrush = ParseBrush(_settings.OnlineColor, Brushes.White);
        RoomTitleText.Foreground = headerBrush;
        OnlineText.Foreground = headerBrush;
        RoomTitleText.Visibility = _settings.ShowRoomTitle ? Visibility.Visible : Visibility.Collapsed;
        OnlineText.Visibility = _settings.ShowOnline ? Visibility.Visible : Visibility.Collapsed;
        var headerFontSize = Math.Clamp(_settings.HeaderFontSize, 8, 30);
        OnlineText.FontSize = headerFontSize;
        RoomTitleText.FontSize = headerFontSize;
        RefreshContentLimit();

        // 保存配置后，对已显示的弹幕/通知也即时生效。
        var textMaxWidth = Math.Clamp(_settings.OverlayWidth, 320, 900) - 80;

        foreach (var row in _danmakus)
        {
            row.DisplayText = DanmakuRowViewModel.WrapText(row.RawText, _settings.DanmakuWrapLength);
            row.TextBrush = ParseBrush(_settings.DanmakuTextColor, Brushes.White);
            row.NameBrush = ParseBrush(_settings.DanmakuTextColor, Brushes.White);
            row.TextShadow = CreateShadow(_settings.DanmakuShadow, _settings.ShadowColor);
            row.NameShadow = CreateShadow(_settings.DanmakuShadow, _settings.ShadowColor);
            row.FontSize = DanmakuFontSize;
            row.NameFontSize = Math.Max(8, DanmakuFontSize * NameFontScale);
            row.TextMaxWidth = textMaxWidth;
        }

        var giftFontSize = Math.Clamp(_settings.GiftFontSize, 8, 30);
        foreach (var gift in _gifts)
        {
            gift.Shadow = CreateShadow(_settings.DanmakuShadow, _settings.ShadowColor);
            gift.FontSize = giftFontSize;
        }

        var entryFontSize = Math.Clamp(_settings.EntryFontSize, 8, 30);
        foreach (var entry in _entries)
        {
            entry.Shadow = CreateShadow(_settings.DanmakuShadow, _settings.ShadowColor);
            entry.FontSize = entryFontSize;
        }

        var scFontSize = Math.Clamp(_settings.SuperChatFontSize, 8, 30);
        foreach (var sc in _superChats)
        {
            sc.FontSize = scFontSize;
            sc.NameFontSize = Math.Max(8, scFontSize * NameFontScale);
            sc.TextShadow = CreateShadow(_settings.DanmakuShadow, _settings.ShadowColor);
            sc.NameShadow = sc.TextShadow;
            sc.TextMaxWidth = textMaxWidth;
        }

        TrimArea(_danmakus, Math.Max(1, _settings.MaxDanmakuLines));
        TrimArea(_superChats, Math.Max(1, _settings.MaxSuperChatLines));
        TrimArea(_gifts, Math.Max(1, _settings.MaxGiftLines));
        TrimArea(_entries, Math.Max(1, _settings.MaxEntryLines));

        UpdateAreaVisibility();
    }

    public void SetRoomTitle(string title, string anchor)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            RoomTitleText.Text = string.IsNullOrWhiteSpace(anchor) ? "B站直播间" : $"{anchor} 的直播间";
        }
        else
        {
            RoomTitleText.Text = string.IsNullOrWhiteSpace(anchor) ? title : $"{title} · {anchor}";
        }
    }

    /// <summary>
    /// 更新顶部数字。
    /// 六个口径见 BiliSettings.OnlineDisplayMode，其中「在线人数」是 B站弹幕流推送的真实值
    /// （仅直连模式可用，拿不到时自动回退到人气）。
    /// </summary>
    /// <param name="realOnline">真实在线人数；&lt;=0 表示当前拿不到。</param>
    public void SetOnline(RoomInfo room, int realOnline = 0)
    {
        if (!room.LiveStatus)
        {
            OnlineText.Text = "未开播";
            return;
        }

        var popularityText = $"人气:{FormatOnline(room.Online)}";
        var watchedText = room.Watched > 0 ? $"看过:{FormatOnline(room.Watched)}" : "";
        var interactionText = room.Interaction > 0 ? $"互动:{FormatOnline(room.Interaction)}" : "";
        var onlineText = realOnline > 0 ? $"在线:{FormatOnline(realOnline)}" : "";

        OnlineText.Text = _settings.OnlineDisplayMode switch
        {
            0 => popularityText,
            1 => watchedText.Length > 0 ? watchedText : popularityText,
            2 => interactionText.Length > 0 ? interactionText : popularityText,
            3 => watchedText.Length > 0 ? $"{watchedText} · {popularityText}" : popularityText,
            4 => onlineText.Length > 0 ? onlineText : popularityText,
            5 => onlineText.Length > 0 ? $"{onlineText} · {popularityText}" : popularityText,
            _ => popularityText
        };
    }

    private static string FormatOnline(int value) =>
        value >= 10000 ? $"{value / 10000.0:0.#}万" : value.ToString();

    public void AddDanmaku(DanmakuItem item)
    {
        var defaultTextBrush = ParseBrush(_settings.DanmakuTextColor, Brushes.White);

        // 「显示弹幕原色」开着时优先用弹幕自带颜色（B站彩色弹幕），关掉就统一用配置色。
        // 只影响弹幕区；SC / 礼物 / 舰长等走通知区，不受这个开关影响。
        Brush textBrush = _settings.ShowDanmakuOriginalColor
            ? ParseBrush(item.DanmakuColor, defaultTextBrush)
            : defaultTextBrush;
        var nameBrush = defaultTextBrush;

        var textShadow = CreateShadow(_settings.DanmakuShadow, _settings.ShadowColor);
        var nameShadow = CreateShadow(_settings.DanmakuShadow, _settings.ShadowColor);
        var row = new DanmakuRowViewModel(item, _settings.DanmakuWrapLength, textBrush, nameBrush, textShadow, nameShadow, DanmakuFontSize, Math.Clamp(_settings.OverlayWidth, 320, 900) - 80);
        row.NameFontSize = Math.Max(8, DanmakuFontSize * NameFontScale);

        // 粉丝牌颜色也可在“礼物/粉丝牌颜色”页配置，例如“粉丝团灯牌:淡黄色”。
        if (row.HasMedal)
        {
            var medalBrush = FindGiftBrush(row.MedalName);
            if (medalBrush != null)
            {
                row.MedalBrush = medalBrush;
            }
        }

        _danmakus.Add(row);
        while (_danmakus.Count > _settings.MaxDanmakuLines)
        {
            _danmakus.RemoveAt(0);
        }

        // 鼠标停在弹幕区时，只有原本就在底部附近才自动跟随，方便往上翻看历史。
        FollowIfAllowed(DanmakuScroll, _isMouseOverDanmaku);

        UpdateLayout();
        ApplyAnchor();
        _ = LoadAvatarAsync(row);
    }

    /// <summary>
    /// 通知分流：礼物 / 大航海进礼物区，其它（进场、关注、分享等）进进场区。
    /// SC 不走这里 —— 它有自己的 SuperChatReceived 事件和 SC 区。
    /// </summary>
    public void AddNotice(NoticeItem notice)
    {
        if (notice.Kind is NoticeKind.Gift or NoticeKind.Guard)
        {
            AddGiftNotice(notice);
        }
        else
        {
            AddEntryNotice(notice);
        }
    }

    public void AddNotice(string text) => AddEntryNotice(new NoticeItem { Text = text });

    /// <summary>SC 区：头像 + SC 标识 + 舰长标识 + 粉丝牌 + 用户名 + 可换行正文。</summary>
    public void AddSuperChat(SuperChatItem item)
    {
        var defaultTextBrush = ParseBrush(_settings.DanmakuTextColor, Brushes.White);
        var shadow = CreateShadow(_settings.DanmakuShadow, _settings.ShadowColor);
        var scFontSize = Math.Clamp(_settings.SuperChatFontSize, 8, 30);

        // 复用弹幕行的渲染（头像 / 舰长 / 粉丝牌 / 用户名 / UID）。
        // 正文靠 TextWrapping 随宽度自动换行，所以 wrapLength 传 0，不做按字符数的硬换行。
        var row = new DanmakuRowViewModel(
            new DanmakuItem
            {
                UserId = item.UserId,
                UserName = item.UserName,
                Text = string.IsNullOrWhiteSpace(item.Message) ? "（没有留言内容）" : item.Message,
                Face = item.Face,
                MedalName = item.MedalName,
                MedalLevel = item.MedalLevel,
                GuardLevel = item.GuardLevel
            },
            0,
            defaultTextBrush,
            defaultTextBrush,
            shadow,
            shadow,
            scFontSize,
            Math.Clamp(_settings.OverlayWidth, 320, 900) - 80);

        row.NameFontSize = Math.Max(8, scFontSize * NameFontScale);
        row.ScAmount = item.Amount;

        if (row.HasMedal)
        {
            var medalBrush = FindGiftBrush(row.MedalName);
            if (medalBrush != null)
            {
                row.MedalBrush = medalBrush;
            }
        }

        _superChats.Add(row);
        TrimArea(_superChats, Math.Max(1, _settings.MaxSuperChatLines));

        UpdateAreaVisibility();
        FollowIfAllowed(ScScroll, _isMouseOverSc);
        UpdateLayout();
        ApplyAnchor();
        _ = LoadAvatarAsync(row);
    }

    /// <summary>礼物区：礼物投喂与大航海。</summary>
    public void AddGiftNotice(NoticeItem notice)
    {
        // 连击合并规则（保持原项目行为）：只有当「上一条通知」就是同一个人送的同一个礼物时才合并；
        // 中间夹了任何别的通知就不合并。
        if (_gifts.Count > 0)
        {
            var last = _gifts[^1];
            if (GiftMerge.CanMerge(last.GiftKey, notice.GiftKey) && !string.IsNullOrEmpty(last.GiftName))
            {
                var (count, amount) = GiftMerge.Combine(
                    last.GiftCount, last.GiftAmount, notice.GiftCount, notice.GiftAmount);

                last.GiftCount = count;
                last.GiftAmount = amount;

                if (last.Segments.Count >= 2)
                {
                    last.Segments[1].Text = $"{last.GiftName} x{count}个";
                }

                if (last.Segments.Count >= 3)
                {
                    last.Segments[2].Text = amount > 0 ? $"（{amount:0.##} CNY）" : "";
                }

                FollowIfAllowed(GiftScroll, _isMouseOverGift);
                return;
            }
        }

        AddNoticeRow(_gifts, notice, GiftScroll, _isMouseOverGift);
    }

    /// <summary>进场区：进入直播间 / 关注 / 分享等。</summary>
    public void AddEntryNotice(NoticeItem notice) =>
        AddNoticeRow(_entries, notice, EntryScroll, _isMouseOverEntry);

    private void AddNoticeRow(
        ObservableCollection<NoticeItemViewModel> target, NoticeItem notice, ScrollViewer viewer, bool mouseOver)
    {
        var isGiftArea = ReferenceEquals(target, _gifts);
        var areaColor = isGiftArea ? _settings.GiftTextColor : _settings.NoticeColor;
        var defaultForeground = ParseBrush(areaColor, new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));

        var vm = NoticeItemViewModel.FromNotice(
            notice,
            defaultForeground,
            Math.Clamp(isGiftArea ? _settings.GiftFontSize : _settings.EntryFontSize, 8, 30),
            CreateShadow(_settings.DanmakuShadow, _settings.ShadowColor),
            text => FindGiftBrush(text));

        target.Add(vm);
        TrimArea(target, Math.Max(1, isGiftArea ? _settings.MaxGiftLines : _settings.MaxEntryLines));

        UpdateAreaVisibility();
        FollowIfAllowed(viewer, mouseOver);
        UpdateLayout();
        ApplyAnchor();
    }

    public void ClearMessages()
    {
        if (_danmakus.Count == 0 && _superChats.Count == 0 && _gifts.Count == 0 && _entries.Count == 0)
        {
            return;
        }

        _danmakus.Clear();
        _superChats.Clear();
        _gifts.Clear();
        _entries.Clear();

        // 与聊天悬浮窗一致：清空时保持底边不动地收拢，而不是向顶边塌陷。
        UpdateAreaVisibility();
        UpdateLayout();
        ApplyAnchor();
    }

    /// <summary>把某个区的条目裁到保留上限。</summary>
    private static void TrimArea<T>(ObservableCollection<T> items, int max)
    {
        while (items.Count > max)
        {
            items.RemoveAt(0);
        }
    }

    /// <summary>空的分区整块收起，不占弹幕窗高度。</summary>
    private void UpdateAreaVisibility()
    {
        ScBorder.Visibility = _superChats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GiftBorder.Visibility = _gifts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EntryBorder.Visibility = _entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== 滚动策略 ====================

    private static bool IsNearBottom(ScrollViewer viewer) =>
        viewer.ScrollableHeight <= 0 || viewer.VerticalOffset >= viewer.ScrollableHeight - 40;

    /// <summary>新消息到达时的跟随策略：鼠标停在该区看历史时不打扰，否则自动跟到最新。</summary>
    private static void FollowIfAllowed(ScrollViewer viewer, bool mouseOver)
    {
        if (!mouseOver || IsNearBottom(viewer))
        {
            viewer.ScrollToEnd();
        }
    }

    private void DanmakuScroll_MouseEnter(object sender, MouseEventArgs e) => _isMouseOverDanmaku = true;

    private void DanmakuScroll_MouseLeave(object sender, MouseEventArgs e)
    {
        _isMouseOverDanmaku = false;
        DanmakuScroll.ScrollToEnd();
    }

    private void ScScroll_MouseEnter(object sender, MouseEventArgs e) => _isMouseOverSc = true;

    private void ScScroll_MouseLeave(object sender, MouseEventArgs e)
    {
        _isMouseOverSc = false;
        // 鼠标离开就回到最新一条
        ScScroll.ScrollToEnd();
    }

    private void GiftScroll_MouseEnter(object sender, MouseEventArgs e) => _isMouseOverGift = true;

    private void GiftScroll_MouseLeave(object sender, MouseEventArgs e)
    {
        _isMouseOverGift = false;
        GiftScroll.ScrollToEnd();
    }

    private void EntryScroll_MouseEnter(object sender, MouseEventArgs e) => _isMouseOverEntry = true;

    private void EntryScroll_MouseLeave(object sender, MouseEventArgs e)
    {
        _isMouseOverEntry = false;
        EntryScroll.ScrollToEnd();
    }

    /// <summary>滚轮翻看历史。四个区各滚各的，步长按该区自己的字号算。</summary>
    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.Delta == 0)
        {
            return;
        }

        var isScArea = ReferenceEquals(viewer, ScScroll);
        var fontForArea = ReferenceEquals(viewer, DanmakuScroll)
            ? Math.Max(8.0, _settings.FontSize)
            : isScArea
                ? Math.Max(8.0, _settings.SuperChatFontSize)
                : ReferenceEquals(viewer, GiftScroll)
                    ? Math.Max(8.0, _settings.GiftFontSize)
                    : Math.Max(8.0, _settings.EntryFontSize);

        // SC 行是两行文字 + 头像，一步迈大一点
        var step = Math.Max(20.0, (fontForArea + 5) * (isScArea ? 4 : 3));

        var target = viewer.VerticalOffset - Math.Sign(e.Delta) * step;
        viewer.ScrollToVerticalOffset(Math.Clamp(target, 0, viewer.ScrollableHeight));
        e.Handled = true;
    }

    // ==================== 位置：底边锚定 + 夹在屏幕内 ====================

    private void OverlayWindow_LocationChanged(object? sender, EventArgs e)
    {
        if (_suppressAnchor)
        {
            // 程序自己微调位置（吸底 / 拉回屏幕）不落盘，避免每条弹幕都写一次配置。
            return;
        }

        if (ActualHeight > 0 && !double.IsNaN(Top))
        {
            _anchorBottom = Top + ActualHeight;
        }
    }

    private void OverlayWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_suppressAnchor)
        {
            return;
        }

        if (e.HeightChanged)
        {
            ApplyAnchor();
        }
        else if (e.WidthChanged)
        {
            KeepOnScreen();
        }
    }

    private void ApplyAnchor()
    {
        if (ActualHeight <= 0 || double.IsNaN(Top))
        {
            return;
        }

        if (double.IsNaN(_anchorBottom))
        {
            _anchorBottom = Top + ActualHeight;
            KeepOnScreen();
            return;
        }

        var previous = _suppressAnchor;
        _suppressAnchor = true;
        try
        {
            var target = _anchorBottom - ActualHeight;
            if (Math.Abs(Top - target) > 0.5)
            {
                Top = target;
            }

            KeepOnScreen();
        }
        finally
        {
            _suppressAnchor = previous;
        }
    }

    /// <summary>把悬浮窗拉回当前显示器的工作区，保证任何时候都完整可见。</summary>
    private void KeepOnScreen()
    {
        if (!IsInitialized)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (!GetWindowRect(handle, out var rect))
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo32 { cbSize = Marshal.SizeOf<MonitorInfo32>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var work = info.rcWork;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var x = width <= work.Right - work.Left
            ? Math.Clamp(rect.Left, work.Left, work.Right - width)
            : work.Left;
        var y = height <= work.Bottom - work.Top
            ? Math.Clamp(rect.Top, work.Top, work.Bottom - height)
            : work.Top;

        if (x == rect.Left && y == rect.Top)
        {
            return;
        }

        var previous = _suppressAnchor;
        _suppressAnchor = true;
        try
        {
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        finally
        {
            _suppressAnchor = previous;
        }
    }

    /// <summary>把悬浮窗放到当前显示器右下角的默认位置。</summary>
    public void ResetPosition()
    {
        _anchorBottom = double.NaN;
        _settings.OverlayAnchorBottom = null;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            Left = 40;
            Top = 40;
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo32 { cbSize = Marshal.SizeOf<MonitorInfo32>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var work = info.rcWork;
        var scaleY = DpiScaleY > 0 ? DpiScaleY : 1.0;
        var scaleX = DpiScaleX > 0 ? DpiScaleX : 1.0;
        var width = ActualWidth > 0 ? (int)Math.Ceiling(ActualWidth * scaleX) : 440;
        var height = ActualHeight > 0 ? (int)Math.Ceiling(ActualHeight * scaleY) : 120;
        const int margin = 24;

        var x = Math.Max(work.Left, work.Right - width - margin);
        var y = Math.Max(work.Top, work.Bottom - height - margin);

        var previous = _suppressAnchor;
        _suppressAnchor = true;
        try
        {
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        finally
        {
            _suppressAnchor = previous;
        }

        if (!double.IsNaN(Top) && ActualHeight > 0)
        {
            _anchorBottom = Top + ActualHeight;
        }

        SavePosition();
    }

    private void SavePosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        _settings.OverlayLeft = Left;
        _settings.OverlayTop = Top;
        _settings.OverlayAnchorBottom = double.IsNaN(_anchorBottom) ? null : _anchorBottom;
        BiliSettingsService.Save(_settings);
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePosition();
        base.OnClosed(e);
    }

    /// <summary>弹幕区字号（钳制到合理范围）。</summary>
    private double DanmakuFontSize => Math.Clamp(_settings.FontSize, 8, 40);

    /// <summary>用户名相对弹幕字号的比例。</summary>
    private double NameFontScale => Math.Clamp(_settings.NameFontScale, 0.5, 1.5);

    private static FontFamily ParseFontFamily(string? value)
    {
        try
        {
            return string.IsNullOrWhiteSpace(value) ? new FontFamily("Microsoft YaHei UI") : new FontFamily(value);
        }
        catch
        {
            return new FontFamily("Microsoft YaHei UI");
        }
    }

    private double DpiScaleX => GetDpiScale().DpiScaleX;

    private double DpiScaleY => GetDpiScale().DpiScaleY;

    private DpiScale GetDpiScale()
    {
        try
        {
            return VisualTreeHelper.GetDpi(this);
        }
        catch
        {
            return new DpiScale(1, 1);
        }
    }

    /// <summary>按屏幕工作区收紧弹幕区高度上限，避免悬浮窗比屏幕还高。</summary>
    private void RefreshContentLimit()
    {
        // 其它几个分区的高度会影响弹幕区可用高度，先算它们
        RefreshAreaLimits();

        var desired = Math.Max(120, _settings.DanmakuMaxHeight);
        var available = AvailableContentHeight();
        DanmakuScroll.MaxHeight = double.IsNaN(available) || available <= 0
            ? desired
            : Math.Max(120, Math.Min(desired, available));
    }

    /// <summary>
    /// 各分区高度 = 「最多显示几行」× 该区一行的高度。保留条数比显示条数多的时候，
    /// 多出来的历史就在这块区域里用滚轮翻看。
    /// </summary>
    private void RefreshAreaLimits()
    {
        // SC 行带头像，比纯文字行高
        var scLineHeight = Math.Max(34.0, Math.Max(8.0, _settings.SuperChatFontSize) * 2.4 + 8);
        ScScroll.MaxHeight = Math.Clamp(_settings.MaxSuperChatVisibleLines, 1, 50) * scLineHeight;

        GiftScroll.MaxHeight = Math.Clamp(_settings.MaxGiftVisibleLines, 1, 50)
                               * (Math.Max(8.0, _settings.GiftFontSize) + 5);

        EntryScroll.MaxHeight = Math.Clamp(_settings.MaxEntryVisibleLines, 1, 50)
                                * (Math.Max(8.0, _settings.EntryFontSize) + 5);
    }

    /// <summary>背景画刷：把「背景颜色」自带的 alpha 再乘上「背景不透明度」。</summary>
    private Brush BuildBackgroundBrush()
    {
        var fallback = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        var brush = ParseBrush(_settings.BackgroundColor, fallback);
        if (brush is SolidColorBrush solid)
        {
            var alpha = (byte)Math.Round(solid.Color.A * Math.Clamp(_settings.BackgroundOpacity, 0.0, 1.0));
            return new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
        }

        return brush;
    }

    private double AvailableContentHeight()
    {
        if (!IsInitialized)
        {
            return double.NaN;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return double.NaN;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo32 { cbSize = Marshal.SizeOf<MonitorInfo32>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return double.NaN;
        }

        var scale = DpiScaleY;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var workHeightDip = (info.rcWork.Bottom - info.rcWork.Top) / scale;

        // 除弹幕区以外的固定高度：内边距 + 边框 + 顶部标题行 + 通知区上限 + 外边距 + 余量
        var chrome = RootBorder.Padding.Top + RootBorder.Padding.Bottom
                     + RootBorder.BorderThickness.Top + RootBorder.BorderThickness.Bottom
                     + HeaderPanel.ActualHeight
                     + ScScroll.MaxHeight + GiftScroll.MaxHeight + EntryScroll.MaxHeight
                     + DanmakuScroll.Margin.Top + DanmakuScroll.Margin.Bottom
                     + 28;

        return workHeightDip - chrome;
    }

    private async System.Threading.Tasks.Task LoadAvatarAsync(DanmakuRowViewModel row)
    {
        BitmapImage? bitmap = null;
        if (!string.IsNullOrEmpty(row.AvatarUrl))
        {
            bitmap = await _faceCache.GetFaceFromUrlAsync(row.AvatarUrl);
        }

        if (bitmap == null && row.UserId > 0)
        {
            bitmap = await _faceCache.GetFaceAsync(row.UserId);
        }

        if (bitmap != null)
        {
            await Dispatcher.InvokeAsync(() => row.Avatar = bitmap);
        }
    }

    private static DropShadowEffect? CreateShadow(bool enabled, string shadowColor)
    {
        if (!enabled)
        {
            return null;
        }

        var color = ParseColor(shadowColor, Colors.Black);
        return new DropShadowEffect
        {
            Color = color,
            BlurRadius = 2.5,
            ShadowDepth = 1,
            Direction = 270,
            Opacity = 0.85
        };
    }

    private Brush? FindGiftBrush(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var pair in _settings.GiftColors)
        {
            if (string.IsNullOrEmpty(pair.Key))
            {
                continue;
            }

            if (text.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                return TryParseBrush(pair.Value);
            }
        }

        return null;
    }

    private static Brush? TryParseBrush(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return new BrushConverter().ConvertFromString(value) as Brush;
        }
        catch
        {
            return null;
        }
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
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            var color = ColorConverter.ConvertFromString(value) as Color?;
            return color ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !_settings.ClickThrough)
        {
            DragMove();

            // 拖动结束后把新位置记为停靠点，并确保没有滑出屏幕。
            KeepOnScreen();
            if (!double.IsNaN(Top) && ActualHeight > 0)
            {
                _anchorBottom = Top + ActualHeight;
            }

            SavePosition();
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo32 lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo32
    {
        public int cbSize;
        public Win32Rect rcMonitor;
        public Win32Rect rcWork;
        public uint dwFlags;
    }
}
