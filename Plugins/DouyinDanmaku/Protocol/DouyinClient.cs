using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DouyinDanmaku.Protocol;

/// <summary>六类消息的类别。</summary>
public enum DanmakuKind
{
    /// <summary>弹幕（WebcastChatMessage）。</summary>
    Chat,
    /// <summary>点赞（WebcastLikeMessage）。</summary>
    Like,
    /// <summary>礼物（WebcastGiftMessage）。</summary>
    Gift,
    /// <summary>入场（WebcastMemberMessage）。</summary>
    Member,
    /// <summary>房间统计（WebcastRoomUserSeqMessage）。</summary>
    RoomStats,
    /// <summary>粉丝团（WebcastFansclubMessage）。</summary>
    Fansclub,
}

/// <summary>
/// 一条解析后的直播消息。字段与 DyDanmaku 的输出模板变量一一对应。
/// </summary>
public sealed class DanmakuEvent
{
    public required DanmakuKind Kind { get; init; }

    /// <summary>发送者昵称（${nickname}）。</summary>
    public string Nickname { get; init; } = "";

    /// <summary>财富等级（${payGradeLevel}）。-1 表示未取到。</summary>
    public long PayGradeLevel { get; init; } = -1;

    /// <summary>粉丝团等级（${fansClubLevel}）。-1 表示未取到。</summary>
    public long FansClubLevel { get; init; } = -1;

    /// <summary>弹幕内容（${content}，仅 Chat）。</summary>
    public string Content { get; init; } = "";

    /// <summary>点赞数（${count}，仅 Like）。</summary>
    public long Count { get; init; }

    /// <summary>点赞累计（Like.total）。</summary>
    public long LikeTotal { get; init; }

    /// <summary>礼物名（${giftName}，仅 Gift）。</summary>
    public string GiftName { get; init; } = "";

    /// <summary>礼物钻石价（${giftDiamondCount}，仅 Gift）。</summary>
    public long GiftDiamondCount { get; init; }

    /// <summary>礼物连击数（${giftCombo}，仅 Gift）。</summary>
    public long GiftCombo { get; init; }

    /// <summary>本次连击计数（${comboCount}，仅 Gift）。</summary>
    public long ComboCount { get; init; }

    /// <summary>重复计数（${repeatCount}，仅 Gift）。</summary>
    public long RepeatCount { get; init; }

    /// <summary>礼物 id（${giftId}，仅 Gift）。</summary>
    public long GiftId { get; init; }

    /// <summary>礼物组合数（仅 Gift）。</summary>
    public long GroupCount { get; init; }

    /// <summary>当前在线人数（${memberCount}，仅 Member）。</summary>
    public long MemberCount { get; init; }

    /// <summary>入场动作描述（${actionDescription}，仅 Member）。</summary>
    public string ActionDescription { get; init; } = "";

    /// <summary>用户 id（${userId}，仅 Member）。</summary>
    public long UserId { get; init; }

    /// <summary>房间当前人数（${totalStr}，仅 RoomStats）。</summary>
    public long Total { get; init; }

    /// <summary>房间累计人数（${totalPvForAnchor}，仅 RoomStats）。</summary>
    public long TotalPvForAnchor { get; init; }

    /// <summary>粉丝团原始可读文本（仅 Fansclub）。</summary>
    public string FansclubText { get; init; } = "";
}

/// <summary>
/// 抖音直播推流客户端。HttpClient + ClientWebSocket + 手写 protobuf。
/// 推流握手必须带 signature（X-Bogus，抖音 webmssdk 的 JS 算出来的），
/// 缺它服务端直接拿 200 拒掉 WebSocket 升级 —— 签名由外部注入的委托计算
/// （插件里传 <see cref="DouyinSigner"/>，测试里传 null 走离线）。
/// 协议流程来自三个开源实现的交叉验证：DyDanmaku、dycast、DouyinLiveWebFetcher。
/// </summary>
public sealed class DouyinClient : IAsyncDisposable
{
    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";

    /// <summary>提取不到 user_unique_id 时的兜底访客 id（数字合法即可，签名与 URL 保持一致就行）。</summary>
    private const string FallbackUniqueId = "7319483754668557238";

    // 实测可用的推流端点（按优先级）。前三个来自开源实现，第四个是 hl 变体兜底。
    private static readonly string[] Endpoints =
    {
        "wss://webcast3-ws-web-lq.douyin.com/webcast/im/push/v2/",
        "wss://webcast100-ws-web-lq.douyin.com/webcast/im/push/v2/",
        "wss://webcast5-ws-web-hl.douyin.com/webcast/im/push/v2/",
        "wss://webcast3-ws-web-hl.douyin.com/webcast/im/push/v2/",
    };

    /// <summary>签名委托：(roomId, userUniqueId) → X-Bogus；null 或返回 null = 不带签名连（大概率被拒）。</summary>
    private readonly Func<string, string, string?>? _signer;

    /// <param name="signer">签名器委托（用 <see cref="DouyinSigner.Compute"/>；离线测试传 null）。</param>
    public DouyinClient(Func<string, string, string?>? signer = null)
    {
        _signer = signer;
    }

    /// <summary>解析出一条消息。</summary>
    public event Action<DanmakuEvent>? MessageReceived;

    /// <summary>状态变化（未连接 / 连接中 / 已连接 / 错误信息）。</summary>
    public event Action<string>? StatusChanged;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _recvLoop;
    private Task? _heartbeat;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>最近一次拉到的房间页面原文（诊断用：roomId 解析失败时插件会把它落盘）。</summary>
    public static string? LastRoomPage { get; set; }

    /// <summary>
    /// 连接直播间并开始收流。返回是否成功。
    /// </summary>
    public async Task<bool> ConnectAsync(string webRoomId, CancellationToken ct = default)
    {
        if (IsConnected)
        {
            return true;
        }

        await DisconnectAsync();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 之后一律用这个局部令牌：DisconnectAsync 会把 _cts 置 null，
        // 而下面的 await 期间随时可能被它打断，再去解引用 _cts 就是 NRE。
        var token = _cts.Token;

        // 步骤 1：取 ttwid（wss 连接必需的 cookie）
        StatusChanged?.Invoke("正在获取 ttwid…");
        string ttwid = await FetchTtwidAsync(token).ConfigureAwait(false);
        if (ttwid == "")
        {
            StatusChanged?.Invoke("获取 ttwid 失败");
            return false;
        }

        // 步骤 2：webRoomId → 真实 roomId + user_unique_id（页面 RENDER_DATA 里，此步不需要 signature）
        StatusChanged?.Invoke("正在获取 roomId…");
        var (roomId, uniqueId, pageBody) = await FetchRoomParamsAsync(webRoomId, ttwid, token).ConfigureAwait(false);
        LastRoomPage = pageBody;
        if (uniqueId == "")
        {
            uniqueId = FallbackUniqueId;
        }

        if (roomId == "")
        {
            // 页面解析不出来：纯数字房间号直接拿自己当 roomId。
            // live.douyin.com/{数字} 里这个数字通常就是 roomId 的数字形态；
            // DySpider 解析 RENDER_DATA 主要是为了兼容短号/抖音号形态。
            if (IsPlainRoomId(webRoomId))
            {
                StatusChanged?.Invoke($"页面未解析出 roomId，改用房间号自身 {webRoomId} 连接");
                roomId = webRoomId;
            }
            else
            {
                StatusChanged?.Invoke($"无法解析房间 {webRoomId} 的 roomId（页面快照已保存，见插件日志）");
                return false;
            }
        }

        // 步骤 2.5：算推流签名（X-Bogus）。缺签名握手会被服务端 200 拒掉。
        string? signature = null;
        if (_signer != null)
        {
            StatusChanged?.Invoke("正在计算签名…");
            try
            {
                signature = _signer(roomId, uniqueId);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"签名异常：{Truncate(ex.Message, 100)}");
            }

            if (signature == null)
            {
                StatusChanged?.Invoke("未取得签名（仍尝试无签名连接）");
            }
        }

        // 步骤 3：依次尝试端点，找出一个能连上的
        ClientWebSocket? connected = null;
        string? okEndpoint = null;
        Exception? lastError = null;
        foreach (var ep in Endpoints)
        {
            var url = BuildWssUrl(ep, roomId, webRoomId, uniqueId, signature);
            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("User-Agent", Ua);
            ws.Options.SetRequestHeader("Cookie", $"ttwid={ttwid}");
            ws.Options.SetRequestHeader("Referer", $"https://live.douyin.com/{webRoomId}");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
                await ws.ConnectAsync(new Uri(url), linked.Token).ConfigureAwait(false);
                connected = ws;
                okEndpoint = ep;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                ws.Dispose();

                // 用户点了「取消/断开」：别再往下试端点，否则会白跑完剩下几个，
                // 最后还把状态行写成「连接超时」，把用户自己的取消说成超时。
                if (token.IsCancellationRequested)
                {
                    StatusChanged?.Invoke("已取消连接");
                    return false;
                }
            }
        }

        if (connected == null || okEndpoint == null)
        {
            var reason = lastError is OperationCanceledException
                ? "连接超时"
                : lastError?.Message ?? "未知原因";
            StatusChanged?.Invoke($"推流端点连接失败：{Truncate(reason, 120)}");
            return false;
        }

        _ws = connected;
        ResetStats();
        StatusChanged?.Invoke($"已连接直播间 {webRoomId}（{okEndpoint.Split('/')[2]}）");

        _heartbeat = Task.Run(() => HeartbeatAsync(_cts.Token), CancellationToken.None);
        _recvLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        return true;
    }

    /// <summary>断开并清理。幂等，可反复调用。</summary>
    public async Task DisconnectAsync()
    {
        var cts = _cts;
        var ws = _ws;
        _cts = null;
        _ws = null;

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        if (ws != null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    // CloseAsync 在收流循环正阻塞于 ReceiveAsync 时可能不理会取消令牌
                    // （同一个 socket 上不允许并发的收发），所以再兜一层硬上限。
                    var closing = ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                    await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(false);
                }
            }
            catch
            {
                // 关闭失败不影响清理
            }
            finally
            {
                ws.Dispose();
            }
        }

        foreach (var t in new[] { _recvLoop, _heartbeat })
        {
            if (t == null)
            {
                continue;
            }

            try
            {
                // 后台任务不能无限等：收流循环可能正卡在 ReceiveAsync 上，
                // 而它等的是已经 Dispose 掉的 socket，谁也保证不了它马上退出。
                // 3 秒不结束就放它去，别把「断开」按钮一起拖死。
                var finished = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
                if (finished == t)
                {
                    await t.ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }
        _recvLoop = null;
        _heartbeat = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private async Task<string> FetchTtwidAsync(CancellationToken ct)
    {
        using var http = MakeHttp();
        var req = new HttpRequestMessage(HttpMethod.Get, "https://live.douyin.com/");
        req.Headers.Add("Cookie", "__ac_nonce=0123407cc00a9e438deb4");
        var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.Headers.TryGetValues("Set-Cookie", out var sc))
        {
            foreach (var h in sc)
            {
                var m = System.Text.RegularExpressions.Regex.Match(h, @"ttwid=([^;]+)");
                if (m.Success)
                {
                    return m.Groups[1].Value;
                }
            }
        }

        return "";
    }

    private static async Task<(string RoomId, string UniqueId, string Body)> FetchRoomParamsAsync(string webRoomId, string ttwid, CancellationToken ct)
    {
        using var http = MakeHttp();
        var req = new HttpRequestMessage(HttpMethod.Get, $"https://live.douyin.com/{webRoomId}");
        req.Headers.Add("Cookie", $"ttwid={ttwid}&msToken=; __ac_nonce=0123407cc00a9e438deb4");
        var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (ExtractRoomId(body), ExtractUniqueId(body), body);
    }

    /// <summary>页面 JSON 里的 user_unique_id（转义形态 \"user_unique_id\":\"7319...\"）。</summary>
    private static string ExtractUniqueId(string body)
    {
        foreach (var key in new[] { "\\\"user_unique_id\\\":\\\"", "\"user_unique_id\":\"" })
        {
            int i = body.IndexOf(key, StringComparison.Ordinal);
            if (i < 0)
            {
                continue;
            }

            int start = i + key.Length;
            int j = start;
            while (j < body.Length && char.IsAsciiDigit(body[j]))
            {
                j++;
            }

            if (j > start)
            {
                return body[start..j];
            }
        }

        // 宽松兜底：user_unique_id 与数字之间允许转义引号、冒号、空白
        var m = System.Text.RegularExpressions.Regex.Match(body, @"user_unique_id[^\d]{0,10}(\d{6,})");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>纯数字且长度合理 → 可直接当作 roomId 用。</summary>
    private static bool IsPlainRoomId(string s) =>
        s.Length >= 6 && s.Length <= 20 && s.All(char.IsAsciiDigit);

    /// <summary>页面 JSON 里的 roomId。兼容 "roomId":"123" 与被转义的 roomId\":\"123\"。</summary>
    private static string ExtractRoomId(string body)
    {
        foreach (var key in new[] { "\"roomId\":\"", "roomId\\\":\\\"", "\"room_id\":\"", "room_id\\\":\\\"" })
        {
            int i = body.IndexOf(key, StringComparison.Ordinal);
            if (i < 0)
            {
                continue;
            }

            int start = i + key.Length;
            int j = start;
            while (j < body.Length && char.IsAsciiDigit(body[j]))
            {
                j++;
            }

            if (j > start)
            {
                return body[start..j];
            }
        }

        // 宽松兜底：键名与数字之间允许转义引号、冒号、空白
        // （RENDER_DATA 是被转义塞进 HTML 的 JSON，"roomId\":\"123\"" 这种）
        var m = System.Text.RegularExpressions.Regex.Match(
            body, @"(?:roomId|room_id)[^\d]{0,12}(\d{8,})");
        return m.Success ? m.Groups[1].Value : "";
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ws = _ws;
                if (ws == null || ws.State != WebSocketState.Open)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                await SendAsync(ws, BuildPushFrame("hb"), WebSocketMessageType.Binary, ct).ConfigureAwait(false);
                await Task.Delay(5000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // 心跳失败不抛出，收帧循环会察觉连接断开
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[1 << 20];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var ws = _ws;
                if (ws == null)
                {
                    return;
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

                var (res, data) = await ReceiveFullAsync(ws, buf, linked.Token).ConfigureAwait(false);
                if (res == null)
                {
                    StatusChanged?.Invoke("连接已断开");
                    return;
                }

                if (res.MessageType == WebSocketMessageType.Close)
                {
                    StatusChanged?.Invoke($"服务端关闭连接（{res.CloseStatusDescription}）");
                    return;
                }

                if (data.Length == 0)
                {
                    continue;
                }

                try
                {
                    HandleFrame(data, ws);
                }
                catch
                {
                    // 单帧解析失败不影响后续帧
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"收流出错：{Truncate(ex.Message, 120)}");
        }
    }

    /// <summary>收满一帧（含分段拼接）。</summary>
    private static async Task<(WebSocketReceiveResult?, byte[])> ReceiveFullAsync(
        ClientWebSocket ws, byte[] buf, CancellationToken ct)
    {
        var ms = new MemoryStream();
        WebSocketReceiveResult res;
        try
        {
            do
            {
                res = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    return (res, Array.Empty<byte>());
                }

                ms.Write(buf, 0, res.Count);
            }
            while (!res.EndOfMessage);
        }
        catch
        {
            return (null, Array.Empty<byte>());
        }

        return (res, ms.ToArray());
    }

    /// <summary>一帧的原始信息（诊断用：不关心情是否被解析出来，只想知道服务端推了什么）。</summary>
    internal struct FrameInfo
    {
        public bool NeedAck;
        public string? InternalExt;
        public ulong LogId;

        /// <summary>PushFrame.payloadType（hb / ack / 空 = 业务帧）。</summary>
        public string? PayloadType;

        /// <summary>payload 字节数（0 = 空帧）。</summary>
        public int PayloadLen;

        /// <summary>帧内所有 Message.method 名（含我们没实现解析的）。</summary>
        public string[] Methods;

        public FrameInfo()
        {
            Methods = Array.Empty<string>();
        }
    }

    /// <summary>
    /// 解析一帧推流：PushFrame → gzip → Response → 按 method 分派六类消息。
    /// 拆成静态方法是为了能离线自测（本机无法连抖音，联调不可行）。
    /// </summary>
    internal static List<DanmakuEvent> ParseFrame(byte[] data, out FrameInfo info)
    {
        info = new FrameInfo();
        var result = new List<DanmakuEvent>();

        try
        {
            // PushFrame{seqId=1, logId=2, service=3, method=4, headersList=5, payloadEncoding=6, payloadType=7, payload=8}
            string? payloadType = null;
            byte[]? payload = null;

            var frame = new Pb(data);
            while (frame.Next(out int f, out int wire))
            {
                if (f == 7 && wire == 2)
                {
                    payloadType = frame.Str();
                }
                else if (f == 8 && wire == 2)
                {
                    payload = frame.Bytes();
                }
                else if (f == 2 && wire == 0)
                {
                    info.LogId = frame.Varint();
                }
                else
                {
                    frame.Skip(wire);
                }
            }

            info.PayloadType = payloadType;
            info.PayloadLen = payload?.Length ?? 0;

            if (payloadType is "hb" or "ack" || payload is not { Length: > 0 })
            {
                return result;
            }

            byte[] inner;
            try
            {
                inner = Decompress(payload);
            }
            catch
            {
                inner = payload;
            }

            // Response{messagesList=1, cursor=2, fetchInterval=3, now=4, internalExt=5, fetchType=6, needAck=9}
            var methods = new List<string>();
            var messages = new List<(string Method, byte[] Payload)>();

            var response = new Pb(inner);
            while (response.Next(out int rf, out int rw))
            {
                if (rf == 1 && rw == 2)
                {
                    // Message{method=1, payload=2, msgId=3, msgType=4, offset=5}
                    var msg = new Pb(response.Bytes());
                    string method = "";
                    byte[]? msgPayload = null;
                    while (msg.Next(out int mf, out int mw))
                    {
                        if (mf == 1 && mw == 2)
                        {
                            method = msg.Str();
                        }
                        else if (mf == 2 && mw == 2)
                        {
                            msgPayload = msg.Bytes();
                        }
                        else
                        {
                            msg.Skip(mw);
                        }
                    }

                    if (method != "")
                    {
                        methods.Add(method);
                        if (msgPayload is { Length: > 0 })
                        {
                            messages.Add((method, msgPayload));
                        }
                    }
                }
                else if (rf == 5 && rw == 2)
                {
                    info.InternalExt = response.Str();
                }
                else if (rf == 9 && rw == 0)
                {
                    info.NeedAck = response.Varint() != 0;
                }
                else
                {
                    response.Skip(rw);
                }
            }

            info.Methods = methods.ToArray();

            foreach (var (method, msgPayload) in messages)
            {
                var ev = method switch
                {
                    "WebcastChatMessage" => ParseChat(msgPayload),
                    "WebcastLikeMessage" => ParseLike(msgPayload),
                    "WebcastGiftMessage" => ParseGift(msgPayload),
                    "WebcastMemberMessage" => ParseMember(msgPayload),
                    "WebcastRoomUserSeqMessage" => ParseRoomUserSeq(msgPayload),
                    "WebcastFansclubMessage" => ParseFansclub(msgPayload),
                    _ => null,
                };

                if (ev != null)
                {
                    result.Add(ev);
                }
            }
        }
        catch
        {
            // 单帧解析失败返回空，不影响后续帧
        }

        return result;
    }

    /// <summary>兼容旧签名（tests 在用）：只回传 ack 三件套。</summary>
    internal static List<DanmakuEvent> ParseFrame(byte[] data, out bool needAck, out string? internalExt, out ulong logId)
    {
        var events = ParseFrame(data, out FrameInfo info);
        needAck = info.NeedAck;
        internalExt = info.InternalExt;
        logId = info.LogId;
        return events;
    }

    // ---------- 收流统计（诊断用） ----------
    private long _frameCount;
    private long _hbCount;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _methodCounts = new();

    /// <summary>一行收流摘要，状态行用：帧数/心跳数/各 method 计数。</summary>
    public string StatsSummary
    {
        get
        {
            var frames = Interlocked.Read(ref _frameCount);
            var hb = Interlocked.Read(ref _hbCount);
            if (_methodCounts.IsEmpty)
            {
                return $"帧 {frames}（心跳 {hb}，无业务帧）";
            }

            var parts = _methodCounts.OrderBy(kv => kv.Value).Reverse().Select(kv => $"{ShortMethod(kv.Key)}×{kv.Value}");
            return $"帧 {frames}（心跳 {hb}）{string.Join(" ", parts)}";
        }
    }

    private static string ShortMethod(string m) => m.StartsWith("Webcast", StringComparison.Ordinal) ? m[7..^7] : m;

    private void ResetStats()
    {
        Interlocked.Exchange(ref _frameCount, 0);
        Interlocked.Exchange(ref _hbCount, 0);
        _methodCounts.Clear();
    }

    private void HandleFrame(byte[] data, ClientWebSocket ws)
    {
        Interlocked.Increment(ref _frameCount);
        var events = ParseFrame(data, out FrameInfo info);

        if (info.PayloadType is "hb" or "ack")
        {
            Interlocked.Increment(ref _hbCount);
        }

        foreach (var m in info.Methods)
        {
            _methodCounts.AddOrUpdate(m, 1, (_, c) => c + 1);
        }

        if (info.NeedAck)
        {
            var ackPayload = info.InternalExt == null ? null : Encoding.UTF8.GetBytes(info.InternalExt);
            _ = SendAsync(ws, BuildPushFrame("ack", ackPayload, info.LogId), WebSocketMessageType.Binary, CancellationToken.None);
        }

        foreach (var ev in events)
        {
            MessageReceived?.Invoke(ev);
        }
    }

    // ---------- 六类消息解析 ----------

    /// <summary>
    /// ChatMessage{common=1, user=2, content=3, visibleToSender=4}
    /// </summary>
    private static DanmakuEvent ParseChat(byte[] buf)
    {
        string nickname = "";
        string content = "";
        long pay = -1, fans = -1;

        var p = new Pb(buf);
        while (p.Next(out int f, out int wire))
        {
            if (f == 2 && wire == 2)
            {
                ReadUser(p.Bytes(), ref nickname, ref pay, ref fans);
            }
            else if (f == 3 && wire == 2)
            {
                content = p.Str();
            }
            else
            {
                p.Skip(wire);
            }
        }

        return new DanmakuEvent
        {
            Kind = DanmakuKind.Chat,
            Nickname = nickname,
            PayGradeLevel = pay,
            FansClubLevel = fans,
            Content = content,
        };
    }

    /// <summary>
    /// LikeMessage{common=1, count=2, total=3, color=4, user=5, icon=6}
    /// </summary>
    private static DanmakuEvent ParseLike(byte[] buf)
    {
        string nickname = "";
        long pay = -1, fans = -1, count = 0, total = 0;

        var p = new Pb(buf);
        while (p.Next(out int f, out int wire))
        {
            if (f == 2 && wire == 0) count = (long)p.Varint();
            else if (f == 3 && wire == 0) total = (long)p.Varint();
            else if (f == 5 && wire == 2) ReadUser(p.Bytes(), ref nickname, ref pay, ref fans);
            else p.Skip(wire);
        }

        return new DanmakuEvent
        {
            Kind = DanmakuKind.Like,
            Nickname = nickname,
            PayGradeLevel = pay,
            FansClubLevel = fans,
            Count = count,
            LikeTotal = total,
        };
    }

    /// <summary>
    /// GiftMessage{common=1, giftId=2, fanTicketCount=3, groupCount=4, repeatCount=5, comboCount=6, user=7, toUser=8, …}
    /// GiftStruct 的字段编号尚未拿到权威定义，礼物名与钻石价走启发式兜底：
    /// 跳过已知的 user(7)/toUser(8) 后，取第一个含字符串字段的嵌套 message 当作礼物结构。
    /// </summary>
    private static DanmakuEvent ParseGift(byte[] buf)
    {
        string nickname = "";
        long pay = -1, fans = -1;
        long giftId = 0, groupCount = 0, repeatCount = 0, comboCount = 0;
        string giftName = "";
        long giftDiamond = 0;

        var p = new Pb(buf);
        while (p.Next(out int f, out int wire))
        {
            switch (f)
            {
                case 2 when wire == 0: giftId = (long)p.Varint(); break;
                case 4 when wire == 0: groupCount = (long)p.Varint(); break;
                case 5 when wire == 0: repeatCount = (long)p.Varint(); break;
                case 6 when wire == 0: comboCount = (long)p.Varint(); break;
                case 7 when wire == 2: ReadUser(p.Bytes(), ref nickname, ref pay, ref fans); break;
                case 8 when wire == 2: p.Skip(wire); break; // toUser
                default:
                    // 兜底扫 GiftStruct：跳过已知的 common(1)/user(7)/toUser(8)，
                    // 取第一个含字符串字段的嵌套 message 当作礼物结构。
                    if (wire == 2 && giftName == "" && f is not (1 or 7 or 8))
                    {
                        var candidate = p.Bytes();
                        var (name, diamond) = TryReadGiftStruct(candidate);
                        if (name != "")
                        {
                            giftName = name;
                            giftDiamond = diamond;
                            break;
                        }
                    }
                    else
                    {
                        p.Skip(wire);
                    }

                    break;
            }
        }

        return new DanmakuEvent
        {
            Kind = DanmakuKind.Gift,
            Nickname = nickname,
            PayGradeLevel = pay,
            FansClubLevel = fans,
            GiftId = giftId,
            GiftName = giftName,
            GiftDiamondCount = giftDiamond,
            GiftCombo = comboCount,
            ComboCount = comboCount,
            RepeatCount = repeatCount,
            GroupCount = groupCount,
        };
    }

    /// <summary>
    /// 从 GiftStruct 里取礼物名与钻石价。GiftStruct 权威编号缺失，用启发式：
    /// 字符串字段里取最长的那个当礼物名；varint 里取 1~100000 区间第一个非零值当钻石价。
    /// </summary>
    private static (string Name, long Diamond) TryReadGiftStruct(byte[] buf)
    {
        if (buf.Length == 0 || buf.Length > 4096)
        {
            return ("", 0);
        }

        string best = "";
        long diamond = 0;
        var p = new Pb(buf);
        while (p.Next(out int f, out int wire))
        {
            if (wire == 2)
            {
                var s = TryUtf8(p.Bytes()) ?? "";
                if (s.Length > best.Length && s.Length <= 64 && !looksLikeUrl(s))
                {
                    best = s;
                }
            }
            else if (wire == 0)
            {
                var v = p.Varint();
                if (diamond == 0 && v > 0 && v < 100000)
                {
                    diamond = (long)v;
                }
            }
            else
            {
                p.Skip(wire);
            }
        }

        return (best, diamond);
    }

    private static bool looksLikeUrl(string s) => s.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// MemberMessage{common=1, user=2, memberCount=3, operator=4, …, actionDescription=11, userId=12, popStr=14}
    /// </summary>
    private static DanmakuEvent ParseMember(byte[] buf)
    {
        string nickname = "";
        long pay = -1, fans = -1;
        long memberCount = 0, userId = 0;
        string actionDescription = "";

        var p = new Pb(buf);
        while (p.Next(out int f, out int wire))
        {
            if (f == 2 && wire == 2) ReadUser(p.Bytes(), ref nickname, ref pay, ref fans);
            else if (f == 3 && wire == 0) memberCount = (long)p.Varint();
            else if (f == 11 && wire == 2) actionDescription = p.Str();
            else if (f == 12 && wire == 0) userId = (long)p.Varint();
            else p.Skip(wire);
        }

        return new DanmakuEvent
        {
            Kind = DanmakuKind.Member,
            Nickname = nickname,
            PayGradeLevel = pay,
            FansClubLevel = fans,
            MemberCount = memberCount,
            ActionDescription = actionDescription,
            UserId = userId,
        };
    }

    /// <summary>
    /// RoomUserSeqMessage：已确认 total=3；totalPvForAnchor 的编号没有权威来源，
    /// 兜底取除 total 之外的第一个 varint。
    /// </summary>
    private static DanmakuEvent ParseRoomUserSeq(byte[] buf)
    {
        long total = 0, totalPv = 0;

        var p = new Pb(buf);
        while (p.Next(out int f, out int wire))
        {
            if (wire != 0)
            {
                p.Skip(wire);
                continue;
            }

            var v = (long)p.Varint();
            if (f == 3)
            {
                total = v;
            }
            else if (totalPv == 0)
            {
                totalPv = v;
            }
        }

        return new DanmakuEvent
        {
            Kind = DanmakuKind.RoomStats,
            Total = total,
            TotalPvForAnchor = totalPv,
        };
    }

    /// <summary>
    /// WebcastFansclubMessage 的字段编号没有权威来源，兜底直接展示可读文本。
    /// </summary>
    private static DanmakuEvent ParseFansclub(byte[] buf)
    {
        string nickname = "";
        long pay = -1, fans = -1;
        string? text = null;

        var p = new Pb(buf);
        while (p.Next(out int f, out int wire))
        {
            if (wire == 2)
            {
                var sub = p.Bytes();
                // 优先尝试当成 User 解析（粉丝团消息里通常有 user 字段）
                if (nickname == "")
                {
                    string n = "";
                    long pp = -1, ff = -1;
                    ReadUser(sub, ref n, ref pp, ref ff);
                    if (n != "")
                    {
                        nickname = n;
                        pay = pp;
                        fans = ff;
                        continue;
                    }
                }

                text ??= TryUtf8(sub);
            }
            else
            {
                p.Skip(wire);
            }
        }

        return new DanmakuEvent
        {
            Kind = DanmakuKind.Fansclub,
            Nickname = nickname,
            PayGradeLevel = pay,
            FansClubLevel = fans,
            FansclubText = text ?? "",
        };
    }

    /// <summary>
    /// User{id=1, shortId=2, nickname=3, gender=4, …, followInfo=22, payGrade=23, fansClub=24}
    /// 　PayGrade{totalDiamondCount=1, name=3, level=6, nextDiamond=8, gradeDescribe=13}
    /// 　FansClub{data=1(FansClubData)}；FansClubData{clubName=1, level=2, …}
    /// </summary>
    private static void ReadUser(byte[] buf, ref string nickname, ref long payLevel, ref long fansLevel)
    {
        var p = new Pb(buf);
        while (p.Next(out int f, out int wire))
        {
            if (f == 3 && wire == 2)
            {
                nickname = p.Str();
            }
            else if (f == 23 && wire == 2)
            {
                var pg = p.Sub();
                while (pg.Next(out int pf, out int pw))
                {
                    if (pf == 6 && pw == 0)
                    {
                        payLevel = (long)pg.Varint();
                    }
                    else
                    {
                        pg.Skip(pw);
                    }
                }
            }
            else if (f == 24 && wire == 2)
            {
                var fc = p.Sub();
                while (fc.Next(out int ff, out int fw))
                {
                    if (ff == 1 && fw == 2)
                    {
                        var data = fc.Sub();
                        while (data.Next(out int df, out int dw))
                        {
                            if (df == 2 && dw == 0)
                            {
                                fansLevel = (long)data.Varint();
                            }
                            else
                            {
                                data.Skip(dw);
                            }
                        }
                    }
                    else
                    {
                        fc.Skip(fw);
                    }
                }
            }
            else
            {
                p.Skip(wire);
            }
        }
    }

    // ---------- 底层工具 ----------

    private async Task SendAsync(ClientWebSocket ws, byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(data, type, true, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // 发送失败不抛出
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>PushFrame{seqId=1, logId=2, …, payloadType=7, payload=8}</summary>
    private static byte[] BuildPushFrame(string payloadType, byte[]? payload = null, ulong logId = 0)
    {
        var ms = new MemoryStream();
        void Varint(ulong v)
        {
            while (v >= 0x80)
            {
                ms.WriteByte((byte)(v | 0x80));
                v >>= 7;
            }

            ms.WriteByte((byte)v);
        }

        void Tag(int f, int w) => Varint((ulong)((f << 3) | w));

        if (logId != 0)
        {
            Tag(2, 0);
            Varint(logId);
        }

        Tag(7, 2);
        var typeBytes = Encoding.UTF8.GetBytes(payloadType);
        Varint((ulong)typeBytes.Length);
        ms.Write(typeBytes);

        if (payload is { Length: > 0 })
        {
            Tag(8, 2);
            Varint((ulong)payload.Length);
            ms.Write(payload);
        }

        return ms.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        gz.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static string? TryUtf8(byte[] b)
    {
        if (b.Length == 0)
        {
            return null;
        }

        try
        {
            var s = Encoding.UTF8.GetString(b);
            foreach (var c in s)
            {
                if (c < ' ' && c != '\t' && c != '\r' && c != '\n')
                {
                    return null;
                }
            }

            return s;
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient MakeHttp()
    {
        var handler = new HttpClientHandler { UseCookies = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add("User-Agent", Ua);
        return client;
    }

    private static string BuildWssUrl(string endpoint, string roomId, string webRoomId, string uniqueId, string? signature)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url = endpoint +
            "?app_name=douyin_web&version_code=180800&webcast_sdk_version=1.0.14-beta.0" +
            "&update_version_code=1.0.14-beta.0&compress=gzip" +
            "&internal_ext=internal_src:dim|wss_push_room_id:" + roomId +
            "|wss_push_did:" + uniqueId + "|first_req_ms:" + now.ToString(CultureInfo.InvariantCulture) +
            "|fetch_time:" + now.ToString(CultureInfo.InvariantCulture) +
            "|seq:1|wss_info:0-0-0-0|wrds_v:7392094459690748497" +
            "&cursor=t-" + now.ToString(CultureInfo.InvariantCulture) + "_r-1_d-1_u-1_h-1" +
            "&host=https://live.douyin.com&aid=6383&live_id=1&did_rule=3&endpoint=live_pc&support_wrds=1" +
            "&user_unique_id=" + uniqueId +
            "&im_path=/webcast/im/fetch/&device_platform=web&cookie_enabled=true" +
            "&screen_width=1920&screen_height=1080&browser_language=zh-CN&browser_platform=Win32" +
            "&browser_name=Mozilla&browser_version=5.0%20(Windows)&browser_online=true&tz_name=Asia/Shanghai" +
            "&identity=audience&need_persist_msg_count=15&room_id=" + roomId + "&heartbeatDuration=0";

        // 签名是握手被 200 拒掉的根因：缺它 webcast 服务端不升级 WebSocket
        if (signature != null)
        {
            url += "&signature=" + Uri.EscapeDataString(signature);
        }

        return url;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
