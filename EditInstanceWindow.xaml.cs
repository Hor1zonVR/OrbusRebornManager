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
    private readonly string _savedSourcePath;
    private string? _selectedNewImage;
    private bool _hasCrop;
    private Rect _crop;
    private bool _legacyFit;
    private double _legacyZoom;
    private double _legacyOffsetX;
    private double _legacyOffsetY;

    public string DisplayName { get; private set; } = "";
    public string? NewImagePath => _selectedNewImage;
    public bool ResetImage { get; private set; }

    // Old card framing is preserved until the player explicitly chooses
    // a crop. This avoids modifying previously saved artwork on a name edit.
    public bool IconFit => _hasCrop ? false : _legacyFit;
    public double IconZoom => _hasCrop ? 1 : _legacyZoom;
    public double IconOffsetX => _hasCrop ? 0 : _legacyOffsetX;
    public double IconOffsetY => _hasCrop ? 0 : _legacyOffsetY;
    public bool IconHasCrop => _hasCrop;
    public Rect IconCrop => _crop;

    public EditInstanceWindow(GameInstance instance)
    {
        InitializeComponent();

        DisplayName = instance.Name;
        NameText.Text = instance.Name;
        PreviewName.Text = instance.Name;
        PreviewKind.Text = instance.CreatedByManager
            ? "Modded copy" : "Existing installation";

        _savedSourcePath = instance.CustomIconPath;
        _hasCrop = instance.IconHasCrop;
        _crop = InstanceArtwork.SavedCrop(instance);
        _legacyFit = instance.IconFit;
        _legacyZoom = Math.Clamp(instance.IconZoom, 1, 2.5);
        _legacyOffsetX = instance.IconOffsetX;
        _legacyOffsetY = instance.IconOffsetY;
        RefreshCover();

        Loaded += (_, _) => NameText.Focus();
    }

    private void NameText_TextChanged(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (PreviewName != null)
            PreviewName.Text = string.IsNullOrWhiteSpace(NameText.Text)
                ? "Unnamed instance" : NameText.Text.Trim();
    }

    private void RefreshCover()
    {
        string? path = ResetImage ? null : _selectedNewImage ?? _savedSourcePath;
        ImageSource? image = _hasCrop
            ? InstanceArtwork.LoadCropped(path, _crop)
            : InstanceArtwork.Load(path);
        bool hasImage = image != null;

        CustomIcon.Source = image;
        CustomIcon.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        DefaultIcon.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        AdjustCropButton.IsEnabled = hasImage;
        ImageStatusText.Text = !hasImage
            ? "Using original Orbus artwork"
            : _hasCrop ? "Custom crop — can be adjusted anytime" :
                "Legacy cover — choose Adjust to set the crop";

        // A new explicit crop is already cut to the right ratio and should
        // never receive the old transform a second time.
        CustomIcon.Stretch = _hasCrop ? Stretch.UniformToFill
            : _legacyFit ? Stretch.Uniform : Stretch.UniformToFill;
        PreviewScale.ScaleX = _hasCrop ? 1 : _legacyZoom;
        PreviewScale.ScaleY = _hasCrop ? 1 : _legacyZoom;
        PreviewMove.X = _hasCrop ? 0 : _legacyOffsetX * 2;
        PreviewMove.Y = _hasCrop ? 0 : _legacyOffsetY * 2;
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose a cover image",
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (picker.ShowDialog(this) == true)
            OpenCropper(picker.FileName, newFile: true);
    }

    private void AdjustCrop_Click(object sender, RoutedEventArgs e)
    {
        string source = _selectedNewImage ?? _savedSourcePath;
        if (string.IsNullOrWhiteSpace(source)) return;
        OpenCropper(source, newFile: false);
    }

    private void OpenCropper(string path, bool newFile)
    {
        try
        {
            if (!File.Exists(path) ||
                Path.GetExtension(path).ToLowerInvariant() is not
                    (".png" or ".jpg" or ".jpeg" or ".bmp"))
                throw new InvalidOperationException("Choose a PNG, JPG or BMP image.");

            if (new FileInfo(path).Length > 12L * 1024 * 1024)
                throw new InvalidOperationException("Choose an image smaller than 12 MB.");

            // Don't alter the saved image or the pending editor state
            // until the crop window returns a confirmed selection.
            Rect? previous = !newFile && _hasCrop ? _crop : null;
            var window = new CropInstanceCoverWindow(path, previous)
            {
                Owner = this
            };
            if (window.ShowDialog() != true)
                return;

            if (newFile)
                _selectedNewImage = path;

            _crop = window.SelectedCrop;
            _hasCrop = true;
            ResetImage = false;
            RefreshCover();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not crop image",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cover_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryDroppedFile(e.Data) != null
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Cover_Drop(object sender, DragEventArgs e)
    {
        if (TryDroppedFile(e.Data) is string file)
            OpenCropper(file, newFile: true);
        e.Handled = true;
    }

    private static string? TryDroppedFile(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] files ||
            files.Length != 1)
            return null;
        return Path.GetExtension(files[0]).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp" ? files[0] : null;
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        _selectedNewImage = null;
        _hasCrop = false;
        _crop = new Rect(0, 0, 1, 1);
        _legacyFit = false;
        _legacyZoom = 1;
        _legacyOffsetX = _legacyOffsetY = 0;
        ResetImage = true;
        RefreshCover();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameText.Text.Trim();
        if (name.Length == 0 || name.Length > 50 || name.Any(char.IsControl))
        {
            MessageBox.Show(this,
                "Use a name between 1 and 50 characters, without control characters.",
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
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
