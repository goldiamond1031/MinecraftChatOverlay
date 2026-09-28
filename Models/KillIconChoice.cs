using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MinecraftChatOverlay;

/// <summary>
/// 设置页里「击杀图标」列表的一项 —— 用户导入的一张图片。
///
/// v8 起软件不再附带内置矢量图标，所以这个类只剩"用户图片"这一种来源。
/// 每项带一个勾选框（<see cref="IsPicked"/>），开「随机显示」后才显示，
/// 每次击杀从勾中的项里抽一个；关掉随机时勾选框藏起来，
/// 直接用**列表里选中的那一项**（见 <see cref="ShowCheckBox"/>）。
/// </summary>
public sealed class KillIconChoice : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isPicked = true;
    private bool _showCheckBox = true;
    private BitmapSource? _thumb;
    private bool _thumbTried;

    public KillIconChoice(string path)
    {
        File = path;
        Name = SafeName(path);
    }

    /// <summary>图片绝对路径。</summary>
    public string File { get; }

    /// <summary>列表里显示的名字。</summary>
    public string Name { get; }

    /// <summary>是否勾选（参与随机抽取）。</summary>
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
            Raise(nameof(IsPicked));
        }
    }

    /// <summary>
    /// 要不要显示勾选框。由「随机显示」开关驱动：关掉随机时勾选框没意义
    /// （用选中的那个），藏起来免得用户以为"勾了才用"。
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
            Raise(nameof(ShowCheckBox));
        }
    }

    /// <summary>
    /// 列表左边那个小预览图。
    ///
    /// 懒加载：列表可能有几十项，构造时全读一遍会卡住 UI，而且图片解码本身不便宜。
    /// 绑定一取到这个属性才去读盘。读失败返回 null —— 列表项会退化成"只有一个文件名"
    /// （界面上有对应的提示），比整个列表崩掉好。
    ///
    /// 用 <c>OnLoad</c> + <c>Freeze</c>：前者立刻把文件读完、不占着文件句柄，
    /// 后者让这张图能被多线程共享，离开 UI 线程用也不会抛。
    /// </summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbTried)
            {
                return _thumb;
            }

            _thumbTried = true;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.DecodePixelWidth = 40;
                image.UriSource = new Uri(File, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                _thumb = image;
            }
            catch
            {
                _thumb = null;
            }

            return _thumb;
        }
    }

    /// <summary>缩略图读不出来的话，界面上要标一下，别让用户对着空白猜。</summary>
    public bool HasThumbnail => Thumbnail != null;

    private static string SafeName(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => Name;
}
