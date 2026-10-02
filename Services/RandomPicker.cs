using System;
using System.Collections.Generic;
using System.Linq;

namespace MinecraftChatOverlay.Services;

/// <summary>
/// 「从勾选的项里抽一个」的公共逻辑。图标和提示音两处都要这套规则，
/// 抽到一处来，免得两边各写一遍、将来改规则只改了一半。
///
/// 三条规则：
///   1. 只从**勾中**的项里抽。一个都没勾时退回全部（而不是什么都不做）——
///      用户关掉勾选框的本意应该是"不想用这个"，而不是"什么都别放"；
///      一个都不勾多半是误操作，此时宁可放第一个也别让功能静默失效。
///   2. 关掉随机开关时不抽，固定用**列表里当前选中的那一项**。
///      （v9 前是"固定用第一个"，改成选中项 —— 关掉随机时列表项不显示勾选框，
///       "选哪个用哪个"才是用户看得见、控得住的语义。）
///   3. 没有选中项时（比如选中被移除、或列表刚重建）退回第一个，保证不出空。
///
/// 另外刻意做了一件事：**抽签避开上一次抽中的那个**（有别的可选项时）。
/// 连着两次显示同一张图会让人怀疑"随机是不是没生效"，那属于观感 bug。
/// </summary>
public static class RandomPicker
{
    [ThreadStatic]
    private static Random? _shared;

    private static Random Rng => _shared ??= new Random();

    /// <summary>抽一个。列表为空返回 null。</summary>
    /// <param name="all">全部候选项（保持用户排序）。</param>
    /// <param name="picked">其中被勾中的那些。</param>
    /// <param name="randomEnabled">随机开关。</param>
    /// <param name="lastPicked">上一次抽中的那个，用来避免连续重复。</param>
    /// <param name="selected">关掉随机时要用的那一项（通常是列表里选中的）。为空则退回第一项。</param>
    /// <param name="sequential">顺序播放开关：为 true 时不抽签，直接返回"当前轮到的那一项"（就是 <paramref name="selected"/>）。</param>
    public static string? Pick(
        IReadOnlyList<string> all,
        IReadOnlyCollection<string> picked,
        bool randomEnabled,
        string? lastPicked = null,
        string? selected = null,
        bool sequential = false)
    {
        if (all.Count == 0)
        {
            return null;
        }

        if (!randomEnabled)
        {
            // 关掉随机 = 「就用我选的那个」。选中项不在列表里（被移除/刚重建）时退回第一项。
            if (!string.IsNullOrEmpty(selected) &&
                all.Any(a => string.Equals(a, selected, StringComparison.OrdinalIgnoreCase)))
            {
                return selected;
            }

            return all[0];
        }

        // 顺序播放：不抽签，直接返回"轮到的那一项" —— 也就是列表里选中（紫色高亮）的那个。
        // 它不在候选项里（没勾 / 刚被移除）就退回候选项的第一个。
        // 放完该轮到谁，由调用方用 Next() 算出来，再把列表的选中项搬过去（高亮跟着走）。
        if (sequential)
        {
            var queue = Pool(all, picked);
            if (queue.Count == 0)
            {
                return null;
            }

            return queue.Any(q => string.Equals(q, selected, StringComparison.OrdinalIgnoreCase))
                ? selected
                : queue[0];
        }

        // 只看勾中的。一个都没勾就退回全部（见类注释第 1 条）。
        var pool = Pool(all, picked);

        if (pool.Count == 1)
        {
            return pool[0];
        }

        // 避开上一次那个。只有 pool 确实还有别的可选时才避，
        // 否则（比如只勾了两项、上次抽的是其中之一）避开会导致偏向另一个。
        // 这里的取舍是：宁可稍微偏向，也不要连续两次看着一模一样。
        if (!string.IsNullOrEmpty(lastPicked))
        {
            var narrowed = pool.Where(p => !string.Equals(p, lastPicked, StringComparison.OrdinalIgnoreCase)).ToList();
            if (narrowed.Count > 0)
            {
                pool = narrowed;
            }
        }

        return pool[Rng.Next(pool.Count)];
    }

    /// <summary>
    /// 顺序播放时"下一个轮到谁"：候选项里排在 <paramref name="current"/> 之后的那一项，
    /// 到末尾就绕回第一个；current 不在候选项里（或为空）则返回第一个。
    /// </summary>
    /// <param name="all">全部候选项（保持用户排序）。</param>
    /// <param name="picked">其中被勾中的那些。</param>
    /// <param name="current">当前轮到的那个。</param>
    public static string? Next(
        IReadOnlyList<string> all,
        IReadOnlyCollection<string> picked,
        string? current)
    {
        var queue = Pool(all, picked);
        if (queue.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrEmpty(current))
        {
            return queue[0];
        }

        var index = queue.FindIndex(q => string.Equals(q, current, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return queue[0];
        }

        return queue[(index + 1) % queue.Count];
    }

    /// <summary>真正参与抽取的池子：勾中的那些；一个都没勾就退回全部。</summary>
    private static List<string> Pool(IReadOnlyList<string> all, IReadOnlyCollection<string> picked)
    {
        var pool = all.Where(picked.Contains).ToList();
        if (pool.Count == 0)
        {
            pool = all.ToList();
        }

        return pool;
    }
}
