using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MinecraftChatOverlay;

/// <summary>
/// 统一动效参数与入场编排。
///
/// 设计基调：**含蓄轻快**。
/// 四条原则：
///   1. 错峰（stagger）—— 同一动画里各区块依次到达，而不是整体一起动，这是"轻盈"最关键的一招；
///   2. 快速起步 + 极缓收尾 —— 用 QuinticEase.EaseOut，主体到得早、尾巴收得长；
///   3. 幅度小 —— 位移不超过 14px、缩放不低于 0.985、过冲不超过 15%；
///   4. 方向向上 —— 一律"浮起"，不用横向滑入。
///
/// 所有时长/幅度/曲线都集中在下面，改风格只动这一个文件。
/// </summary>
public static class Motion
{
    // ==================== 时长（毫秒） ====================
    // 节奏取向：**快应答、慢安顿**。
    // 按下要立刻有反应（灵动的第一要素），回弹和收尾则放慢，形成"轻重差"。
    // 全篇时长都刻意不统一 —— 大元素慢、小元素快、强调元素带弹性，
    // 这种快慢对比本身就是"灵动"的来源；如果处处都是 200ms，会显得机械。

    /// <summary>按下：即时反馈，要跟手。</summary>
    public const double PressMs = 70;

    /// <summary>回弹：刻意比按下慢得多，形成"轻重差"，这是手感柔和的来源。</summary>
    public const double ReleaseMs = 260;

    /// <summary>悬停进入。灵动要求"一碰就有反应"，所以比按下略慢、比旧值快。</summary>
    public const double HoverInMs = 150;

    /// <summary>悬停离开：略慢于进入，避免"啪"地弹回去。</summary>
    public const double HoverOutMs = 220;

    /// <summary>按钮光泽扫过。</summary>
    public const double ShineMs = 820;

    /// <summary>区块入场的位移时长。</summary>
    public const double EnterMs = 480;

    /// <summary>区块入场的淡入时长。</summary>
    public const double FadeMs = 380;

    /// <summary>区块之间的错峰间隔。60ms 属于"含蓄"，调到 90 会更明显、140 就很"动"了。</summary>
    public const double StaggerMs = 60;

    /// <summary>导航指示条滑动。</summary>
    public const double IndicatorMs = 260;

    /// <summary>开关滑块拨动。带轻微过冲，是"雀跃"的来源。</summary>
    public const double ToggleMs = 260;

    /// <summary>开关轨道变色比滑块晚这么久跟上 —— 滑块先到、颜色后到，产生"被带动"的跟随感。</summary>
    public const double ToggleFollowMs = 40;

    /// <summary>成功心跳的总时长。</summary>
    public const double BeatMs = 180;

    /// <summary>校验失败轻晃的总时长。</summary>
    public const double ShakeMs = 240;

    // ==================== 幅度 ====================

    /// <summary>入场位移（px）。越小越含蓄，14 已经能看出"浮起"但不夸张。</summary>
    public const double EnterOffset = 14;

    /// <summary>悬停上浮（px）。</summary>
    public const double HoverLift = -2;

    /// <summary>按下下沉（px）。用"下沉"代替"缩小"，观感更轻。</summary>
    public const double PressSink = 1;

    /// <summary>按下缩放。只缩 1.5%，几乎察觉不到，但手感会变软。</summary>
    public const double PressScale = 0.985;

    /// <summary>指示条的轻微过冲量。BackEase 的 Amplitude 默认是 1，那会弹得像橡皮球；0.15 约 5% 过冲，是"安顿"的感觉。</summary>
    public const double IndicatorOvershoot = 0.15;

    /// <summary>开关滑块的过冲量。0.4 约 15% 过冲，是"雀跃但不轻浮"的位置。</summary>
    public const double ToggleOvershoot = 0.4;

    // ==================== 曲线 ====================

    /// <summary>柔：快速起步 + 极缓收尾。动效的默认曲线，占了九成用途。</summary>
    public static IEasingFunction Soft() => new QuinticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>带轻微惯性的落位：过冲一点点再收回来。用于指示条、开关滑块这类"被拨动"的东西。</summary>
    public static IEasingFunction Settle() => new BackEase
    {
        Amplitude = IndicatorOvershoot,
        EasingMode = EasingMode.EaseOut
    };

    // ==================== 入场编排 ====================

    /// <summary>
    /// 让面板内的"区块"错峰浮入。
    ///
    /// 区块的定义：面板头的 Grid + 所有 CardStyle 卡片。
    /// 如果某个容器只是用来装若干张卡片，则递归进去 —— 容器本身不参与动画，
    /// 否则容器的动画会和子块的动画叠加，错峰感会被冲掉。
    /// </summary>
    /// <param name="panel">面板根（ScrollViewer 或任意 FrameworkElement）。</param>
    /// <param name="cardStyle">用于识别卡片的 Style，通常是 TryFindResource("CardStyle")。</param>
    public static void StaggerIn(FrameworkElement panel, Style? cardStyle)
    {
        var content = panel is ScrollViewer sv ? sv.Content as FrameworkElement : panel;
        if (content == null)
        {
            return;
        }

        var blocks = new List<FrameworkElement>();
        Collect(content, cardStyle, blocks);
        if (blocks.Count == 0)
        {
            return;
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            AnimateBlock(blocks[i], i * StaggerMs);
        }
    }

    private static void AnimateBlock(FrameworkElement element, double delayMs)
    {
        var delay = TimeSpan.FromMilliseconds(delayMs);

        // 基值先设成"起始状态"，这样在延迟期间元素是隐藏的、不会先闪一下再跳走。
        var translate = new TranslateTransform(0, EnterOffset);
        element.RenderTransform = translate;
        element.Opacity = 0;

        var slide = new DoubleAnimation(EnterOffset, 0, TimeSpan.FromMilliseconds(EnterMs))
        {
            BeginTime = delay,
            EasingFunction = Soft()
        };

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs))
        {
            BeginTime = delay,
            EasingFunction = Soft()
        };

        // 动画跑完就把属性归还给本地值。
        // 不这么做的话，动画会一直"持有" RenderTransform.Y，和 CardStyle 里的悬停动画
        // 争抢同一个属性，表现为卡片偶尔卡在浮起或落下的状态。
        slide.Completed += (_, _) =>
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        };

        translate.BeginAnimation(TranslateTransform.YProperty, slide);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void Collect(DependencyObject node, Style? cardStyle, List<FrameworkElement> output)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<FrameworkElement>())
        {
            var cardCount = child is Panel p
                ? p.Children.OfType<FrameworkElement>().Count(c => c is Border b && ReferenceEquals(b.Style, cardStyle))
                : 0;

            if (cardCount >= 2)
            {
                // 这只是一个"卡片容器"，穿透进去，让每张卡片各自错峰
                Collect(child, cardStyle, output);
            }
            else
            {
                output.Add(child);
            }
        }
    }

    // ==================== 情绪反馈 ====================
    // 界面像有情绪，是"少女感"里最容易被忽略、但最有效的一层：
    // 做对了轻轻雀跃一下，做错了轻轻晃一下表示"不行哦"。

    /// <summary>
    /// 成功心跳：极短的一次 1 → 1.03 → 1。用于保存成功、操作生效这类正反馈。
    /// 注意是"心跳"不是"弹跳"——幅度小、只有一次、没有回摆。
    /// </summary>
    public static void Heartbeat(FrameworkElement element)
    {
        var scale = EnsureMotionTransforms(element).Scale;
        var duration = TimeSpan.FromMilliseconds(BeatMs);

        var anim = new DoubleAnimationUsingKeyFrames
        {
            // FillBehavior.Stop：动画结束后自动回到基值(1)，不需要手工清理，也不会和别的动画抢属性
            FillBehavior = FillBehavior.Stop
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.03, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(BeatMs * 0.45)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, duration)
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        });

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim.Clone());
    }

    /// <summary>
    /// 失败轻晃：左右各晃一次，幅度很小（3px）。用于校验不通过、查询失败这类负反馈。
    /// 比弹一个对话框温柔得多，也更符合"轻声提醒"的语气。
    /// </summary>
    public static void Shake(FrameworkElement element)
    {
        var shift = EnsureMotionTransforms(element).Shift;

        double[] offsets = { 0, -3, 3, -2, 2, 0 };
        var step = ShakeMs / (offsets.Length - 1);

        var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        for (var i = 0; i < offsets.Length; i++)
        {
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(
                offsets[i],
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * step)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }

        shift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>
    /// 取（必要时创建）一组专供反馈动效使用的 Scale + Translate。
    /// 元素自带的 RenderTransform 会被保留并放在前面，不会被覆盖掉。
    /// </summary>
    private static (ScaleTransform Scale, TranslateTransform Shift) EnsureMotionTransforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup existing &&
            existing.Children.Count >= 2 &&
            existing.Children[^2] is ScaleTransform scale &&
            existing.Children[^1] is TranslateTransform shift)
        {
            return (scale, shift);
        }

        var group = new TransformGroup();
        if (element.RenderTransform != null)
        {
            group.Children.Add(element.RenderTransform);
        }

        var newScale = new ScaleTransform(1, 1);
        var newShift = new TranslateTransform(0, 0);
        group.Children.Add(newScale);
        group.Children.Add(newShift);

        element.RenderTransform = group;
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        return (newScale, newShift);
    }
}
