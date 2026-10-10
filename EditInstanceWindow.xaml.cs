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
    private string? _currentImage;

    public string DisplayName { get; private set; } = "";
    public string? NewImagePath => _selectedNewImage;
    public bool ResetImage { get; private set; }

    public EditInstanceWindow(GameInstance instance)
    {
        InitializeComponent();

        DisplayName = instance.Name;
        NameText.Text = instance.Name;
        _currentImage = instance.CustomIconPath;
        ShowPreview(InstanceArtwork.Load(_currentImage),
            string.IsNullOrWhiteSpace(_currentImage) ? "Using default Orbus artwork" :
                Path.GetFileName(_currentImage));

        Loaded += (_, _) =>
        {
            NameText.Focus();
            NameText.SelectAll();
        };
    }

    private void ShowPreview(ImageSource? source, string label)
    {
        CustomIcon.Source = source;
        CustomIcon.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
        DefaultIcon.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
        ImageStatusText.Text = source == null ? "Using default Orbus artwork" : label;
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a custom instance icon",
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
                throw new InvalidOperationException("The selected image could not be opened.");

            _selectedNewImage = dialog.FileName;
            ResetImage = false;
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
        ShowPreview(null, "");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = NameText.Text.Trim();

        if (name.Length == 0 || name.Length > 50 ||
            name.Any(char.IsControl))
        {
            MessageBox.Show(this,
                "Use a display name between 1 and 50 characters without control characters.",
                "Invalid instance name", MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
