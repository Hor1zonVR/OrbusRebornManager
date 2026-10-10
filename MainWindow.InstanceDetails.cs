using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrbusRebornManager;

// A view of a managed mod in one instance. Rebuilt from the registry each
// time; we don't change or accidentally index the underlying mod database.
public sealed class InstanceModRow
{
    private static readonly Brush[] Backgrounds =
    {
        new SolidColorBrush(Color.FromRgb(63, 69, 91)),
        new SolidColorBrush(Color.FromRgb(54, 86, 77)),
        new SolidColorBrush(Color.FromRgb(84, 61, 91)),
        new SolidColorBrush(Color.FromRgb(90, 72, 53)),
        new SolidColorBrush(Color.FromRgb(53, 76, 97)),
    };

    public InstalledMod Installed { get; }
    public ModDefinition Definition { get; }
    public string Name => Installed.Name;
    public string Version => Installed.Local ? "Local build" : Installed.Version;
    public string AssetDisplay => string.IsNullOrWhiteSpace(Installed.AssetName)
        ? (Installed.Local ? "Local plugin" : "Managed plugin")
        : Installed.AssetName;
    public string Author => Installed.Local ? "Local DLL" :
        string.IsNullOrWhiteSpace(Definition.Author) ? "Community mod" : Definition.Author;
    public string IconLetter => string.IsNullOrWhiteSpace(Name) ? "M" :
        Name.Trim()[0].ToString().ToUpperInvariant();
    public Brush IconBrush { get; }
    public bool MissingFiles { get; }
    public bool CanModify => !MissingFiles;
    public bool CanUpdate => UpdateAvailable && Release != null && Asset != null && !Installed.Local;
    public bool UpdateAvailable { get; }
    public string UpdateText => CanUpdate ? "Update available: " + Release!.TagName : "";
    public string ToggleText => Installed.Enabled ? "Disable" : "Enable";
    public string Status => MissingFiles ? "Files missing" :
        Installed.Enabled ? "Enabled" : "Disabled";
    public Brush StatusBrush => MissingFiles
        ? new SolidColorBrush(Color.FromRgb(241, 179, 111))
        : Installed.Enabled
            ? new SolidColorBrush(Color.FromRgb(111, 219, 171))
            : new SolidColorBrush(Color.FromRgb(172, 174, 187));

    public GitHubRelease? Release { get; }
    public GitHubAsset? Asset { get; }

    public InstanceModRow(InstalledMod installed, ModDefinition definition,
        bool missingFiles, GitHubRelease? release, GitHubAsset? asset)
    {
        Installed = installed;
        Definition = definition;
        MissingFiles = missingFiles;
        Release = release;
        Asset = asset;
        UpdateAvailable = !installed.Local && release != null &&
            !string.Equals(installed.Version, release.TagName,
                StringComparison.OrdinalIgnoreCase);

        uint hash = 2166136261;
        foreach (char c in installed.Id.ToUpperInvariant())
            hash = unchecked((hash ^ c) * 16777619);
        IconBrush = Backgrounds[(int)(hash % (uint)Backgrounds.Length)];
    }
}

public partial class MainWindow
{
    private readonly List<InstanceModRow> _allInstalledInstanceMods = new();
    private string _installedFilter = "all";

    private void ApplyInstalledFilters()
    {
        if (InstalledMods == null || InstalledSearchText == null ||
            EmptyInstalledText == null || NoMatchingInstalledText == null)
            return;

        var query = InstalledSearchText.Text.Trim();
        IEnumerable<InstanceModRow> rows = _allInstalledInstanceMods;

        if (query.Length > 0)
            rows = rows.Where(x =>
                x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Version.Contains(query, StringComparison.OrdinalIgnoreCase));
        rows = _installedFilter switch
        {
            "enabled" => rows.Where(x => x.Installed.Enabled && !x.MissingFiles),
            "disabled" => rows.Where(x => !x.Installed.Enabled && !x.MissingFiles),
            "updates" => rows.Where(x => x.CanUpdate),
            _ => rows
        };

        InstalledMods.Clear();
        foreach (var row in rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            InstalledMods.Add(row);

        int total = _allInstalledInstanceMods.Count;
        EmptyInstalledText.Visibility = total == 0
            ? Visibility.Visible : Visibility.Collapsed;
        NoMatchingInstalledText.Visibility = total > 0 && InstalledMods.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        InstalledCountLabel.Text = InstalledMods.Count == total
            ? (total == 1 ? "1 installed mod" : total + " installed mods")
            : InstalledMods.Count + " of " + total + " mods";
        InstalledSearchHint.Visibility =
            query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InstalledSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (InstalledSearchText == null || EmptyInstalledText == null) return;
        ApplyInstalledFilters();
    }

    private void InstalledFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string filter }) return;
        if (filter is not ("all" or "enabled" or "disabled" or "updates")) return;
        _installedFilter = filter;
        foreach (var button in new[]
            { FilterAllButton, FilterEnabledButton, FilterDisabledButton, FilterUpdatesButton })
        {
            button.Background = button.Tag as string == filter
                ? new SolidColorBrush(Color.FromRgb(59, 74, 76))
                : new SolidColorBrush(Color.FromRgb(48, 49, 57));
            button.BorderBrush = button.Tag as string == filter
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("StrokeBrush");
        }
        ApplyInstalledFilters();
    }

    private void InstalledClearFilters_Click(object sender, RoutedEventArgs e)
    {
        InstalledSearchText.Text = "";
        FilterAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, FilterAllButton));
    }

    private void RefreshSelectedInstanceOverview()
    {
        if (SelectedInstanceName == null) return;
        string? path = GameOrWarn(false);
        if (path == null)
        {
            SelectedInstanceName.Text = "Select an instance";
            SelectedInstanceMeta.Text = "Choose an instance from your library.";
            SelectedInstanceLoaderText.Text = "Not configured";
            SelectedInstancePathText.Text = "";
            SelectedInstanceWarning.Text = "";
            SelectedInstanceWarning.Visibility = Visibility.Collapsed;
            SelectedLoaderButton.Visibility = Visibility.Collapsed;
            return;
        }

        GameInstance? instance = _instanceStore.Load().FirstOrDefault(
            x => string.Equals(Path.GetFullPath(x.Path), path,
                StringComparison.OrdinalIgnoreCase));
        var artwork = instance == null ? null : InstanceArtwork.Cover(instance);
        SelectedInstanceArtwork.Source = artwork;
        SelectedInstanceArtwork.Visibility = artwork == null
            ? Visibility.Collapsed : Visibility.Visible;
        SelectedInstanceLogo.Visibility = artwork == null
            ? Visibility.Visible : Visibility.Collapsed;

        // Scale the saved cover framing down for the compact header icon.
        // Keep the original stored image intact so editing stays reversible.
        SelectedInstanceArtwork.Stretch = instance?.IconHasCrop != true &&
            instance?.IconFit == true
            ? Stretch.Uniform : Stretch.UniformToFill;
        SelectedInstanceArtwork.RenderTransformOrigin = new Point(0.5, 0.5);
        double zoom = instance?.IconHasCrop == true ? 1 :
            Math.Clamp(instance?.IconZoom ?? 1, 1, 2.5);
        var framing = new TransformGroup();
        framing.Children.Add(new ScaleTransform(zoom, zoom));
        framing.Children.Add(new TranslateTransform(
            (instance?.IconHasCrop == true ? 0 :
                Math.Clamp(instance?.IconOffsetX ?? 0, -80, 80)) * (53.0 / 164.0),
            (instance?.IconHasCrop == true ? 0 :
                Math.Clamp(instance?.IconOffsetY ?? 0, -80, 80)) * (53.0 / 137.0)));
        SelectedInstanceArtwork.RenderTransform = framing;
        SelectedInstanceName.Text = instance?.Name ?? Path.GetFileName(path);
        SelectedInstanceMeta.Text = (instance?.CreatedByManager == true
            ? "Modded copy" : "Existing installation") + "  ·  " +
            (_allInstalledInstanceMods.Count == 1 ? "1 managed mod" :
            _allInstalledInstanceMods.Count + " managed mods");
        SelectedInstancePathText.Text = path;
        SelectedInstancePathText.ToolTip = path;

        bool loader = _service.IsBepInExInstalled(path);
        SelectedInstanceLoaderText.Text = loader ? "BepInEx installed" : "BepInEx not installed";
        SelectedInstanceLoaderText.Foreground = loader
            ? (Brush)FindResource("AccentBrush")
            : new SolidColorBrush(Color.FromRgb(241, 179, 111));
        SelectedLoaderButton.Visibility = loader
            ? Visibility.Collapsed : Visibility.Visible;

        int missing = _allInstalledInstanceMods.Count(x => x.MissingFiles);
        string warning = !loader
            ? "Mod loader missing. Install BepInEx before adding client mods."
            : missing > 0
                ? (missing == 1 ? "1 managed mod folder is missing. " :
                    missing + " managed mod folders are missing. ") +
                  "Check its files before enabling it."
                : "";
        SelectedInstanceWarning.Text = warning;
        SelectedInstanceWarning.Visibility = warning.Length == 0
            ? Visibility.Collapsed : Visibility.Visible;
        SelectedModSummary.Text = missing == 0
            ? "Manage the plugins installed in this instance."
            : "Missing files are shown below; existing untracked files are never removed.";
    }

    private void SelectedOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string? game = GameOrWarn();
        if (game == null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = game,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open instance folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SelectedOpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        string? game = GameOrWarn();
        if (game == null) return;
        try
        {
            string plugins = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(plugins))
            {
                MessageBox.Show(this,
                    "The BepInEx plugins folder does not exist yet. Install BepInEx and launch OrbusVR once before adding mods.",
                    "Plugins folder unavailable", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = plugins, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open plugins folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SelectedCheckModUpdates_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(() => CheckReleasesAsync(false));
        RefreshSelectedInstanceOverview();
    }

    private async void InstalledUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: InstanceModRow row } || !row.CanUpdate)
            return;
        string? game = GameOrWarn();
        if (game == null || row.Release == null || row.Asset == null)
            return;

        if (_service.IsGameRunning())
        {
            MessageBox.Show(this, "Close OrbusVR before updating mods.",
                "Game is running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_service.IsBepInExInstalled(game))
        {
            MessageBox.Show(this, "Install BepInEx before updating mods.",
                "Loader missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(this,
            "Update " + row.Name + " from " + row.Installed.Version + " to " +
            row.Release.TagName + "?\n\nThis downloads an approved GitHub release and modifies only this instance.",
            "Update mod", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await RunAsync(async () =>
        {
            await _service.InstallReleaseAsync(
                game, row.Definition, row.Release, row.Asset,
                message => Dispatcher.Invoke(() => Log(message)));
            UpdateInstalledRows();
            UpdateReleaseStatuses();
            UpdateDashboard();
            RefreshInstanceCards();
            Log(row.Name + " updated to " + row.Release.TagName + ".");
        });
    }
}
