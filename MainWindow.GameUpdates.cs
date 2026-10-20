using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private readonly Dictionary<string, GameReleaseDownloadService.GameReleaseInfo> _latestGameReleases =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _checkingGameUpdates;
    private bool _gameUpdateBusy;
    private CancellationTokenSource? _gameUpdateCancellation;

    private GameInstance? CurrentGameInstance()
    {
        string? selectedPath = GameOrWarn(false);
        if (selectedPath == null) return null;
        return _instanceStore.Load().FirstOrDefault(x =>
            string.Equals(Path.GetFullPath(x.Path), selectedPath, StringComparison.OrdinalIgnoreCase));
    }

    private bool CanUpdateGame(GameInstance? instance) =>
        instance?.CreatedByManager == true &&
        !string.IsNullOrWhiteSpace(instance.GameVersion) &&
        instance.Edition is GameReleaseDownloadService.Community or GameReleaseDownloadService.Renewed;

    private void RefreshGameUpdateControls()
    {
        if (GameUpdateControls == null) return;
        var instance = CurrentGameInstance();
        bool tracked = CanUpdateGame(instance);
        GameUpdateControls.Visibility = tracked ? Visibility.Visible : Visibility.Collapsed;
        if (!tracked || instance == null) return;

        string edition = instance.Edition == GameReleaseDownloadService.Renewed
            ? "Orbus Renewed" : "Community Edition";
        bool hasLatest = _latestGameReleases.TryGetValue(instance.Edition, out var latest);
        bool update = hasLatest && !string.Equals(instance.GameVersion, latest!.Version,
            StringComparison.OrdinalIgnoreCase);
        bool backup = !string.IsNullOrWhiteSpace(instance.BackupPath) &&
                      Directory.Exists(instance.BackupPath);

        if (!_gameUpdateBusy)
        {
            GameUpdateStatusText.Text = _checkingGameUpdates ? "Checking official GitHub releases..." :
                update ? edition + " update available: " + instance.GameVersion + " → " + latest!.Version :
                hasLatest ? edition + " " + instance.GameVersion + " · Up to date" :
                edition + " " + instance.GameVersion + " · Check for updates";
        }
        GameUpdateButton.Visibility = update && !_gameUpdateBusy
            ? Visibility.Visible : Visibility.Collapsed;
        GameRollbackButton.Visibility = backup && !_gameUpdateBusy
            ? Visibility.Visible : Visibility.Collapsed;
        GameUpdateCheckButton.IsEnabled = !_gameUpdateBusy && !_checkingGameUpdates;
        GameUpdateCancelButton.Visibility = _gameUpdateBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CheckGameUpdatesAsync()
    {
        if (_checkingGameUpdates || _gameUpdateBusy) return;
        _checkingGameUpdates = true;
        RefreshGameUpdateControls();
        try
        {
            // One request per upstream repository. A failed repo doesn't
            // suppress updates available from another repo.
            foreach (string edition in new[] {
                GameReleaseDownloadService.Renewed, GameReleaseDownloadService.Community })
            {
                try
                {
                    _latestGameReleases[edition] =
                        await GameReleaseDownloadService.GetLatestAsync(edition);
                }
                catch (Exception ex)
                {
                    Log("Could not check " + edition + " game releases: " + ex.Message);
                }
            }
        }
        finally
        {
            _checkingGameUpdates = false;
            RefreshInstanceCards();
            RefreshGameUpdateControls();
        }
    }

    private async void CheckGameUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckGameUpdatesAsync();

    private async void UpdateGame_Click(object sender, RoutedEventArgs e)
    {
        if (_gameUpdateBusy || _checkingGameUpdates || _jobs > 0) return;
        var instance = CurrentGameInstance();
        if (!CanUpdateGame(instance) || instance == null ||
            !_latestGameReleases.TryGetValue(instance.Edition, out var latest) ||
            string.Equals(instance.GameVersion, latest.Version, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "There is no confirmed game update for this instance.",
                "No update available", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string confirmation = "Update '" + instance.Name + "' from " + instance.GameVersion +
            " to " + latest.Version + "?\n\n" +
            "RM will download and verify the original game ZIP (or reuse its cached copy), " +
            "stage the new version, preserve BepInEx plugins and configs, and keep a " +
            "full copy of the previous game for rollback.\n\n" +
            "Generated IL2CPP interop is rebuilt on next launch. Some mods may " +
            "need a new build after game updates. Your current game and settings " +
            "will not be replaced until staging is complete.\n\n" +
            "Close OrbusVR before proceeding.";
        if (MessageBox.Show(this, confirmation, "Update game instance",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _gameUpdateBusy = true;
        _jobs++;
        _gameUpdateCancellation = new CancellationTokenSource();
        GameUpdateCancelButton.IsEnabled = true;
        GameUpdateProgress.Value = 0;
        GameUpdateProgress.Visibility = Visibility.Visible;
        GameUpdateStatusText.Text = "Preparing update...";
        RefreshGameUpdateControls();
        try
        {
            var progress = new Progress<GameReleaseDownloadService.Status>(p =>
            {
                GameUpdateProgress.Value = p.Percent;
                GameUpdateStatusText.Text = p.Message;
            });
            await GameUpdateService.UpdateAsync(instance, latest, progress,
                _gameUpdateCancellation.Token);
            Log("Updated " + instance.Name + " to " + latest.Version + ".");
            UpdateDashboard();
            UpdateInstalledRows();
            UpdateReleaseStatuses();
            RefreshInstanceCards();
            MessageBox.Show(this,
                "Game updated to " + latest.Version + ". Your BepInEx mods and settings were preserved.\n\n" +
                "Launch the game once to regenerate IL2CPP interop if needed. " +
                "You can use Rollback to restore the previous game build.",
                "Game updated", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            GameUpdateStatusText.Text = "Update cancelled. Existing game was not changed.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Game update failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _gameUpdateCancellation.Dispose();
            _gameUpdateCancellation = null;
            _gameUpdateBusy = false;
            _jobs--;
            GameUpdateCancelButton.IsEnabled = true;
            GameUpdateProgress.Visibility = Visibility.Collapsed;
            RefreshInstanceCards();
            RefreshGameUpdateControls();
        }
    }

    private void CancelGameUpdate_Click(object sender, RoutedEventArgs e)
    {
        GameUpdateStatusText.Text = "Cancelling before installation...";
        _gameUpdateCancellation?.Cancel();
        GameUpdateCancelButton.IsEnabled = false;
    }

    private async void RollbackGame_Click(object sender, RoutedEventArgs e)
    {
        if (_gameUpdateBusy || _jobs > 0) return;
        var instance = CurrentGameInstance();
        if (instance == null || string.IsNullOrWhiteSpace(instance.BackupPath) ||
            !Directory.Exists(instance.BackupPath)) return;

        if (MessageBox.Show(this,
            "Restore '" + instance.Name + "' from " + instance.GameVersion +
            " to " + instance.BackupGameVersion + "?\n\n" +
            "RM will swap the saved complete game folders. Your current updated version " +
            "is also kept so you can undo this rollback.\n\nClose OrbusVR first.",
            "Rollback game version", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _gameUpdateBusy = true;
        _jobs++;
        RefreshGameUpdateControls();
        try
        {
            await GameUpdateService.RollbackAsync(instance);
            Log("Rolled back " + instance.Name + ".");
            RefreshInstanceCards();
            UpdateInstalledRows();
            UpdateDashboard();
            UpdateReleaseStatuses();
            MessageBox.Show(this,
                "Restored the previous game version and its modded game files.",
                "Rollback completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Rollback failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _gameUpdateBusy = false;
            _jobs--;
            RefreshGameUpdateControls();
        }
    }
}
