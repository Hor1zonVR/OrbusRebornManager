using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrbusRebornManager;

// A server lives in the launcher just like a client instance: file location,
// human-readable name and a status that can be refreshed without opening it.
public sealed class ServerCardRow : INotifyPropertyChanged
{
    public string Path { get; }
    public string Name { get; }
    private string _status = "Not checked";
    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; Changed(); Changed(nameof(StatusColor)); } }
    }
    public Brush StatusColor => _status.StartsWith("● Running", StringComparison.Ordinal)
        ? new SolidColorBrush(Color.FromRgb(100, 215, 169))
        : _status.StartsWith("○ Stopped", StringComparison.Ordinal)
            ? new SolidColorBrush(Color.FromRgb(220, 155, 151))
            : new SolidColorBrush(Color.FromRgb(151, 165, 180));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ServerCardRow(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        try
        {
            string config = System.IO.Path.Combine(path, "config", "orbus.config.json");
            if (!File.Exists(config)) return;
            using var json = JsonDocument.Parse(File.ReadAllText(config));
            if (json.RootElement.TryGetProperty("serverName", out var label) &&
                label.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(label.GetString()))
                Name = label.GetString()!.Trim();
        }
        catch { /* A missing or malformed optional name must never block management. */ }
    }
}

public partial class MainWindow
{
    private readonly ServerHostService _serverHost = new();
    private readonly ServerComponentService _serverComponents = new();
    private bool _serverBusy;
    private bool _serverStatusKnown;
    private bool _serverRunning;
    private bool _consoleLoading;
    private bool _serverCardRefreshBusy;
    private int _statusRequestNumber;
    private string? _selectedServerPath;
    private string _serverTab = "overview";
    private readonly DispatcherTimer _statusRefreshTimer =
        new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _consoleRefreshTimer =
        new() { Interval = TimeSpan.FromSeconds(3) };

    public ObservableCollection<ServerCardRow> ServerCards { get; } = new();
    public ObservableCollection<ServerComponentRow> InstalledServerComponents { get; } = new();

    // Existing ServerPath settings automatically become the first entry.
    private void InitializeServerLibrary()
    {
        _settings.ManagedServerPaths ??= new();
        if (!string.IsNullOrWhiteSpace(_settings.ServerPath) &&
            !_settings.ManagedServerPaths.Any(p =>
                string.Equals(p, _settings.ServerPath, StringComparison.OrdinalIgnoreCase)))
            _settings.ManagedServerPaths.Insert(0, _settings.ServerPath);
        RefreshServerCards();
        SetServerStatusUnknown();
        _statusRefreshTimer.Tick += async (_, _) =>
        {
            if (ServerControlPanel.Visibility == Visibility.Visible && !_serverBusy)
                await RefreshServerStatusAsync();
            else if (ServerLibraryPanel.Visibility == Visibility.Visible)
                await RefreshServerCardsStatusAsync();
        };
        _statusRefreshTimer.Start();
        _consoleRefreshTimer.Tick += async (_, _) =>
        {
            if (ServerControlPanel.Visibility == Visibility.Visible &&
                _serverTab == "console" && ConsoleAutoRefreshCheck.IsChecked == true)
                await RefreshConsoleAsync();
        };
        _consoleRefreshTimer.Start();
    }

    private void RefreshServerCards()
    {
        ServerCards.Clear();
        foreach (string path in _settings.ManagedServerPaths ?? new())
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !ServerCards.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                ServerCards.Add(new ServerCardRow(path));
        }
        EmptyServerLibrary.Visibility = ServerCards.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        ServerLibraryCount.Text = ServerCards.Count == 1 ? "1 saved server" :
            ServerCards.Count + " saved servers";
    }

    private string? ServerFolder(bool warn = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_selectedServerPath))
                throw new InvalidOperationException("Choose a server card first.");
            return ServerHostService.ValidateServerPath(_selectedServerPath);
        }
        catch (Exception ex)
        {
            if (warn)
                MessageBox.Show(this, ex.Message, "Choose a server",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
    }

    private void AddServerFolder(string input)
    {
        string full = ServerHostService.ValidateServerPath(input);
        _settings.ManagedServerPaths ??= new();
        if (!_settings.ManagedServerPaths.Any(x => x.Equals(full, StringComparison.OrdinalIgnoreCase)))
            _settings.ManagedServerPaths.Add(full);
        _settings.ServerPath = full;
        _service.SaveSettings(_settings);
        RefreshServerCards();
        OpenServerDetail(full, "overview");
    }

    private async void Servers_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel("servers");
        if (ServerCards.Count == 0)
        {
            try
            {
                string? detected = await _serverHost.DetectAsync();
                if (detected != null)
                {
                    // Auto-register the exact running server only when nothing was saved.
                    _settings.ManagedServerPaths ??= new();
                    if (!_settings.ManagedServerPaths.Contains(detected, StringComparer.OrdinalIgnoreCase))
                    {
                        _settings.ManagedServerPaths.Add(detected);
                        _settings.ServerPath = detected;
                        _service.SaveSettings(_settings);
                        RefreshServerCards();
                    }
                }
            }
            catch { /* The Add server action remains available without Docker. */ }
        }
        await RefreshServerCardsStatusAsync();
    }

    private async Task RefreshServerCardsStatusAsync()
    {
        if (_serverCardRefreshBusy) return;
        _serverCardRefreshBusy = true;
        try
        {
            foreach (var card in ServerCards.ToArray())
            {
                if (ServerLibraryPanel.Visibility != Visibility.Visible) break;
                try
                {
                    var status = await _serverHost.GetStatusAsync(card.Path);
                    if (ServerCards.Contains(card))
                        card.Status = status.Running ? "● Running" : "○ Stopped";
                }
                catch (Exception ex)
                {
                    if (ServerCards.Contains(card))
                        card.Status = "Unavailable · " + ex.Message.Split('\n')[0];
                }
            }
        }
        finally { _serverCardRefreshBusy = false; }
    }

    private async void DetectServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? found = await _serverHost.DetectAsync();
            if (found == null)
            {
                MessageBox.Show(this, "No local Orbus server was discovered. Use Add existing server to browse for its folder.",
                    "Server not found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AddServerFolder(found);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not detect server"); }
    }

    private void BrowseServerFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder containing the Orbus server's docker-compose.yml"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { AddServerFolder(dialog.FolderName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Invalid server folder"); }
    }

    private void ServerCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ServerCardRow row })
            OpenServerDetail(row.Path, "overview");
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServerCardRow row }) return;
        if (MessageBox.Show(this,
            "Remove '" + row.Name + "' from OrbusManager?\n\nThis only removes the saved entry; the Docker server, database and files are NOT deleted.",
            "Forget server", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _settings.ManagedServerPaths.RemoveAll(p =>
            p.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
        if (_settings.ServerPath.Equals(row.Path, StringComparison.OrdinalIgnoreCase))
            _settings.ServerPath = _settings.ManagedServerPaths.FirstOrDefault() ?? "";
        if (_selectedServerPath?.Equals(row.Path, StringComparison.OrdinalIgnoreCase) == true)
            _selectedServerPath = null;
        _service.SaveSettings(_settings);
        RefreshServerCards();
    }

    private async void BackToServers_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel("servers");
        await RefreshServerCardsStatusAsync();
    }

    private void OpenServerDetail(string path, string tab)
    {
        try { _selectedServerPath = ServerHostService.ValidateServerPath(path); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Server unavailable"); return; }
        _settings.ServerPath = _selectedServerPath;
        _service.SaveSettings(_settings);
        SelectedServerName.Text = ServerCards.FirstOrDefault(x =>
            x.Path.Equals(_selectedServerPath, StringComparison.OrdinalIgnoreCase))?.Name
            ?? System.IO.Path.GetFileName(_selectedServerPath);
        SelectedServerPathText.Text = _selectedServerPath;
        _serverStatusKnown = false;
        _serverRunning = false;
        SetServerStatusUnknown();
        ShowPanel("serverdetail");
        ShowServerTab(tab);
        RefreshServerComponentRows();
        _ = RefreshServerStatusAsync();
    }

    private void ShowServerTab(string tab)
    {
        _serverTab = tab;
        ServerOverviewTab.Visibility = tab == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ServerPanel.Visibility = tab == "mods" ? Visibility.Visible : Visibility.Collapsed;
        ServerConsoleTab.Visibility = tab == "console" ? Visibility.Visible : Visibility.Collapsed;
        var selected = new SolidColorBrush(Color.FromRgb(50, 66, 70));
        var normal = Brushes.Transparent;
        OverviewTabButton.Background = tab == "overview" ? selected : normal;
        ServerModsTabButton.Background = tab == "mods" ? selected : normal;
        ConsoleTabButton.Background = tab == "console" ? selected : normal;
        NavServer.Background = tab == "mods"
            ? new SolidColorBrush(Color.FromRgb(48, 50, 58)) : Brushes.Transparent;
        NavServers.Background = tab != "mods"
            ? new SolidColorBrush(Color.FromRgb(48, 50, 58)) : Brushes.Transparent;
        if (tab == "mods") RefreshServerComponentRows();
        if (tab == "console") _ = RefreshConsoleAsync(force: true);
    }

    // Sidebar Server Mods is a shortcut to the selected server's Mods tab.
    private void OpenServerModLibrary()
    {
        string? path = _selectedServerPath ?? _settings.ServerPath;
        if (!string.IsNullOrWhiteSpace(path))
            OpenServerDetail(path, "mods");
        else ShowPanel("servers");
    }

    private void ServerOverviewTab_Click(object sender, RoutedEventArgs e) => ShowServerTab("overview");
    private void ServerModsTab_Click(object sender, RoutedEventArgs e) => ShowServerTab("mods");
    private void ServerConsoleTab_Click(object sender, RoutedEventArgs e) => ShowServerTab("console");

    private void SetServerStatusUnknown()
    {
        ServerStateText.Text = "Checking status";
        ServerSummaryText.Text = "Checking Docker Compose services…";
        ServerStateText.Foreground = new SolidColorBrush(Color.FromRgb(205, 208, 216));
        _serverStatusKnown = false;
        UpdateServerActions();
    }

    private void UpdateServerActions()
    {
        StartStopServerButton.IsEnabled = !_serverBusy && _serverStatusKnown;
        RestartServerButton.IsEnabled = !_serverBusy && _serverStatusKnown && _serverRunning;
        ReloadServerModsButton.IsEnabled = !_serverBusy && _serverStatusKnown && _serverRunning;
        StartStopServerButton.Content = _serverRunning ? "■  Stop server" : "▶  Start server";
        StartStopServerButton.Background = _serverRunning
            ? new SolidColorBrush(Color.FromRgb(111, 56, 60))
            : new SolidColorBrush(Color.FromRgb(84, 212, 171));
        StartStopServerButton.Foreground = _serverRunning
            ? Brushes.White : new SolidColorBrush(Color.FromRgb(18, 36, 32));
    }

    private async Task RefreshServerStatusAsync()
    {
        string? folder = ServerFolder(false);
        if (folder == null || _serverBusy) return;
        int request = ++_statusRequestNumber;
        try
        {
            var status = await _serverHost.GetStatusAsync(folder);
            if (request != _statusRequestNumber || _serverBusy ||
                !string.Equals(folder, _selectedServerPath, StringComparison.OrdinalIgnoreCase)) return;
            _serverRunning = status.Running;
            _serverStatusKnown = true;
            ServerStateText.Text = status.Running ? "Online" : "Offline";
            ServerStateText.Foreground = new SolidColorBrush(status.Running
                ? Color.FromRgb(106, 228, 177) : Color.FromRgb(235, 174, 155));
            ServerSummaryText.Text = status.Summary;
            GameServiceText.Text = status.Services.FirstOrDefault(x => x.Service == "game")?.State ?? "stopped";
            ApiServiceText.Text = status.Services.FirstOrDefault(x => x.Service == "api")?.State ?? "stopped";
            OtherServicesText.Text = status.Services.Count(x => x.Service != "game" && x.Service != "api")
                .ToString();
            foreach (var row in ServerCards.Where(x =>
                x.Path.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                row.Status = status.Running ? "● Running" : "○ Stopped";
        }
        catch (Exception ex)
        {
            if (request != _statusRequestNumber ||
                !string.Equals(folder, _selectedServerPath, StringComparison.OrdinalIgnoreCase)) return;
            _serverStatusKnown = false;
            ServerStateText.Text = "Unavailable";
            ServerSummaryText.Text = ex.Message;
            ServerStateText.Foreground = new SolidColorBrush(Color.FromRgb(235, 174, 155));
        }
        finally { if (request == _statusRequestNumber) UpdateServerActions(); }
    }

    private async void RefreshServer_Click(object sender, RoutedEventArgs e) =>
        await RefreshServerStatusAsync();

    private void SetServerBusy(bool busy)
    {
        _serverBusy = busy;
        if (busy) ++_statusRequestNumber; // invalidate an in-flight status request
        UpdateServerActions();
    }

    private async Task RunServerCommandAsync(Func<string, Task<string>> action,
        string label, bool confirm)
    {
        string? folder = ServerFolder();
        if (folder == null || _serverBusy) return;
        if (confirm && MessageBox.Show(this,
            label + "?\n\nConnected players may be disconnected. This action does NOT delete your characters or database.",
            "Confirm server action", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetServerBusy(true);
        _serverStatusKnown = false;
        UpdateServerActions();
        ServerStateText.Text = label + "...";
        ServerActivityText.Text = label + " in progress…";
        try
        {
            await action(folder);
            ServerActivityText.Text = label + " completed.";
            Log(label + " completed.");
        }
        catch (Exception ex)
        {
            ServerActivityText.Text = label + " failed: " + ex.Message;
            Log(label + " failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, label + " failed", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _serverStatusKnown = false;
            SetServerBusy(false);
            await RefreshServerStatusAsync();
        }
    }

    private async void StartStopServer_Click(object sender, RoutedEventArgs e)
    {
        if (!_serverStatusKnown || _serverBusy) return;
        bool stop = _serverRunning;
        await RunServerCommandAsync(stop ? _serverHost.StopAsync : _serverHost.StartAsync,
            stop ? "Stop server" : "Start server", confirm: stop);
    }

    private async void RestartServer_Click(object sender, RoutedEventArgs e) =>
        await RunServerCommandAsync(_serverHost.RestartAsync, "Restart game and API", confirm: true);

    private async void ReloadServerMods_Click(object sender, RoutedEventArgs e) =>
        await RunServerCommandAsync(_serverHost.ReloadComponentsAsync, "Reload server mods", confirm: true);

    private void OpenServerFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = ServerFolder();
        if (folder == null) return;
        try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open folder"); }
    }

    private async Task RefreshConsoleAsync(bool force = false)
    {
        if (_consoleLoading || _serverBusy || _serverTab != "console") return;
        string? folder = ServerFolder(false);
        if (folder == null) return;
        _consoleLoading = true;
        try
        {
            string text = await _serverHost.LogsAsync(folder);
            if (!string.Equals(folder, _selectedServerPath, StringComparison.OrdinalIgnoreCase)) return;
            string result = string.IsNullOrWhiteSpace(text) ? "(No recent server logs.)" :
                text.Length <= 60000 ? text : text[^60000..];
            if (force || !string.Equals(ServerLogsText.Text, result, StringComparison.Ordinal))
            {
                ServerLogsText.Text = result;
                if (ConsoleAutoRefreshCheck.IsChecked == true) ServerLogsText.ScrollToEnd();
            }
            ConsoleStatusText.Text = "Latest 180 lines · updates every 3 seconds when Auto-refresh is enabled";
        }
        catch (Exception ex)
        {
            if (string.Equals(folder, _selectedServerPath, StringComparison.OrdinalIgnoreCase))
                ConsoleStatusText.Text = "Console unavailable: " + ex.Message;
        }
        finally { _consoleLoading = false; }
    }

    private async void ViewServerLogs_Click(object sender, RoutedEventArgs e) =>
        await RefreshConsoleAsync(force: true);

    private void CopyServerLogs_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ServerLogsText.Text))
            Clipboard.SetText(ServerLogsText.Text);
    }

    private void RefreshServerComponentRows()
    {
        if (InstalledServerComponents == null || ServerComponentPathText == null) return;
        InstalledServerComponents.Clear();
        string? folder = ServerFolder(false);
        if (folder == null)
        {
            ServerComponentPathText.Text = "Choose your server under My Servers first.";
            EmptyInstalledServerComponents.Visibility = Visibility.Visible;
            return;
        }
        ServerComponentPathText.Text = Path.Combine(folder, "mods", "components");
        try
        {
            foreach (var component in _serverComponents.List(folder))
                InstalledServerComponents.Add(component);
            EmptyInstalledServerComponents.Visibility =
                InstalledServerComponents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            EmptyInstalledServerComponents.Visibility = Visibility.Visible;
            Log("Couldn't read server components: " + ex.Message);
        }
    }

    private void RefreshServerComponents_Click(object sender, RoutedEventArgs e) =>
        RefreshServerComponentRows();

    private void ImportServerComponent_Click(object sender, RoutedEventArgs e)
    {
        string? folder = ServerFolder();
        if (folder == null) return;
        var picker = new OpenFileDialog
        {
            Title = "Choose a trusted Orbus server component",
            Filter = "JavaScript server component (*.js)|*.js"
        };
        if (picker.ShowDialog(this) != true) return;
        if (MessageBox.Show(this,
            "Install " + Path.GetFileName(picker.FileName) + " into this server?\n\n" +
            "Server-side JavaScript can run with the server's permissions. Only install trusted code.",
            "Install server component", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _serverComponents.Import(folder, picker.FileName);
            RefreshServerComponentRows();
            Log("Component copied. Restart the game service to load it.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed"); }
    }

    private void ToggleServerComponent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServerComponentRow component }) return;
        string? folder = ServerFolder();
        if (folder == null) return;
        string action = component.Enabled ? "Disable" : "Enable";
        if (MessageBox.Show(this,
            action + " " + component.FileName + "?\n\nRestart the game service afterward.",
            action + " component", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _serverComponents.Toggle(folder, component);
            RefreshServerComponentRows();
            Log(component.FileName + (component.Enabled ? " disabled." : " enabled.") + " Restart game to apply.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Component action failed"); }
    }

    private async void InstallServerCatalogMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModRow row } || row.Working) return;
        string? folder = ServerFolder();
        if (folder == null) return;
        if (MessageBox.Show(this,
            "Install/update " + row.Name + " from its reviewed GitHub release?\n\n" +
            "Server-side scripts run with the server's permissions. Only install code you trust.",
            "Install server mod", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        row.Working = true;
        try
        {
            row.Status = "Downloading reviewed .js release...";
            string result = await _serverComponents.InstallCuratedAsync(folder, row.Definition, _service);
            row.Status = result;
            RefreshServerComponentRows();
            Log(result);
        }
        catch (Exception ex)
        {
            row.Status = ex.Message;
            MessageBox.Show(this, ex.Message, "Server mod installation failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { row.Working = false; }
    }
}
