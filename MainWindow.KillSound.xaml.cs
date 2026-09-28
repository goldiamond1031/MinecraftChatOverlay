using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MinecraftChatOverlay.Models;
using MinecraftChatOverlay.Services;

namespace MinecraftChatOverlay;

/// <summary>
/// 「击杀提示音」卡片。和「消息提示音」共用同一套播放器，
/// 但触发条件不是「像玩家说话」，而是击杀匹配器已经确认是你的击杀。
///
/// v8 起软件不再附带内置音效，列表里只有用户导入的文件，且支持随机抽取。
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<SoundChoice> _killSoundChoices = new();
    private readonly ChatSoundNotifier _killSoundNotifier = new();
    private bool _killSoundLoading = true;

    /// <summary>窗口加载时调用（见 MainWindow_Loaded）。</summary>
    private void InitializeKillSoundUi()
    {
        _killSoundLoading = true;

        KillSoundChoiceListBox.ItemsSource = _killSoundChoices;
        LoadKillSoundUiFromSettings();

        Closing += (_, _) =>
        {
            try
            {
                _killSoundNotifier.Dispose();
            }
            catch
            {
                // 关窗时别影响退出流程
            }
        };

        _killSoundLoading = false;

        // 同消息提示音：回填期间 Changed 被跳过，锁定态得手动同步一次。
        RefreshLockedCards();
    }

    private void LoadKillSoundUiFromSettings()
    {
        KillSoundEnableCheckBox.IsChecked = _settings.KillFeedback.SoundEnabled;
        KillSoundRandomCheckBox.IsChecked = _settings.KillFeedback.SoundRandom;

        RebuildKillSoundChoices();
        ApplyKillSoundToNotifier();

        // 预热：先把可能要放的音 Open 一次，第一次击杀才不会慢半拍。
        // 随机模式：勾中的都要热（第一轮抽到没热过的那个还是会慢）。
        // 固定模式：只会播选中的那一个，热一个就够。
        _killSoundNotifier.Prewarm(PreheatKillSoundFiles());

        RefreshKillSoundStatus();
    }

    /// <summary>该预热哪些音。随机模式是全部勾中的，固定模式只有选中的那个。</summary>
    private IEnumerable<string> PreheatKillSoundFiles()
    {
        if (_settings.KillFeedback.SoundRandom)
        {
            return _killSoundChoices.Where(c => c.IsPicked).Select(c => c.File).ToList();
        }

        return KillSoundChoiceListBox?.SelectedItem is SoundChoice selected
            ? new[] { selected.File }
            : _killSoundChoices.Take(1).Select(c => c.File).ToList();
    }

    private void ApplyKillSoundToNotifier()
    {
        _killSoundNotifier.Enabled = _settings.KillFeedback.SoundEnabled;

        // SoundFile 只作为"找不到指定文件时的兜底"，真正放什么由 NotifyKillSound 传参决定。
        // 兜底值跟着当前语义走：关随机时"选中哪个就是哪个"，开随机时取第一个。
        _killSoundNotifier.SoundFile = FallbackKillSoundFile();

        // 连杀/双杀该响几次就响几次，不做防刷屏间隔。
        _killSoundNotifier.MinIntervalMs = 0;
    }

    /// <summary>兜底用哪个音：关随机时用选中的，开随机或没选中时退回第一项。</summary>
    private string FallbackKillSoundFile()
    {
        if (!_settings.KillFeedback.SoundRandom &&
            KillSoundChoiceListBox?.SelectedItem is SoundChoice selected)
        {
            return selected.File;
        }

        return _killSoundChoices.FirstOrDefault()?.File ?? "";
    }

    /// <summary>按配置里的文件列表重建列表，并把勾选状态带回来。</summary>
    private void RebuildKillSoundChoices()
    {
        var wasLoading = _killSoundLoading;
        _killSoundLoading = true;
        try
        {
            _killSoundChoices.Clear();

            foreach (var file in _settings.KillFeedback.SoundFiles)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                var choice = SoundChoice.FromFile(file);

                // 勾选状态从配置读回来。老配置里 picked 是空的 → 全不勾，
                // 界面上会提示"一个都没勾"，比默默变成"全勾"更诚实。
                choice.IsPicked = _settings.KillFeedback.SoundPicked.Any(
                    p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase));

                _killSoundChoices.Add(choice);
            }

            // 挂钩子：用户点勾选框 → 双向绑定写回 IsPicked → 通知 → 同步进配置。
            ClearHandlers(_killSoundHandlers);
            HookSoundChoiceNotifications(_killSoundHandlers, _killSoundChoices, OnKillSoundPickedChanged);

            // 勾选框只在开随机时才显示
            ApplySoundChoiceCheckBoxVisibility(_killSoundChoices, _settings.KillFeedback.SoundRandom);

            KillSoundChoiceListBox.SelectedItem = _killSoundChoices.FirstOrDefault();
        }
        finally
        {
            _killSoundLoading = wasLoading;
        }
    }

    /// <summary>把列表项的勾选状态同步回配置。</summary>
    private void SyncKillSoundPickedFromChoices()
    {
        var picked = _settings.KillFeedback.SoundPicked;
        picked.Clear();
        picked.AddRange(_killSoundChoices.Where(c => c.IsPicked).Select(c => c.File));
    }

    private void RefreshKillSoundStatus()
    {
        if (KillSoundStatusText == null)
        {
            return;
        }

        if (!_settings.KillFeedback.SoundEnabled)
        {
            KillSoundStatusText.Text = Copy.KillSoundOff;
            return;
        }

        var parts = new System.Collections.Generic.List<string>();
        parts.Add(Copy.KillSoundStats(_killSoundNotifier.PlayCount));

        if (_settings.KillFeedback.SoundFiles.Count == 0)
        {
            parts.Add("还没有音效 —— 点【添加音效…】导入一个");
        }
        else if (_settings.KillFeedback.SoundRandom)
        {
            var pickedCount = _settings.KillFeedback.SoundPicked.Count;
            parts.Add(pickedCount == 0
                ? "随机播放（一个都没勾，会从全部里抽）"
                : $"随机播放（已勾 {pickedCount} 个）");
        }
        else
        {
            var selectedName = (KillSoundChoiceListBox?.SelectedItem as SoundChoice)?.Name;
            parts.Add(string.IsNullOrWhiteSpace(selectedName)
                ? "固定用列表中选中的那个"
                : $"固定用「{selectedName}」");
        }

        if (_killSoundNotifier.LastWarning.Length > 0)
        {
            parts.Add(_killSoundNotifier.LastWarning);
        }

        KillSoundStatusText.Text = string.Join(" —— ", parts);
    }

    private void KillSoundEnableCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_killSoundLoading)
        {
            return;
        }

        _settings.KillFeedback.SoundEnabled = KillSoundEnableCheckBox.IsChecked == true;
        ApplyKillSoundToNotifier();
        SaveKillFeedbackSettings();
        RefreshKillSoundStatus();
        RefreshLockedCards();

        if (_settings.KillFeedback.SoundEnabled)
        {
            _killSoundNotifier.Prewarm(_killSoundChoices.Where(c => c.IsPicked).Select(c => c.File));
        }
    }

    /// <summary>「随机播放」开关变了。</summary>
    private void KillSoundRandomCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_killSoundLoading)
        {
            return;
        }

        var random = KillSoundRandomCheckBox.IsChecked == true;
        _settings.KillFeedback.SoundRandom = random;

        // 勾选框跟着随机开关显隐：关掉就不再显示（用列表里选中的那个）
        ApplySoundChoiceCheckBoxVisibility(_killSoundChoices, random);

        SaveKillFeedbackSettings();
        RefreshKillSoundStatus();
    }

    /// <summary>列表项上的勾选框变了（参与随机抽取）。</summary>
    private void KillSoundPicked_Changed(object sender, RoutedEventArgs e)
    {
        if (_killSoundLoading)
        {
            return;
        }

        SyncKillSoundPickedFromChoices();
        SaveKillFeedbackSettings();
        RefreshKillSoundStatus();

        // 刚勾上的那个预热一下，等下抽到它才不会慢半拍
        _killSoundNotifier.Prewarm(_killSoundChoices.Where(c => c.IsPicked).Select(c => c.File));
    }

    private void KillSoundChoiceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_killSoundLoading || KillSoundChoiceListBox.SelectedItem is not SoundChoice choice)
        {
            return;
        }

        _killSoundNotifier.Prewarm(new[] { choice.File });
        RefreshKillSoundStatus();
    }

    private void PreviewKillSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (KillSoundChoiceListBox.SelectedItem is not SoundChoice choice)
        {
            MessageBox.Show(this, Copy.PickSound, "试听", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _killSoundNotifier.Preview(choice.File);
        RefreshKillSoundStatus();
    }

    /// <summary>【添加音效…】把用户挑的音频加进列表（可多选）。</summary>
    private void BrowseKillSoundFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加击杀提示音（可多选）",
            Filter = "音频文件 (*.wav;*.mp3;*.wma;*.m4a)|*.wav;*.mp3;*.wma;*.m4a"
                     + "|所有文件 (*.*)|*.*",
            Multiselect = true,
        };

        var resDirectory = Path.Combine(AppContext.BaseDirectory, "res");
        if (Directory.Exists(resDirectory))
        {
            dialog.InitialDirectory = resDirectory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            if (_settings.KillFeedback.SoundFiles.Any(
                    f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _settings.KillFeedback.SoundFiles.Add(file);

            // 新加的默认勾上 —— 用户刚导入的显然是想用的
            if (!_settings.KillFeedback.SoundPicked.Any(
                    f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.KillFeedback.SoundPicked.Add(file);
            }
        }

        RebuildKillSoundChoices();
        ApplyKillSoundToNotifier();
        SaveKillFeedbackSettings();
        RefreshKillSoundStatus();

        // 选完把最后加的那个放一遍，用户马上知道对不对
        var last = dialog.FileNames.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(last))
        {
            _killSoundNotifier.Prewarm(new[] { last });
            _killSoundNotifier.Preview(last);
        }
    }

    private void RemoveKillSoundChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (KillSoundChoiceListBox.SelectedItem is not SoundChoice choice)
        {
            if (KillSoundStatusText != null)
            {
                KillSoundStatusText.Text = "先在列表里选一个，再点移除。";
            }

            return;
        }

        _settings.KillFeedback.SoundFiles.RemoveAll(
            f => string.Equals(f, choice.File, StringComparison.OrdinalIgnoreCase));
        _settings.KillFeedback.SoundPicked.RemoveAll(
            f => string.Equals(f, choice.File, StringComparison.OrdinalIgnoreCase));

        _killSoundChoices.Remove(choice);
        KillSoundChoiceListBox.SelectedItem = _killSoundChoices.FirstOrDefault();

        ApplyKillSoundToNotifier();
        SaveKillFeedbackSettings();
        RefreshKillSoundStatus();
    }

    /// <summary>击杀匹配成功后调用，播一次击杀提示音。</summary>
    private void NotifyKillSound()
    {
        try
        {
            var k = _settings.KillFeedback;
            if (!k.SoundEnabled || k.SoundFiles.Count == 0)
            {
                return;
            }

            // 抽签在调用方做：播放器只管放，不管选哪个。
            // 关掉随机时用「列表里选中的那个」；开随机才从勾中的抽。
            var selected = (KillSoundChoiceListBox?.SelectedItem as SoundChoice)?.File;

            var file = RandomPicker.Pick(
                k.SoundFiles, k.SoundPicked, k.SoundRandom, _lastPlayedKillSound, selected);
            if (string.IsNullOrWhiteSpace(file))
            {
                return;
            }

            _lastPlayedKillSound = file;

            // 不传 SoundFile —— 直接把抽中的这个交给播放器。
            // 播放器那边会另开一路实例，不会掐断上一个还没放完的音。
            _killSoundNotifier.NotifyKillSound(file);
            RefreshKillSoundStatus();
        }
        catch
        {
            // 声音出问题不能影响击杀反馈本身
        }
    }
}
