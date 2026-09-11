using System.IO;
using System.Text;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// 持续读取 Minecraft latest.log 的新增行，过滤出 [CHAT] 聊天栏消息。
/// 支持日志文件被启动器删除/重建（例如游戏重启后 latest.log 重新生成）。
/// </summary>
public sealed class MinecraftLogWatcher : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource _cts = new();
    private readonly List<byte> _pendingBytes = new();

    private Thread? _thread;
    private FileStream? _stream;
    private FileSystemWatcher? _watcher;
    private string? _logPath;
    private string _encodingName = "Auto";
    private bool _reopenRequested;

    public event Action<string>? ChatLineReceived;
    public event Action<string>? StatusChanged;

    /// <summary>调试用：每当解码出一行包含 [CHAT] 的原始日志时触发（第一个参数是解码后的文本，第二个是原始字节 HEX）。</summary>
    public event Action<string, string>? DebugLineReceived;

    public bool IsRunning => _thread?.IsAlive == true;

    /// <summary>监听过程中切换日志编码，对后续新读取的行立即生效。</summary>
    public void UpdateEncoding(string encodingName)
    {
        Volatile.Write(ref _encodingName, string.IsNullOrWhiteSpace(encodingName) ? "Auto" : encodingName);
    }

    public void Start(string logPath, string encodingName = "Auto")
    {
        Stop();
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        _logPath = logPath;
        _encodingName = string.IsNullOrWhiteSpace(encodingName) ? "Auto" : encodingName;
        _pendingBytes.Clear();

        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "MinecraftLogWatcher"
        };
        _thread = thread;
        thread.Start();

        StartWatcher(logPath);
    }

    public void Stop()
    {
        _cts.Cancel();
        DisposeWatcher();
        CloseStream();
        if (_thread != null && _thread != Thread.CurrentThread)
        {
            try
            {
                _thread.Join(1000);
            }
            catch (ThreadStateException)
            {
            }
        }

        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    private void Loop()
    {
        NotifyStatus(Copy.ListeningStarted);

        var buffer = new byte[8192];
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                TryOpenStreamIfNeeded();
                CheckReopenIfNeeded();

                if (_stream == null)
                {
                    Thread.Sleep(300);
                    continue;
                }

                int read = _stream.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    ProcessBytes(buffer, read);
                }
                else
                {
                    CheckTruncatedOrRecreated();
                    Thread.Sleep(150);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                NotifyStatus(Copy.WatchError + ex.Message);
                CloseStream();
                Thread.Sleep(500);
            }
        }

        CloseStream();
    }

    private void TryOpenStreamIfNeeded()
    {
        if (_stream != null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_logPath))
        {
            NotifyStatus(Copy.NoLogPathSet);
            Thread.Sleep(300);
            return;
        }

        if (!File.Exists(_logPath))
        {
            NotifyStatus(Copy.WaitingForLog + _logPath);
            return;
        }

        try
        {
            _stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _stream.Seek(0, SeekOrigin.End);
            _pendingBytes.Clear();
            NotifyStatus(Copy.ListeningTo + _logPath);
        }
        catch (Exception ex)
        {
            NotifyStatus(Copy.CannotOpenLog + ex.Message);
        }
    }

    private void ProcessBytes(byte[] buffer, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _pendingBytes.Add(buffer[i]);
        }

        var lineStart = 0;
        for (var i = 0; i < _pendingBytes.Count; i++)
        {
            if (_pendingBytes[i] == (byte)'\n')
            {
                var length = i - lineStart;
                if (length > 0)
                {
                    var lineBytes = new byte[length];
                    _pendingBytes.CopyTo(lineStart, lineBytes, 0, length);

                    // 去掉 Windows 的 \r。
                    if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
                    {
                        lineBytes = lineBytes[..^1];
                    }

                    HandleLine(lineBytes);
                }

                lineStart = i + 1;
            }
        }

        if (lineStart > 0)
        {
            _pendingBytes.RemoveRange(0, lineStart);
        }

        // 防止极端情况下某行没有换行符且无限增长。
        if (_pendingBytes.Count > 1024 * 1024)
        {
            _pendingBytes.Clear();
        }
    }

    private void HandleLine(byte[] lineBytes)
    {
        if (lineBytes.Length == 0)
        {
            return;
        }

        var line = LogTextDecoder.Decode(lineBytes, Volatile.Read(ref _encodingName));
        if (line.Contains("[CHAT]", StringComparison.Ordinal))
        {
            DebugLineReceived?.Invoke(line, Convert.ToHexString(lineBytes));
        }

        var chat = ChatLineParser.TryParseChatLine(line);
        if (chat != null)
        {
            ChatLineReceived?.Invoke(chat);
        }
    }

    private void StartWatcher(string logPath)
    {
        DisposeWatcher();
        try
        {
            var fullPath = Path.GetFullPath(logPath);
            var directory = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            _watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Created += (_, _) => RequestReopen();
            _watcher.Renamed += (_, _) => RequestReopen();
            _watcher.Deleted += (_, _) => RequestReopen();
            _watcher.Error += (_, _) => { /* watcher 内部错误忽略，主循环仍会轮询 */ };
        }
        catch
        {
            // 监听不到文件系统事件也没关系，轮询逻辑仍然可用。
        }
    }

    private void RequestReopen()
    {
        lock (_sync)
        {
            _reopenRequested = true;
        }
    }

    private void CheckReopenIfNeeded()
    {
        lock (_sync)
        {
            if (_reopenRequested)
            {
                _reopenRequested = false;
                CloseStream();
                NotifyStatus(Copy.LogChanged);
            }
        }
    }

    private void CheckTruncatedOrRecreated()
    {
        if (_stream == null || string.IsNullOrEmpty(_logPath))
        {
            return;
        }

        if (!File.Exists(_logPath))
        {
            CloseStream();
            NotifyStatus(Copy.LogGone);
            return;
        }

        try
        {
            var currentLength = new FileInfo(_logPath).Length;
            if (currentLength < _stream.Length)
            {
                CloseStream();
                NotifyStatus(Copy.LogRotated);
            }
        }
        catch
        {
            // 文件可能正被占用或短暂消失，交给下一轮处理。
        }
    }

    private void CloseStream()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
        }

        _stream = null;
    }

    private void DisposeWatcher()
    {
        try
        {
            _watcher?.Dispose();
        }
        catch
        {
        }

        _watcher = null;
    }

    private void NotifyStatus(string message)
    {
        StatusChanged?.Invoke(message);
    }
}
