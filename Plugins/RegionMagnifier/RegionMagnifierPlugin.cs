using System.Windows;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Plugins.RegionMagnifier;

/// <summary>
/// 「区域放大」——原本是主程序里的内置功能，现在是一个标准插件：
/// 框选游戏画面里的一小块（比如物品栏最后一格），放大后钉在屏幕某个位置。
///
/// 它用到了插件平台的大部分能力：注册导航页（表单由宿主渲染）、枚举游戏窗口、
/// 自己开一个置顶窗口（RegionMagnifierWindow）、把自己的配置存进插件数据目录。
/// </summary>
public sealed class RegionMagnifierPlugin : IPlugin
{
    private IPluginHost _host = null!;
    private RegionMagnifierSettings _settings = new();
    private RegionMagnifierWindow? _window;

    public string Id => "goldiamond.regionmagnifier";

    public string DisplayName => "区域放大";

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = PluginSettingsFile.Load<RegionMagnifierSettings>(host.PluginDirectory);

        var page = new PluginPage
        {
            Title = DisplayName,
            Description = "框选游戏画面里的一小块（比如物品栏最后一格），放大后钉在屏幕某个位置。抓的是屏幕画面，游戏本身不改动。",
            StatusText = BuildStatus,
        };

        page.Fields.Add(new PluginField
        {
            Key = "target",
            Label = "游戏窗口",
            Kind = PluginFieldKind.Select,
            OptionsProvider = BuildTargetOptions,
            OptionsRefreshSeconds = 3,
            SelectedKey = TargetKey(_settings.TargetProcessName, _settings.TargetTitleHint),
            Hint = "列表里是当前可见的游戏类窗口。游戏重启换了 PID 也没关系，会按进程名重新找回来。",
            Changed = field =>
            {
                var (processName, _) = SplitKey(field.SelectedKey);
                _settings.TargetProcessName = processName;

                // 标题只当提示用：游戏标题会随"单人游戏/多人游戏"变，留空免得下次找不到窗口
                _settings.TargetTitleHint = "";
                Save();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "zoom",
            Label = "放大倍数",
            Kind = PluginFieldKind.Slider,
            Number = _settings.Zoom,
            Minimum = 1,
            Maximum = 8,
            Step = 0.5,
            Suffix = "×",
            Hint = "显示窗口大小 = 选区大小 × 倍数 ÷ 屏幕缩放。",
            Changed = field =>
            {
                _settings.Zoom = field.Number;
                SaveAndApply();
            },
        });

        page.Fields.Add(new PluginField
        {
            Key = "fps",
            Label = "每秒帧数",
            Kind = PluginFieldKind.Slider,
            Number = _settings.Fps,
            Minimum = 5,
            Maximum = 60,
            Step = 5,
            Suffix = " fps",
            Hint = "抓取上限，抓不动会自然掉帧。",
            Changed = field =>
            {
                _settings.Fps = (int)Math.Round(field.Number);
                SaveAndApply();
            },
        });

        page.Fields.Add(MakeToggle("relative", "选区跟着游戏窗口走", _settings.RelativeToWindow,
            "勾着：选区记成游戏客户区里的相对位置，游戏窗口挪了、换分辨率了也还对着同一块画面。",
            value =>
            {
                ConvertRegionForRelativeMode(value);
                _settings.RelativeToWindow = value;
                SaveAndApply();
            }));

        page.Fields.Add(MakeToggle("clickThrough", "点击穿透", _settings.ClickThrough,
            "勾着：鼠标点放大窗等于点到下面的游戏；这时候就不能拖动放大窗了。",
            value =>
            {
                _settings.ClickThrough = value;
                SaveAndApply();
            }));

        page.Fields.Add(MakeToggle("smooth", "平滑放大", _settings.SmoothScale,
            "放大倍数高的时候平滑更好看，但对像素游戏（比如物品栏格子）最近邻更清楚。",
            value =>
            {
                _settings.SmoothScale = value;
                SaveAndApply();
            }));

        page.Fields.Add(MakeToggle("border", "显示边框", _settings.ShowBorder,
            null,
            value =>
            {
                _settings.ShowBorder = value;
                SaveAndApply();
            }));

        page.Fields.Add(MakeToggle("excludeCapture", "把自己排除在抓屏之外", _settings.ExcludeFromCapture,
            "默认开着：防止放大窗和选区重叠时自己拍自己。要是用别的录屏/截图软件看不到放大窗，把这个关掉。",
            value =>
            {
                _settings.ExcludeFromCapture = value;
                SaveAndApply();
            }));

        page.Actions.Add(new PluginAction
        {
            Label = "框选要放大的区域",
            Primary = true,
            Invoke = SelectRegion,
        });

        page.Actions.Add(new PluginAction
        {
            Label = "显示 / 隐藏放大窗",
            Invoke = ToggleWindow,
        });

        page.Actions.Add(new PluginAction
        {
            Label = "摆到右上角",
            Invoke = MoveToTopRight,
        });

        host.RegisterPage(page);
        host.Log($"[区域放大] 已加载，数据目录：{host.PluginDirectory}");

        // 上次退出时放大窗是开着的，就自动开回来
        if (_settings.Enabled && _settings.RegionWidth > 0 && _settings.RegionHeight > 0)
        {
            ShowWindow();
        }
    }

    public void Shutdown()
    {
        try
        {
            _window?.Close();
        }
        catch
        {
            // 关不掉就算了，宿主退出时会一并收掉
        }

        _window = null;
    }

    // ------------------------------------------------------------------ 页面用的小工具

    private static PluginField MakeToggle(string key, string label, bool value, string? hint, Action<bool> changed)
        => new()
        {
            Key = key,
            Label = label,
            Kind = PluginFieldKind.Toggle,
            Bool = value,
            Hint = hint,
            Changed = field => changed(field.Bool),
        };

    private string BuildStatus()
    {
        var parts = new List<string>();

        parts.Add(string.IsNullOrWhiteSpace(_settings.TargetProcessName)
            ? "目标：未选择"
            : $"目标：{_settings.TargetProcessName}");

        parts.Add(_settings.RegionWidth > 0
            ? $"选区 {_settings.RegionWidth}×{_settings.RegionHeight}" + (_settings.RelativeToWindow ? "（相对客户区）" : "（屏幕坐标）")
            : "选区：还没框选");

        parts.Add(_window is { IsVisible: true }
            ? "放大窗：" + _window.StatusText
            : "放大窗：未显示");

        return string.Join("  ·  ", parts);
    }

    /// <summary>候选窗口 → 下拉项。key 里带上进程名，方便记回设置。</summary>
    private IReadOnlyList<PluginOption> BuildTargetOptions()
    {
        var options = new List<PluginOption>();
        try
        {
            foreach (var window in _host.GetGameWindows())
            {
                options.Add(new PluginOption(TargetKey(window.ProcessName, window.Title), window.ToString()));
            }
        }
        catch
        {
            // 枚举失败就当没有
        }

        return options;
    }

    private static string TargetKey(string processName, string title)
    {
        processName ??= "";
        title ??= "";

        // 选中项要把"进程 + 标题"都记住，才能在一堆同名窗口里认准那一个
        return string.IsNullOrWhiteSpace(processName) ? "" : processName + "|" + title;
    }

    private static (string ProcessName, string Title) SplitKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return ("", "");
        }

        var index = key.IndexOf('|');
        return index < 0 ? (key, "") : (key[..index], key[(index + 1)..]);
    }

    private void Save() => PluginSettingsFile.Save(_host.PluginDirectory, _settings);

    /// <summary>存盘并让放大窗立刻按新设置重排。</summary>
    private void SaveAndApply()
    {
        Save();

        try
        {
            _window?.ApplySettings();
        }
        catch
        {
            // 窗口不在就无所谓
        }
    }

    /// <summary>
    /// "选区相对客户区"这个开关单独处理：顺手把已有坐标换算过去，
    /// 不然切换之后抓的位置会整体偏一个客户区原点。
    /// </summary>
    private void ConvertRegionForRelativeMode(bool wantRelative)
    {
        if (wantRelative == _settings.RelativeToWindow || _settings.RegionWidth <= 0 || _settings.RegionHeight <= 0)
        {
            return;
        }

        try
        {
            var handle = RegionWindowFinder.ResolveTarget(_settings.TargetProcessName, _settings.TargetTitleHint, out _);
            if (handle != IntPtr.Zero
                && RegionWindowFinder.TryGetClientRectOnScreen(handle, out var clientX, out var clientY, out _, out _))
            {
                if (wantRelative)
                {
                    _settings.RegionX -= clientX;
                    _settings.RegionY -= clientY;
                }
                else
                {
                    _settings.RegionX += clientX;
                    _settings.RegionY += clientY;
                }
            }
        }
        catch
        {
            // 换算不了就保持原样，用户重新框一次即可
        }
    }

    // ------------------------------------------------------------------ 框选与显示窗

    /// <summary>开全屏选择层框一块；结果按设置换算成"相对客户区"存起来。</summary>
    private string? SelectRegion()
    {
        var handle = RegionWindowFinder.ResolveTarget(_settings.TargetProcessName, _settings.TargetTitleHint, out _);

        RegionSelectorWindow selector;
        try
        {
            selector = new RegionSelectorWindow(handle)
            {
                Owner = Application.Current?.MainWindow,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
            };
        }
        catch (Exception ex)
        {
            return "打开框选层失败：" + ex.Message;
        }

        if (selector.ShowDialog() != true)
        {
            return "已取消框选";
        }

        var storedRelative = false;

        if (_settings.RelativeToWindow && handle != IntPtr.Zero
            && RegionWindowFinder.TryGetClientRectOnScreen(handle, out var clientX, out var clientY, out var clientW, out var clientH))
        {
            // 整块都在客户区里才存相对坐标，否则退回绝对坐标，免得抓出一堆黑边
            if (selector.ResultX >= clientX && selector.ResultY >= clientY
                && selector.ResultX + selector.ResultWidth <= clientX + clientW
                && selector.ResultY + selector.ResultHeight <= clientY + clientH)
            {
                _settings.RegionX = selector.ResultX - clientX;
                _settings.RegionY = selector.ResultY - clientY;
                storedRelative = true;
            }
        }

        if (!storedRelative)
        {
            _settings.RegionX = selector.ResultX;
            _settings.RegionY = selector.ResultY;
        }

        _settings.RegionWidth = selector.ResultWidth;
        _settings.RegionHeight = selector.ResultHeight;
        Save();

        ShowWindow();
        return storedRelative ? "已记下选区（相对游戏客户区）" : "已记下选区（绝对屏幕坐标）";
    }

    private void ShowWindow()
    {
        try
        {
            if (_window is null)
            {
                var window = new RegionMagnifierWindow(_settings);
                window.Moved += (left, top) =>
                {
                    _settings.DisplayLeft = left;
                    _settings.DisplayTop = top;
                    Save();
                };

                _window = window;
            }

            _settings.Enabled = true;
            _window.PositionFromSettings();
            _window.ApplySettings();

            if (!_window.IsVisible)
            {
                _window.Show();
            }

            _window.ReassertTopmost();
            Save();
        }
        catch (Exception ex)
        {
            _host.Log("[区域放大] 显示放大窗失败：" + ex.Message);
        }
    }

    private void HideWindow()
    {
        _settings.Enabled = false;
        _window?.Hide();
        Save();
    }

    private string? ToggleWindow()
    {
        if (_window is { IsVisible: true })
        {
            HideWindow();
            return "已隐藏放大窗";
        }

        ShowWindow();
        return "已显示放大窗";
    }

    private string? MoveToTopRight()
    {
        try
        {
            if (_window is null)
            {
                ShowWindow();
            }

            _window?.MoveToTopRight();

            if (_window is not null)
            {
                var bounds = _window.Bounds;
                _settings.DisplayLeft = bounds.Left;
                _settings.DisplayTop = bounds.Top;
                Save();
            }

            return "已摆到右上角";
        }
        catch (Exception ex)
        {
            return "挪到右上角失败：" + ex.Message;
        }
    }
}
