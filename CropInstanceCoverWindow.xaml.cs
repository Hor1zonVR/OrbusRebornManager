using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace OrbusRebornManager;

/// <summary>
/// A non-destructive 164:137 selection over the ENTIRE original photograph.
/// Users choose a crop before it is displayed in the library; the stored source
/// image never gets replaced by a destructive crop.
/// </summary>
public partial class CropInstanceCoverWindow : Window
{
    private const double Ratio = 164.0 / 137.0;
    private readonly BitmapSource _bitmap;
    private readonly Rect? _existing;
    private Rect _photoBounds;
    private Rect _selection;
    private Rect _dragStartSelection;
    private Point _dragStart;
    private DragAction _action = DragAction.None;

    private enum DragAction { None, Move, TopLeft, TopRight, BottomLeft, BottomRight }

    public Rect SelectedCrop { get; private set; }

    public CropInstanceCoverWindow(string sourcePath, Rect? existing = null)
    {
        InitializeComponent();
        _bitmap = InstanceArtwork.Load(sourcePath) as BitmapSource
            ?? throw new InvalidOperationException("The selected image couldn't be opened.");
        _existing = existing;
        FullPhoto.Source = _bitmap;
        Loaded += (_, _) => InitializeImage();
    }

    private void InitializeImage()
    {
        const double margin = 14;
        double width = CropCanvas.Width - margin * 2;
        double height = CropCanvas.Height - margin * 2;
        double imageScale = Math.Min(width / _bitmap.PixelWidth,
            height / _bitmap.PixelHeight);

        width = _bitmap.PixelWidth * imageScale;
        height = _bitmap.PixelHeight * imageScale;
        _photoBounds = new Rect(
            (CropCanvas.Width - width) / 2,
            (CropCanvas.Height - height) / 2, width, height);

        FullPhoto.Width = width;
        FullPhoto.Height = height;
        Canvas.SetLeft(FullPhoto, _photoBounds.Left);
        Canvas.SetTop(FullPhoto, _photoBounds.Top);

        if (_existing is Rect saved &&
            saved.Width > 0 && saved.Height > 0 &&
            saved.X >= 0 && saved.Y >= 0 &&
            saved.Right <= 1.00001 && saved.Bottom <= 1.00001)
        {
            double cropW = Math.Clamp(saved.Width * width, 1, width);
            double cropH = cropW / Ratio;
            if (cropH > height)
            {
                cropH = height;
                cropW = cropH * Ratio;
            }
            double x = _photoBounds.Left + saved.X * width;
            double y = _photoBounds.Top + saved.Y * height;
            x = Math.Clamp(x, _photoBounds.Left, _photoBounds.Right - cropW);
            y = Math.Clamp(y, _photoBounds.Top, _photoBounds.Bottom - cropH);
            _selection = new Rect(x, y, cropW, cropH);
        }
        else
        {
            ResetSelection();
        }
        DrawSelection();
    }

    private void ResetSelection()
    {
        double maximumWidth = Math.Min(_photoBounds.Width,
            _photoBounds.Height * Ratio);
        double selectedWidth = maximumWidth * .88;
        double selectedHeight = selectedWidth / Ratio;
        _selection = new Rect(
            _photoBounds.Left + (_photoBounds.Width - selectedWidth) / 2,
            _photoBounds.Top + (_photoBounds.Height - selectedHeight) / 2,
            selectedWidth, selectedHeight);
    }

    private static void Put(Rectangle r, double left, double top,
        double width, double height)
    {
        Canvas.SetLeft(r, left);
        Canvas.SetTop(r, top);
        r.Width = Math.Max(0, width);
        r.Height = Math.Max(0, height);
    }

    private void DrawSelection()
    {
        if (_selection.Width <= 0) return;

        Canvas.SetLeft(CropFrame, _selection.Left);
        Canvas.SetTop(CropFrame, _selection.Top);
        CropFrame.Width = _selection.Width;
        CropFrame.Height = _selection.Height;

        Put(ShadeTop, 0, 0, CropCanvas.Width, _selection.Top);
        Put(ShadeBottom, 0, _selection.Bottom,
            CropCanvas.Width, CropCanvas.Height - _selection.Bottom);
        Put(ShadeLeft, 0, _selection.Top,
            _selection.Left, _selection.Height);
        Put(ShadeRight, _selection.Right, _selection.Top,
            CropCanvas.Width - _selection.Right, _selection.Height);

        var normalized = NormalizedCrop();
        CropInfo.Text =
            $"Cover crop · {normalized.Width * 100:0}% of photo width · 164:137 ratio";
    }

    private Rect NormalizedCrop() => new(
        Math.Clamp((_selection.Left - _photoBounds.Left) / _photoBounds.Width, 0, 1),
        Math.Clamp((_selection.Top - _photoBounds.Top) / _photoBounds.Height, 0, 1),
        Math.Clamp(_selection.Width / _photoBounds.Width, 0, 1),
        Math.Clamp(_selection.Height / _photoBounds.Height, 0, 1));

    private DragAction DetermineAction(Point p)
    {
        const double tolerance = 15;
        bool left = Math.Abs(p.X - _selection.Left) <= tolerance;
        bool right = Math.Abs(p.X - _selection.Right) <= tolerance;
        bool top = Math.Abs(p.Y - _selection.Top) <= tolerance;
        bool bottom = Math.Abs(p.Y - _selection.Bottom) <= tolerance;

        if (left && top) return DragAction.TopLeft;
        if (right && top) return DragAction.TopRight;
        if (left && bottom) return DragAction.BottomLeft;
        if (right && bottom) return DragAction.BottomRight;
        return _selection.Contains(p) ? DragAction.Move : DragAction.None;
    }

    private void Crop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _dragStart = e.GetPosition(CropCanvas);
        _dragStartSelection = _selection;
        _action = DetermineAction(_dragStart);

        if (_action == DragAction.None && _photoBounds.Contains(_dragStart))
        {
            // Clicking elsewhere on the picture re-centres the selection.
            _selection.X = Math.Clamp(_dragStart.X - _selection.Width / 2,
                _photoBounds.Left, _photoBounds.Right - _selection.Width);
            _selection.Y = Math.Clamp(_dragStart.Y - _selection.Height / 2,
                _photoBounds.Top, _photoBounds.Bottom - _selection.Height);
            _dragStartSelection = _selection;
            _action = DragAction.Move;
            DrawSelection();
        }

        if (_action != DragAction.None)
        {
            CropCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Crop_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(CropCanvas);
        if (_action == DragAction.None)
        {
            CropCanvas.Cursor = DetermineAction(p) switch
            {
                DragAction.TopLeft or DragAction.BottomRight => Cursors.SizeNWSE,
                DragAction.TopRight or DragAction.BottomLeft => Cursors.SizeNESW,
                DragAction.Move => Cursors.SizeAll,
                _ => Cursors.Arrow
            };
            return;
        }

        double dx = p.X - _dragStart.X;
        double dy = p.Y - _dragStart.Y;
        if (_action == DragAction.Move)
        {
            _selection.X = Math.Clamp(_dragStartSelection.X + dx,
                _photoBounds.Left, _photoBounds.Right - _selection.Width);
            _selection.Y = Math.Clamp(_dragStartSelection.Y + dy,
                _photoBounds.Top, _photoBounds.Bottom - _selection.Height);
        }
        else
        {
            bool movingLeft = _action is DragAction.TopLeft or DragAction.BottomLeft;
            bool movingTop = _action is DragAction.TopLeft or DragAction.TopRight;

            double anchorX = movingLeft ? _dragStartSelection.Right :
                _dragStartSelection.Left;
            double anchorY = movingTop ? _dragStartSelection.Bottom :
                _dragStartSelection.Top;

            double maxX = movingLeft ? anchorX - _photoBounds.Left :
                _photoBounds.Right - anchorX;
            double maxY = movingTop ? anchorY - _photoBounds.Top :
                _photoBounds.Bottom - anchorY;
            double maxWidth = Math.Min(maxX, maxY * Ratio);

            double requestedWidthX = _dragStartSelection.Width + (movingLeft ? -dx : dx);
            double requestedWidthY = _dragStartSelection.Width +
                (movingTop ? -dy : dy) * Ratio;
            double width = Math.Abs(dx) > Math.Abs(dy) * Ratio
                ? requestedWidthX : requestedWidthY;
            width = Math.Clamp(width, Math.Min(22, maxWidth), maxWidth);

            double height = width / Ratio;
            _selection = new Rect(movingLeft ? anchorX - width : anchorX,
                movingTop ? anchorY - height : anchorY, width, height);
        }

        DrawSelection();
        e.Handled = true;
    }

    private void Crop_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_action == DragAction.None) return;
        _action = DragAction.None;
        if (CropCanvas.IsMouseCaptured) CropCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Crop_LostMouseCapture(object sender, MouseEventArgs e) =>
        _action = DragAction.None;

    private void ChangeSize(double factor)
    {
        double maxWidth = Math.Min(_photoBounds.Width, _photoBounds.Height * Ratio);
        double width = Math.Clamp(_selection.Width * factor,
            Math.Min(22, maxWidth), maxWidth);
        double height = width / Ratio;
        double x = Math.Clamp(_selection.X + (_selection.Width - width) / 2,
            _photoBounds.Left, _photoBounds.Right - width);
        double y = Math.Clamp(_selection.Y + (_selection.Height - height) / 2,
            _photoBounds.Top, _photoBounds.Bottom - height);
        _selection = new Rect(x, y, width, height);
        DrawSelection();
    }

    private void Crop_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangeSize(e.Delta > 0 ? 1.075 : 1 / 1.075);
        e.Handled = true;
    }

    private void Smaller_Click(object sender, RoutedEventArgs e) => ChangeSize(.9);
    private void Larger_Click(object sender, RoutedEventArgs e) => ChangeSize(1 / .9);

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ResetSelection();
        DrawSelection();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        SelectedCrop = NormalizedCrop();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
