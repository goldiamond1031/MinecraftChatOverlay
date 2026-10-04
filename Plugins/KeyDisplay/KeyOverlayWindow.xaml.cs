using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using FontFamily = System.Windows.Media.FontFamily;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace MinecraftChatOverlay.Plugins.KeyDisplay;

/// <summary>
/// 键位悬浮窗：无边框、透明、置顶、不抢焦点、不占任务栏。
/// 里面是一堆自由摆放的格子（Canvas 定位），每个格子一个 Border + TextBlock。
///
/// 两条路径分得很清楚，别混：
///   <see cref="Rebuild"/> —— 结构变了才走（增删键、改位置/尺寸/字体/圆角/阴影），重建整棵树；
///   <see cref="Refresh"/> —— 每帧走，只比对按下状态，**变了才换画笔**。
/// 刷新是 30fps 级别的，绝不能每帧重建视觉树。
/// </summary>
public partial class KeyOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")] private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int index, IntPtr value);

    /// <summary>一个格子的视觉元素 + 上一次的按下状态。</summary>
    private sealed class CellVisual
    {
        public Border Root = null!;
        public TextBlock Text = null!;

        /// <summary>显示 CPS 的格子才有这一行（鼠标左/右键）。</summary>
        public TextBlock? CpsText;

        public ScaleTransform Scale = null!;
        public KeyCell Model = null!;
        public bool IsDown;
    }

    /// <summary>鼠标左键 / 右键 —— 只有这两个的 CPS 才有意义。</summary>
    private static bool IsMouseButton(int virtualKey) => virtualKey is 0x01 or 0x02;

    private readonly List<CellVisual> _cells = new();

    /// <summary>
    /// 彩色循环走到哪了（0~1 表示绕色环一圈的比例）。
    ///
    /// 由插件侧按真实时间推进后塞进来（<see cref="SetCycleProgress"/>），窗口只负责按它算颜色。
    /// 为什么不在窗口里自己计时：循环是"所有开了循环的颜色一起转"，
    /// 各处各自计时会互相跑偏、看着像不同步。
    /// </summary>
    private double _cycleProgress;

    /// <summary>给阴影留的余量（窗口边缘会裁掉超出的效果）。</summary>
    private double _shadowPadding;

    private bool _positioned;

    // ---- 自绘拖动 ----
    //
    // 为什么不用系统 DragMove()：它允许把窗口拖出屏幕外任意远，松手就停在那儿，
    // 出界的位置还会被 LocationChanged 存进配置 —— 重启才被拉回来。
    // 自绘拖动能在移动的每一帧做钳制，窗口物理上出不了屏幕。
    // 手感和 DragMove 一致：都是「鼠标相对窗口的抓取点保持不变」。

    /// <summary>正在拖动。</summary>
    private bool _dragging;

    /// <summary>按下时鼠标在窗口内的坐标（DIP）。增量公式：Left += (当前窗口内X - 抓取点X)。</summary>
    private double _dragGrabX;

    /// <summary>按下时鼠标在窗口内的 Y。</summary>
    private double _dragGrabY;

    public KeyOverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>鼠标穿透。开着时点不到这个窗口（按键显示盖在游戏上必须默认开着）。</summary>
    public bool ClickThrough { get; set; }

    /// <summary>
    /// 防出屏开关。关了之后 <see cref="ClampToVirtualScreen"/> 直接跳过 ——
    /// 用户想把窗口一半藏在屏幕外就随他去（他自己的选择）。
    /// 默认 true，由插件从设置同步过来（<see cref="KeyDisplaySettings.ClampToScreen"/>）。
    /// </summary>
    public bool ClampEnabled { get; set; } = true;

    // ===================== 重建 =====================

    /// <summary>按设置重建整棵树。增删键、改外观都会走这里。</summary>
    public void Rebuild(KeyDisplaySettings settings)
    {
        RootCanvas.Children.Clear();
        _cells.Clear();

        // 整体缩放。格子数据本身不动，这里只是每次重建时乘一遍 —— 滑块来回拖不会累积误差
        var overall = OverallScaleOf(settings);

        // 窗口要给阴影和果冻过冲留余量（按所有格子里最费空间的那个算，含各自的覆盖）
        _shadowPadding = PaddingOf(settings);
        RootCanvas.Margin = new Thickness(_shadowPadding);

        foreach (var cell in settings.Keys)
        {
            // 每个格子的外观单独解析：自己覆盖了就用覆盖，没覆盖就用全局
            var look = ResolveLook(cell, settings, CycleProgressFor(settings));

            var text = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(cell.DisplayText)
                    ? VirtualKeys.Name(cell.VirtualKey)
                    : cell.DisplayText,
                FontFamily = look.Font,
                FontSize = look.FontSize * overall,
                FontWeight = look.Weight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,

                // 拖动交给外面的 Border，别让文字把鼠标事件吃掉
                IsHitTestVisible = false,
            };

            // 鼠标键可以顺便在下面多显示一行 CPS
            TextBlock? cpsText = null;
            FrameworkElement content = text;

            if (cell.ShowCps && IsMouseButton(cell.VirtualKey))
            {
                cpsText = new TextBlock
                {
                    Text = "0",
                    FontFamily = look.Font,
                    FontSize = Math.Max(8, look.FontSize * 0.62 * overall),
                    FontWeight = look.Weight,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false,
                };

                content = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Children = { text, cpsText },
                };
            }

            // 果冻效果走 RenderTransform 缩放（不触碰布局），原点设在格子中心。
            // 注意这跟上面的「整体缩放」是两码事：这个是按下时的动画，那个是静态显示尺寸。
            var popScale = new ScaleTransform(1, 1);

            var border = new Border
            {
                Width = Math.Max(8, cell.Width * overall),
                Height = Math.Max(8, cell.Height * overall),

                // 圆角、边框、阴影的体积感也要跟着整体缩放，
                // 不然放大后圆角显小、边框显细、阴影显浅，看着就不像"同一个东西变大了"
                CornerRadius = new CornerRadius(look.Corner.TopLeft * overall,
                                               look.Corner.TopRight * overall,
                                               look.Corner.BottomRight * overall,
                                               look.Corner.BottomLeft * overall),
                BorderThickness = new Thickness(look.BorderThickness.Left * overall,
                                                look.BorderThickness.Top * overall,
                                                look.BorderThickness.Right * overall,
                                                look.BorderThickness.Bottom * overall),
                BorderBrush = look.BorderBrush,
                Effect = ScaleShadow(look.Shadow, overall),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = popScale,
                Child = content,
            };

            Canvas.SetLeft(border, cell.X * overall);
            Canvas.SetTop(border, cell.Y * overall);
            RootCanvas.Children.Add(border);

            var visual = new CellVisual
            {
                Root = border,
                Text = text,
                CpsText = cpsText,
                Scale = popScale,
                Model = cell,
                IsDown = false,
            };
            ApplyColors(visual, settings);
            _cells.Add(visual);
        }

        ResizeToContent(settings);
        SyncPositionToContent(settings);
    }

    /// <summary>
    /// 只刷**外观**（字体、字号、粗体、圆角、边框、阴影、颜色），不重建元素、不动位置尺寸。
    ///
    /// 为什么单独开这条路径：页面上的滑块一拖起来一秒会调几十次。走 <see cref="Rebuild"/> 的话
    /// 每次都清空重造整棵视觉树，手感立刻变成幻灯片。这条路径只改属性，开销可以忽略。
    /// </summary>
    public void ApplyAppearance(KeyDisplaySettings settings)
    {
        // 整体缩放也在这条路径上生效 —— 它虽然改的是"尺寸"，但格子宽高在 Rebuild 时已经乘过了，
        // 这里只负责把字号/圆角/边框/阴影跟上。不乘的话拖一下别的滑块就会把整体缩放抹平。
        var overall = OverallScaleOf(settings);

        // 阴影 / 果冻幅度变了，窗口要留的余量也跟着变，否则会被边缘切掉
        var padding = PaddingOf(settings);

        if (Math.Abs(padding - _shadowPadding) > 0.01)
        {
            _shadowPadding = padding;
            RootCanvas.Margin = new Thickness(_shadowPadding);
            ResizeToContent(settings);
            SyncPositionToContent(settings);
        }

        foreach (var visual in _cells)
        {
            // 每个格子重新解析一遍（局部覆盖可能刚改过）
            var look = ResolveLook(visual.Model, settings, CycleProgressFor(settings));

            visual.Text.FontFamily = look.Font;
            visual.Text.FontSize = look.FontSize * overall;
            visual.Text.FontWeight = look.Weight;

            if (visual.CpsText is not null)
            {
                visual.CpsText.FontFamily = look.Font;
                visual.CpsText.FontSize = Math.Max(8, look.FontSize * 0.62 * overall);
                visual.CpsText.FontWeight = look.Weight;
            }

            visual.Root.Width = Math.Max(8, visual.Model.Width * overall);
            visual.Root.Height = Math.Max(8, visual.Model.Height * overall);
            visual.Root.CornerRadius = new CornerRadius(look.Corner.TopLeft * overall,
                                                        look.Corner.TopRight * overall,
                                                        look.Corner.BottomRight * overall,
                                                        look.Corner.BottomLeft * overall);
            visual.Root.BorderThickness = new Thickness(look.BorderThickness.Left * overall,
                                                        look.BorderThickness.Top * overall,
                                                        look.BorderThickness.Right * overall,
                                                        look.BorderThickness.Bottom * overall);
            visual.Root.BorderBrush = look.BorderBrush;
            visual.Root.Effect = ScaleShadow(look.Shadow, overall);

            Canvas.SetLeft(visual.Root, visual.Model.X * overall);
            Canvas.SetTop(visual.Root, visual.Model.Y * overall);

            ApplyColors(visual, settings);
        }

        // 整体缩放（或字号）变了会改变内容包围盒，窗口要跟着重新贴合
        ResizeToContent(settings);
    }

    // ===================== 彩色循环 =====================

    /// <summary>
    /// 插件侧推进了循环进度之后调这里：只**重刷颜色**，不重建视觉树、不动布局。
    ///
    /// 为什么单独开这条：循环是每秒一次的高频刷新，走 <see cref="Rebuild"/> 会每秒把整棵树重造一遍。
    /// 走 <see cref="ApplyColors"/> 只是给几个元素换画笔，开销可以忽略。
    /// </summary>
    public void SetCycleProgress(double progress, KeyDisplaySettings settings)
    {
        _cycleProgress = progress;

        // 没有任何一处开着循环 → 不必白刷（进度也顺手归零，免得关掉再开时"跳"一下）
        if (!settings.AnyColorCycle)
        {
            return;
        }

        foreach (var visual in _cells)
        {
            ApplyColors(visual, settings);
        }
    }

    /// <summary>自检用：读第一个格子当前文字用的颜色（验彩色循环有没有真的接到画笔上）。</summary>
    internal Color? FirstTextColor() => TextColorAt(0);

    /// <summary>自检用：读第 <paramref name="index"/> 个格子当前**边框**用的颜色。</summary>
    internal Color? BorderColorAt(int index) =>
        (index >= 0 && index < _cells.Count
            ? _cells[index].Root.BorderBrush as SolidColorBrush
            : null)?.Color;

    /// <summary>自检用：读第 <paramref name="index"/> 个格子当前**阴影**用的颜色（没开阴影返回 null）。</summary>
    internal Color? ShadowColorAt(int index) =>
        index >= 0 && index < _cells.Count
            ? (_cells[index].Root.Effect as DropShadowEffect)?.Color
            : null;

    /// <summary>自检用：读第 <paramref name="index"/> 个格子当前文字用的颜色（验不同对象同不同步）。</summary>
    internal Color? TextColorAt(int index) =>
        (index >= 0 && index < _cells.Count
            ? _cells[index].Text.Foreground as SolidColorBrush
            : null)?.Color;

    /// <summary>一个格子解析完覆盖之后的有效外观。</summary>
    private sealed class CellLook
    {
        public FontFamily Font = null!;
        public double FontSize;
        public FontWeight Weight;
        public CornerRadius Corner;
        public Thickness BorderThickness;
        public Brush BorderBrush = null!;
        public DropShadowEffect? Shadow;

        /// <summary>边框颜色的**原始 hex**（没经过循环上色）—— 高频刷色路径要拿它重算。</summary>
        public string BorderColorHex = "";

        /// <summary>边框颜色循环开关（已解析过每键覆盖）。</summary>
        public bool BorderCycleOn;

        /// <summary>阴影颜色的**原始 hex**（没经过循环上色）。</summary>
        public string ShadowColorHex = "";

        /// <summary>阴影颜色循环开关（已解析过每键覆盖）。</summary>
        public bool ShadowCycleOn;
    }

    /// <summary>
    /// 把一个格子的外观解析出来：它自己设了覆盖就用覆盖，没设就用全局的。
    ///
    /// Rebuild 和 ApplyAppearance 都走这里 —— 免得两处各写一遍解析逻辑，日后改一处漏一处。
    /// </summary>
    /// <param name="cycleProgress">
    /// 彩色循环进度（0~1）。传 -1 表示"这次调用不算循环" —— 用于自检等需要拿到**原始颜色**的场合。
    /// </param>
    private static CellLook ResolveLook(KeyCell cell, KeyDisplaySettings settings, double cycleProgress)
    {
        var look = ResolveLookCore(cell, settings, cycleProgress);

        var borderBrush = CycleBrushOf(
            look.BorderColorHex, Colors.Transparent,
            look.BorderCycleOn,
            cycleProgress);

        var effectiveShadowColor = CycleColorOf(
            look.ShadowColorHex, Colors.Black,
            look.ShadowCycleOn,
            cycleProgress);

        look.BorderBrush = borderBrush;
        look.Shadow = look.Shadow
            is null
            ? null
            : new DropShadowEffect
            {
                Color = effectiveShadowColor,
                BlurRadius = look.Shadow.BlurRadius,
                Opacity = look.Shadow.Opacity,
                ShadowDepth = look.Shadow.ShadowDepth,
                Direction = look.Shadow.Direction,
            };

        return look;
    }

    /// <summary>
    /// 解析一个格子的外观，**但不算颜色循环**（边框色 / 阴影色原样带出、循环开关单独记着）。
    ///
    /// 为什么要把"颜色循环"从解析里拆出去：循环是每秒几十次的高频刷新，走 <see cref="ApplyColors"/>
    /// 那条轻路径只换画笔；如果颜色在解析时就定死了，高频路径就得整份重新解析，
    /// 而且——**边框和阴影会永远停在"解析那一刻"的颜色上**（这就是之前那个 bug：
    /// 打开开关时刷成一次光谱色，之后再没人更新它）。
    /// </summary>
    private static CellLook ResolveLookCore(KeyCell cell, KeyDisplaySettings settings, double cycleProgress)
    {
        var fontName = string.IsNullOrWhiteSpace(cell.FontFamilyOverride)
            ? settings.FontFamily
            : cell.FontFamilyOverride;

        var fontSize = cell.FontSizeOverride >= 0 ? cell.FontSizeOverride : settings.FontSize;
        var bold = cell.BoldOverride < 0 ? settings.Bold : cell.BoldOverride == 1;

        var corner = cell.CornerRadiusOverride < 0 ? settings.CornerRadius : cell.CornerRadiusOverride;
        var borderThickness = cell.BorderThicknessOverride < 0
            ? settings.BorderThickness
            : cell.BorderThicknessOverride;

        var borderColor = string.IsNullOrWhiteSpace(cell.BorderColorOverride)
            ? settings.BorderColor
            : cell.BorderColorOverride;

        var shadowEnabled = cell.ShadowEnabledOverride < 0
            ? settings.ShadowEnabled
            : cell.ShadowEnabledOverride == 1;

        var shadowColor = string.IsNullOrWhiteSpace(cell.ShadowColorOverride)
            ? settings.ShadowColor
            : cell.ShadowColorOverride;

        var shadowBlur = cell.ShadowBlurOverride < 0 ? settings.ShadowBlur : cell.ShadowBlurOverride;
        var shadowOffset = cell.ShadowOffsetOverride < 0 ? settings.ShadowOffset : cell.ShadowOffsetOverride;
        var shadowOpacity = cell.ShadowOpacityOverride < 0 ? settings.ShadowOpacity : cell.ShadowOpacityOverride;
        var shadowDirection = cell.ShadowDirectionOverride < 0 ? settings.ShadowDirection : cell.ShadowDirectionOverride;

        // 彩色循环：边框、阴影各自看自己的开关（每键覆盖优先于全局）
        var borderCycleOn = ResolveCycle(cell.BorderColorCycleOverride, settings.BorderColorCycle);
        var shadowCycleOn = ResolveCycle(cell.ShadowColorCycleOverride, settings.ShadowColorCycle);

        var borderBrush = CycleBrushOf(
            borderColor, Colors.Transparent,
            borderCycleOn,
            cycleProgress);

        var effectiveShadowColor = CycleColorOf(
            shadowColor, Colors.Black,
            shadowCycleOn,
            cycleProgress);

        return new CellLook
        {
            Font = new FontFamily(string.IsNullOrWhiteSpace(fontName) ? "Microsoft YaHei UI" : fontName),
            FontSize = Math.Clamp(fontSize, 6, 200),
            Weight = bold ? FontWeights.Bold : FontWeights.Normal,
            Corner = new CornerRadius(Math.Max(0, corner)),
            BorderThickness = new Thickness(Math.Max(0, borderThickness)),
            BorderBrush = borderBrush,
            BorderColorHex = borderColor,
            BorderCycleOn = borderCycleOn,
            ShadowColorHex = shadowColor,
            ShadowCycleOn = shadowCycleOn,
            Shadow = shadowEnabled
                ? new DropShadowEffect
                {
                    Color = effectiveShadowColor,
                    BlurRadius = Math.Max(0, shadowBlur),
                    Opacity = Math.Clamp(shadowOpacity, 0, 1),
                    ShadowDepth = Math.Max(0, shadowOffset),
                    Direction = ((shadowDirection % 360) + 360) % 360,
                }
                : null,
        };
    }

    /// <summary>
    /// 每键覆盖 vs 全局：覆盖设过就用覆盖，没设过（null）才看全局。
    /// 跟其它覆盖项的"空 / 负数 = 跟随全局"是同一套约定。
    /// </summary>
    private static bool ResolveCycle(bool? cellOverride, bool global) =>
        cellOverride ?? global;

    /// <summary>按循环开关决定要用的颜色：关着就是原色，开着就往色相上推一格。</summary>
    private static Color CycleColorOf(string hex, Color fallback, bool cycle, double progress)
    {
        var color = ParseColor(hex, fallback);

        if (!cycle || progress < 0)
        {
            return color;
        }

        return ColorCycle.Shift(color, progress);
    }

    /// <summary>
    /// 这次重建/刷外观时该用哪个循环进度。
    ///
    /// 只在**真有地方开着循环**时才把进度交出去 —— 否则拿原始颜色，
    /// 免得"用户关了循环但进度残留在某处"这种状态下颜色对不上。
    /// </summary>
    private double CycleProgressFor(KeyDisplaySettings settings) =>
        settings.AnyColorCycle ? _cycleProgress : -1;

    /// <summary>同上，直接给画笔（边框、文字这些要的是一次性 Brush）。</summary>
    private static Brush CycleBrushOf(string hex, Color fallback, bool cycle, double progress) =>
        new SolidColorBrush(CycleColorOf(hex, fallback, cycle, progress));

    /// <summary>非循环路径用的重载 —— 就是"这次不算循环"。</summary>
    private static CellLook ResolveLook(KeyCell cell, KeyDisplaySettings settings) =>
        ResolveLook(cell, settings, -1);

    /// <summary>
    /// 整体显示缩放，夹在 0.5 ~ 2.0。
    ///
    /// 下界 0.5 是防呆：再小字就看不清了，用户会以为插件坏了。
    /// 上界 2.0 是防溢出：窗口是按内容包围盒算的，放到 4 倍一个 46px 的格子就 184px，
    /// 几个格子铺开能占掉小半个屏幕，还容易盖住游戏里要紧的东西。
    /// </summary>
    internal static double OverallScaleOf(KeyDisplaySettings settings) =>
        Math.Clamp(settings.OverallScale <= 0 ? 1.0 : settings.OverallScale, 0.5, 2.0);

    /// <summary>
    /// 阴影跟着整体缩放。模糊和偏移按比例放大，不透明度不动
    /// （放大阴影的同时加深颜色会显得脏，跟 ModernControls 里"去掉 Effect 免得阴影显脏"是同一个考虑）。
    /// </summary>
    private static DropShadowEffect? ScaleShadow(DropShadowEffect? source, double scale)
    {
        if (source is null || Math.Abs(scale - 1.0) < 0.001)
        {
            return source;
        }

        return new DropShadowEffect
        {
            Color = source.Color,
            BlurRadius = source.BlurRadius * scale,
            ShadowDepth = source.ShadowDepth * scale,
            Direction = source.Direction,
            Opacity = source.Opacity,
            RenderingBias = source.RenderingBias,
        };
    }

    /// <summary>
    /// 窗口要给效果留多少余量。取所有格子（含各自的覆盖）里最费空间的那个，
    /// 再加上果冻过冲需要的量 —— 不然阴影或弹跳会被窗口边缘切掉。
    /// </summary>
    private static double PaddingOf(KeyDisplaySettings settings)
    {
        double maxCell = 0;
        double maxShadow = 0;

        // 留白是给"缩放后的格子"留的，所以也要乘整体缩放 —— 否则放大之后阴影会被窗口边缘切掉
        var overall = OverallScaleOf(settings);

        foreach (var cell in settings.Keys)
        {
            maxCell = Math.Max(maxCell, Math.Max(cell.Width, cell.Height) * overall);

            var enabled = cell.ShadowEnabledOverride < 0
                ? settings.ShadowEnabled
                : cell.ShadowEnabledOverride == 1;

            if (!enabled)
            {
                continue;
            }

            var blur = cell.ShadowBlurOverride < 0 ? settings.ShadowBlur : cell.ShadowBlurOverride;
            var offset = cell.ShadowOffsetOverride < 0 ? settings.ShadowOffset : cell.ShadowOffsetOverride;

            maxShadow = Math.Max(maxShadow, (Math.Max(0, blur) + Math.Max(0, offset) + 2) * overall);
        }

        // 果冻回弹会冲过原尺寸，按 25% 留（劲度拉到最低时振荡幅度相当大）
        var dynamic = settings.DynamicEnabled ? maxCell * 0.25 : 0;

        return Math.Max(maxShadow, dynamic);
    }

    /// <summary>窗口大小 = 内容包围盒（+ 阴影余量）。格子摆到哪，窗口就长到哪。</summary>
    private void ResizeToContent(KeyDisplaySettings settings)
    {
        double width = 0;
        double height = 0;

        // ⚠ 包围盒必须按**缩放后**的坐标算：格子是按 cell.X * overall 摆的，
        // 这里要是用原始值，放大后内容就会超出窗口边界、被窗口边缘裁掉
        // （表现是"放大之后有一圈像边框的东西把内容挡住"）。
        var overall = OverallScaleOf(settings);

        foreach (var cell in settings.Keys)
        {
            width = Math.Max(width, (cell.X + cell.Width) * overall);
            height = Math.Max(height, (cell.Y + cell.Height) * overall);
        }

        Width = Math.Max(8, width + _shadowPadding * 2);
        Height = Math.Max(8, height + _shadowPadding * 2);

        // 窗口尺寸变了，右/下缘可能出界（放大缩放、改阴影的典型场景）—— 钳回来
        ClampToVirtualScreen();
    }

    // ===================== 每帧刷新 =====================

    /// <summary>
    /// 帧刷新：只比对每个格子的按下状态，**翻转了才换画笔**。
    /// 返回 true 表示这一帧确实有格子变了状态（调试用）。
    /// </summary>
    public bool Refresh(KeyDisplaySettings settings)
    {
        var changed = false;

        foreach (var visual in _cells)
        {
            var down = VirtualKeys.IsDown(visual.Model.VirtualKey);
            if (down == visual.IsDown)
            {
                continue;
            }

            visual.IsDown = down;
            ApplyColors(visual, settings);

            // 按下压扁并**保持住**，松开才弹性回弹 —— 这才是果冻该有的行为
            if (settings.DynamicEnabled)
            {
                if (down)
                {
                    Squash(visual, settings);
                }
                else
                {
                    Release(visual, settings);
                }
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 按下：快速**整体缩小**并保持住（手指不松，格子就一直是被按下去的样子）。
    ///
    /// 是**等比**缩放（横竖同一个比例）—— 整个格子连着里面的字一起缩，
    /// 不是只压扁一个方向。
    ///
    /// 走 RenderTransform，完全不触碰布局：悬浮窗是 30~60fps 刷新的，
    /// 要是改 Width/Height 触发重排，按下的一瞬间会肉眼可见地卡一下。
    /// </summary>
    private void Squash(CellVisual visual, KeyDisplaySettings settings)
    {
        var force = Math.Clamp(settings.DynamicForce, 0, 100) / 100.0;
        if (force <= 0.01)
        {
            return;
        }

        var scale = 1 - (force * 0.38);   // 力度 100 时整个缩到 0.62

        LastPopTarget = scale;
        LastPopCurve = string.Format(
            "按下整体缩到 {0:0.00} 倍并保持 → 松开弹性回弹（振荡 {1} 次，劲度 {2:0.0}）",
            scale, OscillationsOf(force), SpringinessOf(force));

        void Start(DependencyProperty property)
        {
            visual.Scale.BeginAnimation(property, new DoubleAnimation
            {
                To = scale,
                Duration = new Duration(TimeSpan.FromMilliseconds(70)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },

                // 保持住 —— 松开之前一直是被按下去的样子
                FillBehavior = FillBehavior.HoldEnd,
            });
        }

        Start(ScaleTransform.ScaleXProperty);
        Start(ScaleTransform.ScaleYProperty);
    }

    /// <summary>
    /// 松开：从压扁状态**弹性回弹**到原尺寸。
    ///
    /// 果冻感全在这一下 —— <c>ElasticEase</c> 会冲过 1.0 再振荡衰减回来。
    /// （不指定 From：WPF 会自动从当前值也就是压扁后的值开始。）
    /// </summary>
    private void Release(CellVisual visual, KeyDisplaySettings settings)
    {
        var force = Math.Clamp(settings.DynamicForce, 0, 100) / 100.0;

        if (force <= 0.01)
        {
            // 没开力度就直接把动画摘掉，属性回落到基础值 1.0
            visual.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            visual.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }

        void Start(DependencyProperty property)
        {
            visual.Scale.BeginAnimation(property, new DoubleAnimation
            {
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(450)),
                EasingFunction = new ElasticEase
                {
                    Oscillations = OscillationsOf(force),
                    Springiness = SpringinessOf(force),
                    EasingMode = EasingMode.EaseOut,
                },

                // 播完把控制权还给基础值（1.0），免得动画一直占着属性
                FillBehavior = FillBehavior.Stop,
            });
        }

        Start(ScaleTransform.ScaleXProperty);
        Start(ScaleTransform.ScaleYProperty);
    }

    /// <summary>回弹时抖几下。力度越大抖得越多。</summary>
    private static int OscillationsOf(double force) => 2 + (int)Math.Round(force * 3);

    /// <summary>
    /// 弹簧劲度。<b>越小振荡幅度越大</b>（越有胶质感），越大则抖得快而碎。
    /// 所以这里是"力度越大、劲度越小" —— 跟他要的"果冻感拉满"是同一个方向。
    /// </summary>
    private static double SpringinessOf(double force) => 4 - force;

    /// <summary>
    /// 自检用：鼠标穿透现在是不是真的生效。
    /// 直接读 Win32 的扩展样式位 —— 这是最硬的证据（不是问我们自己记的状态）。
    /// </summary>
    internal bool IsClickThroughActive()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            return (GetWindowLong(handle, GwlExStyle) & WsExTransparent) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>自检用：内容左上角在屏幕上的位置（= 窗口位置 + 留白）。改留白时它应该纹丝不动。</summary>
    internal Point ContentOrigin => new Point(Left + _shadowPadding, Top + _shadowPadding);

    /// <summary>自检用：第一个格子的实际渲染尺寸。整体缩放改的就是它。</summary>
    internal Size FirstCellSize() =>
        _cells.Count > 0 ? new Size(_cells[0].Root.Width, _cells[0].Root.Height) : new Size(0, 0);

    /// <summary>自检用：某个键的格子现在生效的字号。</summary>
    internal double GetCellFontSize(int virtualKey)
    {
        foreach (var visual in _cells)
        {
            if (visual.Model.VirtualKey == virtualKey)
            {
                return visual.Text.FontSize;
            }
        }

        return 0;
    }

    /// <summary>自检用：某个键的 CPS 那一行现在显示什么（没这一行就返回空）。</summary>
    internal string GetCellCpsText(int virtualKey)
    {
        foreach (var visual in _cells)
        {
            if (visual.Model.VirtualKey == virtualKey)
            {
                return visual.CpsText?.Text ?? "";
            }
        }

        return "";
    }

    /// <summary>自检用：上一次果冻弹出的目标倍数（没弹过是 0）。</summary>
    internal double LastPopTarget { get; private set; }

    /// <summary>自检用：上一次果冻动画的曲线摘要，用来核对它确实是弹性曲线而不是随便一个动画。</summary>
    internal string LastPopCurve { get; private set; } = "";

    /// <summary>
    /// 自检用：某个键的格子现在挂着动画没有。
    ///
    /// 为什么不直接读 <c>ScaleX</c> 来验：WPF 动画是在 UI 线程的时钟上推进的，
    /// 自检里用 <c>Thread.Sleep</c> 等半程会把 UI 线程堵死，动画根本不前进 ——
    /// 读出来永远是 1.0，那是假阴性。挂没挂上动画就不受这个影响。
    /// </summary>
    internal bool IsPopping(int virtualKey)
    {
        foreach (var visual in _cells)
        {
            if (visual.Model.VirtualKey == virtualKey)
            {
                return visual.Scale.HasAnimatedProperties;
            }
        }

        return false;
    }

    /// <summary>把当前按下状态全部重置，强迫下一帧重刷一遍颜色（改完颜色设置要调）。</summary>
    public void InvalidateStates()
    {
        foreach (var visual in _cells)
        {
            visual.IsDown = false;
        }
    }

    private void ApplyColors(CellVisual visual, KeyDisplaySettings settings)
    {
        var cell = visual.Model;

        // 文字 / 背景各自的循环开关（每键覆盖优先于全局）
        var textCycle = ResolveCycle(
            visual.IsDown ? cell.PressedTextColorCycleOverride : cell.TextColorCycleOverride,
            visual.IsDown ? settings.PressedTextColorCycle : settings.TextColorCycle);

        var bgCycle = ResolveCycle(
            visual.IsDown ? cell.PressedBackgroundColorCycleOverride : cell.BackgroundColorCycleOverride,
            visual.IsDown ? settings.PressedBackgroundColorCycle : settings.BackgroundColorCycle);

        var textHex = visual.IsDown
            ? Pick(cell.PressedTextColorOverride, settings.PressedTextColor)
            : Pick(cell.TextColorOverride, settings.IdleTextColor);

        var backgroundHex = visual.IsDown
            ? Pick(cell.PressedBackgroundColorOverride, settings.PressedBackgroundColor)
            : Pick(cell.BackgroundColorOverride, settings.IdleBackgroundColor);

        var textBrush = new SolidColorBrush(CycleColorOf(textHex, Colors.White, textCycle, _cycleProgress));

        visual.Text.Foreground = textBrush;

        if (visual.CpsText is not null)
        {
            // CPS 那行稍淡一点，别跟主文字抢
            visual.CpsText.Foreground = textBrush;
            visual.CpsText.Opacity = 0.75;
        }

        visual.Root.Background = new SolidColorBrush(
            CycleColorOf(backgroundHex, Colors.Transparent, bgCycle, _cycleProgress));

        // ⚠ 边框和阴影也必须在这条路径上刷 —— 它们跟文字/背景一样是要跟着循环动的颜色。
        //
        // 这里曾经漏掉过：边框色和阴影色只在 Rebuild / ApplyAppearance 里算过一次，
        // 而彩色循环的高频路径走的是本方法（Rebuild 期间压根不会被调）。
        // 表现就是"打开开关后边框/阴影变成当时那个光谱色，然后固定不动"。
        // 留意 `AnyColorCycle` 只统计"有没有任何一处开着循环"，不代表本格子的边框/阴影开着；
        // 所以这里要各自再判一次开关，别让没开循环的格子白刷（也免得破坏它原来的颜色）。
        if (_cycleProgress >= 0)
        {
            var look = ResolveLookCore(cell, settings, -1);

            if (look.BorderCycleOn || visual.Root.BorderBrush is null)
            {
                visual.Root.BorderBrush = new SolidColorBrush(
                    CycleColorOf(look.BorderColorHex, Colors.Transparent, look.BorderCycleOn, _cycleProgress));
            }

            if (look.ShadowCycleOn)
            {
                visual.Root.Effect = ScaleShadow(BuildShadow(look, _cycleProgress), OverallScaleOf(settings));
            }
        }
    }

    /// <summary>
    /// 按解析结果（可选：带上循环换色）造一个阴影效果。
    /// 抽出来是为了让「重建时的静态阴影」和「循环时的高频重算」走同一套参数，不会两处跑偏。
    /// </summary>
    private static DropShadowEffect? BuildShadow(CellLook look, double cycleProgress)
    {
        if (look.Shadow is null)
        {
            return null;
        }

        var color = cycleProgress >= 0
            ? CycleColorOf(look.ShadowColorHex, Colors.Black, look.ShadowCycleOn, cycleProgress)
            : look.Shadow.Color;

        return new DropShadowEffect
        {
            Color = color,
            BlurRadius = look.Shadow.BlurRadius,
            Opacity = look.Shadow.Opacity,
            ShadowDepth = look.Shadow.ShadowDepth,
            Direction = look.Shadow.Direction,
        };
    }

    /// <summary>
    /// 刷新 CPS 显示。数值由插件统计好传进来 —— 窗口自己不做计量，
    /// 它只是个显示层，而且窗口不显示的时候也不该继续算。
    /// </summary>
    public void UpdateCps(int leftCps, int rightCps)
    {
        foreach (var visual in _cells)
        {
            if (visual.CpsText is null)
            {
                continue;
            }

            var value = visual.Model.VirtualKey switch
            {
                0x01 => leftCps,
                0x02 => rightCps,
                _ => 0,
            };

            var text = value.ToString();

            if (visual.CpsText.Text != text)
            {
                visual.CpsText.Text = text;
            }
        }
    }

    /// <summary>每键覆盖项留空就跟随全局。</summary>
    private static string Pick(string? over, string fallback) =>
        string.IsNullOrWhiteSpace(over) ? fallback : over!;

    // ===================== 位置 =====================

    /// <summary>按设置摆位。只在第一次摆（之后用户可能拖过）。</summary>
    public void ApplyPosition(KeyDisplaySettings settings, bool force = false)
    {
        if (_positioned && !force)
        {
            return;
        }

        // 存的 WindowLeft/Top 是**内容左上角**，不是窗口左上角 —— 窗口还得减掉留白
        Left = settings.WindowLeft - _shadowPadding;
        Top = settings.WindowTop - _shadowPadding;
        _positioned = true;

        // 装载摆位后钳一次 —— 防手改配置/换显示器之后位置在屏幕外
        ClampToVirtualScreen();
    }

    /// <summary>用户拖过窗口之后，把当前位置记回设置（记的是内容左上角）。</summary>
    public void RememberCurrentPosition(KeyDisplaySettings settings)
    {
        settings.WindowLeft = Left + _shadowPadding;
        settings.WindowTop = Top + _shadowPadding;
    }

    /// <summary>
    /// 把窗口位置钳进「所有显示器的联合范围」—— **防超出屏幕**的统一出口。
    ///
    /// 用 <c>SystemParameters.VirtualScreen</c>（全部屏幕的联合包围盒）而不是单屏 WorkArea：
    /// 多显示器下副屏也能去（联合盒自然覆盖），只拦「所有屏幕之外」。
    /// 这些值是 DIP 单位，跟窗口 Left/Top 同单位，不需要 DPI 换算。
    ///
    /// 调用时机（窗口位置/尺寸可能变了的出口都挂上）：
    ///   · 拖动的每一帧（OnMouseMove）；
    ///   · ResizeToContent 之后（放大缩放/改阴影 → 窗口变大 → 右/下缘出界）；
    ///   · ApplyPosition（装载摆位，防手改配置把位置写出界）；
    ///   · SyncPositionToContent（留白反向补偿 → 窗口原点出界）。
    ///
    /// ⚠ Math.Clamp 在 min&gt;max 时抛异常 —— 窗口比整个屏幕还大的极端情况要先判，
    ///   直接对齐到左上角。
    /// </summary>
    public void ClampToVirtualScreen()
    {
        // 开关关着就完全不动位置 —— "防出屏"是保护，不是强制
        if (!ClampEnabled)
        {
            return;
        }

        if (double.IsNaN(Left) || double.IsNaN(Top) ||
            double.IsNaN(ActualWidth) || double.IsNaN(ActualHeight))
        {
            return;
        }

        var vsLeft = SystemParameters.VirtualScreenLeft;
        var vsTop = SystemParameters.VirtualScreenTop;
        var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

        // 水平
        if (ActualWidth <= vsRight - vsLeft)
        {
            Left = Math.Clamp(Left, vsLeft, vsRight - ActualWidth);
        }
        else
        {
            // 窗口比所有屏幕加起来还宽 —— 对齐左缘（窗口比屏幕大时不存在"好位置"）
            Left = vsLeft;
        }

        // 垂直
        if (ActualHeight <= vsBottom - vsTop)
        {
            Top = Math.Clamp(Top, vsTop, vsBottom - ActualHeight);
        }
        else
        {
            Top = vsTop;
        }
    }

    /// <summary>
    /// 把窗口位置重新对齐到「内容左上角」。
    ///
    /// 为什么需要这个：悬浮窗尺寸 = 内容 + 留白，而留白是给阴影和果冻过冲留的、
    /// **会随着阴影大小变化**。如果窗口 Left/Top 不动、只有留白变了，内容在屏幕上就会跟着漂
    /// —— 这就是「改个阴影大小，按键就移位」的原因。
    /// 所以位置一律按内容左上角算：留白变多少，窗口位置反向补多少，内容看着纹丝不动。
    /// </summary>
    public void SyncPositionToContent(KeyDisplaySettings settings)
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        var left = settings.WindowLeft - _shadowPadding;
        var top = settings.WindowTop - _shadowPadding;

        if (Math.Abs(Left - left) > 0.01)
        {
            Left = left;
        }

        if (Math.Abs(Top - top) > 0.01)
        {
            Top = top;
        }

        // 留白反向补偿后窗口原点可能出界（内容贴屏幕左/上边缘时）—— 钳回来。
        // 注意：钳的是窗口位置，万一真触发了，"内容纹丝不动"就让位给"别出屏幕"
        // —— 出界的格子用户根本看不见，比移位严重。
        ClampToVirtualScreen();
    }

    // ===================== 穿透 / 任务栏 / 拖动 =====================

    public void UpdateTransparent()
    {
        // 拖到一半被打开穿透（窗口从此收不到鼠标事件）—— 先把拖动收干净，
        // 不然 _dragging 挂在 true，后续行为全是错的
        if (ClickThrough)
        {
            EndDrag();
        }

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            style = ClickThrough ? (style | WsExTransparent) : (style & ~WsExTransparent);
            SetWindowLong(handle, GwlExStyle, style);
        }
        catch
        {
        }
    }

    public bool IsReallyVisible()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            return handle != IntPtr.Zero && IsWindowVisible(handle);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清掉所有者窗口（宿主主窗口最小化时，被拥有的窗口会跟着不可见）。</summary>
    public void DetachFromOwner()
    {
        try
        {
            Owner = null;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(handle, GwlpHwndParent, IntPtr.Zero);
            }
            else
            {
                SetWindowLongPtr32(handle, GwlpHwndParent, IntPtr.Zero);
            }

            MakeToolWindow();
        }
        catch
        {
        }
    }

    /// <summary>
    /// 不占任务栏 / 不进 Alt+Tab。必须用 Win32 的 WS_EX_TOOLWINDOW ——
    /// WPF 的 ShowInTaskbar="False" 是靠"挂个隐藏所有者"实现的，而我们要把所有者清掉。
    ///
    /// ⚠ 代价（2026-10-04 发现）：WS_EX_TOOLWINDOW 会被 OBS 的窗口枚举过滤 ——
    ///   悬浮窗在 OBS「窗口捕获」的列表里根本不出现，直播用户抓不到。
    ///   所以有 <see cref="SetObsMode"/>：直播时把 TOOLWINDOW 摘掉，
    ///   换成"出现在 OBS 列表（代价：任务栏 / Alt+Tab 里也会出现）"。
    ///   宿主的聊天悬浮窗（OverlayWindow）没设 TOOLWINDOW 所以 OBS 一直能看到，就是这个道理。
    /// </summary>
    public void MakeToolWindow()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            if ((style & WsExToolWindow) != 0)
            {
                return;
            }

            SetWindowLong(handle, GwlExStyle, style | WsExToolWindow);
        }
        catch
        {
        }
    }

    /// <summary>
    /// OBS 直播模式：true = 摘掉 TOOLWINDOW（窗口出现在 OBS 窗口捕获列表里，可被抓）；
    /// false = 恢复 TOOLWINDOW（不占任务栏 / 不进 Alt+Tab，但 OBS 看不到）。
    ///
    /// 动态切换，不用重建窗口 —— 就是改一个 Win32 扩展样式位。
    /// 开着的代价：因为所有者已被 <see cref="DetachFromOwner"/> 清掉、
    /// WPF 的 ShowInTaskbar="False" 失效，任务栏和 Alt+Tab 里会出现这个窗口。
    /// 直播时任务栏多一个图标基本无感，但要在设置页把这个取舍写明白。
    /// </summary>
    public void SetObsMode(bool on)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            var wanted = on ? (style & ~WsExToolWindow) : (style | WsExToolWindow);

            if (wanted != style)
            {
                SetWindowLong(handle, GwlExStyle, wanted);
            }
        }
        catch
        {
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DetachFromOwner();
        UpdateTransparent();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!ClickThrough)
        {
            // 自绘拖动（不用 DragMove —— 见字段区注释）：记抓取点、捕获鼠标。
            // 捕获是为了鼠标移出窗口后事件仍然派发给本窗口 —— 拖动不中断。
            _dragging = true;
            var pos = e.GetPosition(this);
            _dragGrabX = pos.X;
            _dragGrabY = pos.Y;

            try { CaptureMouse(); } catch { }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging)
        {
            return;
        }

        try
        {
            // 增量式跟随：鼠标在窗口内的坐标相对抓取点偏了多少，窗口就补多少。
            // 不需要鼠标的屏幕物理坐标（那套要 DPI 换算），全是 DIP，直接算。
            // 数学：鼠标物理移 D、窗口还没动 → 窗口内坐标偏 +D → 窗口 Left += D 补上 →
            //       鼠标窗口内坐标回到抓取点。每帧如此，窗口始终跟手。
            var pos = e.GetPosition(this);

            Left += pos.X - _dragGrabX;
            Top += pos.Y - _dragGrabY;

            // ★ 每帧钳制 —— 这就是「防超出屏幕」的核心。拖动中窗口就出不了屏，
            // 而不是拖出去再弹回来。
            ClampToVirtualScreen();
        }
        catch
        {
            // 拖动路径绝不抛 —— 抛了会留下 _dragging=true 的脏状态
            EndDrag();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    /// <summary>结束拖动：释放捕获、清标志。幂等。</summary>
    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        try { ReleaseMouseCapture(); } catch { }
    }

    private static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text) &&
                ColorConverter.ConvertFromString(text) is Color color)
            {
                return color;
            }
        }
        catch
        {
        }

        return fallback;
    }
}
