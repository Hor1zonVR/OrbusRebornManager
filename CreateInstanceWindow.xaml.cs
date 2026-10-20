using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;

namespace OrbusRebornManager;

public partial class CreateInstanceWindow : Window
{
    private readonly ManagerService _manager;
    private readonly InstanceStore _store = new();
    private CancellationTokenSource? _copyCancellation;
    private bool _busy;
    private bool _importModList;
    private bool _previewOnly;

    public GameInstance? CreatedInstance { get; private set; }
    public bool InstallLoader { get; private set; }
    public bool RememberLocation { get; private set; }
    public string SelectedStoragePath => StoragePathText.Text.Trim();

    public CreateInstanceWindow(
        ManagerService manager,
        string sourcePath,
        string storagePath,
        bool previewOnly = false,
        string? suggestedName = null,
        bool importModList = false)
    {
        InitializeComponent();
        _manager = manager;
        _importModList = importModList;
        _previewOnly = previewOnly;
        if (importModList)
        {
            EditionChoice.SelectedIndex = 2;
            EditionChoice.IsEnabled = false;
        }
        else
        {
            EditionChoice.SelectedIndex = 0;
        }
        UpdateEditionChoice();
        SourcePathText.Text = sourcePath;
        StoragePathText.Text = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornModdingInstances")
            : storagePath;

        UpdateLocationLabels();

        // Suggest an unused name instead of beginning with a collision.
        var names = _store.Load()
            .Select(i => i.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Importing can suggest the exported name, but it must still follow
        // the safe local directory naming rules used by CreateCleanCopyAsync.
        string baseName = string.IsNullOrWhiteSpace(suggestedName)
            ? (importModList
                ? (File.Exists(Path.Combine(sourcePath, "OrbusVR.exe")) ? "Modded Orbus Renewed" : "Modded Orbus")
                : "My Orbus Community")
            : System.Text.RegularExpressions.Regex.Replace(
                suggestedName.Trim(), @"[^a-zA-Z0-9 _-]", "").Trim();
        if (baseName.Length == 0) baseName = "Imported Orbus";
        if (baseName.Length > 42) baseName = baseName[..42].TrimEnd();

        string name = baseName;
        int index = 2;
        while (names.Contains(name) ||
               Directory.Exists(Path.Combine(StoragePathText.Text, name)))
            name = baseName + " " + index++;
        NameText.Text = name;

        if (importModList)
        {
            Title = "Create imported instance";
            LoaderCheck.IsChecked = true;
            LoaderCheck.IsEnabled = false;
            CreationStatusText.Text =
                "A new, clean game copy will be made for this imported mod list.";
            CreateButton.Content = "Create for import";
        }

        if (previewOnly)
        {
            CreateButton.IsEnabled = false;
            CreateButton.Content = "Preview only";
            CancelButton.Content = "Close preview";
            CreationStatusText.Text =
                "Preview only — no files will be copied or settings changed.";
        }

        Loaded += (_, _) =>
        {
            NameText.Focus();
            NameText.SelectAll();
        };
    }

    private string AvailableName(string name)
    {
        if (_store == null || StoragePathText == null) return name;
        var existing = _store.Load().Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string original = name;
        int number = 2;
        while (existing.Contains(name) ||
               Directory.Exists(Path.Combine(StoragePathText.Text, name)))
            name = original + " " + number++;
        return name;
    }

    private void EditionCard_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _importModList || _previewOnly || sender is not Button { Tag: string tag } ||
            !int.TryParse(tag, out int edition))
            return;
        EditionChoice.SelectedIndex = edition;
        UpdateEditionChoice();
    }

    private void UpdateEditionCardAppearance()
    {
        Button[] cards = { CommunityEditionCard, RenewedEditionCard, LocalEditionCard };
        TextBlock[] indicators = { CommunityEditionIndicator, RenewedEditionIndicator, LocalEditionIndicator };
        for (int i = 0; i < cards.Length; i++)
        {
            bool selected = EditionChoice.SelectedIndex == i;
            cards[i].Background = new SolidColorBrush(
                selected ? Color.FromRgb(39, 62, 58) : Color.FromRgb(37, 38, 45));
            cards[i].BorderBrush = new SolidColorBrush(
                selected ? Color.FromRgb(87, 217, 172) : Color.FromRgb(57, 59, 68));
            cards[i].BorderThickness = new Thickness(selected ? 2 : 1);
            indicators[i].Text = selected ? "✓" : "›";
            indicators[i].Foreground = new SolidColorBrush(selected ? Color.FromRgb(87, 217, 172) : Color.FromRgb(184, 194, 207));
            cards[i].IsEnabled = !_busy && !_previewOnly && !_importModList;
        }
    }

    private void EditionChoice_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (EditionInfo == null || ExistingSourcePanel == null) return;
        UpdateEditionChoice();
    }

    private void UpdateEditionChoice()
    {
        if (EditionInfo == null || ExistingSourcePanel == null || NameText == null) return;
        UpdateEditionCardAppearance();
        bool existing = EditionChoice.SelectedIndex == 2;
        ExistingSourcePanel.Visibility = existing ? Visibility.Visible : Visibility.Collapsed;
        ExistingSourceHeading.Visibility = existing ? Visibility.Visible : Visibility.Collapsed;
        if (existing)
        {
            EditionInfo.Text = "Copy a previously installed game. Your original folder is never modified.";
            LoaderInfo.Text = "Install BepInEx only in your newly created copy.";
        }
        else if (EditionChoice.SelectedIndex == 1)
        {
            EditionInfo.Text = "Downloads the latest PC release from FourOfSpades4/reborn-server. Mods depend on the installed release version.";
            LoaderInfo.Text = "BepInEx and RebornCam were tested on Renewed v0.12.0-alpha. Newer releases may need retesting.";
            LoaderCheck.IsChecked = true;
            if (!_importModList) NameText.Text = AvailableName("My Orbus Renewed");
        }
        else
        {
            EditionInfo.Text = "Downloads the latest official Community Edition PC release from GitHub.";
            LoaderInfo.Text = "Install BepInEx only in your newly created instance.";
            LoaderCheck.IsChecked = true;
            if (!_importModList) NameText.Text = AvailableName("My Orbus Community");
        }
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        EditionChoice.SelectedIndex = 2;
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the Reborn or Renewed installation to copy"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            SourcePathText.Text = _manager.NormalizeGamePath(dialog.FolderName);
            UpdateLocationLabels();
            CreationStatusText.Text = "";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Not a supported OrbusVR installation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseStorage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where new OrbusVR instances will be saved"
        };
        if (dialog.ShowDialog(this) == true)
        {
            StoragePathText.Text = dialog.FolderName;
            UpdateLocationLabels();
            CreationStatusText.Text = "";
        }
    }

    private void UpdateLocationLabels()
    {
        string source = SourcePathText.Text.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string storage = StoragePathText.Text.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        SourceLocationLabel.Text = string.IsNullOrWhiteSpace(source)
            ? "Choose your original game folder"
            : Path.GetFileName(source);
        SourceLocationLabel.ToolTip = source;

        string local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        StorageLocationLabel.Text = storage.StartsWith(
            local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "%LOCALAPPDATA%" + storage[local.Length..]
            : storage;
        StorageLocationLabel.ToolTip = storage;
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Chrome_Close_Click(object sender, RoutedEventArgs e)
    {
        Cancel_Click(sender, e);
    }

    private static bool IsWithin(string folder, string potentialChild)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(potentialChild));
        return string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(normalized + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            bool existing = EditionChoice.SelectedIndex == 2;
            string source = existing ? _manager.NormalizeGamePath(SourcePathText.Text) : "";
            string parent = Path.GetFullPath(StoragePathText.Text);
            string name = NameText.Text.Trim();

            if (name.Length == 0)
                throw new InvalidOperationException("Choose a name for your instance.");

            if (existing &&
                (IsWithin(source, parent) || IsWithin(source, Path.Combine(parent, name))))
                throw new InvalidOperationException(
                    "Instances cannot be stored inside the original OrbusVR game folder.");

            if (_manager.IsGameRunning())
                throw new InvalidOperationException("Close OrbusVR before creating a new instance.");

            _busy = true;
            CreateButton.IsEnabled = false;
            EditionChoice.IsEnabled = false;
            UpdateEditionCardAppearance();
            CopyProgress.Visibility = Visibility.Visible;
            CreationStatusText.Text = existing ? "Preparing a clean game copy..." :
                "Preparing to download the game from GitHub...";
            _copyCancellation = new CancellationTokenSource();
            Directory.CreateDirectory(parent);

            GameInstance copy;
            if (existing)
            {
                var progress = new Progress<(int Done, int Total)>(p =>
                {
                    CopyProgress.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                    CreationStatusText.Text = $"Copying game files: {p.Done:N0} of {p.Total:N0}";
                });
                copy = await _store.CreateCleanCopyAsync(
                    source, parent, name, progress, _copyCancellation.Token);
                copy.Edition = GameClientLayout.IsRenewed(source)
                    ? GameReleaseDownloadService.Renewed : GameReleaseDownloadService.Community;
            }
            else
            {
                string edition = EditionChoice.SelectedIndex == 1
                    ? GameReleaseDownloadService.Renewed
                    : GameReleaseDownloadService.Community;
                var progress = new Progress<GameReleaseDownloadService.Status>(p =>
                {
                    CopyProgress.Value = p.Percent;
                    CreationStatusText.Text = p.Message;
                });
                copy = await GameReleaseDownloadService.InstallLatestAsync(
                    edition, parent, name, progress, _copyCancellation.Token);
            }

            var items = _store.Load();
            items.Add(copy);
            _store.Save(items);

            CreatedInstance = copy;
            InstallLoader = LoaderCheck.IsChecked == true;
            RememberLocation = RememberLocationCheck.IsChecked == true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            CreationStatusText.Text = "Instance creation cancelled. No game files were installed.";
        }
        catch (Exception ex)
        {
            CreationStatusText.Text = "Couldn't create instance: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Could not create instance",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            _copyCancellation?.Dispose();
            _copyCancellation = null;
            CreateButton.IsEnabled = true;
            EditionChoice.IsEnabled = !_importModList && !_previewOnly;
            UpdateEditionCardAppearance();
            CancelButton.IsEnabled = true;
            CopyProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            CreationStatusText.Text = "Cancelling operation...";
            _copyCancellation?.Cancel();
            CancelButton.IsEnabled = false;
            return;
        }
        Close();
    }

    private void Window_Closing(object? sender,
        System.ComponentModel.CancelEventArgs e)
    {
        // Setting DialogResult after a successful copy closes the dialog.
        // Never treat that close as a user-requested cancellation.
        if (!_busy || CreatedInstance != null) return;
        e.Cancel = true;
        _copyCancellation?.Cancel();
        CreationStatusText.Text = "Cancelling operation...";
    }
}
