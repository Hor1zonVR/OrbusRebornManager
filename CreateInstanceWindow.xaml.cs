using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace OrbusRebornManager;

public partial class CreateInstanceWindow : Window
{
    private readonly ManagerService _manager;
    private readonly InstanceStore _store = new();
    private CancellationTokenSource? _copyCancellation;
    private bool _busy;

    public GameInstance? CreatedInstance { get; private set; }
    public bool InstallLoader { get; private set; }
    public bool RememberLocation { get; private set; }
    public string SelectedStoragePath => StoragePathText.Text.Trim();

    public CreateInstanceWindow(
        ManagerService manager,
        string sourcePath,
        string storagePath)
    {
        InitializeComponent();
        _manager = manager;
        SourcePathText.Text = sourcePath;
        StoragePathText.Text = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornModdingInstances")
            : storagePath;

        // Suggest an unused name instead of beginning with a collision.
        var names = _store.Load()
            .Select(i => i.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string name = "Modded Orbus";
        int index = 2;
        while (names.Contains(name) ||
               Directory.Exists(Path.Combine(StoragePathText.Text, name)))
            name = "Modded Orbus " + index++;
        NameText.Text = name;
        Loaded += (_, _) =>
        {
            NameText.Focus();
            NameText.SelectAll();
        };
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the OrbusVR Reborn installation to copy"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            SourcePathText.Text = _manager.NormalizeGamePath(dialog.FolderName);
            CreationStatusText.Text = "";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Not a supported OrbusVR installation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseStorage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where new OrbusVR instances will be saved"
        };
        if (dialog.ShowDialog(this) == true)
        {
            StoragePathText.Text = dialog.FolderName;
            CreationStatusText.Text = "";
        }
    }

    private static bool IsWithin(string folder, string potentialChild)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(potentialChild));
        return string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(normalized + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            string source = _manager.NormalizeGamePath(SourcePathText.Text);
            string parent = Path.GetFullPath(StoragePathText.Text);
            string name = NameText.Text.Trim();

            if (name.Length == 0)
                throw new InvalidOperationException("Choose a name for your instance.");

            // Never create an instance directory inside the source game folder.
            // Validate before creating the parent to leave the source unchanged.
            if (IsWithin(source, parent) ||
                IsWithin(source, Path.Combine(parent, name)))
            {
                throw new InvalidOperationException(
                    "Instances cannot be stored inside the original OrbusVR game folder.");
            }

            if (_manager.IsGameRunning())
                throw new InvalidOperationException(
                    "Close OrbusVR before creating a new instance.");

            _busy = true;
            CreateButton.IsEnabled = false;
            CopyProgress.Visibility = Visibility.Visible;
            CreationStatusText.Text = "Preparing your game copy...";
            _copyCancellation = new CancellationTokenSource();
            Directory.CreateDirectory(parent);

            var progress = new Progress<(int Done, int Total)>(p =>
            {
                CopyProgress.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                CreationStatusText.Text =
                    $"Copying game files: {p.Done:N0} of {p.Total:N0}";
            });

            GameInstance copy = await _store.CreateCleanCopyAsync(
                source, parent, name, progress, _copyCancellation.Token);

            var items = _store.Load();
            items.Add(copy);
            _store.Save(items);

            CreatedInstance = copy;
            InstallLoader = LoaderCheck.IsChecked == true;
            RememberLocation = RememberLocationCheck.IsChecked == true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            CreationStatusText.Text = "Instance creation cancelled. No game files were installed.";
        }
        catch (Exception ex)
        {
            CreationStatusText.Text = "Couldn't create instance: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Could not create instance",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            _copyCancellation?.Dispose();
            _copyCancellation = null;
            CreateButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            CopyProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            CreationStatusText.Text = "Cancelling copy...";
            _copyCancellation?.Cancel();
            CancelButton.IsEnabled = false;
            return;
        }
        Close();
    }

    private void Window_Closing(object? sender,
        System.ComponentModel.CancelEventArgs e)
    {
        // Setting DialogResult after a successful copy closes the dialog.
        // Never treat that close as a user-requested cancellation.
        if (!_busy || CreatedInstance != null) return;
        e.Cancel = true;
        _copyCancellation?.Cancel();
        CreationStatusText.Text = "Cancelling copy...";
    }
}
