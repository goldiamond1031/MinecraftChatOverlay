using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using MinecraftChatOverlay.Models;

namespace MinecraftChatOverlay;

/// <summary>
/// 「卡片锁定」状态的统一出处，以及列表勾选框的接线。
///
/// <b>锁定态：</b>总开关（击杀时显示图标 / 击杀时播放提示音 / 消息提示音）关掉之后，
/// 卡片下半部分的控件会被 <c>IsEnabled=False</c> 禁掉。禁掉只是一半，
/// 用户还得一眼看出"这张卡现在不生效"。做法是整卡降不透明度 + 换底色，
/// 也就是 <c>LockedCardStyle</c>。
///
/// XAML 那边把三个 bool 绑到对应 Border 的 <c>Tag</c> 上，样式里用
/// <c>Trigger Property="Tag" Value="True"</c> 判断。之所以走绑定而不是在代码里
/// 直接改 Border 属性：卡片是用 StaticResource 拿的样式，直接改本地值会永久
/// 盖掉样式的 hover 触发器，回不去了。
///
/// 注意：这里是「锁定」而不是「禁用」。卡片不整体 Disable —— 总开关本身就在
/// 卡片里面，整卡禁用会连开关一起点不动，用户就永远打不开了。
///
/// <b>勾选框接线：</b>列表项模板在 ModernControls.xaml 里，那是个纯
/// ResourceDictionary（没有 x:Class），XAML 编译器不允许在里面挂事件处理器。
/// 所以从数据这一侧接：SoundChoice / KillIconChoice 都实现了 INotifyPropertyChanged，
/// 往集合里加项时订阅 PropertyChanged；用户点勾选框 → 双向绑定写回 IsPicked
/// → 触发通知 → 同步进配置。
/// </summary>
public partial class MainWindow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>击杀图标卡（总开关 <c>KillBannerEnableCheckBox</c>）。</summary>
    public bool IsKillIconCardLocked => _settings?.KillFeedback?.BannerEnabled != true;

    /// <summary>击杀提示音卡（总开关 <c>KillSoundEnableCheckBox</c>）。</summary>
    public bool IsKillSoundCardLocked => _settings?.KillFeedback?.SoundEnabled != true;

    /// <summary>悬浮窗 › 消息提示音卡（总开关 <c>SoundNotifyEnableCheckBox</c>）。</summary>
    public bool IsSoundNotifyCardLocked => _settings?.EnableSoundNotify != true;

    /// <summary>
    /// 三个锁定态一起刷新。开关一变动就调一次，简单省事 ——
    /// 三个 bool 都是读设置对象的即时值，重算成本可以忽略，
    /// 不值得为每个开关单独写一条依赖。
    /// </summary>
    private void RefreshLockedCards()
    {
        Raise(nameof(IsKillIconCardLocked));
        Raise(nameof(IsKillSoundCardLocked));
        Raise(nameof(IsSoundNotifyCardLocked));
    }

    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ===================== 列表勾选框接线 =====================

    // 两张卡各用一本账。合成一本的话，重建其中一张卡会把另一张卡的钩子也清掉 ——
    // 那样"在 A 卡里改勾选、B 卡不再响应"这种 bug 会非常隐蔽。
    private readonly Dictionary<SoundChoice, PropertyChangedEventHandler> _soundNotifyHandlers = new();
    private readonly Dictionary<SoundChoice, PropertyChangedEventHandler> _killSoundHandlers = new();
    private readonly Dictionary<KillIconChoice, PropertyChangedEventHandler> _killIconHandlers = new();

    /// <summary>给一批提示音项挂上「勾选变了」的通知。</summary>
    private void HookSoundChoiceNotifications(
        Dictionary<SoundChoice, PropertyChangedEventHandler> registry,
        IEnumerable<SoundChoice> choices,
        Action onChanged)
    {
        foreach (var choice in choices)
        {
            // 闭包捕获可变变量：用局部变量接住，避免经典的"就地捕获"问题
            var item = choice;
            void Handler(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(SoundChoice.IsPicked))
                {
                    onChanged();
                }
            }

            registry[item] = Handler;
            item.PropertyChanged += Handler;
        }
    }

    /// <summary>给一批图标项挂上「勾选变了」的通知。</summary>
    private void HookKillIconNotifications(IEnumerable<KillIconChoice> choices)
    {
        foreach (var choice in choices)
        {
            var item = choice;
            void Handler(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(KillIconChoice.IsPicked) || !_killFeedbackUiReady)
                {
                    return;
                }

                SyncBannerPickedFromChoices();
                QueueKillFeedbackSave();
                RefreshKillBannerStatus();
            }

            _killIconHandlers[item] = Handler;
            item.PropertyChanged += Handler;
        }
    }

    /// <summary>把一本账上的钩子全部摘掉。列表重建前调。</summary>
    private void ClearHandlers<T>(Dictionary<T, PropertyChangedEventHandler> registry)
        where T : notnull, INotifyPropertyChanged
    {
        foreach (var pair in registry)
        {
            pair.Key.PropertyChanged -= pair.Value;
        }

        registry.Clear();
    }

    /// <summary>消息提示音的勾选变了：写回配置并刷新状态行。</summary>
    private void OnSoundNotifyPickedChanged()
    {
        // 灌数据期间勾选框会被程序性改动，那不是用户操作，不该回写
        if (_soundNotifyLoading)
        {
            return;
        }

        SyncSoundNotifyPickedFromChoices();
        SaveSoundNotifySettings();
        RefreshSoundNotifyStatus();

        // 刚勾上的那些预热一下，等下抽到才不会慢半拍
        _soundNotifier.Prewarm(PreheatSoundNotifyFiles());
    }

    /// <summary>击杀提示音的勾选变了。语义同上。</summary>
    private void OnKillSoundPickedChanged()
    {
        if (_killSoundLoading)
        {
            return;
        }

        SyncKillSoundPickedFromChoices();
        SaveKillFeedbackSettings();
        RefreshKillSoundStatus();
        _killSoundNotifier.Prewarm(PreheatKillSoundFiles());
    }

    // ===================== 勾选框显隐 =====================

    /// <summary>
    /// 把「随机开关」的状态推给列表项，决定要不要显示勾选框。
    ///
    /// 语义：**关掉随机时列表项不显示勾选框**，直接用当前选中的那一项 ——
    /// 因为随机开关关着的时候勾选框本来就是摆设，留着反而让人以为"勾了才用"。
    /// 开随机才需要勾选（挑出参与抽签的池子）。
    ///
    /// 三张卡（击杀图标 / 击杀提示音 / 消息提示音）各调各的。
    /// </summary>
    private void ApplySoundChoiceCheckBoxVisibility(
        IEnumerable<SoundChoice> choices, bool randomEnabled)
    {
        foreach (var c in choices)
        {
            c.ShowCheckBox = randomEnabled;
        }
    }

    private void ApplyKillIconCheckBoxVisibility(bool randomEnabled)
    {
        foreach (var c in _killIconChoices)
        {
            c.ShowCheckBox = randomEnabled;
        }
    }
}

