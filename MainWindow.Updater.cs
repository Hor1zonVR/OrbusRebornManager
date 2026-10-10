using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private UpdateManager? _managerUpdater;
    private UpdateInfo? _availableManagerUpdate;
    private bool _managerUpdateBusy;

    private void InitializeManagerUpdates()
    {
        CheckManagerUpdatesCheck.IsChecked = _settings.CheckManagerUpdates;
        UninstallManagerButton.IsEnabled = false;
        ManagerVersionText.Text = "Installed version: v" +
            (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown");

        try
        {
            _managerUpdater = new UpdateManager(
                new SimpleWebSource(
                    "https://hor1zonvr.github.io/OrbusRebornManager/updates"));

            UninstallManagerButton.IsEnabled = _managerUpdater.IsInstalled;
            if (!_managerUpdater.IsInstalled)
            {
                ManagerUpdateStatusText.Text =
                    "Automatic updates require the installed RebornManager-Setup.exe edition. " +
                    "Standalone builds cannot update themselves.";
                CheckManagerUpdatesButton.IsEnabled = false;
            }
            else
            {
                ManagerUpdateStatusText.Text = "Updates are provided through the official RebornManager update feed.";
            }
        }
        catch (Exception ex)
        {
            ManagerUpdateStatusText.Text = "Updater unavailable: " + ex.Message;
            CheckManagerUpdatesButton.IsEnabled = false;
            UninstallManagerButton.IsEnabled = false;
        }
    }

    private async Task CheckManagerUpdatesAsync(bool manual = false)
    {
        if (_managerUpdateBusy || _managerUpdater is not { IsInstalled: true })
            return;

        _managerUpdateBusy = true;
        CheckManagerUpdatesButton.IsEnabled = false;
        ManagerUpdateStatusText.Text = "Checking for RebornManager updates...";

        try
        {
            var update = await _managerUpdater.CheckForUpdatesAsync();
            _availableManagerUpdate = update;

            if (update == null)
            {
                UpdateActionButton.Visibility = Visibility.Collapsed;
                ManagerUpdateStatusText.Text = "You're up to date.";
                if (manual) Log("RebornManager is up to date.");
            }
            else
            {
                string version = update.TargetFullRelease.Version.ToString();
                UpdateActionButton.Content = "Update & restart";
                UpdateActionButton.Visibility = Visibility.Visible;
                ManagerUpdateStatusText.Text = "Version " + version + " is available.";
                Log("RebornManager v" + version + " is available.");
            }
        }
        catch (Exception ex)
        {
            ManagerUpdateStatusText.Text = "Could not check for updates: " + ex.Message;
            if (manual) Log("RebornManager update check failed.");
        }
        finally
        {
            _managerUpdateBusy = false;
            CheckManagerUpdatesButton.IsEnabled = _managerUpdater.IsInstalled;
        }
    }

    private async void CheckManagerUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckManagerUpdatesAsync(manual: true);

    private async void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_managerUpdater == null || _availableManagerUpdate == null || _managerUpdateBusy)
            return;

        if (_jobs > 0)
        {
            MessageBox.Show(this,
                "Finish the current mod or instance operation before restarting the manager.",
                "Update pending", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _managerUpdateBusy = true;
        UpdateActionButton.IsEnabled = false;
        CheckManagerUpdatesButton.IsEnabled = false;
        ManagerUpdateProgress.Visibility = Visibility.Visible;
        ManagerUpdateProgress.Value = 0;

        try
        {
            ManagerUpdateStatusText.Text = "Downloading the update...";
            await _managerUpdater.DownloadUpdatesAsync(
                _availableManagerUpdate,
                progress => Dispatcher.BeginInvoke(new Action(() =>
                {
                    ManagerUpdateProgress.Value = progress;
                    ManagerUpdateStatusText.Text = "Downloading update: " + progress + "%";
                })));

            ManagerUpdateStatusText.Text = "Restarting to complete the update...";
            _service.SaveSettings(_settings);
            _managerUpdater.ApplyUpdatesAndRestart(_availableManagerUpdate.TargetFullRelease);
        }
        catch (Exception ex)
        {
            ManagerUpdateStatusText.Text = "Update failed: " + ex.Message;
            Log("RebornManager update failed.");
        }
        finally
        {
            _managerUpdateBusy = false;
            UpdateActionButton.IsEnabled = true;
            CheckManagerUpdatesButton.IsEnabled = _managerUpdater.IsInstalled;
            ManagerUpdateProgress.Visibility = Visibility.Collapsed;
        }
    }
    private void UninstallManager_Click(object sender, RoutedEventArgs e)
    {
        if (_managerUpdateBusy || _jobs > 0)
        {
            MessageBox.Show(this,
                "Finish the current operation before uninstalling RebornManager.",
                "RebornManager is busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_managerUpdater is not { IsInstalled: true })
        {
            MessageBox.Show(this,
                "This copy is not managed by the RebornManager installer. " +
                "Use Windows Installed apps to remove the installed version.",
                "Uninstall unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(this,
            "Uninstall RebornManager from this computer?\n\n" +
            "Your OrbusVR game installations, modded instances, " +
            "and RebornManager settings will be kept so you can reinstall safely.\n\n" +
            "The manager will close to complete the uninstall.",
            "Uninstall RebornManager", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        try
        {
            // Velopack places Update.exe next to the installed app's "current" directory.
            // Invoke its uninstall command; never delete install folders manually.
            string installerRoot = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, ".."));
            string updaterPath = Path.Combine(installerRoot, "Update.exe");
            if (!File.Exists(updaterPath))
                throw new FileNotFoundException(
                    "The installed Velopack uninstaller could not be found.", updaterPath);

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = "uninstall",
                WorkingDirectory = installerRoot,
                UseShellExecute = true
            });
            if (process == null)
                throw new InvalidOperationException("Could not launch the uninstaller.");

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Uninstall failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

}
