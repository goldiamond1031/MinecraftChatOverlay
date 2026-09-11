using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftChatOverlay.Services;

public sealed class BuJiDaoQueryResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = "";
    public JsonElement Root { get; init; }
}

/// <summary>布吉岛玩家数据查询客户端。</summary>
public static class BuJiDaoQueryService
{
    private const string GameStatsEndpoint = "https://api.mcbjd.net/v2/gamestats";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<BuJiDaoQueryResult> QueryPlayerAsync(
        string apiKey,
        string playerId,
        string gametype,
        string subtype,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new BuJiDaoQueryResult { Success = false, Error = Copy.NeedApiKey };
        }

        if (string.IsNullOrWhiteSpace(playerId))
        {
            return new BuJiDaoQueryResult { Success = false, Error = Copy.NeedPlayerId };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, GameStatsEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey.Trim()}");
            request.Headers.TryAddWithoutValidation("User-Agent", "MinecraftChatOverlay/1.0");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var payload = new Dictionary<string, string>
            {
                ["gametype"] = gametype,
                ["subtype"] = string.IsNullOrWhiteSpace(subtype) ? "all" : subtype
            };

            if (IsUuid(playerId))
            {
                payload["uuid"] = playerId.Trim();
            }
            else
            {
                payload["username"] = playerId.Trim();
            }

            var json = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new BuJiDaoQueryResult
                {
                    Success = false,
                    Error = $"HTTP {(int)response.StatusCode}：{ExtractMessage(body)}"
                };
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return new BuJiDaoQueryResult { Success = false, Error = Copy.QueryEmptyResponse };
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.Clone();

            if (root.ValueKind == JsonValueKind.Object)
            {
                var code = ReadString(root, "code");
                if (!string.IsNullOrWhiteSpace(code) && code != "200")
                {
                    var message = ReadString(root, "message") ?? ReadString(root, "msg") ?? Copy.QueryBadCode;
                    return new BuJiDaoQueryResult { Success = false, Error = $"code={code}：{message}" };
                }

                var success = ReadString(root, "success");
                if (string.Equals(success, "false", StringComparison.OrdinalIgnoreCase))
                {
                    var message = ReadString(root, "message") ?? ReadString(root, "msg") ?? Copy.QueryRejected;
                    return new BuJiDaoQueryResult { Success = false, Error = message };
                }
            }

            return new BuJiDaoQueryResult { Success = true, Root = root };
        }
        catch (TaskCanceledException)
        {
            return new BuJiDaoQueryResult { Success = false, Error = Copy.QueryTimeout };
        }
        catch (Exception ex)
        {
            return new BuJiDaoQueryResult { Success = false, Error = ex.Message };
        }
    }

    private static bool IsUuid(string value)
    {
        return Regex.IsMatch(
            value.Trim(),
            @"^(?i)[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Copy.NoContent;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return ReadString(root, "message")
                   ?? ReadString(root, "msg")
                   ?? ReadString(root, "error")
                   ?? body[..Math.Min(body.Length, 160)];
        }
        catch
        {
            return body[..Math.Min(body.Length, 160)];
        }
    }
}
