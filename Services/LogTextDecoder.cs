using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// 解码 Minecraft 日志行。网易/国服部分客户端会把日志写成 GBK，
/// 而原版 Java 通常为 UTF-8，因此提供 Auto/UTF-8/GBK 三种方式。
/// </summary>
public static class LogTextDecoder
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly UTF8Encoding Utf8Strict = new(false, true);
    private static Encoding? _gbk;
    private static bool _gbkAttempted;

    /// <summary>布吉岛/部分服务器的 VIP 图标在 GBK 日志中的字节与希望显示的文字。</summary>
    private static readonly (byte High, byte Low, string Label)[] VipIconMappings =
    {
        // 实际日志中还出现了 AC DD，它和 AC DE~AC E4 正好组成 vip1~vip8。
        (0xAC, 0xDD, "[vip1]"),
        (0xAC, 0xDE, "[vip2]"),
        (0xAC, 0xDF, "[vip3]"),
        (0xAC, 0xE0, "[vip4]"),
        (0xAC, 0xE1, "[vip5]"),
        (0xAC, 0xE2, "[vip6]"),
        (0xAC, 0xE3, "[vip7]"),
        (0xAC, 0xE4, "[vip8]"),
    };

    public static string Decode(byte[] bytes, string encodingName)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        var trimmed = TrimBom(bytes);

        if (string.Equals(encodingName, "UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            return Utf8NoBom.GetString(trimmed);
        }

        if (string.Equals(encodingName, "GBK", StringComparison.OrdinalIgnoreCase))
        {
            var gbk = GetGbkEncoding();
            return gbk.GetString(ReplaceVipIconsWithText(trimmed));
        }

        // Auto：能按严格 UTF-8 解码就按 UTF-8，否则退回 GBK。
        try
        {
            return Utf8Strict.GetString(trimmed);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                return GetGbkEncoding().GetString(ReplaceVipIconsWithText(trimmed));
            }
            catch
            {
                return Utf8NoBom.GetString(trimmed);
            }
        }
    }

    /// <summary>
    /// 把 VIP 图标的 GBK 双字节替换成可读的 [vipN] 文字。
    /// 替换后的 ASCII 文字不会影响后续 GBK 中文解码。
    /// </summary>
    private static byte[] ReplaceVipIconsWithText(byte[] bytes)
    {
        var hasVip = false;
        foreach (var mapping in VipIconMappings)
        {
            if (ContainsVip(bytes, mapping))
            {
                hasVip = true;
                break;
            }
        }

        if (!hasVip)
        {
            return bytes;
        }

        var result = new List<byte>(bytes.Length + 32);
        for (var i = 0; i < bytes.Length; i++)
        {
            string? label = null;
            if (i + 1 < bytes.Length)
            {
                label = GetVipLabel(bytes[i], bytes[i + 1]);
            }

            if (label != null)
            {
                result.AddRange(Encoding.ASCII.GetBytes(label));
                i++;
            }
            else
            {
                result.Add(bytes[i]);
            }
        }

        return result.ToArray();
    }

    private static bool ContainsVip(byte[] bytes, (byte High, byte Low, string Label) mapping)
    {
        for (var i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == mapping.High && bytes[i + 1] == mapping.Low)
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetVipLabel(byte high, byte low)
    {
        foreach (var mapping in VipIconMappings)
        {
            if (high == mapping.High && low == mapping.Low)
            {
                return mapping.Label;
            }
        }

        return null;
    }

    private static byte[] TrimBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes[3..];
        }

        return bytes;
    }

    private static Encoding GetGbkEncoding()
    {
        if (_gbk != null)
        {
            return _gbk;
        }

        if (!_gbkAttempted)
        {
            _gbkAttempted = true;
            try
            {
                EnsureCodePagesProviderRegistered();
                _gbk = Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            }
            catch
            {
                // 当前运行时没有 GBK 支持时退回 UTF-8，避免程序崩溃。
                _gbk = Encoding.UTF8;
            }
        }

        return _gbk ?? Encoding.UTF8;
    }

    private static void EnsureCodePagesProviderRegistered()
    {
        // System.Text.Encoding.CodePages 在 .NET 运行时中可能可用，但不一定默认注册。
        // 使用反射调用，避免编译期引入额外 NuGet 包。
        var assembly = Assembly.Load("System.Text.Encoding.CodePages");
        var providerType = assembly.GetType("System.Text.CodePagesEncodingProvider");
        var instance = providerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (instance is EncodingProvider provider)
        {
            Encoding.RegisterProvider(provider);
        }
    }
}
