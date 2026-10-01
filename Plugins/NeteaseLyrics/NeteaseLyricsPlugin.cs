using System.IO;
using System.Windows;
using System.Windows.Threading;
using MinecraftChatOverlay.Plugin;
using MinecraftChatOverlay.Plugins.NeteaseLyrics.Lyrics;
using MinecraftChatOverlay.Plugins.NeteaseLyrics.Playback;

// WPF + WinForms 同时开着，Application / MessageBox 这些名字会二义，钉死成 WPF 那套
using Application = System.Windows.Application;

namespace MinecraftChatOverlay.Plugins.NeteaseLyrics;

/// <summary>
/// 「网易云歌词」插件。
///
/// 数据来自两处，都不需要注入网易云：
///   1) 歌名 / 歌手 —— 网易云客户端窗口标题（形如「歌名 - 歌手」）
///   2) 播放进度   —— BetterNCM 中继插件 MCOBridge 写出的 state.json（position / duration / playing）
/// 歌词本身从网易云公开接口按「歌名 + 歌手」精确匹配取回，带翻译，缓存在插件目录里。
/// </summary>
public sealed class NeteaseLyricsPlugin : IPlugin
{
    private IPluginHost _host = null!;
    private NeteaseLyricsSettings _settings = new();
    private NeteaseLyricsPage? _page;
    private LyricsWindow? _window;
    private RelayPlaybackSource _relay = new();
    private NeteaseLyricClient? _lyrics;
    private DispatcherTimer? _timer;
    private LyricDocument? _document;
    private string _songKey = "";
    private bool _fetching;
    private bool _lastReallyVisible;

    /// <summary>上一次"当前行有没有逐字数据"，只在状态翻转时写一行日志（方便用户自查）。</summary>
    private bool? _lastKaraokeOn;

    // 窗口标题（EnumWindows）不用每 200ms 读一次：一秒一次足够
    private DateTime _lastTitleRead = DateTime.MinValue;
    private string? _cachedTitle;
    private string? _cachedArtist;

    // 进度是从哪儿来的（ncm 原生 / media 元素 / 进度条滑块），变了才写日志
    private string? _lastSource;

    /// <summary>用户刚点过【隐藏窗口】：在被显式叫出来之前，每秒的自动显示不许再把窗口放出来。</summary>
    private bool _autoShowSuppressed;

    /// <summary>使用说明窗（同时只开一个）。</summary>
    private LyricsHelpWindow? _help;

    public string Id => "goldiamond.neteaselyrics";

    public string DisplayName => "网易云歌词";

    public IPluginHost Host => _host;

    public NeteaseLyricsSettings Settings => _settings;

    public LyricDocument? Document => _document;

    public PlaybackState? LastState { get; private set; }

    public string Status { get; private set; } = "还没开始";

    /// <summary>页面里是否展开"使用引导"卡片（首次装载自动展开，之后点"使用说明"展开）。</summary>
    public bool GuideVisible { get; set; }

    /// <summary>最近一次"安装中继"的结果，显示在引导卡片里。</summary>
    public string RelayInstallResult { get; private set; } = "";

    public bool IsWindowVisible => _window?.IsVisible == true;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = PluginSettingsFile.Load<NeteaseLyricsSettings>(host.PluginDirectory);
        _relay = new RelayPlaybackSource
        {
            StatePath = string.IsNullOrWhiteSpace(_settings.RelayPath) ? RelayPlaybackSource.DefaultPath : _settings.RelayPath!,
        };
        _lyrics = new NeteaseLyricClient(Path.Combine(host.PluginDirectory, "lyrics-cache"));

        host.RegisterPage(new PluginPage
        {
            Title = DisplayName,
            Description = "在屏幕任意位置显示网易云正在播放的歌词，跟着播放进度逐行滚动。"
                        + "数据来自一个只读网易云界面的小中继插件，本插件不注入、不修改网易云。",
            ContentFactory = () =>
            {
                try
                {
                    _page = new NeteaseLyricsPage(this);
                    return _page;
                }
                catch (Exception ex)
                {
                    // 页面建不出来时，宿主只会画页头 —— 用户看到的就是"只有标题和副标题"。
                    // 所以这里必须自己记下来，并且返回一个能显示错误的控件。
                    Log("[歌词] ★页面创建失败★ " + ex);
                    return new System.Windows.Controls.TextBlock
                    {
                        Text = "页面创建失败（已写入 plugin.log）：\n" + ex.Message,
                        TextWrapping = System.Windows.TextWrapping.Wrap,
                        Margin = new System.Windows.Thickness(12),
                    };
                }
            },
        });

        Log("[歌词] Initialize：即将按设置显示窗口，Enabled=" + _settings.Enabled);
        if (_settings.Enabled)
        {
            ShowWindow();
        }

        if (!_settings.OnboardingShown)
        {
            _settings.OnboardingShown = true;
            SaveSettings();

            // 首次装载（**卸载后重装也算** —— 卸载会把 plugin-data\<id>\ 整个删掉，
            // 这个开关跟着回到默认值，所以重装时会再走一遍这里）：
            //   1) 插件页面顶部展开"使用引导"卡片
            //   2) 弹一次「使用说明」窗 —— 用户光看插件页根本不知道网易云那边还要装东西
            //
            // 为什么排到 ApplicationIdle 而不是在这儿直接弹：
            //   Initialize 跑在宿主启动阶段，主窗口可能还没 Show()，
            //   这时候 ShowDialog 会把启动流程一起卡住。等 UI 空闲了再弹最稳。
            GuideVisible = true;
            Log("[歌词] 首次装载：已展开页面内引导，稍后弹一次使用说明窗");

            try
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_help is { IsLoaded: true })
                        {
                            return;
                        }

                        ShowHelp(null);
                    }
                    catch (Exception ex)
                    {
                        Log("[歌词] 自动弹出使用说明失败：" + ex);
                    }
                }), DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                Log("[歌词] 安排使用说明窗失败：" + ex);
            }
        }

        // 200ms 一拍：位置靠"时间戳 + 外推"，但"该换行了"只在 Tick 里判 ——
        // 1 秒一拍的话每行最多晚 1 秒才切，200ms 是人眼看不出来的量级。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // 中继的代码变了就顺手更新一份（只有装过才更新，没装过不动）
        EnsureRelayUpToDate();

        host.Log($"[网易云歌词] 已加载，配置目录：{host.PluginDirectory}");
    }

    public void Shutdown()
    {
        try
        {
            _timer?.Stop();
            _timer = null;
            _page?.StopTimer();
            _window?.Close();
            _window = null;
            _help?.Close();
            _help = null;
            _lyrics?.Dispose();
            _lyrics = null;
        }
        catch
        {
            // 退出流程里不能抛
        }
    }

    /// <summary>同时写到宿主调试后台和插件目录的 plugin.log（宿主日志只在 UI 里，排查不方便）。</summary>
    private readonly List<string> _recentLog = new();

    /// <summary>给插件页面显示的最近日志（不往宿主调试后台写）。</summary>
    public string RecentLog
    {
        get
        {
            lock (_recentLog)
            {
                return string.Join(Environment.NewLine, _recentLog);
            }
        }
    }

    public void ClearLog()
    {
        lock (_recentLog)
        {
            _recentLog.Clear();
        }
    }

    /// <summary>插件自己的日志文件路径（页面里可以直接打开）。</summary>
    public string LogFilePath => Path.Combine(_host.PluginDirectory, "plugin.log");

    internal void Log(string message)
    {
        // 注意：不调用 _host.Log —— 那是往软件本体的"调试后台"写。插件的日志只留在插件自己这里。
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss}  {message}";
            lock (_recentLog)
            {
                _recentLog.Add(line);
                if (_recentLog.Count > 400)
                {
                    _recentLog.RemoveRange(0, _recentLog.Count - 400);
                }
            }

            File.AppendAllText(Path.Combine(_host!.PluginDirectory, "plugin.log"), line + Environment.NewLine);
        }
        catch
        {
        }
    }

    /// <summary>中继插件的安装目录（BetterNCM / chromatic 认的开发插件目录）。</summary>
    private static IEnumerable<string> RelayInstallDirectories()
    {
        yield return @"C:\betterncm\plugins_dev\MCOBridge";
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "betterncm", "plugins_dev", "MCOBridge");
    }

    /// <summary>中继插件的文件内容（打包在本插件里，装机时写出去）。</summary>
    private const string RelayManifestJson = """
{
    "manifest_version": 1,
    "name": "MCOBridge",
    "version": "1.0.1",
    "author": "goldiamond",
    "description": "把网易云当前的播放进度写成一个 JSON 文件，供 MinecraftChatOverlay 的歌词插件读取。只读 DOM、只写文件，不改客户端任何东西。",
    "betterncm_version": ">=1.0.0",
    "preview": "preview.png",
    "ncm3-compatible": true,
    "noDevReload": true,
    "injects": { "Main": [ { "file": "index.js" } ] }
}
""";

    private const string RelayIndexJs = """
// MCOBridge —— 把网易云的播放进度写成 JSON，给 MinecraftChatOverlay 的歌词插件读。
//
// 取进度按这个顺序，第一个能用的就用：
//   1) BetterNCM 原生接口（有就最准）
//   2) 页面里的 <audio>/<video>.currentTime
//   3) 进度条 input[type=range].value（最早只有这一条路）
//
// 什么时候写文件：**事件回调为主，定时器只留 5 秒兜底**。
//   为什么这么改：以前只有一个 setInterval(1000)，而网易云被游戏盖住 / 最小化时，
//   Chromium 会把"隐藏页面"的定时器节流到**一分钟一次**（实测 state.json 只在
//   每分钟的第 15 秒写一次）—— 于是暂停、拖动进度条、切歌这些最多要 60 秒才
//   被外面发现。媒体事件和 DOM 事件不受这个节流影响，所以主路径换成事件。
const DIR = "./MCOBridge";
const FILE = DIR + "/state.json";
const HEARTBEAT_MS = 5000;
const SETTLE_MS = 3500;
const EVENTS = ["input", "change", "click", "timeupdate", "seeking", "seeked",
                "play", "playing", "pause", "ended", "ratechange", "durationchange", "loadedmetadata"];

let dirReady = false, busy = false, pending = false;
let lastPos = null, lastChangeAt = 0;
let mediaEl = null, settleTimer = null;

function readMedia() {
    try {
        if (!mediaEl || !mediaEl.isConnected) { mediaEl = document.querySelector("audio, video"); }
        if (mediaEl && isFinite(mediaEl.duration) && mediaEl.duration > 0) {
            return { position: mediaEl.currentTime, duration: mediaEl.duration, source: "media" };
        }
    } catch (e) { }
    return null;
}

function readSlider() {
    try {
        var inputs = document.querySelectorAll("input[type=range]");
        for (var i = 0; i < inputs.length; i++) {
            var max = parseFloat(inputs[i].max), val = parseFloat(inputs[i].value);
            if (isFinite(max) && max > 0) {
                return { position: isFinite(val) ? val : 0, duration: max, source: "slider" };
            }
        }
    } catch (e) { }
    return null;
}

function readNative() {
    try {
        var ncm = betterncm && betterncm.ncm;
        if (ncm && typeof ncm.getPosition === "function") {
            var pos = Number(ncm.getPosition());
            var dur = (typeof ncm.getDuration === "function") ? Number(ncm.getDuration()) : 0;
            if (isFinite(pos) && pos >= 0) {
                if (!isFinite(dur) || dur <= 0) {
                    var m = readMedia() || readSlider();
                    dur = m ? m.duration : 0;
                }
                if (dur > 0) { return { position: pos, duration: dur, source: "ncm" }; }
            }
        }
    } catch (e) { }
    return null;
}

function readProgress() {
    return readNative() || readMedia() || readSlider();
}

function positionIsMoving() { return (Date.now() - lastChangeAt) < 3000; }

function playingNow() {
    try { if (mediaEl && mediaEl.isConnected) { return !mediaEl.paused; } } catch (e) { }
    return positionIsMoving();
}

async function ensureDir() { if (dirReady) return; try { await betterncm.fs.mkdir(DIR + "/"); } catch (e) { } dirReady = true; }

async function writeState() {
    if (busy) { pending = true; return; }   // 上一次还没写完：记一笔，写完立刻补一次（不靠定时器）
    busy = true;
    try {
        var p = readProgress();
        var now = Date.now();
        if (p && p.position !== lastPos) { lastPos = p.position; lastChangeAt = now; }
        var state = p
            ? { position: p.position, duration: p.duration, playing: playingNow(), title: document.title || "", source: p.source, updatedAt: now }
            : { position: null, duration: null, playing: false, title: document.title || "", source: "none", updatedAt: now };
        await ensureDir();
        await betterncm.fs.writeFileText(FILE, JSON.stringify(state));
    } catch (e) {
        console.warn("[MCOBridge] 写状态出错", e && e.message);
    } finally {
        busy = false;
        if (pending) { pending = false; writeState(); }
    }
}

// 交互之后再补写一次：位置"停下来了"要等 3 秒才看得出来，光靠事件那一刻判不出来。
// 用户点暂停 / 拖进度条时窗口一定是可见的，这点 setTimeout 不会被节流。
function scheduleSettle() {
    try {
        if (settleTimer) { clearTimeout(settleTimer); }
        settleTimer = setTimeout(function () { settleTimer = null; writeState(); }, SETTLE_MS);
    } catch (e) { }
}

function onEvent() { writeState(); scheduleSettle(); }

// 事件一律挂在 document 的捕获阶段：网易云切页会把滑块 / 播放器整个换掉，
// 挂在元素身上的监听会跟着一起消失。
for (var i = 0; i < EVENTS.length; i++) {
    try { document.addEventListener(EVENTS[i], onEvent, true); } catch (e) { }
}

// 兜底心跳：Worker 和页面计时器**两条都开** —— Worker 里的定时器在部分环境里更不容易
// 被节流，但万一它建得起来却不出消息，页面计时器还能兜一层（重复触发会被 busy/pending 合并掉）
function startHeartbeat() {
    try {
        var src = "setInterval(function () { postMessage(1); }, " + HEARTBEAT_MS + ");";
        var url = URL.createObjectURL(new Blob([src], { type: "text/javascript" }));
        var worker = new Worker(url);
        worker.onmessage = function () { writeState(); };
    } catch (e) { }
    setInterval(writeState, HEARTBEAT_MS);
}

plugin.onLoad(function () {
    console.log("[MCOBridge] loaded");
    writeState();
    startHeartbeat();
});
""";

    /// <summary>
    /// 启动时把"已经装过的"中继插件跟内置版本对齐 —— 中继改了行为之后（比如这次从定时器
    /// 改成事件驱动），用户只要重启一次网易云就生效，不用再点一遍【把中继插件装到网易云】。
    /// 目录不存在就不管（说明压根没装过，别替用户创建）。
    /// </summary>
    private void EnsureRelayUpToDate()
    {
        foreach (var dir in RelayInstallDirectories())
        {
            try
            {
                var js = Path.Combine(dir, "index.js");
                if (!Directory.Exists(dir) || !File.Exists(js))
                {
                    continue;
                }

                if (string.Equals(File.ReadAllText(js), RelayIndexJs, StringComparison.Ordinal))
                {
                    continue;
                }

                File.WriteAllText(js, RelayIndexJs);
                File.WriteAllText(Path.Combine(dir, "manifest.json"), RelayManifestJson);
                Log("[歌词] 中继插件已自动更新到内置版本（重启网易云后生效）：" + dir);
            }
            catch (Exception ex)
            {
                Log("[歌词] 自动更新中继插件失败（" + dir + "）：" + ex.Message);
            }
        }
    }


    /// <summary>
    /// 把中继插件装到网易云那边（BetterNCM 的开发插件目录）。
    /// 返回给用户看的一句话。
    /// </summary>
    public string InstallRelay()
    {
        try
        {
            var candidates = RelayInstallDirectories().ToList();

            var written = new List<string>();
            foreach (var dir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "manifest.json"), RelayManifestJson);
                    File.WriteAllText(Path.Combine(dir, "index.js"), RelayIndexJs);
                    written.Add(dir);
                }
                catch
                {
                    // 这个位置写不进去就试下一个
                }
            }

            if (written.Count == 0)
            {
                return RelayInstallResult = "装不上：写不进网易云的插件目录（多半是权限问题，试试用管理员身份运行）";
            }

            Log("[歌词] 中继已写入：" + string.Join(" | ", written));
            RelayInstallResult = "已装好中继插件：完全退出网易云（托盘右键退出）再打开，进度就会开始上报。文件位置：" + written[0];
            return "已装好中继插件 → 完全退出网易云（托盘右键退出）再打开，进度就会开始上报。" + Environment.NewLine
                 + "如果网易云装了 BetterNCM，也可以在插件商店里点「重载」生效。" + Environment.NewLine
                 + "文件位置：" + written[0];
        }
        catch (Exception ex)
        {
            return RelayInstallResult = "装不上：" + ex.Message;
        }
    }

    /// <summary>
    /// 【使用说明】按钮：弹一个独立窗口（外观照抄软件本体的公告窗 / B站扫码窗那一套：
    /// 合并 ModernControls.xaml 主题字典 + 居中于主窗口 + SizeToContent），
    /// 而不是以前那种系统 MessageBox。
    /// </summary>
    public void ShowHelp(Window? owner)
    {
        try
        {
            if (_help is { IsLoaded: true })
            {
                _help.Activate();
                return;
            }

            var dialog = new LyricsHelpWindow(this);
            _help = dialog;

            // 歌词窗是 Topmost 的：它正显示着的时候，这个说明窗也得 Topmost，
            // 否则用户把歌词窗拖到屏幕中间就再也看不到说明了。
            dialog.Topmost = IsWindowVisible;

            // 宿主主窗口可见才设 Owner：它最小化进托盘时，被它拥有的窗口会跟着不可见（歌词窗踩过这个坑）。
            try
            {
                var main = owner ?? Application.Current?.MainWindow;
                if (main is not null && !ReferenceEquals(main, dialog) && main.IsVisible && main.WindowState != WindowState.Minimized)
                {
                    dialog.Owner = main;
                }
                else
                {
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
            }
            catch
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dialog.Closed += (_, _) => _help = null;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Log("[歌词] 打开使用说明失败：" + ex);
        }
    }


    public void SaveSettings()
    {
        try
        {
            PluginSettingsFile.Save(_host.PluginDirectory, _settings);
        }
        catch (Exception ex)
        {
            Log("[网易云歌词] 保存设置失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把歌词窗显示出来。这是"用户 / 设置主动要求显示"的入口，
    /// 会清掉 _autoShowSuppressed（也就是用户刚刚点过【隐藏窗口】的那条标记）。
    /// </summary>
    public void ShowWindow()
    {
        _autoShowSuppressed = false;
        EnsureWindowVisible();
    }

    /// <summary>
    /// 内部（每秒刷新）用的"确保窗口在"：用户手动隐藏过就什么都不做。
    /// 这就是"正在放歌时点【隐藏窗口】没反应"的修复点 ——
    /// 以前 Tick → Push 无条件 Show()，一秒后窗口又自己回来了。
    /// </summary>
    public void EnsureWindowVisible()
    {
        try
        {
            if (_autoShowSuppressed || !_settings.Enabled)
            {
                return;
            }

            if (_window is null)
            {
                _window = new LyricsWindow();
                _window.LocationChanged += (_, _) =>
                {
                    _settings.WindowLeft = _window!.Left;
                    _settings.WindowTop = _window!.Top;
                    SaveSettings();
                };
            }

            _window.ApplySettings(_settings);
            if (!_window.IsVisible)
            {
                _window.Show();
                _window.DetachFromOwner();
                Log("[歌词] 已调用 Show()，之后 IsVisible=" + _window.IsVisible + " 实际可见=" + _window.IsLoaded);
            }

            _window.ApplySettings(_settings);
        }
        catch (Exception ex)
        {
            Log("[网易云歌词] 显示歌词窗失败：" + ex.Message);
        }
    }

    /// <summary>用户点【隐藏窗口】：藏起来，并记住"是他自己要藏的"，免得下一秒又被自动显示放出来。</summary>
    public void HideWindow()
    {
        _autoShowSuppressed = true;
        HideWindowCore();
    }

    /// <summary>硬隐藏（关插件 / 退出时用），不动那个标记。</summary>
    private void HideWindowCore()
    {
        try
        {
            _window?.Hide();
        }
        catch
        {
        }
    }

    /// <summary>把窗口挪回屏幕右上角（换分辨率 / 不小心拖出屏幕时用）。</summary>
    public void MoveToTopRight()
    {
        try
        {
            var area = SystemParameters.WorkArea;
            _settings.WindowLeft = Math.Max(0, area.Right - _settings.WindowWidth - 24);
            _settings.WindowTop = area.Top + 24;
            SaveSettings();
            EnsureWindowVisible();
        }
        catch
        {
        }
    }

    /// <summary>手动重取一次歌词（换歌没跟上时用）。</summary>
    public void RefreshLyrics()
    {
        _songKey = "";
        Tick();
    }

    private async void Tick()
    {
        try
        {
            if (!_settings.Enabled)
            {
                Status = "已关闭";
                HideWindow();
                return;
            }

            var state = _relay.Read();
            LastState = state;

            if (state is null)
            {
                Status = "没读到播放数据 —— 网易云没在放歌，或者中继插件没装/没加载";
                Push("未在播放", null, null, null, "");
                return;
            }

            if (DateTime.Now - _lastTitleRead >= TimeSpan.FromSeconds(1))
            {
                _lastTitleRead = DateTime.Now;
                (_cachedTitle, _cachedArtist) = WindowTitleMetadata.TryRead();
            }

            var title = _cachedTitle;
            var artist = _cachedArtist;
            var key = (title ?? "") + "|" + (artist ?? "");
            if (!string.Equals(key, _songKey, StringComparison.OrdinalIgnoreCase))
            {
                _songKey = key;
                _lastKaraokeOn = null;
                await LoadLyricsAsync(title, artist).ConfigureAwait(true);
            }

            var position = state.PositionNow;
            var progress = $"{Format(position)} / {Format(state.Duration)}";

            if (_document is null)
            {
                var hint = title is null ? "认不出当前歌曲（网易云窗口标题里没有歌名）" : $"没找到《{title}》的歌词";
                Status = hint;
                Push(hint, null, null, null, progress);
                return;
            }

            // 歌词提前/延后：正数让歌词提前出现
            var index = LrcParser.IndexAt(_document.Lines, Math.Max(0, position + _settings.LyricOffsetMs / 1000.0));
            var romaji = index >= 0 && index < _document.Lines.Count ? _document.Lines[index].Romaji : null;
            var karaokeLine = index >= 0 && index < _document.Lines.Count ? _document.Lines[index] : null;
            var karaokeOn = _settings.Karaoke && karaokeLine is { HasKaraoke: true };
            var sungChars = karaokeOn ? LrcParser.SungChars(karaokeLine!, Math.Max(0, position + _settings.LyricOffsetMs / 1000.0)) : -1;

            if (_lastKaraokeOn != karaokeOn)
            {
                _lastKaraokeOn = karaokeOn;
                Log(karaokeOn
                    ? $"[歌词] 逐字生效：《{_document.Title}》第 {index + 1} 行，本行 {sungChars}/{(_document.Lines[index].Text.Length)} 字"
                    : $"[歌词] 这一行没有逐字数据（这首歌没 yrc，或这行的逐字和歌词文字对不上）");
            }

            if (!string.Equals(state.Source, _lastSource, StringComparison.Ordinal))
            {
                _lastSource = state.Source;
                Log("[歌词] 进度来源：" + (string.IsNullOrWhiteSpace(state.Source) ? "未知（旧版中继）" : state.Source));
            }

            // 刷新频率固定 200ms（见 Initialize）：位置靠外推，换行判定跟着这个节拍走

            var current = index >= 0 && index < _document.Lines.Count ? _document.Lines[index].Text : "♪";
            var translation = index >= 0 && index < _document.Lines.Count ? _document.Lines[index].Translation : null;
            var next = index + 1 >= 0 && index + 1 < _document.Lines.Count ? _document.Lines[index + 1].Text : null;

            Status = $"{_document.Title} - {_document.Artist}    {progress}    （{_document.Lines.Count} 行）";

            var really = _window?.IsReallyVisible() ?? false;
            if (really != _lastReallyVisible)
            {
                _lastReallyVisible = really;
                Log("[歌词] 系统可见性变化 → " + really + "（WPF可见=" + (_window?.IsVisible ?? false) + "）");
            }
            Push(current, translation, romaji, next, progress, karaokeOn, sungChars);
        }
        catch (Exception ex)
        {
            Log("[网易云歌词] 刷新失败：" + ex.Message);
        }
    }

    private async Task LoadLyricsAsync(string? title, string? artist)
    {
        if (_lyrics is null || string.IsNullOrWhiteSpace(title) || _fetching)
        {
            _document = string.IsNullOrWhiteSpace(title) ? null : _document;
            return;
        }

        _fetching = true;
        try
        {
            Log($"[网易云歌词] 查歌词：{title} - {artist}");
            _document = await _lyrics.FindAsync(title!, artist, CancellationToken.None).ConfigureAwait(true);
            Log(_document is null
                ? $"[网易云歌词] 没找到《{title}》的歌词"
                : $"[网易云歌词] 找到《{_document.Title}》共 {_document.Lines.Count} 行，翻译={_document.HasTranslation}");
        }
        catch (Exception ex)
        {
            Log("[网易云歌词] 取歌词失败：" + ex.Message);
            _document = null;
        }
        finally
        {
            _fetching = false;
        }
    }

    private void Push(string current, string? translation, string? romaji, string? next, string progress,
                      bool karaoke = false, int sungChars = -1)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        ShowWindow();
        _window?.ShowLine(current, translation, romaji, next, progress, _settings.ShowTranslation, _settings.ShowRomaji, _settings.ShowNextLine, _settings.ShowTrackInfo, karaoke, sungChars);
    }

    private static string Format(double seconds)
    {
        if (seconds <= 0)
        {
            return "--:--";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
    }
}
