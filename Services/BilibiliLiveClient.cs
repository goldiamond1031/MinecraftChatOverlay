using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinecraftChatOverlay.Models;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// Bilibili 直播弹幕 WebSocket 客户端。
/// 只做最核心的弹幕/入场/礼物/人气连接，界面不依赖具体协议实现。
/// </summary>
public sealed class BilibiliLiveClient : ILiveClient
{
    private const int HeaderLength = 16;
    private const int OperationHeartbeatReply = 3;
    private const int OperationNotification = 5;
    private const int OperationAuth = 7;
    private const int OperationAuthReply = 8;
    private const int OperationHeartbeat = 2;

    // INTERACT_WORD_V2 的 protobuf 字段号（B站未公开，实测 + 社区文档确认）
    private const int InteractFieldUserId = 1;
    private const int InteractFieldUserName = 2;
    private const int InteractFieldMsgType = 5;

    // SEND_GIFT_V2 的 protobuf 字段号（实测得出）：外层放用户信息，礼物详情在内层消息里
    private const int GiftFieldPayload = 10;    // 外层：礼物详情（嵌套消息）
    private const int GiftFieldUserName = 2;    // 外层：送礼人昵称
    private const int GiftInnerName = 2;        // 内层：礼物名称
    private const int GiftInnerNum = 3;         // 内层：数量
    private const int GiftInnerPrice = 5;       // 内层：单价（金瓜子）
    private const int GiftInnerCoinType = 8;    // 内层：货币类型（gold / silver）

    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
        61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
        36, 20, 34, 44, 52
    };

    private readonly CookieContainer _cookieContainer;
    private readonly HttpClient _http;
    private readonly string? _loginCookie;
    private readonly long _loginUid;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _disposed;
    private bool _cookiePrepared;
    private bool _wbiPrepared;
    private string _mixinKey = "";
    private string _roomIdInput = "";
    private int _danmakuTotal;
    private int _danmakuParsed;
    private int _receiveCount;
    private int _commandCount;
    private int _packetParsed;
    private int _interactSamples;
    private int _onlineSamples;
    private int _giftSamples;
    private readonly HashSet<string> _unknownCommands = new(StringComparer.Ordinal);

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>直播间基础信息（房间号/标题/在线/主播名）。</summary>
    public RoomInfo? CurrentRoom { get; private set; }

    public event Action<RoomInfo>? RoomInfoUpdated;
    public event Action<DanmakuItem>? DanmakuReceived;
    public event Action<NoticeItem>? NoticeReceived;

    /// <inheritdoc />
    public event Action<SuperChatItem>? SuperChatReceived;
    public event Action<int>? PopularityUpdated;

    /// <summary>有人进入直播间（uid, 昵称）。用于估算「近期在场人数」。</summary>
    public event Action<long, string>? EntryObserved;

    /// <summary>真实在线人数（来自弹幕流的 ONLINE_RANK_COUNT 命令）。</summary>
    public event Action<int>? OnlineCountUpdated;
    public event Action<string>? StatusChanged;
    public event Action<Exception>? ErrorOccurred;

    public BilibiliLiveClient(string? loginCookie = null)
    {
        _loginCookie = loginCookie;
        // 注意：CookieContainer 默认每个域最多只存 20 个 Cookie，
        // 浏览器导出的完整 Cookie 常有 40 个以上，超出的会被静默丢弃，
        // 导致 SESSDATA / DedeUserID 这类关键 Cookie 缺失（用户名被风控打码、拿不到真实 UID/头像）。
        _cookieContainer = new CookieContainer
        {
            Capacity = 500,
            PerDomainCapacity = 300,
            MaxCookieSize = 8192
        };
        _http = new HttpClient(new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://live.bilibili.com/");

        if (!string.IsNullOrWhiteSpace(loginCookie))
        {
            var cookieUri = new Uri("https://www.bilibili.com/");
            foreach (var part in loginCookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                var name = part[..idx].Trim();
                var value = part[(idx + 1)..].Trim();
                try
                {
                    _cookieContainer.Add(cookieUri, new Cookie(name, value) { Domain = "bilibili.com", Path = "/" });
                }
                catch
                {
                }

                if (name == "DedeUserID" && long.TryParse(value, out var uid))
                {
                    _loginUid = uid;
                }
            }
        }
    }

    /// <summary>
    /// B 站部分接口需要 buvid3/buvid4 Cookie，否则返回 code=-352 风控。
    /// 先访问主站让 HttpClient 的 CookieContainer 拿到 .bilibili.com 的 Cookie，
    /// 再调用官方指纹接口兜底写入 buvid3/buvid4。
    /// </summary>
    private async Task EnsureBuvidCookieAsync(CancellationToken token)
    {
        if (_cookiePrepared)
        {
            return;
        }

        _cookiePrepared = true;

        try
        {
            // 访问主站通常会 Set-Cookie: buvid3 / buvid4 / b_nut。
            using (var response = await _http.GetAsync("https://www.bilibili.com/", token).ConfigureAwait(false))
            {
                // 只需要把 Set-Cookie 收进 CookieContainer。
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        try
        {
            // 兜底：从官方指纹接口手动写入 Cookie。
            var url = "https://api.bilibili.com/x/frontend/finger/spi";
            using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                return;
            }

            var cookieUri = new Uri("https://www.bilibili.com/");
            if (data.TryGetProperty("b_3", out var b3) && b3.GetString() is { Length: > 0 } b3Value)
            {
                _cookieContainer.Add(cookieUri, new Cookie("buvid3", b3Value) { Domain = "bilibili.com", Path = "/" });
            }

            if (data.TryGetProperty("b_4", out var b4) && b4.GetString() is { Length: > 0 } b4Value)
            {
                _cookieContainer.Add(cookieUri, new Cookie("buvid4", b4Value) { Domain = "bilibili.com", Path = "/" });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 获取不到 Cookie 时先继续尝试；仍失败时错误信息会展示接口返回内容。
        }

        try
        {
            var cookieCount = _cookieContainer.GetCookies(new Uri("https://api.live.bilibili.com/")).Count;
            StatusChanged?.Invoke($"B站 Cookie 已准备: {cookieCount} 个");
        }
        catch
        {
        }
    }

    /// <summary>
    /// 获取 B 站 WBI 签名需要的 img_key / sub_key，并生成 mixin_key。
    /// getDanmuInfo 等接口近期要求 w_rid 签名，否则返回 -352。
    /// </summary>
    private async Task EnsureWbiKeyAsync(CancellationToken token)
    {
        if (_wbiPrepared)
        {
            return;
        }

        _wbiPrepared = true;

        try
        {
            var url = "https://api.bilibili.com/x/web-interface/nav";
            using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("wbi_img", out var wbiImg))
            {
                return;
            }

            var imgKey = ExtractWbiKey(wbiImg, "img_url");
            var subKey = ExtractWbiKey(wbiImg, "sub_url");
            if (string.IsNullOrEmpty(imgKey) || string.IsNullOrEmpty(subKey))
            {
                return;
            }

            var origin = imgKey + subKey;
            if (origin.Length < 64)
            {
                return;
            }

            var sb = new StringBuilder(32);
            foreach (var index in MixinKeyEncTab)
            {
                if (index < origin.Length)
                {
                    sb.Append(origin[index]);
                }
            }

            _mixinKey = sb.ToString(0, Math.Min(32, sb.Length));
            StatusChanged?.Invoke($"WBI 签名密钥已获取: {_mixinKey}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 获取不到 WBI key 时不阻断主流程；如果接口确实需要签名，会继续在日志中暴露 -352。
            StatusChanged?.Invoke("WBI 签名密钥获取失败");
        }
    }

    private static string? ExtractWbiKey(JsonElement wbiImg, string propertyName)
    {
        if (!wbiImg.TryGetProperty(propertyName, out var urlElement) ||
            urlElement.GetString() is not { Length: > 0 } url)
        {
            return null;
        }

        try
        {
            var path = new Uri(url).AbsolutePath;
            return Path.GetFileNameWithoutExtension(path);
        }
        catch
        {
            return null;
        }
    }

    private string BuildWbiSignedQuery(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            dict[pair.Key] = pair.Value;
        }

        dict["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sorted = dict.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

        var sb = new StringBuilder();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('&');
            }

            sb.Append(sorted[i].Key).Append('=').Append(Uri.EscapeDataString(sorted[i].Value));
        }

        var query = sb.ToString();
        var wRid = Md5Hex(query + _mixinKey);
        return query + "&w_rid=" + wRid;
    }

    private static string Md5Hex(string input)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task ConnectAsync(string roomIdInput, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        try
        {
            StatusChanged?.Invoke("正在获取直播间信息...");
            await EnsureBuvidCookieAsync(token).ConfigureAwait(false);
            await EnsureWbiKeyAsync(token).ConfigureAwait(false);

            var roomId = roomIdInput.Trim();
            _roomIdInput = roomId;
            var roomInfo = await GetRoomInfoAsync(roomId, token).ConfigureAwait(false);
            await FillInteractionAsync(roomInfo, token).ConfigureAwait(false);
            CurrentRoom = roomInfo;
            RoomInfoUpdated?.Invoke(roomInfo);

            // 人数直接用 getInfoByRoom 的 room_info.online（B站现在叫「人气」）。
            // 旧的 getOnlineRank 接口已废弃，实测恒返回 onlineNum=0，故不再调用。
            StatusChanged?.Invoke($"房间状态：{(roomInfo.LiveStatus ? "直播中" : "未开播")}，人气 {roomInfo.Online}");

            StatusChanged?.Invoke("正在获取弹幕服务器...");
            await EnsureBuvidCookieAsync(token).ConfigureAwait(false);
            await EnsureWbiKeyAsync(token).ConfigureAwait(false);
            var (tokenKey, host, port) = await GetDanmuServerAsync(roomInfo.RoomId, token).ConfigureAwait(false);

            StatusChanged?.Invoke($"正在连接弹幕服务器 {host}:{port} ...");
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
            socket.Options.SetRequestHeader("Origin", "https://live.bilibili.com");

            // 关键：B站现在可能要求 WebSocket 握手也带 Cookie（buvid3/buvid4），否则认证成功但不推消息。
            var cookieHeader = _cookieContainer.GetCookieHeader(new Uri($"https://{host}/"));
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                socket.Options.SetRequestHeader("Cookie", cookieHeader);
                StatusChanged?.Invoke($"WebSocket 携带 Cookie: {cookieHeader.Length} 字符");
            }

            socket.Options.KeepAliveInterval = TimeSpan.Zero;

            await socket.ConnectAsync(new Uri($"wss://{host}:{port}/sub"), token).ConfigureAwait(false);
            _socket = socket;

            // 发送认证包；protover=3 让服务器返回 Brotli 压缩包。
            // uid 填登录用户的真实 UID（Cookie 里的 DedeUserID）：匿名 uid=0 时服务器
            // 容易直接断连，带上真实 UID 也才能拿到不打码的用户名。
            var buvid3 = _cookieContainer.GetCookies(new Uri("https://www.bilibili.com/"))["buvid3"]?.Value ?? "";
            var authBody = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["uid"] = _loginUid,
                ["roomid"] = roomInfo.RoomId,
                ["protover"] = 3,
                ["platform"] = "web",
                ["type"] = 2,
                ["key"] = tokenKey,
                ["buvid"] = buvid3
            });
            StatusChanged?.Invoke($"认证包(uid={_loginUid}): {Truncate(authBody, 160)}");
            var authPacket = BuildPacket(Encoding.UTF8.GetBytes(authBody), OperationAuth);
            await socket.SendAsync(authPacket, WebSocketMessageType.Binary, true, token).ConfigureAwait(false);

            // 心跳线程。
            _ = Task.Run(async () =>
            {
                try
                {
                    var heartbeatPacket = BuildPacket(Array.Empty<byte>(), OperationHeartbeat);
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                        if (_socket?.State == WebSocketState.Open)
                        {
                            await _socket.SendAsync(heartbeatPacket, WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(ex);
                }
            }, token);

            // 定时刷新房间在线人数/标题。B 站不会在弹幕流里推真实在线，只推人气值。
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(_roomIdInput))
                        {
                            continue;
                        }

                        var latestRoom = await GetRoomInfoAsync(_roomIdInput, token).ConfigureAwait(false);
                        await FillInteractionAsync(latestRoom, token).ConfigureAwait(false);
                        CurrentRoom = latestRoom;
                        RoomInfoUpdated?.Invoke(latestRoom);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    // 刷新失败忽略，下一轮再试。
                }
            }, token);

            StatusChanged?.Invoke("已连接，等待弹幕...");
            _receiveTask = ReceiveLoopAsync(socket, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var hadConnection = _socket != null || _cts != null || _receiveTask != null;

        if (_cts != null)
        {
            _cts.Cancel();
        }

        if (_socket != null)
        {
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            _socket.Dispose();
            _socket = null;
        }

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch
            {
            }

            _receiveTask = null;
        }

        _cts?.Dispose();
        _cts = null;

        if (hadConnection)
        {
            StatusChanged?.Invoke("已断开");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisconnectAsync().GetAwaiter().GetResult();
        _http.Dispose();
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var receiveBuffer = new byte[64 * 1024];

        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server close", CancellationToken.None).ConfigureAwait(false);
                        StatusChanged?.Invoke("服务器关闭了连接");
                        return;
                    }

                    stream.Write(receiveBuffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var packet = stream.ToArray();
                _receiveCount++;
                if (_receiveCount <= 3)
                {
                    StatusChanged?.Invoke($"收到 WebSocket 数据块 {packet.Length} 字节");
                }

                ProcessPackets(packet);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex);
            StatusChanged?.Invoke("连接异常断开：" + ex.Message);
        }
        finally
        {
            if (_socket == socket)
            {
                StatusChanged?.Invoke("连接已结束");
            }
        }
    }

    private void ProcessPackets(byte[] data)
    {
        var offset = 0;
        while (offset + HeaderLength <= data.Length)
        {
            var totalLength = ReadInt32(data, offset);
            var headerLength = ReadUInt16(data, offset + 4);
            var protocolVersion = ReadUInt16(data, offset + 6);
            var operation = ReadInt32(data, offset + 8);

            if (totalLength < HeaderLength || headerLength < HeaderLength || offset + totalLength > data.Length)
            {
                break;
            }

            var body = new byte[totalLength - headerLength];
            if (body.Length > 0)
            {
                Buffer.BlockCopy(data, offset + headerLength, body, 0, body.Length);
            }

            ProcessBody(protocolVersion, operation, body);
            offset += totalLength;
        }
    }

    private void ProcessBody(int protocolVersion, int operation, byte[] body)
    {
        _packetParsed++;
        if (_packetParsed <= 5)
        {
            StatusChanged?.Invoke($"解析数据包[{_packetParsed}]: proto={protocolVersion}, op={operation}, bodyLen={body.Length}");
        }

        switch (protocolVersion)
        {
            case 2: // zlib
                using (var input = new MemoryStream(body))
                using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    zlib.CopyTo(output);
                    ProcessPackets(output.ToArray());
                }
                break;

            case 3: // brotli
                using (var input = new MemoryStream(body))
                using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    brotli.CopyTo(output);
                    ProcessPackets(output.ToArray());
                }
                break;

            default:
                if (operation == OperationAuthReply)
                {
                    var authReply = Encoding.UTF8.GetString(body);
                    StatusChanged?.Invoke($"认证回复: {Truncate(authReply, 200)}");
                }
                else if (operation == OperationHeartbeatReply && body.Length >= 4)
                {
                    var online = ReadInt32(body, 0);
                    PopularityUpdated?.Invoke(online);
                }
                else if (operation == OperationNotification)
                {
                    var json = Encoding.UTF8.GetString(body);
                    try
                    {
                        HandleCommandJson(json);
                    }
                    catch (Exception ex)
                    {
                        // 单个未知/损坏命令不应影响整条连接。
                        System.Diagnostics.Debug.WriteLine(ex);
                    }
                }
                break;
        }
    }

    private void HandleCommandJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("cmd", out var cmdElement))
        {
            return;
        }

        var cmd = cmdElement.GetString();
        _commandCount++;
        if (_commandCount <= 10)
        {
            StatusChanged?.Invoke($"收到弹幕流命令[{_commandCount}]: {cmd}");
        }

        if (_commandCount % 100 == 0)
        {
            StatusChanged?.Invoke($"命令统计: 收到 {_commandCount} 个命令，其中弹幕 {_danmakuTotal} 条，成功解析 {_danmakuParsed} 条");
        }

        switch (cmd)
        {
            case "DANMU_MSG":
            case "DANMU_MSG_MIRROR":
                _danmakuTotal++;
                if (_danmakuTotal <= 5)
                {
                    WriteRawDanmakuLog(json);
                }

                if (_danmakuTotal <= 3)
                {
                    StatusChanged?.Invoke($"DANMU_MSG 样例[{_danmakuTotal}]: {Truncate(json, 1200)}");
                }

                if (TryParseDanmaku(root, out var danmaku))
                {
                    _danmakuParsed++;
                    DanmakuReceived?.Invoke(danmaku);
                }
                else if (_danmakuParsed == 0 && _danmakuTotal == 1)
                {
                    StatusChanged?.Invoke($"首条弹幕解析失败，原始JSON: {Truncate(json, 240)}");
                }

                if (_danmakuTotal % 100 == 0)
                {
                    StatusChanged?.Invoke($"弹幕统计: 收到 {_danmakuTotal} 条，成功解析 {_danmakuParsed} 条");
                }
                break;

            case "INTERACT_WORD":
                if (TryParseInteract(root, out var interactText))
                {
                    NoticeReceived?.Invoke(new NoticeItem { Text = interactText, Kind = NoticeKind.Entry });
                }
                break;

            case "INTERACT_WORD_V2": // B站现在推的是 V2，数据在 base64 的 protobuf（pb 字段）里
                HandleInteractV2(root);
                break;

            case "SEND_GIFT":
                if (TryParseGift(root, out var giftUserName, out var giftName, out var giftCount, out var giftAmount))
                {
                    // 不在这里做时间窗合并：合并规则是「与上一条通知相邻且同为该礼物」，
                    // 由通知区（OverlayWindow.AddNotice）判断，中间夹了别的通知就不合并。
                    NoticeReceived?.Invoke(NoticeItem.CreateGift(
                        giftUserName, giftName, giftCount, giftAmount, mergeKey: $"{giftUserName}|{giftName}"));
                }
                break;

            case "SEND_GIFT_V2": // B站现在推的是 V2：数据全在 protobuf（data.pb）里
                if (TryParseGiftV2(root, out var v2UserName, out var v2GiftName, out var v2Count, out var v2Amount))
                {
                    NoticeReceived?.Invoke(NoticeItem.CreateGift(
                        v2UserName, v2GiftName, v2Count, v2Amount, mergeKey: $"{v2UserName}|{v2GiftName}"));
                }
                else if (_giftSamples++ < 2)
                {
                    StatusChanged?.Invoke($"SEND_GIFT_V2 解析失败，结构为: {DumpGiftV2ForDebug(root)}");
                }
                break;

            case "WELCOME_GUARD":
                if (TryParseWelcomeGuard(root, out var guardName, out var guardLevel))
                {
                    NoticeReceived?.Invoke(NoticeItem.CreateGuard(guardName, guardLevel));
                }
                break;

            case "GUARD_BUY": // 开通大航海（B站现在主要推这个）
                if (TryParseGuardBuy(root, out var buyName, out var buyLevel, out var buyCount))
                {
                    NoticeReceived?.Invoke(NoticeItem.CreateGuard(buyName, buyLevel, buyCount));
                }
                break;

            case "USER_TOAST_MSG":
                if (TryParseUserToast(root, out var toastName, out var toastLevel, out var toastCount))
                {
                    NoticeReceived?.Invoke(NoticeItem.CreateGuard(toastName, toastLevel, toastCount));
                }
                break;

            case "SUPER_CHAT_MESSAGE":
                // SC 不再拼成一行纯文本通知，改成发富数据，由弹幕窗的 SC 区单独渲染
                if (TryParseSuperChat(root, out var superChat) && superChat != null)
                {
                    SuperChatReceived?.Invoke(superChat);
                }
                break;

            case "LIVE":
            case "PREPARING":
            case "ROOM_CHANGE":
            case "WATCHED_CHANGE":
            case "ONLINE_RANK_V2":
            case "ONLINE_RANK_V3":
            case "LIKE_INFO_V3_CLICK":
            case "LIKE_INFO_V3_UPDATE":
            case "POPULARITY_CHANGE":
            case "ROOM_REAL_TIME_MESSAGE_UPDATE":
            case "STOP_LIVE_ROOM_LIST":
            case "HOT_ROOM_NOTIFY":
            case "ENTRY_EFFECT":
            case "NOTICE_MSG":
                // 已知但当前不展示的命令，静默忽略。
                break;

            default:
                // B站经常把命令改名（V2 等），遇到没见过的命令打一条日志，方便发现协议变更。
                if (_unknownCommands.Add(cmd ?? ""))
                {
                    StatusChanged?.Invoke($"⚠ 未处理的命令: {cmd}（如与本项目相关可反馈）");
                }
                break;

            case "ONLINE_RANK_COUNT":
                // 这个命令里带「真实在线人数」：
                //   count        = 高能用户数（互动过的人）
                //   online_count = 当前在线人数
                // （依据：Rust 库 blivedm 的 BiliMessage::OnlineRankCount 文档，
                //   B站官方 H5 播放器的 onlineCount 事件也对应这里）
                if (root.TryGetProperty("data", out var onlineData) && onlineData.ValueKind == JsonValueKind.Object)
                {
                    if (_onlineSamples < 3)
                    {
                        _onlineSamples++;
                        StatusChanged?.Invoke($"ONLINE_RANK_COUNT 样例: {Truncate(onlineData.GetRawText(), 300)}");
                    }

                    if (onlineData.TryGetProperty("online_count", out var onlineCountElement) &&
                        onlineCountElement.TryGetInt32(out var onlineCount) && onlineCount >= 0)
                    {
                        OnlineCountUpdated?.Invoke(onlineCount);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// 兼容解析弹幕。B 站不同时期 DANMU_MSG 的 info 字段略有差异，
    /// 这里采用“按数组/字符串类型自适应”的保守解析。
    /// </summary>
    private static bool TryParseDanmaku(JsonElement root, out DanmakuItem item)
    {
        item = null!;
        try
        {
            if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array || info.GetArrayLength() < 2)
            {
                return false;
            }

            var userPart = info[1];
            var medalPart = info.GetArrayLength() > 2 ? info[2] : default;

            string? text = null;
            string? userName = null;
            long userId = 0;
            string? medalName = null;
            int medalLevel = 0;
            string? colorText = null;

            if (info.GetArrayLength() > 0 && info[0].ValueKind == JsonValueKind.Array)
            {
                var basic = info[0];
                // 通常 basic[3] 是 10 进制颜色值，例如 16777215 = 白色。
                if (basic.GetArrayLength() > 3 && basic[3].TryGetInt32(out var c))
                {
                    colorText = $"#{c:X6}";
                }
            }

            // 新版弹幕会把正文塞在 info[0][15].extra.content 里，优先取这个字段。
            var extraContent = TryGetDanmakuContentFromInfo(info);
            if (!string.IsNullOrEmpty(extraContent))
            {
                text = extraContent;
            }

            if (userPart.ValueKind == JsonValueKind.String)
            {
                // 旧版本：info[1] 直接是弹幕文字，info[2] 可能是用户信息数组。
                if (!string.IsNullOrEmpty(userPart.GetString()))
                {
                    text = userPart.GetString();
                }

                if (info.GetArrayLength() > 2 && info[2].ValueKind == JsonValueKind.Array)
                {
                    ParseUserArray(info[2], ref userName, ref userId);
                }

                if (info.GetArrayLength() > 3 && info[3].ValueKind == JsonValueKind.Array)
                {
                    ParseMedalArray(info[3], ref medalName, ref medalLevel);
                }
            }
            else if (userPart.ValueKind == JsonValueKind.Array)
            {
                // 当前主流：info[1] 是 [文本/uid/用户名/...] 这种数组。
                // 部分版本把 uid 放数组第 0 项，文本在另一个位置；这里尽力兼容。
                var arr = userPart.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToArray();
                var numberArr = userPart.EnumerateArray().ToArray();
                var firstIsNumber = numberArr.Length > 0 &&
                    (numberArr[0].ValueKind == JsonValueKind.Number ||
                     (numberArr[0].ValueKind == JsonValueKind.String && long.TryParse(numberArr[0].GetString(), out _)));

                // 常见布局1：[文本, 用户名, ...]
                // 常见布局2：[0, 用户名, ...]，文本在 info[1][?] 不存在时会到 info[?]
                if (numberArr.Length > 1 && firstIsNumber)
                {
                    // 若第 0 项是数字，说明这一项不是文本，更像用户信息。
                    ParseUserArray(userPart, ref userName, ref userId);
                    text = FindBestText(info, userName, medalName);
                }
                else
                {
                    // 第 0 项非数字，按 [文本, 用户名, ...] 解析。
                    if (arr.Length > 0 && !string.IsNullOrEmpty(arr[0]))
                    {
                        text = arr[0];
                    }

                    if (arr.Length > 1 && !string.IsNullOrEmpty(arr[1]))
                    {
                        userName = arr[1];
                    }

                    if (numberArr.Length > 0 && numberArr[0].TryGetInt64(out var uid))
                    {
                        userId = uid;
                    }
                }

                ParseMedalArray(medalPart, ref medalName, ref medalLevel);
            }

            if (string.IsNullOrEmpty(text))
            {
                text = extraContent;
            }

            var extraUser = TryGetDanmakuUserFromInfo(info);
            if (extraUser.Uid > 0)
            {
                userId = extraUser.Uid;
            }

            if (!string.IsNullOrEmpty(extraUser.Name) && !extraUser.Name.Contains("***"))
            {
                userName = extraUser.Name;
            }

            var faceUrl = extraUser.Face;
            var guardLevel = extraUser.GuardLevel;

            if (string.IsNullOrEmpty(userName))
            {
                userName = FindUserName(info) ?? "未知用户";
            }

            if (string.IsNullOrEmpty(text))
            {
                // 最后兜底：递归查找看起来像弹幕的字符串字段。
                text = FindBestText(info, userName, medalName);
            }

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            // 表情包弹幕：B站两种表情的原文案格式不统一
            // （房间表情是「超级爱你」，大表情是「[嘉然2.0_可是蒂娜我]」），
            // 这里统一成「[表情]xxx」，方便和普通文字弹幕区分。
            string emoteUrl = "";
            if (TryGetEmoticon(info, out var emoteImageUrl))
            {
                text = NormalizeEmoticonText(text);
                emoteUrl = emoteImageUrl;
            }

            item = new DanmakuItem
            {
                UserId = userId,
                UserName = userName,
                Text = text,
                Face = faceUrl ?? "",
                MedalName = medalName,
                MedalLevel = medalLevel,
                GuardLevel = guardLevel,
                DanmakuColor = colorText,
                EmoteUrl = emoteUrl
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析 SEND_GIFT_V2。数据全在 data.pb（protobuf）里：
    /// 外层 2=送礼人昵称；内层（字段 10）2=礼物名、3=数量、5=单价、8=货币类型。
    /// </summary>
    private static bool TryParseGiftV2(
        JsonElement root, out string userName, out string giftName, out int count, out double amount)
    {
        userName = "某人";
        giftName = "礼物";
        count = 1;
        amount = 0;
        try
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("pb", out var pbElement) ||
                pbElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var bytes = Convert.FromBase64String(pbElement.GetString()!);
            var payload = ProtoReader.GetFieldBytes(bytes, GiftFieldPayload);
            if (payload == null)
            {
                return false;
            }

            var outer = ProtoReader.ReadFields(bytes);
            var gift = ProtoReader.ReadFields(payload);

            var outerName = GetProtoString(outer, GiftFieldUserName);
            if (!string.IsNullOrEmpty(outerName))
            {
                userName = outerName;
            }

            var innerName = GetProtoString(gift, GiftInnerName);
            if (!string.IsNullOrEmpty(innerName))
            {
                giftName = innerName;
            }

            count = (int)Math.Max(1, GetProtoInt(gift, GiftInnerNum));
            var price = GetProtoInt(gift, GiftInnerPrice);
            var coinType = GetProtoString(gift, GiftInnerCoinType);

            // 金瓜子：1000 金瓜子 = 1 元
            if (string.Equals(coinType, "gold", StringComparison.OrdinalIgnoreCase))
            {
                amount = price * count / 1000.0;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>调试用：把 SEND_GIFT_V2 的 protobuf 结构展开成可读文本。</summary>
    private static string DumpGiftV2ForDebug(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("pb", out var pb) ||
                pb.ValueKind != JsonValueKind.String)
            {
                return "(没有 data.pb)";
            }

            var bytes = Convert.FromBase64String(pb.GetString()!);
            var outer = ProtoReader.ReadFields(bytes);
            var parts = new List<string>();
            foreach (var pair in outer.OrderBy(p => p.Key))
            {
                parts.Add(pair.Value is string s && s.Length > 0 ? $"{pair.Key}=\"{Truncate(s, 60)}\"" : $"{pair.Key}=<{(pair.Value is string ? "二进制" : pair.Value)}>");
            }

            var payload = ProtoReader.GetFieldBytes(bytes, GiftFieldPayload);
            if (payload != null)
            {
                var inner = ProtoReader.ReadFields(payload);
                parts.Add("__内层__");
                foreach (var pair in inner.OrderBy(p => p.Key))
                {
                    parts.Add(pair.Value is string s && s.Length > 0 ? $"{pair.Key}=\"{Truncate(s, 60)}\"" : $"{pair.Key}=<{(pair.Value is string ? "二进制" : pair.Value)}>");
                }

                // 内层里可能还藏着一层
                foreach (var key in inner.Keys.ToList())
                {
                    var nested = ProtoReader.GetFieldBytes(payload, key);
                    if (nested == null)
                    {
                        continue;
                    }

                    var nestedFields = ProtoReader.ReadFields(nested);
                    if (nestedFields.Count == 0)
                    {
                        continue;
                    }

                    parts.Add($"__内层[{key}]__");
                    foreach (var pair in nestedFields.OrderBy(p => p.Key))
                    {
                        parts.Add(pair.Value is string s && s.Length > 0 ? $"{pair.Key}=\"{Truncate(s, 60)}\"" : $"{pair.Key}=<{(pair.Value is string ? "二进制" : pair.Value)}>");
                    }
                }
            }

            return string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            return "(结构解析失败: " + ex.Message + ")";
        }
    }

    /// <summary>判断是不是表情包弹幕，是则取出表情图片地址。</summary>
    private static bool TryGetEmoticon(JsonElement info, out string imageUrl)
    {
        imageUrl = "";
        try
        {
            if (info.ValueKind != JsonValueKind.Array ||
                info.GetArrayLength() == 0 ||
                info[0].ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var element in info[0].EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty("emoticon_unique", out var uniqueElement) ||
                    uniqueElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(uniqueElement.GetString()))
                {
                    continue;
                }

                if (element.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String)
                {
                    imageUrl = urlElement.GetString() ?? "";
                }

                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>把表情文案统一成「[表情]名字」。</summary>
    private static string NormalizeEmoticonText(string text)
    {
        var name = text.Trim();
        if (name.Length >= 2 && name[0] == '[' && name[^1] == ']')
        {
            name = name[1..^1];
        }

        return string.IsNullOrEmpty(name) ? "[表情]" : "[表情]" + name;
    }

    private static string? TryGetDanmakuContentFromInfo(JsonElement info)
    {
        try
        {
            if (info.ValueKind != JsonValueKind.Array || info.GetArrayLength() == 0 || info[0].ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var basic = info[0];
            if (basic.GetArrayLength() <= 15)
            {
                return null;
            }

            var extraElement = basic[15];
            if (extraElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (extraElement.TryGetProperty("extra", out var extraJsonElement) &&
                extraJsonElement.GetString() is { Length: > 0 } extraJson)
            {
                using var extraDoc = JsonDocument.Parse(extraJson);
                if (extraDoc.RootElement.TryGetProperty("content", out var contentElement) &&
                    contentElement.GetString() is { Length: > 0 } content)
                {
                    return content;
                }
            }

            if (extraElement.TryGetProperty("content", out var directContentElement) &&
                directContentElement.GetString() is { Length: > 0 } directContent)
            {
                return directContent;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static (long Uid, string? Name, string? Face, int GuardLevel) TryGetDanmakuUserFromInfo(JsonElement info)
    {
        try
        {
            if (info.ValueKind != JsonValueKind.Array || info.GetArrayLength() == 0 || info[0].ValueKind != JsonValueKind.Array)
            {
                return (0, null, null, 0);
            }

            var basic = info[0];
            if (basic.GetArrayLength() <= 15 || basic[15].ValueKind != JsonValueKind.Object)
            {
                return (0, null, null, 0);
            }

            var userElement = default(JsonElement);
            var found = false;

            // 新版结构：user 直接放在 info[0][15].user
            if (basic[15].TryGetProperty("user", out var directUser) && directUser.ValueKind == JsonValueKind.Object)
            {
                userElement = directUser;
                found = true;
            }
            else if (basic[15].TryGetProperty("extra", out var extraJsonElement) &&
                     extraJsonElement.GetString() is { Length: > 0 } extraJson)
            {
                // 某些版本：user 藏在 extra 字符串里
                using var extraDoc = JsonDocument.Parse(extraJson);
                if (extraDoc.RootElement.TryGetProperty("user", out var extraUserElement) &&
                    extraUserElement.ValueKind == JsonValueKind.Object)
                {
                    userElement = extraUserElement;
                    found = true;
                }
            }

            if (!found)
            {
                return (0, null, null, 0);
            }

            var baseElement = userElement.TryGetProperty("base", out var baseInfo) ? baseInfo : userElement;
            var uid = baseElement.TryGetProperty("uid", out var uidElement) ? uidElement.GetInt64() : 0;
            var name = baseElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var face = baseElement.TryGetProperty("face", out var faceElement) ? faceElement.GetString() : null;
            var guardLevel = 0;
            if (userElement.TryGetProperty("guard", out var guardElement) &&
                guardElement.ValueKind == JsonValueKind.Object &&
                guardElement.TryGetProperty("guard_level", out var guardLevelElement))
            {
                guardLevel = guardLevelElement.GetInt32();
            }

            return (uid, name, face, guardLevel);
        }
        catch
        {
            return (0, null, null, 0);
        }
    }

    private static void ParseUserArray(JsonElement userElement, ref string? userName, ref long userId)
    {
        if (userElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var arr = userElement.EnumerateArray().ToArray();
        if (arr.Length > 0)
        {
            if (arr[0].TryGetInt64(out var uid))
            {
                userId = uid;
            }
            else if (arr[0].ValueKind == JsonValueKind.String && long.TryParse(arr[0].GetString(), out var uidFromString))
            {
                userId = uidFromString;
            }
        }

        // 用户名字符串通常出现在数组前 3 个元素中的某一个。
        for (var i = 0; i < Math.Min(arr.Length, 4); i++)
        {
            if (arr[i].ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(arr[i].GetString()))
            {
                var value = arr[i].GetString()!;
                // 跳过常见的空字段或明显不是人名的文本。
                if (long.TryParse(value, out _) || value == "0" || value.Length > 32)
                {
                    continue;
                }

                userName = value;
                break;
            }
        }
    }

    private static void ParseMedalArray(JsonElement medalElement, ref string? medalName, ref int medalLevel)
    {
        if (medalElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var arr = medalElement.EnumerateArray().ToArray();
        if (arr.Length > 0 && arr[0].TryGetInt32(out var level))
        {
            medalLevel = level;
        }

        for (var i = 1; i < Math.Min(arr.Length, 4); i++)
        {
            if (arr[i].ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(arr[i].GetString()))
            {
                medalName = arr[i].GetString();
                break;
            }
        }
    }

    private static string? FindBestText(JsonElement info, string? excludeName = null, string? excludeMedal = null)
    {
        var candidates = new List<string>();
        CollectStrings(info, candidates);

        string? best = null;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 500)
            {
                continue;
            }

            if (long.TryParse(candidate, out _))
            {
                continue;
            }

            if (excludeName is { Length: > 0 } && candidate == excludeName)
            {
                continue;
            }

            if (excludeMedal is { Length: > 0 } && candidate == excludeMedal)
            {
                continue;
            }

            if (best == null || candidate.Length > best.Length)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static void CollectStrings(JsonElement element, List<string> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                output.Add(element.GetString() ?? "");
                break;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectStrings(child, output);
                }
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectStrings(property.Value, output);
                }
                break;
        }
    }

    private static string? FindUserName(JsonElement info)
    {
        for (var i = 1; i < info.GetArrayLength(); i++)
        {
            var element = info[i];
            if (element.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var child in element.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.String)
                {
                    var s = child.GetString();
                    if (!string.IsNullOrEmpty(s) && s.Length <= 32 && !long.TryParse(s, out _))
                    {
                        return s;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 处理 INTERACT_WORD_V2（进入直播间/关注/分享）。
    /// 该命令把字段编码进 protobuf（data.pb 是 base64），所以必须解码才能拿到用户和类型。
    /// 这也是目前唯一能实时看到「有人进入直播间」的来源。
    /// </summary>
    private void HandleInteractV2(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("pb", out var pbElement) ||
            pbElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        Dictionary<int, object> fields;
        try
        {
            fields = ProtoReader.ReadFields(Convert.FromBase64String(pbElement.GetString()!));
        }
        catch
        {
            return;
        }

        _interactSamples++;
        if (_interactSamples <= 3)
        {
            var dump = string.Join(", ", fields.Select(p => $"{p.Key}={p.Value}"));
            StatusChanged?.Invoke($"INTERACT_WORD_V2 字段解析: {Truncate(dump, 400)}");
        }

        var msgType = GetProtoInt(fields, InteractFieldMsgType);
        var userName = GetProtoString(fields, InteractFieldUserName);
        var userId = GetProtoInt(fields, InteractFieldUserId);

        // msg_type: 1=进入直播间，2=关注，3=分享；只关心进场。
        if (msgType != 1)
        {
            return;
        }

        EntryObserved?.Invoke(userId, userName);

        if (!string.IsNullOrEmpty(userName))
        {
            NoticeReceived?.Invoke(new NoticeItem { Text = $"{userName}进入了直播间", Kind = NoticeKind.Entry });
        }
    }

    private static long GetProtoInt(Dictionary<int, object> fields, int fieldNumber) =>
        fields.TryGetValue(fieldNumber, out var value) && value is long number ? number : 0;

    private static string GetProtoString(Dictionary<int, object> fields, int fieldNumber) =>
        fields.TryGetValue(fieldNumber, out var value) && value is string text ? text : "";

    private static bool TryParseInteract(JsonElement root, out string text)
    {
        text = "";
        try
        {
            var data = root.GetProperty("data");
            var uname = data.TryGetProperty("uname", out var u) ? u.GetString() : "某人";
            if (string.IsNullOrEmpty(uname))
            {
                uname = "某人";
            }

            var msgType = data.TryGetProperty("msg_type", out var mt) ? mt.GetInt32() : 0;
            // msg_type: 1=进入直播间，2=关注，3=分享；只展示进入直播间。
            if (msgType != 1)
            {
                return false;
            }

            text = $"{uname}进入了直播间";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseGift(
        JsonElement root, out string userName, out string giftName, out int count, out double amount)
    {
        userName = "某人";
        giftName = "礼物";
        count = 1;
        amount = 0;
        try
        {
            var data = root.GetProperty("data");
            userName = data.TryGetProperty("uname", out var u) ? u.GetString() ?? "某人" : "某人";
            giftName = data.TryGetProperty("giftName", out var g) ? g.GetString() ?? "礼物" : "礼物";
            count = data.TryGetProperty("num", out var n) && n.TryGetInt32(out var num) ? Math.Max(1, num) : 1;
            var coinType = data.TryGetProperty("coin_type", out var c) ? c.GetString() : "gold";
            var price = data.TryGetProperty("price", out var p) && p.TryGetInt32(out var priceValue) ? priceValue : 0;
            var totalCoin = data.TryGetProperty("total_coin", out var t) && t.TryGetInt64(out var totalValue)
                ? totalValue
                : (long)price * count;

            // 金瓜子：1000 金瓜子 = 1 元
            if (string.Equals(coinType, "gold", StringComparison.OrdinalIgnoreCase))
            {
                amount = totalCoin / 1000.0;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseWelcomeGuard(JsonElement root, out string userName, out int guardLevel)
    {
        userName = "";
        guardLevel = 0;
        try
        {
            var data = root.GetProperty("data");
            userName = data.TryGetProperty("username", out var u) ? u.GetString() ?? ""
                     : data.TryGetProperty("uname", out var u2) ? u2.GetString() ?? "" : "";
            guardLevel = data.TryGetProperty("guard_level", out var g) ? g.GetInt32() : 0;
            return !string.IsNullOrEmpty(userName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析 GUARD_BUY（开通大航海）。</summary>
    private static bool TryParseGuardBuy(JsonElement root, out string userName, out int guardLevel, out int count)
    {
        userName = "";
        guardLevel = 0;
        count = 1;
        try
        {
            var data = root.GetProperty("data");
            userName = data.TryGetProperty("username", out var u) ? u.GetString() ?? ""
                     : data.TryGetProperty("uname", out var u2) ? u2.GetString() ?? "" : "";
            guardLevel = data.TryGetProperty("guard_level", out var g) ? g.GetInt32() : 0;
            if (data.TryGetProperty("num", out var n) && n.TryGetInt32(out var num) && num > 0)
            {
                count = num;
            }

            return !string.IsNullOrEmpty(userName);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseUserToast(JsonElement root, out string userName, out int guardLevel, out int count)
    {
        userName = "";
        guardLevel = 0;
        count = 1;
        try
        {
            var data = root.GetProperty("data");
            userName = data.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
            guardLevel = data.TryGetProperty("guard_level", out var g) ? g.GetInt32() : 0;
            if (data.TryGetProperty("num", out var n) && n.TryGetInt32(out var num) && num > 0)
            {
                count = num;
            }

            return !string.IsNullOrEmpty(userName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析 SUPER_CHAT_MESSAGE。
    /// 金额单位：SC 的 price 单位就是「元」（30 元的 SC，price = 30），直接用，不要再除。
    /// 注意别和礼物混：礼物的 price 是金瓜子，那边才需要除以 1000。
    /// 粉丝牌在 data.medal_info 上（不是 user_info 里），且这里是个对象而不是弹幕那种数组。
    /// </summary>
    private static bool TryParseSuperChat(JsonElement root, out SuperChatItem? item)
    {
        item = null;
        try
        {
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var message = data.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? ""
                : "";
            var price = TryReadInt(data, "price");

            // 留言为空、但确实有金额的 SC 也要显示，不能整条丢掉
            if (price <= 0 && string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            var uid = TryReadLong(data, "uid");
            var userName = "某人";
            var face = "";
            var guardLevel = 0;

            if (data.TryGetProperty("user_info", out var userInfo) && userInfo.ValueKind == JsonValueKind.Object)
            {
                if (userInfo.TryGetProperty("uname", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(nameElement.GetString()))
                {
                    userName = nameElement.GetString()!;
                }

                if (userInfo.TryGetProperty("face", out var faceElement) && faceElement.ValueKind == JsonValueKind.String)
                {
                    face = faceElement.GetString() ?? "";
                }

                if (uid <= 0)
                {
                    uid = TryReadLong(userInfo, "uid");
                }

                guardLevel = TryReadInt(userInfo, "guard_level");
            }

            string? medalName = null;
            var medalLevel = 0;
            if (data.TryGetProperty("medal_info", out var medal))
            {
                ParseMedalObject(medal, ref medalName, ref medalLevel);
            }

            item = new SuperChatItem
            {
                UserId = uid,
                UserName = userName,
                Face = face,
                MedalName = medalName,
                MedalLevel = medalLevel,
                GuardLevel = guardLevel,
                Amount = price,
                Message = message,
                DurationSeconds = TryReadInt(data, "time")
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 容错读整数。B站不同接口/时期会把同一个字段发成数字或字符串，
    /// 直接 GetInt32() 碰到字符串会抛异常，整条消息就被静默丢掉了。
    /// </summary>
    private static int TryReadInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            return 0;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        return element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed)
            ? parsed
            : 0;
    }

    private static long TryReadLong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element))
        {
            return 0;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
        {
            return number;
        }

        return element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var parsed)
            ? parsed
            : 0;
    }

    /// <summary>解析粉丝牌对象（SC 的 medal_info 是对象，不是弹幕那种数组）。</summary>
    private static void ParseMedalObject(JsonElement medal, ref string? medalName, ref int medalLevel)
    {
        if (medal.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        medalLevel = TryReadInt(medal, "medal_level");

        if (medal.TryGetProperty("medal_name", out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(nameElement.GetString()))
        {
            medalName = nameElement.GetString();
        }
    }

    /// <summary>补齐「互动人数」（高能榜口径，真实人数但不是在线人数）。</summary>
    private async Task FillInteractionAsync(RoomInfo room, CancellationToken token)
    {
        if (room.RoomId <= 0 || room.Uid <= 0)
        {
            return;
        }

        room.Interaction = await BiliRoomApi
            .QueryInteractionAsync(_http, _cookieContainer, room.RoomId, room.Uid, token)
            .ConfigureAwait(false);
    }

    private async Task<RoomInfo> GetRoomInfoAsync(string inputRoomId, CancellationToken token)
    {
        try
        {
            return await GetRoomInfoFromInfoApiAsync(inputRoomId, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception firstError)
        {
            try
            {
                return await GetRoomInfoFromRoomInitApiAsync(inputRoomId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception secondError)
            {
                throw new InvalidOperationException(
                    "无法获取直播间信息。请确认填的是 live.bilibili.com/ 后面的直播间号，而不是 UID。\r\n" +
                    $"接口1: {firstError.Message}\r\n接口2: {secondError.Message}",
                    secondError);
            }
        }
    }

    private async Task<RoomInfo> GetRoomInfoFromInfoApiAsync(string inputRoomId, CancellationToken token)
    {
        var query = string.IsNullOrEmpty(_mixinKey)
            ? $"room_id={Uri.EscapeDataString(inputRoomId)}"
            : BuildWbiSignedQuery(new Dictionary<string, string>
            {
                ["room_id"] = inputRoomId
            });

        var url = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom?{query}";
        using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"getInfoByRoom HTTP {(int)response.StatusCode}: {Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("room_info", out var roomInfoElement))
        {
            throw new InvalidOperationException($"getInfoByRoom 没有返回 room_info，可能房间号不存在: {Truncate(json)}");
        }

        var roomInfo = new RoomInfo
        {
            RoomId = roomInfoElement.TryGetProperty("room_id", out var roomId) ? roomId.GetInt64() : 0,
            Uid = roomInfoElement.TryGetProperty("uid", out var uid) ? uid.GetInt64() : 0,
            Title = roomInfoElement.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            Online = roomInfoElement.TryGetProperty("online", out var online) ? online.GetInt32() : 0,
            LiveStatus = roomInfoElement.TryGetProperty("live_status", out var live) ? live.GetInt32() == 1 : false
        };

        if (data.TryGetProperty("anchor_info", out var anchor))
        {
            roomInfo.UserName = anchor.TryGetProperty("base_info", out var baseInfo)
                && baseInfo.TryGetProperty("uname", out var uname)
                ? uname.GetString() ?? ""
                : "";
        }

        // watched_show 的含义随房间而变：「X人看过」才是真实观看人数，
        // 「X人气」则与 room_info.online 同值，没有额外信息。
        if (data.TryGetProperty("watched_show", out var watchedShow) &&
            watchedShow.TryGetProperty("text_large", out var watchedText) &&
            watchedText.ValueKind == JsonValueKind.String &&
            watchedText.GetString()!.Contains("看过", StringComparison.Ordinal) &&
            watchedShow.TryGetProperty("num", out var watchedNum) &&
            watchedNum.TryGetInt32(out var watchedValue))
        {
            roomInfo.Watched = watchedValue;
        }

        if (roomInfo.RoomId <= 0)
        {
            throw new InvalidOperationException($"getInfoByRoom 返回的 room_id 无效: {Truncate(json)}");
        }

        return roomInfo;
    }

    private async Task<RoomInfo> GetRoomInfoFromRoomInitApiAsync(string inputRoomId, CancellationToken token)
    {
        var url = $"https://api.live.bilibili.com/room/v1/Room/room_init?id={Uri.EscapeDataString(inputRoomId)}";
        using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"room_init HTTP {(int)response.StatusCode}: {Truncate(json)}");
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("room_id", out var roomId))
        {
            throw new InvalidOperationException($"room_init 没有返回房间数据，可能房间号不存在: {Truncate(json)}");
        }

        var roomInfo = new RoomInfo
        {
            RoomId = roomId.GetInt64(),
            Uid = data.TryGetProperty("uid", out var uid) ? uid.GetInt64() : 0,
            Title = data.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            Online = data.TryGetProperty("online", out var online) ? online.GetInt32() : 0,
            LiveStatus = data.TryGetProperty("live_status", out var live) ? live.GetInt32() == 1 : false
        };

        if (roomInfo.RoomId <= 0)
        {
            throw new InvalidOperationException($"room_init 返回的 room_id 无效: {Truncate(json)}");
        }

        return roomInfo;
    }

    private static void WriteRawDanmakuLog(string json)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MinecraftChatOverlay");
            Directory.CreateDirectory(dir);

            var file = Path.Combine(dir, "raw_danmaku.log");
            File.AppendAllText(file, json + Environment.NewLine + "----" + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static string Truncate(string text, int maxLength = 300)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private async Task<(string token, string host, int port)> GetDanmuServerAsync(long realRoomId, CancellationToken token)
    {
        var query = string.IsNullOrEmpty(_mixinKey)
            ? $"id={realRoomId}&type=0"
            : BuildWbiSignedQuery(new Dictionary<string, string>
            {
                ["id"] = realRoomId.ToString(),
                ["type"] = "0"
            });

        StatusChanged?.Invoke(string.IsNullOrEmpty(_mixinKey)
            ? "getDanmuInfo 未使用 WBI 签名"
            : $"getDanmuInfo 使用 WBI 签名: {query}");
        var danmuUrl = $"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?{query}";
        using var request = new HttpRequestMessage(HttpMethod.Get, danmuUrl);

        // 确保请求一定带上 Cookie（某些情况下 CookieContainer 的自动发送可能不生效）
        var cookieHeader = _cookieContainer.GetCookieHeader(new Uri("https://api.live.bilibili.com/"));
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        using var danmuResponse = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var json = await danmuResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);

        if (!danmuResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"getDanmuInfo HTTP {(int)danmuResponse.StatusCode}: {Truncate(json)}");
        }

        using var danmuDoc = JsonDocument.Parse(json);
        if (!danmuDoc.RootElement.TryGetProperty("data", out var danmuData) ||
            !danmuData.TryGetProperty("token", out var tokenElement) ||
            !danmuData.TryGetProperty("host_list", out var hostList))
        {
            throw new InvalidOperationException($"getDanmuInfo 返回数据不完整: {Truncate(json)}");
        }

        var tokenKey = tokenElement.GetString() ?? "";
        string host = "";
        var port = 443;
        foreach (var hostElement in hostList.EnumerateArray())
        {
            host = hostElement.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
            if (hostElement.TryGetProperty("wss_port", out var wssPort))
            {
                port = wssPort.GetInt32();
            }

            if (!string.IsNullOrEmpty(host))
            {
                break;
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            throw new InvalidOperationException($"getDanmuInfo 没有返回可用的弹幕服务器: {Truncate(json)}");
        }

        return (tokenKey, host, port);
    }

    private static byte[] BuildPacket(byte[] body, int operation, int protocol = 1)
    {
        var packet = new byte[HeaderLength + body.Length];
        WriteInt32(packet, 0, packet.Length);
        WriteUInt16(packet, 4, HeaderLength);
        WriteUInt16(packet, 6, (ushort)protocol);
        WriteInt32(packet, 8, operation);
        WriteInt32(packet, 12, 1);
        Buffer.BlockCopy(body, 0, packet, HeaderLength, body.Length);
        return packet;
    }

    private static int ReadInt32(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset, 4));

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));

    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), value);

    private static void WriteUInt16(byte[] buffer, int offset, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)value);
}
