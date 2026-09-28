using System.IO;
using System.Text.Json;
using MinecraftChatOverlay.Models;

namespace MinecraftChatOverlay.Services;

/// <summary>读取/保存用户配置到 %AppData%\MinecraftChatOverlay\settings.json。</summary>
public static class SettingsService
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MinecraftChatOverlay");

    private static readonly string ConfigFile = Path.Combine(ConfigDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                if (settings != null)
                {
                    settings.ColorRules ??= new List<Models.TextColorRule>();
                    settings.ReplaceRules ??= new List<Models.TextReplaceRule>();
                    settings.BlockKeywords ??= new List<BlockKeywordItem>();
                    settings.KillFeedback ??= new Models.KillFeedbackSettings();

                    // v8 把提示音和图标都改成"纯用户列表"，
                    // 下面这一串是保证反序列化出来的集合不为 null ——
                    // 老配置里没有这些字段，Newtonsoft/STJ 会留 null，
                    // 后面一 .Add 就是空引用。
                    settings.SoundNotifyFiles ??= new List<string>();
                    settings.SoundNotifyPicked ??= new List<string>();
                    settings.SoundNotifyUserFiles ??= new List<string>();
                    settings.KillFeedback.BannerIconFiles ??= new List<string>();
                    settings.KillFeedback.BannerIconPicked ??= new List<string>();
                    settings.KillFeedback.SoundFiles ??= new List<string>();
                    settings.KillFeedback.SoundPicked ??= new List<string>();
                    settings.KillFeedback.SoundUserFiles ??= new List<string>();

                    MigrateLegacySoundFiles(settings);
                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时使用默认配置，不阻止程序启动。
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFile, json);
        }
        catch
        {
            // 保存失败不应导致 UI 崩溃；用户可以手动检查目录权限。
        }
    }

    public static string ConfigPath => ConfigFile;

    /// <summary>
    /// 把 v7 的旧音效字段并进 v8 的新列表。
    ///
    /// v7 是「内置表 + 用户文件」两段式：<c>SoundNotifyFile</c> 存当前选中的那一个，
    /// <c>SoundNotifyUserFiles</c> 存用户自己加过的。v8 去掉了内置音效、改成纯用户列表，
    /// 所以要把「用户加过的」全搬进新列表，并把「当前选中的」也一并带上
    /// （万一用户只改了选中项、忘了它也在 user files 里）。
    ///
    /// <b>只搬存在且不是内置路径的。</b> 内置的那几个 wav（res\notify_*.wav）
    /// 已经随程序删掉了，搬过来只会变成一个"文件不存在"的死项。
    ///
    /// 这个方法只补空 —— 新列表里已经有内容就不再动，重复跑也安全。
    /// </summary>
    private static void MigrateLegacySoundFiles(AppSettings settings)
    {
        MergeSoundList(settings.SoundNotifyFiles, settings.SoundNotifyUserFiles, settings.SoundNotifyFile);
        MergeSoundList(settings.KillFeedback.SoundFiles, settings.KillFeedback.SoundUserFiles, settings.KillFeedback.SoundFile);

        // 新列表刚建出来时把勾选状态默认成全选 —— 用户导入的音显然是想用的。
        // 只在 picked 是空的时候补，之后用户自己去取消勾选，不要再覆盖回去。
        if (settings.SoundNotifyPicked.Count == 0)
        {
            settings.SoundNotifyPicked.AddRange(settings.SoundNotifyFiles);
        }

        if (settings.KillFeedback.SoundPicked.Count == 0)
        {
            settings.KillFeedback.SoundPicked.AddRange(settings.KillFeedback.SoundFiles);
        }

        // 图标同理：v7 只有一个自定义图路径，搬进新列表
        if (settings.KillFeedback.BannerIconFiles.Count == 0 &&
            IsUsableUserFile(settings.KillFeedback.BannerCustomImagePath))
        {
            settings.KillFeedback.BannerIconFiles.Add(settings.KillFeedback.BannerCustomImagePath);
        }

        if (settings.KillFeedback.BannerIconPicked.Count == 0)
        {
            settings.KillFeedback.BannerIconPicked.AddRange(settings.KillFeedback.BannerIconFiles);
        }
    }

    private static void MergeSoundList(List<string> target, List<string> legacyUserFiles, string legacySelected)
    {
        foreach (var file in legacyUserFiles)
        {
            AddIfUsable(target, file);
        }

        AddIfUsable(target, legacySelected);
    }

    private static void AddIfUsable(List<string> target, string file)
    {
        if (!IsUsableUserFile(file))
        {
            return;
        }

        if (!target.Contains(file, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(file);
        }
    }

    /// <summary>
    /// 这个路径值不值得搬进新列表：非空、不是内置音效、文件确实还在。
    /// 内置音效的判据是路径里带 <c>res\notify_</c> 或是相对路径 ——
    /// 用户自己导入的一定是绝对路径，正好用来区分。
    /// </summary>
    private static bool IsUsableUserFile(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return false;
        }

        try
        {
            // 相对路径 = 以前的内置音，不要
            if (!Path.IsPathRooted(file))
            {
                return false;
            }

            return File.Exists(file);
        }
        catch
        {
            return false;
        }
    }
}
