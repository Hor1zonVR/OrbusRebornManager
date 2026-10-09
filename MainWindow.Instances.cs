
using System.Windows;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private async void OpenInstances_Click(
        object sender, RoutedEventArgs e) =>
        await OpenInstancesAsync(focusCreation: false);

    private async void CreateInstance_Click(
        object sender, RoutedEventArgs e) =>
        await OpenInstancesAsync(focusCreation: true);

    private async Task OpenInstancesAsync(bool focusCreation)
    {
        var window = new InstancesWindow(
            _service,
            _settings.GamePath,
            focusCreation)
        {
            Owner = this
        };

        if (window.ShowDialog() != true ||
            string.IsNullOrWhiteSpace(window.SelectedGamePath))
            return;

        _settings.GamePath = window.SelectedGamePath;
        _service.SaveSettings(_settings);

        UpdateDashboard();
        UpdateInstalledRows();
        UpdateReleaseStatuses();

        Log("Selected " + _settings.GamePath);

        if (window.CreatedNewInstance &&
            MessageBox.Show(
                this,
                "Your clean OrbusVR copy is ready!\n\n" +
                "Install BepInEx into this new instance now?",
                "New instance created",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            await RunAsync(async () =>
            {
                await _service.InstallLoaderAsync(
                    _settings.GamePath,
                    message => Dispatcher.Invoke(
                        () => Log(message)));

                UpdateDashboard();

                MessageBox.Show(
                    this,
                    "BepInEx is installed in the new instance. " +
                    "Launch OrbusVR once before adding mods, " +
                    "then close the game and import your camera DLL.",
                    "Instance ready",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
        }
    }

    private void SimpleSettings_Click(
        object sender, RoutedEventArgs e)
    {
        Settings_Click(sender, e);

        PageHeading.Text = "Settings";
        PageSubtitle.Text = "Keep installed mods up to date.";
    }
}
