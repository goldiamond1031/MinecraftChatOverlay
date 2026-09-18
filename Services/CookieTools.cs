using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// 登录 Cookie 的解析工具：B站不同接口、不同时期下发登录字段的方式不一样，
/// 可能出现在「响应里的 url 参数」「响应头 Set-Cookie」「CookieContainer」三处，
/// 所以统一在这里按同样顺序尝试，登录和续期两个流程共用。
/// </summary>
internal static class CookieTools
{
    /// <summary>登录真正需要的字段（顺序即拼进 Cookie 的顺序）。</summary>
    public static readonly string[] LoginFieldNames =
    {
        "SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5"
    };

    private static readonly Uri[] ProbeUris =
    {
        new("https://passport.bilibili.com/"),
        new("https://www.bilibili.com/"),
        new("https://api.bilibili.com/"),
        new("https://live.bilibili.com/")
    };

    /// <summary>从「返回的 url 查询参数」里取登录字段。</summary>
    public static string FromUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "";
        }

        var queryIndex = url.IndexOf('?');
        if (queryIndex < 0)
        {
            return "";
        }

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in url[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = pair[..eq];
            foreach (var target in LoginFieldNames)
            {
                if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                {
                    found[target] = WebUtility.UrlDecode(pair[(eq + 1)..]);
                    break;
                }
            }
        }

        return Build(found);
    }

    /// <summary>从响应头的 Set-Cookie 里取登录字段。</summary>
    public static string FromHeaders(HttpResponseMessage? response)
    {
        if (response == null || !response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return "";
        }

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in values)
        {
            var firstPart = header.Split(';')[0];
            var eq = firstPart.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = firstPart[..eq].Trim();
            foreach (var target in LoginFieldNames)
            {
                if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                {
                    found[target] = firstPart[(eq + 1)..].Trim();
                    break;
                }
            }
        }

        return Build(found);
    }

    /// <summary>从 CookieContainer 里取登录字段。</summary>
    public static string FromContainer(CookieContainer? container)
    {
        if (container == null)
        {
            return "";
        }

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var uri in ProbeUris)
        {
            try
            {
                foreach (Cookie cookie in container.GetCookies(uri))
                {
                    foreach (var target in LoginFieldNames)
                    {
                        if (string.Equals(cookie.Name, target, StringComparison.OrdinalIgnoreCase))
                        {
                            found[target] = cookie.Value;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        return Build(found);
    }

    /// <summary>按固定顺序拼 Cookie 字符串；没有 SESSDATA 视为失败（返回空）。</summary>
    public static string Build(Dictionary<string, string> found)
    {
        if (!found.TryGetValue("SESSDATA", out var sessData) || string.IsNullOrWhiteSpace(sessData))
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var name in LoginFieldNames)
        {
            if (found.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{name}={value}");
            }
        }

        return string.Join("; ", parts);
    }

    /// <summary>从 Cookie 字符串里读某个字段（如 bili_jct）。</summary>
    public static string GetField(string cookie, string name)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return "";
        }

        foreach (var part in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            if (string.Equals(part[..eq].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(eq + 1)..].Trim();
            }
        }

        return "";
    }

    /// <summary>列出 url 里的参数名（不列值），用于诊断。</summary>
    public static string DescribeUrlParams(string url)
    {
        var index = url.IndexOf('?');
        if (index < 0)
        {
            return url.Length == 0 ? "空" : "该 url 没有参数";
        }

        var names = url[(index + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=')[0])
            .ToArray();
        return names.Length == 0 ? "空" : string.Join(",", names);
    }

    /// <summary>列出响应头里的 Cookie 名（不列值），用于诊断。</summary>
    public static string DescribeHeaders(HttpResponseMessage? response)
    {
        if (response == null || !response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return "无 Set-Cookie";
        }

        var names = values.Select(v => v.Split(';')[0].Split('=')[0].Trim()).Distinct().ToArray();
        return names.Length == 0 ? "无 Set-Cookie" : string.Join(",", names);
    }

    /// <summary>列出容器里的 Cookie 名（不列值），用于诊断。</summary>
    public static string DescribeContainer(CookieContainer? container)
    {
        if (container == null)
        {
            return "空";
        }

        var names = new List<string>();
        foreach (var uri in ProbeUris)
        {
            try
            {
                foreach (Cookie cookie in container.GetCookies(uri))
                {
                    if (!names.Contains(cookie.Name))
                    {
                        names.Add(cookie.Name);
                    }
                }
            }
            catch
            {
            }
        }

        return names.Count == 0 ? "空" : string.Join(",", names);
    }
}
