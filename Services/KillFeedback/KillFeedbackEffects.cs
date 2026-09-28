using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MinecraftChatOverlay.Services.KillFeedback;

/// <summary>击杀反馈的视觉效果类型。</summary>
public enum KillFeedbackEffect
{
    /// <summary>不反馈（只留开关）。</summary>
    None = 0,

    /// <summary>画面四周一圈光带向外亮起再退掉 —— 视野中心完全不受影响。</summary>
    EdgePulse,

    /// <summary>四周压暗（不加颜色），比边缘脉冲更含蓄。</summary>
    VignettePulse,

    /// <summary>红绿蓝三通道沿径向错开几像素再归位。</summary>
    Chromatic,

    /// <summary>整幅画面放大一点点再弹回。</summary>
    ZoomPunch,

    /// <summary>整幅画面小幅高频抖动，幅度衰减。</summary>
    Shake,

    /// <summary>全屏闪光：画面短暂变亮。</summary>
    Flash,

    /// <summary>中心冲击环：一圈光从画面中心向外扩散。</summary>
    Shockwave,

    /// <summary>故障撕裂：横向块位移 + 红蓝分离。</summary>
    Glitch,
}

/// <summary>一次反馈的参数。</summary>
public sealed class KillFeedbackParameters
{
    public KillFeedbackEffect Effect { get; set; } = KillFeedbackEffect.EdgePulse;

    /// <summary>强度 0 ~ 5.0（界面显示 0 ~ 500%）。</summary>
    public double Strength { get; set; } = 0.6;

    /// <summary>持续时间（毫秒）。</summary>
    public double DurationMs { get; set; } = 380;

    /// <summary>边缘脉冲的颜色，格式 0xRRGGBB；其余效果忽略。</summary>
    public uint TintRgb { get; set; } = 0xFFF5E0u;
}

/// <summary>
/// 击杀反馈的**视觉效果渲染器** —— 纯像素运算，不依赖 WPF。
///
/// 为什么单独拆出来、不直接写在窗口里：
///   1. 预览器要用它（在真实截图上演算给你挑）；
///   2. 挑定之后要把**同一套数学**搬进注入 DLL 的着色器里。
///      所以这里刻意不用任何 WPF 的图像处理类，只有 int[] 进、int[] 出，
///      以后照着这段就能翻成 HLSL / GLSL，不会出现"预览和游戏里不一样"。
///
/// 像素格式固定 Bgra32，打包成 int（小端）：
///   bit 0-7 = B，8-15 = G，16-23 = R，24-31 = A
///
/// 所有效果的强度包络统一走 <see cref="Envelope"/>：**快进缓出** ——
/// 前 16% 快速冲到峰值，之后指数回落。这样"起得有劲、收得干净"，
/// 不会像线性淡入淡出那样显得钝。
/// </summary>
public static class KillFeedbackEffects
{
    /// <summary>上升段占总时长的比例。剩下的时间用来衰减。</summary>
    private const double RisePortion = 0.16;

    /// <summary>衰减快慢：越大回落越快。</summary>
    private const double DecayRate = 3.6;

    /// <summary>画面上做效果的最小边长（像素）。太小的窗口就别折腾了。</summary>
    private const int MinEdge = 32;

    /// <summary>
    /// 把 <paramref name="source"/> 在 <paramref name="elapsedMs"/> 这一刻的样子，
    /// 按 <paramref name="parameters"/> 渲染进 <paramref name="target"/>。
    /// 效果已经结束（或类型是 None）时直接复制原图。
    /// </summary>
    public static void Render(
        int[] source, int[] target, int width, int height,
        KillFeedbackParameters parameters, double elapsedMs)
    {
        if (width < MinEdge || height < MinEdge || source.Length < width * height || target.Length < width * height)
        {
            return;
        }

        var duration = Math.Max(1.0, parameters.DurationMs);
        var progress = elapsedMs / duration;
        var strength = Math.Clamp(parameters.Strength, 0.0, 5.0);
        var envelope = Envelope(progress);

        if (parameters.Effect == KillFeedbackEffect.None || envelope <= 0.001 || strength <= 0.001)
        {
            Array.Copy(source, target, width * height);
            return;
        }

        switch (parameters.Effect)
        {
            case KillFeedbackEffect.EdgePulse:
                RenderEdgePulse(source, target, width, height, envelope * strength, parameters.TintRgb);
                break;
            case KillFeedbackEffect.VignettePulse:
                RenderVignettePulse(source, target, width, height, envelope * strength);
                break;
            case KillFeedbackEffect.Chromatic:
                RenderChromatic(source, target, width, height, envelope * strength);
                break;
            case KillFeedbackEffect.ZoomPunch:
                RenderZoomPunch(source, target, width, height, envelope * strength);
                break;
            case KillFeedbackEffect.Shake:
                RenderShake(source, target, width, height, envelope * strength, elapsedMs);
                break;
            case KillFeedbackEffect.Flash:
                RenderFlash(source, target, width, height, envelope * strength);
                break;
            case KillFeedbackEffect.Shockwave:
                RenderShockwave(source, target, width, height, envelope * strength, elapsedMs, duration);
                break;
            case KillFeedbackEffect.Glitch:
                RenderGlitch(source, target, width, height, envelope * strength, elapsedMs);
                break;
            default:
                Array.Copy(source, target, width * height);
                break;
        }
    }

    /// <summary>
    /// 多效果共存：按列表顺序依次叠加。每个效果仍然用与单效果完全相同的数学，
    /// 只是前一个效果的结果会作为后一个效果的输入。
    /// <paramref name="scratch"/> 是调用方提供的临时缓冲，避免每帧分配。
    /// </summary>
    public static void RenderMany(
        int[] source, int[] target, int[] scratch, int width, int height,
        IReadOnlyList<KillFeedbackParameters> effects, double elapsedMs)
    {
        if (width < MinEdge || height < MinEdge ||
            source.Length < width * height || target.Length < width * height ||
            scratch.Length < width * height)
        {
            return;
        }

        if (effects == null || effects.Count == 0)
        {
            Array.Copy(source, target, width * height);
            return;
        }

        int[] read = source;
        int[] write = scratch;
        var applied = 0;

        for (var i = 0; i < effects.Count; i++)
        {
            var p = effects[i];
            if (p == null || p.Effect == KillFeedbackEffect.None || p.Strength <= 0.001)
            {
                continue;
            }

            Render(read, write, width, height, p, elapsedMs);
            read = write;
            write = ReferenceEquals(write, scratch) ? target : scratch;
            applied++;
        }

        if (applied == 0)
        {
            Array.Copy(source, target, width * height);
        }
        else if (!ReferenceEquals(read, target))
        {
            Array.Copy(read, target, width * height);
        }
    }

    /// <summary>
    /// 统一的强度包络：快进缓出。
    /// progress &lt; 0 或 &gt; 1 时返回 0（效果已结束）。
    /// </summary>
    public static double Envelope(double progress)
    {
        if (progress <= 0 || progress >= 1)
        {
            return 0;
        }

        if (progress < RisePortion)
        {
            return progress / RisePortion;
        }

        var decayed = (progress - RisePortion) / (1 - RisePortion);
        return Math.Exp(-DecayRate * decayed);
    }

    /// <summary>一次效果从触发到完全结束要多久（毫秒）——给播放器掐时间用。</summary>
    public static double TotalMs(KillFeedbackParameters parameters) =>
        Math.Max(1.0, parameters.DurationMs) + 60;

    /// <summary>
    /// 包络到达峰值的那一刻（毫秒）。
    /// 界面在不播放时的静止预览用它 —— 直接画峰值帧，调强度时才能一眼看出差别
    /// （画 elapsed=0 的话永远是一张原图，看不出任何变化）。
    /// </summary>
    public static double PeakElapsedMs(KillFeedbackParameters parameters) =>
        Math.Max(1.0, parameters.DurationMs) * RisePortion;

    // ===================== 九种效果 =====================

    /// <summary>
    /// 边缘脉冲：以"离最近屏幕边有多远"决定亮度，越靠边越亮，向中心平方衰减。
    /// 中心区域完全不动 —— 这是它对起床战争最友好的地方。
    /// </summary>
    private static void RenderEdgePulse(int[] source, int[] target, int w, int h, double amount, uint tintRgb)
    {
        var tintB = (int)(tintRgb & 0xFFu);
        var tintG = (int)((tintRgb >> 8) & 0xFFu);
        var tintR = (int)((tintRgb >> 16) & 0xFFu);
        var band = Math.Max(24.0, Math.Min(w, h) * 0.30);

        Parallel.For(0, h, y =>
        {
            var edgeY = Math.Min(y, h - 1 - y);
            var rowBase = y * w;
            for (var x = 0; x < w; x++)
            {
                var edge = Math.Min(edgeY, Math.Min(x, w - 1 - x));
                var falloff = 1.0 - Math.Clamp(edge / band, 0.0, 1.0);
                var k = falloff * falloff * amount;

                if (k <= 0.002)
                {
                    target[rowBase + x] = source[rowBase + x];
                    continue;
                }

                var c = source[rowBase + x];
                var b = (c & 0xFF) + (int)(tintB * k);
                var g = ((c >> 8) & 0xFF) + (int)(tintG * k);
                var r = ((c >> 16) & 0xFF) + (int)(tintR * k);
                target[rowBase + x] = Pack(b, g, r, (c >> 24) & 0xFF);
            }
        });
    }

    /// <summary>暗角脉冲：同一套边缘权重，但方向是"压暗"而不是"发光"。</summary>
    private static void RenderVignettePulse(int[] source, int[] target, int w, int h, double amount)
    {
        var band = Math.Max(24.0, Math.Min(w, h) * 0.38);

        Parallel.For(0, h, y =>
        {
            var edgeY = Math.Min(y, h - 1 - y);
            var rowBase = y * w;
            for (var x = 0; x < w; x++)
            {
                var edge = Math.Min(edgeY, Math.Min(x, w - 1 - x));
                var falloff = 1.0 - Math.Clamp(edge / band, 0.0, 1.0);
                var k = falloff * falloff * amount * 0.75;

                if (k <= 0.002)
                {
                    target[rowBase + x] = source[rowBase + x];
                    continue;
                }

                var c = source[rowBase + x];
                var keep = 1.0 - k;
                var b = (int)((c & 0xFF) * keep);
                var g = (int)(((c >> 8) & 0xFF) * keep);
                var r = (int)(((c >> 16) & 0xFF) * keep);
                target[rowBase + x] = Pack(b, g, r, (c >> 24) & 0xFF);
            }
        });
    }

    /// <summary>
    /// 色差分离：红通道往外拉、蓝通道往里收，绿通道不动。
    /// 位移方向是"从画面中心指向该像素"，所以边缘错得最开、中心几乎没错。
    /// </summary>
    private static void RenderChromatic(int[] source, int[] target, int w, int h, double amount)
    {
        var maxOffset = Math.Max(2.0, Math.Min(w, h) * 0.014);
        var offset = maxOffset * amount;
        var cx = (w - 1) * 0.5;
        var cy = (h - 1) * 0.5;

        Parallel.For(0, h, y =>
        {
            var rowBase = y * w;
            var dy = y - cy;
            for (var x = 0; x < w; x++)
            {
                var dx = x - cx;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.001)
                {
                    target[rowBase + x] = source[rowBase + x];
                    continue;
                }

                var ux = dx / len * offset;
                var uy = dy / len * offset;

                var r = SampleNearest(source, w, h, x + ux, y + uy) >> 16 & 0xFF;
                var g = source[rowBase + x] >> 8 & 0xFF;
                var b = SampleNearest(source, w, h, x - ux, y - uy) & 0xFF;
                target[rowBase + x] = Pack(b, g, r, source[rowBase + x] >> 24 & 0xFF);
            }
        });
    }

    /// <summary>
    /// 缩放脉冲：以画面中心为原点放大一点点。
    /// 用双线性采样（这个效果位移最大，最近邻会出现明显块状）。
    /// </summary>
    private static void RenderZoomPunch(int[] source, int[] target, int w, int h, double amount)
    {
        var scale = 1.0 + amount * 0.10;
        var inv = 1.0 / scale;
        var cx = (w - 1) * 0.5;
        var cy = (h - 1) * 0.5;

        Parallel.For(0, h, y =>
        {
            var rowBase = y * w;
            var sy = cy + (y - cy) * inv;
            for (var x = 0; x < w; x++)
            {
                var sx = cx + (x - cx) * inv;
                target[rowBase + x] = SampleBilinear(source, w, h, sx, sy);
            }
        });
    }

    /// <summary>
    /// 抖动：整幅画面按两级正弦错位，幅度随包络衰减。
    /// 用不同频率的两个轴，避免看起来像规则的水平来回。
    /// </summary>
    private static void RenderShake(
        int[] source, int[] target, int w, int h, double amount, double elapsedMs)
    {
        var amplitude = Math.Max(1.0, Math.Min(w, h) * 0.016) * amount;
        var phase = elapsedMs / 1000.0 * 2 * Math.PI * 21;

        var dx = Math.Sin(phase) * amplitude;
        var dy = Math.Sin(phase * 1.37 + 1.1) * amplitude * 0.65;

        var shiftX = (int)Math.Round(dx);
        var shiftY = (int)Math.Round(dy);

        Parallel.For(0, h, y =>
        {
            var rowBase = y * w;
            var sy = y - shiftY;
            for (var x = 0; x < w; x++)
            {
                target[rowBase + x] = SampleNearest(source, w, h, x - shiftX, sy);
            }
        });
    }

    /// <summary>全屏闪光：整体加亮，默认用暖白，强度由用户决定。</summary>
    private static void RenderFlash(int[] source, int[] target, int w, int h, double amount)
    {
        var add = Math.Clamp(255.0 * amount * 0.55, 0.0, 2000.0);

        Parallel.For(0, h, y =>
        {
            var rowBase = y * w;
            for (var x = 0; x < w; x++)
            {
                var c = source[rowBase + x];
                target[rowBase + x] = Pack(
                    (c & 0xFF) + (int)add,
                    ((c >> 8) & 0xFF) + (int)add,
                    ((c >> 16) & 0xFF) + (int)add,
                    (c >> 24) & 0xFF);
            }
        });
    }

    /// <summary>中心冲击环：随进度从画面中心向外扩散的一圈光。</summary>
    private static void RenderShockwave(
        int[] source, int[] target, int w, int h, double amount,
        double elapsedMs, double durationMs)
    {
        var progress = Math.Clamp(elapsedMs / Math.Max(1.0, durationMs), 0.0, 1.0);
        var maxRadius = Math.Sqrt((double)w * w + (double)h * h) * 0.62;
        var radius = progress * maxRadius;
        var width = Math.Max(5.0, Math.Min(w, h) * 0.055);
        var cx = (w - 1) * 0.5;
        var cy = (h - 1) * 0.5;
        var peak = Math.Clamp(255.0 * amount, 0.0, 2000.0);

        Parallel.For(0, h, y =>
        {
            var rowBase = y * w;
            var dy = y - cy;
            for (var x = 0; x < w; x++)
            {
                var dx = x - cx;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                var delta = dist - radius;
                var ring = Math.Exp(-(delta * delta) / (2.0 * width * width));
                if (ring <= 0.001)
                {
                    target[rowBase + x] = source[rowBase + x];
                    continue;
                }

                var c = source[rowBase + x];
                var k = peak * ring;
                target[rowBase + x] = Pack(
                    (c & 0xFF) + (int)k,
                    ((c >> 8) & 0xFF) + (int)(k * 0.94),
                    ((c >> 16) & 0xFF) + (int)(k * 0.78),
                    (c >> 24) & 0xFF);
            }
        });
    }

    /// <summary>故障撕裂：按行做横向块位移，并让红蓝通道沿位移方向分离。</summary>
    private static void RenderGlitch(
        int[] source, int[] target, int w, int h, double amount, double elapsedMs)
    {
        var maxShift = Math.Min(Math.Min(w, h) * 0.18, Math.Max(1.0, Math.Min(w, h) * 0.012) * amount);
        var rowHeight = Math.Max(1, h / 36);
        var phase = elapsedMs / 1000.0 * 17.0;

        Parallel.For(0, h, y =>
        {
            var rowBase = y * w;
            var row = y / rowHeight;
            var noise = Fract(Math.Sin(row * 12.9898 + phase) * 43758.5453);
            var shift = (int)Math.Round((noise - 0.5) * 2.0 * maxShift);

            for (var x = 0; x < w; x++)
            {
                var c = source[rowBase + x];
                var r = SampleNearest(source, w, h, x + shift, y) >> 16 & 0xFF;
                var g = (c >> 8) & 0xFF;
                var b = SampleNearest(source, w, h, x - shift, y) & 0xFF;
                target[rowBase + x] = Pack(b, g, r, (c >> 24) & 0xFF);
            }
        });
    }

    private static double Fract(double value) => value - Math.Floor(value);

    // ===================== 采样与打包 =====================

    /// <summary>最近邻采样，坐标越界就夹到边缘（这样抖动/缩放时边缘不会出现黑边）。</summary>
    private static int SampleNearest(int[] src, int w, int h, double x, double y)
    {
        var ix = (int)x;
        var iy = (int)y;
        if (ix < 0) ix = 0; else if (ix >= w) ix = w - 1;
        if (iy < 0) iy = 0; else if (iy >= h) iy = h - 1;
        return src[iy * w + ix];
    }

    /// <summary>双线性采样，同样夹边。</summary>
    private static int SampleBilinear(int[] src, int w, int h, double x, double y)
    {
        if (x < 0) x = 0; else if (x > w - 1.001) x = w - 1.001;
        if (y < 0) y = 0; else if (y > h - 1.001) y = h - 1.001;

        var x0 = (int)x;
        var y0 = (int)y;
        var fx = x - x0;
        var fy = y - y0;

        var x1 = x0 + 1 < w ? x0 + 1 : w - 1;
        var y1 = y0 + 1 < h ? y0 + 1 : h - 1;

        var c00 = src[y0 * w + x0];
        var c10 = src[y0 * w + x1];
        var c01 = src[y1 * w + x0];
        var c11 = src[y1 * w + x1];

        var top = 1.0 - fy;
        var left = 1.0 - fx;

        var w00 = left * top;
        var w10 = fx * top;
        var w01 = left * fy;
        var w11 = fx * fy;

        var b = (c00 & 0xFF) * w00 + (c10 & 0xFF) * w10 + (c01 & 0xFF) * w01 + (c11 & 0xFF) * w11;
        var g = ((c00 >> 8) & 0xFF) * w00 + ((c10 >> 8) & 0xFF) * w10
              + ((c01 >> 8) & 0xFF) * w01 + ((c11 >> 8) & 0xFF) * w11;
        var r = ((c00 >> 16) & 0xFF) * w00 + ((c10 >> 16) & 0xFF) * w10
              + ((c01 >> 16) & 0xFF) * w01 + ((c11 >> 16) & 0xFF) * w11;
        var a = ((c00 >> 24) & 0xFF) * w00 + ((c10 >> 24) & 0xFF) * w10
              + ((c01 >> 24) & 0xFF) * w01 + ((c11 >> 24) & 0xFF) * w11;

        return (ClampByte((int)(a + 0.5)) << 24)
             | (ClampByte((int)(r + 0.5)) << 16)
             | (ClampByte((int)(g + 0.5)) << 8)
             | ClampByte((int)(b + 0.5));
    }

    private static int Pack(int b, int g, int r, int a) =>
        (ClampByte(a) << 24) | (ClampByte(r) << 16) | (ClampByte(g) << 8) | ClampByte(b);

    private static int ClampByte(int value) => value < 0 ? 0 : value > 255 ? 255 : value;
}

/// <summary>效果列表里的一项（给界面绑定用）。</summary>
public sealed class KillFeedbackChoice
{
    public KillFeedbackEffect Effect { get; init; }

    /// <summary>显示名。</summary>
    public string Name { get; init; } = "";

    /// <summary>一句话说明，重点是"对视野的影响"。</summary>
    public string Summary { get; init; } = "";

    /// <summary>要不要在列表里打「推荐」标。</summary>
    public bool IsRecommended { get; init; }

    public override string ToString() => Name;
}
