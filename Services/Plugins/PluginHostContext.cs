using System.IO;
using MinecraftChatOverlay.Plugin;

namespace MinecraftChatOverlay.Services.Plugins;

/// <summary>
/// 交给单个插件的 IPluginHost 实现。
/// 这里每一次调用都是"插件代码碰宿主"的边界，所以全部包了 try/catch ——
/// 一个烂插件必须只烂它自己，不能把整个软件带走。
/// </summary>
internal sealed class PluginHostContext : IPluginHost
{
    private readonly PluginManager _manager;

    public PluginHostContext(PluginManager manager, PluginEntry entry)
    {
        _manager = manager;
        Entry = entry;
    }

    public PluginEntry Entry { get; }

    public int ApiVersion => PluginApi.Version;

    public string PluginDirectory => _manager.GetPluginDataDirectory(Entry);

    public string PluginsRootDirectory => _manager.PluginsRootDirectory;

    public event Action<ChatLine>? ChatLineReceived;

    public IReadOnlyList<ChatLine> RecentChatLines => _manager.RecentChatLines;

    public void RegisterPage(PluginPage page) => Entry.Page = page;

    public void Log(string message) => _manager.RaisePluginLog(Entry, message);

    public void ShowToast(string message) => _manager.RaisePluginToast(Entry, message);

    public IReadOnlyList<GameWindowInfo> GetGameWindows() => HostWindowEnumerator.FindGameWindows();

    public void SendToOverlay(string text) => _manager.RaisePluginOverlayMessage(Entry, text);

    public void ReassertOverlayTopmost() => _manager.RaisePluginOverlayTopmost(Entry);

    /// <summary>宿主投递一条聊天消息。插件抛异常只影响这一条消息，不打断其它插件。</summary>
    internal void RaiseChatLine(ChatLine line)
    {
        try
        {
            ChatLineReceived?.Invoke(line);
        }
        catch (Exception ex)
        {
            _manager.RaisePluginLog(Entry, "处理聊天消息时出错：" + ex.Message);
        }
    }
}