using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// 把每次 AI 识图返回的原文追加到一个日志文件里，方便回查
/// "刚才那次为什么没认出来" —— 偶发问题光看当前界面是查不出来的。
/// 位置：%AppData%\MinecraftChatOverlay\ai-responses.log
/// </summary>
public static class AiResponseLog
{
    /// <summary>超过这个大小就裁掉前面一半，避免无限增长。</summary>
    private const long MaxBytes = 512 * 1024;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MinecraftChatOverlay",
        "ai-responses.log");

    /// <summary>记一次返回。失败不抛异常，不能因为它影响识别流程。</summary>
    public static void Append(string source, string? text, int parsedCount)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            TrimIfTooLarge();

            var sb = new StringBuilder();
            sb.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {source}  认出 {parsedCount} 条 =====");
            sb.AppendLine(string.IsNullOrWhiteSpace(text) ? "(返回内容为空)" : text.Trim());
            sb.AppendLine();

            File.AppendAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 记录失败不影响识别
        }
    }

    /// <summary>
    /// 记一次"发给 AI 的东西"：接口、模型、参数、图片信息、完整提示词。
    /// 和后面的返回原文配成一对，出问题时前后对照就能看清。
    /// </summary>
    public static void AppendRequest(string source, IEnumerable<string> lines)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            TrimIfTooLarge();

            var sb = new StringBuilder();
            sb.AppendLine($"----- {DateTime.Now:yyyy-MM-dd HH:mm:ss}  发给 AI：{source} -----");
            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }

            sb.AppendLine();

            File.AppendAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 记录失败不影响识别
        }
    }

    /// <summary>清空记录（给界面上的按钮用）。</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch
        {
        }
    }

    private static void TrimIfTooLarge()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= MaxBytes)
            {
                return;
            }

            var text = File.ReadAllText(FilePath, Encoding.UTF8);
            var keepFrom = text.Length / 2;
            // 尽量从一条记录的开头切，别切在半截
            var boundary = text.IndexOf("=====", keepFrom, StringComparison.Ordinal);
            if (boundary > 0)
            {
                keepFrom = boundary;
            }

            File.WriteAllText(FilePath, text[keepFrom..], new UTF8Encoding(false));
        }
        catch
        {
        }
    }
}
