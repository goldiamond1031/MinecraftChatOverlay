using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftChatOverlay.Services;
using QRCoder;

namespace MinecraftChatOverlay;

/// <summary>扫码登录窗口：显示二维码并轮询扫码状态，成功后把 Cookie 交给调用方。</summary>
public partial class BiliQrLoginWindow : Window
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookieContainer;
    private CancellationTokenSource? _cts;
    private bool _loggedIn;

    /// <summary>登录成功后拿到的 Cookie（未登录成功时为空）。</summary>
    public string Cookie { get; private set; } = "";

    public BiliQrLoginWindow()
    {
        InitializeComponent();

        _cookieContainer = new CookieContainer { Capacity = 500, PerDomainCapacity = 300 };
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookieContainer,
            AutomaticDecompression = DecompressionMethods.All,
            // 登录成功后可能通过重定向下发 Cookie，自己处理才能保证拿到 Set-Cookie
            AllowAutoRedirect = false
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _http.Dispose();
        };
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
    }

    private async Task RefreshAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        QrImage.Source = null;
        QrPlaceholder.Visibility = Visibility.Visible;
        QrPlaceholder.Text = "正在准备…";
        SetStatus("", false);

        // 先取设备指纹（buvid3/buvid4）再生成二维码，降低被 B站风控要求安全验证的概率
        await BiliLoginService.EnsureDeviceFingerprintAsync(_http, _cookieContainer, token);
        if (token.IsCancellationRequested || _loggedIn)
        {
            return;
        }

        QrPlaceholder.Text = "正在获取二维码…";
        var session = await BiliLoginService.GenerateQrCodeAsync(_http, token);
        if (token.IsCancellationRequested || _loggedIn)
        {
            return;
        }

        if (session == null)
        {
            QrPlaceholder.Text = "获取二维码失败";
            SetStatus("请检查网络后点「刷新二维码」重试", true);
            return;
        }

        // 二维码图片里的链接本身就是 B站登录页，手机扫码即可
        QrImage.Source = RenderQrCode(session.Value.Url);
        QrPlaceholder.Visibility = Visibility.Collapsed;
        SetStatus("等待扫码…", false);

        _ = PollLoopAsync(session.Value.Key, token);
    }

    private async Task PollLoopAsync(string qrCodeKey, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // 2 秒一次，接近官方网页登录的轮询节奏（太频繁更容易被风控盯上）
                await Task.Delay(2000, token);
                var result = await BiliLoginService.PollAsync(_http, _cookieContainer, qrCodeKey, token);
                if (token.IsCancellationRequested || _loggedIn)
                {
                    return;
                }

                SetStatus(result.Message, result.State is QrLoginState.Failed);

                switch (result.State)
                {
                    case QrLoginState.Confirmed:
                        _loggedIn = true;
                        Cookie = result.Cookie;
                        SetStatus("登录成功，正在保存…", false);
                        DialogResult = true;
                        return;

                    case QrLoginState.Expired:
                        QrPlaceholder.Text = "二维码已过期";
                        QrPlaceholder.Visibility = Visibility.Visible;
                        QrImage.Source = null;
                        return;

                    case QrLoginState.Failed:
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus("查询失败：" + ex.Message, true);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("TextSecondaryBrush");
    }

    private static BitmapImage RenderQrCode(string url)
    {
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qrData).GetGraphic(10);

        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
