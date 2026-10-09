
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace OrbusRebornManager;

public partial class InstancesWindow : Window
{
    private readonly ManagerService _manager;
    private readonly InstanceStore _store = new();

    private readonly ObservableCollection<GameInstance>
        _instances = new();

    private bool _busy;

    public string? SelectedGamePath { get; private set; }
    public bool CreatedNewInstance { get; private set; }

    public InstancesWindow(
        ManagerService manager,
        string currentGamePath,
        bool focusCreation = false)
    {
        InitializeComponent();

        _manager = manager;

        foreach (var instance in _store.Load())
            _instances.Add(instance);

        InstancesList.ItemsSource = _instances;

        if (!string.IsNullOrWhiteSpace(currentGamePath))
        {
            try
            {
                var valid =
                    manager.NormalizeGamePath(currentGamePath);

                var existing = _store.AddExisting(
                    _instances.ToList(), valid);

                if (!_instances.Any(
                    x => SamePath(x.Path, valid)))
                {
                    _instances.Add(existing);
                }

                InstancesList.SelectedItem =
                    _instances.FirstOrDefault(
                        x => SamePath(x.Path, valid));
            }
            catch
            {
                // A removed installation can be replaced
                // using Add existing game.
            }
        }

        if (InstancesList.SelectedItem == null &&
            _instances.Count > 0)
        {
            InstancesList.SelectedIndex = 0;
        }

        if (focusCreation)
            Loaded += (_, _) => NewNameText.Focus();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(
                Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(
                Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private void AddExisting_Click(
        object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Choose your existing OrbusVR Reborn folder"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            string game =
                _manager.NormalizeGamePath(dialog.FolderName);

            var list = _instances.ToList();

            var item = _store.AddExisting(
                list, game, Path.GetFileName(game));

            if (!_instances.Any(
                x => SamePath(x.Path, game)))
            {
                _instances.Add(item);
            }

            InstancesList.SelectedItem =
                _instances.First(
                    x => SamePath(x.Path, game));

            CopyStatus.Text =
                "Existing installation added. " +
                "Select 'Use selected instance' to switch to it.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this, ex.Message, "Invalid installation",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Forget_Click(
        object sender, RoutedEventArgs e)
    {
        if (_busy ||
            InstancesList.SelectedItem is not GameInstance item)
            return;

        if (MessageBox.Show(
                this,
                "Remove '" + item.Name +
                "' from the manager's list?\n\n" +
                "This does NOT delete the game files.",
                "Remove from list",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) !=
            MessageBoxResult.Yes)
        {
            return;
        }

        _instances.Remove(item);
        _store.Save(_instances);

        CopyStatus.Text =
            "Removed from the list. " +
            "The game folder has not been deleted.";
    }

    private void UseSelected_Click(
        object sender, RoutedEventArgs e)
    {
        if (_busy ||
            InstancesList.SelectedItem is not GameInstance item)
            return;

        try
        {
            SelectedGamePath =
                _manager.NormalizeGamePath(item.Path);

            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this, ex.Message, "Game missing",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void BrowseParent_Click(
        object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder that will contain your new instance"
        };

        if (dialog.ShowDialog(this) == true)
            DestinationParentText.Text = dialog.FolderName;
    }

    private async void CreateCopy_Click(
        object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (InstancesList.SelectedItem is not GameInstance source)
        {
            MessageBox.Show(
                this,
                "Select an existing installation to copy first.");
            return;
        }

        if (_manager.IsGameRunning())
        {
            MessageBox.Show(
                this,
                "Close OrbusVR before copying its game files.");
            return;
        }

        if (string.IsNullOrWhiteSpace(
                DestinationParentText.Text))
        {
            MessageBox.Show(
                this, "Choose a destination folder first.");
            return;
        }

        try
        {
            string sourcePath =
                _manager.NormalizeGamePath(source.Path);

            _busy = true;
            CopyButton.IsEnabled = false;
            CopyStatus.Text = "Preparing copy...";
            CopyProgress.Value = 0;

            var report =
                new Progress<(int Done, int Total)>(p =>
                {
                    CopyProgress.Value =
                        p.Total == 0
                            ? 100
                            : p.Done * 100.0 / p.Total;

                    CopyStatus.Text =
                        $"Copying game files: {p.Done:N0} / {p.Total:N0}";
                });

            var copy = await _store.CreateCleanCopyAsync(
                sourcePath,
                DestinationParentText.Text,
                NewNameText.Text,
                report);

            _manager.NormalizeGamePath(copy.Path);

            _instances.Add(copy);
            _store.Save(_instances);

            SelectedGamePath = copy.Path;
            CreatedNewInstance = true;

            _busy = false;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            CopyStatus.Text =
                "Copy failed: " + ex.Message;

            MessageBox.Show(
                this, ex.Message,
                "Could not create instance",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            CopyButton.IsEnabled = true;
        }
    }

    private void Close_Click(
        object sender, RoutedEventArgs e)
    {
        if (!_busy)
            Close();
    }

    protected override void OnClosing(
        System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
            e.Cancel = true;

        base.OnClosing(e);
    }
}
