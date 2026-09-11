using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace MinecraftChatOverlay.Services;

/// <summary>候选字段的来源，界面据此标注"推荐"还是"实测"。</summary>
public enum PlayerQueryFieldSource
{
    /// <summary>内置字段库：来自已验证可用的字段清单，随时可选。</summary>
    Library = 0,

    /// <summary>本次查询返回的数据里发现的额外字段。</summary>
    FromData = 1
}

/// <summary>字段选择器里的一个候选项。</summary>
public sealed class PlayerQueryFieldCandidate : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isChecked;

    /// <summary>建议的显示名（用户可以改）。</summary>
    public string Label { get; init; } = "";

    /// <summary>JSON 路径，喂给 <see cref="Models.PlayerQueryField.Path"/>。</summary>
    public string Path { get; init; } = "";

    /// <summary>所属分组，用于在列表里归堆。</summary>
    public string Group { get; init; } = "";

    /// <summary>当前查询结果里的真实值。内置库里没有（还没查过时为空）。</summary>
    public string Sample { get; init; } = "";

    public PlayerQueryFieldSource Source { get; init; } = PlayerQueryFieldSource.Library;

    /// <summary>已经加进显示字段列表里了。</summary>
    public bool AlreadyAdded { get; init; }

    /// <summary>
    /// 勾选状态。需要通知变更，因为"全选 / 清空"是从代码里改这个值的。
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    /// <summary>下拉列表里显示的文字（分组 + 名称）。路径单独显示会太长。</summary>
    public string PickerText => string.IsNullOrWhiteSpace(Group) ? Label : Group + " · " + Label;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 玩家查询的候选字段来源，两个合在一起：
///
///   1. <b>内置字段库</b> —— 任何时候都能选，不需要先查询。
///      内容取自原本"起床 / 空岛预设"里那一套已验证可用的字段，加上响应字段名表里确认存在的项。
///   2. <b>本次查询数据</b> —— 查询过之后，把返回 JSON 里额外发现的标量路径补进来，
///      带上真实值，方便确认挑对了。
///
/// 这样"新增字段"这件事在第一使用、还没查过任何数据时就能完成。
/// </summary>
public static class PlayerQueryFieldCatalog
{
    /// <summary>数组不展开：带下标的路径不稳定，而且这些接口里数组基本不是标量统计。</summary>
    private const int MaxDepth = 8;

    /// <summary>路径末段 → 中文名 的猜测表（只用于从数据里扫出来的字段；库里的字段自带中文名）。</summary>
    private static readonly Dictionary<string, string> LabelHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "玩家",
        ["uuid"] = "UUID",
        ["exp"] = "经验",
        ["level"] = "等级",
        ["rank"] = "段位",
        ["online"] = "在线",
        ["counts"] = "总数",
        ["count"] = "数量",
        ["total_game"] = "总场次",
        ["total_win"] = "总胜场",
        ["total_lose"] = "总败场",
        ["total_kills"] = "总击杀",
        ["total_deaths"] = "总死亡",
        ["total_fk"] = "总最终击杀",
        ["total_bed_destroy"] = "总拆床",
        ["total_bed_destory"] = "总拆床",
        ["game"] = "场次",
        ["games"] = "场次",
        ["win"] = "胜场",
        ["wins"] = "胜场",
        ["lose"] = "败场",
        ["loses"] = "败场",
        ["kills"] = "击杀",
        ["deaths"] = "死亡",
        ["final_kills"] = "最终击杀",
        ["bed_destroy"] = "拆床",
        ["bed_destory"] = "拆床",
        ["beds"] = "拆床",
        ["fk"] = "最终击杀",
        ["ws"] = "连胜",
        ["winstreak"] = "连胜",
        ["best_winstreak"] = "最高连胜"
    };

    // ==================== 内置字段库 ====================

    /// <summary>
    /// 按游戏类型返回内置字段库。这些路径都来自原本"起床 / 空岛预设"，是已经跑通过的那一套，
    /// 不依赖任何查询结果，所以第一次打开软件就能用。
    /// </summary>
    public static IReadOnlyList<PlayerQueryFieldCandidate> Library(string gametype)
    {
        return string.Equals(gametype, "skywars", StringComparison.OrdinalIgnoreCase)
            ? SkywarsLibrary()
            : BedwarsLibrary();
    }

    private static List<PlayerQueryFieldCandidate> BedwarsLibrary()
    {
        const string modeGroup = "当前模式（跟随查询模式）";
        return new List<PlayerQueryFieldCandidate>
        {
            Lib("基础", "玩家", "data.name"),

            Lib("总览（全部模式合计）", "总场数", "data.total_game"),
            Lib("总览（全部模式合计）", "总胜场", "data.total_win"),
            Lib("总览（全部模式合计）", "总败场", "data.total_lose"),
            Lib("总览（全部模式合计）", "总胜率", "winrate:win=data.total_win;total=data.total_game"),
            Lib("总览（全部模式合计）", "总击杀", "data.total_kills"),
            Lib("总览（全部模式合计）", "总死亡", "data.total_deaths"),
            Lib("总览（全部模式合计）", "总最终击杀", "data.total_fk"),
            Lib("总览（全部模式合计）", "总拆床", "data.total_bed_destroy"),

            Lib(modeGroup, "胜率", "winrate:win=data.bedwars.{mode}.win;lose=data.bedwars.{mode}.lose"),
            Lib(modeGroup, "胜场", "data.bedwars.{mode}.win"),
            Lib(modeGroup, "败场", "data.bedwars.{mode}.lose"),
            Lib(modeGroup, "击杀", "data.bedwars.{mode}.kills"),
            Lib(modeGroup, "死亡", "data.bedwars.{mode}.deaths"),
            Lib(modeGroup, "最终击杀", "data.bedwars.{mode}.final_kills"),

            // 注意：接口自己的字段名就是 bed_destory（拼写是错的），不要"修正"成 bed_destroy
            Lib(modeGroup, "拆床", "data.bedwars.{mode}.bed_destory")
        };
    }

    private static List<PlayerQueryFieldCandidate> SkywarsLibrary()
    {
        const string modeGroup = "当前模式（跟随查询模式）";
        return new List<PlayerQueryFieldCandidate>
        {
            Lib("基础", "玩家", "data.name"),

            Lib("总览（成就统计）", "空岛总场数", "data.achievements.sw_games.counts"),
            Lib("总览（成就统计）", "空岛总胜场", "data.achievements.sw_wins.counts"),
            Lib("总览（成就统计）", "空岛总胜率",
                "winrate:win=data.achievements.sw_wins.counts;total=data.achievements.sw_games.counts"),

            Lib(modeGroup, "胜率", "winrate:win=data.skywars.{mode}.win;lose=data.skywars.{mode}.lose"),
            Lib(modeGroup, "胜场", "data.skywars.{mode}.win"),
            Lib(modeGroup, "败场", "data.skywars.{mode}.lose"),
            Lib(modeGroup, "击杀", "data.skywars.{mode}.kills"),
            Lib(modeGroup, "死亡", "data.skywars.{mode}.deaths"),

            Lib("各模式明细", "Solo 击杀", "data.skywars.sw1.kills"),
            Lib("各模式明细", "Solo 胜场", "data.skywars.sw1.win"),
            Lib("各模式明细", "双人 击杀", "data.skywars.sw2.kills"),
            Lib("各模式明细", "双人 胜场", "data.skywars.sw2.win")
        };
    }

    private static PlayerQueryFieldCandidate Lib(string group, string label, string path) => new()
    {
        Group = group,
        Label = label,
        Path = path,
        Source = PlayerQueryFieldSource.Library
    };

    // ==================== 组合出候选清单 ====================

    /// <summary>
    /// 内置库 + 本次查询数据里额外发现的字段。
    /// <paramref name="root"/> 可以为空（还没查询过），此时只返回内置库。
    /// </summary>
    public static List<PlayerQueryFieldCandidate> Build(
        JsonElement? root,
        string gametype,
        string modeKey,
        IReadOnlyCollection<string> existingPaths)
    {
        var existing = new HashSet<string>(
            existingPaths ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var result = new List<PlayerQueryFieldCandidate>();
        var library = Library(gametype);
        var libraryPaths = new HashSet<string>(library.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var item in library)
        {
            result.Add(new PlayerQueryFieldCandidate
            {
                Label = item.Label,
                Path = item.Path,
                Group = item.Group,
                Source = PlayerQueryFieldSource.Library,
                AlreadyAdded = existing.Contains(item.Path)
            });
        }

        if (root.HasValue)
        {
            var raw = new List<(string Path, string Value)>();
            Walk(root.Value, "", 0, raw);

            foreach (var (path, value) in raw)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                // 路径里出现当前模式名就换成 {mode}，和库里、预设里的写法保持一致，
                // 这样切换模式后字段依然有效。
                var templated = Templatize(path, modeKey);
                var finalPath = templated ?? path;

                // 库里已有的、或者已经出现过的，不再重复
                if (libraryPaths.Contains(finalPath) ||
                    result.Any(c => string.Equals(c.Path, finalPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var lastSegment = LastSegment(path);
                var label = LabelHints.TryGetValue(lastSegment, out var hint) ? hint : lastSegment;

                // 通用的名字加父级前缀，避免一屏全是"击杀"
                var parentSegment = SecondToLastSegment(path);
                if (!string.IsNullOrWhiteSpace(parentSegment) && IsGenericLabel(label))
                {
                    label = parentSegment + " " + label;
                }

                if (templated != null)
                {
                    label = "当前模式 · " + label;
                }

                result.Add(new PlayerQueryFieldCandidate
                {
                    Label = label,
                    Path = finalPath,
                    Group = "本次查询额外发现",
                    Sample = Trim(value, 22),
                    Source = PlayerQueryFieldSource.FromData,
                    AlreadyAdded = existing.Contains(finalPath)
                });
            }
        }

        return result
            .OrderBy(c => c.AlreadyAdded)
            .ThenBy(c => c.Source)
            .ThenBy(c => c.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把路径里等于当前模式名的那一段换成 {mode}。</summary>
    private static string? Templatize(string path, string modeKey)
    {
        if (string.IsNullOrWhiteSpace(modeKey))
        {
            return null;
        }

        var segments = path.Split('.');
        var hit = false;
        for (var i = 0; i < segments.Length; i++)
        {
            if (string.Equals(segments[i], modeKey, StringComparison.OrdinalIgnoreCase))
            {
                segments[i] = "{mode}";
                hit = true;
            }
        }

        return hit ? string.Join('.', segments) : null;
    }

    private static void Walk(JsonElement element, string path, int depth, List<(string, string)> output)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = string.IsNullOrEmpty(path) ? property.Name : path + "." + property.Name;
                    Walk(property.Value, childPath, depth + 1, output);
                }
                break;

            case JsonValueKind.Array:
                // 数组略过：带下标的路径不稳定
                break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                output.Add((path, Format(element)));
                break;
        }
    }

    private static string Format(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.TryGetInt64(out var l)
            ? l.ToString("N0", CultureInfo.InvariantCulture)
            : element.GetRawText(),
        JsonValueKind.True => "是",
        JsonValueKind.False => "否",
        _ => "（空）"
    };

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('.');
        return i < 0 ? path : path[(i + 1)..];
    }

    private static string SecondToLastSegment(string path)
    {
        var parts = path.Split('.');
        if (parts.Length < 3)
        {
            return "";
        }

        var segment = parts[^2];
        return segment.StartsWith('{') ? "" : segment;
    }

    private static bool IsGenericLabel(string label) =>
        label is "击杀" or "死亡" or "胜场" or "败场" or "拆床" or "最终击杀" or "场次" or "数量" or "总数";

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Length <= max ? value : value[..max] + "…";
    }
}
