using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OrbusRebornManager;

public partial class EditInstanceWindow : Window
{
    // Stored positions use the existing 164x137 library cover coordinate system.
    // The large editor doubles that size without changing saved instance data.
    private const double CoverWidth = 164;
    private const double CoverHeight = 137;
    private const double PreviewScaleFactor = 2;
    private const double MinZoom = 1;
    private const double MaxZoom = 2.5;
    private const double ZoomStep = .1;

    private string? _selectedNewImage;
    private bool _ready;
    private bool _dragging;
    private bool _fit;
    private double _zoom = 1;
    private double _offsetX;
    private double _offsetY;
    private Point _dragOrigin;
    private double _dragStartX;
    private double _dragStartY;

    public string DisplayName { get; private set; } = "";
    public string? NewImagePath => _selectedNewImage;
    public bool ResetImage { get; private set; }
    public bool IconFit => _fit;
    public double IconZoom => _zoom;
    public double IconOffsetX => _offsetX;
    public double IconOffsetY => _offsetY;

    public EditInstanceWindow(GameInstance instance)
    {
        InitializeComponent();

        DisplayName = instance.Name;
        NameText.Text = instance.Name;
        PreviewName.Text = instance.Name;
        PreviewKind.Text = instance.CreatedByManager
            ? "Modded copy" : "Existing installation";

        _fit = instance.IconFit;
        _zoom = Math.Clamp(instance.IconZoom, MinZoom, MaxZoom);
        _offsetX = Math.Clamp(instance.IconOffsetX, -80, 80);
        _offsetY = Math.Clamp(instance.IconOffsetY, -80, 80);

        ShowPreview(InstanceArtwork.Load(instance.CustomIconPath),
            "Custom cover");
        _ready = true;
        RefreshFraming();

        Loaded += (_, _) => NameText.Focus();
    }

    private void NameText_TextChanged(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (PreviewName != null)
            PreviewName.Text = string.IsNullOrWhiteSpace(NameText.Text)
                ? "Unnamed instance" : NameText.Text.Trim();
    }

    private void ShowPreview(ImageSource? source, string label)
    {
        CustomIcon.Source = source;
        BlurredPreview.Source = source;
        bool hasImage = source != null;

        CustomIcon.Visibility = hasImage
            ? Visibility.Visible : Visibility.Collapsed;
        DefaultIcon.Visibility = hasImage
            ? Visibility.Collapsed : Visibility.Visible;
        ImageStatusText.Text = hasImage ? label : "Using original Orbus artwork";

        if (_ready)
            RefreshFraming();
    }

    private void ClampPosition()
    {
        if (CustomIcon.Source is not BitmapSource bitmap)
        {
            _offsetX = _offsetY = 0;
            return;
        }

        double imageWidth = Math.Max(1, bitmap.PixelWidth);
        double imageHeight = Math.Max(1, bitmap.PixelHeight);

        double scale = _fit
            ? Math.Min(CoverWidth / imageWidth, CoverHeight / imageHeight)
            : Math.Max(CoverWidth / imageWidth, CoverHeight / imageHeight);

        // Prevent dragging a filled cover beyond its painted image.
        // Fit mode keeps the full image centered until it is zoomed enough to pan.
        double maxX = Math.Max(0, (imageWidth * scale * _zoom - CoverWidth) / 2);
        double maxY = Math.Max(0, (imageHeight * scale * _zoom - CoverHeight) / 2);
        _offsetX = Math.Clamp(_offsetX, -Math.Min(80, maxX), Math.Min(80, maxX));
        _offsetY = Math.Clamp(_offsetY, -Math.Min(80, maxY), Math.Min(80, maxY));
    }

    private void RefreshFraming()
    {
        if (!_ready)
            return;

        bool enabled = CustomIcon.Source != null;
        ClampPosition();

        CustomIcon.Stretch = _fit ? Stretch.Uniform : Stretch.UniformToFill;
        BlurredPreview.Visibility = enabled && _fit
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewScale.ScaleX = _zoom;
        PreviewScale.ScaleY = _zoom;
        PreviewMove.X = _offsetX * PreviewScaleFactor;
        PreviewMove.Y = _offsetY * PreviewScaleFactor;

        ZoomValue.Text = $"{_zoom * 100:0}%";
        ZoomOutButton.IsEnabled = enabled && _zoom > MinZoom + .001;
        ZoomInButton.IsEnabled = enabled && _zoom < MaxZoom - .001;
        FillButton.IsEnabled = enabled;
        FitButton.IsEnabled = enabled;
        PreviewBox.Cursor = enabled ? Cursors.SizeAll : Cursors.Arrow;

        var active = new SolidColorBrush(Color.FromRgb(35, 77, 66));
        var inactive = new SolidColorBrush(Color.FromRgb(51, 52, 61));
        FillButton.Background = !_fit && enabled ? active : inactive;
        FitButton.Background = _fit && enabled ? active : inactive;
        FillButton.BorderBrush = !_fit && enabled
            ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("StrokeBrush");
        FitButton.BorderBrush = _fit && enabled
            ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("StrokeBrush");
    }

    private void ChangeZoom(double difference)
    {
        if (CustomIcon.Source == null) return;
        _zoom = Math.Clamp(Math.Round((_zoom + difference) * 10) / 10,
            MinZoom, MaxZoom);
        RefreshFraming();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) =>
        ChangeZoom(-ZoomStep);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) =>
        ChangeZoom(ZoomStep);

    private void Fill_Click(object sender, RoutedEventArgs e)
    {
        _fit = false;
        RefreshFraming();
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        _fit = true;
        RefreshFraming();
    }

    private void ResetFraming_Click(object sender, RoutedEventArgs e)
    {
        _fit = false;
        _zoom = 1;
        _offsetX = 0;
        _offsetY = 0;
        RefreshFraming();
    }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CustomIcon.Source == null) return;
        ChangeZoom(e.Delta > 0 ? ZoomStep : -ZoomStep);
        e.Handled = true;
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CustomIcon.Source == null || e.ChangedButton != MouseButton.Left)
            return;

        _dragOrigin = e.GetPosition(PreviewBox);
        _dragStartX = _offsetX;
        _dragStartY = _offsetY;
        _dragging = true;
        PreviewBox.CaptureMouse();
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Point p = e.GetPosition(PreviewBox);
        _offsetX = _dragStartX + (p.X - _dragOrigin.X) / PreviewScaleFactor;
        _offsetY = _dragStartY + (p.Y - _dragOrigin.Y) / PreviewScaleFactor;
        RefreshFraming();
        e.Handled = true;
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        if (PreviewBox.IsMouseCaptured)
            PreviewBox.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Preview_LostMouseCapture(object sender, MouseEventArgs e) =>
        _dragging = false;

    private void Preview_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasSupportedDrop(e.Data) ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Preview_Drop(object sender, DragEventArgs e)
    {
        if (!HasSupportedDrop(e.Data)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length == 1)
            LoadCandidateImage(files[0]);
        e.Handled = true;
    }

    private static bool HasSupportedDrop(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] files ||
            files.Length != 1) return false;
        return IsSupportedExtension(files[0]);
    }

    private static bool IsSupportedExtension(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp";

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an instance cover image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dialog.ShowDialog(this) == true)
            LoadCandidateImage(dialog.FileName);
    }

    private void LoadCandidateImage(string file)
    {
        try
        {
            if (!IsSupportedExtension(file) || !File.Exists(file))
                throw new InvalidOperationException("Choose a PNG, JPG or BMP image.");
            if (new FileInfo(file).Length > 12L * 1024 * 1024)
                throw new InvalidOperationException("Choose an image smaller than 12 MB.");

            ImageSource preview = InstanceArtwork.Load(file)
                ?? throw new InvalidOperationException("The selected image couldn't be opened.");

            _selectedNewImage = file;
            ResetImage = false;
            _fit = false;
            _zoom = 1;
            _offsetX = _offsetY = 0;
            ShowPreview(preview, Path.GetFileName(file));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid cover image",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        _selectedNewImage = null;
        ResetImage = true;
        _fit = false;
        _zoom = 1;
        _offsetX = _offsetY = 0;
        ShowPreview(null, "");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameText.Text.Trim();
        if (name.Length == 0 || name.Length > 50 || name.Any(char.IsControl))
        {
            MessageBox.Show(this,
                "Use a display name between 1 and 50 characters without control characters.",
                "Invalid instance name", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DisplayName = name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }
}
