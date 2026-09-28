using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// 消息提示音：**有人说话**就播一段提示音。
///
/// 「谁算在说话」这件事不在界面上让用户写规则，而是写死在程序里 ——
/// 判据在 <see cref="ChatTextProcessor.LooksLikePlayerSpeech"/>，那是唯一要调的地方。
///
/// 三个刻意的地方：
///   1. <b>全局最短间隔</b>防刷屏。连着来一堆发言时，没有这个会变成机关枪。
///   2. 声音文件找不到 / 解不开时**退回系统提示音，并把原因写进状态行** —— 不静默失败。
///   3. <b>连杀不掐断</b>。见 <see cref="NotifyKillSound"/> 的注释。
///
/// MediaPlayer 是 DispatcherObject，只能在创建它的线程（UI 线程）上用，
/// 所以所有播放入口都会先把自己弹回 UI 线程。
/// </summary>
public sealed class ChatSoundNotifier : IDisposable
{
    /// <summary>MB_ICONASTERISK：系统「信息提示」音，用来兜底。</summary>
    private const uint MbIconAsterisk = 0x00000040;

    /// <summary>缓存上限。超过就整体清掉重建，避免用户反复换文件时越积越多。</summary>
    private const int MaxCachedPlayers = 16;

    /// <summary>
    /// 「同一时刻最多容纳几路叠加」。连杀通常也就 2~3 杀，留 6 路足够。
    /// 不设上限的话，极端刷屏场面会同时开一堆解码器，内存和混音开销都不划算。
    /// 超了就从最早开始抢。
    /// </summary>
    private const int MaxConcurrentVoices = 6;

    private readonly Dictionary<string, MediaPlayer> _players =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 正在播放中的那一路，按开始时间排。用于"别掐断"的叠加播放 ——
    /// 一个 MediaPlayer 同时只能放一段，要叠加就得另开一个实例，
    /// 放完再收回来。这里记的就是这些"正在响"的实例。
    ///
    /// 另一本账记每个实例当前挂着哪个文件：<see cref="MediaPlayer"/> 没有 Tag 之类的
    /// 自定义槽位，只能自己在外面记。用来判断"这个空闲实例是不是已经指向目标文件"，
    /// 是的话就不用重新 Open（重新 Open 会重建解码器，白花时间）。
    /// </summary>
    private readonly List<MediaPlayer> _voices = new();

    private readonly Dictionary<MediaPlayer, string> _voiceFiles = new();

    private long _lastPlayedAt = long.MinValue;

    /// <summary>是否开启。</summary>
    public bool Enabled { get; set; }

    /// <summary>两条提示音之间至少间隔多少毫秒（防刷屏）。</summary>
    public int MinIntervalMs { get; set; } = 800;

    /// <summary>当前选中的声音文件。</summary>
    public string SoundFile { get; set; } = "";

    /// <summary>累计看过多少条聊天（用来判断"到底有没有收到消息"）。</summary>
    public int SeenCount { get; private set; }

    /// <summary>其中有多少条被判定成"玩家在说话"（用来判断判据准不准）。</summary>
    public int SpeechCount { get; private set; }

    /// <summary>累计真的响了几次（用来判断声音通路通不通）。</summary>
    public int PlayCount { get; private set; }

    /// <summary>
    /// 最近一次出问题（找不到文件 / 打不开 / 播放失败）的说明。
    /// 正常播放不会写这里 —— 免得状态行一直被刷。
    /// </summary>
    public string LastWarning { get; private set; } = "";

    /// <summary>
    /// 最近一次**请求播放**的文件（不管最后放成没放成）。
    ///
    /// 和 <see cref="SoundFile"/> 的区别：那个是"配置里当前挂着哪个"（会被别处改），
    /// 这个是"实际上最后决定放哪个"的快照。排查"为什么响的是这个音"时看它。
    /// </summary>
    public string LastRequestedFile { get; private set; } = "";

    /// <summary>
    /// 聊天线收到一条消息时调用。看着像玩家在说话就响一声。
    /// </summary>
    /// <param name="text">已经过替换规则的文本（也就是用户实际看到的那一份）。</param>
    /// <returns>真的响了就返回 true。</returns>
    public bool NotifyPlayerSpoke(string text)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        SeenCount++;

        if (!ChatTextProcessor.LooksLikePlayerSpeech(text))
        {
            return false;
        }

        SpeechCount++;

        // 判据 + 冷却判定 + 播放整段都放在 UI 线程上：
        // MediaPlayer 必须和创建它的线程一致；冷却计时放同一个线程才不会同时放行两条。
        var played = false;
        RunOnUiThread(() =>
        {
            var now = Environment.TickCount64;
            var interval = Math.Clamp(MinIntervalMs, 0, 30000);
            if (_lastPlayedAt != long.MinValue && now - _lastPlayedAt < interval)
            {
                return;
            }

            _lastPlayedAt = now;
            Play(SoundFile);
            played = true;
        });

        return played;
    }

    /// <summary>
    /// 击杀提示音：调用方已经确认是击杀，不再跑「像不像玩家说话」的判据。
    /// 也不做防刷屏间隔 —— 双杀/连杀该响几次就响几次。
    ///
    /// <b>和消息提示音的关键区别：连杀不掐断上一声。</b>
    /// 消息提示音那边用的是"掐掉重放"（一个实例复用一个 player），因为刷屏场景下
    /// 叠成一片反而吵；但击杀是稀疏事件，一声没放完又来一声是正常的连杀，
    /// 强行掐断会把前一声切断成"半声"，听起来像卡带。所以这里另开一路实例叠着放。
    /// </summary>
    /// <param name="soundFile">
    /// 要放哪个文件。随机抽取由调用方决定 —— 播放器只管放，不管选哪个，
    /// 这样"抽签"逻辑和"播放"逻辑各归各的，改一个不用动另一个。
    /// 传空则退回 <see cref="SoundFile"/>。
    /// </param>
    public bool NotifyKillSound(string? soundFile = null)
    {
        if (!Enabled)
        {
            return false;
        }

        var target = string.IsNullOrWhiteSpace(soundFile) ? SoundFile : soundFile;

        var played = false;
        RunOnUiThread(() =>
        {
            PlayOverlapping(target);
            played = true;
        });

        return played;
    }

    /// <summary>【试听】按钮用：不看开关、不算最短间隔，直接放。</summary>
    public void Preview(string soundFile)
    {
        RunOnUiThread(() => Play(soundFile));
    }

    /// <summary>
    /// 叠加播放：不复用主播放器，而是从池子里取一个空闲的实例放。
    /// 池子见 <see cref="AcquireVoice"/>。
    /// </summary>
    private void PlayOverlapping(string soundFile)
    {
        LastRequestedFile = soundFile ?? "";

        var path = string.IsNullOrWhiteSpace(soundFile) ? null : ResolveSoundPath(soundFile);

        if (path == null)
        {
            // 没选文件 / 找不到：退回系统提示音。注意这里不复用 Play()，
            // 因为 Play() 会去动主播放器，可能把正在响的那一路掐掉。
            MessageBeep(MbIconAsterisk);
            PlayCount++;
            LastWarning = string.IsNullOrWhiteSpace(soundFile)
                ? "没选提示音，用了系统提示音"
                : "找不到声音文件，已改用系统提示音：" + soundFile;
            return;
        }

        try
        {
            var voice = AcquireVoice(path);
            voice.Volume = 0.85;

            // 从头放，不动别人。放完自己不回收 —— 回收交给下一路来抢，
            // 免得在 MediaEnded 回调里做集合增删（那是另一条线程，容易出竞态）。
            voice.Position = TimeSpan.Zero;
            voice.Play();

            PlayCount++;
            LastWarning = "";
        }
        catch (Exception ex)
        {
            MessageBeep(MbIconAsterisk);
            LastWarning = $"播放失败（{ex.Message}），已改用系统提示音";
        }
    }

    /// <summary>
    /// 从叠加池里取一个实例。
    ///
    /// 优先找"没在播"的复用；都在播就再开一个，开到 <see cref="MaxConcurrentVoices"/>
    /// 为止，满了就把最早开的那一路抢过来（团战里 7 连杀这种极端场面，
    /// 听感上抢掉最早一声比直接静音更自然）。
    /// </summary>
    private MediaPlayer AcquireVoice(string absolutePath)
    {
        foreach (var candidate in _voices)
        {
            if (!IsPlaying(candidate))
            {
                // 空着的实例如果指向别的文件，得换源。
                // 已经是指向目标文件的就直接用 —— 省掉一次 Open。
                if (!_voiceFiles.TryGetValue(candidate, out var current) ||
                    !string.Equals(current, absolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    candidate.Open(new Uri(absolutePath, UriKind.Absolute));
                    _voiceFiles[candidate] = absolutePath;
                }

                return candidate;
            }
        }

        if (_voices.Count >= MaxConcurrentVoices)
        {
            var oldest = _voices[0];
            _voices.RemoveAt(0);
            oldest.Open(new Uri(absolutePath, UriKind.Absolute));
            _voiceFiles[oldest] = absolutePath;
            _voices.Add(oldest);
            return oldest;
        }

        var created = new MediaPlayer();
        created.MediaFailed += (_, e) =>
        {
            LastWarning = $"声音打不开（{Path.GetFileName(absolutePath)}）：{e.ErrorException?.Message}";
        };
        created.Open(new Uri(absolutePath, UriKind.Absolute));
        _voiceFiles[created] = absolutePath;
        _voices.Add(created);
        return created;
    }

    private static bool IsPlaying(MediaPlayer player)
    {
        try
        {
            return player.NaturalDuration.HasTimeSpan
                   && player.Position < player.NaturalDuration.TimeSpan
                   && player.Position > TimeSpan.Zero;
        }
        catch
        {
            // 换源的一瞬间这些属性会抛，当成"没在播"处理
            return false;
        }
    }

    /// <summary>
    /// 提前把声音文件打开一次。MediaPlayer 第一次 Open 要读文件 + 建解码器，
    /// 先预热过，真正触发时才是"立刻响"。
    /// </summary>
    public void Prewarm(IEnumerable<string> soundFiles)
    {
        RunOnUiThread(() =>
        {
            foreach (var file in soundFiles)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                var path = ResolveSoundPath(file);
                if (path != null)
                {
                    GetOrCreatePlayer(path);
                }
            }
        });
    }

    // ===================== 内部 =====================

    private void Play(string soundFile)
    {
        LastRequestedFile = soundFile ?? "";

        var path = string.IsNullOrWhiteSpace(soundFile) ? null : ResolveSoundPath(soundFile);

        if (path == null)
        {
            // 没选文件（或选了但找不到）：退回系统提示音，至少"响"了；
            // 路径失效的情况要写清楚，不然用户只会觉得"没声"。
            MessageBeep(MbIconAsterisk);
            PlayCount++;
            LastWarning = string.IsNullOrWhiteSpace(soundFile)
                ? "没选提示音，用了系统提示音"
                : "找不到声音文件，已改用系统提示音：" + soundFile;
            return;
        }

        try
        {
            var player = GetOrCreatePlayer(path);
            player.Volume = 0.85;

            // MediaPlayer 一个实例同时只能放一个。先掐掉再从头放，
            // 这样短音频繁触发时听到的是"重新响一下"，而不是叠成一片。
            player.Stop();
            player.Position = TimeSpan.Zero;
            player.Play();

            PlayCount++;
            LastWarning = "";
        }
        catch (Exception ex)
        {
            MessageBeep(MbIconAsterisk);
            LastWarning = $"播放失败（{ex.Message}），已改用系统提示音";
        }
    }

    private MediaPlayer GetOrCreatePlayer(string absolutePath)
    {
        if (_players.TryGetValue(absolutePath, out var cached))
        {
            return cached;
        }

        if (_players.Count >= MaxCachedPlayers)
        {
            foreach (var player in _players.Values)
            {
                try
                {
                    player.Close();
                }
                catch
                {
                    // 关不掉就算了，下面重建
                }
            }

            _players.Clear();
        }

        var created = new MediaPlayer();
        created.MediaFailed += (_, e) =>
        {
            // 没装解码器（比如某些 ogg）会走到这里。写进状态行，用户才知道是文件的问题。
            LastWarning = $"声音打不开（{Path.GetFileName(absolutePath)}）：{e.ErrorException?.Message}";
        };

        created.Open(new Uri(absolutePath, UriKind.Absolute));
        _players[absolutePath] = created;
        return created;
    }

    /// <summary>
    /// 把相对路径解析成存在的绝对路径：
    /// 先按原样，再按程序目录，再逐级往上找（开发时直接跑 bin\Debug 也能找到 res\ 里的文件）。
    /// </summary>
    public static string? ResolveSoundPath(string soundFile)
    {
        try
        {
            if (Path.IsPathRooted(soundFile))
            {
                return File.Exists(soundFile) ? soundFile : null;
            }

            var inBase = Path.Combine(AppContext.BaseDirectory, soundFile);
            if (File.Exists(inBase))
            {
                return inBase;
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 6 && directory != null; i++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, soundFile);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>保证代码跑在 UI 线程上（MediaPlayer 的要求）。</summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        RunOnUiThread(() =>
        {
            foreach (var player in _players.Values)
            {
                try
                {
                    player.Close();
                }
                catch
                {
                    // 退出流程里不要抛
                }
            }

            _players.Clear();

            foreach (var voice in _voices)
            {
                try
                {
                    voice.Close();
                }
                catch
                {
                    // 同上
                }
            }

            _voices.Clear();
            _voiceFiles.Clear();
        });
    }

    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);
}
