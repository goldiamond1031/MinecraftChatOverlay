using System;
using System.Windows;
using System.Windows.Threading;

namespace MinecraftChatOverlay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 捕获 UI 线程未处理异常
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        // 捕获非 UI 线程未处理异常
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // 宣传片素材导出模式（--promo-capture）：等主窗口建出来就跑一遍导出，然后自动退出。
        if (Promo.PromoCapture.Requested)
        {
            var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };

            timer.Tick += (_, _) =>
            {
                if (MainWindow is MainWindow main)
                {
                    timer.Stop();
                    _ = Promo.PromoCapture.RunAsync(main);
                }
            };

            timer.Start();
        }

        // 宣传片渲染模式（--promo-render）：逐帧画 45 秒成片，画完自动退出。
        if (Promo.PromoRenderer.Requested)
        {
            var renderTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };

            renderTimer.Tick += (_, _) =>
            {
                renderTimer.Stop();
                if (MainWindow is Window main)
                {
                    main.WindowState = WindowState.Minimized; // 渲染用不到窗口，缩到任务栏
                }

                _ = new Promo.PromoRenderer().RunAsync(Dispatcher);
            };

            renderTimer.Start();
        }

        // 背景素材模式（--promo-bg）：只导出能无缝循环的背景，导出完自动退出。
        if (Promo.PromoRenderer.BackgroundRequested)
        {
            var bgTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };

            bgTimer.Tick += (_, _) =>
            {
                bgTimer.Stop();
                if (MainWindow is Window main)
                {
                    main.WindowState = WindowState.Minimized;
                }

                _ = Task.Run(async () =>
                {
                    var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mco-promo", "bg");
                    await new Promo.PromoRenderer().RunBackgroundAsync(dir);
                    Dispatcher.Invoke(() => Shutdown());
                });
            };

            bgTimer.Start();
        }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(Copy.Unhandled(e.Exception), Copy.UnhandledTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // 阻止程序崩溃退出
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        MessageBox.Show(Copy.Unhandled(e.ExceptionObject), Copy.UnhandledTitle, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
