using System.Windows.Threading;

namespace MinecraftChatOverlay.Plugins.NetEaseLyric;

/// <summary>一帧要画出来的东西。</summary>
/// <param name="Title">歌名。</param>
/// <param name="Artist">歌手。</param>
/// <param name="Current">当前句（空 = 前奏，还没到第一句）。</param>
/// <param name="Next">下一句（没有就空）。</param>
/// <param name="Translation">当前句的翻译（没开显示就是空）。</param>
/// <param name="HasLyric">这首歌有没有歌词。</param>
/// <param name="Playing">是否在播放。</param>
/// <param name="Loading">歌词还在路上（刚切歌那一下）。</param>
/// <param name="Message">没有可显示内容时给用户看的说明。</param>
public sealed record LyricFrame(
    string Title,
    string Artist,
    string Current,
    string Next,
    string Translation,
    bool HasLyric,
    bool Playing,
    bool Loading,
    string Message)
{
    public static readonly LyricFrame Empty = new("", "", "", "", "", false, false, false, "");
}

/// <summary>
/// 歌词引擎：把"当前在放什么、放到哪了"和"每句歌词的时间轴"拼起来，算出该显示哪句。
///
/// 位置来源有两路，优先用桥接：
///   1) **本地桥接**（推荐）：网易云里的 InfLink 插件知道精确进度，经 BetterNCM 桥接插件
///      POST 到本机端口（见 <see cref="BridgeReceiver"/>）。它给的是毫秒级 currentTime，
///      两次上报之间我们用本地时钟补上（2 秒一次上报也不会卡顿）。
///   2) **SMTC**（兜底）：只能拿到歌名/歌手/播放状态。因为实测网易云不通过 SMTC 报进度，
///      位置只能用自走时钟估计：以切歌时刻为 0 点，本地计时往前走。
///
/// 节拍分两层：
///   - 快节拍（默认 120ms）：算当前句 → 界面；
///   - 慢节拍（1 秒）：确认歌曲身份（桥接/ SMTC），换歌了才去联网取歌词。
/// </summary>
public sealed class LyricEngine : IDisposable
{
    private readonly SmtcReader _smtc;
    private readonly NeteaseLyricClient _client;
    private readonly Func<NetEaseLyricSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly BridgeReceiver? _bridge;

    private DispatcherTimer? _tickTimer;
    private DispatcherTimer? _trackTimer;

    private List<LyricLine> _lines = new();
    private string _currentKey = "";
    private string _cachedTitle = "";
    private string _cachedArtist = "";
    private long _cachedSongId;
    private bool _ready;
    private bool _lyricLoading;
    private DateTime _lastFetchAttempt = DateTime.MinValue;
    private LyricFrame _lastFrame = LyricFrame.Empty;
    private string _lastError = "";

    // ---------- SMTC 自走时钟（没有桥接时用） ----------
    private TimeSpan _clockBase;
    private DateTime _clockBaseAt;
    private TimeSpan _clockLastRaw = TimeSpan.MinValue;
    private bool _clockWasPlaying;
    private TimeSpan _position;

    // ---------- 桥接时钟（两次上报之间补时间） ----------
    private long _bridgeBaseMs;
    private DateTime _bridgeBaseAt = DateTime.MinValue;

    public LyricEngine(
        SmtcReader smtc,
        NeteaseLyricClient client,
        BridgeReceiver? bridge,
        Func<NetEaseLyricSettings> settings,
        Dispatcher dispatcher,
        Action<string> log)
    {
        _smtc = smtc;
        _client = client;
        _bridge = bridge;
        _settings = settings;
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <summary>每算出一帧就触发一次（在 UI 线程上）。</summary>
    public event Action<LyricFrame>? Updated;

    /// <summary>每认出一首歌就触发一次（换歌了）——插件用它把歌词换成按 id 直接取。</summary>
    public event Action<string, string, long>? TrackChanged;

    /// <summary>SMTC 是否连上了（插件页状态行用）。</summary>
    public bool SmtcReady => _ready;

    public async Task StartAsync()
    {
        _ready = await _smtc.ConnectAsync().ConfigureAwait(true);
        if (!_ready)
        {
            _log("连不上系统媒体会话（SMTC）：网易云设置里可以打开「系统媒体控制」，或者靠本地桥接也行");
        }

        var interval = Math.Clamp(_settings().RefreshIntervalMs, 40, 1000);
        _tickTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(interval) };
        _tickTimer.Tick += (_, _) => TickFast();

        _trackTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _trackTimer.Tick += async (_, _) => await PollTrackAsync().ConfigureAwait(true);

        _tickTimer.Start();
        _trackTimer.Start();

        await PollTrackAsync().ConfigureAwait(true);
    }

    public void Stop()
    {
        _tickTimer?.Stop();
        _trackTimer?.Stop();
        _tickTimer = null;
        _trackTimer = null;
        _lines = new List<LyricLine>();
        _currentKey = "";
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------ 快节拍：算当前句

    private void TickFast()
    {
        try
        {
            var settings = _settings();

            TimeSpan position;
            bool playing;

            if (TryReadBridge(settings) is { } bridge)
            {
                position = bridge.Position;
                playing = bridge.Playing;
            }
            else if (_ready)
            {
                var state = _smtc.ReadTransport();
                _lastError = state.Error;
                if (state.Error.Length > 0)
                {
                    return;
                }

                position = EstimatePosition(state);
                playing = state.Playing;
            }
            else
            {
                return;
            }

            position += TimeSpan.FromMilliseconds(settings.LyricOffsetMs);

            var frame = BuildFrame(position, playing, settings);
            if (!frame.Equals(_lastFrame))
            {
                Push(frame);
            }
        }
        catch (Exception ex)
        {
            _log("刷新歌词出错：" + ex.Message);
        }
    }

    /// <summary>
    /// 桥接数据（如果够新鲜就用）。位置在两次上报之间按本地时钟往前补，
    /// 所以即使桥接 2 秒才来一帧，歌词也是平滑走的。
    /// </summary>
    private (TimeSpan Position, bool Playing)? TryReadBridge(NetEaseLyricSettings settings)
    {
        if (_bridge is null || settings.DataSource == DataSourceKind.SmtcOnly)
        {
            return null;
        }

        var payload = _bridge.Latest;
        if (payload is null || _bridge.SecondsSinceLast > 6)
        {
            return null;
        }

        if (settings.DataSource == DataSourceKind.BridgeOnly && _bridge.SecondsSinceLast > 6)
        {
            return null;
        }

        var receivedUtc = payload.ReceivedAt.ToUniversalTime();
        if (_bridgeBaseAt != receivedUtc)
        {
            // 新的一帧：重新对表
            _bridgeBaseAt = receivedUtc;
            _bridgeBaseMs = payload.PositionMs;
        }

        var elapsed = DateTime.UtcNow - _bridgeBaseAt;
        var positionMs = _bridgeBaseMs + (payload.Playing ? (long)Math.Max(0, elapsed.TotalMilliseconds) : 0);

        if (payload.DurationMs > 0 && positionMs > payload.DurationMs)
        {
            positionMs = payload.DurationMs;
        }

        return (TimeSpan.FromMilliseconds(Math.Max(0, positionMs)), payload.Playing);
    }

    /// <summary>
    /// 位置推算（只用 SMTC 时的兜底）：
    ///   1）**播放器给了可信时间轴**（上报时间几秒内）→ 位置 = 上报位置 + 已经过去的时间 × 速率；
    ///   2）**上报时间空/旧** → 自走时钟（用最后一次可信读数当基准）；
    ///   3）暂停 → 冻结；恢复 → 从冻结位置接着走，不补暂停那段。
    /// 另外播放中忽略"小幅回退"（落后 2 秒内的新读数当迟到旧读数丢掉），免得歌词往回跳。
    /// </summary>
    private TimeSpan EstimatePosition(TransportState state)
    {
        var now = DateTime.UtcNow;
        var reportAge = state.LastUpdated == default
            ? TimeSpan.MaxValue
            : DateTimeOffset.Now - state.LastUpdated;

        var hasTimeline = reportAge != TimeSpan.MaxValue &&
                          reportAge > TimeSpan.FromSeconds(-1) &&
                          reportAge < TimeSpan.FromSeconds(8);

        if (hasTimeline)
        {
            var rate = state.Rate > 0 ? state.Rate : 1.0;
            var extrapolated = state.RawPosition + TimeSpan.FromTicks((long)(reportAge.Ticks * rate));
            var backward = state.Playing && extrapolated < _position - TimeSpan.FromSeconds(2);
            _position = backward ? _position : extrapolated;

            _clockBase = state.RawPosition;
            _clockBaseAt = now - reportAge;
            _clockLastRaw = state.RawPosition;
            _clockWasPlaying = state.Playing;
            return Clamp(state, _position);
        }

        var fresh = _clockLastRaw == TimeSpan.MinValue ||
                    Math.Abs((state.RawPosition - _clockLastRaw).TotalSeconds) > 0.8;

        if (fresh)
        {
            _clockBase = state.RawPosition;
            _clockBaseAt = now;
            _clockLastRaw = state.RawPosition;
            _position = state.RawPosition;
        }
        else if (state.Playing)
        {
            if (!_clockWasPlaying)
            {
                _clockBase = _position;
                _clockBaseAt = now;
            }
            else
            {
                var rate = state.Rate > 0 ? state.Rate : 1.0;
                _position = _clockBase + TimeSpan.FromTicks((long)((now - _clockBaseAt).Ticks * rate));
            }
        }

        _clockWasPlaying = state.Playing;
        return Clamp(state, _position);
    }

    private static TimeSpan Clamp(TransportState state, TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (state.Duration > TimeSpan.Zero && position > state.Duration)
        {
            return state.Duration;
        }

        return position;
    }

    /// <summary>切歌时把两个时钟都清掉。</summary>
    private void ResetClock()
    {
        _clockLastRaw = TimeSpan.MinValue;
        _clockWasPlaying = false;
        _clockBase = TimeSpan.Zero;
        _position = TimeSpan.Zero;
        _bridgeBaseMs = 0;
        _bridgeBaseAt = DateTime.MinValue;
    }

    /// <summary>给插件页诊断用的一行现状。</summary>
    public string DescribeTransport()
    {
        var settings = _settings();
        var bridge = _bridge;

        if (bridge is not null && settings.DataSource != DataSourceKind.SmtcOnly)
        {
            if (bridge.Latest is null)
            {
                var listen = bridge.IsListening ? $"在监听 {bridge.Port}" : $"没起来（{bridge.LastError}）";
                return $"桥接：{listen}，还没收到数据";
            }

            var age = bridge.SecondsSinceLast;
            var text = $"桥接：{bridge.ReceivedCount} 帧，最近 {age:F1} 秒前";
            if (age > 6)
            {
                text += "（太旧，已退回 SMTC）";
            }

            return text;
        }

        var lastUpdated = _clockLastRaw == TimeSpan.MinValue ? "（还没读到过）" : _clockLastRaw.ToString(@"mm\:ss\.ff");
        var error = _lastError.Length > 0 ? $"，读取报错 {_lastError}" : "";
        return $"SMTC：位置 {_position:mm\\:ss}｜原始 {lastUpdated}｜对表于 {(DateTime.UtcNow - _clockBaseAt).TotalSeconds:F0} 秒前{error}";
    }

    // ------------------------------------------------------------ 慢节拍：认歌 + 取歌词

    private async Task PollTrackAsync()
    {
        try
        {
            var settings = _settings();

            string title;
            string artist;
            long songId;

            var bridge = _bridge?.Latest;
            var bridgeFresh = bridge is not null && _bridge!.SecondsSinceLast <= 6 && settings.DataSource != DataSourceKind.SmtcOnly;

            if (bridgeFresh)
            {
                title = bridge!.Title;
                artist = bridge.Artist;
                songId = bridge.SongId;
            }
            else
            {
                if (!_ready)
                {
                    return;
                }

                // 桥接不可用时用 SMTC 认歌（注意：SMTC 在网易云这边不报进度，但歌名是准的）
                var snapshot = await _smtc.TryGetSnapshotAsync(settings.PreferredSource).ConfigureAwait(true);
                if (snapshot is null)
                {
                    if (_currentKey.Length > 0)
                    {
                        _currentKey = "";
                        _cachedTitle = "";
                        _cachedArtist = "";
                        _cachedSongId = 0;
                        _lines = new List<LyricLine>();
                        ResetClock();
                        Push(CreateMessageFrame("等待网易云播放…"));
                    }

                    return;
                }

                title = snapshot.Title;
                artist = snapshot.Artist;
                songId = 0;
            }

            var key = songId > 0 ? "id:" + songId : title + "\u0001" + artist;
            if (key == _currentKey)
            {
                RetryIfStale();
                return;
            }

            _currentKey = key;
            _cachedTitle = title;
            _cachedArtist = artist;
            _cachedSongId = songId;
            _lines = new List<LyricLine>();
            _lyricLoading = true;
            _lastFetchAttempt = DateTime.UtcNow;
            ResetClock();
            Push(CreateMessageFrame("正在找歌词…"));
            _log($"换歌：{title} — {artist}" + (songId > 0 ? $"（id {songId}）" : ""));
            TrackChanged?.Invoke(title, artist, songId);

            // 桥接给了网易云自己的 id，就直接按 id 取歌词（比按歌名搜更准）
            var document = songId > 0
                ? await _client.GetBySongIdAsync(songId, title, artist, CancellationToken.None).ConfigureAwait(true)
                : await _client.GetAsync(title, artist, CancellationToken.None).ConfigureAwait(true);

            if (key != _currentKey)
            {
                return; // 取的过程中又切歌了
            }

            _lyricLoading = false;

            if (document is null)
            {
                Push(CreateMessageFrame("歌词没取到（网络或搜不到），稍后会自动重试"));
                return;
            }

            if (!document.Ok)
            {
                Push(CreateMessageFrame("这首歌没有歌词"));
                return;
            }

            _lines = LrcParser.Parse(document.Lrc, document.TranslationLrc);
            _log($"歌词已载入：{title}（{_lines.Count} 句）");
            TickFast();
        }
        catch (Exception ex)
        {
            _lyricLoading = false;
            _log("读取播放状态出错：" + ex.Message);
        }
    }

    private LyricFrame BuildFrame(TimeSpan position, bool playing, NetEaseLyricSettings settings)
    {
        if (_lines.Count == 0)
        {
            var message = _lyricLoading
                ? "正在找歌词…"
                : (_cachedTitle.Length > 0 ? settings.NoLyricText : "等待网易云播放…");
            return new LyricFrame(_cachedTitle, _cachedArtist, "", "", "", false, playing, _lyricLoading, message);
        }

        var index = LrcParser.FindIndex(_lines, position);
        if (index < 0)
        {
            return new LyricFrame(_cachedTitle, _cachedArtist, "", _lines[0].Text, "", true, playing, false, "");
        }

        var current = _lines[index];
        var next = index + 1 < _lines.Count ? _lines[index + 1].Text : "";
        var translation = settings.ShowTranslation ? current.Translation : "";

        return new LyricFrame(_cachedTitle, _cachedArtist, current.Text, next, translation, true, playing, false, "");
    }

    private LyricFrame CreateMessageFrame(string message) =>
        new(_cachedTitle, _cachedArtist, "", "", "", false, _lastFrame.Playing, false, message);

    /// <summary>手动要求重来一次（插件页上的「重新载入歌词」）。</summary>
    public void ForceReload()
    {
        _currentKey = "";
    }

    /// <summary>取歌词失败/没歌词时，隔 30 秒再试一次。</summary>
    public void RetryIfStale()
    {
        if (_cachedTitle.Length == 0 || _lines.Count > 0 || _lyricLoading)
        {
            return;
        }

        if (DateTime.UtcNow - _lastFetchAttempt > TimeSpan.FromSeconds(30))
        {
            _lastFetchAttempt = DateTime.UtcNow;
            _currentKey = "";
        }
    }

    private void Push(LyricFrame frame)
    {
        _lastFrame = frame;
        Updated?.Invoke(frame);
    }
}
