using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Services.Plugins;

/// <summary>
/// 插件总管：扫描目录、校验、装载、隔离、投递消息、导入/禁用/卸载。
///
/// 任何一步出错都只影响那个插件（状态标成 Failed 并把原因写下来），主程序照常跑。
/// </summary>
public sealed class PluginManager
{
    private const string ManifestFileName = "plugin.json";
    private const string AbstractionsAssemblyName = "MinecraftChatOverlay.Plugin.Abstractions";
    private const string PendingDeleteFileName = "pending-delete.txt";

    private readonly List<PluginEntry> _entries = new();
    private readonly LinkedList<ChatLine> _recentChatLines = new();
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public PluginManager(string portableRoot, string userRoot, IEnumerable<string> disabledIds)
    {
        PortableRoot = portableRoot;
        UserRoot = userRoot;
        foreach (var id in disabledIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                _disabled.Add(id.Trim());
            }
        }
    }

    /// <summary>程序目录旁边的 plugins\（随程序发布的内置插件）。</summary>
    public string PortableRoot { get; }

    /// <summary>用户目录里的 plugins\（导入安装的插件）。</summary>
    public string UserRoot { get; }

    public string PluginsRootDirectory => UserRoot;

    /// <summary>
    /// 插件的数据目录（配置、缓存、图片……）。
    /// 特意和"插件安装目录"分开：更新换版会整个替换安装目录，用户配置不能跟着没。
    /// </summary>
    public string DataRoot => Path.Combine(Path.GetDirectoryName(UserRoot) ?? UserRoot, "plugin-data");

    /// <summary>拿某个插件的数据目录（不存在就建）。</summary>
    internal string GetPluginDataDirectory(PluginEntry entry)
    {
        var directory = Path.Combine(DataRoot, SafeFolderName(entry.Manifest.Id));
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // 建不出来也把路径给插件，让它自己报错
        }

        return directory;
    }

    /// <summary>插件列表或状态变了（界面据此重画）。</summary>
    public event Action? Changed;

    /// <summary>插件写的日志（接到宿主的调试后台）。</summary>
    public event Action<string>? PluginLog;

    /// <summary>插件要往聊天悬浮窗发消息。</summary>
    public event Action<PluginEntry, string>? PluginOverlayMessage;

    /// <summary>插件请求把聊天悬浮窗重新抬到最前面（「窗口全屏」铺满游戏窗口后会用）。</summary>
    public event Action? PluginOverlayTopmostRequested;

    /// <summary>插件弹的提示。</summary>
    public event Action<string>? PluginToast;

    public IReadOnlyList<PluginEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    public IReadOnlyList<ChatLine> RecentChatLines
    {
        get
        {
            lock (_lock)
            {
                return _recentChatLines.ToList();
            }
        }
    }

    /// <summary>当前被禁用的插件 id（存进设置用）。</summary>
    public IReadOnlyList<string> DisabledIds
    {
        get
        {
            lock (_lock)
            {
                return _disabled.ToList();
            }
        }
    }

    // ---------------------------------------------------------------- 扫描与装载

    /// <summary>两个插件根目录都扫一遍，能装的都装上。</summary>
    public void LoadAll()
    {
        ProcessPendingChanges();

        lock (_lock)
        {
            _entries.Clear();
        }

        foreach (var (root, source) in EnumerateRoots())
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var directory in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var manifestPath = Path.Combine(directory, ManifestFileName);
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    var manifest = PluginManifest.TryLoad(manifestPath);
                    if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                    {
                        Add(new PluginEntry
                        {
                            Manifest = manifest ?? new PluginManifest { Id = Path.GetFileName(directory), Name = Path.GetFileName(directory) },
                            Directory = directory,
                            State = PluginState.Failed,
                            Error = "plugin.json 读不出来（格式不对？）",
                        });
                        continue;
                    }

                    // 同名插件以用户目录里的为准
                    lock (_lock)
                    {
                        if (_entries.Any(e => string.Equals(e.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                    }

                    var entry = new PluginEntry { Manifest = manifest, Directory = directory };
                    if (_disabled.Contains(manifest.Id))
                    {
                    // 文件删不掉是 dll 被运行时锁着，那是宿主自己的事，不该让用户"卸载完还要重启才看不到它"：
                    // 直接把条目从列表里摘掉（界面上立刻消失），目录记进 pending-delete，下次启动自动清。
                        entry.Error = "被用户禁用";
                        Add(entry);
                        continue;
                    }

                    Load(entry);
                    Add(entry);
                }
            }
            catch (Exception ex)
            {
                RaiseLog($"[插件] 扫描 {source} 目录时出错：{ex.Message}");
            }
        }

        RaiseChanged();
    }

    private IEnumerable<(string Root, string Source)> EnumerateRoots()
    {
        yield return (PortableRoot, "程序目录");
        yield return (UserRoot, "用户目录");
    }

    private void Add(PluginEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>装载一个插件。出错就标 Failed，不抛。</summary>
    internal void Load(PluginEntry entry)
    {
        entry.State = PluginState.Failed;
        entry.Error = null;
        entry.Instance = null;
        entry.Page = null;

        var manifest = entry.Manifest;

        if (manifest.ApiVersion != PluginApi.Version)
        {
            entry.Error = $"这个插件要求契约版本 {manifest.ApiVersion}，本程序是 {PluginApi.Version}（版本对不上，拒装）";
            RaiseLog($"[插件] {manifest.Id} 装载失败：{entry.Error}");
            return;
        }

        var dllPath = ResolveAssemblyPath(entry);
        if (dllPath is null)
        {
            entry.Error = "插件目录里找不到 dll";
            RaiseLog($"[插件] {manifest.Id} 装载失败：{entry.Error}");
            return;
        }

        try
        {
            var context = new PluginLoadContext(entry.Directory, manifest.Id);
            var assembly = context.LoadFromAssemblyPath(dllPath);
            var type = ResolvePluginType(assembly, manifest.Entry);
            if (type is null)
            {
                entry.Error = "dll 里没有找到实现 IPlugin 的公开类（会自动扫描，也可以在 plugin.json 里写 entry）";
                context.Unload();
                RaiseLog($"[插件] {manifest.Id} 装载失败：{entry.Error}");
                return;
            }

            if (Activator.CreateInstance(type) is not IPlugin instance)
            {
                entry.Error = "插件入口类没法实例化（需要公开无参构造函数）";
                context.Unload();
                RaiseLog($"[插件] {manifest.Id} 装载失败：{entry.Error}");
                return;
            }

            var host = new PluginHostContext(this, entry);
            entry.LoadContext = context;
            entry.Context = host;
            entry.Instance = instance;

            instance.Initialize(host);

            entry.State = PluginState.Loaded;
            RaiseLog($"[插件] 已装载 {manifest.Describe()}（{manifest.Id}）");
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
            entry.State = PluginState.Failed;
            entry.Instance = null;
            RaiseLog($"[插件] {manifest.Id} 装载失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ResolveAssemblyPath(PluginEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Manifest.Assembly))
        {
            var explicitPath = Path.Combine(entry.Directory, entry.Manifest.Assembly!);
            if (File.Exists(explicitPath))
            {
                return explicitPath;
            }
        }

        return Directory.GetFiles(entry.Directory, "*.dll")
            .Where(f => !string.Equals(Path.GetFileNameWithoutExtension(f), AbstractionsAssemblyName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static Type? ResolvePluginType(Assembly assembly, string? entry)
    {
        if (!string.IsNullOrWhiteSpace(entry))
        {
            var named = assembly.GetType(entry!, throwOnError: false);
            if (named is not null)
            {
                return named;
            }
        }

        return assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true })
            .FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t));
    }

    /// <summary>停掉一个插件（禁用/卸载/退出都走这里）。</summary>
    internal void Unload(PluginEntry entry)
    {
        try
        {
            entry.Instance?.Shutdown();
        }
        catch (Exception ex)
        {
            RaiseLog($"[插件] {entry.Manifest.Id} 关闭时出错：{ex.Message}");
        }

        try
        {
            entry.Instance = null;
            entry.Page = null;
            entry.Context = null;
            entry.LoadContext?.Unload();
            entry.LoadContext = null;
        }
        catch
        {
            // 卸载失败就下次启动再说
        }
    }

    // ---------------------------------------------------------------- 消息总线

    /// <summary>宿主收到一条聊天消息：存进最近记录，再投给每个插件。</summary>
    public void OnChatLine(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        var line = new ChatLine(raw, DateTime.Now);
        lock (_lock)
        {
            _recentChatLines.AddLast(line);
            while (_recentChatLines.Count > 200)
            {
                _recentChatLines.RemoveFirst();
            }
        }

        foreach (var entry in Entries)
        {
            if (entry.State == PluginState.Loaded)
            {
                entry.Context?.RaiseChatLine(line);
            }
        }
    }

    // ---------------------------------------------------------------- 导入 / 启用禁用 / 卸载

    /// <summary>
    /// 导入一个插件 zip。成功返回装好的插件，失败返回 null（原因在 message 里）。
    /// needConfirm 用来让界面先弹一个"这个插件会做这些事"的确认框。
    /// </summary>
    public PluginEntry? ImportZip(string zipPath, Func<PluginManifest, bool>? needConfirm, out string message)
    {
        message = "";
        string? tempDirectory = null;

        try
        {
            if (!File.Exists(zipPath))
            {
                message = "找不到这个文件。";
                return null;
            }

            tempDirectory = Path.Combine(Path.GetTempPath(), "mco-plugin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            ExtractZipSafely(zipPath, tempDirectory);

            var manifestPath = FindManifestPath(tempDirectory);
            if (manifestPath is null)
            {
                message = "这个 zip 里没有 plugin.json（它可能不是插件包）。";
                return null;
            }

            var manifest = PluginManifest.TryLoad(manifestPath);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                message = "plugin.json 读不出来或者缺 id。";
                return null;
            }

            if (manifest.ApiVersion != PluginApi.Version)
            {
                message = $"版本对不上：这个插件要求契约版本 {manifest.ApiVersion}，本程序是 {PluginApi.Version}。请更新插件或更新软件。";
                return null;
            }

            if (needConfirm is not null && !needConfirm(manifest))
            {
                message = "已取消安装。";
                return null;
            }

            var pluginRoot = Path.GetDirectoryName(manifestPath)!;

            // 已经装着同名插件：先卸掉再覆盖
            var existing = Entries.FirstOrDefault(e => string.Equals(e.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.State == PluginState.Loaded)
                {
                    Unload(existing);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                try
                {
                    if (Directory.Exists(existing.Directory))
                    {
                        Directory.Delete(existing.Directory, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        StageUpdate(pluginRoot, manifest.Id, out var staged);
                        message = $"这个插件正在运行，dll 被占用删不掉（{ex.Message}）。已经把新版本排进队列（{Path.GetFileName(staged)}），重启软件后自动换上去。";
                    }
                    catch (Exception ex2)
                    {
                        message = $"旧版本文件删不掉（{ex.Message}），排队也没成功（{ex2.Message}）。";
                    }

                    return null;
                }

                lock (_lock)
                {
                    _entries.Remove(existing);
                }
            }

            var targetDirectory = Path.Combine(UserRoot, SafeFolderName(manifest.Id));
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }

            CopyDirectory(pluginRoot, targetDirectory);
            UnblockFiles(targetDirectory);

            var entry = new PluginEntry { Manifest = manifest, Directory = targetDirectory };
            lock (_lock)
            {
                _disabled.Remove(manifest.Id);
            }

            Load(entry);
            Add(entry);
            RaiseChanged();

            message = entry.State == PluginState.Loaded
                ? $"已安装 {manifest.Describe()}"
                : $"已安装，但装载失败：{entry.Error}";
            return entry;
        }
        catch (Exception ex)
        {
            message = "导入失败：" + ex.Message;
            return null;
        }
        finally
        {
            if (tempDirectory is not null)
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch
                {
                    // 清不掉就算了
                }
            }
        }
    }

    /// <summary>启用 / 禁用插件。</summary>
    public bool SetEnabled(PluginEntry entry, bool enabled, out string message)
    {
        message = "";
        try
        {
            if (enabled)
            {
                lock (_lock)
                {
                    _disabled.Remove(entry.Manifest.Id);
                }

                Load(entry);
                message = entry.State == PluginState.Loaded ? $"已启用 {entry.DisplayName}" : $"启用失败：{entry.Error}";
                RaiseChanged();
                return entry.State == PluginState.Loaded;
            }

            Unload(entry);
                    // 文件删不掉是 dll 被运行时锁着，那是宿主自己的事，不该让用户"卸载完还要重启才看不到它"：
                    // 直接把条目从列表里摘掉（界面上立刻消失），目录记进 pending-delete，下次启动自动清。
            entry.Error = "被用户禁用";
            lock (_lock)
            {
                        _entries.Remove(entry);
                        _disabled.Remove(entry.Manifest.Id);
            }

            message = $"已禁用 {entry.DisplayName}（下次启动不会再装载）";
            RaiseChanged();
            return true;
        }
        catch (Exception ex)
        {
            message = "操作失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>卸载（删目录）。文件被占用就返回 false 并让用户重启后再来。</summary>
    public bool Uninstall(PluginEntry entry, out string message)
    {
        message = "";
        var movedToTrash = false;
        try
        {
            if (entry.State == PluginState.Loaded)
            {
                Unload(entry);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            try
            {
                if (Directory.Exists(entry.Directory))
                {
                    Directory.Delete(entry.Directory, recursive: true);
                }
            }
            catch (Exception)
            {
                // 删不掉（dll 还被运行时锁着）就改名搬走：Windows 允许给装着被占用文件的目录改名，
                // 搬出插件根目录之后这边就当它卸载完了，不用让用户重启软件。
                if (TryMoveToTrash(entry.Directory, entry.Manifest.Id))
                {
                    movedToTrash = true;
                }
                else
                {
                    // 文件删不掉是 dll 被运行时锁着，那是宿主自己的事，不该让用户"卸载完还要重启才看不到它"：
                    // 直接把条目从列表里摘掉（界面上立刻消失），目录记进 pending-delete，下次启动自动清。
                    lock (_lock)
                    {
                        _entries.Remove(entry);
                        _disabled.Remove(entry.Manifest.Id);
                    }
    
                    RaiseChanged();
                    AddPendingDelete(entry.Directory);
                    message = $"已卸载 {entry.DisplayName}（文件当时还被占用，残留目录会在下次启动软件时自动清掉）";
                    try
                    {
                        var dataDir = Path.Combine(DataRoot, SafeFolderName(entry.Manifest.Id));
                        if (Directory.Exists(dataDir))
                        {
                            Directory.Delete(dataDir, recursive: true);
                        }
                    }
                    catch
                    {
                        // 数据目录留着也不碍事
                    }

                    return true;
                    }
            }

            lock (_lock)
            {
                _entries.Remove(entry);
                _disabled.Remove(entry.Manifest.Id);
            }

            try
            {
                var dataDirectory = Path.Combine(DataRoot, SafeFolderName(entry.Manifest.Id));
                if (Directory.Exists(dataDirectory))
                {
                    Directory.Delete(dataDirectory, recursive: true);
                }
            }
            catch
            {
                // 数据目录留着也不碍事
            }

            RaiseChanged();
            message = movedToTrash
                ? $"已卸载 {entry.DisplayName}（文件当时还被占用，先搬进了回收目录，下次启动会自动清掉）"
                : $"已卸载 {entry.DisplayName}";
            return true;
        }
        catch (Exception ex)
        {
            message = "卸载失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>退出时调用：每个插件的 Shutdown 都会被调用一次。</summary>
    public void ShutdownAll()
    {
        foreach (var entry in Entries)
        {
            if (entry.State == PluginState.Loaded)
            {
                Unload(entry);
            }
        }
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>
    /// 启动时先把上次排队的事情做完：
    ///  · <id>.new 目录 → 覆盖成正式目录（更新一个"正在运行"的插件走的这条路）
    ///  · pending-delete.txt 里列的目录 → 删掉（卸载残留）
    /// 因为 dll 被运行时锁着的时候删不掉，只能排队到下次启动。
    /// </summary>
    public void ProcessPendingChanges()
    {
        // 先把上次"删不掉、改名搬进回收目录"的残留清掉（那时 dll 还被占用）
        CleanTrash();

        foreach (var root in new[] { PortableRoot, UserRoot })
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var staged in Directory.GetDirectories(root, "*.new"))
                {
                    var target = staged[..^4];
                    try
                    {
                        if (Directory.Exists(target))
                        {
                            Directory.Delete(target, recursive: true);
                        }

                        Directory.Move(staged, target);
                        RaiseLog($"[插件] 已完成排队的更新：{Path.GetFileName(target)}");
                    }
                    catch (Exception ex)
                    {
                        RaiseLog($"[插件] 排队更新没做成（{Path.GetFileName(staged)}）：{ex.Message}");
                    }
                }

                var listPath = Path.Combine(root, PendingDeleteFileName);
                if (File.Exists(listPath))
                {
                    var remaining = new List<string>();
                    foreach (var line in File.ReadAllLines(listPath))
                    {
                        var name = line.Trim();
                        if (name.Length == 0)
                        {
                            continue;
                        }

                        try
                        {
                            var directory = Path.Combine(root, name);
                            if (Directory.Exists(directory))
                            {
                                Directory.Delete(directory, recursive: true);
                            }

                            RaiseLog($"[插件] 已删掉卸载残留：{name}");
                        }
                        catch
                        {
                            remaining.Add(name);
                        }
                    }

                    if (remaining.Count == 0)
                    {
                        File.Delete(listPath);
                    }
                    else
                    {
                        File.WriteAllLines(listPath, remaining);
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseLog("[插件] 处理排队变更出错：" + ex.Message);
            }
        }
    }

    /// <summary>把新版本先放到 <id>.new，等下次启动再换上去。</summary>
    private void StageUpdate(string sourceDirectory, string pluginId, out string stagedDirectory)
    {
        stagedDirectory = Path.Combine(UserRoot, SafeFolderName(pluginId) + ".new");
        if (Directory.Exists(stagedDirectory))
        {
            Directory.Delete(stagedDirectory, recursive: true);
        }

        CopyDirectory(sourceDirectory, stagedDirectory);
        UnblockFiles(stagedDirectory);
    }

    /// <summary>
    /// 卸载时"删不掉但搬得动"的临时落脚点。放在插件根目录<strong>外面</strong> —— 扫描插件时不会看它。
    /// </summary>
    private string TrashRoot => Path.Combine(Path.GetDirectoryName(UserRoot) ?? UserRoot, "plugin-trash");

    /// <summary>
    /// 把删不掉的插件目录"改名搬走"。
    ///
    /// Windows 允许给装着"被占用文件"的目录改名（改名只动目录项，不碰被锁的文件本身），
    /// 所以这一步几乎总能成功 —— 搬出插件根目录之后，用户这边立刻就当它卸载完了，不用重启软件。
    /// 搬到回收目录里的残留会在下次启动时清掉（那时 dll 没被加载，删得掉）。
    /// </summary>
    private bool TryMoveToTrash(string directory, string pluginId)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return true;
            }

            var trash = TrashRoot;
            Directory.CreateDirectory(trash);
            var target = Path.Combine(trash, SafeFolderName(pluginId) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
            Directory.Move(directory, target);
            RaiseLog($"[插件] {pluginId} 的目录删不掉，已经先搬进回收目录：{target}");
            return true;
        }
        catch (Exception ex)
        {
            RaiseLog($"[插件] 回收目录也搬不进去（{pluginId}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>清掉上次卸载时搬进回收目录的残留（那次 dll 还被占用，删不掉）。</summary>
    private void CleanTrash()
    {
        try
        {
            var trash = TrashRoot;
            if (!Directory.Exists(trash))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(trash))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    RaiseLog($"[插件] 已清掉卸载残留：{Path.GetFileName(dir)}");
                }
                catch (Exception ex)
                {
                    RaiseLog($"[插件] 卸载残留这次还是删不掉（{Path.GetFileName(dir)}）：{ex.Message}");
                }
            }
        }
        catch
        {
        }
    }


    /// <summary>把删不掉的目录记进"下次启动删"名单。</summary>
    private void AddPendingDelete(string directory)
    {
        try
        {
            var root = Path.GetDirectoryName(directory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var listPath = Path.Combine(root, PendingDeleteFileName);
            var name = Path.GetFileName(directory);
            var existing = File.Exists(listPath) ? File.ReadAllLines(listPath).Select(x => x.Trim()).ToList() : new List<string>();
            if (!existing.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                existing.Add(name);
                File.WriteAllLines(listPath, existing);
            }
        }
        catch
        {
            // 记不上就下次手动删
        }
    }
    private static string? FindManifestPath(string directory)
    {
        // zip 里可能直接是文件，也可能套了一层文件夹
        var direct = Path.Combine(directory, ManifestFileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        foreach (var sub in Directory.GetDirectories(directory))
        {
            var nested = Path.Combine(sub, ManifestFileName);
            if (File.Exists(nested))
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>安全解压：逐个校验条目路径，防止 zip 里塞 "../../" 逃出目标目录（zip slip）。</summary>
    private static void ExtractZipSafely(string zipPath, string targetDirectory)
    {
        var prefix = Path.GetFullPath(targetDirectory + Path.DirectorySeparatorChar);

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var zipEntry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(zipEntry.Name))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(targetDirectory, zipEntry.FullName));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"zip 里有非法路径：{zipEntry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            zipEntry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>去掉"从网上下载"标记，否则 Windows 可能不让加载这些 dll。</summary>
    private static void UnblockFiles(string directory)
    {
        try
        {
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file + ":Zone.Identifier");
                }
                catch
                {
                    // 没有这个数据流（或不是 NTFS）就算了
                }
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static string SafeFolderName(string id)
    {
        var builder = new StringBuilder();
        foreach (var c in id)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }

        var name = builder.ToString().Trim('.', '_');
        return string.IsNullOrWhiteSpace(name) ? "plugin-" + Guid.NewGuid().ToString("N")[..8] : name;
    }

    internal void RaisePluginLog(PluginEntry entry, string message) => RaiseLog($"[插件 {entry.Manifest.Id}] {message}");

    internal void RaisePluginToast(PluginEntry entry, string message) => PluginToast?.Invoke(message);

    internal void RaisePluginOverlayMessage(PluginEntry entry, string text)
    {
        try
        {
            PluginOverlayMessage?.Invoke(entry, text);
        }
        catch
        {
            // 悬浮窗不在也不该影响插件
        }
    }

    internal void RaisePluginOverlayTopmost(PluginEntry entry)
    {
        try
        {
            PluginOverlayTopmostRequested?.Invoke();
        }
        catch
        {
            // 悬浮窗不在也不该影响插件
        }
    }

    private void RaiseLog(string message)
    {
        try
        {
            PluginLog?.Invoke(message);
        }
        catch
        {
            // 日志不该出错
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // 界面刷新失败不该影响插件
        }
    }
}
