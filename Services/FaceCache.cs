using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// 简单的用户头像缓存。B 站实时消息不直接带头像 URL，
/// 这里通过 uid 调用 B 站 API 获取并缓存到内存，避免每条弹幕都请求一次。
/// </summary>
public sealed class FaceCache : IDisposable
{
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly ConcurrentDictionary<long, Task<BitmapImage?>> _cache = new();
    private bool _disposed;

    public FaceCache()
    {
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
    }

    public async Task<BitmapImage?> GetFaceFromUrlAsync(string faceUrl)
    {
        if (string.IsNullOrWhiteSpace(faceUrl) || _disposed)
        {
            return null;
        }

        try
        {
            faceUrl = NormalizeAvatarUrl(faceUrl);
            using var response = await _http.GetAsync(faceUrl).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new System.IO.MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public async Task<BitmapImage?> GetFaceAsync(long userId)
    {
        if (userId <= 0)
        {
            return null;
        }

        if (_disposed)
        {
            return null;
        }

        return await _cache.GetOrAdd(userId, async uid =>
        {
            try
            {
                var url = $"https://api.bilibili.com/x/web-interface/card?mid={uid}&photo=false";
                using var response = await _http.GetAsync(url).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("card", out var card) ||
                    !card.TryGetProperty("face", out var face) ||
                    face.GetString() is not { Length: > 0 } faceUrl)
                {
                    return null;
                }

                // 替换成 56x56 的头像，节省流量。
                faceUrl = NormalizeAvatarUrl(faceUrl);
                using var imageResponse = await _http.GetAsync(faceUrl).ConfigureAwait(false);
                imageResponse.EnsureSuccessStatusCode();

                var bytes = await imageResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new System.IO.MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }).ConfigureAwait(false);
    }

    private static string NormalizeAvatarUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + url;
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + url[7..];
        }

        return url;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        _cache.Clear();
    }
}
