using System.Windows;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.NetEaseLyric;

/// <summary>
/// 网易云歌词插件。
///
/// 数据来源：系统媒体会话（SMTC）→ 曲名/歌手/播放进度；歌词来自网易云公开接口。
/// 界面：插件自己开的置顶透明小窗（不走宿主的悬浮窗），样式可在插件页里调。
/// </summary>
public sealed class NetEaseLyricPlugin : IPlugin
{
    private const string AutoSourceKey = "__auto__";

    private IPluginHost? _host;
    private NetEaseLyricSettings _settings = new();
    private SmtcReader _smtc = new();
    private NeteaseLyricClient? _client;
    private BridgeReceiver? _bridge;
    private LyricEngine? _engine;
    private LyricWindow? _window;
    private LyricFrame _lastFrame = LyricFrame.Empty;
    private string _status = "还没开始";

    public string Id => "goldiamond.neteaselyric";

    public string DisplayName => "网易云歌词";

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = PluginSettingsFile.Load<NetEaseLyricSettings>(host.PluginDirectory);
        _client = new NeteaseLyricClient(host.PluginDirectory);
        _smtc = new SmtcReader();

        host.RegisterPage(BuildPage());

        if (_settings.Enabled)
        {
            StartLyrics();
        }
        else
        {
            _status = "已关闭（打开「显示歌词窗」就会开始跟随）";
        }

        host.Log($"[网易云歌词] 已装载，插件目录：{host.PluginDirectory}");
    }

    public void Shutdown()
    {
        StopLyrics();
        _host?.Log("[网易云歌词] 已卸载");
    }

    // ------------------------------------------------------------ 启停

    private void StartLyrics()
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

            // 本地桥接接收端：InfLink 桥接插件会往这个端口 POST 精确进度
            if (_settings.DataSource != DataSourceKind.SmtcOnly)
            {
                _bridge = new BridgeReceiver(_settings.BridgePort, WriteLog);
                _bridge.Start();
            }

            _engine = new LyricEngine(_smtc, _client!, _bridge, () => _settings, dispatcher, WriteLog);
            _engine.Updated += OnFrame;

            _window = new LyricWindow(_settings, SaveSettings);
            _window.Show();

            _ = _engine.StartAsync();
            _status = "正在跟随播放器…";
        }
        catch (Exception ex)
        {
            _status = "启动失败：" + ex.Message;
            WriteLog("启动失败：" + ex);
        }
    }

    private void StopLyrics()
    {
        try
        {
            if (_engine is not null)
            {
                _engine.Updated -= OnFrame;
                _engine.Dispose();
                _engine = null;
            }

            _bridge?.Dispose();
            _bridge = null;

            _window?.Close();
            _window = null;
        }
        catch (Exception ex)
        {
            WriteLog("停止出错：" + ex.Message);
        }
    }

    private void OnFrame(LyricFrame frame)
    {
        _lastFrame = frame;

        if (_window is not null)
        {
            _window.UpdateFrame(frame);

            if (_settings.HideWhenPaused && !frame.Playing && !frame.Loading && frame.Current.Length == 0)
            {
                _window.Visibility = Visibility.Hidden;
            }
            else if (_window.Visibility != Visibility.Visible)
            {
                _window.Visibility = Visibility.Visible;
            }
        }

        _status = DescribeFrame(frame) + (_engine is null ? "" : "｜" + _engine.DescribeTransport());
    }

    private static string DescribeFrame(LyricFrame frame)
    {
        if (frame.Title.Length == 0)
        {
            return frame.Message.Length > 0 ? frame.Message : "等待网易云播放…";
        }

        var track = $"{frame.Title} — {frame.Artist}";
        if (frame.Loading)
        {
            return $"{track}｜正在找歌词…";
        }

        return frame.HasLyric ? track : $"{track}｜{frame.Message}";
    }

    // ------------------------------------------------------------ 插件页

    private PluginPage BuildPage()
    {
        var page = new PluginPage
        {
            Title = "网易云歌词",
            Description = "把网易云正在播放的歌词显示成一个自己的置顶小窗。需要在网易云音乐设置里打开「系统媒体控制」。",
            StatusText = () => _status,
        };

        page.Fields.Add(new PluginField
        {
            Key = "source",
            Label = "位置数据来源",
            Kind = PluginFieldKind.Select,
            SelectedKey = _settings.DataSource switch
            {
                DataSourceKind.BridgeOnly => "bridge",
                DataSourceKind.SmtcOnly => "smtc",
                _ => "auto",
            },
            OptionsProvider = () => new List<PluginOption>
            {
                new("auto", "自动（优先本地桥接，其次 SMTC）"),
                new("bridge", "只用本地桥接"),
                new("smtc", "只用 SMTC"),
            },
            Hint = "网易云不通过 SMTC 上报播放进度，所以想精确对齐歌词，需要「本地桥接」：网易云里装 InfLink + 桥接插件，把进度发到本机端口。",
            Changed = field =>
            {
                _settings.DataSource = field.SelectedKey switch
                {
                    "bridge" => DataSourceKind.BridgeOnly,
                    "smtc" => DataSourceKind.SmtcOnly,
                    _ => DataSourceKind.Auto,
                };
                SaveSettings();

                // 换数据源要重建接收端/引擎
                StopLyrics();
                StartLyrics();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "bridgeport",
            Label = "桥接端口",
            Kind = PluginFieldKind.Text,
            Text = _settings.BridgePort.ToString(),
            Hint = "InfLink 桥接插件默认发往 27431，和参考程序用的是同一个端口。改完按回车生效（会重启接收端）。",
            Changed = field =>
            {
                if (int.TryParse(field.Text?.Trim(), out var port) && port is > 0 and < 65536)
                {
                    _settings.BridgePort = port;
                    SaveSettings();
                    StopLyrics();
                    StartLyrics();
                }
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "enabled",
            Label = "显示歌词窗",
            Kind = PluginFieldKind.Toggle,
            Bool = _settings.Enabled,
            Hint = "关掉就整个停掉（不动系统里的东西，下次打开继续用）。",
            Changed = field =>
            {
                _settings.Enabled = field.Bool;
                SaveSettings();
                if (field.Bool)
                {
                    StartLyrics();
                }
                else
                {
                    StopLyrics();
                    _status = "已关闭";
                }
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "lines",
            Label = "显示行数",
            Kind = PluginFieldKind.Select,
            SelectedKey = _settings.ShowNextLine ? "double" : "single",
            OptionsProvider = () => new List<PluginOption>
            {
                new("single", "单行（只显示当前句）"),
                new("double", "双行（当前句 + 下一句预告）"),
            },
            Hint = "双行会多占一点高度，唱歌时能提前看到下一句。",
            Changed = field =>
            {
                _settings.ShowNextLine = field.SelectedKey == "double";
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "translation",
            Label = "显示官方翻译",
            Kind = PluginFieldKind.Toggle,
            Bool = _settings.ShowTranslation,
            Hint = "日文/英文歌会在当前句下面多显示一行中文翻译（不是每首都有）。",
            Changed = field =>
            {
                _settings.ShowTranslation = field.Bool;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "trackinfo",
            Label = "显示歌名歌手",
            Kind = PluginFieldKind.Toggle,
            Bool = _settings.ShowTrackInfo,
            Changed = field =>
            {
                _settings.ShowTrackInfo = field.Bool;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "fontsize",
            Label = "字号",
            Kind = PluginFieldKind.Slider,
            Number = _settings.FontSize,
            Minimum = 14,
            Maximum = 72,
            Step = 1,
            Suffix = " px",
            Changed = field =>
            {
                _settings.FontSize = field.Number;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "bold",
            Label = "粗体",
            Kind = PluginFieldKind.Toggle,
            Bool = _settings.Bold,
            Changed = field =>
            {
                _settings.Bold = field.Bool;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "textcolor",
            Label = "文字颜色",
            Kind = PluginFieldKind.Text,
            Text = _settings.TextColor,
            Hint = "写 #RRGGBB 或 #AARRGGBB，比如 #FFFFFFFF（不透明白）。改完按回车生效。",
            Changed = field =>
            {
                _settings.TextColor = SafeColor(field.Text, "#FFFFFFFF");
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "shadowcolor",
            Label = "影子颜色",
            Kind = PluginFieldKind.Text,
            Text = _settings.ShadowColor,
            Hint = "影子就是主文字后面那层同款文字，颜色 #RRGGBB。默认纯黑。",
            Changed = field =>
            {
                _settings.ShadowColor = SafeColor(field.Text, "#FF000000");
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "shadowopacity",
            Label = "影子不透明度",
            Kind = PluginFieldKind.Slider,
            Number = _settings.ShadowOpacity,
            Minimum = 0,
            Maximum = 255,
            Step = 5,
            Changed = field =>
            {
                _settings.ShadowOpacity = field.Number;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "shadowblur",
            Label = "影子模糊",
            Kind = PluginFieldKind.Slider,
            Number = _settings.ShadowBlur,
            Minimum = 0,
            Maximum = 24,
            Step = 0.5,
            Hint = "0 是硬边描边感（参考项目那种），往大调越来越柔。",
            Changed = field =>
            {
                _settings.ShadowBlur = field.Number;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "shadowoffsetx",
            Label = "影子水平偏移",
            Kind = PluginFieldKind.Slider,
            Number = _settings.ShadowOffsetX,
            Minimum = -12,
            Maximum = 12,
            Step = 0.5,
            Suffix = " px",
            Changed = field =>
            {
                _settings.ShadowOffsetX = field.Number;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "shadowoffsety",
            Label = "影子垂直偏移",
            Kind = PluginFieldKind.Slider,
            Number = _settings.ShadowOffsetY,
            Minimum = -12,
            Maximum = 12,
            Step = 0.5,
            Suffix = " px",
            Changed = field =>
            {
                _settings.ShadowOffsetY = field.Number;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "bgopacity",
            Label = "底色不透明度",
            Kind = PluginFieldKind.Slider,
            Number = _settings.BackgroundOpacity,
            Minimum = 0,
            Maximum = 255,
            Step = 5,
            Hint = "0 = 完全透明（只有字和影子，推荐）。想要「字幕条」的感觉就往大调。",
            Changed = field =>
            {
                _settings.BackgroundOpacity = field.Number;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "locked",
            Label = "锁定（鼠标穿透）",
            Kind = PluginFieldKind.Toggle,
            Bool = _settings.Locked,
            Hint = "锁上之后点不到歌词窗，鼠标会穿过去给游戏；解锁后可以用鼠标把它拖到任意位置。",
            Changed = field =>
            {
                _settings.Locked = field.Bool;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "hidewhenpaused",
            Label = "暂停时隐藏",
            Kind = PluginFieldKind.Toggle,
            Bool = _settings.HideWhenPaused,
            Changed = field =>
            {
                _settings.HideWhenPaused = field.Bool;
                ApplySettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "offset",
            Label = "歌词提前量",
            Kind = PluginFieldKind.Slider,
            Number = _settings.LyricOffsetMs,
            Minimum = -1000,
            Maximum = 1000,
            Step = 10,
            Suffix = " ms",
            Hint = "觉得歌词慢半拍就往正数调，觉得抢拍就往负数调。",
            Changed = field =>
            {
                _settings.LyricOffsetMs = field.Number;
                SaveSettings();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "source",
            Label = "跟随哪个播放器",
            Kind = PluginFieldKind.Select,
            SelectedKey = string.IsNullOrWhiteSpace(_settings.PreferredSource) ? AutoSourceKey : _settings.PreferredSource,
            OptionsRefreshSeconds = 5,
            OptionsProvider = BuildSourceOptions,
            Hint = "默认优先网易云；同时开着别的播放器时可以在这里指定。",
            Changed = field =>
            {
                _settings.PreferredSource = field.SelectedKey == AutoSourceKey ? "" : field.SelectedKey ?? "";
                SaveSettings();
                _engine?.ForceReload();
            },
        });

        page.Actions.Add(new PluginAction
        {
            Label = "重新载入歌词",
            Primary = true,
            Invoke = () =>
            {
                _engine?.ForceReload();
                _status = "正在重新载入歌词…";
                return null;
            },
        });

        page.Actions.Add(new PluginAction
        {
            Label = "重置窗口位置",
            Invoke = () =>
            {
                _window?.ResetPosition();
                SaveSettings();
                return "歌词窗已回到屏幕底部居中";
            },
        });

        page.Actions.Add(new PluginAction
        {
            Label = "清空歌词缓存",
            Invoke = () =>
            {
                var count = _client?.ClearCache() ?? 0;
                return $"已清掉 {count} 个缓存文件，下次播放会重新联网取歌词";
            },
        });

        page.Actions.Add(new PluginAction
        {
            Label = "看看桥接到了什么",
            Invoke = ShowBridgeRaw,
        });

        page.Actions.Add(new PluginAction
        {
            Label = "测试读取",
            Invoke = () =>
            {
                _ = TestReadAsync();
                return null;
            },
        });

        return page;
    }

    /// <summary>把桥接最新一帧的内容摊开给用户看（排查"到底有没有收到数据、字段对不对"）。</summary>
    private string? ShowBridgeRaw()
    {
        var bridge = _bridge;
        if (bridge is null)
        {
            return "桥接接收端没启动（数据源选了「只用 SMTC」，或者插件没在跑）";
        }

        if (!bridge.IsListening)
        {
            return $"桥接接收端起不来：{bridge.LastError}（端口可能被别的程序占了，比如你的 LyricShadow 参考程序）";
        }

        var payload = bridge.Latest;
        if (payload is null)
        {
            return $"在 127.0.0.1:{bridge.Port} 监听中，还没收到数据。检查：① 网易云装好 InfLink + 桥接插件并重启过 ② 网易云在放歌";
        }

        var detail = $"{payload.Title} — {payload.Artist}｜进度 {TimeSpan.FromMilliseconds(payload.PositionMs):mm\\:ss\\.fff}" +
                     $"/{TimeSpan.FromMilliseconds(payload.DurationMs):mm\\:ss}｜{(payload.Playing ? "播放中" : "已暂停")}" +
                     $"｜id {payload.SongId}｜{bridge.SecondsSinceLast:F1} 秒前（共 {bridge.ReceivedCount} 帧）";
        WriteLog("[桥接] " + detail + "｜原始：" + payload.Raw);
        _host?.ShowToast(detail);
        return null;
    }

    private IReadOnlyList<PluginOption> BuildSourceOptions()
    {
        var options = new List<PluginOption> { new(AutoSourceKey, "自动（优先网易云）") };

        foreach (var source in _smtc.ListSources())
        {
            options.Add(new PluginOption(source, source));
        }

        return options;
    }

    // ------------------------------------------------------------ 杂项

    private void ApplySettings()
    {
        SaveSettings();
        _window?.ApplySettings(_settings);
    }

    private void SaveSettings()
    {
        if (_host is not null)
        {
            PluginSettingsFile.Save(_host.PluginDirectory, _settings);
        }
    }

    private void WriteLog(string message) => _host?.Log("[网易云歌词] " + message);

    /// <summary>把"上次上报时间"说成人话（诊断用）。</summary>
    private static string DescribeAge(DateTimeOffset lastUpdated)
    {
        if (lastUpdated == default)
        {
            return "没提供（很多播放器都这样，插件会自动改用本地时钟走）";
        }

        var age = DateTimeOffset.Now - lastUpdated;
        return age.TotalSeconds < 0 ? "未来时间？" : $"{age.TotalSeconds:F1} 秒前";
    }

    private static string SafeColor(string input, string fallback)    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            return fallback;
        }

        try
        {
            return System.Windows.Media.ColorConverter.ConvertFromString(text) is System.Windows.Media.Color
                ? text
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private async Task TestReadAsync()
    {
        try
        {
            if (!await _smtc.ConnectAsync())
            {
                _host?.ShowToast("连不上系统媒体会话，请确认网易云设置里打开了「系统媒体控制」");
                return;
            }

            var snapshot = await _smtc.TryGetSnapshotAsync(_settings.PreferredSource);
            if (snapshot is null)
            {
                var sources = string.Join("、", _smtc.ListSources());
                var message = sources.Length == 0
                    ? "没读到任何媒体会话（网易云在放歌吗？SMTC 开了吗？）"
                    : "没读到播放中的歌曲。当前会话：" + sources;
                WriteLog(message);
                _host?.ShowToast(message);
                return;
            }

            var transport = _smtc.ReadTransport();
            var raw = transport.RawPosition == TimeSpan.Zero && transport.Duration == TimeSpan.Zero
                ? "（没读到进度）"
                : $"原始位置 {transport.RawPosition:mm\\:ss\\.ff} / {transport.Duration:mm\\:ss}，上报于 {DescribeAge(transport.LastUpdated)}，状态 {(transport.Playing ? "播放中" : "非播放")}，速率 {transport.Rate:0.##}" +
                  (transport.Error.Length > 0 ? $"，报错 {transport.Error}" : "");

            var text = $"{snapshot.Title} — {snapshot.Artist}｜{raw}｜引擎：{_engine?.DescribeTransport() ?? "未启动"}";
            WriteLog(text);
            _host?.ShowToast($"{snapshot.Title} — {snapshot.Artist}｜{raw}");
        }
        catch (Exception ex)
        {
            WriteLog("测试读取失败：" + ex);
            _host?.ShowToast("测试读取失败：" + ex.Message);
        }
    }
}
