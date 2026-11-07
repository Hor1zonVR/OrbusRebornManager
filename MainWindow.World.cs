using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private readonly ServerWorldSettingsService _worldService = new();
    private ServerWorldSnapshot? _currentWorldSnapshot;
    private string? _worldServerPath;
    private bool _worldSaving;

    private void LoadWorldSettings()
    {
        string? folder = ServerFolder(false);
        if (folder == null) return;
        try
        {
            var world = _worldService.Load(folder);
            _worldServerPath = folder;
            _currentWorldSnapshot = world;
            WorldServerNameText.Text = world.ServerName;
            WorldMotdText.Text = world.Motd;
            WorldAllowAccountsCheck.IsChecked = world.AllowNewAccounts;
            WorldCharacterLimitText.Text = world.MaxCharacters.ToString(CultureInfo.InvariantCulture);
            WorldRareChanceText.Text = world.VeryRareDropChance.ToString("0.##", CultureInfo.InvariantCulture);
            WorldSaveButton.IsEnabled = true;
            WorldInfoText.Text = "Settings loaded from your server. Changes take effect after restarting game and API.";
        }
        catch (Exception ex)
        {
            _currentWorldSnapshot = null;
            WorldSaveButton.IsEnabled = false;
            WorldInfoText.Text = "Cannot read World settings: " + ex.Message;
        }
    }

    private void WorldRefresh_Click(object sender, RoutedEventArgs e) =>
        LoadWorldSettings();

    private void WorldSave_Click(object sender, RoutedEventArgs e)
    {
        if (_worldSaving || _currentWorldSnapshot == null) return;
        string? folder = ServerFolder();
        if (folder == null ||
            !folder.Equals(_worldServerPath, StringComparison.OrdinalIgnoreCase)) return;
        if (!int.TryParse(WorldCharacterLimitText.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int limit) ||
            !double.TryParse(WorldRareChanceText.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double chance))
        {
            WorldInfoText.Text = "Enter valid numbers for character limit and rare cosmetic chance.";
            return;
        }
        var edit = new ServerWorldEdit(
            WorldServerNameText.Text,
            WorldMotdText.Text,
            WorldAllowAccountsCheck.IsChecked == true,
            limit, chance);
        if (MessageBox.Show(this,
                "Save World configuration changes?\n\n" +
                "The original config is backed up locally. Existing players are not modified.\n" +
                "Changes will take effect after you restart game and API.",
                "Save world settings", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _worldSaving = true;
        WorldSaveButton.IsEnabled = false;
        try
        {
            string backup = _worldService.Save(folder, edit, _currentWorldSnapshot.FileSha256);
            LoadWorldSettings();
            WorldInfoText.Text = "Saved! Restart game and API to apply. Backup: " + backup;
            Log("Saved World configuration and created backup.");
        }
        catch (Exception ex)
        {
            WorldInfoText.Text = "Save failed: " + ex.Message;
            Log("World config save failed: " + ex.Message);
        }
        finally
        {
            _worldSaving = false;
            if (_currentWorldSnapshot != null) WorldSaveButton.IsEnabled = true;
        }
    }

    private async void WorldRestart_Click(object sender, RoutedEventArgs e) =>
        await RunServerCommandAsync(_serverHost.RestartAsync,
            "Restart game and API to apply World settings", confirm: true);

    private void WorldTryXpBomb_Click(object sender, RoutedEventArgs e)
    {
        // The native bomb works server-wide, but its internal duration and bonus
        // have not been verified as modifiable in the Community Server image.
        // Route to the existing safe gifting UI instead of faking a multiplier.
        ShowServerTab("players");
        PlayerItemSearchText.Text = "1351";
        WorldInfoText.Text = "The XP Boost Bomb is item #1351. Give it to your own character and activate it in-game to test its normal behaviour.";
    }
}
