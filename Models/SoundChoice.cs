using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace MinecraftChatOverlay.Models;

/// <summary>
/// 提示音列表里的一项。v8 起列表里只会有用户自己导入的文件 ——
/// 软件不再附带内置音效，所以这里也没有「内置 / 自定义」的分别了。
///
/// 每一项带一个 <see cref="IsPicked"/>，就是列表项左边那个勾选框。
/// 开「随机播放」后才显示勾选框，从勾中的项里抽一个；
/// 关掉随机时勾选框藏起来，直接用**列表里选中的那一项**（见 <see cref="ShowCheckBox"/>）。
/// </summary>
public sealed class SoundChoice : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isPicked = true;
    private bool _showCheckBox = true;

    /// <summary>显示名，取文件名。</summary>
    public string Name { get; set; } = "";

    /// <summary>文件绝对路径。</summary>
    public string File { get; set; } = "";

    /// <summary>
    /// 是否勾选（参与随机抽取）。默认勾上 —— 用户刚导入一个音，显然是想用它。
    /// </summary>
    public bool IsPicked
    {
        get => _isPicked;
        set
        {
            if (_isPicked == value)
            {
                return;
            }

            _isPicked = value;
            Raise();
        }
    }

    /// <summary>
    /// 要不要显示勾选框。由「随机播放」开关驱动：
    /// 关掉随机时勾选框没有意义（用选中的那个），藏起来免得用户以为"勾了才用"。
    /// 绑定到勾选框的 Visibility，用一个 bool→Visibility 的转换器。
    /// </summary>
    public bool ShowCheckBox
    {
        get => _showCheckBox;
        set
        {
            if (_showCheckBox == value)
            {
                return;
            }

            _showCheckBox = value;
            Raise();
        }
    }

    /// <summary>从路径造一项。</summary>
    public static SoundChoice FromFile(string path) => new()
    {
        Name = SafeName(path),
        File = path,
        IsPicked = true
    };

    private static string SafeName(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            // 路径里有非法字符时不要让它崩在列表渲染上
            return path;
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => Name;
}
