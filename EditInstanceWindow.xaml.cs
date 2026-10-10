using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace OrbusRebornManager;

public partial class EditInstanceWindow : Window
{
    private string? _selectedNewImage;
    private bool _ready;
    private bool _dragging;
    private Point _dragPoint;
    private double _dragStartX;
    private double _dragStartY;

    public string DisplayName { get; private set; } = "";
    public string? NewImagePath => _selectedNewImage;
    public bool ResetImage { get; private set; }
    public bool IconFit => FitImageCheck.IsChecked == true;
    public double IconZoom => ZoomSlider.Value;
    public double IconOffsetX => XSlider.Value;
    public double IconOffsetY => YSlider.Value;

    public EditInstanceWindow(GameInstance instance)
    {
        InitializeComponent();

        DisplayName = instance.Name;
        NameText.Text = instance.Name;

        FitImageCheck.IsChecked = instance.IconFit;
        ZoomSlider.Value = Math.Clamp(instance.IconZoom, 1.0, 2.5);
        XSlider.Value = Math.Clamp(instance.IconOffsetX, -80, 80);
        YSlider.Value = Math.Clamp(instance.IconOffsetY, -80, 80);

        ShowPreview(InstanceArtwork.Load(instance.CustomIconPath),
            string.IsNullOrWhiteSpace(instance.CustomIconPath)
                ? "Default Orbus artwork" : "Custom cover");
        _ready = true;
        RefreshFraming();

        Loaded += (_, _) =>
        {
            NameText.Focus();
            NameText.SelectAll();
        };
    }

    private void ShowPreview(ImageSource? source, string label)
    {
        CustomIcon.Source = source;
        BlurredPreview.Source = source;
        CustomIcon.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
        DefaultIcon.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
        ImageStatusText.Text = source == null ? "Default Orbus artwork" : label;

        if (_ready)
            RefreshFraming();
    }

    private void RefreshFraming()
    {
        if (!_ready)
            return;

        bool enabled = CustomIcon.Source != null;
        FitImageCheck.IsEnabled = enabled;
        FramingControls.IsEnabled = enabled;
        ZoomValue.Text = $"{ZoomSlider.Value * 100:0}%";
        XValue.Text = $"{XSlider.Value:0}";
        YValue.Text = $"{YSlider.Value:0}";

        bool fit = FitImageCheck.IsChecked == true;
        CustomIcon.Stretch = fit ? Stretch.Uniform : Stretch.UniformToFill;
        BlurredPreview.Visibility = enabled && fit
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewScale.ScaleX = ZoomSlider.Value;
        PreviewScale.ScaleY = ZoomSlider.Value;
        PreviewMove.X = XSlider.Value;
        PreviewMove.Y = YSlider.Value;
    }

    private void ArtworkSlider_Changed(object sender,
        RoutedPropertyChangedEventArgs<double> e) => RefreshFraming();

    private void ArtworkControls_Changed(object sender, RoutedEventArgs e) =>
        RefreshFraming();

    private void ResetFraming_Click(object sender, RoutedEventArgs e)
    {
        FitImageCheck.IsChecked = false;
        ZoomSlider.Value = 1.0;
        XSlider.Value = 0;
        YSlider.Value = 0;
        RefreshFraming();
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CustomIcon.Source == null || e.ChangedButton != MouseButton.Left)
            return;

        _dragging = true;
        _dragPoint = e.GetPosition(PreviewBox);
        _dragStartX = XSlider.Value;
        _dragStartY = YSlider.Value;
        PreviewBox.CaptureMouse();
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var position = e.GetPosition(PreviewBox);
        XSlider.Value = Math.Clamp(_dragStartX + position.X - _dragPoint.X,
            XSlider.Minimum, XSlider.Maximum);
        YSlider.Value = Math.Clamp(_dragStartY + position.Y - _dragPoint.Y,
            YSlider.Minimum, YSlider.Maximum);
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        if (PreviewBox.IsMouseCaptured)
            PreviewBox.ReleaseMouseCapture();
    }

    private void Preview_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragging = false;
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an instance cover image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            if (new FileInfo(dialog.FileName).Length > 12L * 1024 * 1024)
                throw new InvalidOperationException("Choose an image smaller than 12 MB.");

            ImageSource? preview = InstanceArtwork.Load(dialog.FileName);
            if (preview == null)
                throw new InvalidOperationException("The image couldn't be opened.");

            _selectedNewImage = dialog.FileName;
            ResetImage = false;
            ResetFraming_Click(sender, e);
            ShowPreview(preview, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid image",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        _selectedNewImage = null;
        ResetImage = true;
        ResetFraming_Click(sender, e);
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
