using System.IO;
using System.Windows.Threading;
using DouyinDanmaku.Protocol;
using MinecraftChatOverlay.Plugin;

namespace DouyinDanmaku;

/// <summary>
/// 抖音直播弹幕插件。把直播间六类消息（弹幕/礼物/点赞/入场/粉丝团/房间统计）
/// 渲染成文本后转发到宿主聊天悬浮窗。
/// </summary>
public sealed class DouyinDanmakuPlugin : IPlugin
{
    public string Id => "nn.douyindanmaku";

    public string DisplayName => "抖音直播弹幕";

    private IPluginHost? _host;
    private Settings _settings = new();
    private DouyinClient? _client;
    private PluginPage? _page;
    private DouyinPageView? _view;

    /// <summary>宿主 UI 线程的调度器，Initialize 时抓下来（那会儿就在 UI 线程上）。</summary>
    private Dispatcher? _dispatcher;

    private bool _disposed;

    /// <summary>正在连接/断开，期间按钮禁用。</summary>
    private volatile bool _busy;

    /// <summary>忙的时候按钮上写什么。卡住时能一眼看出卡在哪一头。</summary>
    private volatile string _busyText = "连接中…";

    /// <summary>
    /// 当前这次连接尝试的取消源。连接最坏要跑 4 个端点 × 12 秒，
    /// 用户点「断开」必须能立刻掐掉它，不能干等。
    /// </summary>
    private volatile CancellationTokenSource? _connectCts;

    /// <summary>状态行文字。收流线程写、StatusText 委托读，所以用 volatile。</summary>
    private volatile string _status = "未连接";
    private long _received;
    private long _forwarded;
    private long _lastRoomTotal = -1;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = PluginSettingsFile.Load<Settings>(host.PluginDirectory);
        _lastRoomTotal = -1;

        // 宿主是在 UI 线程上调 Initialize 的（PluginManager.LoadAll → instance.Initialize），
        // 所以此刻的 Dispatcher 就是宿主 UI 线程那个。收流线程要往悬浮窗发消息，
        // 必须靠它编组回去（见 OnMessage 里的说明）。
        _dispatcher = System.Windows.Application.Current?.Dispatcher;

        _page = BuildPage();
        host.RegisterPage(_page);

        host.Log($"抖音直播弹幕插件已加载，数据目录 {host.PluginDirectory}");
        host.Log(_dispatcher == null
            ? "[抖音] 警告：没取到宿主 UI 调度器，弹幕将退回直发模式（跨线程可能被宿主吞掉）"
            : "[抖音] 已取到宿主 UI 调度器，弹幕会编组到 UI 线程再发");

        var auto = _settings;
        if (auto.AutoConnect && auto.WebRoomId.Trim() != "")
        {
            _ = ConnectAsync(auto.WebRoomId.Trim());
        }
    }

    /// <summary>禁用/卸载/退出时调用。幂等，可反复调用（铁律二）。</summary>
    public void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 忽略
        }

        // 再跑一遍确保连接侧资源确实释放干净
        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 忽略
        }

        var view = _view;
        if (view != null)
        {
            view.SettingsChanged -= OnSettingsChanged;
            view.ConnectRequested -= OnConnectRequested;
            _view = null;
        }

        if (_page != null)
        {
            _page.Fields.Clear();
            _page.Actions.Clear();
            _page.ContentFactory = null;
            _page.StatusText = null;
            _page = null;
        }

        _host = null;
    }

    // ---------- 连接生命周期 ----------

    private bool IsConnected => _client?.IsConnected == true;

    private async Task<bool> ConnectAsync(string webRoomId)
    {
        if (_disposed)
        {
            return false;
        }

        await DisconnectAsync().ConfigureAwait(false);

        var client = new DouyinClient(DouyinSigner.Instance.Compute);
        client.MessageReceived += OnMessage;
        client.StatusChanged += OnStatus;
        _client = client;
        _status = "连接中…";
        _busyText = "连接中…";
        _busy = true;
        Interlocked.Exchange(ref _received, 0);
        Interlocked.Exchange(ref _forwarded, 0);
        RefreshViewState();

        // 自己持一份取消源：DisconnectAsync 会先于 DouyinClient 内部建 _cts 之前
        // 就把它取消掉，光靠 client 内部的令牌会漏掉这个竞态窗口。
        var cts = new CancellationTokenSource();
        _connectCts = cts;

        bool ok;
        try
        {
            ok = await client.ConnectAsync(webRoomId, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ok = false;
            _status = "已取消连接";
        }
        catch (Exception ex)
        {
            ok = false;
            _status = $"连接异常：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_connectCts, cts))
            {
                _connectCts = null;
            }

            cts.Dispose();
        }

        _busy = false;

        if (!ok)
        {
            client.MessageReceived -= OnMessage;
            client.StatusChanged -= OnStatus;
            await client.DisposeAsync().ConfigureAwait(false);
            if (ReferenceEquals(_client, client))
            {
                _client = null;
            }

            // 连接失败时把最近一次房间页面快照落盘，便于排查
            // （roomId 解析失败时尤其需要看页面真实结构）
            DumpDiagnostic();
        }

        RefreshViewState();
        return ok;
    }

    private async Task DisconnectAsync()
    {
        // 掐掉正在进行的连接尝试：它最坏要跑 4 个端点 × 12 秒。
        // 先取消再取 _client，这样 ConnectAsync 里那个还没赋上 _client 的
        // 早期阶段（取 ttwid / 解析 roomId）也能被打断。
        var pending = _connectCts;
        if (pending != null)
        {
            try
            {
                pending.Cancel();
            }
            catch
            {
            }
        }

        var client = _client;
        if (client == null)
        {
            // 没有连接也要复位忙标志：点「断开」时若连接已经自己断了（收流线程报错退出），
            // 这里直接 return 会把按钮永远留在「连接中…」且禁用，用户再也点不动。
            _busy = false;
            RefreshViewState();
            return;
        }

        _client = null;
        _status = "未连接";
        Interlocked.Exchange(ref _lastRoomTotal, -1);

        client.MessageReceived -= OnMessage;
        client.StatusChanged -= OnStatus;

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // 忙标志必须在 finally 里复位：DisposeAsync 内部要关 WebSocket 并等收流/心跳
            // 两个后台任务退出，任何一步卡住或抛异常都不能把按钮永久锁死。
            _busy = false;
            RefreshViewState();
        }
    }

    private void OnStatus(string s)
    {
        _status = s;
        _host?.Log($"[抖音] {s}");
    }

    /// <summary>连接失败时把最近一次房间页面快照写到数据目录，便于排查。</summary>
    private void DumpDiagnostic()
    {
        var page = DouyinClient.LastRoomPage;
        var host = _host;
        if (string.IsNullOrEmpty(page) || host == null)
        {
            return;
        }

        try
        {
            var path = Path.Combine(host.PluginDirectory, "diagnostic-room.html");
            File.WriteAllText(path, page);
            host.Log($"[抖音] 房间页面快照已保存：{path}");
        }
        catch (Exception ex)
        {
            host.Log($"[抖音] 页面快照保存失败：{ex.Message}");
        }
    }

    // ---------- 消息转发 ----------

    private void OnMessage(DanmakuEvent ev)
    {
        Interlocked.Increment(ref _received);

        var host = _host;
        if (host == null || _disposed)
        {
            return;
        }

        var settings = _settings;
        if (!IsKindEnabled(ev.Kind, settings))
        {
            return;
        }

        // 房间统计每秒来一次，只在人数变化时才转发，避免刷屏
        if (ev.Kind == DanmakuKind.RoomStats)
        {
            var previous = Interlocked.Read(ref _lastRoomTotal);
            if (ev.Total == previous)
            {
                return;
            }

            Interlocked.Exchange(ref _lastRoomTotal, ev.Total);
        }

        var text = DanmakuFormatter.Render(ev, settings);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 这里跑在 DouyinClient 的收流线程上，而宿主的 AddMessage 是直接往
        // ObservableCollection 里塞的（OverlayWindow.xaml.cs:528 `_messages.Add(vm)`），
        // 宿主又没调用 EnableCollectionSynchronization —— 跨线程必然抛
        // 「该类型的 CollectionView 不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改」，
        // 异常在 MainWindow.Plugins.xaml.cs:134 被 catch 吞掉，宿主 EnableDebugLog=False 时
        // 连日志都不落盘。表现就是「连上了、状态行有帧数、悬浮窗一条都没有」。
        // 所以必须编组回 UI 线程再发。
        OnUi(() =>
        {
            try
            {
                host.SendToOverlay(text);
                Interlocked.Increment(ref _forwarded);
            }
            catch (Exception ex)
            {
                host.Log($"[抖音] 转发到悬浮窗失败：{ex.Message}");
            }
        });
    }

    private static bool IsKindEnabled(DanmakuKind kind, Settings s) => kind switch
    {
        DanmakuKind.Chat => s.ShowChat,
        DanmakuKind.Like => s.ShowLike,
        DanmakuKind.Gift => s.ShowGift,
        DanmakuKind.Member => s.ShowMember,
        DanmakuKind.RoomStats => s.ShowRoomStats,
        DanmakuKind.Fansclub => s.ShowFansclub,
        _ => false,
    };

    /// <summary>
    /// 把动作丢到宿主 UI 线程上跑。拿不到调度器时只能就地执行（直发模式），
    /// 这是最后的兜底：编组不了也总比把消息全丢掉强，只是跨线程可能被宿主吞掉。
    /// </summary>
    private void OnUi(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
        catch
        {
            // 宿主正在退出、调度器已关：丢弃这次刷新
        }
    }

    /// <summary>
    /// 刷新按钮/输入框状态。这只是外观，拿不到调度器时直接跳过 ——
    /// 绝不能从别的线程去碰控件，那会直接抛异常。
    /// </summary>
    private void RefreshViewState()
    {
        var dispatcher = _dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        OnUi(() => _view?.SetState(IsConnected, _busy, _busyText));
    }

    // ---------- 配置页面 ----------

    private PluginPage BuildPage() => new()
    {
        Title = "抖音直播弹幕",
        Description = "直连抖音直播间，把弹幕、礼物、点赞、入场、粉丝团、房间统计转发到聊天悬浮窗。",
        StatusText = () =>
        {
            var client = _client;
            var received = Interlocked.Read(ref _received);
            var forwarded = Interlocked.Read(ref _forwarded);

            // 拿不到调度器时把这件事直接摆在状态行上：宿主日志受 EnableDebugLog 门控，
            // 光靠 host.Log 排查不到，而这一条恰好决定了「收到但转发不出去」是不是本因。
            var warn = _dispatcher == null ? " · ⚠ 无UI调度器" : "";

            if (client != null && client.IsConnected)
            {
                return $"已连接 · 收到 {received} 条 · 已转发 {forwarded} 条{warn} · {client.StatsSummary}";
            }

            return $"{_status} · 收到 {received} 条 · 已转发 {forwarded} 条{warn}";
        },

        // 自绘整页：只有这样连接区才能落在页面最上面。
        // 宿主在 MainWindow.Plugins.xaml.cs:324-329 看到 ContentFactory 返回非 null
        // 就直接 return，不再建「设置」「操作」两张卡片 —— 也就是说字段和操作按钮
        // 都由我们自己画，页头的标题/说明和下面的状态行仍由宿主画。
        ContentFactory = () =>
        {
            // 宿主在插件启用/禁用/安装/卸载时会整体重建页面（RebuildPluginPages），
            // 于是这个工厂可能被调用第二次。旧 view 已经不在可视树上了，但要先把事件解绑，
            // 免得它继续挂着回调。
            var previous = _view;
            if (previous != null)
            {
                previous.SettingsChanged -= OnSettingsChanged;
                previous.ConnectRequested -= OnConnectRequested;
                _view = null;
            }

            var view = new DouyinPageView();
            view.Bind(_settings);
            view.SettingsChanged += OnSettingsChanged;
            view.ConnectRequested += OnConnectRequested;
            view.SetState(IsConnected, _busy);
            _view = view;
            return view;
        },
    };

    /// <summary>用户改了任意设置：写回配置对象并落盘。</summary>
    private void OnSettingsChanged()
    {
        var host = _host;
        var view = _view;
        if (host == null || view == null || _disposed)
        {
            return;
        }

        view.ReadInto(_settings);
        PluginSettingsFile.Save(host.PluginDirectory, _settings);
    }

    private void OnConnectRequested()
    {
        if (_disposed)
        {
            return;
        }

        // 正忙时这一下点击是「取消」，不是「再连一次」。
        if (_busy)
        {
            var pending = _connectCts;
            if (pending != null)
            {
                _busyText = "取消中…";
                RefreshViewState();
                try
                {
                    pending.Cancel();
                }
                catch
                {
                    // 已经取消了
                }
            }

            // 卡在断开阶段时重复点击直接忽略：DisconnectAsync 自己会复位 _busy。
            return;
        }

        if (_client != null)
        {
            _busyText = "断开中…";
            _busy = true;
            RefreshViewState();
            _ = DisconnectAsync().ContinueWith(_ => RefreshViewState());
            return;
        }

        var webRoomId = _settings.WebRoomId.Trim();
        if (webRoomId == "")
        {
            _host?.ShowToast("请先填写直播间号");
            return;
        }

        _ = ConnectAsync(webRoomId);
    }
}
