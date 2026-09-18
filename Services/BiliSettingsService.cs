using System;
using System.IO;
using System.Text.Json;
using MinecraftChatOverlay.Models;

namespace MinecraftChatOverlay.Services;

/// <summary>读取/保存 B站弹幕模块的配置（%AppData%\MinecraftChatOverlay\bili.json）。</summary>
public static class BiliSettingsService
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MinecraftChatOverlay");

    private static readonly string ConfigFile = Path.Combine(ConfigDirectory, "bili.json");

    public static BiliSettings Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                var settings = JsonSerializer.Deserialize<BiliSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                if (settings != null)
                {
                    settings.GiftColors ??= new BiliSettings().GiftColors;
                    NormalizeGiftColors(settings);
                    SeedGiftColors(settings);
                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时使用默认配置，不阻止程序启动。
        }

        return new BiliSettings();
    }

    /// <summary>
    /// 统一礼物名字典的比较器：JSON 反序列化会丢掉我们设定的 OrdinalIgnoreCase，
    /// 这里重建一遍，顺带把仅大小写不同的重复项合并（后出现的覆盖前者）。
    /// </summary>
    private static void NormalizeGiftColors(BiliSettings settings)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.GiftColors)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                normalized[pair.Key.Trim()] = pair.Value;
            }
        }

        settings.GiftColors = normalized;
    }

    /// <summary>
    /// 用内置礼物表补全用户配置（只做一次，靠 GiftColorsSeeded 标记）。
    /// 顺序按内置表（价格从高到低）重排，已有礼物保留用户自己设的颜色；
    /// 用户自己加的、内置表里没有的礼物追加在后面。
    /// 只做一次是为了：之后用户删掉的行不会再被加回来。
    /// </summary>
    private static void SeedGiftColors(BiliSettings settings)
    {
        if (settings.GiftColorsSeeded)
        {
            return;
        }

        settings.GiftColorsSeeded = true;

        var ordered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in new BiliSettings().GiftColors)
        {
            ordered[pair.Key] = settings.GiftColors.TryGetValue(pair.Key, out var existing) &&
                                !string.IsNullOrWhiteSpace(existing)
                ? existing
                : pair.Value;
        }

        foreach (var pair in settings.GiftColors)
        {
            if (!ordered.ContainsKey(pair.Key))
            {
                ordered[pair.Key] = pair.Value;
            }
        }

        settings.GiftColors = ordered;

        try
        {
            Save(settings);
        }
        catch
        {
            // 补全后没写回也没关系，下次启动会再试（标记只在内存里，不写回就不会丢）。
        }
    }

    public static void Save(BiliSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFile, json);
        }
        catch
        {
            // 保存失败不应影响弹幕显示。
        }
    }

    public static string ConfigPath => ConfigFile;
}
