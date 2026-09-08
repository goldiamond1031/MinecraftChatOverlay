using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Services;
using MinecraftChatOverlay.ViewModels;

namespace MinecraftChatOverlay;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;

    private readonly AppSettings _settings;
    private readonly ObservableCollection<ChatMessageViewModel> _messages = new();
    private bool _isMouseOverOverlay;

    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ChatList.ItemsSource = _messages;
        ApplySettings();
        if (_settings.OverlayLeft.HasValue)
        {
            Left = _settings.OverlayLeft.Value;
        }

        if (_settings.OverlayTop.HasValue)
        {
            Top = _settings.OverlayTop.Value;
        }

        LocationChanged += (_, _) => SavePosition();
    }

    private void SavePosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        _settings.OverlayLeft = Left;
        _settings.OverlayTop = Top;
        try
        {
            SettingsService.Save(_settings);
        }
        catch
        {
            // 记忆位置失败不应影响悬浮窗使用。
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateClickThrough();
    }

    public void ApplySettings()
    {
        Width = Math.Clamp(_settings.OverlayWidth, 120, 2000);
        Opacity = Math.Clamp(_settings.OverlayOpacity, 0.1, 1.0);
        RootBorder.Background = BuildBackgroundBrush();
        ChatScroll.MaxHeight = Math.Clamp(_settings.OverlayMaxHeight, 100, 2000);
        UpdateClickThrough();

        var fontFamily = ParseFontFamily(_settings.FontFamily);
        var fontSize = Math.Clamp(_settings.FontSize, 8, 96);
        var fontWeight = ParseFontWeight(_settings.FontWeight);
        var foreground = ParseBrush(_settings.TextColor, Brushes.White);
        var shadow = CreateShadow(_settings.TextShadow);
        var maxTextWidth = Math.Max(80, Width - 40);

        foreach (var message in _messages)
        {
            message.Segments = BuildSegments(message.RawText, message.Timestamp);
            message.FontFamily = fontFamily;
            message.FontSize = fontSize;
            message.FontWeight = fontWeight;
            message.Foreground = foreground;
            message.Shadow = shadow;
            message.MaxTextWidth = maxTextWidth;
            message.TextSelectionEnabled = _settings.EnableTextSelection;
        }

        while (_messages.Count > Math.Max(1, _settings.MaxMessages))
        {
            _messages.RemoveAt(0);
        }
    }

    public void AddMessage(string chatMessage)
    {
        // 合并连续重复消息：只有紧挨着的同一条消息会被忽略。
        if (_settings.MergeDuplicateMessages &&
            _messages.Count > 0 &&
            string.Equals(_messages[^1].RawText, chatMessage, StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTime.Now;
        // 鼠标不在悬浮窗上时始终自动跟随最新消息；
        // 鼠标在悬浮窗上时，只有原本在底部附近才自动跟随，方便查看历史。
        var autoScroll = !_isMouseOverOverlay || IsNearBottom();
        var previousHeight = ActualHeight;
        var vm = new ChatMessageViewModel
        {
            Timestamp = now,
            RawText = chatMessage,
            Segments = BuildSegments(chatMessage, now),
            FontFamily = ParseFontFamily(_settings.FontFamily),
            FontSize = Math.Clamp(_settings.FontSize, 8, 96),
            FontWeight = ParseFontWeight(_settings.FontWeight),
            Foreground = ParseBrush(_settings.TextColor, Brushes.White),
            Shadow = CreateShadow(_settings.TextShadow),
            MaxTextWidth = Math.Max(80, Width - 40),
            TextSelectionEnabled = _settings.EnableTextSelection
        };

        _messages.Add(vm);
        while (_messages.Count > Math.Max(1, _settings.MaxMessages))
        {
            _messages.RemoveAt(0);
        }

        // 如果用户正在查看历史消息，不强制跳到底部；只有原本就在底部附近时才自动跟随。
        if (autoScroll)
        {
            ChatScroll.ScrollToEnd();
        }

        // 让悬浮窗保持底部位置不变，新消息加入后整体向上扩展。
        ChatList.UpdateLayout();
        var newHeight = ActualHeight;
        if (newHeight > previousHeight && !double.IsNaN(Top))
        {
            Top -= newHeight - previousHeight;
        }

        AnimateNewMessage(vm);
    }

    private void AnimateNewMessage(ChatMessageViewModel message)
    {
        if (!_settings.EnableMessageAnimation)
        {
            return;
        }

        try
        {
            ChatList.UpdateLayout();
            if (ChatList.ItemContainerGenerator.ContainerFromItem(message) is not FrameworkElement container)
            {
                return;
            }

            var translate = new TranslateTransform(0, 24);
            container.RenderTransform = translate;
            container.Opacity = 0;

            // 从下往上滑入，使用曲线缓动。
            var slide = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            translate.BeginAnimation(TranslateTransform.YProperty, slide);

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            container.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        catch
        {
            // 动画失败不应影响消息显示。
        }
    }

    private bool IsNearBottom()
    {
        return ChatScroll.ScrollableHeight <= 0 || ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 40;
    }

    public void ClearMessages()
    {
        _messages.Clear();
    }

    private List<ChatSegmentViewModel> BuildSegments(string rawText, DateTime timestamp)
    {
        // 这里保留原始文本并实时应用替换规则，修改规则后已显示消息也会重新渲染。
        var displayText = ChatTextProcessor.ApplyReplacements(rawText, _settings.ReplaceRules);

        var foreground = ParseBrush(_settings.TextColor, Brushes.White);
        var defaultWeight = ParseFontWeight(_settings.FontWeight);

        var segments = new List<ChatSegmentViewModel>();
        if (_settings.ShowTimestamp)
        {
            segments.Add(new ChatSegmentViewModel(
                $"[{timestamp:HH:mm:ss}] ",
                foreground,
                defaultWeight));
        }

        // 先按用户颜色规则生成分段，再叠加“玩家发言颜色/发言玩家 ID 颜色”。
        var colored = ChatTextProcessor.BuildColoredSegments(displayText, _settings.ColorRules, foreground, defaultWeight);
        var contentBrush = string.IsNullOrWhiteSpace(_settings.PlayerContentColor)
            ? null
            : ParseBrush(_settings.PlayerContentColor, foreground);
        var nameBrush = string.IsNullOrWhiteSpace(_settings.PlayerNameColor)
            ? null
            : ParseBrush(_settings.PlayerNameColor, foreground);
        colored = ChatTextProcessor.ApplyPlayerColorOverrides(colored, displayText, foreground, contentBrush, nameBrush);

        foreach (var segment in colored)
        {
            segment.Text = WrapText(segment.Text, _settings.WrapLength);
            segments.Add(segment);
        }

        return segments;
    }

    private static string WrapText(string text, int wrapLength)
    {
        if (wrapLength <= 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length + 16);
        var count = 0;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n')
            {
                sb.Append(ch);
                count = 0;
                continue;
            }

            sb.Append(ch);
            count++;
            if (count >= wrapLength)
            {
                sb.AppendLine();
                count = 0;
            }
        }

        return sb.ToString();
    }

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

    private static FontWeight ParseFontWeight(string? value)
    {
        return value?.Trim() switch
        {
            "Bold" => FontWeights.Bold,
            "SemiBold" => FontWeights.SemiBold,
            "Light" => FontWeights.Light,
            "Thin" => FontWeights.Thin,
            _ => FontWeights.Normal
        };
    }

    private static DropShadowEffect? CreateShadow(bool enabled)
    {
        if (!enabled)
        {
            return null;
        }

        return new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 2.5,
            ShadowDepth = 1,
            Direction = 270,
            Opacity = 0.85
        };
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

    private Brush BuildBackgroundBrush()
    {
        var fallback = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        var brush = ParseBrush(_settings.BackgroundColor, fallback);
        if (brush is SolidColorBrush solid)
        {
            var alpha = (byte)Math.Round(solid.Color.A * Math.Clamp(_settings.BackgroundOpacity, 0.0, 1.0));
            return new SolidColorBrush(Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B));
        }

        return brush;
    }

    private void RootBorder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isMouseOverOverlay = true;
    }

    private void RootBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isMouseOverOverlay = false;
        // 鼠标一旦离开悬浮窗，自动回到最新消息。
        ChatScroll.ScrollToEnd();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !_settings.ClickThrough)
        {
            DragMove();
        }
    }

    private void UpdateClickThrough()
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

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
