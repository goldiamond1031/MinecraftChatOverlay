using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MinecraftChatOverlay.Services;

/// <summary>AI 识图的参数（都由用户在界面上自己填）。</summary>
public sealed class AiVisionOptions
{
    /// <summary>接口地址，例如 https://api.openai.com/v1 或服务商给的中转地址。</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>模型 ID，必须是支持图片的模型。</summary>
    public string ModelId { get; init; } = "";

    public string ApiKey { get; init; } = "";

    /// <summary>发给 AI 的提示词。</summary>
    public string Prompt { get; init; } = "";

    /// <summary>
    /// 是否压缩后再上传。false（默认）= 原图直传，保真度最高；
    /// true = 缩到 1600 宽并转 JPEG，省流量但小字可能变糊。
    /// </summary>
    public bool Compress { get; init; }

    /// <summary>
    /// 额外合并进请求体的 JSON（用来关闭思考模式等）。
    /// 不同服务商字段名不同：智谱/火山是 {"thinking":{"type":"disabled"}}，
    /// 通义/硅基流动是 {"enable_thinking":false}，所以做成可填而不是写死。
    /// </summary>
    public string ExtraBodyJson { get; init; } = "";

    /// <summary>
    /// 接口类型：
    ///   "chat"    = OpenAI 兼容的对话模型（默认）
    ///   "zhipuOcr" = 智谱 GLM-OCR 的 layout_parsing（专用 OCR 接口，形状不一样）
    /// </summary>
    public string ApiMode { get; init; } = "chat";
}

/// <summary>
/// 把截图发给「OpenAI 兼容」的识图接口，拿回它识别出的文字。
/// 只负责"发图 → 拿文字"，认出来的文字怎么解析、怎么分配颜色由上层的解析器管。
/// </summary>
public static class AiVisionClient
{
    /// <summary>开启压缩时才缩到这个宽度。</summary>
    private const int MaxImageWidth = 1600;

    /// <summary>
    /// 原图超过这个大小就强制压缩：多数识图接口对单张图有 4~10MB 上限，
    /// base64 还会再涨 1/3，所以留点余量。
    /// </summary>
    private const long MaxRawImageBytes = 8L * 1024 * 1024;

    private const int JpegQuality = 85;

    public static async Task<string> RecognizeAsync(
        AiVisionOptions options, string imagePath, CancellationToken token, Action<string>? trace = null)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException("还没填接口地址（BaseUrl）。");
        }

        if (string.IsNullOrWhiteSpace(options.ModelId))
        {
            throw new InvalidOperationException("还没填模型 ID。");
        }

        var dataUrl = BuildImageDataUrl(imagePath, options.Compress, trace);

        var payload = new
        {
            model = options.ModelId,
            max_tokens = 4096,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = options.Prompt },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            }
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + options.ApiKey.Trim());
        }

        var isZhipuOcr = string.Equals(options.ApiMode, "zhipuOcr", StringComparison.OrdinalIgnoreCase);

        string url;
        string json;
        if (isZhipuOcr)
        {
            // 智谱 GLM-OCR：POST /layout_parsing，body 是 {model, file}，file 直接吃 base64 data URL
            url = options.BaseUrl.TrimEnd('/') + "/layout_parsing";
            json = JsonSerializer.Serialize(new
            {
                model = options.ModelId,
                file = dataUrl
            });
        }
        else
        {
            url = options.BaseUrl.TrimEnd('/') + "/chat/completions";
            json = MergeExtraBody(JsonSerializer.Serialize(payload), options.ExtraBodyJson, trace);
        }

        trace?.Invoke("正在请求：" + url + "（模型 " + options.ModelId + "）");
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // 这里刻意不用 ConfigureAwait(false)：trace 回调会去改 WPF 控件，
        // 必须回到调用方的（UI）线程，否则会报「调用线程无法访问此对象」。
        using var response = await http.PostAsync(url, content, token);
        var body = await response.Content.ReadAsStringAsync(token);

        trace?.Invoke($"接口返回 HTTP {(int)response.StatusCode}");

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"识图接口返回 HTTP {(int)response.StatusCode}：{Shorten(body)}");
        }

        var recognizedText = isZhipuOcr
            ? ExtractOcrText(body)
            : ExtractContent(body, out var finishReason);
        trace?.Invoke($"正文 {recognizedText.Length} 字");

        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            var hint = isZhipuOcr
                ? "GLM-OCR 没解析出文字。如果原始返回里是 ![](page=0,bbox=[...]) 这种，"
                  + "说明它把整张图判成了「图片」而不是文字区域 —— 它是文档解析模型，"
                  + "对游戏截图不在行。建议把「接口类型」换成对话模型、模型填 glm-4v-flash（免费）。"
                : "常见原因：模型把输出预算花在思考上了、或者被 max_tokens 截断。";

            throw new InvalidOperationException(
                "接口返回 200，但没拿到正文。" + hint + "原始返回：" + Shorten(body, 400));
        }

        return recognizedText;
    }

    /// <summary>从返回的 JSON 里取 choices[0].message.content。</summary>
    private static string ExtractContent(string body, out string finishReason)
    {
        finishReason = "?";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("返回里没有 choices。");
            }

            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                finishReason = fr.GetString() ?? "?";
            }

            var message = choice.TryGetProperty("message", out var m) ? m : default;
            if (message.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("返回里没有 message。");
            }

            var text = ReadContentField(message, "content");

            // 正文为空时退回读 reasoning_content —— 思考型模型会把内容放那儿
            if (string.IsNullOrWhiteSpace(text))
            {
                text = ReadContentField(message, "reasoning_content");
            }

            return text;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("返回的不是合法 JSON：" + Shorten(body) + "（" + ex.Message + "）");
        }
    }

    /// <summary>
    /// 拼成 data:image/...;base64,... 形式。
    /// 默认直接读原文件（PNG 就发 PNG，不重新编码，保真度最高）；
    /// 只有用户勾了"压缩上传"或者原图实在太大时才缩放 + 转 JPEG。
    /// </summary>
    /// <summary>
    /// 把用户填的额外请求参数合并进请求体。填错了不影响主流程（忽略并提示）。
    /// </summary>
    private static string MergeExtraBody(string baseJson, string? extraJson, Action<string>? trace)
    {
        if (string.IsNullOrWhiteSpace(extraJson))
        {
            return baseJson;
        }

        try
        {
            if (JsonNode.Parse(baseJson) is not JsonObject body)
            {
                return baseJson;
            }

            if (JsonNode.Parse(extraJson) is not JsonObject extra)
            {
                trace?.Invoke("额外请求参数要是一个 JSON 对象（例如 {\"thinking\":{\"type\":\"disabled\"}}），已忽略");
                return baseJson;
            }

            foreach (var pair in extra)
            {
                body[pair.Key] = pair.Value?.DeepClone();
            }

            trace?.Invoke("已带上额外参数：" + string.Join("、", extra.Select(x => x.Key)));
            return body.ToJsonString();
        }
        catch (Exception ex)
        {
            trace?.Invoke("额外请求参数不是合法 JSON，已忽略：" + ex.Message);
            return baseJson;
        }
    }

    /// <summary>
    /// 解析智谱 GLM-OCR（layout_parsing）的返回：
    /// 优先取 md_results（markdown 整篇），没有就退回把 layout_details 里每块的 content 拼起来。
    /// </summary>
    private static string ExtractOcrText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("md_results", out var md) && md.ValueKind == JsonValueKind.String)
            {
                // 把 ![](page=0,bbox=[...]) 这种"图片占位符"去掉：
                // GLM-OCR 把整块判成图片时，md_results 里就只有这种东西，不是文字。
                var text = Regex.Replace(md.GetString() ?? "", @"!\[[^\]]*\]\([^)]*\)", " ").Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            if (root.TryGetProperty("layout_details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var page in details.EnumerateArray())
                {
                    if (page.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var item in page.EnumerateArray())
                    {
                        if (item.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.String)
                        {
                            sb.AppendLine(content.GetString());
                        }
                    }
                }

                if (sb.Length > 0)
                {
                    return sb.ToString();
                }
            }

            return "";
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("返回的不是合法 JSON：" + Shorten(body) + "（" + ex.Message + "）");
        }
    }

    /// <summary>读 message 里的某个正文字段：字符串、分段数组两种形态都兼容。</summary>
    private static string ReadContentField(JsonElement message, string name)
    {
        if (!message.TryGetProperty(name, out var content))
        {
            return "";
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    sb.AppendLine(t.GetString());
                }
            }

            return sb.ToString();
        }

        return content.ToString();
    }

    private static string BuildImageDataUrl(string imagePath, bool compress, Action<string>? trace)
    {
        var rawLength = new FileInfo(imagePath).Length;
        var forceCompress = compress || rawLength > MaxRawImageBytes;

        if (!forceCompress)
        {
            var bytes = File.ReadAllBytes(imagePath);
            var mime = imagePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       imagePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? "image/jpeg"
                : "image/png";
            trace?.Invoke($"原图直传：{bytes.Length / 1024} KB（{mime}，未做任何处理）");
            return $"data:{mime};base64," + Convert.ToBase64String(bytes);
        }

        if (!compress && rawLength > MaxRawImageBytes)
        {
            trace?.Invoke($"原图 {rawLength / 1024 / 1024} MB 超过接口上限，这次改成压缩上传");
        }


        var frame = BitmapDecoder
            .Create(new Uri(imagePath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
            .Frames[0];

        BitmapSource source = frame;
        if (frame.PixelWidth > MaxImageWidth)
        {
            var scale = (double)MaxImageWidth / frame.PixelWidth;
            source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        trace?.Invoke($"已压缩成 JPEG：{ms.Length / 1024} KB（宽 {source.PixelWidth}）");
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private static string Shorten(string text, int max = 240)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
