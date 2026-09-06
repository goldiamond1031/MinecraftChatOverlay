namespace MinecraftChatOverlay.Services;

/// <summary>把 Minecraft 客户端日志中的一行解析为聊天栏消息。</summary>
public static class ChatLineParser
{
    // 标准 Minecraft 客户端日志聊天行中，logger 和 [CHAT] 之间是 "] : " 前缀：
    // [时间] [线程/级别] [net.minecraft...ChatComponent/]: [CHAT] 实际聊天内容
    // 这里使用完整的 "]: [CHAT]" 避免把其他日志中偶然出现的 [CHAT] 当成聊天。
    private const string ChatMarker = "[CHAT]";
    private const string ChatLineSeparator = "]: [CHAT]";

    /// <summary>
    /// 例如：
    /// [069月2026 09:32:12.646] [Render thread/INFO] [net.minecraft.client.gui.components.ChatComponent/]: [CHAT] 床被摧毁 &gt; ...\n
    /// 过滤后返回：
    /// 床被摧毁 &gt; ...
    /// 不是聊天栏日志（例如 Ignoring chat session）时返回 null。
    /// </summary>
    public static string? TryParseChatLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var separatorIndex = line.IndexOf(ChatLineSeparator, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return null;
        }

        var chatIndex = separatorIndex + ChatLineSeparator.Length - ChatMarker.Length;
        var start = chatIndex + ChatMarker.Length;

        // 跳过 [CHAT] 后面的普通空格；保留消息内部空格。
        while (start < line.Length && line[start] == ' ')
        {
            start++;
        }

        if (start >= line.Length)
        {
            return null;
        }

        var message = line[start..].Trim();

        // 如果日志里出现字面 \n（反斜杠+n），自动替换成真正换行。
        message = message.Replace("\\n", "\n").Trim();

        // 部分服务器/日志写入器在 GBK 下无法写入 ☆，会先变成 ?。
        // 这里针对常见的 “[xx 数字?]” 前缀还原为 “[xx 数字☆]”，例如 [0阶 23?] -> [0阶 23☆]。
        message = System.Text.RegularExpressions.Regex.Replace(message, @"(?<=\d)\?\]", "☆]");

        return string.IsNullOrEmpty(message) ? null : message;
    }
}
