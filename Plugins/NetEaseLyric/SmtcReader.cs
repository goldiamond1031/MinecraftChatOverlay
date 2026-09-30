using Windows.Media.Control;

namespace MinecraftChatOverlay.Plugins.NetEaseLyric;

/// <summary>一次"当前在放什么"的快照。</summary>
/// <param name="Title">歌名。</param>
/// <param name="Artist">歌手。</param>
/// <param name="Album">专辑（可能为空）。</param>
/// <param name="Position">当前播放位置（已按时间外推，比 SMTC 原始值跟手）。</param>
/// <param name="Duration">总时长（读不到就是 <see cref="TimeSpan.Zero"/>）。</param>
/// <param name="Playing">是否正在播放。</param>
/// <param name="Source">来源标识（比如 cloudmusic.exe），用来确认是不是网易云。</param>
public sealed record TrackSnapshot(
    string Title,
    string Artist,
    string Album,
    TimeSpan Position,
    TimeSpan Duration,
    bool Playing,
    string Source)
{
    /// <summary>用来判断"换歌了"的键。</summary>
    public string Key => $"{Title}\u0001{Artist}";
}

/// <summary>
/// 搬运层读到的原始状态。<see cref="Error"/> 非空说明某次读取失败了，
/// 插件页的诊断会把它原样显示出来（免得"读不到"变成猜谜）。
/// </summary>
/// <param name="RawPosition">SMTC 报的位置，**没有外推**（可能是很久以前上报的）。</param>
/// <param name="Duration">总时长。</param>
/// <param name="Playing">播放状态是不是 Playing。</param>
/// <param name="Rate">播放速率（一般 1.0）。</param>
/// <param name="LastUpdated">SMTC 说这个位置是什么时候上报的（可能为 default，即"没提供"）。</param>
/// <param name="Error">读取失败的原因（没有就是空）。</param>
public sealed record TransportState(
    TimeSpan RawPosition,
    TimeSpan Duration,
    bool Playing,
    double Rate,
    DateTimeOffset LastUpdated,
    string Error)
{
    public static readonly TransportState Failed = new(
        TimeSpan.Zero, TimeSpan.Zero, false, 1.0, default, "还没建立会话");
}

/// <summary>
/// 系统媒体会话（SMTC）读取器。
///
/// 为什么要用 SMTC：这是 Windows 给"当前在放什么"提供的官方通道，网易云在设置里
/// 打开「系统媒体控制」后就会把 曲名/歌手/时长/播放位置/播放状态 报给系统。
///
/// 这里**只负责把原始值搬出来**，不做任何时间推算 —— 推算交给 LyricEngine 的自走时钟，
/// 因为"网易云到底多久上报一次位置"这件事各版本不一样，得假设它可能一直不更新。
/// </summary>
public sealed class SmtcReader
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>第一次成功拿到管理器时置位。</summary>
    public bool Ready => _manager is not null;

    /// <summary>上一次读到的来源标识，日志/页面上给用户看。</summary>
    public string LastSource { get; private set; } = "";

    /// <summary>
    /// 建立连接。失败（老系统没有 SMTC、或系统组件异常）返回 false，插件随后显示提示即可。
    /// </summary>
    public async Task<bool> ConnectAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _session = null;
            return _manager is not null;
        }
        catch
        {
            _manager = null;
            return false;
        }
    }

    /// <summary>当前所有媒体会话的来源标识（给插件页做"选哪个播放器"的下拉框）。</summary>
    public IReadOnlyList<string> ListSources()
    {
        try
        {
            return _manager?.GetSessions()
                .Select(s => s.SourceAppUserModelId ?? "")
                .Where(s => s.Length > 0)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 取一次快照。<paramref name="preferredSource"/> 是用户选的来源（空 = 自动挑网易云）。
    /// 没在放东西 / 读不到就返回 null（不算错误）。
    /// </summary>
    public async Task<TrackSnapshot?> TryGetSnapshotAsync(string preferredSource)
    {
        try
        {
            if (_manager is null)
            {
                return null;
            }

            var session = await ResolveSessionAsync(preferredSource);
            if (session is null)
            {
                return null;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            var title = (props?.Title ?? "").Trim();
            var artist = (props?.Artist ?? "").Trim();
            if (title.Length == 0 && artist.Length == 0)
            {
                return null;
            }

            var source = session.SourceAppUserModelId ?? "";
            LastSource = source;

            return new TrackSnapshot(
                title,
                artist,
                (props?.AlbumTitle ?? "").Trim(),
                timeline.Position < TimeSpan.Zero ? TimeSpan.Zero : timeline.Position,
                timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero,
                playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                source);
        }
        catch
        {
            // 会话中途没了很正常（关播放器、切设备），下一轮重来
            _session = null;
            return null;
        }
    }

    /// <summary>
    /// 只读"放到哪了/在不在放"，全是同步调用，很便宜 —— 快节拍（每秒好几次）用它。
    /// 返回的是**原始值，不外推**；会话没了或调用失败会把原因写进 <see cref="TransportState.Error"/>，
    /// 不抛异常（读不到不该让插件崩）。
    /// </summary>
    public TransportState ReadTransport()
    {
        var session = _session;
        if (session is null)
        {
            return TransportState.Failed;
        }

        try
        {
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            var duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;

            var position = timeline.Position;
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            return new TransportState(
                position,
                duration,
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                playback.PlaybackRate is > 0 ? playback.PlaybackRate.Value : 1.0,
                timeline.LastUpdatedTime,
                "");
        }
        catch (Exception ex)
        {
            // 会话中途没了很正常（关播放器、切设备）：清掉缓存，等慢节拍重新建立
            _session = null;
            return new TransportState(TimeSpan.Zero, TimeSpan.Zero, false, 1.0, default, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 挑一个会话：用户指定了就用它，否则优先网易云，再退到"系统当前会话"。
    /// </summary>
    private async Task<GlobalSystemMediaTransportControlsSession?> ResolveSessionAsync(string preferredSource)
    {
        var manager = _manager!;
        var sessions = manager.GetSessions();
        if (sessions.Count == 0)
        {
            _session = null;
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredSource))
        {
            var picked = sessions.FirstOrDefault(s =>
                string.Equals(s.SourceAppUserModelId, preferredSource, StringComparison.OrdinalIgnoreCase));
            if (picked is not null)
            {
                _session = picked;
                return picked;
            }
        }
        else
        {
            // 网易云 PC 版的来源标识一般是 cloudmusic.exe；不同版本可能带包名，所以用包含判断
            var netease = sessions.FirstOrDefault(s =>
                (s.SourceAppUserModelId ?? "").Contains("cloudmusic", StringComparison.OrdinalIgnoreCase));
            if (netease is not null)
            {
                _session = netease;
                return netease;
            }
        }

        // 退路：缓存住的会话还活着就用它，否则用系统当前会话
        if (_session is not null && sessions.Any(s => s.SourceAppUserModelId == _session.SourceAppUserModelId))
        {
            return _session;
        }

        try
        {
            _session = manager.GetCurrentSession();
        }
        catch
        {
            _session = null;
        }

        await Task.CompletedTask;
        return _session;
    }
}
