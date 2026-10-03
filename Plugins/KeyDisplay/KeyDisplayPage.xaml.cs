using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftChatOverlay.Plugin;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;

namespace MinecraftChatOverlay.Plugins.KeyDisplay;

/// <summary>
/// 「按键显示」设置页。
///
/// 中间那块画布是这个页面的核心：格子直接在上面拖位置、拖右下角改大小、吸附网格，
/// 而且会**实时反映真实的按下状态** —— 在配置页里按一下 W，画布上的 W 就亮，
/// 不用切到游戏去验证。
///
/// 闸门（DEV-NOTES 坑 53/58）：构造里往控件填值会触发 Checked/TextChanged/SelectionChanged，
/// 那些处理器一跑就会把还没填上的空值写回配置，所以填值期间必须把 _loading 关掉。
/// </summary>
public partial class KeyDisplayPage : System.Windows.Controls.UserControl
{
    private readonly KeyDisplayPlugin _plugin;

    private KeyDisplaySettings _settings => _plugin.Settings;

    private bool _loading = true;

    // ---- 画布 ----
    /// <summary>视口还没算出来时的兜底尺寸。</summary>
    private const double FallbackViewportWidth = 600;

    private const double FallbackViewportHeight = 400;

    /// <summary>内容右边/下边留的余量，让用户能把格子继续往外拖。</summary>
    private const double ContentMargin = 60;

    /// <summary>当前画布宽度（由 <see cref="UpdateCanvasSize"/> 算出来）。</summary>
    private double CanvasWidth => DesignCanvas is null ? FallbackViewportWidth : DesignCanvas.Width;

    /// <summary>当前画布高度。</summary>
    private double CanvasHeight => DesignCanvas is null ? FallbackViewportHeight : DesignCanvas.Height;

    /// <summary>画布上的一个格子元素。</summary>
    private sealed class CanvasCell
    {
        public Border Root = null!;
        public TextBlock Text = null!;
        public Border Handle = null!;
        public KeyCell Model = null!;
        public bool IsDown;
    }

    private readonly Dictionary<KeyCell, CanvasCell> _canvasCells = new();

    private KeyCell? _selected;
    private bool _dragging;
    private bool _resizing;
    private bool _dirty;
    private Point _dragStart;
    private double _origX, _origY, _origW, _origH;

    // ---- 定时器 ----
    private DispatcherTimer? _liveTimer;     // 画布实时预览 + 按键捕获
    private DispatcherTimer? _commitTimer;   // 把改动攒一下再应用/存盘，别拖一次滑块重建十几次窗口

    private readonly KeyCapture _capture = new();

    private static readonly string[] CommonFonts =
    {
        "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "SimHei", "SimSun",
        "Consolas", "Cascadia Mono", "Arial", "Impact", "微软雅黑",
    };

    private static readonly (int Ms, string Text)[] RefreshChoices =
    {
        (16, "16 毫秒（约 60fps，最跟手）"),
        (24, "24 毫秒（约 40fps）"),
        (33, "33 毫秒（约 30fps，推荐）"),
        (50, "50 毫秒"),
        (100, "100 毫秒（最省）"),
    };

    public KeyDisplayPage(KeyDisplayPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        _loading = true;
        try
        {
            FillFonts();
            FillRefreshChoices();
            FillCommonKeys();
            FillCellLookCombos();
            LoadFromSettings();
            UpdateCanvasGrid();
            RebuildCanvas();
        }
        finally
        {
            _loading = false;
        }

        UpdateValueLabels();
        RefreshColorPreviews();
        UpdateCanvasHint();
        UpdateSelectionPanel();

        _liveTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _liveTimer.Tick += (_, _) => LiveTick();
        _liveTimer.Start();

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _commitTimer.Tick += (_, _) =>
        {
            _commitTimer!.Stop();

            // 只把「写盘」攒一下。外观在改动的当下就已经生效了（见 Option_Changed），
            // 早先版本连生效一起攒，结果拖滑块要等 150 毫秒悬浮窗才动，手感很迟钝。
            _plugin.SaveSettings();
        };

        // 构造的时候布局还没发生，CanvasScroll.ViewportWidth 还是 0 ——
        // 那一刻算出来的画布尺寸只能是兜底值。等 Loaded（页面挂进宿主窗口、布局完成）
        // 再算一次，否则画布会一直停在偏小的尺寸上，要等用户拖一下格子才"突然铺满"。
        Loaded += (_, _) => UpdateCanvasSize();
    }

    /// <summary>页面不再需要时由插件调（插件 Shutdown）。</summary>
    public void StopTimers()
    {
        try { _liveTimer?.Stop(); } catch { }
        try { _commitTimer?.Stop(); } catch { }
        _liveTimer = null;
        _commitTimer = null;
        _capture.Stop();
    }

    // ===================== 初始化 =====================

    /// <summary>
    /// 字体下拉列**全部系统字体**。
    /// 因为字体被定成「只能从列表里选」（不让手打），列表就必须够全 ——
    /// 否则用户想要的字体不在列表里就没路可走了。
    /// </summary>
    private void FillFonts()
    {
        try
        {
            var names = new List<string>();

            foreach (var family in Fonts.SystemFontFamilies)
            {
                var name = family.Source;
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.CurrentCultureIgnoreCase);

            foreach (var name in names)
            {
                FontComboBox.Items.Add(name);
            }
        }
        catch
        {
            // 枚举失败就退回常用那几个，至少还能用
            foreach (var font in CommonFonts)
            {
                FontComboBox.Items.Add(font);
            }
        }
    }

    private void FillRefreshChoices()
    {
        foreach (var choice in RefreshChoices)
        {
            RefreshComboBox.Items.Add(new ComboBoxItem { Content = choice.Text, Tag = choice.Ms });
        }
    }

    /// <summary>
    /// 键位下拉列**全部虚拟键**（0x01~0xFE，约 250 个）。
    ///
    /// 需求是「选择按键要全面覆盖」—— 不能只列常用的那几个：功能键、小键盘、符号、
    /// 左右修饰键、鼠标侧键都得能直接选到，否则有些键就永远加不进来了。
    /// 没被系统定义的槽位也会列出来（名字是 VK 加十六进制码），万一要用到也有路走。
    /// </summary>
    private void FillCommonKeys()
    {
        for (var vk = VirtualKeys.MinCode; vk <= VirtualKeys.MaxCode; vk++)
        {
            CommonKeyComboBox.Items.Add(new ComboBoxItem
            {
                Content = VirtualKeys.Name(vk),
                Tag = vk,
            });
        }

        CommonKeyComboBox.SelectedIndex = 0;
    }

    private void LoadFromSettings()
    {
        var s = _settings;

        if (s.Keys.Count == 0)
        {
            foreach (var cell in KeyDisplaySettings.DefaultLayout())
            {
                s.Keys.Add(cell);
            }
        }

        EnabledCheckBox.IsChecked = s.Enabled;
        ClickThroughCheckBox.IsChecked = s.ClickThrough;
        SnapCheckBox.IsChecked = s.SnapToGrid;
        ShadowEnabledCheckBox.IsChecked = s.ShadowEnabled;
        DynamicCheckBox.IsChecked = s.DynamicEnabled;

        FontComboBox.Text = s.FontFamily;

        FontSizeSlider.Value = Math.Clamp(s.FontSize, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
        CornerSlider.Value = Math.Clamp(s.CornerRadius, CornerSlider.Minimum, CornerSlider.Maximum);
        BorderThicknessSlider.Value = Math.Clamp(s.BorderThickness, BorderThicknessSlider.Minimum, BorderThicknessSlider.Maximum);
        ShadowBlurSlider.Value = Math.Clamp(s.ShadowBlur, ShadowBlurSlider.Minimum, ShadowBlurSlider.Maximum);
        ShadowOffsetSlider.Value = Math.Clamp(s.ShadowOffset, ShadowOffsetSlider.Minimum, ShadowOffsetSlider.Maximum);
        ShadowOpacitySlider.Value = Math.Clamp(s.ShadowOpacity, 0, 1);
        ShadowDirectionSlider.Value = Math.Clamp(s.ShadowDirection, 0, 359);
        GridSizeSlider.Value = Math.Clamp(s.GridSize, GridSizeSlider.Minimum, GridSizeSlider.Maximum);
        DynamicScaleSlider.Value = Math.Clamp(s.DynamicForce, DynamicScaleSlider.Minimum, DynamicScaleSlider.Maximum);

        var index = Array.FindIndex(RefreshChoices, c => c.Ms == s.RefreshMs);
        RefreshComboBox.SelectedIndex = index >= 0 ? index : 2;
    }

    // ===================== 画布 =====================

    /// <summary>
    /// 按当前「网格大小」生成画布底纹。
    /// 画的是真网格线（不是点阵），线间距就是吸附粒度 —— 看到什么就吸到什么。
    /// 间距不足 4 像素时按 4 画：再密就糊成一片灰了，反而看不出吸附。
    /// </summary>
    private void UpdateCanvasGrid()
    {
        try
        {
            var spacing = Math.Max(4, Math.Round(_settings.GridSize));

            var lineBrush = new SolidColorBrush(Color.FromArgb(0x38, 0x80, 0x80, 0x80));
            lineBrush.Freeze();

            // 线条画成 1 像素宽的**矩形**而不是 Pen：DrawingBrush 的 tile 会裁掉超出的部分，
            // 用 Pen 画在坐标 0 上的线正好有一半在 tile 外面，渲染出来是半透明的淡线。
            // 放在 tile 的右边缘和下边缘就完整落在里面了。
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing
            {
                Brush = lineBrush,
                Geometry = new RectangleGeometry(new Rect(spacing - 1, 0, 1, spacing)),
            });
            group.Children.Add(new GeometryDrawing
            {
                Brush = lineBrush,
                Geometry = new RectangleGeometry(new Rect(0, spacing - 1, spacing, 1)),
            });

            var brush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, spacing, spacing),
                ViewportUnits = BrushMappingMode.Absolute,
            };
            brush.Freeze();

            DesignCanvas.Background = brush;
        }
        catch
        {
        }
    }

    private void CanvasScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // ViewportWidth / ViewportHeight 在这个事件里可能还是旧值（WPF 的布局顺序是先触发
        // SizeChanged 再更新视口），所以要延到这一帧布局结束之后再算。
        Dispatcher.BeginInvoke(new Action(UpdateCanvasSize), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 画布自己能滚就自己滚；滚到头了（或者压根不用滚）就把滚轮**还给外层页面**。
    ///
    /// 不这么做的话，鼠标停在画布上时整个设置页都滚不动 —— 而画布占了页面很大一块，
    /// 想滚页面得先把鼠标挪到别处，很别扭。ScrollViewer 默认会把滚轮事件吃掉，所以要主动转发。
    /// </summary>
    private void CanvasScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
        {
            return;
        }

        var canScrollSelf = e.Delta > 0
            ? viewer.VerticalOffset > 0
            : viewer.VerticalOffset < viewer.ScrollableHeight;

        if (canScrollSelf)
        {
            return;
        }

        var outer = FindAncestorScrollViewer(viewer);
        if (outer is null)
        {
            return;
        }

        e.Handled = true;
        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
    }

    /// <summary>往上找最近的一个 ScrollViewer —— 也就是宿主承载插件页的那个。</summary>
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject element)
    {
        var current = VisualTreeHelper.GetParent(element);

        while (current is not null)
        {
            if (current is ScrollViewer viewer)
            {
                return viewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 重算画布尺寸：**宽高都取「可视区」和「内容包围盒 + 余量」里较大的那个**。
    ///
    /// 两头都对得住：
    ///   · 内容不多时画布正好铺满可视区 —— 不会再像早先那样"固定 880、可视区只有 868"，
    ///     恒多出 12 像素把水平滚动条永远挂在那儿（滑块还被缩成一个莫名其妙的小方块）；
    ///   · 内容往外拖时画布跟着长，真需要滚动了才出现滚动条，那时候它是合理的。
    /// </summary>
    internal void UpdateCanvasSize()
    {
        try
        {
            var viewportWidth = CanvasScroll.ViewportWidth;
            var viewportHeight = CanvasScroll.ViewportHeight;

            if (viewportWidth < 10) viewportWidth = FallbackViewportWidth;
            if (viewportHeight < 10) viewportHeight = FallbackViewportHeight;

            double contentRight = 0;
            double contentBottom = 0;

            foreach (var cell in _settings.Keys)
            {
                contentRight = Math.Max(contentRight, cell.X + cell.Width);
                contentBottom = Math.Max(contentBottom, cell.Y + cell.Height);
            }

            var width = Math.Max(viewportWidth, contentRight + ContentMargin);
            var height = Math.Max(viewportHeight, contentBottom + ContentMargin);

            if (Math.Abs(DesignCanvas.Width - width) > 0.5)
            {
                DesignCanvas.Width = width;
            }

            if (Math.Abs(DesignCanvas.Height - height) > 0.5)
            {
                DesignCanvas.Height = height;
            }

            UpdateCanvasHint();
        }
        catch
        {
        }
    }

    /// <summary>只刷所有格子的外观属性，**不重建元素** —— 拖滑块时一秒要调几十次。</summary>
    private void RefreshAllCanvasLooks()
    {
        foreach (var visual in _canvasCells.Values)
        {
            ApplyCanvasLook(visual);
        }
    }

    private void RebuildCanvas()
    {
        DesignCanvas.Children.Clear();
        _canvasCells.Clear();

        foreach (var cell in _settings.Keys)
        {
            var text = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false,
            };

            var handle = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = AccentBrush,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeNWSE,
                Tag = cell,
                Visibility = Visibility.Collapsed,
                ToolTip = "拖这里改大小",
            };
            handle.MouseLeftButtonDown += Handle_MouseLeftButtonDown;

            var root = new Border
            {
                Tag = cell,
                Cursor = Cursors.SizeAll,
                ContextMenu = BuildCellMenu(cell),
                Child = new Grid { Children = { text, handle } },
            };
            root.MouseLeftButtonDown += Cell_MouseLeftButtonDown;

            Canvas.SetLeft(root, cell.X);
            Canvas.SetTop(root, cell.Y);
            DesignCanvas.Children.Add(root);

            var visual = new CanvasCell { Root = root, Text = text, Handle = handle, Model = cell };
            _canvasCells[cell] = visual;
            ApplyCanvasLook(visual);
        }

        UpdateCanvasSize();
    }

    /// <summary>按当前设置 + 实时按下状态 + 选中状态刷新一个格子的样子。</summary>
    private void ApplyCanvasLook(CanvasCell visual)
    {
        var s = _settings;
        var cell = visual.Model;

        visual.Text.Text = string.IsNullOrWhiteSpace(cell.DisplayText)
            ? VirtualKeys.Name(cell.VirtualKey)
            : cell.DisplayText;

        visual.Text.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(s.FontFamily) ? "Microsoft YaHei UI" : s.FontFamily);
        visual.Text.FontSize = Math.Clamp(s.FontSize, 6, 200);
        visual.Text.FontWeight = s.Bold ? FontWeights.Bold : FontWeights.Normal;

        var textHex = visual.IsDown
            ? Pick(cell.PressedTextColorOverride, s.PressedTextColor)
            : Pick(cell.TextColorOverride, s.IdleTextColor);

        var backgroundHex = visual.IsDown
            ? Pick(cell.PressedBackgroundColorOverride, s.PressedBackgroundColor)
            : Pick(cell.BackgroundColorOverride, s.IdleBackgroundColor);

        visual.Text.Foreground = MakeBrush(textHex, Colors.White);
        visual.Root.Background = MakeBrush(backgroundHex, Colors.Transparent);

        visual.Root.Width = Math.Max(8, cell.Width);
        visual.Root.Height = Math.Max(8, cell.Height);
        visual.Root.CornerRadius = new CornerRadius(Math.Max(0, s.CornerRadius));

        var isSelected = ReferenceEquals(cell, _selected);
        visual.Root.BorderThickness = new Thickness(isSelected ? 2 : Math.Max(0, s.BorderThickness));
        visual.Root.BorderBrush = isSelected ? AccentBrush : MakeBrush(s.BorderColor, Colors.Transparent);
        visual.Handle.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;

        Canvas.SetLeft(visual.Root, cell.X);
        Canvas.SetTop(visual.Root, cell.Y);
    }

    private Brush AccentBrush
    {
        get
        {
            try
            {
                // 页面挂进宿主窗口之后才解析得到；没挂上就先用个兜底色，不影响功能
                if (TryFindResource("AccentBrush") is Brush brush)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(Color.FromRgb(0x4F, 0x9C, 0xFF));
        }
    }

    private void LiveTick()
    {
        // 捕获中：先看有没有按键
        if (_capture.Armed)
        {
            var captured = _capture.Scan();
            if (captured is int vk)
            {
                AddKey(vk);
                return;
            }
        }

        // 画布实时预览：只在按下状态翻转时改画笔
        foreach (var visual in _canvasCells.Values)
        {
            var down = VirtualKeys.IsDown(visual.Model.VirtualKey);
            if (down == visual.IsDown)
            {
                continue;
            }

            visual.IsDown = down;
            ApplyCanvasLook(visual);
        }
    }

    // ===================== 拖拽与吸附 =====================

    private double SnapValue(double value)
    {
        var s = _settings;
        var grid = Math.Max(1, s.GridSize);
        return s.SnapToGrid ? Math.Round(value / grid) * grid : Math.Round(value);
    }

    private void Cell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not KeyCell cell)
        {
            return;
        }

        SelectCell(cell);

        _dragging = true;
        _dragStart = e.GetPosition(DesignCanvas);
        _origX = cell.X;
        _origY = cell.Y;
        DesignCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not KeyCell cell)
        {
            return;
        }

        SelectCell(cell);

        _resizing = true;
        _dragStart = e.GetPosition(DesignCanvas);
        _origW = cell.Width;
        _origH = cell.Height;
        DesignCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void DesignCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging && !_resizing)
        {
            return;
        }

        var cell = _selected;
        if (cell is null || !_canvasCells.TryGetValue(cell, out var visual))
        {
            return;
        }

        var position = e.GetPosition(DesignCanvas);
        var dx = position.X - _dragStart.X;
        var dy = position.Y - _dragStart.Y;

        if (_dragging)
        {
            // 只保证不跑到负坐标：往外拖的时候画布会跟着长（UpdateCanvasSize），所以不设上限
            cell.X = Math.Max(0, SnapValue(_origX + dx));
            cell.Y = Math.Max(0, SnapValue(_origY + dy));
        }
        else
        {
            cell.Width = Math.Max(16, SnapValue(_origW + dx));
            cell.Height = Math.Max(16, SnapValue(_origH + dy));
        }

        // 只动这一个元素，别整块重建（拖拽时重建会闪）
        Canvas.SetLeft(visual.Root, cell.X);
        Canvas.SetTop(visual.Root, cell.Y);
        visual.Root.Width = cell.Width;
        visual.Root.Height = cell.Height;

        _dirty = true;
        UpdateCellPropBoxes();
        UpdateCanvasSize();   // 拖到哪，画布长到哪
    }

    private void DesignCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点到格子上时那边的处理器已经把事件标记成 Handled，走不到这儿。
        // 能走到这儿说明点的是空白处 —— 取消选中。
        SelectCell(null);
    }

    private void DesignCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
    }

    private void DesignCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging && !_resizing)
        {
            return;
        }

        _dragging = false;
        _resizing = false;

        try { DesignCanvas.ReleaseMouseCapture(); } catch { }

        if (_dirty)
        {
            _dirty = false;
            ApplyStructureAndSave();
        }
    }

    // ===================== 选中与属性 =====================

    private void SelectCell(KeyCell? cell)
    {
        var previous = _selected;
        _selected = cell;

        if (previous is not null && _canvasCells.TryGetValue(previous, out var oldVisual))
        {
            ApplyCanvasLook(oldVisual);
        }

        if (cell is not null && _canvasCells.TryGetValue(cell, out var newVisual))
        {
            ApplyCanvasLook(newVisual);
        }

        UpdateSelectionPanel();
    }

    /// <summary>快照用：选中第一个格子，好把「选中属性区」也渲进图里。</summary>
    internal void SelectFirstCellForSnapshot()
    {
        // 优先挑一个鼠标键 —— 那样「显示 CPS」开关才会露出来，快照能顺带看它
        var cell = _settings.Keys.FirstOrDefault(k => k.VirtualKey is 0x01 or 0x02)
                   ?? _settings.Keys.FirstOrDefault();

        if (cell is not null)
        {
            SelectCell(cell);
        }
    }

    private void UpdateSelectionPanel()
    {
        var cell = _selected;

        CellPropsPanel.Visibility = cell is null ? Visibility.Collapsed : Visibility.Visible;
        DeleteCellButton.IsEnabled = cell is not null;

        if (cell is null)
        {
            return;
        }

        var wasLoading = _loading;
        _loading = true;
        try
        {
            CellTextBox.Text = cell.DisplayText;
            CellWidthBox.Text = cell.Width.ToString("0");
            CellHeightBox.Text = cell.Height.ToString("0");
            CellXBox.Text = cell.X.ToString("0");
            CellYBox.Text = cell.Y.ToString("0");

            var isMouse = cell.VirtualKey is 0x01 or 0x02;
            CellShowCpsCheckBox.Visibility = isMouse ? Visibility.Visible : Visibility.Collapsed;
            CellShowCpsCheckBox.IsChecked = isMouse && cell.ShowCps;
        }
        finally
        {
            _loading = wasLoading;
        }

        RefreshColorPreviews();
        RefreshCellLookControls();
    }

    private void UpdateCellPropBoxes()
    {
        var cell = _selected;
        if (cell is null)
        {
            return;
        }

        var wasLoading = _loading;
        _loading = true;
        try
        {
            CellWidthBox.Text = cell.Width.ToString("0");
            CellHeightBox.Text = cell.Height.ToString("0");
            CellXBox.Text = cell.X.ToString("0");
            CellYBox.Text = cell.Y.ToString("0");
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void CellProp_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null)
        {
            return;
        }

        var cell = _selected;
        cell.DisplayText = CellTextBox.Text ?? "";

        // 上限给得宽松（画布会跟着长），但总得有个头，免得手输 99999 让画布大得没法用
        cell.Width = ParseDouble(CellWidthBox.Text, cell.Width, 16, 1000);
        cell.Height = ParseDouble(CellHeightBox.Text, cell.Height, 16, 1000);
        cell.X = ParseDouble(CellXBox.Text, cell.X, 0, 4000);
        cell.Y = ParseDouble(CellYBox.Text, cell.Y, 0, 4000);

        if (_canvasCells.TryGetValue(cell, out var visual))
        {
            ApplyCanvasLook(visual);
        }

        RefreshColorPreviews();
        ApplyStructureAndSave();
    }

    private static double ParseDouble(string? text, double fallback, double min, double max)
    {
        if (double.TryParse((text ?? "").Trim(), out var value))
        {
            return Math.Clamp(value, min, max);
        }

        return fallback;
    }

    // ===================== 添加 / 删除按键 =====================

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capture.Armed)
        {
            EndCapture("已取消。");
            return;
        }

        // 忽略期吃掉"点这个按钮"那一下的鼠标左键
        _capture.Start(TimeSpan.FromMilliseconds(450));
        CaptureButton.Content = "正在等按键…（再点一次取消）";
        CaptureHintText.Text = "现在按一下你想显示的那个键 —— 键盘任意键、鼠标左键/右键/中键/侧键都行。没有任何东西被记录，只是读了它的状态。";
    }

    private void EndCapture(string hint)
    {
        _capture.Stop();
        CaptureButton.Content = "添加按键（按一下）";

        if (hint.Length > 0)
        {
            CaptureHintText.Text = hint;
        }
    }

    private void AddKey(int virtualKey)
    {
        var name = VirtualKeys.Name(virtualKey);

        if (_settings.Keys.Any(k => k.VirtualKey == virtualKey))
        {
            EndCapture($"{name} 已经在列表里了，直接在上面拖它就行。");
            return;
        }

        var count = _settings.Keys.Count;
        var width = 46.0;
        var height = 46.0;

        var cell = new KeyCell
        {
            VirtualKey = virtualKey,
            Width = width,
            Height = height,
            X = Math.Clamp(SnapValue(10 + (count % 8) * 54), 0, CanvasWidth - width),
            Y = Math.Clamp(SnapValue(10 + (count / 8) * 54), 0, CanvasHeight - height),
        };

        _settings.Keys.Add(cell);
        RebuildCanvas();
        SelectCell(cell);
        EndCapture($"已添加 {name}。拖它换位置，拖右下角小方块改大小。");
        ApplyStructureAndSave();
    }

    private void AddCommonButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommonKeyComboBox.SelectedItem is ComboBoxItem item && item.Tag is int code)
        {
            AddKey(code);
        }
    }

    private void DeleteCellButton_Click(object sender, RoutedEventArgs e) => DeleteCell(_selected);

    /// <summary>删掉一个格子 —— 顶部的按钮和画布上的右键菜单都走这里。</summary>
    private void DeleteCell(KeyCell? cell)
    {
        if (cell is null || !_settings.Keys.Remove(cell))
        {
            return;
        }

        if (ReferenceEquals(_selected, cell))
        {
            _selected = null;
        }

        RebuildCanvas();
        UpdateSelectionPanel();
        ApplyStructureAndSave();
    }

    /// <summary>画布上格子的右键菜单。目前就一项：删除。</summary>
    private ContextMenu BuildCellMenu(KeyCell cell)
    {
        var menu = new ContextMenu
        {
            // ContextMenu 住在弹出层里，取不到页面解析出来的主题资源，颜色只好显式给一份
            Background = ResolveBrush("Surface2Brush", Brushes.WhiteSmoke),
            Foreground = ResolveBrush("TextPrimaryBrush", Brushes.Black),
            BorderBrush = ResolveBrush("BorderStrongBrush", Brushes.Gray),
        };

        var label = string.IsNullOrWhiteSpace(cell.DisplayText)
            ? VirtualKeys.Name(cell.VirtualKey)
            : cell.DisplayText;

        var delete = new MenuItem { Header = "删除这个键（" + label + "）" };
        delete.Click += (_, _) => DeleteCell(cell);
        menu.Items.Add(delete);

        // 右键的时候顺带把它选中，视图上能看出删的是哪一个
        menu.Opened += (_, _) => SelectCell(cell);

        return menu;
    }

    private Brush ResolveBrush(string key, Brush fallback)
    {
        try
        {
            if (TryFindResource(key) is Brush brush)
            {
                return brush;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private void DeselectButton_Click(object sender, RoutedEventArgs e) => SelectCell(null);

    /// <summary>恢复默认设置。不可逆，所以先问一句。</summary>
    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "要把所有设置恢复到默认吗？\n\n按键布局、外观、果冻这些都会回到刚装上时的样子，现在的设置不会保留。",
            "恢复默认设置", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _plugin.ResetToDefaults();
        ReloadFromSettings();
    }

    /// <summary>设置被整体换掉之后（重置），把所有控件重新填一遍。</summary>
    private void ReloadFromSettings()
    {
        _selected = null;

        var wasLoading = _loading;
        _loading = true;
        try
        {
            LoadFromSettings();
        }
        finally
        {
            _loading = wasLoading;
        }

        UpdateValueLabels();
        UpdateCanvasGrid();
        RebuildCanvas();
        UpdateSelectionPanel();
        RefreshColorPreviews();
        UpdateCanvasHint();
    }

    /// <summary>显示 CPS 只对鼠标左/右键有意义，所以这个开关也只在选中它们时露出来。</summary>
    private void CellShowCps_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null)
        {
            return;
        }

        _selected.ShowCps = CellShowCpsCheckBox.IsChecked == true;

        // 结构变了（格子里要多一行字），得重建悬浮窗而不是只刷外观
        _plugin.OnSettingsChanged();
        ScheduleSave();
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Keys.Count == 0)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"要清空全部 {_settings.Keys.Count} 个按键格子吗？",
            "按键显示", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _settings.Keys.Clear();
        _selected = null;
        RebuildCanvas();
        UpdateSelectionPanel();
        ApplyStructureAndSave();
    }

    // ===================== 值变化统一入口 =====================

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var s = _settings;

        s.Enabled = EnabledCheckBox.IsChecked == true;
        s.ClickThrough = ClickThroughCheckBox.IsChecked == true;
        s.SnapToGrid = SnapCheckBox.IsChecked == true;
        s.ShadowEnabled = ShadowEnabledCheckBox.IsChecked == true;
        s.DynamicEnabled = DynamicCheckBox.IsChecked == true;
        s.DynamicForce = DynamicScaleSlider.Value;

        var typedFont = (FontComboBox.Text ?? "").Trim();
        if (typedFont.Length > 0)
        {
            s.FontFamily = typedFont;
        }

        s.FontSize = FontSizeSlider.Value;
        s.CornerRadius = CornerSlider.Value;
        s.BorderThickness = BorderThicknessSlider.Value;
        s.ShadowBlur = ShadowBlurSlider.Value;
        s.ShadowOffset = ShadowOffsetSlider.Value;
        s.ShadowOpacity = ShadowOpacitySlider.Value;
        s.ShadowDirection = ShadowDirectionSlider.Value;
        s.GridSize = GridSizeSlider.Value;

        if (RefreshComboBox.SelectedItem is ComboBoxItem item && item.Tag is int ms)
        {
            s.RefreshMs = ms;
        }

        UpdateValueLabels();
        UpdateCanvasGrid();            // 网格大小可能刚变，底纹要跟着重画
        RefreshAllCanvasLooks();       // 只改属性、不重建元素 —— 拖滑块才跟手
        _plugin.ApplyAppearanceNow();  // 悬浮窗外观立即生效

        // 显示开关 / 鼠标穿透 / 刷新率不属于"外观"，得单独同步 ——
        // 早先只调了上面那条轻路径，结果这两个开关关了没反应
        _plugin.SyncWindowState();

        ScheduleSave();                // 只有「写盘」这一件事被攒起来
    }

    private void BoldButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Bold = !_settings.Bold;
        RefreshColorPreviews();
        RefreshAllCanvasLooks();
        _plugin.ApplyAppearanceNow();
        ScheduleSave();
    }

    private void UpdateValueLabels()
    {
        FontSizeText.Text = ((int)Math.Round(FontSizeSlider.Value)) + " px";
        CornerText.Text = ((int)Math.Round(CornerSlider.Value)).ToString();
        BorderThicknessText.Text = BorderThicknessSlider.Value.ToString("0.#");
        ShadowBlurText.Text = ((int)Math.Round(ShadowBlurSlider.Value)).ToString();
        ShadowOffsetText.Text = ShadowOffsetSlider.Value.ToString("0.#");
        ShadowOpacityText.Text = ShadowOpacitySlider.Value.ToString("0.00");
        ShadowDirectionText.Text = ((int)Math.Round(ShadowDirectionSlider.Value)) + "°";
        GridSizeText.Text = ((int)Math.Round(GridSizeSlider.Value)) + " px";
        DynamicScaleText.Text = ((int)Math.Round(DynamicScaleSlider.Value)) + "%";

        // 没开果冻就别占地方，力度滑块跟着藏起来
        DynamicForcePanel.Visibility = DynamicCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ===================== 颜色 =====================

    /// <summary>
    /// 点色块 → 弹宿主同款调色盘（系统 ColorDialog）。
    /// ⚠ 它只管 RGB：调色盘不支持 alpha，透明度用旁边那条独立滑块。
    /// </summary>
    private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string field)
        {
            return;
        }

        var baseHex = ResolveBaseColor(field);
        var picked = ColorPickerWindow.Pick(Window.GetWindow(this), baseHex);

        var before = MakeColor(baseHex, Colors.White);
        var after = MakeColor(picked, before);

        // 调色盘返回的颜色 A 恒为 255，直接存下去会把原来的透明度抹掉 —— 这里只取它的 RGB
        SetColorField(field, $"#{before.A:X2}{after.R:X2}{after.G:X2}{after.B:X2}");

        AfterColorChanged();
    }

    /// <summary>
    /// 透明度滑块（0~100）→ 改对应颜色的 A 通道。
    ///
    /// 为什么透明度要单独一条滑块：系统调色盘不支持 alpha。宿主自己做颜色设置时也是
    /// 「调色盘选色 + 独立不透明度滑块」这个组合，照着它的做法来最一致。
    /// </summary>
    private void ColorAlpha_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not Slider slider || slider.Tag is not string field)
        {
            return;
        }

        var alpha = (byte)Math.Clamp(Math.Round(slider.Value * 2.55), 0, 255);
        var color = MakeColor(ResolveBaseColor(field), Colors.White);

        SetColorField(field, $"#{alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}");
        AfterColorChanged();
    }

    /// <summary>
    /// 局部外观里的三个下拉框：字体（第一项是「跟随全局」）、粗体、阴影开关。
    /// 后两个是三态 —— 跟随 / 开 / 关，索引 0/1/2，对应设置里的 -1/1/0。
    /// </summary>
    private void FillCellLookCombos()
    {
        CellFontComboBox.Items.Add("（跟随全局）");

        foreach (var name in FontComboBox.Items)
        {
            CellFontComboBox.Items.Add(name);
        }

        CellFontComboBox.SelectedIndex = 0;

        foreach (var combo in new[] { CellBoldComboBox, CellShadowEnabledComboBox })
        {
            combo.Items.Add("跟随全局");
            combo.Items.Add("开");
            combo.Items.Add("关");
            combo.SelectedIndex = 0;
        }
    }

    /// <summary>局部外观的下拉框（字体 / 三态开关）。</summary>
    private void CellLookup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null || sender is not ComboBox combo || combo.Tag is not string field)
        {
            return;
        }

        var cell = _selected;

        switch (field)
        {
            case "CellFont":
                // 第 0 项是「跟随全局」
                cell.FontFamilyOverride = combo.SelectedIndex <= 0
                    ? ""
                    : (combo.SelectedItem as string ?? "");
                break;

            case "CellBold":
                cell.BoldOverride = combo.SelectedIndex switch { 1 => 1, 2 => 0, _ => -1 };
                break;

            case "CellShadowEnabled":
                cell.ShadowEnabledOverride = combo.SelectedIndex switch { 1 => 1, 2 => 0, _ => -1 };
                break;
        }

        AfterCellLookChanged();
    }

    /// <summary>局部外观的数值滑块 —— 拖一下就算设了这个键的覆盖。</summary>
    private void CellNumber_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected is null || sender is not Slider slider || slider.Tag is not string field)
        {
            return;
        }

        var cell = _selected;
        var value = slider.Value;

        switch (field)
        {
            case "CellFontSize": cell.FontSizeOverride = value; break;
            case "CellCorner": cell.CornerRadiusOverride = value; break;
            case "CellBorderThickness": cell.BorderThicknessOverride = value; break;
            case "CellShadowBlur": cell.ShadowBlurOverride = value; break;
            case "CellShadowOffset": cell.ShadowOffsetOverride = value; break;
            case "CellShadowOpacity": cell.ShadowOpacityOverride = value; break;
            case "CellShadowDirection": cell.ShadowDirectionOverride = value; break;
        }

        AfterCellLookChanged();
    }

    /// <summary>清除某一项局部覆盖，恢复跟随全局。</summary>
    private void CellOverrideClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string field)
        {
            return;
        }

        if (_selected is not KeyCell cell)
        {
            return;
        }

        switch (field)
        {
            case "CellFont": cell.FontFamilyOverride = ""; break;
            case "CellFontSize": cell.FontSizeOverride = -1; break;
            case "CellBold": cell.BoldOverride = -1; break;
            case "CellCorner": cell.CornerRadiusOverride = -1; break;
            case "CellBorderThickness": cell.BorderThicknessOverride = -1; break;
            case "CellBorderColor": cell.BorderColorOverride = ""; break;
            case "CellShadowEnabled": cell.ShadowEnabledOverride = -1; break;
            case "CellShadowColor": cell.ShadowColorOverride = ""; break;
            case "CellShadowBlur": cell.ShadowBlurOverride = -1; break;
            case "CellShadowOffset": cell.ShadowOffsetOverride = -1; break;
            case "CellShadowOpacity": cell.ShadowOpacityOverride = -1; break;
            case "CellShadowDirection": cell.ShadowDirectionOverride = -1; break;
        }

        RefreshColorPreviews();
        AfterCellLookChanged();
    }

    /// <summary>局部外观改完：画布预览和悬浮窗都立刻跟上（拖滑块是高频操作，只有写盘攒一下）。</summary>
    private void AfterCellLookChanged()
    {
        RefreshAllCanvasLooks();
        RefreshCellLookControls();
        _plugin.ApplyAppearanceNow();
        ScheduleSave();
    }

    /// <summary>
    /// 把选中格子的局部覆盖值填回属性区那排控件。
    /// 没设覆盖的项显示的是**当前生效的全局值** —— 这样一眼能看出「现在长什么样」。
    /// </summary>
    private void RefreshCellLookControls()
    {
        var cell = _selected;

        var wasLoading = _loading;
        _loading = true;
        try
        {
            var fontIndex = 0;
            if (cell is not null && !string.IsNullOrWhiteSpace(cell.FontFamilyOverride))
            {
                var found = CellFontComboBox.Items.IndexOf(cell.FontFamilyOverride);
                fontIndex = found >= 0 ? found : 0;
            }

            CellFontComboBox.SelectedIndex = fontIndex;

            CellBoldComboBox.SelectedIndex = TriStateIndex(cell?.BoldOverride ?? -1);
            CellShadowEnabledComboBox.SelectedIndex = TriStateIndex(cell?.ShadowEnabledOverride ?? -1);

            SetCellSlider(CellFontSizeSlider, CellFontSizeText, cell?.FontSizeOverride ?? -1, _settings.FontSize, true);
            SetCellSlider(CellCornerSlider, CellCornerText, cell?.CornerRadiusOverride ?? -1, _settings.CornerRadius, false);
            SetCellSlider(CellBorderThicknessSlider, CellBorderThicknessText, cell?.BorderThicknessOverride ?? -1, _settings.BorderThickness, true);
            SetCellSlider(CellShadowBlurSlider, CellShadowBlurText, cell?.ShadowBlurOverride ?? -1, _settings.ShadowBlur, false);
            SetCellSlider(CellShadowOffsetSlider, CellShadowOffsetText, cell?.ShadowOffsetOverride ?? -1, _settings.ShadowOffset, true);
            SetCellSlider(CellShadowOpacitySlider, CellShadowOpacityText, cell?.ShadowOpacityOverride ?? -1, _settings.ShadowOpacity, true);
            SetCellSlider(CellShadowDirectionSlider, CellShadowDirectionText, cell?.ShadowDirectionOverride ?? -1, _settings.ShadowDirection, false);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>三态下拉的索引：0 跟随 / 1 开 / 2 关。</summary>
    private static int TriStateIndex(int value) => value < 0 ? 0 : (value == 1 ? 1 : 2);

    /// <summary>
    /// 把生效值填进局部外观的滑块。覆盖值无效（&lt; 0）时显示全局值 —— 语义是"现在就是全局那个值"。
    /// </summary>
    private void SetCellSlider(Slider slider, TextBlock text, double overrideValue, double globalValue, bool allowHalf)
    {
        var effective = overrideValue < 0 ? globalValue : overrideValue;

        var wasLoading = _loading;
        _loading = true;
        try
        {
            slider.Value = Math.Clamp(effective, slider.Minimum, slider.Maximum);
        }
        finally
        {
            _loading = wasLoading;
        }

        text.Text = allowHalf ? effective.ToString("0.#") : ((int)Math.Round(effective)).ToString();
    }

    /// <summary>取某个颜色字段的当前值。覆盖项留空时用全局对应值兜底 ——
    /// 否则调色盘一打开是白的、拖透明度滑块也没有基准色。</summary>
    private string ResolveBaseColor(string field)
    {
        var current = GetColorField(field);
        if (!string.IsNullOrWhiteSpace(current))
        {
            return current;
        }

        return field switch
        {
            "CellText" => _settings.IdleTextColor,
            "CellBg" => _settings.IdleBackgroundColor,
            "CellPressedText" => _settings.PressedTextColor,
            "CellPressedBg" => _settings.PressedBackgroundColor,
            "CellBorderColor" => _settings.BorderColor,
            "CellShadowColor" => _settings.ShadowColor,
            _ => "#FFFFFFFF",
        };
    }

    private void ColorClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string field)
        {
            return;
        }

        SetColorField(field, "");
        AfterColorChanged();
    }

    private void AfterColorChanged()
    {
        RefreshColorPreviews();
        RefreshAllCanvasLooks();
        _plugin.ApplyAppearanceNow();
        ScheduleSave();
    }

    private string GetColorField(string field)
    {
        var s = _settings;
        var cell = _selected;

        return field switch
        {
            "IdleText" => s.IdleTextColor,
            "IdleBg" => s.IdleBackgroundColor,
            "PressedText" => s.PressedTextColor,
            "PressedBg" => s.PressedBackgroundColor,
            "Border" => s.BorderColor,
            "Shadow" => s.ShadowColor,
            "CellText" => cell?.TextColorOverride ?? "",
            "CellBg" => cell?.BackgroundColorOverride ?? "",
            "CellPressedText" => cell?.PressedTextColorOverride ?? "",
            "CellPressedBg" => cell?.PressedBackgroundColorOverride ?? "",
            "CellBorderColor" => cell?.BorderColorOverride ?? "",
            "CellShadowColor" => cell?.ShadowColorOverride ?? "",
            _ => "#FFFFFFFF",
        };
    }

    private void SetColorField(string field, string value)
    {
        var s = _settings;
        var cell = _selected;

        switch (field)
        {
            case "IdleText": s.IdleTextColor = value; break;
            case "IdleBg": s.IdleBackgroundColor = value; break;
            case "PressedText": s.PressedTextColor = value; break;
            case "PressedBg": s.PressedBackgroundColor = value; break;
            case "Border": s.BorderColor = value; break;
            case "Shadow": s.ShadowColor = value; break;
            case "CellText": if (cell is not null) cell.TextColorOverride = value; break;
            case "CellBg": if (cell is not null) cell.BackgroundColorOverride = value; break;
            case "CellPressedText": if (cell is not null) cell.PressedTextColorOverride = value; break;
            case "CellPressedBg": if (cell is not null) cell.PressedBackgroundColorOverride = value; break;
            case "CellBorderColor": if (cell is not null) cell.BorderColorOverride = value; break;
            case "CellShadowColor": if (cell is not null) cell.ShadowColorOverride = value; break;
        }
    }

    private void RefreshColorPreviews()
    {
        var s = _settings;
        var cell = _selected;

        SetPreview(IdleTextColorPreview, IdleTextColorHexText, s.IdleTextColor);
        SetPreview(IdleBgColorPreview, IdleBgColorHexText, s.IdleBackgroundColor);
        SetPreview(PressedTextColorPreview, PressedTextColorHexText, s.PressedTextColor);
        SetPreview(PressedBgColorPreview, PressedBgColorHexText, s.PressedBackgroundColor);
        SetPreview(BorderColorPreview, BorderColorHexText, s.BorderColor);
        SetPreview(ShadowColorPreview, ShadowColorHexText, s.ShadowColor);

        SetAlphaSlider(IdleTextAlphaSlider, IdleTextAlphaText, s.IdleTextColor);
        SetAlphaSlider(IdleBgAlphaSlider, IdleBgAlphaText, s.IdleBackgroundColor);
        SetAlphaSlider(PressedTextAlphaSlider, PressedTextAlphaText, s.PressedTextColor);
        SetAlphaSlider(PressedBgAlphaSlider, PressedBgAlphaText, s.PressedBackgroundColor);
        SetAlphaSlider(BorderAlphaSlider, BorderAlphaText, s.BorderColor);

        // 覆盖项的色块留空时显示「跟随全局」，但透明度滑块显示实际生效的值（用全局色兜底）
        SetPreview(CellTextColorPreview, CellTextColorHexText, cell?.TextColorOverride ?? "");
        SetPreview(CellBgColorPreview, CellBgColorHexText, cell?.BackgroundColorOverride ?? "");
        SetPreview(CellPressedTextColorPreview, CellPressedTextColorHexText, cell?.PressedTextColorOverride ?? "");
        SetPreview(CellPressedBgColorPreview, CellPressedBgColorHexText, cell?.PressedBackgroundColorOverride ?? "");

        SetAlphaSlider(CellTextAlphaSlider, CellTextAlphaText, ResolveBaseColor("CellText"));
        SetAlphaSlider(CellBgAlphaSlider, CellBgAlphaText, ResolveBaseColor("CellBg"));
        SetAlphaSlider(CellPressedTextAlphaSlider, CellPressedTextAlphaText, ResolveBaseColor("CellPressedText"));
        SetAlphaSlider(CellPressedBgAlphaSlider, CellPressedBgAlphaText, ResolveBaseColor("CellPressedBg"));

        // 局部外观里的两个颜色
        SetPreview(CellBorderColorPreview, CellBorderColorHexText, cell?.BorderColorOverride ?? "");
        SetPreview(CellShadowColorPreview, CellShadowColorHexText, cell?.ShadowColorOverride ?? "");
        SetAlphaSlider(CellBorderAlphaSlider, CellBorderAlphaText, ResolveBaseColor("CellBorderColor"));

        BoldButton.Content = s.Bold ? "粗体：开" : "粗体：关";
    }

    /// <summary>
    /// 把颜色里的 A 通道换算成 0~100 填进滑块和百分比文字。
    /// 设滑块值会触发 ValueChanged，所以这段期间把 _loading 闸门关上，免得回写一遍配置。
    /// </summary>
    private void SetAlphaSlider(Slider slider, TextBlock text, string hex)
    {
        var percent = (int)Math.Clamp(Math.Round(MakeColor(hex, Colors.White).A / 2.55), 0, 100);

        var wasLoading = _loading;
        _loading = true;
        try
        {
            slider.Value = percent;
        }
        finally
        {
            _loading = wasLoading;
        }

        text.Text = percent + "%";
    }

    /// <summary>色块 + 十六进制文字。空值就显示成「跟随全局」并把色块画成棋盘感的中性色。</summary>
    private static void SetPreview(Border swatch, TextBlock? hexText, string value)
    {
        var empty = string.IsNullOrWhiteSpace(value);

        if (hexText is not null)
        {
            hexText.Text = empty ? "跟随全局" : value;
        }

        swatch.Background = empty ? Brushes.Transparent : MakeBrush(value, Colors.Transparent);
    }

    private void UpdateCanvasHint()
    {
        try
        {
            var count = _settings.Keys.Count;
            var snap = _settings.SnapToGrid ? $"吸附网格 {_settings.GridSize:0} px" : "不吸附";
            CanvasHintText.Text = $"共 {count} 个格子 · {snap} · 画布上的亮暗就是真实的按下状态（在这里按一下就能试）";
        }
        catch
        {
        }
    }

    // ===================== 提交 =====================

    /// <summary>
    /// 只把「写盘」攒一下（200 毫秒内的多次改动合并成一次写）。
    /// 外观是**立即**应用的 —— 早先版本连应用一起攒，拖滑块要等 150 毫秒悬浮窗才动，手感很迟钝。
    /// </summary>
    private void ScheduleSave()
    {
        _commitTimer?.Stop();
        _commitTimer?.Start();
    }

    /// <summary>结构变了（增删键、改格子位置和大小）：重建悬浮窗，然后存盘。</summary>
    private void ApplyStructureAndSave()
    {
        _plugin.OnSettingsChanged();
        _plugin.SaveSettings();
        UpdateCanvasHint();
    }

    private static Brush MakeBrush(string? hex, Color fallback) => new SolidColorBrush(MakeColor(hex, fallback));

    private static Color MakeColor(string? hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) &&
                ColorConverter.ConvertFromString(hex) is Color color)
            {
                return color;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static string Pick(string? over, string fallback) =>
        string.IsNullOrWhiteSpace(over) ? fallback : over!;

    private void ComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox &&
            comboBox.Template.FindName("PART_Popup", comboBox) is System.Windows.Controls.Primitives.Popup popup)
        {
            popup.PlacementTarget = comboBox;
            popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        }
    }
}
