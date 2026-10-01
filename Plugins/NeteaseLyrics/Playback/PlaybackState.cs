namespace MinecraftChatOverlay.Plugins.NeteaseLyrics.Playback;

/// <summary>
/// 当前播放状态（来自中继插件写出的 JSON）。
/// <see cref="PositionNow"/> 会按"上次更新时间 + 已经过去的时间"把进度外推，
/// 所以界面刷新可以比中继的写入频率更快，歌词滚动不会一跳一跳的。
/// </summary>
public sealed record PlaybackState
{
    /// <summary>进度（秒）。</summary>
    public double Position { get; init; }

    /// <summary>总时长（秒）。0 表示拿不到。</summary>
    public double Duration { get; init; }

    /// <summary>是否在播放（中继按"进度最近有没有动"判断）。</summary>
    public bool Playing { get; init; }

    /// <summary>中继写这份数据的时间（本地时间）。</summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>页面标题（中继顺带写的，只当兜底）。</summary>
    public string Title { get; init; } = "";

    /// <summary>进度是从哪儿来的：ncm（原生接口）/ media（播放器元素）/ slider（进度条）/ none。</summary>
    public string Source { get; init; } = "";

    /// <summary>按经过时间外推后的当前位置（秒）。</summary>
    public double PositionNow
    {
        get
        {
            var pos = Position;
            if (Playing)
            {
                var elapsed = (DateTime.Now - UpdatedAt).TotalSeconds;
                if (elapsed > 0)
                {
                    pos += elapsed;
                }
            }

            if (Duration > 0 && pos > Duration)
            {
                pos = Duration;
            }

            return Math.Max(0, pos);
        }
    }

    /// <summary>数据是不是太旧了（中继挂了 / 客户端关了）。</summary>
    public bool IsStale(TimeSpan maxAge) => DateTime.Now - UpdatedAt > maxAge;
}
