using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MinecraftChatOverlay.Promo;

/// <summary>
/// 宣传片素材导出（临时工具，用完可从工程里删掉）。
///
/// 用法：MinecraftChatOverlay.exe --promo-capture
/// 干的事：把主窗口切到各个页面、各截一张 2 倍分辨率的 PNG，另存悬浮窗和击杀横幅，
/// 全部落到 %TEMP%\mco-promo\shots\，最后写一个 DONE.txt 并自动退出。
///
/// 为什么不用截屏 API：系统截图那条路（PowerShell 的 Add-Type）被安全策略挡着，
/// 而且自绘导出反而更好 —— 没有桌面缩放的糊、没有鼠标指针、能出 2 倍图。
/// </summary>
internal static class PromoCapture
{
    public static bool Requested =>
        Environment.GetCommandLineArgs().Skip(1)
            .Any(a => a.Equals("--promo-capture", StringComparison.OrdinalIgnoreCase));

    private static string OutDir => Path.Combine(Path.GetTempPath(), "mco-promo", "shots");

    private const double Scale = 2.0;

    public static async Task RunAsync(MainWindow w)
    {
        try
        {
            Directory.CreateDirectory(OutDir);

            // 先把上次的成品清掉，免得新旧混在一起看不出问题
            foreach (var old in Directory.GetFiles(OutDir, "*.png"))
            {
                try
                {
                    File.Delete(old);
                }
                catch
                {
                }
            }

            w.WindowState = WindowState.Normal;
            w.Activate();

            // 等窗口加载完、后台预热（更新检查、市场缓存）跑完，免得截到"正在加载"的状态。
            await SettleAsync(w, 3500);

            // ---------- 主窗口各页面 ----------
            await ShotPageAsync(w, "01_overlay", w.NavOverlayDisplay, w.OverlayPanel, 0);
            await ShotPageAsync(w, "02_overlay_b", w.NavOverlayDisplay, w.OverlayPanel, 430);
            await ShotPageAsync(w, "03_textrules", w.NavTextRules, w.RulesPanel, 0);
            await ShotPageAsync(w, "04_plugins", w.NavPlugins, w.PluginsPanel, 0);
            await ShotMarketAsync(w);
            await ShotPageAsync(w, "06_motionblur", w.NavMotionBlur, w.MotionBlurPanel, 0);
            await ShotPageAsync(w, "07_killfeed", w.NavKillFeed, w.KillFeedPanel, 0);
            await ShotPageAsync(w, "08_killfeed_b", w.NavKillFeed, w.KillFeedPanel, 520);
            await ShotPageAsync(w, "09_bili", w.NavBili, w.BiliPanel, 0);
            await ShotPageAsync(w, "10_about", w.NavAbout, w.AboutPanel, 0);
            await ShotPageAsync(w, "11_debug", w.NavDebug, w.DebugPanel, 0);

            // ---------- 悬浮窗（聊天悬浮窗本体） ----------
            await ShotOverlayAsync(w);

            // ---------- 击杀横幅 ----------
            await ShotKillBannerAsync(w);

            File.WriteAllText(Path.Combine(OutDir, "DONE.txt"),
                "done " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(OutDir);
                File.WriteAllText(Path.Combine(OutDir, "ERROR.txt"), ex.ToString());
            }
            catch
            {
            }
        }
        finally
        {
            try
            {
                Application.Current.Shutdown();
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------ 单页

    private static async Task ShotPageAsync(
        MainWindow w, string name, RadioButton nav, ScrollViewer panel, double scrollTo)
    {
        nav.IsChecked = true;
        await SettleAsync(w, 1100); // 等导航动效 + 面板浮入跑完

        if (scrollTo > 0 && panel is not null)
        {
            panel.ScrollToVerticalOffset(scrollTo);
            await SettleAsync(w, 400);
        }
        else if (panel is not null)
        {
            panel.ScrollToTop();
            await SettleAsync(w, 200);
        }

        SaveVisual(w, Path.Combine(OutDir, name + ".png"), Scale);
    }

    /// <summary>插件页 → 点【插件市场】进市场子视图，再截一张。</summary>
    private static async Task ShotMarketAsync(MainWindow w)
    {
        w.NavPlugins.IsChecked = true;
        await SettleAsync(w, 1100);

        w.PluginMarketButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await SettleAsync(w, 2600); // 市场要联网拉清单

        SaveVisual(w, Path.Combine(OutDir, "05_market.png"), Scale);

        // 截完回列表，免得影响后面的页面
        w.PluginMarketBackButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await SettleAsync(w, 600);
    }

    // ------------------------------------------------------------ 悬浮窗

    /// <summary>
    /// 悬浮窗是独立窗口、背景透明，所以单独开一个实例、塞几条聊天记录再渲染。
    /// 击杀播报那条消息是故意写的 —— 模板里的击杀规则能认出来，聊天区会高亮。
    /// </summary>
    private static async Task ShotOverlayAsync(MainWindow w)
    {
        var settings = Services.SettingsService.Load();
        var overlay = new OverlayWindow(settings)
        {
            Left = 120,
            Top = 120,
            ShowActivated = false,
        };

        try
        {
            overlay.Show();
            await SettleAsync(w, 700);

            overlay.AddMessage("<gd> 这波稳了");
            overlay.AddMessage("Steve 被 gd 击杀了");
            overlay.AddMessage("[19:20] Alex: 打字速度可以啊");
            overlay.AddMessage("gd 获得了成就 [探索时光]");

            await SettleAsync(w, 900);
            SaveVisual(overlay, Path.Combine(OutDir, "20_overlay_window.png"), Scale);
        }
        catch
        {
            // 截图失败不影响其它素材
        }
        finally
        {
            try
            {
                overlay.Close();
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------ 击杀横幅

    /// <summary>击杀横幅：用 res\ciallo.png 当作"用户自定义的击杀图标"钉住不淡出，截完收掉。</summary>
    private static async Task ShotKillBannerAsync(MainWindow w)
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "res", "ciallo.png");
        if (!File.Exists(icon) || !KillBannerWindow.CanLoadImage(icon))
        {
            return;
        }

        var banner = new KillBannerWindow();
        try
        {
            var halo = (Color)ColorConverter.ConvertFromString("#FF8FBE");
            banner.ShowIcon(icon, 0.5, 0.42, 120, halo, 0, 0, 0, persist: true);
            await SettleAsync(w, 900);

            // 横幅窗口铺满整个工作区，直接渲染会是一大张透明图。
            // 所以先整幅渲染，再裁出图标周围 350 逻辑像素见方的区域。
            var width = Math.Max(1, (int)Math.Round(banner.ActualWidth * Scale));
            var height = Math.Max(1, (int)Math.Round(banner.ActualHeight * Scale));
            var full = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            full.Render(banner);

            var crop = (int)(350 * Scale);
            var centerX = (int)(width * 0.5);
            var centerY = (int)(height * 0.42);
            var cropX = Math.Clamp(centerX - crop / 2, 0, width - crop);
            var cropY = Math.Clamp(centerY - crop / 2, 0, height - crop);
            var cropped = new CroppedBitmap(full, new Int32Rect(cropX, cropY, crop, crop));

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using var stream = File.Create(Path.Combine(OutDir, "21_kill_banner.png"));
            encoder.Save(stream);
        }
        catch
        {
            // 同上，单张失败不打断
        }
        finally
        {
            try
            {
                banner.EndDragMode();
                banner.Close();
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------ 工具

    private static async Task SettleAsync(Window w, int ms)
    {
        await Task.Delay(ms);
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Task.Delay(60);
    }

    /// <summary>把一个可视元素渲染成 PNG。scale = 2 就是 2 倍图。</summary>
    private static void SaveVisual(FrameworkElement element, string path, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(element.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Round(element.ActualHeight * scale));

        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
