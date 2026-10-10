using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace OrbusRebornManager;

public partial class FirstRunWindow : Window
{
    private readonly ManagerService _manager;
    private readonly bool _preview;

    public string SelectedGamePath { get; private set; } = "";
    public string SelectedStoragePath { get; private set; }
    public bool CreateFirstInstance { get; private set; }

    public FirstRunWindow(ManagerService manager, bool preview = false)
    {
        InitializeComponent();
        _manager = manager;
        _preview = preview;
        SelectedStoragePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrbusRebornModdingInstances");

        if (_preview)
        {
            ModeCaption.Text = "FIRST-LAUNCH PREVIEW";
            PreviewNotice.Visibility = Visibility.Visible;
            LibraryButton.Content = "Close preview";
            LibraryButton.IsEnabled = true;
            CreateButton.Content = "Preview next step";
        }

        RefreshLocationLabels();
    }

    private void RefreshLocationLabels()
    {
        GameLocationText.Text = string.IsNullOrWhiteSpace(SelectedGamePath)
            ? "Choose the folder containing vrclient.exe"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(SelectedGamePath));
        GameLocationText.ToolTip = SelectedGamePath;

        string local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        StorageLocationText.Text = SelectedStoragePath.StartsWith(
            local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "%LOCALAPPDATA%" + SelectedStoragePath[local.Length..]
            : SelectedStoragePath;
        StorageLocationText.ToolTip = SelectedStoragePath;

        CreateButton.IsEnabled = !string.IsNullOrEmpty(SelectedGamePath);
        if (!_preview)
            LibraryButton.IsEnabled = CreateButton.IsEnabled;
    }

    private void ChooseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select your existing OrbusVR Reborn Community Edition folder"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            SelectedGamePath = _manager.NormalizeGamePath(dialog.FolderName);
            RefreshLocationLabels();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message,
                "OrbusVR folder not recognised",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ChooseStorage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where modded OrbusVR game copies will be saved"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SelectedStoragePath = Path.GetFullPath(dialog.FolderName);
            RefreshLocationLabels();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message,
                "Invalid instance location",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedGamePath))
            return;
        CreateFirstInstance = true;
        DialogResult = true;
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (_preview)
        {
            DialogResult = false;
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedGamePath))
            return;
        CreateFirstInstance = false;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }
}
