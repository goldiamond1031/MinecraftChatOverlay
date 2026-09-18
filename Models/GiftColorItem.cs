using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MinecraftChatOverlay.Models;

/// <summary>
/// 礼物颜色配置项。
/// 实现 INotifyPropertyChanged 是为了「改完颜色立刻能看到」——
/// 否则 DataGrid 不会刷新，改完颜色要重新选中/滚动才能看见新色。
/// </summary>
public sealed class GiftColorItem : INotifyPropertyChanged
{
    private string _giftName = "";
    private string _color = "#FFFFFF";

    public string GiftName
    {
        get => _giftName;
        set => SetField(ref _giftName, value);
    }

    public string Color
    {
        get => _color;
        set => SetField(ref _color, value);
    }

    public GiftColorItem()
    {
    }

    public GiftColorItem(string giftName, string color)
    {
        _giftName = giftName;
        _color = color;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
