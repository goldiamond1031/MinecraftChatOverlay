using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MinecraftChatOverlay.Plugins.NetEaseLyric;

/// <summary>桥接送来的一帧状态。</summary>
/// <param name="Title">歌名。</param>
/// <param name="Artist">歌手。</param>
/// <param name="Album">专辑。</param>
/// <param name="SongId">网易云歌曲 id（有的话歌词可以直接按 id 取，比搜歌准）。</param>
/// <param name="PositionMs">播放位置（毫秒，InfLink 给的精确值）。</param>
/// <param name="DurationMs">总时长（毫秒，可能为 0）。</param>
/// <param name="Playing">是否在播放。</param>
/// <param name="ReceivedAt">本机收到的时刻（用来判断"数据是不是还新鲜"）。</param>
/// <param name="Raw">原始 JSON（诊断用，第一次收到时记下来）。</param>
public sealed record BridgePayload(
    string Title,
    string Artist,
    string Album,
    long SongId,
    long PositionMs,
    long DurationMs,
    bool Playing,
    DateTime ReceivedAt,
    string Raw);

/// <summary>
/// 本地桥接接收端：在 127.0.0.1:&lt;端口&gt; 上收 InfLink 桥接插件 POST 过来的状态。
///
/// 为什么需要它：Windows 的 SMTC 通道里网易云**不报播放进度**（实测位置/时长/上报时间全是空），
/// 而网易云客户端内部的 InfLink 插件知道精确进度；它靠一个 BetterNCM 桥接插件把数据
/// POST 到本机端口（默认 27431，和你之前那个 LyricShadow 参考程序用的是同一个端口协议）：
///
///     POST /state
///     {
///       "song": { "songName", "albumName", "authorName", "ncmId", "duration" },
///       "timeline": { "currentTime": 毫秒, ... },
///       "playback_status": "Playing"
///     }
///
/// 这里自己手写 HTTP 解析，不用 HttpListener 的原因：HttpListener 在非管理员下需要
/// URL ACL 授权（netsh http add urlacl），而 TcpListener 直接绑回环地址没这些麻烦。
/// 另外必须回 CORS 头 —— 请求是从网易云内部的页面发出来的，属于跨源。
/// </summary>
public sealed class BridgeReceiver : IDisposable
{
    private readonly int _port;
    private readonly Action<string> _log;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public BridgeReceiver(int port, Action<string> log)
    {
        _port = port;
        _log = log;
    }

    /// <summary>最近收到的一帧（没收到过就是 null）。</summary>
    public BridgePayload? Latest { get; private set; }

    /// <summary>监听的端口。</summary>
    public int Port => _port;

    /// <summary>监听是否起来了。</summary>
    public bool IsListening => _listener is not null;

    /// <summary>不能监听时的原因（端口被占之类）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>一共收到多少帧。</summary>
    public int ReceivedCount { get; private set; }

    /// <summary>最近一帧距今多少秒（没收到过返回 <see cref="double.MaxValue"/>）。</summary>
    public double SecondsSinceLast =>
        Latest is null ? double.MaxValue : (DateTime.UtcNow - Latest.ReceivedAt.ToUniversalTime()).TotalSeconds;

    public bool Start()
    {
        if (_listener is not null)
        {
            return true;
        }

        try
        {
            var listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            _log($"桥接接收端已监听 127.0.0.1:{_port}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log($"桥接接收端起不来（127.0.0.1:{_port}）：{ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // 忽略
        }
        finally
        {
            _listener = null;
            _cts = null;
        }
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 3000;
                var stream = client.GetStream();

                var (headers, body) = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (headers.Length == 0)
                {
                    return;
                }

                var firstLine = headers.Split("\r\n")[0];
                var isOptions = firstLine.StartsWith("OPTIONS", StringComparison.OrdinalIgnoreCase);
                var isPost = firstLine.StartsWith("POST", StringComparison.OrdinalIgnoreCase);

                if (isPost && body.Length > 0)
                {
                    Parse(body);
                }

                // OPTIONS 是浏览器的跨源预检，必须回 CORS 头，否则真正的 POST 根本不会发出来
                var response = isOptions
                    ? "HTTP/1.1 204 No Content\r\n" +
                      "Access-Control-Allow-Origin: *\r\n" +
                      "Access-Control-Allow-Methods: POST, OPTIONS\r\n" +
                      "Access-Control-Allow-Headers: content-type\r\n" +
                      "Access-Control-Max-Age: 600\r\n" +
                      "Content-Length: 0\r\n" +
                      "Connection: close\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\n" +
                      "Access-Control-Allow-Origin: *\r\n" +
                      "Content-Type: text/plain\r\n" +
                      "Content-Length: 2\r\n" +
                      "Connection: close\r\n\r\nOK";

                var bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(bytes.AsMemory()).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // 单个连接出错无所谓，下一帧还会来
        }
    }

    /// <summary>读一个 HTTP 请求：先读头（直到空行），再按 Content-Length 读 body。</summary>
    private static async Task<(string Headers, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var received = new MemoryStream();
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            received.Write(buffer, 0, read);
            var text = Encoding.UTF8.GetString(received.ToArray());
            headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (received.Length > 256 * 1024)
            {
                break; // 异常大的请求，丢掉
            }
        }

        var all = Encoding.UTF8.GetString(received.ToArray());
        if (headerEnd < 0)
        {
            return (all, "");
        }

        var headers = all[..headerEnd];
        var body = all[(headerEnd + 4)..];

        var contentLength = 0;
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[15..].Trim(), out contentLength);
            }
        }

        while (body.Length < contentLength)
        {
            var read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            received.Write(buffer, 0, read);
            body = Encoding.UTF8.GetString(received.ToArray())[(headerEnd + 4)..];
        }

        return (headers, body);
    }

    /// <summary>解析桥接的 JSON，宽容处理字段名的各种叫法。</summary>
    private void Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var song = root.TryGetProperty("song", out var songElement) ? songElement : default;
            var timeline = root.TryGetProperty("timeline", out var timelineElement) ? timelineElement : default;

            var title = GetString(song, "songName");
            var artist = GetString(song, "authorName");
            var album = GetString(song, "albumName");
            var songId = GetLong(song, "ncmId");
            var duration = GetLong(song, "duration");

            // 位置：桥接历史版本用过 currentTime，也见过 progress / position / positionMs，都认
            var position = FirstLong(timeline, "currentTime", "progress", "position", "positionMs", "current");

            // 时长有些版本放在 timeline 里
            if (duration <= 0)
            {
                duration = FirstLong(timeline, "duration", "durationMs", "totalTime", "total");
            }

            var status = GetString(root, "playback_status");
            var playing = status.Length == 0 || status.Contains("play", StringComparison.OrdinalIgnoreCase);

            // 位置数值可能是秒（有的版本给的是秒），太小就当秒处理：
            // 判断依据：时长合理（>1000ms）且位置 < 时长/100 说明单位不一致 → 按秒乘 1000
            if (duration > 1000 && position > 0 && position < duration / 100 && position < 1000)
            {
                position *= 1000;
            }

            Latest = new BridgePayload(
                title,
                artist,
                album,
                songId,
                position,
                duration,
                playing,
                DateTime.UtcNow,
                ReceivedCount == 0 ? json : "");

            ReceivedCount++;
        }
        catch (Exception ex)
        {
            _log("桥接数据解析失败：" + ex.Message + "｜原文：" + json);
        }
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.ToString(),
                _ => "",
            };
        }

        return "";
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var asDouble))
            {
                return (long)asDouble;
            }
        }

        return 0;
    }

    private static long FirstLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetLong(element, name);
            if (value != 0)
            {
                return value;
            }
        }

        return 0;
    }
}
