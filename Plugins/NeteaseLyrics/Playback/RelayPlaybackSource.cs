using System.IO;
using System.Text.Json;

namespace MinecraftChatOverlay.Plugins.NeteaseLyrics.Playback;

/// <summary>
/// 播放状态的来源：读 BetterNCM 中继插件（MCOBridge）写出的 JSON 文件。
///
/// 为什么不用 SMTC：网易云客户端的原生 SMTC 不汇报时间轴（歌名恒为 Loading、进度全零），
/// 而 InfLink 的"前端"模式只是页面内的 Media Session，CEF 不会转发给系统。
/// 中继插件直接读客户端 DOM 里的进度滑块，数据最全也最稳。
/// </summary>
public sealed class RelayPlaybackSource
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>中继文件的位置（默认按 BetterNCM 的数据目录）。</summary>
    public string StatePath { get; set; } = DefaultPath;

    /// <summary>数据超过这个时间没更新就算过期。</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>超过这个时间没更新就当作"还在放"（继续外推位置）。</summary>
    private static readonly TimeSpan StalePlayingThreshold = TimeSpan.FromSeconds(10);

    public static string DefaultPath { get; } = Path.Combine("C:\\betterncm", "MCOBridge", "state.json");

    /// <summary>备选位置（用户把 BetterNCM 数据目录挪过地方时兜底）。</summary>
    public static IEnumerable<string> CandidatePaths
    {
        get
        {
            yield return DefaultPath;
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "betterncm", "MCOBridge", "state.json");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "betterncm", "MCOBridge", "state.json");
        }
    }

    /// <summary>读一次。文件不存在 / 读坏了 / 数据过期都返回 null（调用方显示"未在播放"）。</summary>
    public PlaybackState? Read()
    {
        // 中继每秒写一次，可能正好读到写了一半的内容 —— 多试几次，能读到一次完整的就用
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var state = ReadOnce();
            if (state is not null)
            {
                return state;
            }
        }

        return null;
    }

    private PlaybackState? ReadOnce()
    {
        foreach (var path in EnumeratePaths())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                var dto = JsonSerializer.Deserialize<RelayDto>(json, Options);
                if (dto is null)
                {
                    continue;
                }

                var stamp = Normalize(dto.UpdatedAt);
                var state = new PlaybackState
                {
                    Position = dto.Position ?? 0,
                    Duration = dto.Duration ?? 0,
                    Playing = dto.Playing,
                    UpdatedAt = stamp,
                    Title = dto.Title ?? "",
                    Source = dto.Source ?? "",
                };

                // 数据旧了不再直接丢弃：只要不是特别旧，就继续沿用（并按播放中处理，让位置继续外推）。
                // 真正在放歌时网易云挂后台就是这样 —— 用户切回窗口后又恢复，不能在这里断掉。
                if (state.IsStale(MaxAge))
                {
                    return null;
                }

                return state with { Playing = state.Playing || state.IsStale(StalePlayingThreshold) };
            }
            catch
            {
                // 读到写了一半的内容 / 格式变了：这一轮当没数据
            }
        }

        return null;
    }

    /// <summary>
    /// 中继每秒写文件，读取时可能读到"多一位/少一位"的撕裂时间戳。
    /// 明显不合理就当作"刚刚"—— 不能因为一个时间戳就把整帧播放数据丢掉。
    /// </summary>
    private static DateTime Normalize(long milliseconds)
    {
        var now = DateTimeOffset.Now;
        var nowMs = now.ToUnixTimeMilliseconds();

        // 差一天以上就认为写坏了
        if (milliseconds <= 0 || milliseconds < nowMs - 86_400_000L || milliseconds > nowMs + 3_600_000L)
        {
            // 常见情况：多了一位（×10），那就试着缩回去
            var guess = milliseconds / 10;
            if (guess >= nowMs - 86_400_000L && guess <= nowMs + 3_600_000L)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(guess).LocalDateTime;
            }

            return now.LocalDateTime;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime;
    }

    private IEnumerable<string> EnumeratePaths()
    {
        if (!string.IsNullOrWhiteSpace(StatePath))
        {
            yield return StatePath;
        }

        foreach (var path in CandidatePaths)
        {
            if (!string.Equals(path, StatePath, StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private sealed class RelayDto
    {
        public double? Position { get; set; }
        public double? Duration { get; set; }
        public bool Playing { get; set; }
        public long UpdatedAt { get; set; }
        public string? Title { get; set; }
        public string? Source { get; set; }
    }
}
