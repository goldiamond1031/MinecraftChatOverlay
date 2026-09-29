using System.Windows;

namespace MinecraftChatOverlay.Plugins.AutoGg;

/// <summary>自动 GG 的设置页面（原来的 AutoGgPanel 整块搬过来）。</summary>
public partial class AutoGgPage : System.Windows.Controls.UserControl
{
    private readonly AutoGgPlugin _plugin;
    private bool _loading;

    public AutoGgPage(AutoGgPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        LoadFromSettings();

        AutoGgTriggerTextBox.LostFocus += (_, _) => SaveToSettings();
        AutoGgChatKeyTextBox.LostFocus += (_, _) => SaveToSettings();
        AutoGgTextTextBox.LostFocus += (_, _) => SaveToSettings();
        AutoGgUseClipboardCheckBox.Checked += (_, _) => SaveToSettings();
        AutoGgUseClipboardCheckBox.Unchecked += (_, _) => SaveToSettings();
        AutoGgSendAgainCheckBox.Checked += (_, _) => SaveToSettings();
        AutoGgSendAgainCheckBox.Unchecked += (_, _) => SaveToSettings();
    }

    /// <summary>把设置填进控件。</summary>
    public void LoadFromSettings()
    {
        // 关键：填控件的时候会触发 Checked/LostFocus 回调，
        // 不加这个闸门就会把"还没填上的空控件"当成用户输入写回设置（真踩过）。
        _loading = true;
        try
        {
            var settings = _plugin.Settings;
            EnableAutoGgCheckBox.IsChecked = settings.Enabled;
            AutoGgTriggerTextBox.Text = settings.TriggerPattern;
            AutoGgChatKeyTextBox.Text = settings.ChatKey;
            AutoGgTextTextBox.Text = settings.Text;
            AutoGgUseClipboardCheckBox.IsChecked = settings.UseClipboard;
            AutoGgSendAgainCheckBox.IsChecked = settings.SendAgainAfterGg;
        }
        finally
        {
            _loading = false;
        }

        UpdateBodyVisibility();
        UpdateLastTriggerText();
    }

    /// <summary>把控件读回设置并存盘。</summary>
    public void SaveToSettings()
    {
        if (_loading)
        {
            return;
        }

        var settings = _plugin.Settings;
        settings.Enabled = EnableAutoGgCheckBox.IsChecked == true;
        settings.TriggerPattern = AutoGgTriggerTextBox.Text.Trim();
        settings.ChatKey = AutoGgChatKeyTextBox.Text.Trim();
        settings.Text = AutoGgTextTextBox.Text;
        settings.UseClipboard = AutoGgUseClipboardCheckBox.IsChecked == true;
        settings.SendAgainAfterGg = AutoGgSendAgainCheckBox.IsChecked == true;
        _plugin.SaveSettings();
    }

    /// <summary>上次触发的显示（发送成功后插件会调）。</summary>
    public void UpdateLastTriggerText()
    {
        if (AutoGgLastTriggerText is null)
        {
            return;
        }

        var at = _plugin.Settings.LastTriggerAt;
        AutoGgLastTriggerText.Text = at == DateTime.MinValue
            ? "上次触发：从未"
            : "上次触发：" + at.ToString("HH:mm:ss");
    }

    private void EnableAutoGgCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateBodyVisibility();
        SaveToSettings();
    }

    private void AutoGgResetButton_Click(object sender, RoutedEventArgs e)
    {
        AutoGgTriggerTextBox.Text = @"恭喜! .+? 获得胜利!";
        AutoGgChatKeyTextBox.Text = "t";
        AutoGgTextTextBox.Text = "gg";
        AutoGgUseClipboardCheckBox.IsChecked = true;
        AutoGgSendAgainCheckBox.IsChecked = false;
        SaveToSettings();
        _plugin.Host.ShowToast("自动 GG 已恢复默认设置");
    }

    private void UpdateBodyVisibility()
    {
        AutoGgBody.Visibility = EnableAutoGgCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
}