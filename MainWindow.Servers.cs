using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private readonly ServerHostService _serverHost = new();
    private readonly ServerComponentService _serverComponents = new();
    private bool _serverBusy;

    public ObservableCollection<ServerComponentRow> InstalledServerComponents { get; } = new();

    private string? ServerFolder(bool warn = true)
    {
        try { return ServerHostService.ValidateServerPath(ServerPathText.Text); }
        catch (Exception ex)
        {
            if (warn)
                MessageBox.Show(this, ex.Message, "Choose a server",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
    }

    private void SaveServerFolder(string folder)
    {
        string full = ServerHostService.ValidateServerPath(folder);
        _settings.ServerPath = full;
        _service.SaveSettings(_settings);
        ServerPathText.Text = full;
        ServerPathHint.Text = "Connected to server folder. Your choice is saved for next time.";
        RefreshServerComponentRows();
    }

    private async void Servers_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel("servers");
        if (string.IsNullOrWhiteSpace(ServerPathText.Text))
        {
            string? discovered = await _serverHost.DetectAsync();
            if (discovered != null)
            {
                try { SaveServerFolder(discovered); }
                catch (Exception ex) { Log("Server detection: " + ex.Message); }
            }
        }
        await RefreshServerStatusAsync();
    }

    private async void DetectServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ServerPathHint.Text = "Looking for a running Orbus server...";
            string? found = await _serverHost.DetectAsync();
            if (found == null)
            {
                ServerPathHint.Text = "No running server discovered. Browse to your OrbusRebornServer folder.";
                return;
            }
            SaveServerFolder(found);
            await RefreshServerStatusAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Server detection failed"); }
    }

    private void BrowseServerFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select OrbusRebornServer folder (with docker-compose.yml)" };
        if (dialog.ShowDialog(this) != true) return;
        try { SaveServerFolder(dialog.FolderName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Invalid server folder"); }
    }

    private async void SaveServerPath_Click(object sender, RoutedEventArgs e)
    {
        try { SaveServerFolder(ServerPathText.Text); await RefreshServerStatusAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Invalid server folder"); }
    }

    private void OpenServerFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = ServerFolder();
        if (folder == null) return;
        try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not open folder"); }
    }

    private async Task RefreshServerStatusAsync()
    {
        string? folder = ServerFolder(false);
        if (folder == null)
        {
            ServerStateText.Text = "No server selected";
            ServerSummaryText.Text = "Choose or detect an Orbus Reborn server folder.";
            return;
        }
        if (_serverBusy) return;
        try
        {
            ServerStateText.Text = "Checking Docker...";
            var status = await _serverHost.GetStatusAsync(folder);
            ServerStateText.Text = status.Running ? "● Server running" : "○ Server stopped";
            ServerSummaryText.Text = status.Summary;
            ServerPathHint.Text = folder;
            RefreshServerComponentRows();
        }
        catch (Exception ex)
        {
            ServerStateText.Text = "Status unavailable";
            ServerSummaryText.Text = ex.Message;
            Log("Server status unavailable: " + ex.Message);
        }
    }

    private async void RefreshServer_Click(object sender, RoutedEventArgs e) =>
        await RefreshServerStatusAsync();

    private void SetServerBusy(bool busy)
    {
        _serverBusy = busy;
        StartServerButton.IsEnabled = !busy;
        StopServerButton.IsEnabled = !busy;
        RestartServerButton.IsEnabled = !busy;
        ReloadServerModsButton.IsEnabled = !busy;
    }

    private async Task RunServerCommandAsync(Func<string, Task<string>> action,
        string label, bool confirm)
    {
        string? folder = ServerFolder();
        if (folder == null || _serverBusy) return;
        if (confirm && MessageBox.Show(this,
            label + "?\n\nThis may disconnect players. The database and characters are kept.",
            "Confirm server action", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetServerBusy(true);
        ServerStateText.Text = label + "...";
        try
        {
            await action(folder);
            Log(label + " completed.");
        }
        catch (Exception ex)
        {
            Log(label + " failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, label + " failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetServerBusy(false);
            await RefreshServerStatusAsync();
        }
    }

    private async void StartServer_Click(object sender, RoutedEventArgs e) =>
        await RunServerCommandAsync(_serverHost.StartAsync, "Start server", confirm: false);

    private async void StopServer_Click(object sender, RoutedEventArgs e) =>
        await RunServerCommandAsync(_serverHost.StopAsync, "Stop server", confirm: true);

    private async void RestartServer_Click(object sender, RoutedEventArgs e) =>
        await RunServerCommandAsync(_serverHost.RestartAsync, "Restart game and API", confirm: true);

    private async void ReloadServerMods_Click(object sender, RoutedEventArgs e) =>
        await RunServerCommandAsync(_serverHost.ReloadComponentsAsync, "Reload server mods", confirm: true);

    private async void ViewServerLogs_Click(object sender, RoutedEventArgs e)
    {
        string? folder = ServerFolder();
        if (folder == null) return;
        try
        {
            ServerLogsText.Text = "Loading logs...";
            string logs = await _serverHost.LogsAsync(folder);
            ServerLogsText.Text = string.IsNullOrWhiteSpace(logs) ? "(No recent logs.)" :
                logs.Length <= 40000 ? logs : logs[^40000..];
            ServerLogsText.ScrollToEnd();
        }
        catch (Exception ex) { ServerLogsText.Text = ex.Message; }
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
