using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MinecraftChatOverlay.Promo;

/// <summary>
/// 宣传片渲染器（临时工具，用完可从工程里删掉）。
///
/// 用法：MinecraftChatOverlay.exe --promo-render
/// 干的事：把 45 秒 × 30fps 的成片逐帧画成 JPEG（1920×1080），落到
/// %TEMP%\mco-promo\frames\，然后自动退出。之后用 ffmpeg 把帧序列合成 mp4。
///
/// 素材来自 PromoCapture 导出的界面截图（%TEMP%\mco-promo\shots\）。
/// 丝滑的关键：所有动效都是"时间驱动"的纯函数 —— 给定 t 就有确定的画面，
/// 逐帧渲染和实时播放是同一个结果，不存在掉帧。
/// </summary>
internal sealed class PromoRenderer
{
    public static bool Requested =>
        Environment.GetCommandLineArgs().Skip(1)
            .Any(a => a.Equals("--promo-render", StringComparison.OrdinalIgnoreCase));

    /// <summary>只导出背景（供后期自己叠字用）：--promo-bg</summary>
    public static bool BackgroundRequested =>
        Environment.GetCommandLineArgs().Skip(1)
            .Any(a => a.Equals("--promo-bg", StringComparison.OrdinalIgnoreCase));

    /// <summary>背景循环一圈的秒数（所有动画都是这 24 秒的整数倍周期 → 首尾无缝）</summary>
    private const double BgLoopSeconds = 24.0;

    private const int W = 1920;
    private const int H = 1080;
    private const double Fps = 30.0;
    private const double Duration = 45.0;

    private static string ShotsDir => Path.Combine(Path.GetTempPath(), "mco-promo", "shots");
    private static string FramesDir => Path.Combine(Path.GetTempPath(), "mco-promo", "frames");

    // 品牌色（与软件 ModernControls.xaml 一致）
    private static readonly Color Primary = Color.FromRgb(0x7C, 0x6C, 0xF0);
    private static readonly Color Accent = Color.FromRgb(0xFF, 0x8F, 0xBE);
    private static readonly Color Mint = Color.FromRgb(0x3F, 0xCF, 0xA9);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xB5, 0x47);
    private static readonly Color Ink = Color.FromRgb(0x2A, 0x2A, 0x31);
    private static readonly Color InkSoft = Color.FromRgb(0x6E, 0x6E, 0x7A);
    private static readonly Color BgTop = Color.FromRgb(0xF4, 0xF4, 0xF9);
    private static readonly Color BgBottom = Color.FromRgb(0xE7, 0xE7, 0xF0);

    private const string CjkFont = "Microsoft YaHei UI";
    private const string LatinFont = "Bahnschrift";

    private BitmapSource? _logo;
    private readonly Dictionary<string, BitmapSource> _shots = new();

    // ------------------------------------------------------------ 入口

    public async Task RunAsync(Dispatcher dispatcher)
    {
        var frames = Path.Combine(FramesDir);
        Directory.CreateDirectory(frames);

        // 上次的帧清掉
        foreach (var old in Directory.GetFiles(frames, "*.jpg"))
        {
            try
            {
                File.Delete(old);
            }
            catch
            {
            }
        }

        await Task.Run(() => LoadAssets());

        var total = (int)(Duration * Fps);
        for (var i = 0; i < total; i++)
        {
            var t = i / Fps;
            var frame = RenderFrame(t);

            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(frame));

            var path = Path.Combine(frames, $"f{i:D5}.jpg");
            using var stream = File.Create(path);
            encoder.Save(stream);

            if (i % 150 == 0)
            {
                File.WriteAllText(Path.Combine(FramesDir, "PROGRESS.txt"),
                    $"{i}/{total} ({t:F1}s){Environment.NewLine}");
            }
        }

        File.WriteAllText(Path.Combine(FramesDir, "DONE.txt"),
            "done " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + $"  frames={total}{Environment.NewLine}");
    }

    // ------------------------------------------------------------ 背景单独导出

    /// <summary>
    /// 只画背景（没有文字、卡片、水印、黑场），输出一个能无缝循环的 24 秒素材 + 一张静态图。
    /// 无缝的原因：所有正弦/漂移都用 BgLoopSeconds 的整数倍周期，t=0 和 t=24 的画面完全一致。
    /// </summary>
    public async Task RunBackgroundAsync(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        await Task.Run(() => LoadAssets());

        var total = (int)(BgLoopSeconds * Fps); // 720 帧
        for (var i = 0; i < total; i++)
        {
            var t = i / Fps;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                DrawLoopBackground(dc, t);
            }

            var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            // 静态图：挑一个光斑位置好看的相位，存 PNG（无损）
            if (i == (int)(6.0 * Fps))
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var pngStream = File.Create(Path.Combine(outputDir, "MCO_bg_1080p.png"));
                png.Save(pngStream);
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(outputDir, $"f{i:D5}.jpg"));
            encoder.Save(stream);

            if (i % 120 == 0)
            {
                File.WriteAllText(Path.Combine(outputDir, "PROGRESS.txt"),
                    $"{i}/{total}{Environment.NewLine}");
            }
        }

        File.WriteAllText(Path.Combine(outputDir, "DONE.txt"),
            "done " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + $"  frames={total}{Environment.NewLine}");
    }

    /// <summary>循环版背景：结构与 DrawBackground 相同，但频率全是 1/24s 的整数倍。</summary>
    private void DrawLoopBackground(DrawingContext dc, double t)
    {
        var w = 2 * Math.PI / BgLoopSeconds;
        var phase = w * t;

        // 底色：渐变轴慢慢转，一圈正好 24 秒
        var a = 0.32 + 0.10 * Math.Sin(phase * 2);
        var bg = new LinearGradientBrush(BgTop, BgBottom,
            new Point(0, 0), new Point(a, 1));
        dc.DrawRectangle(bg, null, new Rect(0, 0, W, H));

        // 光斑 1（紫）：1 倍周期
        DrawGlow(dc, W * (0.24 + 0.06 * Math.Sin(phase)), H * (0.30 + 0.05 * Math.Cos(phase)),
            560, Primary, 0.16);

        // 光斑 2（粉）：1 倍 + 2 倍，相位错开，避免和 1 同步
        DrawGlow(dc, W * (0.78 + 0.05 * Math.Cos(phase + 1.7)), H * (0.74 + 0.06 * Math.Sin(phase * 2 + 0.8)),
            520, Accent, 0.13);

        // 光斑 3（青）：2 倍周期，极慢
        DrawGlow(dc, W * (0.52 + 0.04 * Math.Sin(phase * 2 + 3.0)), H * 0.12, 420, Mint, 0.07);

        // 细点阵：24 秒正好走 5 个格，首尾接得上
        var dot = new SolidColorBrush(Color.FromArgb(14, 42, 42, 49));
        const double step = 46.0;
        var drift = t * (step * 5 / BgLoopSeconds);
        for (var y = -step; y < H + step; y += step)
        {
            for (var x = -step; x < W + step; x += step)
            {
                dc.DrawEllipse(dot, null, new Point(x + drift % step, y + drift * 0.4 % step), 1.3, 1.3);
            }
        }

        // 暗角
        var vignette = new RadialGradientBrush(
            Color.FromArgb(0, 20, 20, 34),
            Color.FromArgb(46, 20, 20, 34));
        dc.DrawRectangle(vignette, null, new Rect(0, 0, W, H));

        // 粒子：竖直位移 24 秒正好走一整屏高（含缓冲），x 摆动 2 倍周期
        const int count = 26;
        for (var i = 0; i < count; i++)
        {
            var seed = i * 97.13;
            const double travel = H + 160;
            var x = (Math.Sin(seed) * 0.5 + 0.5) * W + Math.Sin(phase * 2 + seed) * 40;
            var y = ((Math.Cos(seed * 1.7) * 0.5 + 0.5) * H - t * (travel / BgLoopSeconds)) % travel;
            if (y < -80)
            {
                y += travel;
            }

            var size = 3 + (i % 4) * 2.4;
            var color = i % 3 == 0 ? Accent : i % 3 == 1 ? Primary : Mint;
            DrawGlow(dc, x, y + 80, size * 5, color, 0.10);
            dc.DrawEllipse(Brush(color, 0.16), null, new Point(x, y + 80), size * 0.5, size * 0.5);
        }
    }

    // ------------------------------------------------------------ 素材

    private void LoadAssets()
    {
        var logoPath = Path.Combine(AppContext.BaseDirectory, "LOGO256x.png");
        if (File.Exists(logoPath))
        {
            _logo = LoadBitmap(logoPath);
        }

        foreach (var file in Directory.GetFiles(ShotsDir, "*.png"))
        {
            _shots[Path.GetFileNameWithoutExtension(file)] = LoadBitmap(file);
        }
    }

    private static BitmapSource LoadBitmap(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(path);
        img.DecodePixelWidth = 1920; // 控制内存；卡片显示尺寸最大 ~1100，解码到 1920 足够
        img.EndInit();
        img.Freeze();
        return img;
    }

    // ------------------------------------------------------------ 帧渲染

    private RenderTargetBitmap RenderFrame(double t)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            DrawBackground(dc, t);
            DrawParticles(dc, t);
            DrawWatermark(dc, t);

            foreach (var scene in Scenes)
            {
                if (t < scene.Start || t > scene.End)
                {
                    continue;
                }

                var p = (t - scene.Start) / (scene.End - scene.Start);
                scene.Draw(dc, p, t);
            }

            // 全片黑场进出
            var globalIn = 1.0 - Smooth(Math.Clamp(t / 0.7, 0, 1));
            var globalOut = Smooth(Math.Clamp((t - (Duration - 0.6)) / 0.6, 0, 1));
            var black = Math.Max(globalIn, globalOut);
            if (black > 0.001)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(255 * black), 8, 8, 14)), null,
                    new Rect(0, 0, W, H));
            }
        }

        var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    // ------------------------------------------------------------ 场景表

    private sealed record Scene(double Start, double End, Action<DrawingContext, double, double> Draw);

    private List<Scene> Scenes => new()
    {
        // S1 钩子：logo + 「MCO 又迎来了一次更新」
        new Scene(0.0, 4.2, DrawHook),

        // S2 改名：划掉旧名，亮出 More than Chat Overlay
        new Scene(4.2, 9.2, DrawRebrand),

        // S3 理念：功能不是越多越好 → 插件平台
        new Scene(9.2, 15.2, DrawPhilosophy),

        // S4 邀请：你的插件也能进市场
        new Scene(15.2, 19.2, DrawInvite),

        // S5 动态模糊
        new Scene(19.2, 24.2, DrawMotionBlur),

        // S6 击杀特效
        new Scene(24.2, 30.7, DrawKillFx),

        // S7 无边框全屏 + 悬浮窗
        new Scene(30.7, 35.2, DrawFullscreen),

        // S8 开源生态
        new Scene(35.2, 40.2, DrawEcosystem),

        // S9 署名
        new Scene(40.2, 42.6, DrawSignature),

        // S10 再会
        new Scene(42.6, 45.0, DrawFarewell),
    };

    // ------------------------------------------------------------ S1 钩子

    private void DrawHook(DrawingContext dc, double p, double t)
    {
        var opacity = Math.Min(Fade(p, 0.10, 0.0), Fade(p, 0.10, 0.12));

        if (_logo is not null)
        {
            // logo 落位：从 1.35 倍缩回 1.0，带一点回弹
            var s = 1.35 - 0.35 * EaseOutBack(Math.Clamp(p / 0.30, 0, 1));
            DrawGlow(dc, W / 2.0, 300, 260, Primary, 0.30 * opacity);
            DrawImageCentered(dc, _logo, W / 2.0, 300, 150 * s, 150 * s, opacity);
        }

        var small = MakeText("没错", CjkFont, FontWeights.Medium, 40, Brush(InkSoft, opacity));
        DrawCentered(dc, small, W / 2.0, 468 + (1 - EaseOutCubic(Math.Clamp(p / 0.35, 0, 1))) * 26);

        var big = MakeText("MCO 迎来了一次更新", CjkFont, FontWeights.Bold, 96, Brush(Ink, opacity));
        DrawCentered(dc, big, W / 2.0, 560 + (1 - EaseOutCubic(Math.Clamp((p - 0.06) / 0.35, 0, 1))) * 34);

        // 版本小胶囊
        var chipP = EaseOutCubic(Math.Clamp((p - 0.30) / 0.30, 0, 1));
        DrawChip(dc, W / 2.0, 705, "v1.2.1 · 开源免费", Primary, opacity * chipP,
            (1 - chipP) * 20);
    }

    // ------------------------------------------------------------ S2 改名

    private void DrawRebrand(DrawingContext dc, double p, double t)
    {
        var opacity = Math.Min(Fade(p, 0.08, 0.0), Fade(p, 0.10, 0.12));

        // 旧名先亮出来，再被划掉
        var oldP = Math.Clamp(p / 0.34, 0, 1);
        var oldText = MakeText("Minecraft Chat Overlay", LatinFont, FontWeights.SemiBold, 92,
            Brush(InkSoft, opacity * 0.92));
        var oldY = 430 + (1 - EaseOutCubic(oldP)) * 30;
        DrawCentered(dc, oldText, W / 2.0, oldY);

        // 划线：粉色，从左往右扫过，扫完旧名变淡
        var strikeP = Math.Clamp((p - 0.34) / 0.16, 0, 1);
        if (strikeP > 0)
        {
            var lineW = oldText.Width * EaseInOutCubic(strikeP);
            var pen = new Pen(Brush(Accent, opacity * 0.95), 7);
            dc.DrawLine(pen, new Point(W / 2.0 - oldText.Width / 2, oldY + oldText.Height / 2 + 4),
                new Point(W / 2.0 - oldText.Width / 2 + lineW, oldY + oldText.Height / 2 + 4));
        }

        // 新名：划线扫过的同时顶上来
        var newP = Math.Clamp((p - 0.46) / 0.30, 0, 1);
        if (newP > 0)
        {
            var rise = (1 - EaseOutCubic(newP)) * 44;
            var newText = MakeText("More than Chat Overlay", LatinFont, FontWeights.Bold, 96,
                Brush(Primary, opacity * newP));
            DrawCentered(dc, newText, W / 2.0, 590 + rise);

            DrawGlow(dc, W / 2.0, 590 + 60, 300 * newP, Primary, 0.20 * opacity * newP);

            var subP = Math.Clamp((p - 0.66) / 0.28, 0, 1);
            DrawSpaced(dc, "不 止 于 悬 浮 窗", CjkFont, FontWeights.Bold, 58,
                Brush(Ink, opacity * subP), W / 2.0, 742 + (1 - EaseOutCubic(subP)) * 24, 10);
        }
    }

    // ------------------------------------------------------------ S3 理念

    private void DrawPhilosophy(DrawingContext dc, double p, double t)
    {
        var opacity = Math.Min(Fade(p, 0.08, 0.0), Fade(p, 0.10, 0.12));
        DrawKicker(dc, "为什么做插件平台", opacity);

        // 左侧三行观点，逐条浮现
        var lines = new[]
        {
            ("一个软件的功能", "并不是越多越好"),
            ("对不需要某些功能的用户", "太多功能只会让软件臃肿"),
            ("于是我充分发挥开源的优势", "搭了一个插件平台"),
        };

        for (var i = 0; i < lines.Length; i++)
        {
            var lp = Math.Clamp((p - 0.10 - i * 0.20) / 0.22, 0, 1);
            if (lp <= 0)
            {
                continue;
            }

            var y = 300 + i * 150 + (1 - EaseOutCubic(lp)) * 26;
            dc.DrawRoundedRectangle(Brush(Primary, 0.14 * opacity * lp), null,
                new Rect(180, y + 14, 8, 74), 4, 4);
            var main = MakeText(lines[i].Item1, CjkFont, FontWeights.Bold, 52, Brush(Ink, opacity * lp));
            dc.DrawText(main, new Point(216, y - 6));
            var sub = MakeText(lines[i].Item2, CjkFont, FontWeights.Regular, 36, Brush(InkSoft, opacity * lp));
            dc.DrawText(sub, new Point(216, y + 58));
        }

        // 右侧：插件页截图卡片从右滑入
        var cardP = EaseOutCubic(Math.Clamp((p - 0.30) / 0.42, 0, 1));
        if (cardP > 0 && _shots.TryGetValue("04_plugins", out var plugins))
        {
            var rect = new Rect(1050, 250, 720, 492);
            var shifted = new Rect(rect.X + (1 - cardP) * 140, rect.Y + (1 - cardP) * 30, rect.Width, rect.Height);
            DrawShotCard(dc, plugins, shifted, 20, opacity * cardP);

            DrawChip(dc, shifted.X + 118, shifted.Y - 4, "插件平台", Primary, opacity * cardP, 0);
        }
    }

    // ------------------------------------------------------------ S4 邀请

    private void DrawInvite(DrawingContext dc, double p, double t)
    {
        var opacity = Math.Min(Fade(p, 0.08, 0.0), Fade(p, 0.10, 0.12));
        DrawKicker(dc, "插件市场", opacity);

        var title = MakeText("你的插件，也能进市场", CjkFont, FontWeights.Bold, 84, Brush(Ink, opacity));
        DrawCentered(dc, title, W / 2.0, 260 + (1 - EaseOutCubic(Math.Clamp(p / 0.30, 0, 1))) * 30);

        // 三步流程
        var steps = new[]
        {
            ("01", "把插件发给我", Primary),
            ("02", "审核通过", Mint),
            ("03", "投入插件市场，供其他用户下载", Accent),
        };

        for (var i = 0; i < steps.Length; i++)
        {
            var sp = EaseOutCubic(Math.Clamp((p - 0.26 - i * 0.16) / 0.22, 0, 1));
            if (sp <= 0)
            {
                continue;
            }

            var x = 320 + i * 470;
            var y = 470 + (1 - sp) * 30;
            DrawSoftShadow(dc, new Rect(x, y, 380, 130), 22, opacity * sp);
            dc.DrawRoundedRectangle(Brush(Colors.White, opacity * sp),
                new Pen(Brush(Color.FromRgb(0xE7, 0xE7, 0xEC), opacity * sp), 1.4),
                new Rect(x, y, 380, 130), 22, 22);

            var num = MakeText(steps[i].Item1, LatinFont, FontWeights.Bold, 40, Brush(steps[i].Item3, opacity * sp));
            dc.DrawText(num, new Point(x + 26, y + 20));
            var label = MakeText(steps[i].Item2, CjkFont, FontWeights.Bold, 30, Brush(Ink, opacity * sp));
            dc.DrawText(label, new Point(x + 26, y + 70));
        }

        // 底部：市场截图缩略条
        var barP = EaseOutCubic(Math.Clamp((p - 0.60) / 0.30, 0, 1));
        if (barP > 0 && _shots.TryGetValue("05_market", out var market))
        {
            var rect = new Rect(W / 2.0 - 420, 660, 840, 330);
            var shifted = new Rect(rect.X, rect.Y + (1 - barP) * 40, rect.Width, rect.Height);
            DrawShotCard(dc, market, shifted, 18, opacity * barP);
        }
    }

    // ------------------------------------------------------------ S5 动态模糊

    private void DrawMotionBlur(DrawingContext dc, double p, double t)
    {
        var opacity = Math.Min(Fade(p, 0.08, 0.0), Fade(p, 0.10, 0.12));
        DrawKicker(dc, "软件已经可以实现", opacity);

        var title = MakeText("游戏的动态模糊", CjkFont, FontWeights.Bold, 92, Brush(Ink, opacity));
        DrawCentered(dc, title, W / 2.0, 250 + (1 - EaseOutCubic(Math.Clamp(p / 0.28, 0, 1))) * 30);

        var sub = MakeText("实时处理游戏画面，让移动带出动感", CjkFont, FontWeights.Regular, 34,
            Brush(InkSoft, opacity));
        DrawCentered(dc, sub, W / 2.0, 380);

        // 设置页卡片从右往左滑入；滑动时画两重"拖影"，正好扣题
        var cardP = EaseOutCubic(Math.Clamp((p - 0.24) / 0.44, 0, 1));
        if (cardP > 0 && _shots.TryGetValue("06_motionblur", out var blur))
        {
            var rect = new Rect(W / 2.0 - 520, 460, 1040, 530);
            var x = rect.X + (1 - cardP) * 260;
            var current = new Rect(x, rect.Y, rect.Width, rect.Height);

            for (var i = 3; i >= 1; i--)
            {
                var ghostX = x + i * 46 * (1 - cardP);
                DrawImageClipped(dc, blur, new Rect(ghostX, rect.Y, rect.Width, rect.Height), 20,
                    opacity * cardP * 0.10 * i);
            }

            DrawShotCard(dc, blur, current, 20, opacity * cardP);
        }
    }

    // ------------------------------------------------------------ S6 击杀特效

    private void DrawKillFx(DrawingContext dc, double p, double t)
    {
        var opacity = Math.Min(Fade(p, 0.08, 0.0), Fade(p, 0.10, 0.12));
        DrawKicker(dc, "在这条路上更进一步", opacity);

        var title = MakeText("匹配击杀信息，触发视觉特效", CjkFont, FontWeights.Bold, 76, Brush(Ink, opacity));
        DrawCentered(dc, title, W / 2.0, 220 + (1 - EaseOutCubic(Math.Clamp(p / 0.26, 0, 1))) * 28);

        // 左：击杀反馈设置页
        var cardP = EaseOutCubic(Math.Clamp((p - 0.20) / 0.40, 0, 1));
        if (cardP > 0 && _shots.TryGetValue("07_killfeed", out var killfeed))
        {
            var rect = new Rect(150, 340, 880, 572);
            var shifted = new Rect(rect.X + (1 - cardP) * -120, rect.Y, rect.Width, rect.Height);
            DrawShotCard(dc, killfeed, shifted, 20, opacity * cardP);
        }

        // 右：自定义击杀图标（就是横幅窗口里的样子），外发光呼吸
        var iconP = EaseOutBack(Math.Clamp((p - 0.44) / 0.26, 0, 1));
        if (iconP > 0 && _shots.TryGetValue("21_kill_banner", out var banner))
        {
            var pulse = 0.9 + 0.1 * Math.Sin((t - 24.2) * 4.4);
            DrawGlow(dc, 1430, 540, 320 * pulse, Accent, 0.38 * opacity * iconP);
            DrawImageCentered(dc, banner, 1430, 540, 430 * iconP, 430 * iconP, opacity * iconP);
        }

        // 三个要点胶囊
        var chips = new[] { "内置多种特效", "自定义击杀图标", "自定义音效" };
        for (var i = 0; i < chips.Length; i++)
        {
            var cp = EaseOutCubic(Math.Clamp((p - 0.62 - i * 0.10) / 0.20, 0, 1));
            if (cp <= 0)
            {
                continue;
            }

            DrawChip(dc, 1160 + i * 240, 830, chips[i], Mint, opacity * cp, (1 - cp) * 18);
        }
    }

    // ------------------------------------------------------------ S7 无边框全屏

    private void DrawFullscreen(DrawingContext dc, double p, double t)
    {
        var opacity = Math.Min(Fade(p, 0.08, 0.0), Fade(p, 0.10, 0.12));

        // 深色"屏幕"底：无边框全屏的示意（不画游戏画面，用品牌色氛围光代替）
        var screenP = EaseOutCubic(Math.Clamp(p / 0.30, 0, 1));
        var screen = new Rect(W / 2.0 - 560, 260, 1120, 630);
        var grew = new Rect(screen.X - (1 - screenP) * 80, screen.Y - (1 - screenP) * 46,
            screen.Width + (1 - screenP) * 160, screen.Height + (1 - screenP) * 92);

        DrawSoftShadow(dc, grew, 24, 1.6 * opacity * screenP);
        dc.PushClip(new RectangleGeometry(grew, 24, 24));
        dc.DrawRectangle(ScreenGradient(t), null, grew);
        DrawGlow(dc, grew.X + grew.Width * 0.30, grew.Y + grew.Height * 0.42, 300, Primary, 0.22 * opacity * screenP);
        DrawGlow(dc, grew.X + grew.Width * 0.72, grew.Y + grew.Height * 0.60, 260, Accent, 0.16 * opacity * screenP);
        dc.Pop();

        // 标题压在深色底上方
        var title = MakeText("游戏无边框全屏", CjkFont, FontWeights.Bold, 84, Brush(Ink, opacity));
        DrawCentered(dc, title, W / 2.0, 130 + (1 - EaseOutCubic(Math.Clamp(p / 0.24, 0, 1))) * 26);

        // 悬浮窗本体浮在"屏幕"上，缓缓放大
        var ovP = EaseOutCubic(Math.Clamp((p - 0.30) / 0.36, 0, 1));
        if (ovP > 0 && _shots.TryGetValue("20_overlay_window", out var overlay))
        {
            var w = 620 * (0.94 + 0.06 * ovP);
            var h = w * overlay.Height / overlay.Width;
            var x = grew.X + 90;
            var y = grew.Y + 90 + (1 - ovP) * 40;
            DrawGlow(dc, x + w / 2, y + h / 2, w * 0.75, Primary, 0.28 * opacity * ovP);
            dc.PushTransform(new TranslateTransform(0, 0));
            dc.DrawImage(overlay, new Rect(x, y, w, h));
            dc.Pop();
        }

        // 右下角：windowfullscreen 插件的说明
        var noteP = Math.Clamp((p - 0.56) / 0.24, 0, 1);
        if (noteP > 0)
        {
            var x = grew.X + grew.Width - 420;
            var y = grew.Y + grew.Height - 150 + (1 - EaseOutCubic(noteP)) * 24;
            DrawSoftShadow(dc, new Rect(x, y, 360, 100), 18, opacity * noteP);
            dc.DrawRoundedRectangle(Brush(Colors.White, 0.97 * opacity * noteP),
                new Pen(Brush(Color.FromRgb(0xE7, 0xE7, 0xEC), opacity * noteP), 1.4),
                new Rect(x, y, 360, 100), 18, 18);
            var t1 = MakeText("窗口全屏插件", CjkFont, FontWeights.Bold, 30, Brush(Primary, opacity * noteP));
            dc.DrawText(t1, new Point(x + 24, y + 14));
            var t2 = MakeText("一键无边框全屏，随时还原", CjkFont, FontWeights.Regular, 22,
                Brush(InkSoft, opacity * noteP));
            dc.DrawText(t2, new Point(x + 24, y + 58));
        }

        var line = MakeText("有了它，悬浮窗在全屏游戏里也能稳定显示", CjkFont, FontWeights.Regular, 34,
            Brush(InkSoft, opacity * Math.Clamp((p - 0.66) / 0.20, 0, 1)));
        DrawCentered(dc, line, W / 2.0, 940);
    }

    // ------------------------------------------------------------ S8 开源生态

    private void DrawEcosystem(DrawingContext dc, double p, double t)
    {
        var opacity = Math.Min(Fade(p, 0.08, 0.0), Fade(p, 0.10, 0.12));
        DrawKicker(dc, "最后", opacity);

        var title = MakeText("开源生态，没办法只靠我一个人支撑", CjkFont, FontWeights.Bold, 68, Brush(Ink, opacity));
        DrawCentered(dc, title, W / 2.0, 240 + (1 - EaseOutCubic(Math.Clamp(p / 0.26, 0, 1))) * 26);

        // 两股动力
        var pills = new[]
        {
            ("你对软件的打赏", Accent),
            ("你对插件的开发", Primary),
        };

        for (var i = 0; i < pills.Length; i++)
        {
            var pp = EaseOutBack(Math.Clamp((p - 0.24 - i * 0.14) / 0.24, 0, 1));
            if (pp <= 0)
            {
                continue;
            }

            var cx = W / 2.0 - 330 + i * 660;
            var cy = 430 + (1 - pp) * 26;
            DrawGlow(dc, cx, cy + 46, 190 * pp, pills[i].Item2, 0.30 * opacity * pp);
            DrawSoftShadow(dc, new Rect(cx - 250, cy, 500, 130), 26, opacity * pp);
            dc.DrawRoundedRectangle(Brush(Colors.White, opacity * pp),
                new Pen(Brush(Color.FromRgb(0xE7, 0xE7, 0xEC), opacity * pp), 1.4),
                new Rect(cx - 250, cy, 500, 130), 26, 26);
            var text = MakeText(pills[i].Item1, CjkFont, FontWeights.Bold, 42, Brush(Ink, opacity * pp));
            DrawCentered(dc, text, cx, cy + 42);
        }

        var lineP = Math.Clamp((p - 0.52) / 0.24, 0, 1);
        var line = MakeText("都会成为软件继续向前的动力", CjkFont, FontWeights.Bold, 52, Brush(Primary, opacity * lineP));
        DrawCentered(dc, line, W / 2.0, 680 + (1 - EaseOutCubic(lineP)) * 24);

        // 右下角：打赏码（来自关于页截图）
        var qrP = EaseOutCubic(Math.Clamp((p - 0.62) / 0.26, 0, 1));
        if (qrP > 0 && _shots.TryGetValue("10_about", out var about))
        {
            var w = 330;
            var h = w * about.Height / about.Width;
            var rect = new Rect(W - w - 150, H - h - 130, w, h);
            DrawShotCard(dc, about, new Rect(rect.X, rect.Y + (1 - qrP) * 30, rect.Width, rect.Height), 16,
                opacity * qrP);
        }
    }

    // ------------------------------------------------------------ S9 署名 / S10 再会

    private void DrawSignature(DrawingContext dc, double p, double t)
    {
        var opacity = Fade(p, 0.10, 0.15);

        if (_logo is not null)
        {
            DrawGlow(dc, W / 2.0, 380, 240, Primary, 0.26 * opacity);
            DrawImageCentered(dc, _logo, W / 2.0, 380, 130, 130, opacity);
        }

        var big = MakeText("我是 gd", CjkFont, FontWeights.Bold, 120, Brush(Ink, opacity));
        DrawCentered(dc, big, W / 2.0, 560 + (1 - EaseOutCubic(Math.Clamp(p / 0.30, 0, 1))) * 30);
    }

    private void DrawFarewell(DrawingContext dc, double p, double t)
    {
        var opacity = Math.Min(Fade(p, 0.12, 0.0), Fade(p, 0.10, 0.12));

        if (_logo is not null)
        {
            DrawImageCentered(dc, _logo, W / 2.0, 320, 110, 110, opacity);
        }

        var big = MakeText("我们 再会", CjkFont, FontWeights.Bold, 108, Brush(Ink, opacity));
        DrawCentered(dc, big, W / 2.0, 470);

        var sub = MakeText("Minecraft Chat Overlay · More than Chat Overlay", LatinFont, FontWeights.SemiBold, 34,
            Brush(InkSoft, opacity * Math.Clamp((p - 0.24) / 0.24, 0, 1)));
        DrawCentered(dc, sub, W / 2.0, 620);

        // 联系方式小胶囊（竖排，避免长链接互相压住）
        var chips = new[] { ("B 站：space.bilibili.com/586937256", Accent), ("QQ 闲聊群：qm.qq.com/q/yQN8DLlhHG", Mint) };
        for (var i = 0; i < chips.Length; i++)
        {
            var cp = EaseOutCubic(Math.Clamp((p - 0.40 - i * 0.14) / 0.24, 0, 1));
            if (cp <= 0)
            {
                continue;
            }

            DrawChip(dc, W / 2.0, 730 + i * 78, chips[i].Item1, chips[i].Item2, opacity * cp, (1 - cp) * 16);
        }
    }

    // ------------------------------------------------------------ 公共部件

    /// <summary>小标签（导航左上角的小字）。</summary>
    private void DrawKicker(DrawingContext dc, string text, double opacity)
    {
        var kicker = MakeText(text, CjkFont, FontWeights.Bold, 30, Brush(Primary, opacity * 0.9));
        dc.DrawText(kicker, new Point(180, 116));
        dc.DrawRoundedRectangle(Brush(Primary, 0.55 * opacity), null, new Rect(180, 168, 56, 5), 2.5, 2.5);
    }

    /// <summary>胶囊标签。cy 是中心 y。</summary>
    private void DrawChip(DrawingContext dc, double cx, double cy, string text, Color color, double opacity, double dy)
    {
        var ft = MakeText(text, CjkFont, FontWeights.Bold, 26, Brush(Ink, opacity));
        var padX = 24.0;
        var w = ft.Width + padX * 2 + 18;
        var h = 54.0;
        var rect = new Rect(cx - w / 2, cy - h / 2 + dy, w, h);

        DrawSoftShadow(dc, rect, h / 2, opacity);
        dc.DrawRoundedRectangle(Brush(Colors.White, opacity),
            new Pen(Brush(color, opacity), 1.6), rect, h / 2, h / 2);
        dc.DrawRoundedRectangle(Brush(color, 0.16 * opacity), null,
            new Rect(rect.X + 12, rect.Y + 12, 30, 30), 15, 15);
        dc.DrawText(ft, new Point(rect.X + padX + 18, cy - ft.Height / 2 + dy));
    }

    /// <summary>截图卡片：白底圆角 + 柔和投影 + 内容 cover 裁切 + 细边框。</summary>
    private void DrawShotCard(DrawingContext dc, BitmapSource img, Rect rect, double radius, double opacity)
    {
        DrawSoftShadow(dc, rect, radius, opacity);

        dc.PushClip(new RectangleGeometry(rect, radius, radius));
        dc.DrawRectangle(Brush(Colors.White, opacity), null, rect);

        // cover 适配：截图是 2 倍图，直接缩放即可，画质足够
        var scale = Math.Max(rect.Width / img.Width, rect.Height / img.Height);
        var w = img.Width * scale;
        var h = img.Height * scale;
        dc.DrawImage(img,
            new Rect(rect.X - (w - rect.Width) / 2, rect.Y - (h - rect.Height) / 2, w, h));
        dc.Pop();

        dc.DrawRoundedRectangle(null, new Pen(Brush(Color.FromRgb(0xE7, 0xE7, 0xEC), opacity), 1.4),
            rect, radius, radius);
    }

    /// <summary>带透明度的图片裁切绘制（不带白底与边框）。</summary>
    private void DrawImageClipped(DrawingContext dc, BitmapSource img, Rect rect, double radius, double opacity)
    {
        dc.PushClip(new RectangleGeometry(rect, radius, radius));
        var scale = Math.Max(rect.Width / img.Width, rect.Height / img.Height);
        var w = img.Width * scale;
        var h = img.Height * scale;
        dc.PushOpacity(opacity);
        dc.DrawImage(img, new Rect(rect.X - (w - rect.Width) / 2, rect.Y - (h - rect.Height) / 2, w, h));
        dc.Pop();
        dc.Pop();
    }

    private void DrawImageCentered(DrawingContext dc, BitmapSource img, double cx, double cy, double w, double h,
        double opacity)
    {
        dc.PushOpacity(opacity);
        dc.DrawImage(img, new Rect(cx - w / 2, cy - h / 2, w, h));
        dc.Pop();
    }

    /// <summary>径向发光（品牌色的柔光晕，代替 blur effect）。</summary>
    private void DrawGlow(DrawingContext dc, double cx, double cy, double radius, Color color, double opacity)
    {
        if (opacity <= 0.002 || radius <= 1)
        {
            return;
        }

        var brush = new RadialGradientBrush(Color.FromArgb((byte)(255 * opacity), color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B));
        dc.DrawEllipse(brush, null, new Point(cx, cy), radius, radius);
    }

    /// <summary>柔和投影：多层圆角矩形叠加，从内到外透明度二次衰减。</summary>
    private static void DrawSoftShadow(DrawingContext dc, Rect rect, double radius, double opacity)
    {
        const int layers = 10;
        for (var i = layers; i >= 1; i--)
        {
            var k = i / (double)layers;
            var grow = 4 + k * 26;
            var alpha = 0.055 * (1 - k) * (1 - k) * opacity;
            if (alpha <= 0.001)
            {
                continue;
            }

            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 30, 30, 45)),
                null,
                new Rect(rect.X - grow, rect.Y - grow, rect.Width + grow * 2, rect.Height + grow * 2),
                radius + grow, radius + grow);
        }
    }

    // ------------------------------------------------------------ 背景

    private void DrawBackground(DrawingContext dc, double t)
    {
        // 底色 + 缓慢漂移的品牌色光斑 + 细点阵 + 暗角
        var bg = new LinearGradientBrush(BgTop, BgBottom, new Point(0, 0), new Point(0.25, 1));
        dc.DrawRectangle(bg, null, new Rect(0, 0, W, H));

        // 光斑 1（紫）：左上，绕大圈慢慢走
        var cx1 = W * (0.24 + 0.06 * Math.Sin(t * 0.20));
        var cy1 = H * (0.30 + 0.05 * Math.Cos(t * 0.16));
        DrawGlow(dc, cx1, cy1, 560, Primary, 0.16);

        // 光斑 2（粉）：右下，反向走
        var cx2 = W * (0.78 + 0.05 * Math.Cos(t * 0.14 + 1.7));
        var cy2 = H * (0.74 + 0.06 * Math.Sin(t * 0.18 + 0.8));
        DrawGlow(dc, cx2, cy2, 520, Accent, 0.13);

        // 光斑 3（青）：中上，极慢
        var cx3 = W * (0.52 + 0.04 * Math.Sin(t * 0.10 + 3.0));
        DrawGlow(dc, cx3, H * 0.12, 420, Mint, 0.07);

        // 细点阵
        var dot = new SolidColorBrush(Color.FromArgb(14, 42, 42, 49));
        var step = 46.0;
        var drift = (t * 6) % step;
        for (var y = -step; y < H + step; y += step)
        {
            for (var x = -step; x < W + step; x += step)
            {
                dc.DrawEllipse(dot, null, new Point(x + drift, y + drift * 0.4), 1.3, 1.3);
            }
        }

        // 暗角
        var vignette = new RadialGradientBrush(
            Color.FromArgb(0, 20, 20, 34),
            Color.FromArgb(46, 20, 20, 34));
        dc.DrawRectangle(vignette, null, new Rect(0, 0, W, H));
    }

    private void DrawParticles(DrawingContext dc, double t)
    {
        // 一小把缓浮的柔光粒子（固定种子，保证每帧一致）
        const int count = 26;
        for (var i = 0; i < count; i++)
        {
            var seed = i * 97.13;
            var speed = 14 + (i % 5) * 7;
            var x = (Math.Sin(seed) * 0.5 + 0.5) * W + Math.Sin(t * 0.35 + seed) * 40;
            var y = ((Math.Cos(seed * 1.7) * 0.5 + 0.5) * H - t * speed) % (H + 160);
            if (y < -80)
            {
                y += H + 160;
            }

            var size = 3 + (i % 4) * 2.4;
            var color = i % 3 == 0 ? Accent : i % 3 == 1 ? Primary : Mint;
            DrawGlow(dc, x, y, size * 5, color, 0.10);
            dc.DrawEllipse(Brush(color, 0.16), null, new Point(x, y), size * 0.5, size * 0.5);
        }
    }

    private void DrawWatermark(DrawingContext dc, double t)
    {
        var opacity = 0.55 * Math.Clamp(t / 2.0, 0, 1);
        if (_logo is not null)
        {
            DrawImageCentered(dc, _logo, 66, 62, 40, 40, opacity);
        }

        var mark = MakeText("Minecraft Chat Overlay · v1.2.1", LatinFont, FontWeights.SemiBold, 20,
            Brush(InkSoft, opacity));
        dc.DrawText(mark, new Point(96, 44));
    }

    /// <summary>S7 用的深色"屏幕"渐变，随时间轻微呼吸。</summary>
    private Brush ScreenGradient(double t)
    {
        var a = 0.5 + 0.5 * Math.Sin(t * 0.5);
        return new LinearGradientBrush(
            Color.FromRgb(0x17, 0x14, 0x25),
            Color.FromRgb(0x0F, 0x0D, 0x1A),
            new Point(0, 0), new Point(0.6, 1));
    }

    // ------------------------------------------------------------ 文本

    private FormattedText MakeText(string text, string family, FontWeight weight, double size, Brush brush)
    {
        var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, weight, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, size, brush, 1.0)
        {
            MaxTextWidth = W - 160,
            MaxTextHeight = size * 2.6,
            TextAlignment = TextAlignment.Left,
        };
        return ft;
    }

    private void DrawCentered(DrawingContext dc, FormattedText ft, double cx, double topY)
    {
        dc.DrawText(ft, new Point(cx - ft.Width / 2, topY));
    }

    private void DrawSpaced(DrawingContext dc, string text, string family, FontWeight weight, double size,
        Brush brush, double cx, double cy, double spacing)
    {
        var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, weight, FontStretches.Normal);
        var total = 0.0;
        var widths = new List<double>();
        foreach (var ch in text)
        {
            var ft = new FormattedText(ch.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, size, brush, 1.0);
            widths.Add(ft.Width);
            total += ft.Width + spacing;
        }

        total -= spacing;
        var x = cx - total / 2;
        for (var i = 0; i < text.Length; i++)
        {
            var ft = new FormattedText(text[i].ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, size, brush, 1.0);
            dc.DrawText(ft, new Point(x, cy - size * 0.82));
            x += widths[i] + spacing;
        }
    }

    // ------------------------------------------------------------ 缓动 / 颜色

    private static Brush Brush(Color color, double opacity) =>
        new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(255 * opacity, 0, 255), color.R, color.G, color.B));

    private static double Fade(double p, double inFrac, double outFrac)
    {
        var result = 1.0;
        if (inFrac > 0)
        {
            result = Math.Min(result, Math.Clamp(p / inFrac, 0, 1));
        }

        if (outFrac > 0)
        {
            result = Math.Min(result, Math.Clamp((1 - p) / outFrac, 0, 1));
        }

        return result;
    }

    private static double Smooth(double x) => x * x * (3 - 2 * x);

    private static double EaseOutCubic(double x) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), 3);

    private static double EaseInOutCubic(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    private static double EaseOutBack(double x)
    {
        x = Math.Clamp(x, 0, 1);
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }
}
