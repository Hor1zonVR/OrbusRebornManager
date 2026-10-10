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
                _settings.DefaultInstanceDirectory = window.SelectedStoragePath;

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

    private void SimpleSettings_Click(object sender, RoutedEventArgs e)
    {
        Settings_Click(sender, e);
    }
}
