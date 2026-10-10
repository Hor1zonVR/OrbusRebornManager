using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private void OpenInstances_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose an existing OrbusVR Reborn installation"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            string game = _service.NormalizeGamePath(dialog.FolderName);
            var list = _instanceStore.Load();
            var instance = _instanceStore.AddExisting(
                list, game, Path.GetFileName(game));

            _settings.GamePath = instance.Path;
            _service.SaveSettings(_settings);
            UpdateDashboard();
            UpdateReleaseStatuses();
            UpdateInstalledRows();
            RefreshInstanceCards();
            OpenInstance(new InstanceCard(instance, "", "", true));
            Log("Added " + instance.Name + " to your library.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not add installation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CreateInstance_Click(object sender, RoutedEventArgs e)
    {
        var existing = _instanceStore.Load();

        // Prefer the original installation over an already-modded copy.
        string source = existing.FirstOrDefault(i => !i.CreatedByManager)?.Path
            ?? _settings.GamePath;

        await CreateInstanceFromSourceAsync(source);
    }

    private async Task CreateInstanceFromSourceAsync(string source)
    {
        var window = new CreateInstanceWindow(
            _service, source, _settings.DefaultInstanceDirectory)
        {
            Owner = this
        };

        if (window.ShowDialog() != true || window.CreatedInstance == null)
            return;

        var created = window.CreatedInstance;
        try
        {
            if (window.RememberLocation)
            {
                _settings.DefaultInstanceDirectory = window.SelectedStoragePath;
                DefaultInstanceFolderText.Text = _settings.DefaultInstanceDirectory;
            }

            _settings.GamePath = created.Path;
            _service.SaveSettings(_settings);

            RefreshInstanceCards();
            UpdateDashboard();
            UpdateReleaseStatuses();
            UpdateInstalledRows();

            if (window.InstallLoader)
            {
                await RunAsync(async () =>
                {
                    Log("Setting up the mod loader...");
                    await _service.InstallLoaderAsync(
                        created.Path,
                        message => Dispatcher.Invoke(() => Log(message)));
                });

                UpdateDashboard();
            }

            OpenInstance(new InstanceCard(created, "", "", true));
            RefreshInstanceCards();

            if (window.InstallLoader && _service.IsBepInExInstalled(created.Path))
            {
                Log("Instance ready. Launch OrbusVR once to finish preparing BepInEx.");
            }
            else
            {
                Log("Instance created. You can prepare it for mods when you're ready.");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The game copy was created, but setup was incomplete: " + ex.Message +
                "\n\nYour instance files were not deleted.",
                "Instance setup incomplete",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshInstanceCards();
        }
    }

    private async Task ShowFirstRunAsync(bool preview)
    {
        var welcome = new FirstRunWindow(_service, preview)
        {
            Owner = this
        };

        if (welcome.ShowDialog() != true)
            return;

        if (preview)
        {
            // A real preview of both first-launch screens. No persisted
            // settings, registry edits, downloads or game-file copies.
            var dialog = new CreateInstanceWindow(
                _service, welcome.SelectedGamePath, welcome.SelectedStoragePath,
                previewOnly: true)
            {
                Owner = this
            };
            dialog.ShowDialog();
            return;
        }

        try
        {
            // Register the original installation, but never modify or copy it
            // until the player explicitly confirms creation in the next window.
            var registered = _instanceStore.AddExisting(
                _instanceStore.Load(), welcome.SelectedGamePath);
            _settings.GamePath = registered.Path;
            _settings.DefaultInstanceDirectory = welcome.SelectedStoragePath;
            _service.SaveSettings(_settings);
            DefaultInstanceFolderText.Text = _settings.DefaultInstanceDirectory;

            RefreshInstanceCards();
            UpdateDashboard();
            UpdateReleaseStatuses();
            UpdateInstalledRows();
            ShowPanel("instances");

            if (welcome.CreateFirstInstance)
                await CreateInstanceFromSourceAsync(registered.Path);
            else
                Log("Your original game is added. Create a modded copy whenever you're ready.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Couldn't finish first-time setup: " + ex.Message,
                "Setup incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PreviewFirstRun_Click(object sender, RoutedEventArgs e)
    {
        await ShowFirstRunAsync(preview: true);
    }

    private void ChangeInstanceStorage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the default folder for new OrbusVR instances"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _settings.DefaultInstanceDirectory = Path.GetFullPath(dialog.FolderName);
            _service.SaveSettings(_settings);
            DefaultInstanceFolderText.Text = _settings.DefaultInstanceDirectory;
            Log("Default instance location changed. Existing copies were not moved.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save location",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenInstanceStorage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Path.GetFullPath(_settings.DefaultInstanceDirectory);
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open instance folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SimpleSettings_Click(object sender, RoutedEventArgs e)
    {
        Settings_Click(sender, e);
    }
}
