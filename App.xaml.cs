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
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"发生未处理异常：\n{e.Exception}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // 阻止程序崩溃退出
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"发生未处理异常：\n{e.ExceptionObject}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}