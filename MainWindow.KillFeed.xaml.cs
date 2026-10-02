using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Services;
using MinecraftChatOverlay.Services.GameMotionBlur;
using MinecraftChatOverlay.Services.KillFeedback;

namespace MinecraftChatOverlay;

/// <summary>
/// 「击杀反馈」页的逻辑（MainWindow 的另一个 partial 文件）。
///
/// 这一版只做**效果预览**：拿你最近一张游戏截图，在上面按同一套数学实时演算，
/// 让你先把效果挑定。真正接入击杀识别（匹配规则 + 注入 DLL）是下一步。
///
/// 之所以坚持"同一套数学"：渲染逻辑全在 <see cref="KillFeedbackEffects"/> 里，
/// 那一份是纯像素运算、不依赖 WPF —— 以后照着它翻成 HLSL / GLSL 就行，
/// 不会出现"预览里好看、进了游戏不一样"。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 预览的内部渲染分辨率上限。效果本身和分辨率无关，
    /// 压到 720 宽是为了每帧的像素循环够快（60fps 下留足余量）。
    /// </summary>
    private const int KillPreviewMaxWidth = 720;

    private readonly Stopwatch _killPreviewClock = new();
    private readonly List<KillFeedbackParameters> _killPreviewParamsList = new();
    private KillFeedbackParameters _killPreviewParams = new();
    private int[] _killPreviewScratch = Array.Empty<int>();

    /// <summary>击杀匹配（跑在真实聊天流上）。</summary>
    private readonly KillFeedbackMatcher _killFeedbackMatcher = new();

    private int[] _killPreviewSource = Array.Empty<int>();
    private int[] _killPreviewFrame = Array.Empty<int>();
    private WriteableBitmap? _killPreviewBitmap;
    private int _killPreviewWidth;
    private int _killPreviewHeight;
    private bool _killPreviewAnimating;

    private bool _killFeedbackUiReady;
    private DispatcherTimer? _killFeedbackSaveTimer;

    /// <summary>上一次"往游戏里送事件"的情况（成功时为空）。</summary>
    private string _killFeedbackSendNote = "";

    /// <summary>击杀横幅窗口（惰性创建，避免每次都 new）。</summary>
    private KillBannerWindow? _killBanner;

    /// <summary>窗口加载时调用（见 MainWindow_Loaded）。</summary>
    private void InitializeKillFeedUi()
    {
        _killFeedbackUiReady = false;

        MigrateLegacyKillFeedbackSettings();
        ApplyKillFeedbackSettingsToUi();
        KillFeedbackDurationSlider.Value = Math.Clamp(_settings.KillFeedback.DurationMs, 120, 900);

        // 匹配那一段
        KillFeedbackEnableCheckBox.IsChecked = _settings.KillFeedback.Enabled;
        KillFeedbackPlayerIdTextBox.Text = _settings.KillFeedback.PlayerId;
        KillFeedbackSampleTextBox.Text = _settings.KillFeedback.SampleMessage;
        KillFeedbackSampleEnemyTextBox.Text = _settings.KillFeedback.SampleEnemyId;
        KillFeedbackRulesTextBox.Text = _settings.KillFeedback.MatchRules;
        KillFeedbackRegexRulesCheckBox.IsChecked = _settings.KillFeedback.MatchRulesAreRegex;
        KillFeedbackStripCheckBox.IsChecked = _settings.KillFeedback.StripBrackets;

        KillBannerEnableCheckBox.IsChecked = _settings.KillFeedback.BannerEnabled;
        ApplyBannerSettingsToUi();

        UpdateKillEdgeColorSwatch();

        CaptureKillFeedbackRuleInputs();
        RefreshKillFeedbackStats();

        _killFeedbackUiReady = true;

        ApplyKillFeedbackParameters();
        LoadLatestGameScreenshot();
        RefreshLockedCards();

        // 击杀图标窗口常驻：启动就建好并挂上，之后再也不 Hide/Show。
        // 交给 MainWindow 的 Closing 统一收掉（见 Window_Closing）。
        EnsureKillBannerWindow();

        // 关窗时一定要把逐帧回调摘掉，否则 CompositionTarget 会一直抓着这个窗口。
        Closing += (_, _) =>
        {
            try
            {
                StopKillPreviewAnimation();
            }
            catch
            {
                // 退出流程里不要抛
            }
        };
    }

    /// <summary>
    /// 建立（或取回）击杀图标窗口，并让它在屏幕上"常驻待命"。
    ///
    /// 为什么要提前建：真正击杀时只该有一个"让图标亮一下"的动作，
    /// 不该顺带做建窗口 / 显示窗口这种会扰动全屏游戏的操作。
    /// 窗口建好后就一直在那儿（透明 + 鼠标穿透，肉眼看不见），击杀时才置图标可见。
    /// </summary>
    private void EnsureKillBannerWindow()
    {
        if (_killBanner == null)
        {
            _killBanner = new KillBannerWindow();
            _killBanner.PositionChanged -= KillBanner_PositionChanged;
            _killBanner.PositionChanged += KillBanner_PositionChanged;
        }

        _killBanner.EnsureVisible();
    }

    private static KillFeedbackEffect ParseEffect(string? value) =>
        Enum.TryParse<KillFeedbackEffect>(value, ignoreCase: true, out var parsed)
            ? parsed
            : KillFeedbackEffect.EdgePulse;

    // ===================== 参数 =====================

    private void ApplyKillFeedbackParameters()
    {
        var duration = KillFeedbackDurationSlider.Value;
        var list = new List<KillFeedbackParameters>();
        var edgeTint = ParseKillEdgeColor(_settings.KillFeedback.EdgeColor);
        AddKillPreviewEffect(list, KillFeedbackEffect.EdgePulse, KillEffectEdgeCheckBox, KillEffectEdgeStrengthTextBox, duration, edgeTint);
        AddKillPreviewEffect(list, KillFeedbackEffect.VignettePulse, KillEffectVignetteCheckBox, KillEffectVignetteStrengthTextBox, duration);
        AddKillPreviewEffect(list, KillFeedbackEffect.Chromatic, KillEffectChromaticCheckBox, KillEffectChromaticStrengthTextBox, duration);
        AddKillPreviewEffect(list, KillFeedbackEffect.ZoomPunch, KillEffectZoomCheckBox, KillEffectZoomStrengthTextBox, duration);
        AddKillPreviewEffect(list, KillFeedbackEffect.Shake, KillEffectShakeCheckBox, KillEffectShakeStrengthTextBox, duration);
        AddKillPreviewEffect(list, KillFeedbackEffect.Flash, KillEffectFlashCheckBox, KillEffectFlashStrengthTextBox, duration);
        AddKillPreviewEffect(list, KillFeedbackEffect.Shockwave, KillEffectShockwaveCheckBox, KillEffectShockwaveStrengthTextBox, duration);
        AddKillPreviewEffect(list, KillFeedbackEffect.Glitch, KillEffectGlitchCheckBox, KillEffectGlitchStrengthTextBox, duration);

        _killPreviewParamsList.Clear();
        _killPreviewParamsList.AddRange(list);
        _killPreviewParams = list.Count > 0
            ? list[0]
            : new KillFeedbackParameters { Effect = KillFeedbackEffect.None, DurationMs = duration };

        if (!_killFeedbackUiReady)
        {
            return;
        }

        var k = _settings.KillFeedback;
        k.DurationMs = duration;
        k.EdgeEnabled = KillEffectEdgeCheckBox.IsChecked == true;
        k.EdgeStrength = ReadPercent(KillEffectEdgeStrengthTextBox, 0.6);
        k.VignetteEnabled = KillEffectVignetteCheckBox.IsChecked == true;
        k.VignetteStrength = ReadPercent(KillEffectVignetteStrengthTextBox, 0.6);
        k.ChromaticEnabled = KillEffectChromaticCheckBox.IsChecked == true;
        k.ChromaticStrength = ReadPercent(KillEffectChromaticStrengthTextBox, 0.6);
        k.ZoomEnabled = KillEffectZoomCheckBox.IsChecked == true;
        k.ZoomStrength = ReadPercent(KillEffectZoomStrengthTextBox, 0.6);
        k.ShakeEnabled = KillEffectShakeCheckBox.IsChecked == true;
        k.ShakeStrength = ReadPercent(KillEffectShakeStrengthTextBox, 0.6);
        k.FlashEnabled = KillEffectFlashCheckBox.IsChecked == true;
        k.FlashStrength = ReadPercent(KillEffectFlashStrengthTextBox, 0.6);
        k.ShockwaveEnabled = KillEffectShockwaveCheckBox.IsChecked == true;
        k.ShockwaveStrength = ReadPercent(KillEffectShockwaveStrengthTextBox, 0.6);
        k.GlitchEnabled = KillEffectGlitchCheckBox.IsChecked == true;
        k.GlitchStrength = ReadPercent(KillEffectGlitchStrengthTextBox, 0.6);
        k.BannerEnabled = KillBannerEnableCheckBox.IsChecked == true;
        CollectBannerSettingsFromUi();
        k.MultiEffectConfigured = true;
    }

    private static void AddKillPreviewEffect(
        List<KillFeedbackParameters> list, KillFeedbackEffect effect,
        CheckBox check, TextBox text, double duration, uint tintRgb = 0xFFF5E0u)
    {
        if (check.IsChecked != true)
        {
            return;
        }

        var strength = ReadPercent(text, 0.6);
        if (strength <= 0.001)
        {
            return;
        }

        list.Add(new KillFeedbackParameters
        {
            Effect = effect,
            Strength = strength,
            DurationMs = duration,
            TintRgb = tintRgb,
        });
    }

    private static double ReadPercent(TextBox text, double fallback)
    {
        if (double.TryParse(text.Text, out var percent))
        {
            return Math.Clamp(percent / 100.0, 0.0, 5.0);
        }
        return fallback;
    }

    private static string FormatPercent(double strength) =>
        Math.Round(Math.Clamp(strength, 0.0, 5.0) * 100.0).ToString("F0");

    private void MigrateLegacyKillFeedbackSettings()
    {
        var k = _settings.KillFeedback;
        if (k.MultiEffectConfigured)
        {
            return;
        }

        k.EdgeEnabled = false;
        k.VignetteEnabled = false;
        k.ChromaticEnabled = false;
        k.ZoomEnabled = false;
        k.ShakeEnabled = false;

        var strength = Math.Clamp(k.Strength, 0.0, 5.0);
        switch (ParseEffect(k.Effect))
        {
            case KillFeedbackEffect.EdgePulse:
                k.EdgeEnabled = true; k.EdgeStrength = strength; break;
            case KillFeedbackEffect.VignettePulse:
                k.VignetteEnabled = true; k.VignetteStrength = strength; break;
            case KillFeedbackEffect.Chromatic:
                k.ChromaticEnabled = true; k.ChromaticStrength = strength; break;
            case KillFeedbackEffect.ZoomPunch:
                k.ZoomEnabled = true; k.ZoomStrength = strength; break;
            case KillFeedbackEffect.Shake:
                k.ShakeEnabled = true; k.ShakeStrength = strength; break;
        }

        k.MultiEffectConfigured = true;
    }

    private void ApplyKillFeedbackSettingsToUi()
    {
        var k = _settings.KillFeedback;
        KillEffectEdgeCheckBox.IsChecked = k.EdgeEnabled;
        KillEffectEdgeStrengthTextBox.Text = FormatPercent(k.EdgeStrength);
        UpdateKillEdgeColorSwatch();
        KillEffectVignetteCheckBox.IsChecked = k.VignetteEnabled;
        KillEffectVignetteStrengthTextBox.Text = FormatPercent(k.VignetteStrength);
        KillEffectChromaticCheckBox.IsChecked = k.ChromaticEnabled;
        KillEffectChromaticStrengthTextBox.Text = FormatPercent(k.ChromaticStrength);
        KillEffectZoomCheckBox.IsChecked = k.ZoomEnabled;
        KillEffectZoomStrengthTextBox.Text = FormatPercent(k.ZoomStrength);
        KillEffectShakeCheckBox.IsChecked = k.ShakeEnabled;
        KillEffectShakeStrengthTextBox.Text = FormatPercent(k.ShakeStrength);
        KillEffectFlashCheckBox.IsChecked = k.FlashEnabled;
        KillEffectFlashStrengthTextBox.Text = FormatPercent(k.FlashStrength);
        KillEffectShockwaveCheckBox.IsChecked = k.ShockwaveEnabled;
        KillEffectShockwaveStrengthTextBox.Text = FormatPercent(k.ShockwaveStrength);
        KillEffectGlitchCheckBox.IsChecked = k.GlitchEnabled;
        KillEffectGlitchStrengthTextBox.Text = FormatPercent(k.GlitchStrength);
        KillBannerEnableCheckBox.IsChecked = k.BannerEnabled;
        ApplyBannerSettingsToUi();
    }

    /// <summary>
    /// 拖滑块时不要每动一下就写盘（原来悬浮窗那两个滑块就是这么干的，写得很密）。
    /// 这里停手 400ms 才落盘。
    /// </summary>
    private void QueueKillFeedbackSave()
    {
        if (_killFeedbackSaveTimer == null)
        {
            _killFeedbackSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _killFeedbackSaveTimer.Tick += (_, _) =>
            {
                _killFeedbackSaveTimer!.Stop();
                SaveKillFeedbackSettings();
            };
        }

        _killFeedbackSaveTimer.Stop();
        _killFeedbackSaveTimer.Start();
    }

    private void SaveKillFeedbackSettings()
    {
        try
        {
            SettingsService.Save(_settings);
        }
        catch
        {
            // 存配置失败不该打断使用
        }
    }

    // ===================== 预览底图 =====================

    private void LoadLatestGameScreenshot()
    {
        var directory = ResolveScreenshotDir();
        string? newest = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                newest = Directory.EnumerateFiles(directory, "*.png")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
        }
        catch
        {
            // 目录读不了就走下面的提示
        }

        if (newest == null)
        {
            KillFeedbackPreviewSourceText.Text = "";
            KillFeedbackPreviewStatusText.Text = Copy.KillFeedbackNoScreenshot;
            return;
        }

        LoadPreviewImage(newest);
    }

    private void LoadPreviewImage(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = KillPreviewMaxWidth;
            bitmap.EndInit();
            bitmap.Freeze();

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            if (width < 64 || height < 64)
            {
                KillFeedbackPreviewStatusText.Text = Copy.KillFeedbackImageTooSmall;
                return;
            }

            // Bgra32 的字节流直接倒进 int[]，正好对上渲染器的打包格式（小端）
            var stride = width * 4;
            var bytes = new byte[stride * height];
            bitmap.CopyPixels(bytes, stride, 0);

            var pixels = new int[width * height];
            Buffer.BlockCopy(bytes, 0, pixels, 0, pixels.Length * 4);

            _killPreviewSource = pixels;
            _killPreviewFrame = new int[width * height];
            _killPreviewScratch = new int[width * height];
            _killPreviewWidth = width;
            _killPreviewHeight = height;

            _killPreviewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            KillFeedbackPreviewImage.Source = _killPreviewBitmap;

            RenderKillPreviewFrame(0);

            KillFeedbackPreviewSourceText.Text = Path.GetFileName(path) + $"（{width}×{height}）";
            KillFeedbackPreviewStatusText.Text = "";
        }
        catch (Exception ex)
        {
            KillFeedbackPreviewStatusText.Text = "这张图读不出来：" + ex.Message;
        }
    }

    // ===================== 渲染与播放 =====================

    private void RenderKillPreviewFrame(double elapsedMs)
    {
        if (_killPreviewBitmap == null || _killPreviewSource.Length == 0)
        {
            return;
        }

        if (_killPreviewScratch.Length != _killPreviewFrame.Length)
        {
            _killPreviewScratch = new int[_killPreviewFrame.Length];
        }

        KillFeedbackEffects.RenderMany(
            _killPreviewSource, _killPreviewFrame, _killPreviewScratch,
            _killPreviewWidth, _killPreviewHeight,
            _killPreviewParamsList, elapsedMs);

        _killPreviewBitmap.WritePixels(
            new Int32Rect(0, 0, _killPreviewWidth, _killPreviewHeight),
            _killPreviewFrame, _killPreviewWidth * 4, 0);
    }

    private void StartKillPreviewAnimation()
    {
        _killPreviewClock.Restart();

        if (_killPreviewAnimating)
        {
            return;
        }

        _killPreviewAnimating = true;
        CompositionTarget.Rendering += OnKillPreviewRendering;
    }

    private void StopKillPreviewAnimation()
    {
        if (!_killPreviewAnimating)
        {
            return;
        }

        _killPreviewAnimating = false;
        CompositionTarget.Rendering -= OnKillPreviewRendering;
        _killPreviewClock.Stop();
    }

    /// <summary>每次合成前一帧都调一次 —— 比 DispatcherTimer 更贴屏幕刷新节奏。</summary>
    private void OnKillPreviewRendering(object? sender, EventArgs e)
    {
        var elapsed = _killPreviewClock.Elapsed.TotalMilliseconds;
        var total = KillPreviewTotalMs();

        if (elapsed <= total)
        {
            RenderKillPreviewFrame(elapsed);
            return;
        }

        // 一遍放完就收尾（循环开关已去掉 —— 预览只演一次）。
        StopKillPreviewAnimation();
        RenderKillPreviewFrame(0);
        KillFeedbackPreviewStatusText.Text = "";
    }

    // ===================== 事件 =====================

    private void KillFeedbackEffectSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        ApplyKillFeedbackParameters();
        QueueKillFeedbackSave();

        // 勾选/取消效果就自动放一遍，省得每次都要再点一下触发
        if (_killPreviewBitmap != null)
        {
            KillFeedbackPreviewStatusText.Text = Copy.KillFeedbackPlaying;
            StartKillPreviewAnimation();
        }
    }

    private void KillFeedbackEffectText_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        ApplyKillFeedbackParameters();
        QueueKillFeedbackSave();

        // 没在播放时直接画峰值帧，输入完数字能立刻看到变化
        if (!_killPreviewAnimating && _killPreviewBitmap != null)
        {
            RenderKillPreviewFrame(KillFeedbackEffects.PeakElapsedMs(_killPreviewParams));
        }
    }

    private double KillPreviewTotalMs()
    {
        double total = 0.0;
        for (var i = 0; i < _killPreviewParamsList.Count; i++)
        {
            total = Math.Max(total, KillFeedbackEffects.TotalMs(_killPreviewParamsList[i]));
        }

        return total > 0.0 ? total : Math.Max(1.0, _killPreviewParams.DurationMs) + 60.0;
    }

    private void KillFeedbackTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_killPreviewBitmap == null)
        {
            MessageBox.Show(this, Copy.KillFeedbackNoScreenshot, "触发一次",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ApplyKillFeedbackParameters();
        KillFeedbackPreviewStatusText.Text = Copy.KillFeedbackPlaying;
        StartKillPreviewAnimation();

        // 预览时把击杀提示音也一起放一遍 —— 让"画面效果 + 音效"在这一页上能一次试完。
        // 跟随击杀提示音的开关：关着就不响，和游戏里真实击杀的表现一致。
        PreviewKillSoundForEffect();
    }

    /// <summary>
    /// 点【触发一次】时顺带播放一次击杀提示音。
    ///
    /// 走**和真击杀完全同一套抽签逻辑**（<see cref="RandomPicker.Pick"/>）——
    /// 这样"预览里听到什么"和"游戏里会响什么"是一回事。
    ///
    /// 之前这里直接读 <c>_settings.KillFeedback.SoundFile</c> 那个旧字段，出了个 bug：
    /// 那个字段只在开关变动 / 列表重建时才刷新，**光在列表里改选中项不会更新它**，
    /// 所以关掉随机、改选另一个音之后，预览还在播老的那个。
    /// （现在 <c>SoundFile</c> 只剩播放器内部兜底的作用，不该再拿来决定播什么。）
    ///
    /// 不看规则匹配、也不受最短间隔限制 —— 它就是个试听。
    /// </summary>
    private void PreviewKillSoundForEffect()
    {
        try
        {
            if (!_settings.KillFeedback.SoundEnabled)
            {
                return;
            }

            var k = _settings.KillFeedback;

            // 和 NotifyKillSound 同一套：关随机用列表里选中的，开随机从勾中的抽。
            var selected = (KillSoundChoiceListBox?.SelectedItem as SoundChoice)?.File;

            var file = RandomPicker.Pick(
                k.SoundFiles, k.SoundPicked, k.SoundRandom, null, selected);

            if (!string.IsNullOrWhiteSpace(file))
            {
                _killSoundNotifier.Preview(file);
            }
        }
        catch
        {
            // 音效只是附加反馈，播不出来不能影响预览动画
        }
    }

    private void KillFeedbackPickImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Copy.KillFeedbackPickImageTitle,
            Filter = Copy.KillFeedbackImageFilter,
        };

        var directory = ResolveScreenshotDir();
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        StopKillPreviewAnimation();
        LoadPreviewImage(dialog.FileName);
    }

    private void KillFeedbackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        ApplyKillFeedbackParameters();
        QueueKillFeedbackSave();

        // 没在播放的时候，直接把**峰值那一帧**画出来。
        // 这样拖强度滑块能立刻看出差别；如果画 elapsed=0，永远是一张原图，看不出变化。
        if (!_killPreviewAnimating)
        {
            RenderKillPreviewFrame(KillFeedbackEffects.PeakElapsedMs(_killPreviewParams));
        }
    }

    // ===================== 匹配规则 =====================

    /// <summary>把界面上的输入收进配置，并同步给匹配器。</summary>
    private void CaptureKillFeedbackRuleInputs()
    {
        _settings.KillFeedback.PlayerId = KillFeedbackPlayerIdTextBox.Text.Trim();
        _settings.KillFeedback.SampleMessage = KillFeedbackSampleTextBox.Text.Trim();
        _settings.KillFeedback.SampleEnemyId = KillFeedbackSampleEnemyTextBox.Text.Trim();

        // 规则框保留原始换行 —— "一行一条"就是靠换行分隔的，逐行 Trim 在编译阶段做
        _settings.KillFeedback.MatchRules = KillFeedbackRulesTextBox.Text;
        _settings.KillFeedback.MatchRulesAreRegex = KillFeedbackRegexRulesCheckBox.IsChecked == true;
        _settings.KillFeedback.StripBrackets = KillFeedbackStripCheckBox.IsChecked == true;

        _killFeedbackMatcher.Rules = _settings.KillFeedback.MatchRules;
        _killFeedbackMatcher.RulesAreRegex = _settings.KillFeedback.MatchRulesAreRegex;
        _killFeedbackMatcher.StripDecorationsOn = _settings.KillFeedback.StripBrackets;
    }

    /// <summary>
    /// 刷新"前置条件"横幅：击杀反馈的画面效果走动态模糊那条注入通路，
    /// 没注入就等于效果到不了游戏。这里把这件事说在明面上，别让用户自己猜。
    /// </summary>
    private void RefreshKillFeedbackPrereq()
    {
        if (KillFeedbackPrereqBanner == null)
        {
            return;
        }

        var ready = false;
        try
        {
            ready = _motionBlur.TargetProcessId > 0 && _motionBlur.GetStatus().HookInstalled;
        }
        catch
        {
            // 读不到状态就按"没就绪"显示，提示总比误导好
            ready = false;
        }

        KillFeedbackPrereqIcon.Text = ready ? "✓" : "!";
        KillFeedbackPrereqTitle.Text = ready
            ? Copy.KillFeedbackPrereqReady
            : Copy.KillFeedbackPrereqMissing;
        KillFeedbackPrereqDetail.Text = ready
            ? Copy.KillFeedbackPrereqReadyDetail
            : Copy.KillFeedbackPrereqMissingDetail;

        var accent = ready
            ? (Brush)FindResource("MintBrush")
            : (Brush)FindResource("ConsoleWarnBrush");

        KillFeedbackPrereqIcon.Foreground = accent;
        KillFeedbackPrereqTitle.Foreground = accent;
    }

    /// <summary>
    /// 刷新「必须填了匹配规则才会生效」那条横幅。
    ///
    /// 和上面的"要先注入"横幅一个道理：**规则空着的时候整页都在空转，但界面上
    /// 一点异常都看不出来** —— 用户会以为是软件坏了。所以常驻显示、按实际情况变色：
    /// 没规则 = 琥珀色提醒；有规则 = 薄荷色的"已经填了 N 条"。
    /// 右边那个【查看填写教程】按钮是给"不知道怎么写"的人准备的（见 KillFeedbackHelpWindow）。
    /// </summary>
    private void RefreshKillFeedbackRuleHint()
    {
        if (KillFeedbackRuleHintBanner == null)
        {
            return;
        }

        // IsReady = 规则框里至少有一条能用的规则（空行 / # 注释不算）
        var ready = _killFeedbackMatcher.IsReady;
        var accent = ready
            ? (Brush)FindResource("MintBrush")
            : (Brush)FindResource("ConsoleWarnBrush");

        KillFeedbackRuleHintIcon.Text = ready ? "✓" : "!";
        KillFeedbackRuleHintIcon.Foreground = accent;

        KillFeedbackRuleHintTitle.Text = ready
            ? $"已经填了 {_killFeedbackMatcher.RuleCount} 条匹配规则"
            : "必须填了匹配规则才会生效";
        KillFeedbackRuleHintTitle.Foreground = accent;

        KillFeedbackRuleHintDetail.Text = ready
            ? "规则这一半边是通的。剩下的看上面那条（有没有注入）和卡片底部那行数字（有没有匹配上）。"
            : "规则框空着，软件一条杀都认不出来 —— 画面效果和提示音都不会响。不会写就点右边的【▶ 新手引导】。";
    }

    /// <summary>【查看填写教程】：弹一个独立窗讲规则怎么填（外观同公告窗）。</summary>
    private void KillFeedbackRuleHelpButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new KillFeedbackHelpWindow { Owner = this };
        dialog.ShowDialog();
    }

    // ============================================================================
    //  匹配规则 · 新手引导
    //
    //  为什么做在页面里（而不是像【文字版】那样开个弹窗）：用户写不出规则，不是看不懂
    //  解释，而是不知道"这行字到底该写什么"。所以引导得让他看着规则框被填、被删、
    //  被写错又改对 —— 浮层在页面底部讲 + 高亮目标控件 + 替他改规则框。
    //
    //  状态机只有三个字段：_tutorialActive / _tutorialStepIndex / 每一步的 Enter 动作。
    //  正文里的 {id} 占位符在显示的那一刻替换成用户填的 ID（他可能中途才填上）。
    // ============================================================================

    private bool _tutorialActive;
    private int _tutorialStepIndex = -1;
    private (string Title, string Body, Action? Enter)[]? _tutorialSteps;
    private DispatcherTimer? _tutorialFlashTimer;

    /// <summary>引导正文里引用 ID 用这个：没填就写「你的ID」，填了就照他的来。</summary>
    private string TutorialId()
    {
        var id = KillFeedbackPlayerIdTextBox.Text.Trim();
        return id.Length > 0 ? id : "你的ID";
    }

    /// <summary>
    /// 16 步：开场 → 填 ID → 写出一个错规则 → 讲清为什么错 → 找出不变的部分 →
    /// 第二种击杀消息 → 砍掉 # 后面的内容 → 总结 → 称号 / VIP 不用写。
    /// </summary>
    private (string Title, string Body, Action? Enter)[] BuildTutorialSteps() => new (string, string, Action?)[]
    {
        ("先搞清楚：软件能知道什么",
            "软件没法直接知道你打死了谁 —— 游戏不会把这件事告诉外部程序，它只能从聊天栏的消息里间接判断。"
            + "所以要你告诉它：聊天栏里出现什么样的文字，就说明你击杀了一次。"
            + "下面用起床战争的消息，一起把这段文字试出来。",
            null),

        ("第一步：你自己的 ID",
            "先把你自己的 ID 填上 —— 就是你在游戏里显示的名字，只填名字本身，称号和 VIP 前缀不用管。填好再点下一步。",
            () =>
            {
                ClearTutorialHighlights();
                HighlightTutorialTarget(KillFeedbackPlayerIdHighlight, false);
                ScrollTutorialTargetIntoView(KillFeedbackPlayerIdHighlight);
            }),

        ("拿一条真实的消息看看",
            "以起床战争为例：你造成一次击杀时，聊天栏会跳出一行「{id}击败了 XXX」这样的消息 —— 是这样吧？",
            () =>
            {
                ClearTutorialHighlights();
                ScrollTutorialTargetIntoView(KillFeedbackRulesHighlight);
            }),

        ("那……规则就照抄这一行？",
            "那么，也就是说：把你的击杀信息连带着敌人的 ID，完整填进规则框里 —— 这样对吗？",
            () =>
            {
                ClearTutorialHighlights();
                SetTutorialRules("{id}击败了 XXX");
                HighlightTutorialTarget(KillFeedbackRulesHighlight, false);
                ScrollTutorialTargetIntoView(KillFeedbackRulesHighlight);
            }),

        ("不对！",
            "这样填，就等于告诉软件：聊天栏里每次出现这行文字，就给我一次反馈。"
            + "仔细看看 —— 这行文字里是不是还带着一个明确的敌人 ID？",
            FlashTutorialRulesRed),

        ("按这条规则，谁会给你反馈？",
            "也就是说：只有那个 ID 的玩家被你击杀之后，你才会收到一次反馈 —— 换个人杀就不灵了。",
            null),

        ("但这明显不是我们要的",
            "我们要的效果是：无论是谁，只要被你击杀，就给你一次反馈。所以先把刚才那条错的规则清掉。",
            () =>
            {
                ClearTutorialHighlights();
                SetTutorialRules("");
                ScrollTutorialTargetIntoView(KillFeedbackRulesHighlight);
            }),

        ("那我们多看几条",
            "多看几条击杀消息：{id}击败了 A、{id}击败了 B、{id}击败了 C —— 你找到规律了吗？",
            () =>
            {
                ClearTutorialHighlights();
                ScrollTutorialTargetIntoView(KillFeedbackRulesHighlight);
            }),

        ("变的部分 / 不变的部分",
            "没错：你触发的击杀消息里，总有变的部分和不变的部分 —— 变的是敌人的 ID；"
            + "而不变的那几个字，就可以写下来告诉软件：出现这几个字，就代表我击杀了敌人。",
            () =>
            {
                ClearTutorialHighlights();
                SetTutorialRules("{id}击败了");
                HighlightTutorialTarget(KillFeedbackRulesHighlight, false);
                ScrollTutorialTargetIntoView(KillFeedbackRulesHighlight);
            }),

        ("可服务器不止一种击杀消息",
            "有些服里，击杀消息长这样：XXX 成为了 {id} 的第 #1 个最终击杀。同样的，我们还是先找出变的部分和不变的部分。",
            null),

        ("这次变的不只是 ID",
            "你会发现：变的不只是敌人的 ID，后面那个数字也在变。这个时候就要注意了 —— 不能只是把会变的部分删掉就走。",
            null),

        ("为什么 # 后面不能留数字",
            "软件判断一条消息是不是你的击杀时，是一个字一个字对着来的：它拿到「XXX 成为了 {id} 的第 #2 个最终击杀」，"
            + "就从「成为了」开始逐字匹配 —— 前面都很顺，可匹配到 # 后面时：消息里跟着的是数字 2，而你写的是「个」，"
            + "软件就认为没匹配上，判定这条不是你的击杀消息。",
            () =>
            {
                ClearTutorialHighlights();
                AppendTutorialRule("成为了{id}的第#个最终击杀");
                FlashTutorialRulesRed();
            }),

        ("那该怎么办呢",
            "可以注意到：我们其实并不需要让软件去匹配 # 后面的内容。",
            null),

        ("砍掉多余的部分",
            "只要有「成为了 {id} 的第」这几个字，不就可以确定这条消息是你的击杀消息了吗？",
            () =>
            {
                ClearTutorialHighlights();
                ReplaceLastTutorialRule("成为了{id}的第");
                HighlightTutorialTarget(KillFeedbackRulesHighlight, false);
                ScrollTutorialTargetIntoView(KillFeedbackRulesHighlight);
            }),

        ("总结一下规则怎么写",
            "匹配规则就是：填入只有你击杀别人时才会出现的那段文字组合。常见的还有：{id} 一吼、"
            + "被 {id} 的神之箭所贯穿、{id} 的利剑终结了 —— 一局里有几种格式，就写几条。",
            ClearTutorialHighlights),

        ("最后一点：称号和 VIP",
            "如果你有称号，或者 VIP 前缀，这两样都不用写进规则里 —— 默认开着的「剥离方括号前缀」会在匹配前先把它去掉。"
            + "点【完成】收工：规则框里已经留了一份示例，照着改成你服里的消息就行。",
            ClearTutorialHighlights),
    };

    /// <summary>【▶ 新手引导】：先弹窗讲清楚代价（会清空已填规则），用户点了才进去。</summary>
    private void KillFeedbackTutorialStartButton_Click(object sender, RoutedEventArgs e)
    {
        var intro = new KillFeedbackTutorialIntroWindow { Owner = this };
        if (intro.ShowDialog() != true)
        {
            return;
        }

        // 按弹窗里承诺的两件事：打开开关、清空已填的匹配规则（其它设置一个不动）
        KillFeedbackEnableCheckBox.IsChecked = true;
        SetTutorialRules("");

        StartTutorial();
    }

    private void StartTutorial()
    {
        _tutorialSteps = BuildTutorialSteps();
        _tutorialActive = true;

        // 切到别的页时把浮层收起来（引导不该还挂在别的页面上）
        KillFeedPanel.IsVisibleChanged -= KillFeedPanel_IsVisibleChanged;
        KillFeedPanel.IsVisibleChanged += KillFeedPanel_IsVisibleChanged;

        ShowTutorialStep(0);
    }

    private void KillFeedPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateTutorialOverlayVisibility();

    private void ShowTutorialStep(int index)
    {
        if (_tutorialSteps == null || _tutorialSteps.Length == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, _tutorialSteps.Length - 1);
        _tutorialStepIndex = index;

        var (title, body, enter) = _tutorialSteps[index];
        enter?.Invoke();

        KillFeedbackTutorialStepText.Text = $"新手引导 · {index + 1}/{_tutorialSteps.Length}";
        KillFeedbackTutorialTitleText.Text = title.Replace("{id}", TutorialId());
        KillFeedbackTutorialBodyText.Text = body.Replace("{id}", TutorialId());
        KillFeedbackTutorialHintText.Visibility = Visibility.Collapsed;
        KillFeedbackTutorialPrevButton.IsEnabled = index > 0;
        KillFeedbackTutorialNextButton.Content = index == _tutorialSteps.Length - 1 ? "完成" : "下一步";

        UpdateTutorialOverlayVisibility();
    }

    private void KillFeedbackTutorialNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_tutorialActive || _tutorialSteps == null)
        {
            return;
        }

        // 第 2 步（填 ID）没填就不让走 —— 后面每一步的正文都要用到这个 ID
        if (_tutorialStepIndex == 1 && KillFeedbackPlayerIdTextBox.Text.Trim().Length == 0)
        {
            KillFeedbackTutorialHintText.Text = "请先在上面的「你自己的 ID」框里填上你的游戏 ID（只填名字，称号和 VIP 前缀不用管），再点下一步。";
            KillFeedbackTutorialHintText.Visibility = Visibility.Visible;
            return;
        }

        if (_tutorialStepIndex >= _tutorialSteps.Length - 1)
        {
            EndTutorial();
            return;
        }

        ShowTutorialStep(_tutorialStepIndex + 1);
    }

    private void KillFeedbackTutorialPrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_tutorialActive)
        {
            return;
        }

        ShowTutorialStep(_tutorialStepIndex - 1);
    }

    private void KillFeedbackTutorialExitButton_Click(object sender, RoutedEventArgs e) => EndTutorial();

    private void EndTutorial()
    {
        _tutorialActive = false;
        KillFeedbackTutorialFlashStop();
        ClearTutorialHighlights();
        UpdateTutorialOverlayVisibility();
    }

    private void UpdateTutorialOverlayVisibility()
    {
        if (KillFeedbackTutorialOverlay == null)
        {
            return;
        }

        var show = _tutorialActive && KillFeedPanel != null && KillFeedPanel.IsVisible;
        KillFeedbackTutorialOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把某个容器高亮出来（alarm = 红色，用来讲"这样写是错的"）。</summary>
    private void HighlightTutorialTarget(Border? box, bool alarm)
    {
        if (box == null)
        {
            return;
        }

        box.BorderThickness = new Thickness(alarm ? 3 : 2);
        box.BorderBrush = (Brush)FindResource(alarm ? "DangerBrush" : "PrimaryBrush");
        box.Background = (Brush)FindResource(alarm ? "DangerSoftBrush" : "PrimarySoftBrush");
    }

    private void ClearTutorialHighlights()
    {
        KillFeedbackTutorialFlashStop();
        ClearTutorialHighlight(KillFeedbackPlayerIdHighlight);
        ClearTutorialHighlight(KillFeedbackRulesHighlight);
    }

    private static void ClearTutorialHighlight(Border? box)
    {
        if (box == null)
        {
            return;
        }

        box.BorderThickness = new Thickness(0);
        box.BorderBrush = null;
        box.Background = null;
    }

    /// <summary>红色高亮一下规则框（讲错在哪那两步用），1.6 秒后回到琥珀色高亮。</summary>
    private void FlashTutorialRulesRed()
    {
        HighlightTutorialTarget(KillFeedbackRulesHighlight, true);
        ScrollTutorialTargetIntoView(KillFeedbackRulesHighlight);

        _tutorialFlashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _tutorialFlashTimer.Stop();
        _tutorialFlashTimer.Tick -= TutorialFlashTimer_Tick;
        _tutorialFlashTimer.Tick += TutorialFlashTimer_Tick;
        _tutorialFlashTimer.Start();
    }

    private void TutorialFlashTimer_Tick(object? sender, EventArgs e)
    {
        KillFeedbackTutorialFlashStop();
        HighlightTutorialTarget(KillFeedbackRulesHighlight, false);
    }

    private void KillFeedbackTutorialFlashStop()
    {
        if (_tutorialFlashTimer == null)
        {
            return;
        }

        _tutorialFlashTimer.Stop();
        _tutorialFlashTimer.Tick -= TutorialFlashTimer_Tick;
    }

    /// <summary>把目标滚到视口偏上的位置 —— 引导卡片浮在底部，别让它挡住正在讲的东西。</summary>
    private void ScrollTutorialTargetIntoView(FrameworkElement? target)
    {
        if (target == null || KillFeedPanel == null)
        {
            return;
        }

        try
        {
            var y = target.TransformToAncestor(KillFeedPanel).Transform(new Point(0, 0)).Y;
            KillFeedPanel.ScrollToVerticalOffset(Math.Max(0, KillFeedPanel.VerticalOffset + y - 150));
        }
        catch
        {
            // 还没布局好就算了，下一步会再滚一次
        }
    }

    /// <summary>直接写规则框（引导在替用户改规则），一次写完整套：入设置、存盘、刷新横幅。</summary>
    private void SetTutorialRules(string text)
    {
        KillFeedbackRulesTextBox.Text = text.Replace("{id}", TutorialId());
        KillFeedbackRulesTextBox.CaretIndex = KillFeedbackRulesTextBox.Text.Length;
        CaptureKillFeedbackRuleInputs();
        SaveKillFeedbackSettings();
        RefreshKillFeedbackStats();
    }

    /// <summary>规则框新开一行（讲"服务器不止一种击杀消息"那步用）。</summary>
    private void AppendTutorialRule(string rule)
    {
        var existing = KillFeedbackRulesTextBox.Text.Replace("\r\n", "\n").TrimEnd('\n');
        SetTutorialRules(existing.Length == 0 ? rule : existing + "\n" + rule);
    }

    /// <summary>把规则框最后一行换掉（讲"砍掉 # 后面的内容"那步用）。</summary>
    private void ReplaceLastTutorialRule(string rule)
    {
        var lines = KillFeedbackRulesTextBox.Text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count == 0)
        {
            SetTutorialRules(rule);
            return;
        }

        lines[lines.Count - 1] = rule;
        SetTutorialRules(string.Join("\n", lines));
    }

    private void RefreshKillFeedbackStats()
    {
        RefreshKillFeedbackPrereq();
        RefreshKillFeedbackRuleHint();

        if (KillFeedbackStatsText == null)
        {
            return;
        }

        if (!_settings.KillFeedback.Enabled)
        {
            KillFeedbackStatsText.Text = Copy.KillFeedbackOff;
            return;
        }

        if (!_killFeedbackMatcher.IsReady)
        {
            KillFeedbackStatsText.Text = Copy.KillFeedbackNoPattern;
            return;
        }

        var text = Copy.KillFeedbackStats(
            _killFeedbackMatcher.RuleCount,
            _killFeedbackMatcher.SeenCount,
            _killFeedbackMatcher.MatchedCount,
            _killFeedbackMatcher.LastMatchAt,
            _killFeedbackMatcher.LastMatchedRule);

        if (_killFeedbackMatcher.LastWarning.Length > 0)
        {
            text += " —— " + _killFeedbackMatcher.LastWarning;
        }

        // 第二行单独说"游戏那边"—— 匹配和画面是两个独立的故障点，分开显示才好定位
        text += Environment.NewLine + "游戏那边：" + DescribeGameSide();

        if (_killFeedbackSendNote.Length > 0)
        {
            text += " —— " + _killFeedbackSendNote;
        }

        KillFeedbackStatsText.Text = text;
    }

    /// <summary>
    /// 聊天线收到一条消息时调用（见 Watcher_ChatLineReceived）。
    /// 传进来的是「已经过替换规则的文本」—— 和悬浮窗里显示的那一份一致。
    ///
    /// 现在只记计数：**"规则对不对"和"画面效果"是两个独立的故障点**，
    /// 先把匹配这一半单独验证掉，DLL 那半边出问题时才不会被混在一起查。
    /// </summary>
    private void NotifyKillFeedback(string replacedText)
    {
        try
        {
            if (!_settings.KillFeedback.Enabled)
            {
                return;
            }

            if (_killFeedbackMatcher.TryMatch(replacedText))
            {
                SendKillFeedbackToGame();
                NotifyKillSound();
                Dispatcher.BeginInvoke(new Action(ShowKillBanner));
            }

            RefreshKillFeedbackStats();
        }
        catch
        {
            // 击杀反馈出任何问题都不能影响聊天悬浮窗本身
        }
    }

    /// <summary>
    /// <summary>
    /// 把"刚击杀了"这件事送到游戏进程里的 DLL。
    /// 原生侧会按当前配置数组同时播放所有已启用效果，每个效果用自己的大小。
    /// </summary>
    private void SendKillFeedbackToGame()
    {
        ApplyKillFeedbackParameters();

        var configs = new List<KillEffectConfig>();
        AddKillEffectConfig(configs, KillFeedbackEffectKind.EdgePulse, _settings.KillFeedback.EdgeEnabled, _settings.KillFeedback.EdgeStrength, ParseKillEdgeColor(_settings.KillFeedback.EdgeColor));
        AddKillEffectConfig(configs, KillFeedbackEffectKind.VignettePulse, _settings.KillFeedback.VignetteEnabled, _settings.KillFeedback.VignetteStrength);
        AddKillEffectConfig(configs, KillFeedbackEffectKind.Chromatic, _settings.KillFeedback.ChromaticEnabled, _settings.KillFeedback.ChromaticStrength);
        AddKillEffectConfig(configs, KillFeedbackEffectKind.ZoomPunch, _settings.KillFeedback.ZoomEnabled, _settings.KillFeedback.ZoomStrength);
        AddKillEffectConfig(configs, KillFeedbackEffectKind.Shake, _settings.KillFeedback.ShakeEnabled, _settings.KillFeedback.ShakeStrength);
        AddKillEffectConfig(configs, KillFeedbackEffectKind.Flash, _settings.KillFeedback.FlashEnabled, _settings.KillFeedback.FlashStrength);
        AddKillEffectConfig(configs, KillFeedbackEffectKind.Shockwave, _settings.KillFeedback.ShockwaveEnabled, _settings.KillFeedback.ShockwaveStrength);
        AddKillEffectConfig(configs, KillFeedbackEffectKind.Glitch, _settings.KillFeedback.GlitchEnabled, _settings.KillFeedback.GlitchStrength);

        if (configs.Count == 0)
        {
            _killFeedbackSendNote = "没有启用任何效果";
            return;
        }

        try
        {
            _motionBlur.WriteKillEffects(configs);
            _motionBlur.RequestKillFeedbackEffects(
                (uint)Math.Clamp(_settings.KillFeedback.DurationMs, 60.0, 5000.0));

            _killFeedbackSendNote = "";
        }
        catch (Exception ex)
        {
            _killFeedbackSendNote = "送到游戏失败：" + ex.Message;
        }
    }

    private static void AddKillEffectConfig(
        List<KillEffectConfig> configs, KillFeedbackEffectKind kind, bool enabled, double strength,
        uint reserved = 0u)
    {
        if (!enabled)
        {
            return;
        }

        var clamped = (float)Math.Clamp(strength, 0.0, 5.0);
        if (clamped <= 0.0001f)
        {
            return;
        }

        configs.Add(new KillEffectConfig
        {
            Kind = (uint)kind,
            Strength = clamped,
            Enabled = 1u,
            Reserved = reserved,
        });
    }

    /// <summary>把 #RRGGBB 解析成 0x00RRGGBB；解析失败时退回暖白。</summary>
    private static uint ParseKillEdgeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0xFFF5E0u;
        }

        var text = value.Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            text = text.Substring(1);
        }

        if (text.Length == 3)
        {
            text = new string(new[] { text[0], text[0], text[1], text[1], text[2], text[2] });
        }

        if (text.Length == 6 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return rgb & 0xFFFFFFu;
        }

        return 0xFFF5E0u;
    }

    /// <summary>刷新边缘颜色按钮的小色块。</summary>
    private void UpdateKillEdgeColorSwatch()
    {
        if (KillEffectEdgeColorSwatch == null)
        {
            return;
        }

        var rgb = ParseKillEdgeColor(_settings.KillFeedback.EdgeColor);
        KillEffectEdgeColorSwatch.Background = new SolidColorBrush(Color.FromRgb(
            (byte)((rgb >> 16) & 0xFFu),
            (byte)((rgb >> 8) & 0xFFu),
            (byte)(rgb & 0xFFu)));
    }

    private void KillEffectEdgeColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        var current = ParseKillEdgeColor(_settings.KillFeedback.EdgeColor);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(
                (int)((current >> 16) & 0xFFu),
                (int)((current >> 8) & 0xFFu),
                (int)(current & 0xFFu)),
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        _settings.KillFeedback.EdgeColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateKillEdgeColorSwatch();
        ApplyKillFeedbackParameters();
        QueueKillFeedbackSave();
        if (!_killPreviewAnimating && _killPreviewBitmap != null)
        {
            RenderKillPreviewFrame(KillFeedbackEffects.PeakElapsedMs(_killPreviewParams));
        }
    }

    // ===================== 击杀图标（原「击杀横幅」） =====================
    //
    // v8 起软件不再附带任何内置图标，图标全部由用户导入。
    // 列表项带勾选框（参与随机抽取），配一个「随机显示」开关。

    /// <summary>列表里显示的全部图标（用户导入的，顺序就是界面上的顺序）。</summary>
    private readonly ObservableCollection<KillIconChoice> _killIconChoices = new();

    /// <summary>上一次抽中的图标路径。用来避免连着两次显示同一张（观感上像随机没生效）。</summary>
    private string _lastShownKillIcon = "";

    /// <summary>上一次抽中的提示音路径。同上。</summary>
    private string _lastPlayedKillSound = "";

    /// <summary>把配置里的击杀图标设置灌进界面控件。</summary>
    private void ApplyBannerSettingsToUi()
    {
        var k = _settings.KillFeedback;

        KillBannerPosXSlider.Value = Math.Clamp(k.BannerPosX, 0.0, 1.0);
        KillBannerPosYSlider.Value = Math.Clamp(k.BannerPosY, 0.0, 1.0);
        KillBannerSizeSlider.Value = Math.Clamp(k.BannerIconSize, 24, 320);
        KillBannerFadeInSlider.Value = Math.Clamp(k.BannerFadeInMs, 0, 2000);
        KillBannerHoldSlider.Value = Math.Clamp(k.BannerHoldMs, 0, 6000);
        KillBannerFadeOutSlider.Value = Math.Clamp(k.BannerFadeOutMs, 0, 2000);
        KillBannerRandomCheckBox.IsChecked = k.BannerIconRandom;
        KillBannerSequentialCheckBox.IsChecked = k.BannerIconSequential;

        RebuildKillIconChoices();
        UpdateKillIconColorSwatch();
        RefreshKillIconPreview();
        RefreshKillBannerStatus();
    }

    /// <summary>把界面上的击杀图标设置收回配置。</summary>
    private void CollectBannerSettingsFromUi()
    {
        var k = _settings.KillFeedback;

        k.BannerPosX = Math.Clamp(KillBannerPosXSlider.Value, 0.0, 1.0);
        k.BannerPosY = Math.Clamp(KillBannerPosYSlider.Value, 0.0, 1.0);
        k.BannerIconSize = Math.Clamp(KillBannerSizeSlider.Value, 24, 320);
        k.BannerFadeInMs = Math.Clamp(KillBannerFadeInSlider.Value, 0, 2000);
        k.BannerHoldMs = Math.Clamp(KillBannerHoldSlider.Value, 0, 6000);
        k.BannerFadeOutMs = Math.Clamp(KillBannerFadeOutSlider.Value, 0, 2000);
        k.BannerIconRandom = KillBannerRandomCheckBox.IsChecked == true;
        k.BannerIconSequential = KillBannerSequentialCheckBox.IsChecked == true;

        // 图片列表和勾选状态都是从配置直接驱动的（列表项的双向绑定直接改配置集合），
        // 这里不用回收 —— 回收反而容易把用户刚点的勾覆盖掉。
    }

    private void KillBannerSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        _settings.KillFeedback.BannerEnabled = KillBannerEnableCheckBox.IsChecked == true;
        CollectBannerSettingsFromUi();
        QueueKillFeedbackSave();
        RefreshLockedCards();
        RefreshKillBannerStatus();
    }

    /// <summary>「随机显示」开关变了。</summary>
    private void KillBannerRandomCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        var random = KillBannerRandomCheckBox.IsChecked == true;
        _settings.KillFeedback.BannerIconRandom = random;

        // 随机和顺序都是"轮着放"，同时开没有意义 —— 勾一个就把另一个取消
        if (random && KillBannerSequentialCheckBox.IsChecked == true)
        {
            KillBannerSequentialCheckBox.IsChecked = false;
            _settings.KillFeedback.BannerIconSequential = false;
        }

        // 勾选框跟着显隐：两种"轮着放"都关掉时才收起（那时固定用列表里选中的那张）
        ApplyKillIconCheckBoxVisibility(random || _settings.KillFeedback.BannerIconSequential);

        QueueKillFeedbackSave();
        RefreshKillBannerStatus();
        RefreshKillIconPreview();
    }

    /// <summary>「顺序播放」开关变了。</summary>
    private void KillBannerSequentialCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        var sequential = KillBannerSequentialCheckBox.IsChecked == true;
        _settings.KillFeedback.BannerIconSequential = sequential;

        if (sequential && KillBannerRandomCheckBox.IsChecked == true)
        {
            KillBannerRandomCheckBox.IsChecked = false;
            _settings.KillFeedback.BannerIconRandom = false;
        }

        // 打开顺序播放时得先有"轮到谁"：列表里没有选中项就退回第一张
        if (sequential && KillBannerIconListBox?.SelectedItem == null)
        {
            KillBannerIconListBox!.SelectedItem = _killIconChoices.FirstOrDefault();
        }

        ApplyKillIconCheckBoxVisibility(sequential || _settings.KillFeedback.BannerIconRandom);

        QueueKillFeedbackSave();
        RefreshKillBannerStatus();
        RefreshKillIconPreview();
    }

    /// <summary>列表项上的勾选框变了（参与随机抽取）。</summary>
    private void KillBannerIconPicked_Changed(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        SyncBannerPickedFromChoices();
        QueueKillFeedbackSave();
        RefreshKillBannerStatus();
    }

    /// <summary>
    /// 列表选中项变了。关掉随机时「选中哪张就用哪张」，所以这里要刷新预览；
    /// 开随机时选中只是光标，不影响抽签，刷一下也无害（预览会重抽一张）。
    /// </summary>
    private void KillBannerIconListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        RefreshKillIconPreview();
        RefreshKillBannerStatus();
    }

    /// <summary>把列表项的勾选状态同步回配置。</summary>
    private void SyncBannerPickedFromChoices()
    {
        var picked = _settings.KillFeedback.BannerIconPicked;
        picked.Clear();
        picked.AddRange(_killIconChoices.Where(c => c.IsPicked).Select(c => c.File));
    }

    /// <summary>位置 / 大小 / 三段时长 滑块共用。</summary>
    private void KillBannerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        CollectBannerSettingsFromUi();
        RefreshKillIconPreview();
        RefreshKillBannerStatus();
        QueueKillFeedbackSave();
    }

    /// <summary>【添加图标…】把用户自己挑的图片加进列表。</summary>
    private void KillBannerAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "选一张图片当击杀图标（可多选）",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var failed = new List<string>();

        foreach (var file in dialog.FileNames)
        {
            // 当场验一次能不能解码。选了张坏图却不报，用户要等到真击杀才发现是空白。
            if (!KillBannerWindow.CanLoadImage(file))
            {
                failed.Add(Path.GetFileName(file));
                continue;
            }

            if (_settings.KillFeedback.BannerIconFiles.Any(
                    f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _settings.KillFeedback.BannerIconFiles.Add(file);

            // 新加的默认勾上 —— 用户刚导入的显然是想用的
            if (!_settings.KillFeedback.BannerIconPicked.Any(
                    f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.KillFeedback.BannerIconPicked.Add(file);
            }
        }

        RebuildKillIconChoices();
        RefreshKillIconPreview();
        RefreshKillBannerStatus();
        QueueKillFeedbackSave();

        if (failed.Count > 0)
        {
            KillBannerStatusText.Text =
                "这几张读不出来，没加进去：" + string.Join("、", failed);
        }
    }

    /// <summary>【移除】把列表里选中的图标拿掉。</summary>
    private void KillBannerRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        if (KillBannerIconListBox.SelectedItem is not KillIconChoice choice)
        {
            KillBannerStatusText.Text = "先在列表里选一个，再点移除。";
            return;
        }

        _settings.KillFeedback.BannerIconFiles.RemoveAll(
            f => string.Equals(f, choice.File, StringComparison.OrdinalIgnoreCase));
        _settings.KillFeedback.BannerIconPicked.RemoveAll(
            f => string.Equals(f, choice.File, StringComparison.OrdinalIgnoreCase));

        _killIconChoices.Remove(choice);
        KillBannerIconListBox.SelectedItem = _killIconChoices.FirstOrDefault();

        RefreshKillIconPreview();
        RefreshKillBannerStatus();
        QueueKillFeedbackSave();
    }

    /// <summary>点色块换图标的外发光颜色。</summary>
    private void KillBannerIconColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        var current = ParseKillEdgeColor(_settings.KillFeedback.BannerIconColor);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(
                (int)((current >> 16) & 0xFFu),
                (int)((current >> 8) & 0xFFu),
                (int)(current & 0xFFu)),
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        _settings.KillFeedback.BannerIconColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateKillIconColorSwatch();
        RefreshKillIconPreview();
        QueueKillFeedbackSave();
    }

    /// <summary>【试一下】—— 按当前设置真的在屏幕上弹一次图标。</summary>
    private void KillBannerTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        try
        {
            if (!PreflightKillIcon())
            {
                return;
            }

            ShowKillIcon();
        }
        catch (Exception ex)
        {
            KillBannerStatusText.Text = "试不了：" + ex.Message;
        }
    }

    /// <summary>
    /// 【拖动定位】—— 在屏幕上弹出图标，让用户直接拖到想要的位置。
    ///
    /// 松手时把新坐标写回配置，并退出拖动模式（把鼠标穿透还给游戏）。
    /// 这件事必须在主窗口还活着的时候做 —— 用户可能拖完就关软件，
    /// 拖动窗口的 Closed 里没人接管，坐标就丢了。
    /// </summary>
    private void KillBannerDragButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        if (KillBannerDragButton.IsChecked == true)
        {
            StartKillIconDrag();
        }
        else
        {
            StopKillIconDrag();
        }
    }

    private void StartKillIconDrag()
    {
        if (!PreflightKillIcon())
        {
            KillBannerDragButton.IsChecked = false;
            return;
        }

        CollectBannerSettingsFromUi();
        var k = _settings.KillFeedback;

        EnsureKillBannerWindow();

        var loaded = _killBanner!.BeginDragMode(
            ResolveCurrentKillIconPreviewPath(),
            k.BannerPosX,
            k.BannerPosY,
            Math.Clamp(k.BannerIconSize, 24, 320),
            ParseKillEdgeColorToColor(k.BannerIconColor));

        if (!loaded)
        {
            KillBannerStatusText.Text = "没有可拖的图标 —— 先在上面添加一张图片。";
            _killBanner.EndDragMode();
            KillBannerDragButton.IsChecked = false;
            return;
        }

        KillBannerStatusText.Text = "把屏幕上的图标拖到想要的位置，松手即保存。";
    }

    private void StopKillIconDrag()
    {
        _killBanner?.EndDragMode();
        RefreshKillBannerStatus();
    }

    /// <summary>拖动松手：把百分比坐标写进配置和滑块。</summary>
    private void KillBanner_PositionChanged(double posX, double posY)
    {
        // 拖动模式是一次性的，松手就结束 —— 不然鼠标一直点不到游戏
        KillBannerDragButton.IsChecked = false;

        _settings.KillFeedback.BannerPosX = posX;
        _settings.KillFeedback.BannerPosY = posY;

        // 滑块跟着走。它俩的 ValueChanged 会调 CollectBannerSettingsFromUi，
        // 但那里读的就是刚写进去的配置，所以不会来回打架。
        _killFeedbackUiReady = false;
        KillBannerPosXSlider.Value = posX;
        KillBannerPosYSlider.Value = posY;
        _killFeedbackUiReady = true;

        KillBannerStatusText.Text =
            $"位置已保存：横向 {posX:P0}，纵向 {posY:P0}";

        QueueKillFeedbackSave();
    }

    /// <summary>
    /// 开播之前先检查一遍。返回 false 表示不能放，并且已经把原因写进状态行。
    /// </summary>
    private bool PreflightKillIcon()
    {
        var k = _settings.KillFeedback;

        if (k.BannerIconFiles.Count == 0)
        {
            KillBannerStatusText.Text = "还没有图标 —— 先点【添加图标…】导入一张图片。";
            return false;
        }

        if (!k.BannerEnabled)
        {
            // 试一下 / 拖动定位是预览行为，总开关关着也应该能看效果，
            // 但要提醒一声，免得用户以为已经生效了。
            KillBannerStatusText.Text = "提示：总开关还没打开，现在只在预览，击杀时不会真的弹。";
        }

        return true;
    }

    /// <summary>
    /// 取一张图标用来预览 / 拖动。开了随机就按随机规则抽，没开就用第一张。
    /// 预览必须和真实击杀走同一套抽取逻辑，否则"预览里是这张、游戏里是另一张"。
    /// </summary>
    private string ResolveCurrentKillIconPreviewPath()
    {
        var k = _settings.KillFeedback;

        // 关掉随机时用「列表里选中的那张」；开随机才从勾中的抽。
        var selected = (KillBannerIconListBox?.SelectedItem as KillIconChoice)?.File;

        return RandomPicker.Pick(
                   k.BannerIconFiles, k.BannerIconPicked, k.BannerIconRandom,
                   _lastShownKillIcon, selected, k.BannerIconSequential)
               ?? k.BannerIconFiles.FirstOrDefault()
               ?? "";
    }

    /// <summary>按「配置里的文件列表 + 勾选状态」重建列表。</summary>
    private void RebuildKillIconChoices()
    {
        if (KillBannerIconListBox == null)
        {
            return;
        }

        var k = _settings.KillFeedback;

        _killIconChoices.Clear();
        foreach (var file in k.BannerIconFiles)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            _killIconChoices.Add(new KillIconChoice(file)
            {
                // 勾选状态从配置读回来。老配置里 picked 是空的 → 全不勾，
                // 界面上会提示"一个都没勾"，比默默变成"全勾"更诚实。
                IsPicked = k.BannerIconPicked.Any(
                    p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase))
            });
        }

        KillBannerIconListBox.ItemsSource = _killIconChoices;
        KillBannerIconListBox.SelectedItem = _killIconChoices.FirstOrDefault();

        // 勾选框只在开随机时才显示
        ApplyKillIconCheckBoxVisibility(k.BannerIconRandom || k.BannerIconSequential);

        // 挂钩子：用户点勾选框 → 双向绑定写回 IsPicked → 通知 → 同步进配置。
        ClearHandlers(_killIconHandlers);
        HookKillIconNotifications(_killIconChoices);
    }

    private void UpdateKillIconColorSwatch()
    {
        if (KillBannerIconColorSwatch == null)
        {
            return;
        }

        var rgb = ParseKillEdgeColor(_settings.KillFeedback.BannerIconColor);
        KillBannerIconColorSwatch.Background = new SolidColorBrush(Color.FromRgb(
            (byte)((rgb >> 16) & 0xFFu),
            (byte)((rgb >> 8) & 0xFFu),
            (byte)(rgb & 0xFFu)));
    }

    /// <summary>把当前图标画进设置页里的小预览框。</summary>
    private void RefreshKillIconPreview()
    {
        if (KillBannerPreviewImage == null)
        {
            return;
        }

        var k = _settings.KillFeedback;
        var path = ResolveCurrentKillIconPreviewPath();
        var size = Math.Clamp(k.BannerIconSize, 24, 320) * 0.5; // 预览框里缩一半，免得撑爆

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            KillBannerPreviewImage.Source = null;
            KillBannerPreviewImage.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            KillBannerPreviewImage.Source = bmp;
            KillBannerPreviewImage.Width = size;
            KillBannerPreviewImage.Height = size;
            KillBannerPreviewImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            KillBannerPreviewImage.Source = null;
            KillBannerPreviewImage.Visibility = Visibility.Collapsed;
            KillBannerStatusText.Text = "这张图读不出来：" + ex.Message;
        }
    }

    /// <summary>状态行：列表有几张 / 勾了几张 / 总时长多少，一眼能看明白。</summary>
    /// <summary>把列表的选中项（紫色高亮）挪到指定文件上 —— 顺序播放靠它显示"轮到谁"。</summary>
    private void SelectKillIcon(string file)
    {
        var match = _killIconChoices.FirstOrDefault(
            c => string.Equals(c.File, file, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            return;
        }

        KillBannerIconListBox.SelectedItem = match;
        KillBannerIconListBox.ScrollIntoView(match);
    }

    private void RefreshKillBannerStatus()
    {
        if (KillBannerStatusText == null)
        {
            return;
        }

        var k = _settings.KillFeedback;

        if (k.BannerIconFiles.Count == 0)
        {
            KillBannerStatusText.Text = "还没有图标 —— 点【添加图标…】导入一张图片。";
            return;
        }

        var pickedCount = k.BannerIconPicked.Count;

        // 关掉随机时说的是"用列表里选中的那张"，得把名字带上是哪张 ——
        // 不然用户看着状态行不知道到底在用哪张。
        var selectedName = (KillBannerIconListBox?.SelectedItem as KillIconChoice)?.Name;

        var parts = new List<string>
        {
            k.BannerIconFiles.Count + " 张图标",
            k.BannerIconRandom
                ? (pickedCount == 0
                    ? "随机显示（一张都没勾，会从全部里抽）"
                    : $"随机显示（已勾 {pickedCount} 张）")
                : k.BannerIconSequential
                    ? (string.IsNullOrWhiteSpace(selectedName)
                        ? "顺序播放（点列表里的一张来决定轮到谁）"
                        : $"顺序播放 —— 下一个轮到「{selectedName}」")
                    : (string.IsNullOrWhiteSpace(selectedName)
                        ? "固定用列表中选中的那张"
                        : $"固定用「{selectedName}」")
        };

        parts.Add($"总时长 {k.BannerTotalMs:F0} ms（{k.BannerFadeInMs:F0} + {k.BannerHoldMs:F0} + {k.BannerFadeOutMs:F0}）");

        KillBannerStatusText.Text = string.Join(" · ", parts);
    }

    private static Color ParseKillEdgeColorToColor(string? value)
    {
        var rgb = ParseKillEdgeColor(value);
        return Color.FromRgb(
            (byte)((rgb >> 16) & 0xFFu),
            (byte)((rgb >> 8) & 0xFFu),
            (byte)(rgb & 0xFFu));
    }

    private void ShowKillBanner()
    {
        ShowKillIcon();
    }

    /// <summary>按当前配置在屏幕上弹一次击杀图标。</summary>
    private void ShowKillIcon()
    {
        var k = _settings.KillFeedback;
        if (!k.BannerEnabled || k.BannerIconFiles.Count == 0)
        {
            return;
        }

        try
        {
            var path = ResolveCurrentKillIconPreviewPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            // 窗口是常驻的，这里只确保它在场 —— 启动时就建好了，正常不会走到创建分支。
            EnsureKillBannerWindow();

            // 抽中的路径记下来，下一抽避开它（连着两次一样会让人怀疑随机没生效）
            _lastShownKillIcon = path;

            // 顺序播放：这一张放完了，把"轮到谁"的高亮挪到下一个（列表选中项 = 紫色高亮）
            if (k.BannerIconSequential)
            {
                var next = RandomPicker.Next(k.BannerIconFiles, k.BannerIconPicked, path);
                if (!string.IsNullOrEmpty(next))
                {
                    SelectKillIcon(next);
                }
            }

            var loaded = _killBanner!.ShowIcon(
                path,
                k.BannerPosX,
                k.BannerPosY,
                Math.Clamp(k.BannerIconSize, 24, 320),
                ParseKillEdgeColorToColor(k.BannerIconColor),
                Math.Clamp(k.BannerFadeInMs, 0, 2000),
                Math.Clamp(k.BannerHoldMs, 0, 6000),
                Math.Clamp(k.BannerFadeOutMs, 0, 2000));

            if (!loaded)
            {
                KillBannerStatusText.Text = "这张图读不出来，跳过这次显示：" + path;
            }
        }
        catch
        {
            // 图标只是附加反馈，失败不能影响聊天悬浮窗
        }
    }

    /// <summary>
    /// 游戏进程那边的情况 —— 这一行是判断"事件到底送到了没有"的关键：
    /// 匹配涨了但这里的"接收"数为 0，说明事件没送到（多半是没注入，或者 DLL 是旧的没协商上）。
    /// </summary>
    private string DescribeGameSide()
    {
        if (_motionBlur.TargetProcessId <= 0)
        {
            return "还没注入游戏";
        }

        try
        {
            var status = _motionBlur.GetStatus();
            if (!status.HookInstalled)
            {
                return "游戏里的钩子没挂上";
            }

            var text = "DLL 接收 " + status.EventPlayCount + " 次";

            if (status.EventActiveMs > 0)
            {
                text += " · 正在播放（缩放 "
                        + (status.EventZoomMilli / 1000.0).ToString("0.000")
                        + "x，还剩 " + status.EventActiveMs + "ms）";
            }

            return text;
        }
        catch (Exception ex)
        {
            return "读游戏状态失败：" + ex.Message;
        }
    }

    private void KillFeedbackEnableCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        _settings.KillFeedback.Enabled = KillFeedbackEnableCheckBox.IsChecked == true;
        SaveKillFeedbackSettings();
        RefreshKillFeedbackStats();
    }

    private void KillFeedbackRuleInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        CaptureKillFeedbackRuleInputs();
        SaveKillFeedbackSettings();
    }

    private void KillFeedbackStripCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        CaptureKillFeedbackRuleInputs();
        SaveKillFeedbackSettings();
        RefreshKillFeedbackStats();
    }

    private void KillFeedbackRulesTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        CaptureKillFeedbackRuleInputs();
        SaveKillFeedbackSettings();
        RefreshKillFeedbackStats();
    }

    private void KillFeedbackRegexRulesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_killFeedbackUiReady)
        {
            return;
        }

        CaptureKillFeedbackRuleInputs();
        SaveKillFeedbackSettings();
        RefreshKillFeedbackStats();
    }

    private void KillFeedbackBuildPatternButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureKillFeedbackRuleInputs();

        var ok = KillFeedbackMatcher.TryBuildRule(
            _settings.KillFeedback.PlayerId,
            _settings.KillFeedback.SampleMessage,
            _settings.KillFeedback.SampleEnemyId,
            _settings.KillFeedback.StripBrackets,
            out var rule,
            out var description,
            out var error);

        if (!ok)
        {
            KillFeedbackBuildStatusText.Text = error;
            KillFeedbackBuildStatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        // 追加一行，不覆盖已有规则 —— 一局里可能有几种格式，一条一条加进来
        var existing = KillFeedbackRulesTextBox.Text;
        var already = existing
            .Split('\n')
            .Any(line => string.Equals(line.Trim(), rule, StringComparison.Ordinal));

        if (already)
        {
            KillFeedbackBuildStatusText.Text = $"规则「{rule}」已经在列表里了，没有重复加。";
            KillFeedbackBuildStatusText.Foreground = (Brush)FindResource("TextTertiaryBrush");
            return;
        }

        if (existing.Length > 0 && !existing.EndsWith("\n", StringComparison.Ordinal))
        {
            existing += Environment.NewLine;
        }

        KillFeedbackRulesTextBox.Text = existing + rule;
        KillFeedbackRulesTextBox.SelectionStart = KillFeedbackRulesTextBox.Text.Length;

        KillFeedbackBuildStatusText.Text = description;
        KillFeedbackBuildStatusText.Foreground = (Brush)FindResource("TextTertiaryBrush");

        CaptureKillFeedbackRuleInputs();
        SaveKillFeedbackSettings();
        RefreshKillFeedbackStats();
    }

    private void KillFeedbackTestButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureKillFeedbackRuleInputs();

        var sample = KillFeedbackTestTextBox.Text;
        if (string.IsNullOrWhiteSpace(sample))
        {
            KillFeedbackTestResultText.Text = "先粘一条消息进去试试。";
            return;
        }

        _killFeedbackMatcher.TestMatch(
            _settings.KillFeedback.MatchRules,
            _settings.KillFeedback.MatchRulesAreRegex,
            sample,
            _settings.KillFeedback.StripBrackets,
            out var message);

        KillFeedbackTestResultText.Text = message;
    }
}
