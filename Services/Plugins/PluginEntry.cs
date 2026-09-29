using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Services.Plugins;

/// <summary>插件的装载状态。</summary>
public enum PluginState
{
    /// <summary>已装载、正在运行。</summary>
    Loaded,

    /// <summary>被用户禁用（不装载）。</summary>
    Disabled,

    /// <summary>装载失败（契约版本不对 / dll 坏了 / 构造函数抛异常……）。</summary>
    Failed,
}

/// <summary>一个装在磁盘上的插件。</summary>
public sealed class PluginEntry
{
    public required PluginManifest Manifest { get; init; }

    /// <summary>插件所在目录。</summary>
    public required string Directory { get; init; }

    public PluginState State { get; set; } = PluginState.Disabled;

    /// <summary>失败原因 / 状态说明。</summary>
    public string? Error { get; set; }

    /// <summary>插件的 IPlugin 实例（只有 Loaded 时非空）。</summary>
    internal IPlugin? Instance { get; set; }

    /// <summary>插件注册的导航页。</summary>
    public PluginPage? Page { get; set; }

    internal PluginHostContext? Context { get; set; }

    internal PluginLoadContext? LoadContext { get; set; }

    public bool IsLoaded => State == PluginState.Loaded;

    public string DisplayName => string.IsNullOrWhiteSpace(Manifest.Name) ? Manifest.Id : Manifest.Name;
}