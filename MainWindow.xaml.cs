using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Controls.Primitives;

namespace OrbusRebornManager;

public partial class MainWindow : Window
{
    private readonly ManagerService _service = new();
    private ManagerSettings _settings;
    private CatalogDocument _catalog = new();
    private readonly List<ModRow> _allDiscover = new();
    private bool _initialCatalogCheck = true;
    private int _jobs;
    private readonly InstanceStore _instanceStore = new();
    public ObservableCollection<InstanceCard> InstanceCards { get; } = new();
    public ObservableCollection<ModRow> ServerMods { get; } = new();

    public ObservableCollection<ModRow> DiscoverMods { get; } = new();
    public ObservableCollection<ModRow> InstalledMods { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _settings = _service.LoadSettings();
        GamePathText.Text = _settings.GamePath;
        CatalogUrlText.Text = _settings.CatalogUrl;
        AutoUpdatesCheck.IsChecked = _settings.AutoUpdate;
        InitializeManagerUpdates();
        ShowPanel("instances");
        UpdateDashboard();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshInstanceCards();
        await RefreshCatalogAsync();
        if (_settings.CheckManagerUpdates)
            await CheckManagerUpdatesAsync();
    }

    private void Log(string message)
    {
        StatusLine.Text = message;
        ActivityBar.Visibility = _jobs > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RunAsync(Func<Task> work)
    {
        _jobs++;
        ActivityBar.Visibility = Visibility.Visible;
        try { await work(); }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Orbus Reborn Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _jobs--;
            ActivityBar.Visibility = _jobs > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private string? GameOrWarn(bool showMessage = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.GamePath))
                throw new InvalidOperationException("Choose your OrbusVR Reborn folder first.");
            return _service.NormalizeGamePath(_settings.GamePath);
        }
        catch (Exception ex)
        {
            if (showMessage) MessageBox.Show(this, ex.Message, "Select game", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
    }

    private void UpdateDashboard()
    {
        string? game = GameOrWarn(false);
        bool ready = game != null;
        bool loader = ready && _service.IsBepInExInstalled(game!);
        GamePathText.Text = _settings.GamePath;
        TopStatus.Text = !ready ? "●  Select game folder" : loader ? "●  BepInEx detected" : "●  Loader required";
        LoaderStatusText.Text = loader ? "BepInEx detected" : ready ? "Not installed" : "Select your game";
        InstallLoaderButton.IsEnabled = ready && !loader;
        if (ready)
        {
            try
            {
                int count = _service.LoadInstalled(game!).Mods.Count;
                ModsCountText.Text = count + (count == 1 ? " managed mod" : " managed mods");
            }
            catch { ModsCountText.Text = "Mod database error"; }
        }
        else ModsCountText.Text = "0 managed mods";
    }

    private void ShowPanel(string page)
    {
        InstancesPanel.Visibility = page == "instances" ? Visibility.Visible : Visibility.Collapsed;
        DashboardPanel.Visibility = page == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        DiscoverPanel.Visibility = page == "discover" ? Visibility.Visible : Visibility.Collapsed;
        ServerPanel.Visibility = page == "server" ? Visibility.Visible : Visibility.Collapsed;
        InstalledPanel.Visibility = page == "installed" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        (string header, string sub) = page switch
        {
            "instances" => ("My Instances", "Your OrbusVR installations, all in one place."),
            "dashboard" => ("Instance Overview", "Your selected OrbusVR installation."),
            "discover" => ("Client Mods", "Discover and install community client mods."),
            "server" => ("Server Mods", "Find projects for community server operators."),
            "installed" => ("Installed Mods", "Manage mods for your selected instance."),
            "settings" => ("Settings", "Manage your preferences."),
            _ => ("My Instances", "")
        };
        PageHeading.Text = header;
        PageSubtitle.Text = sub;
        HeaderCreateButton.Visibility = page == "instances" ? Visibility.Visible : Visibility.Collapsed;
        HeaderStatusCard.Visibility = page == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        NavHome.Background = page == "instances" || page == "dashboard" || page == "installed" ?
            new SolidColorBrush(Color.FromRgb(37, 55, 70)) : Brushes.Transparent;
        NavBrowse.Background = page == "discover" ?
            new SolidColorBrush(Color.FromRgb(37, 55, 70)) : Brushes.Transparent;
        NavServer.Background = page == "server" ?
            new SolidColorBrush(Color.FromRgb(37, 55, 70)) : Brushes.Transparent;
        NavSettings.Background = page == "settings" ?
            new SolidColorBrush(Color.FromRgb(37, 55, 70)) : Brushes.Transparent;
        if (page == "instances") RefreshInstanceCards();
        if (page == "installed") UpdateInstalledRows();
    }

    private void Instances_Click(object sender, RoutedEventArgs e) => ShowPanel("instances");
    private void Dashboard_Click(object sender, RoutedEventArgs e) => ShowPanel("dashboard");
    private void Discover_Click(object sender, RoutedEventArgs e) => ShowPanel("discover");
    private void ServerMods_Click(object sender, RoutedEventArgs e) => ShowPanel("server");
    private void Installed_Click(object sender, RoutedEventArgs e) => ShowPanel("installed");
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPanel("settings");

    private void RefreshInstanceCards()
    {
        InstanceCards.Clear();
        try
        {
            var list = _instanceStore.Load();
            if (!string.IsNullOrWhiteSpace(_settings.GamePath) &&
                !list.Any(x => string.Equals(
                    Path.GetFullPath(x.Path).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(_settings.GamePath).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    string valid = _service.NormalizeGamePath(_settings.GamePath);
                    _instanceStore.AddExisting(list, valid);
                }
                catch { /* Invalid paths are displayed on the existing overview. */ }
            }
            foreach (var instance in list)
            {
                string status;
                string count = "0 mods";
                try
                {
                    string valid = _service.NormalizeGamePath(instance.Path);
                    int mods = _service.LoadInstalled(valid).Mods.Count;
                    count = mods == 1 ? "1 mod" : mods + " mods";
                    status = _service.IsBepInExInstalled(valid) ? "Ready for mods" : "Mod loader needed";
                }
                catch
                {
                    status = "Game files not found";
                }
                bool selected = string.Equals(instance.Path, _settings.GamePath,
                    StringComparison.OrdinalIgnoreCase);
                InstanceCards.Add(new InstanceCard(instance, count, status, selected));
            }
        }
        catch (Exception ex) { Log("Could not load instances: " + ex.Message); }
        EmptyInstancesPanel.Visibility = InstanceCards.Count == 0 ?
            Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectInstance(InstanceCard card)
    {
        string valid = _service.NormalizeGamePath(card.Instance.Path);
        _settings.GamePath = valid;
        _service.SaveSettings(_settings);
        UpdateDashboard();
        UpdateInstalledRows();
        UpdateReleaseStatuses();
        RefreshInstanceCards();
    }

    private void InstanceCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            sender is not FrameworkElement { Tag: InstanceCard card })
            return;

        // A button inside the card must perform only its own action.
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null && !ReferenceEquals(source, sender))
        {
            if (source is ButtonBase)
                return;
            source = VisualTreeHelper.GetParent(source);
        }

        OpenInstance(card);
        e.Handled = true;
    }

    private void OpenInstance(InstanceCard card)
    {
        try
        {
            SelectInstance(card);
            ShowPanel("installed");
            PageHeading.Text = card.Name;
            PageSubtitle.Text = "Manage mods and launch your instance.";
            SelectedInstanceName.Text = card.Name;
            SelectedInstanceMeta.Text = card.Kind + "  ·  " + card.ModCount;
            SelectedInstanceLoaderText.Text = card.Readiness;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Instance unavailable",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InstanceCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstanceCard card })
            return;

        try
        {
            Clipboard.SetText(card.Instance.Path);
            Log("Instance folder path copied.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Couldn't copy folder path",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InstanceManage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstanceCard card }) return;
        OpenInstance(card);
    }

    private void InstancePlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstanceCard card }) return;
        try
        {
            SelectInstance(card);
            _service.Launch(card.Instance.Path);
            Log("Launching " + card.Name + "...");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Launch failed"); }
    }

    private void InstanceOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstanceCard card }) return;
        try
        {
            if (!Directory.Exists(card.Instance.Path)) throw new DirectoryNotFoundException(card.Instance.Path);
            Process.Start(new ProcessStartInfo { FileName = card.Instance.Path, UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Folder unavailable"); }
    }

    private void InstanceForget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstanceCard card }) return;
        if (MessageBox.Show(this,
            "Remove '" + card.Name + "' from the manager? Game files will not be deleted.",
            "Remove from list", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var list = _instanceStore.Load();
            list.RemoveAll(x => string.Equals(x.Path, card.Instance.Path, StringComparison.OrdinalIgnoreCase));
            _instanceStore.Save(list);
            if (string.Equals(_settings.GamePath, card.Instance.Path, StringComparison.OrdinalIgnoreCase))
            {
                _settings.GamePath = "";
                _service.SaveSettings(_settings);
                UpdateDashboard();
            }
            RefreshInstanceCards();
            Log("Removed from the instance list. Game files preserved.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not remove instance"); }
    }

    private void ServerSearchText_Changed(object sender, TextChangedEventArgs e) => FilterMods();

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Select OrbusVR Reborn folder (containing vrclient.exe)" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            _settings.GamePath = _service.NormalizeGamePath(picker.FolderName);
            _service.SaveSettings(_settings);
            UpdateDashboard();
            UpdateInstalledRows();
            UpdateReleaseStatuses();
            Log("Selected " + _settings.GamePath);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Invalid OrbusVR folder", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void InstallLoader_Click(object sender, RoutedEventArgs e)
    {
        string? game = GameOrWarn();
        if (game == null) return;
        if (MessageBox.Show(this,
            "Install official BepInEx Unity IL2CPP Windows x64 build 788 into this OrbusVR Reborn folder?\n\n" +
            game + "\n\nThe game must be closed. Files already present inside BepInEx are backed up before replacement.",
            "Install BepInEx", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async () =>
        {
            await _service.InstallLoaderAsync(game, m => Dispatcher.Invoke(() => Log(m)));
            UpdateDashboard();
            MessageBox.Show(this,
                "BepInEx installed! Launch OrbusVR once and let it generate its IL2CPP interop files. The first launch may be slow.",
                "Loader ready", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        string? game = GameOrWarn();
        if (game == null) return;
        try { _service.Launch(game); Log("Launching OrbusVR Reborn..."); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Launch failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        string? game = GameOrWarn();
        if (game == null) return;
        string folder = Path.Combine(game, "BepInEx");
        if (!Directory.Exists(folder)) { Log("Install and launch BepInEx first to create logs."); return; }
        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    private void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        string? game = GameOrWarn();
        if (game == null) return;
        var dialog = new OpenFileDialog { Title = "Choose a compiled BepInEx plugin DLL", Filter = "BepInEx DLL (*.dll)|*.dll" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _service.ImportLocalDll(game, dialog.FileName);
            UpdateDashboard(); UpdateInstalledRows(); UpdateReleaseStatuses();
            Log("Local test mod imported: " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task RefreshCatalogAsync()
    {
        await RunAsync(async () =>
        {
            Log("Refreshing curated catalogue...");
            var result = await _service.FetchCatalogAsync(_settings.CatalogUrl);
            _catalog = result.Catalog;
            CatalogInfo.Text = result.Status + "  ·  " + _catalog.Mods.Count + " approved mod(s)";
            _allDiscover.Clear();
            foreach (var def in _catalog.Mods)
                if (!string.Equals(def.Target, "server", StringComparison.OrdinalIgnoreCase))
                    _allDiscover.Add(new ModRow(def));
            FilterMods();
            UpdateReleaseStatuses();
            Log("Catalogue refreshed: " + _catalog.Mods.Count + " mods.");
            await CheckReleasesAsync(_initialCatalogCheck && _settings.AutoUpdate);
            _initialCatalogCheck = false;
        });
    }

    private async Task CheckReleasesAsync(bool autoUpdate)
    {
        string? game = GameOrWarn(false);
        foreach (var row in _allDiscover)
        {
            row.Working = true;
            row.Status = "Checking GitHub Releases...";
            try
            {
                var result = await _service.GetLatestAsync(row.Definition);
                row.Release = result.Release;
                row.Asset = result.Asset;
                row.Version = "Latest " + result.Release.TagName;
                var installed = game == null ? null : _service.FindInstalled(game, row.Definition.Id);
                bool update = installed != null && !installed.Local && installed.Version != result.Release.TagName;
                if (autoUpdate && update && game != null && _service.IsBepInExInstalled(game) && !_service.IsGameRunning())
                {
                    Log("Updating " + row.Name + " to " + result.Release.TagName + "...");
                    await _service.InstallReleaseAsync(game, row.Definition, result.Release, result.Asset);
                    Log("Updated " + row.Name + " automatically.");
                }
            }
            catch (Exception ex)
            {
                row.Version = "Release unavailable";
                row.Status = ex.Message;
            }
            finally { row.Working = false; }
        }
        UpdateReleaseStatuses();
        UpdateInstalledRows();
        UpdateDashboard();
    }

    private void UpdateReleaseStatuses()
    {
        string? game = GameOrWarn(false);
        foreach (var row in _allDiscover)
        {
            if (row.Release == null)
            {
                row.Action = "Unavailable";
                if (row.Status == "Checking releases...") row.Status = "No published release checked.";
                continue;
            }
            InstalledMod? mod = null;
            if (game != null)
            {
                try { mod = _service.FindInstalled(game, row.Definition.Id); }
                catch { /* Dashboard surfaces state errors. */ }
            }
            if (mod == null) { row.Status = "Ready to install"; row.Action = "Install"; }
            else if (mod.Local) { row.Status = "Local test build installed"; row.Action = "Local build"; }
            else if (mod.Version != row.Release.TagName) { row.Status = "Update available · installed " + mod.Version; row.Action = "Update"; }
            else { row.Status = mod.Enabled ? "Installed and up to date" : "Installed · disabled"; row.Action = "Installed"; }
        }
    }

    private void FilterMods()
    {
        if (EmptyCatalogText == null || EmptyServerPanel == null) return;
        string search = SearchText?.Text?.Trim() ?? "";
        DiscoverMods.Clear();
        foreach (var row in _allDiscover)
            if (search.Length == 0 ||
                row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Author.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
                DiscoverMods.Add(row);
        EmptyCatalogText.Visibility = DiscoverMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        string serverSearch = ServerSearchText?.Text?.Trim() ?? "";
        ServerMods.Clear();
        foreach (var mod in _catalog.Mods)
            if (string.Equals(mod.Target, "server", StringComparison.OrdinalIgnoreCase) &&
                (serverSearch.Length == 0 ||
                 mod.Name.Contains(serverSearch, StringComparison.OrdinalIgnoreCase) ||
                 mod.Author.Contains(serverSearch, StringComparison.OrdinalIgnoreCase) ||
                 mod.Description.Contains(serverSearch, StringComparison.OrdinalIgnoreCase)))
                ServerMods.Add(new ModRow(mod));
        EmptyServerPanel.Visibility = ServerMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchText_Changed(object sender, TextChangedEventArgs e) { if (DiscoverMods != null) FilterMods(); }
    private async void RefreshCatalog_Click(object sender, RoutedEventArgs e) => await RefreshCatalogAsync();
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await RunAsync(() => CheckReleasesAsync(false));

    private async void ModAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModRow row }) return;
        string? game = GameOrWarn();
        if (game == null) return;
        if (row.Release == null || row.Asset == null)
        {
            MessageBox.Show(this, "This mod has no matching stable GitHub release yet.", "Release unavailable");
            return;
        }
        InstalledMod? installed = _service.FindInstalled(game, row.Definition.Id);
        if (installed != null && installed.Version == row.Release.TagName) { ShowPanel("installed"); return; }
        if (!_service.IsBepInExInstalled(game))
        {
            MessageBox.Show(this, "Install BepInEx from the Dashboard first.", "Loader required");
            ShowPanel("dashboard"); return;
        }
        string warning = installed == null ?
            "Install " + row.Name + " " + row.Release.TagName + " from this approved GitHub release?\n\n" +
            "BepInEx plugins can execute code inside your game. Only install mods whose publisher you trust.\n\n" +
            (_settings.AutoUpdate ? "Automatic future release updates are ENABLED." : "Automatic updates are disabled.") :
            "Update " + row.Name + " from " + installed.Version + " to " + row.Release.TagName + "?";
        if (MessageBox.Show(this, warning, "Confirm mod installation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunAsync(async () =>
        {
            row.Working = true;
            try
            {
                await _service.InstallReleaseAsync(game, row.Definition, row.Release, row.Asset, m => Dispatcher.Invoke(() => Log(m)));
                Log(row.Name + " installed successfully.");
                UpdateDashboard(); UpdateReleaseStatuses(); UpdateInstalledRows();
            }
            finally { row.Working = false; }
        });
    }

    private void OpenGithub_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModRow row }) return;
        try { Process.Start(new ProcessStartInfo { FileName = row.Repository, UseShellExecute = true }); }
        catch (Exception ex) { Log("Could not open GitHub: " + ex.Message); }
    }

    private void UpdateInstalledRows()
    {
        InstalledMods.Clear();
        string? game = GameOrWarn(false);
        if (game == null) { EmptyInstalledText.Visibility = Visibility.Visible; return; }
        try
        {
            foreach (var installed in _service.LoadInstalled(game).Mods)
            {
                var definition = _catalog.Mods.FirstOrDefault(m => m.Id.Equals(installed.Id, StringComparison.OrdinalIgnoreCase))
                    ?? new ModDefinition { Id = installed.Id, Name = installed.Name, Author = installed.Local ? "Local build" : "Unknown publisher", Repository = installed.Repository, Description = "Installed mod" };
                var row = new ModRow(definition)
                {
                    Version = installed.Version,
                    Status = installed.Local ? "Local test DLL · " + (installed.Enabled ? "enabled" : "disabled") :
                        installed.Enabled ? "Enabled" : "Disabled",
                    Action = installed.Enabled ? "Disable" : "Enable"
                };
                InstalledMods.Add(row);
            }
        }
        catch (Exception ex) { Log("Cannot read mods: " + ex.Message); }
        EmptyInstalledText.Visibility = InstalledMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InstalledToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModRow row }) return;
        string? game = GameOrWarn(); if (game == null) return;
        try
        {
            var current = _service.FindInstalled(game, row.Definition.Id);
            if (current == null) return;
            _service.SetModEnabled(game, current.Id, !current.Enabled);
            UpdateInstalledRows(); UpdateReleaseStatuses();
            Log(row.Name + (current.Enabled ? " disabled." : " enabled."));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Mod toggle failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void InstalledUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModRow row }) return;
        string? game = GameOrWarn(); if (game == null) return;
        if (MessageBox.Show(this, "Remove managed mod '" + row.Name + "'?\n\nIts BepInEx configuration files are preserved.",
            "Uninstall mod", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _service.UninstallMod(game, row.Definition.Id);
            UpdateInstalledRows(); UpdateReleaseStatuses(); UpdateDashboard();
            Log("Uninstalled " + row.Name + ".");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Uninstall failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        string url = CatalogUrlText.Text.Trim();
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"))
        {
            MessageBox.Show(this, "Use an HTTPS raw mods.json URL.", "Invalid catalogue URL"); return;
        }
        _settings.CatalogUrl = url;
        _settings.AutoUpdate = AutoUpdatesCheck.IsChecked == true;
        _settings.CheckManagerUpdates = CheckManagerUpdatesCheck.IsChecked == true;
        _service.SaveSettings(_settings);
        _initialCatalogCheck = false; // Changing catalogue must not silently auto-install updates.
        await RefreshCatalogAsync();
    }

    private async void AddDraft_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string repo = NewRepoText.Text.Trim();
            var parts = ManagerService.GetRepoParts(repo);
            string pattern = NewAssetText.Text.Trim();
            _service.AddLocalDraft(repo, NewNameText.Text.Trim(), parts.Owner, pattern);
            _initialCatalogCheck = false;
            Log("Local draft added. Export JSON to publish after review.");
            await RefreshCatalogAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not add draft", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ExportCatalog_Click(object sender, RoutedEventArgs e)
    {
        var save = new SaveFileDialog { FileName = "mods.json", Filter = "JSON (*.json)|*.json" };
        if (save.ShowDialog(this) != true) return;
        try
        {
            _service.ExportLocalCatalog(save.FileName, _catalog);
            MessageBox.Show(this, "Catalogue exported. Review the JSON and commit it to your public catalogue repository to make the listings available to other players.", "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}

public sealed class InstanceCard
{
    public GameInstance Instance { get; }
    public string Name => Instance.Name;
    public string Kind => Instance.CreatedByManager ? "Modded instance" : "Existing installation";
    public string ModCount { get; }
    public string Readiness { get; }
    public bool IsSelected { get; }
    public InstanceCard(GameInstance instance, string modCount, string readiness, bool isSelected)
    {
        Instance = instance;
        ModCount = modCount;
        Readiness = readiness;
        IsSelected = isSelected;
    }
}
