using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace MinecraftChatOverlay.Services.Plugins;

/// <summary>
/// 每个插件一个独立的、可卸载的 AssemblyLoadContext。
///
/// 两条铁律：
///  1) 契约程序集（MinecraftChatOverlay.Plugin.Abstractions）**必须**回落到宿主那一份，
///     否则插件里的 IPlugin 和宿主里的 IPlugin 会是两个不同的类型，is/as 全部失败。
///  2) 除了插件目录里的 dll，其它一切（WPF、BCL）都回落到默认上下文，别自己再装一份。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private const string AbstractionsAssemblyName = "MinecraftChatOverlay.Plugin.Abstractions";

    private readonly string _directory;

    public PluginLoadContext(string directory, string name)
        : base(name, isCollectible: true)
    {
        _directory = directory;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 契约程序集：返回 null = 交给默认上下文（宿主已经加载了那一份）
        if (string.Equals(assemblyName.Name, AbstractionsAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 插件自带的依赖：优先用插件目录里的
        if (!string.IsNullOrWhiteSpace(assemblyName.Name))
        {
            var candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
            {
                return LoadFromAssemblyPath(candidate);
            }
        }

        // 其它一律交给默认上下文
        return null;
    }
}