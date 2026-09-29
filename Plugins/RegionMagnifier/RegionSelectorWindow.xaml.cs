using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;


namespace MinecraftChatOverlay.Plugins.RegionMagnifier;

/// <summary>
/// 框选层：全屏半透明，拖出一个矩形当作「区域放大」的选区。
///
/// 特意做了两件对"框物品栏最后一格"很关键的事：
///  · 光标旁边的放大镜（最近邻放大，48×48 物理像素）—— 20 像素的小格子靠肉眼对不准；
///  · 方向键微调（Shift=10 像素）、回车/双击确认、Esc 取消。
///
/// 结果用**物理屏幕像素**给出（<see cref="ResultX"/> 等），换算成"相对客户区"由调用方做。
/// </summary>
public partial class RegionSelectorWindow : Window
{
    /// <summary>放大镜取多大一块源区域（物理像素）。</summary>
    private const int LoupeSourceSize = 48;

    private readonly IntPtr _targetHandle;
    private readonly RegionFrameGrabber _loupeGrabber = new();

    private WriteableBitmap? _loupeBitmap;
    private Point _dragStart;
    private bool _dragging;

    private double _selLeft;
    private double _selTop;
    private double _selWidth;
    private double _selHeight;

    private double _scaleX = 1;
    private double _scaleY = 1;

    public int ResultX { get; private set; }

    public int ResultY { get; private set; }

    public int ResultWidth { get; private set; }

    public int ResultHeight { get; private set; }

    /// <param name="targetHandle">游戏窗口句柄，用来画客户区虚线轮廓；没有就传 IntPtr.Zero。</param>
    public RegionSelectorWindow(IntPtr targetHandle)
    {
        _targetHandle = targetHandle;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0)
            {
                _scaleX = dpi.DpiScaleX;
            }

            if (dpi.DpiScaleY > 0)
            {
                _scaleY = dpi.DpiScaleY;
            }

            DimOuter.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
            ShowClientOutline();
        };

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonDown += (_, _) => Cancel();
        MouseDoubleClick += (_, _) => { if (_selWidth > 0 && _selHeight > 0) { Commit(); } };
        KeyDown += OnKeyDown;
    }

    /// <summary>把游戏窗口客户区画成虚线框，方便对齐。</summary>
    private void ShowClientOutline()
    {
        if (_targetHandle == IntPtr.Zero
            || !RegionWindowFinder.TryGetClientRectOnScreen(_targetHandle, out var x, out var y, out var width, out var height))
        {
            return;
        }

        ClientOutline.Margin = new Thickness(x / _scaleX, y / _scaleY, 0, 0);
        ClientOutline.Width = width / _scaleX;
        ClientOutline.Height = height / _scaleY;
        ClientOutline.HorizontalAlignment = HorizontalAlignment.Left;
        ClientOutline.VerticalAlignment = VerticalAlignment.Top;
        ClientOutline.Visibility = Visibility.Visible;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(Root);
        _selLeft = _dragStart.X;
        _selTop = _dragStart.Y;
        _selWidth = 0;
        _selHeight = 0;
        SelBand.Visibility = Visibility.Visible;
        Readout.Visibility = Visibility.Visible;
        UpdateSelectionVisuals();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(Root);

        if (_dragging)
        {
            _selLeft = Math.Min(_dragStart.X, point.X);
            _selTop = Math.Min(_dragStart.Y, point.Y);
            _selWidth = Math.Abs(point.X - _dragStart.X);
            _selHeight = Math.Abs(point.Y - _dragStart.Y);
            UpdateSelectionVisuals();
        }

        UpdateLoupe(point);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;

        // 只是点了一下没拖出面积：当成"重新开始框选"
        if (_selWidth < 2 || _selHeight < 2)
        {
            SelBand.Visibility = Visibility.Collapsed;
            Readout.Visibility = Visibility.Collapsed;
            DimHole.Rect = new Rect(0, 0, 0, 0);
        }
    }

    private void UpdateSelectionVisuals()
    {
        SelBand.Margin = new Thickness(_selLeft, _selTop, 0, 0);
        SelBand.Width = _selWidth;
        SelBand.Height = _selHeight;
        SelBand.HorizontalAlignment = HorizontalAlignment.Left;
        SelBand.VerticalAlignment = VerticalAlignment.Top;

        DimHole.Rect = new Rect(_selLeft, _selTop, _selWidth, _selHeight);

        var px = (int)Math.Round(_selLeft * _scaleX);
        var py = (int)Math.Round(_selTop * _scaleY);
        var pw = (int)Math.Round(_selWidth * _scaleX);
        var ph = (int)Math.Round(_selHeight * _scaleY);

        var clientBase = "";
        if (_targetHandle != IntPtr.Zero
            && RegionWindowFinder.TryGetClientRectOnScreen(_targetHandle, out var cx, out var cy, out _, out _))
        {
            clientBase = $"  客户区({px - cx},{py - cy})";
        }

        ReadoutText.Text = $"{pw} × {ph}   屏幕({px},{py}){clientBase}";

        // 读数摆在选区上方；贴到顶就摆里面
        var readoutTop = _selTop - 34;
        if (readoutTop < 4)
        {
            readoutTop = _selTop + 6;
        }

        Readout.Margin = new Thickness(Math.Max(4, _selLeft), readoutTop, 0, 0);
        Readout.HorizontalAlignment = HorizontalAlignment.Left;
        Readout.VerticalAlignment = VerticalAlignment.Top;
    }

    /// <summary>光标旁边实时放大，帮用户对准小格子。</summary>
    private void UpdateLoupe(Point cursor)
    {
        var centerX = (int)Math.Round(cursor.X * _scaleX);
        var centerY = (int)Math.Round(cursor.Y * _scaleY);
        var sourceX = centerX - LoupeSourceSize / 2;
        var sourceY = centerY - LoupeSourceSize / 2;

        if (!_loupeGrabber.Capture(sourceX, sourceY, LoupeSourceSize, LoupeSourceSize))
        {
            LoupeBox.Visibility = Visibility.Collapsed;
            return;
        }

        if (_loupeBitmap is null)
        {
            _loupeBitmap = new WriteableBitmap(LoupeSourceSize, LoupeSourceSize, 96, 96, PixelFormats.Bgr32, null);
            Loupe.Source = _loupeBitmap;
        }

        _loupeGrabber.CopyTo(_loupeBitmap);
        LoupeBox.Visibility = Visibility.Visible;

        // 放大镜默认摆在光标右下方；贴边就翻到左上方
        var boxSize = LoupeBox.Width;
        var left = cursor.X + 24;
        var top = cursor.Y + 24;
        if (left + boxSize > ActualWidth - 8)
        {
            left = cursor.X - 24 - boxSize;
        }

        if (top + boxSize > ActualHeight - 8)
        {
            top = cursor.Y - 24 - boxSize;
        }

        LoupeBox.Margin = new Thickness(Math.Max(2, left), Math.Max(2, top), 0, 0);
        LoupeBox.HorizontalAlignment = HorizontalAlignment.Left;
        LoupeBox.VerticalAlignment = VerticalAlignment.Top;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;

        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                return;

            case Key.Enter:
                if (_selWidth > 0 && _selHeight > 0)
                {
                    Commit();
                }

                e.Handled = true;
                return;

            case Key.Left:
                Nudge(-step, 0);
                break;

            case Key.Right:
                Nudge(step, 0);
                break;

            case Key.Up:
                Nudge(0, -step);
                break;

            case Key.Down:
                Nudge(0, step);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void Nudge(double dx, double dy)
    {
        if (_selWidth <= 0 || _selHeight <= 0)
        {
            return;
        }

        _selLeft += dx;
        _selTop += dy;
        UpdateSelectionVisuals();
    }

    private void Commit()
    {
        var px = (int)Math.Round(_selLeft * _scaleX);
        var py = (int)Math.Round(_selTop * _scaleY);
        var pw = Math.Max(1, (int)Math.Round(_selWidth * _scaleX));
        var ph = Math.Max(1, (int)Math.Round(_selHeight * _scaleY));

        ResultX = px;
        ResultY = py;
        ResultWidth = pw;
        ResultHeight = ph;
        DialogResult = true;
    }

    private void Cancel()
    {
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _loupeGrabber.Dispose();
    }
}