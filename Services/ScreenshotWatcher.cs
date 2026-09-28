using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// 盯着截图目录，一有新截图就报出来。
/// 注意：FileSystemWatcher 在文件刚创建时就会触发，那时图片往往还没写完，
/// 所以这里会等它"能打开且大小不再变化"之后才报出去。
/// </summary>
public sealed class ScreenshotWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 发现一张已经写好的新截图（参数是完整路径）。
    /// 注意：这是在后台线程上触发的，订阅方要自己切回 UI 线程再动控件。
    /// </summary>
    public event Action<string>? NewScreenshot;

    public string Directory { get; private set; } = "";

    public bool IsRunning => _watcher is not null;

    public void Start(string directory)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
        {
            return;
        }

        Directory = directory;
        _handled.Clear();

        // 已经存在的文件不处理，只处理启动之后新出现的
        foreach (var existing in System.IO.Directory.EnumerateFiles(directory, "*.png"))
        {
            _handled.Add(existing);
        }

        _watcher = new FileSystemWatcher(directory, "*.png")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Created += (_, e) => _ = WaitAndReportAsync(e.FullPath);
        _watcher.Renamed += (_, e) => _ = WaitAndReportAsync(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <summary>等文件写完（最长约 6 秒），再报出去。同一张图只报一次。</summary>
    private async Task WaitAndReportAsync(string path)
    {
        if (_handled.Contains(path))
        {
            return;
        }

        long lastSize = -1;
        for (var i = 0; i < 20; i++)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0 && info.Length == lastSize)
                {
                    // 连续两次大小一致，认为写完了
                    if (_handled.Add(path))
                    {
                        NewScreenshot?.Invoke(path);
                    }

                    return;
                }

                lastSize = info.Exists ? info.Length : -1;
            }
            catch
            {
                // 文件还在被占用，继续等
            }

            await Task.Delay(300).ConfigureAwait(false);
        }

        // 等超时了也报一次，让上层自己决定
        if (_handled.Add(path))
        {
            NewScreenshot?.Invoke(path);
        }
    }

    public void Dispose() => Stop();
}
