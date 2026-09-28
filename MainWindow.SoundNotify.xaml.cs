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
/// 「消息提示音」卡片的逻辑（MainWindow 的另一个 partial 文件）。
///
/// 卡片在「悬浮窗显示」页。需求是**不让用户写规则** ——
/// 「什么算玩家说话」写死在 <see cref="ChatTextProcessor.LooksLikePlayerSpeech"/> 里。
///
/// v8 起软件不再附带内置音效，列表里只有用户导入的文件，且支持随机抽取。
/// </summary>
public partial class MainWindow
{
    /// <summary>列表里显示的全部选项（用户导入的，顺序就是界面上的顺序）。</summary>
    private readonly ObservableCollection<SoundChoice> _soundChoices = new();

    private readonly ChatSoundNotifier _soundNotifier = new();

    /// <summary>上一次抽中的提示音路径。用来避免连着两次响同一个。</summary>
    private string _lastPlayedNotifySound = "";

    /// <summary>正在从配置往界面灌数据时为 true，用来屏蔽这一过程中的控件事件。</summary>
    private bool _soundNotifyLoading = true;

    /// <summary>窗口加载时调用（见 MainWindow_Loaded）。</summary>
    private void InitializeSoundNotifyUi()
    {
        _soundNotifyLoading = true;

        SoundChoiceListBox.ItemsSource = _soundChoices;
        LoadSoundNotifyUiFromSettings();

        Closing += (_, _) =>
        {
            try
            {
                _soundNotifier.Dispose();
            }
            catch
            {
                // 关窗时别影响退出流程
            }
        };

        _soundNotifyLoading = false;

        // 回填时 _soundNotifyLoading 是 true，开关的 Changed 被跳过了，
        // 这里得手动同步一次卡片的锁定态，否则第一次打开界面时不会压暗。
        RefreshLockedCards();
    }

    private void LoadSoundNotifyUiFromSettings()
    {
        SoundNotifyEnableCheckBox.IsChecked = _settings.EnableSoundNotify;
        SoundNotifyRandomCheckBox.IsChecked = _settings.SoundNotifyRandom;
        SoundNotifyIntervalTextBox.Text = _settings.SoundNotifyMinIntervalMs.ToString();

        RebuildSoundChoices();
        ApplySoundNotifyToNotifier();

        // 预热：先把可能要放的声音 Open 一次。MediaPlayer 头一回 Open 要读文件 + 建解码器，
        // 不预热的话第一次触发会慢半拍甚至漏掉。
        // 随机模式热全部勾中的；固定模式只会播选中的那一个。
        _soundNotifier.Prewarm(PreheatSoundNotifyFiles());

        RefreshSoundNotifyStatus();
    }

    /// <summary>该预热哪些音。随机模式是全部勾中的，固定模式只有选中的那个。</summary>
    private IEnumerable<string> PreheatSoundNotifyFiles()
    {
        if (_settings.SoundNotifyRandom)
        {
            return _soundChoices.Where(c => c.IsPicked).Select(c => c.File).ToList();
        }

        return SoundChoiceListBox?.SelectedItem is SoundChoice selected
            ? new[] { selected.File }
            : _soundChoices.Take(1).Select(c => c.File).ToList();
    }

    /// <summary>把配置推给播放器。改完设置记得调一次。</summary>
    private void ApplySoundNotifyToNotifier()
    {
        _soundNotifier.Enabled = _settings.EnableSoundNotify;
        _soundNotifier.MinIntervalMs = _settings.SoundNotifyMinIntervalMs;

        // SoundFile 只作为"找不到指定文件时的兜底"，真正放什么由 NotifySoundForChat 抽签决定。
        // 兜底值跟着当前语义走：关随机时"选中哪个就是哪个"，开随机时取第一个。
        if (!_settings.SoundNotifyRandom && SoundChoiceListBox?.SelectedItem is SoundChoice selected)
        {
            _soundNotifier.SoundFile = selected.File;
        }
        else
        {
            _soundNotifier.SoundFile = _soundChoices.FirstOrDefault()?.File ?? "";
        }
    }

    /// <summary>按配置里的文件列表重建列表，并把勾选状态带回来。</summary>
    private void RebuildSoundChoices()
    {
        var wasLoading = _soundNotifyLoading;
        _soundNotifyLoading = true;
        try
        {
            _soundChoices.Clear();

            foreach (var file in _settings.SoundNotifyFiles)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                var choice = SoundChoice.FromFile(file);

                // 勾选状态从配置读回来
                choice.IsPicked = _settings.SoundNotifyPicked.Any(
                    p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase));

                _soundChoices.Add(choice);
            }

            // 挂钩子：用户点勾选框 → 双向绑定写回 IsPicked → 通知 → 同步进配置。
            // 先清掉上一轮的（旧项已经不在集合里，只能整体清）
            ClearHandlers(_soundNotifyHandlers);
            HookSoundChoiceNotifications(_soundNotifyHandlers, _soundChoices, OnSoundNotifyPickedChanged);

            // 勾选框只在开随机时才显示
            ApplySoundChoiceCheckBoxVisibility(_soundChoices, _settings.SoundNotifyRandom);

            SoundChoiceListBox.SelectedItem = _soundChoices.FirstOrDefault();
        }
        finally
        {
            _soundNotifyLoading = wasLoading;
        }
    }

    /// <summary>把列表项的勾选状态同步回配置。</summary>
    private void SyncSoundNotifyPickedFromChoices()
    {
        var picked = _settings.SoundNotifyPicked;
        picked.Clear();
        picked.AddRange(_soundChoices.Where(c => c.IsPicked).Select(c => c.File));
    }

    private void SaveSoundNotifySettings()
    {
        try
        {
            SettingsService.Save(_settings);
        }
        catch
        {
            // 存配置失败不该打断使用
        }
    }

    private void RefreshSoundNotifyStatus()
    {
        if (SoundNotifyStatusText == null)
        {
            return;
        }

        if (!_settings.EnableSoundNotify)
        {
            SoundNotifyStatusText.Text = Copy.SoundNotifyOff;
            return;
        }

        var parts = new System.Collections.Generic.List<string>
        {
            Copy.SoundNotifyStats(
                _soundNotifier.SeenCount, _soundNotifier.SpeechCount, _soundNotifier.PlayCount)
        };

        if (_settings.SoundNotifyFiles.Count == 0)
        {
            parts.Add("还没有提示音 —— 点【添加提示音…】导入一个");
        }
        else if (_settings.SoundNotifyRandom)
        {
            var pickedCount = _settings.SoundNotifyPicked.Count;
            parts.Add(pickedCount == 0
                ? "随机播放（一个都没勾，会从全部里抽）"
                : $"随机播放（已勾 {pickedCount} 个）");
        }
        else
        {
            var selectedName = (SoundChoiceListBox?.SelectedItem as SoundChoice)?.Name;
            parts.Add(string.IsNullOrWhiteSpace(selectedName)
                ? "固定用列表中选中的那个"
                : $"固定用「{selectedName}」");
        }

        if (_soundNotifier.LastWarning.Length > 0)
        {
            parts.Add(_soundNotifier.LastWarning);
        }

        SoundNotifyStatusText.Text = string.Join(" —— ", parts);
    }

    // ===================== 开关 / 间隔 / 随机 =====================

    private void SoundNotifyEnableCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_soundNotifyLoading)
        {
            return;
        }

        _settings.EnableSoundNotify = SoundNotifyEnableCheckBox.IsChecked == true;
        ApplySoundNotifyToNotifier();
        SaveSoundNotifySettings();
        RefreshSoundNotifyStatus();
        RefreshLockedCards();
    }

    /// <summary>「随机播放」开关变了。</summary>
    private void SoundNotifyRandomCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_soundNotifyLoading)
        {
            return;
        }

        var random = SoundNotifyRandomCheckBox.IsChecked == true;
        _settings.SoundNotifyRandom = random;

        // 勾选框跟着随机开关显隐：关掉就不再显示（用列表里选中的那个）
        ApplySoundChoiceCheckBoxVisibility(_soundChoices, random);

        SaveSoundNotifySettings();
        RefreshSoundNotifyStatus();
    }

    /// <summary>列表项上的勾选框变了（参与随机抽取）。</summary>
    private void SoundChoicePicked_Changed(object sender, RoutedEventArgs e)
    {
        if (_soundNotifyLoading)
        {
            return;
        }

        SyncSoundNotifyPickedFromChoices();
        SaveSoundNotifySettings();
        RefreshSoundNotifyStatus();

        // 刚勾上的那个预热一下
        _soundNotifier.Prewarm(_soundChoices.Where(c => c.IsPicked).Select(c => c.File));
    }

    private void SoundNotifyIntervalTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_soundNotifyLoading)
        {
            return;
        }

        var value = ParseInt(SoundNotifyIntervalTextBox.Text, 800, 0, 30000);

        // 顺手把输入框里的内容规范化一下（填了 "abc" 会变回 800，填了 99999 会变回 30000），
        // 免得用户以为自己填的生效了。
        SoundNotifyIntervalTextBox.Text = value.ToString();

        _settings.SoundNotifyMinIntervalMs = value;
        ApplySoundNotifyToNotifier();
        SaveSoundNotifySettings();
        RefreshSoundNotifyStatus();
    }

    // ===================== 选声音 =====================

    private void SoundChoiceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_soundNotifyLoading || SoundChoiceListBox.SelectedItem is not SoundChoice choice)
        {
            return;
        }

        // 选中就预热，点【试听】和真正触发时都不会有"读文件"的延迟
        _soundNotifier.Prewarm(new[] { choice.File });

        RefreshSoundNotifyStatus();
    }

    private void PreviewSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (SoundChoiceListBox.SelectedItem is not SoundChoice choice)
        {
            MessageBox.Show(this, Copy.PickSound, "试听", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _soundNotifier.Preview(choice.File);
        RefreshSoundNotifyStatus();
    }

    /// <summary>【添加提示音…】把用户挑的音频加进列表（可多选）。</summary>
    private void BrowseSoundFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加消息提示音（可多选）",
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
            // 同一个文件不重复加
            if (_settings.SoundNotifyFiles.Any(
                    f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _settings.SoundNotifyFiles.Add(file);

            if (!_settings.SoundNotifyPicked.Any(
                    f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.SoundNotifyPicked.Add(file);
            }
        }

        RebuildSoundChoices();
        ApplySoundNotifyToNotifier();
        SaveSoundNotifySettings();
        RefreshSoundNotifyStatus();

        // 选完把最后加的那个放一遍，用户马上知道对不对
        var last = dialog.FileNames.LastOrDefault();
        if (!string.IsNullOrWhiteSpace(last))
        {
            _soundNotifier.Prewarm(new[] { last });
            _soundNotifier.Preview(last);
        }
    }

    private void RemoveSoundChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SoundChoiceListBox.SelectedItem is not SoundChoice choice)
        {
            if (SoundNotifyStatusText != null)
            {
                SoundNotifyStatusText.Text = "先在列表里选一个，再点移除。";
            }

            return;
        }

        _settings.SoundNotifyFiles.RemoveAll(
            f => string.Equals(f, choice.File, StringComparison.OrdinalIgnoreCase));
        _settings.SoundNotifyPicked.RemoveAll(
            f => string.Equals(f, choice.File, StringComparison.OrdinalIgnoreCase));

        _soundChoices.Remove(choice);
        SoundChoiceListBox.SelectedItem = _soundChoices.FirstOrDefault();

        ApplySoundNotifyToNotifier();
        SaveSoundNotifySettings();
        RefreshSoundNotifyStatus();
    }

    // ===================== 聊天线接线 =====================

    /// <summary>
    /// 日志线收到一条聊天时调用（见 Watcher_ChatLineReceived）。
    /// 传进来的是「已经过替换规则的文本」—— 也就是用户实际看到的那一份，
    /// 所以"看到什么就按什么判断"。
    /// </summary>
    private void NotifySoundForChat(string replacedText)
    {
        try
        {
            var files = _settings.SoundNotifyFiles;
            if (_soundNotifier.Enabled && files.Count > 0)
            {
                // 抽签在调用方做：播放器只管放，不管选哪个。
                // 关掉随机时用「列表里选中的那个」；开随机才从勾中的抽。
                var selected = (SoundChoiceListBox?.SelectedItem as SoundChoice)?.File;

                var file = RandomPicker.Pick(
                    files, _settings.SoundNotifyPicked, _settings.SoundNotifyRandom,
                    _lastPlayedNotifySound, selected);

                if (!string.IsNullOrWhiteSpace(file))
                {
                    _lastPlayedNotifySound = file;
                    _soundNotifier.SoundFile = file;
                }
            }

            _soundNotifier.NotifyPlayerSpoke(replacedText);
            RefreshSoundNotifyStatus();
        }
        catch
        {
            // 提示音出任何问题都不能影响聊天悬浮窗本身
        }
    }
}
