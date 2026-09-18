using System;
using System.Threading;
using System.Threading.Tasks;
using MinecraftChatOverlay.Models;

namespace MinecraftChatOverlay.Services;

public interface ILiveClient : IDisposable
{
    bool IsConnected { get; }

    event Action<RoomInfo>? RoomInfoUpdated;
    event Action<DanmakuItem>? DanmakuReceived;
    event Action<NoticeItem>? NoticeReceived;

    /// <summary>
    /// 醒目留言（SC）。单独一个事件是因为它要按弹幕那样的富样式显示
    /// （头像 + SC 标识 + 粉丝牌 + 用户名），而不是拼成一行纯文本。
    /// </summary>
    event Action<SuperChatItem>? SuperChatReceived;

    event Action<string>? StatusChanged;
    event Action<Exception>? ErrorOccurred;

    /// <summary>
    /// 真实在线人数更新。
    /// 注意：只有直连模式（BilibiliLiveClient）能提供 —— 数据来自弹幕流的
    /// ONLINE_RANK_COUNT 命令（online_count 字段）。开放平台/身份码模式没有这个数据。
    /// </summary>
    event Action<int>? OnlineCountUpdated;

    Task ConnectAsync(string roomIdOrAuthCode, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}
