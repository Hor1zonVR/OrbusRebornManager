using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private readonly ServerPlayerService _serverPlayerService = new();
    public ObservableCollection<ServerPlayerEntry> VisibleServerPlayers { get; } = new();
    public ObservableCollection<ServerItemEntry> VisiblePlayerItems { get; } = new();
    private ServerPlayerDirectoryResult? _playerDirectory;
    private ServerPlayerEntry? _chosenPlayer;
    private bool _playerBusy;
    private int _playerRequest;
    private bool _playersOnlineOnly;

    private async Task LoadPlayersAsync()
    {
        string? folder = ServerFolder(false);
        if (folder == null || _playerBusy) return;
        int request = ++_playerRequest;
        _playerBusy = true;
        PlayerRefreshButton.IsEnabled = false;
        PlayerDirectoryInfoText.Text = "Loading character history and checking live presence…";
        try
        {
            var result = await _serverPlayerService.LoadAsync(folder);
            if (request != _playerRequest ||
                !string.Equals(folder, _selectedServerPath, StringComparison.OrdinalIgnoreCase))
                return;
            _playerDirectory = result;
            PlayerHistoryCountText.Text = result.Players.Count.ToString();
            PlayerOnlineCountText.Text = result.PresenceVerified ?
                result.Players.Count(p => p.Online == true).ToString() : "—";
            PlayerDirectoryInfoText.Text = result.HistorySource + "  ·  " + result.PresenceSource;
            PlayerDiagnosticText.Text = result.Diagnostic;
            PlayerDiagnosticText.Visibility = string.IsNullOrWhiteSpace(result.Diagnostic)
                ? Visibility.Collapsed : Visibility.Visible;
            string? previousName = _chosenPlayer?.Name;
            _chosenPlayer = previousName == null ? null :
                result.Players.FirstOrDefault(p => p.Name.Equals(previousName, StringComparison.OrdinalIgnoreCase));
            RefreshVisiblePlayers();
            if (_chosenPlayer != null) UpdatePlayerSelection(_chosenPlayer);
            else ClearPlayerSelection();
            try
            {
                _allPlayerItems = _serverPlayerService.ReadItems(folder);
                RefreshItemGrid();
                UpdateLocalIconStatus();
                if (_chosenPlayer != null)
                    await LoadCharacterSnapshotAsync();
            }
            catch (Exception e)
            {
                PlayerGiftStatusText.Text = "Item catalogue unavailable: " + e.Message;
            }
        }
        catch (Exception e)
        {
            if (request != _playerRequest) return;
            PlayerDirectoryInfoText.Text = "Could not load player directory: " + e.Message;
            PlayerDiagnosticText.Text = "No player data was changed.";
            PlayerDiagnosticText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (request == _playerRequest)
            {
                _playerBusy = false;
                PlayerRefreshButton.IsEnabled = true;
                UpdateGrantEnabled();
            }
        }
    }

    private System.Collections.Generic.List<ServerItemEntry> _allPlayerItems = new();

    private void ResetPlayersForServer()
    {
        ++_playerRequest;
        _playerBusy = false;
        _playerDirectory = null;
        _chosenPlayer = null;
        _allPlayerItems.Clear();
        VisiblePlayerItems.Clear();
        VisibleServerPlayers.Clear();
        ResetInventoryViewer();
        PlayerHistoryCountText.Text = "—";
        PlayerOnlineCountText.Text = "—";
        PlayerSearchText.Text = "";
        PlayerDirectoryInfoText.Text = "Open the Players tab to load this server's saved characters.";
        PlayerDiagnosticText.Text = "";
        PlayerDiagnosticText.Visibility = Visibility.Collapsed;
        PlayerRefreshButton.IsEnabled = true;
        _playersOnlineOnly = false;
        PlayerItemSearchText.Text = "";
        ClearPlayerSelection();
        UpdatePlayerFilters();
    }

    private void RefreshVisiblePlayers()
    {
        if (PlayerSearchText == null || EmptyServerPlayersText == null) return;
        VisibleServerPlayers.Clear();
        string query = PlayerSearchText.Text.Trim();
        var players = (_playerDirectory?.Players ?? new()).Where(p =>
            (!_playersOnlineOnly || p.Online == true) &&
            (query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.Online == true).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var player in players) VisibleServerPlayers.Add(player);
        EmptyServerPlayersText.Visibility = VisibleServerPlayers.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyServerPlayersText.Text = _playersOnlineOnly &&
            _playerDirectory?.PresenceVerified != true
                ? "This server has not exposed a verified online-name list yet. Nobody has been marked offline."
                : VisibleServerPlayers.Count == 0
                    ? "No matching characters found. Try refreshing the directory."
                    : "";
    }

    private void UpdatePlayerFilters()
    {
        PlayerAllButton.Background = _playersOnlineOnly
            ? System.Windows.Media.Brushes.Transparent
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 60, 62));
        PlayerOnlineButton.Background = _playersOnlineOnly
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 60, 62))
            : System.Windows.Media.Brushes.Transparent;
    }

    private void PlayerSearch_Changed(object sender, TextChangedEventArgs e) =>
        RefreshVisiblePlayers();

    private void PlayerAll_Click(object sender, RoutedEventArgs e)
    {
        _playersOnlineOnly = false;
        UpdatePlayerFilters();
        RefreshVisiblePlayers();
    }

    private void PlayerOnline_Click(object sender, RoutedEventArgs e)
    {
        _playersOnlineOnly = true;
        UpdatePlayerFilters();
        RefreshVisiblePlayers();
    }

    private async void PlayerRefresh_Click(object sender, RoutedEventArgs e) =>
        await LoadPlayersAsync();

    private void CopyPlayerDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText("Source: " + PlayerDirectoryInfoText.Text + "\n" +
            "Diagnostics: " + PlayerDiagnosticText.Text);
        Log("Player directory diagnostic details copied.");
    }

    private void SelectServerPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ServerPlayerEntry player })
            UpdatePlayerSelection(player);
    }

    private void UpdatePlayerSelection(ServerPlayerEntry player)
    {
        CloseClassLevelEditor();
        _chosenPlayer = player;
        PlayerSelectionEmpty.Visibility = Visibility.Collapsed;
        PlayerProfilePanel.Visibility = Visibility.Visible;
        PlayerProfileNameText.Text = player.Name;
        PlayerProfilePresenceText.Text = player.PresenceText;
        PlayerGiftStatusText.Text = "Choose an item below to prepare a base-item grant.";
        _ = LoadCharacterSnapshotAsync();
        UpdateGrantEnabled();
    }

    private void ClearPlayerSelection()
    {
        CloseClassLevelEditor();
        _chosenPlayer = null;
        PlayerSelectionEmpty.Visibility = Visibility.Visible;
        PlayerProfilePanel.Visibility = Visibility.Collapsed;
        ++_characterRequest;
        UpdateGrantEnabled();
    }

    private void PlayerItemSearch_Changed(object sender, TextChangedEventArgs e)
    {
        _itemPage = 0;
        RefreshItemGrid();
    }

    private void UpdateGrantEnabled()
    {
        if (PlayerGiftButton == null) return;
        PlayerGiftButton.IsEnabled = !_playerBusy && !_giftBusy && !_equipmentGrantBusy &&
            _chosenPlayer != null && _selectedCatalogItem != null;
        RefreshEquipmentControls();
    }

    private async void GivePlayerItem_Click(object sender, RoutedEventArgs e)
    {
        if (_giftBusy || _equipmentGrantBusy || _chosenPlayer == null || _selectedCatalogItem is not ServerItemEntry item)
            return;
        string? folder = ServerFolder();
        if (folder == null) return;
        string target = _chosenPlayer.Name;
        if (MessageBox.Show(this,
            $"Send 1 × {item.Name} (#{item.Id}) to {target}?\n\n" +
            "IMPORTANT: Your testing showed online grants may be overwritten by the live server's next save.\n" +
            "Prefer granting while the recipient is fully logged OFF.\n\n" +
            "This sends an admin request, not a guaranteed delivery. Confirm it in-game.",
            "Confirm item grant", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _giftBusy = true;
        UpdateGrantEnabled();
        PlayerGiftStatusText.Text = "Sending admin base-item request…";
        try
        {
            string response = await _serverPlayerService.GiveItemAsync(folder, target, item.Id);
            PlayerGiftStatusText.Text = "Admin command returned. Confirm the item arrived in-game." +
                (string.IsNullOrWhiteSpace(response) ? "" : "\nResponse: " + response);
            Log($"Admin gift request completed: #{item.Id} to {target}.");
        }
        catch (Exception ex)
        {
            PlayerGiftStatusText.Text = "Gift request failed: " + ex.Message;
            Log("Gift request failed: " + ex.Message);
        }
        finally { _giftBusy = false; UpdateGrantEnabled(); }
    }
}
