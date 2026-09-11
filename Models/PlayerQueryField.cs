using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MinecraftChatOverlay.Models;

/// <summary>拖拽时，某个行是"插入点"，以及插入线画在它的上面还是下面。</summary>
public enum PlayerQueryFieldDropTarget
{
    None = 0,

    /// <summary>插入线画在这一行的上边。</summary>
    Before = 1,

    /// <summary>插入线画在这一行的下边。</summary>
    After = 2
}

/// <summary>玩家查询显示字段：自定义显示名 + JSON 路径。</summary>
public sealed class PlayerQueryField : INotifyPropertyChanged
{
    private bool _isDragging;
    private PlayerQueryFieldDropTarget _dropTarget;

    private string _label = "";
    private string _path = "";

    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    /// <summary>
    /// JSON 路径。既可以在下拉里选，也可以直接手敲，所以需要通知 ——
    /// 从下拉选中时要把值写回输入框。
    /// </summary>
    public string Path
    {
        get => _path;
        set => SetField(ref _path, value);
    }

    /// <summary>
    /// 这一行正被拖动。界面据此把它画成"拿起来了"的样子。
    /// 拖拽过程中界面要跟着变，所以必须发通知。
    /// </summary>
    public bool IsDragging
    {
        get => _isDragging;
        set => SetField(ref _isDragging, value);
    }

    /// <summary>当前拖动的行会插到这一行的上面还是下面。界面据此画插入指示线。</summary>
    public PlayerQueryFieldDropTarget DropTarget
    {
        get => _dropTarget;
        set => SetField(ref _dropTarget, value);
    }

    public PlayerQueryField Clone() => new() { Label = Label, Path = Path };

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
