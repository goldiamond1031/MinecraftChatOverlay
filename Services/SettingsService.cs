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
                    settings.BlockKeywords ??= new List<string>();
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
}
