using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace OrbusRebornManager;

public sealed record CharacterProgressionRow(string Field, string Value);

public partial class MainWindow
{
    private readonly ServerCharacterInspector _characterInspector = new();
    private readonly ServerCharacterLevelEditor _classLevelEditor = new();
    private bool _levelEditBusy;
    public ObservableCollection<OrbusInventorySlot> VisibleInventorySlots { get; } = new();
    public ObservableCollection<CharacterProgressionRow> CharacterProgression { get; } = new();
    public ObservableCollection<ServerClassProgress> VisibleClassProgress { get; } = new();

    private readonly List<OrbusInventorySlot> _inventoryAll = new();
    private ServerItemEntry? _selectedCatalogItem;
    private int _itemPage;
    private int _filteredItemCount;
    private int _slotPage;
    private string _itemCategory = "All";
    private int _characterRequest;
    private bool _giftBusy;
    private const int ItemPageSize = 24;
    private const int InventoryPageSize = 40;


    private ServerClassProgress? _editingClassLevel;
    private string? _editingClassCharacter;
    private string? _editingClassServer;

    // Avoid creating a second WPF Window from a DataTemplate click. The previous
    // implementation could throw before its try/catch and terminate the app.
    private void EditClassLevel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_levelEditBusy || _chosenPlayer == null ||
                sender is not Button { Tag: ServerClassProgress selected })
                return;

            string? folder = ServerFolder();
            if (folder == null) return;

            _editingClassLevel = selected;
            _editingClassCharacter = _chosenPlayer.Name;
            _editingClassServer = folder;
            LevelEditHeadingText.Text = $"Edit {selected.DisplayName} · {_chosenPlayer.Name}";
            LevelEditValueText.Text = selected.Level.ToString(System.Globalization.CultureInfo.InvariantCulture);
            LevelEditPanel.Visibility = Visibility.Visible;
            LevelEditValueText.Focus();
            LevelEditValueText.SelectAll();
        }
        catch (Exception ex)
        {
            CloseClassLevelEditor();
            ShowClassLevelEditorError("Could not open the level editor.", ex);
        }
    }

    private void CloseClassLevelEditor()
    {
        _editingClassLevel = null;
        _editingClassCharacter = null;
        _editingClassServer = null;
        if (LevelEditPanel != null)
            LevelEditPanel.Visibility = Visibility.Collapsed;
    }

    private void CancelClassLevel_Click(object sender, RoutedEventArgs e)
    {
        if (!_levelEditBusy) CloseClassLevelEditor();
    }

    private async void SaveClassLevel_Click(object sender, RoutedEventArgs e)
    {
        if (_levelEditBusy || _editingClassLevel is not { } selected ||
            _editingClassCharacter is not { } name || _editingClassServer is not { } folder)
            return;

        try
        {
            if (_chosenPlayer == null ||
                !string.Equals(_chosenPlayer.Name, name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_selectedServerPath, folder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected character or server has changed. Select the class again.");

            if (!int.TryParse(LevelEditValueText.Text.Trim(), out int next) || next is < 1 or > 100)
            {
                MessageBox.Show(this, "Enter a whole number from 1 to 100.",
                    "Invalid level", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (next == selected.Level)
            {
                CloseClassLevelEditor();
                return;
            }
            if (MessageBox.Show(this,
                    $"Change {name}'s {selected.DisplayName} level from {selected.Level} to {next}?\n\n" +
                    "The character MUST be logged out. This server does not provide verified online presence. " +
                    "A local backup of saved progression will be written before editing. " +
                    "XP and other class levels will not be changed.\n\nContinue?",
                    "Confirm class level edit", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _levelEditBusy = true;
            LevelEditSaveButton.IsEnabled = false;

            var snapshot = await _characterInspector.ReadAsync(folder, name);
            if (!string.Equals(folder, _selectedServerPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(name, _chosenPlayer?.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected character changed while reading its saved data.");

            if (!snapshot.Progression.TryGetValue("skill_level", out string? raw) ||
                string.IsNullOrWhiteSpace(raw))
                throw new InvalidOperationException("Saved class levels could not be read for backup.");

            using (var levels = JsonDocument.Parse(raw))
            {
                if (levels.RootElement.ValueKind != JsonValueKind.Object ||
                    !levels.RootElement.TryGetProperty(selected.Key, out var oldLevel) ||
                    !oldLevel.TryGetInt32(out int current) || current != selected.Level)
                    throw new InvalidOperationException("Saved class level has changed. Refresh the profile first.");
            }

            string backupFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornManager", "progression-backups");
            Directory.CreateDirectory(backupFolder);
            string safeName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(folder + "|" + name)))[..20];
            string backupFile = Path.Combine(backupFolder,
                safeName + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
            var backup = new
            {
                timestampUtc = DateTime.UtcNow,
                characterName = name,
                classKey = selected.Key,
                previousLevel = selected.Level,
                requestedLevel = next,
                skill_level = JsonSerializer.Deserialize<JsonElement>(raw),
                skill_xp = snapshot.Progression.TryGetValue("skill_xp", out string? xp)
                    ? xp : null
            };
            await File.WriteAllTextAsync(backupFile, JsonSerializer.Serialize(backup,
                new JsonSerializerOptions { WriteIndented = true }));

            await _classLevelEditor.EditAsync(folder, name, selected.Key, selected.Level, next);
            Log($"Verified saved level edit: {name} · {selected.DisplayName} → {next}");
            CloseClassLevelEditor();
            await LoadCharacterSnapshotAsync();
            MessageBox.Show(this,
                "Saved level updated and verified.\n\nBackup: " + backupFile +
                "\n\nPlease log back in and confirm the level appears in-game.",
                "Level updated", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowClassLevelEditorError("The class level was not confirmed as updated.", ex);
        }
        finally
        {
            _levelEditBusy = false;
            if (LevelEditSaveButton != null) LevelEditSaveButton.IsEnabled = true;
        }
    }

    private void ShowClassLevelEditorError(string summary, Exception ex)
    {
        string? logPath = null;
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornManager", "logs");
            Directory.CreateDirectory(folder);
            logPath = Path.Combine(folder, "class-level-editor.log");
            File.AppendAllText(logPath, $"[{DateTimeOffset.UtcNow:O}] {summary}\n{ex}\n\n");
        }
        catch (Exception) { /* Logging must never take down the manager. */ }

        Log("Class level editor error: " + ex.Message);
        MessageBox.Show(this, summary + "\n\n" + ex.Message +
            (logPath == null ? "" : "\n\nDiagnostic log: " + logPath),
            "Class level editor", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void ChooseItemIcons_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose your local exported Orbus item icons (subfolders supported)"
        };
        if (dialog.ShowDialog(this) != true) return;
        _settings.ItemIconDirectory = dialog.FolderName;
        LocalItemArtwork.DirectoryPath = dialog.FolderName;
        _service.SaveSettings(_settings);
        await RefreshLocalIconIndexAsync();
    }

    private async Task RefreshLocalIconIndexAsync()
    {
        if (ItemIconFolderText == null) return;
        string folder = LocalItemArtwork.DirectoryPath;
        ItemIconFolderText.Text = "Searching exported icon PNGs, including subfolders…";
        try
        {
            await Task.Run(LocalItemArtwork.RefreshIndex);
            if (!folder.Equals(LocalItemArtwork.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                return;
            UpdateLocalIconStatus();
            // Rebind the displayed cards after indexing (they are immutable records).
            RefreshItemGrid();
            RefreshInventoryPage();
        }
        catch (Exception ex)
        {
            ItemIconFolderText.Text = "Icon search failed: " + ex.Message;
        }
    }

    private void UpdateLocalIconStatus()
    {
        if (ItemIconFolderText == null) return;
        int iconNames = _allPlayerItems.Select(i => i.IconName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int matched = _allPlayerItems
            .Where(i => !string.IsNullOrWhiteSpace(i.IconName))
            .Select(i => i.IconName).Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(n => LocalItemArtwork.FindIcon(n) != null);
        string folder = LocalItemArtwork.DirectoryPath;
        ItemIconFolderText.Text = LocalItemArtwork.IndexedFileCount == 0
            ? "No exported PNGs found. Export item Sprites/Texture2D from your own game " +
              "using AssetStudio, then select the export folder here (nested folders work)."
            : $"Matched {matched:N0}/{iconNames:N0} catalogue icons " +
              $"({LocalItemArtwork.IndexedFileCount:N0} PNGs found). Source: {folder}";
    }

    private void ResetInventoryViewer()
    {
        ++_characterRequest;
        _inventoryAll.Clear();
        VisibleInventorySlots.Clear();
        CharacterProgression.Clear();
        VisibleClassProgress.Clear();
        _slotPage = 0;
        _itemPage = 0;
        _selectedCatalogItem = null;
        _itemCategory = "All";
        CloseClassLevelEditor();
        if (CharacterInventoryStatusText == null) return;
        CharacterInventoryStatusText.Text = "Select a character to inspect their saved inventory.";
        SelectedInventoryNameText.Text = "Select a saved item slot";
        SelectedInventoryStatText.Text = "Actual saved stats and durability appear here.";
        SelectedInventoryDurabilityBar.Visibility = Visibility.Collapsed;
        SelectedInventoryDurabilityText.Visibility = Visibility.Collapsed;
        InventoryPageText.Text = "No inventory loaded";
        SelectedCatalogNameText.Text = "Select an item above";
        SelectedCatalogDetailsText.Text = "Rarity, class requirements and base properties appear here.";
        UpdateGrantEnabled();
    }

    private async Task LoadCharacterSnapshotAsync()
    {
        if (_chosenPlayer == null) return;
        string? folder = ServerFolder(false);
        if (folder == null) return;
        int request = ++_characterRequest;
        string name = _chosenPlayer.Name;
        CharacterInventoryStatusText.Text = "Reading saved character inventory and progression…";
        VisibleInventorySlots.Clear();
        CharacterProgression.Clear();
        VisibleClassProgress.Clear();
        try
        {
            var result = await _characterInspector.ReadAsync(folder, name);
            if (request != _characterRequest ||
                !string.Equals(folder, _selectedServerPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(name, _chosenPlayer?.Name, StringComparison.OrdinalIgnoreCase))
                return;
            _inventoryAll.Clear();
            var lookup = _allPlayerItems.ToDictionary(i => i.Id);
            for (int i = 0; i < result.SerializedSlots.Count; i++)
                _inventoryAll.Add(OrbusInventorySlot.Parse(i, result.SerializedSlots[i], lookup));
            _slotPage = 0;
            RefreshInventoryPage();
            foreach (var item in ServerClassProgress.Read(result.Progression))
                VisibleClassProgress.Add(item);
            foreach (var field in result.Progression.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                if (!field.Key.Equals("skill_level", StringComparison.OrdinalIgnoreCase) &&
                    !field.Key.Equals("skill_xp", StringComparison.OrdinalIgnoreCase))
                    CharacterProgression.Add(new CharacterProgressionRow(field.Key, field.Value));
            CharacterInventoryStatusText.Text =
                $"{_inventoryAll.Count} saved slots · {VisibleClassProgress.Count} classes · " +
                $"{CharacterProgression.Count} other progression fields. " + result.Diagnostic;
        }
        catch (Exception e)
        {
            if (request != _characterRequest ||
                !string.Equals(name, _chosenPlayer?.Name, StringComparison.OrdinalIgnoreCase))
                return;
            CharacterInventoryStatusText.Text = "Read-only inventory inspection unavailable: " + e.Message;
            InventoryPageText.Text = "No inventory available";
        }
    }

    private async void RefreshCharacterSnapshot_Click(object sender, RoutedEventArgs e) =>
        await LoadCharacterSnapshotAsync();

    private void RefreshInventoryPage()
    {
        if (InventoryPageText == null) return;
        int pages = Math.Max(1, (int)Math.Ceiling(_inventoryAll.Count / (double)InventoryPageSize));
        _slotPage = Math.Clamp(_slotPage, 0, pages - 1);
        VisibleInventorySlots.Clear();
        foreach (var slot in _inventoryAll.Skip(_slotPage * InventoryPageSize).Take(InventoryPageSize))
            VisibleInventorySlots.Add(slot);
        InventoryPageText.Text = _inventoryAll.Count == 0 ? "No inventory loaded" :
            $"Slots · Page {_slotPage + 1}/{pages}";
    }

    private void InventoryPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_slotPage > 0) { _slotPage--; RefreshInventoryPage(); }
    }

    private void InventoryNext_Click(object sender, RoutedEventArgs e)
    {
        if ((_slotPage + 1) * InventoryPageSize < _inventoryAll.Count)
        { _slotPage++; RefreshInventoryPage(); }
    }

    private void InspectInventorySlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OrbusInventorySlot slot }) return;
        SelectedInventoryNameText.Text = slot.Name + $" · Slot {slot.Slot + 1}";
        SelectedInventoryStatText.Text = slot.ItemStats +
            "\n\nThese are the exact recognised values stored on the item instance. " +
            "Missing stats are not invented.";
        bool hasDurability = slot.TryGetDurability(out double current, out double max);
        SelectedInventoryDurabilityBar.Visibility =
            hasDurability ? Visibility.Visible : Visibility.Collapsed;
        SelectedInventoryDurabilityText.Visibility =
            hasDurability ? Visibility.Visible : Visibility.Collapsed;
        if (hasDurability)
        {
            SelectedInventoryDurabilityBar.Value = current / max * 100;
            SelectedInventoryDurabilityText.Text = $"Durability: {current:0}/{max:0}";
        }
    }

    private void RefreshItemGrid()
    {
        if (PlayerItemSearchText == null || PlayerItemCountText == null) return;
        string query = PlayerItemSearchText.Text.Trim();
        IEnumerable<ServerItemEntry> items = _allPlayerItems.Where(i =>
            (_itemCategory == "All" || (_itemCategory == "Other"
                ? i.Category == "Materials & Other" || i.Category == "Tools" ||
                  i.Category == "Pets & Mounts" || i.Category == "Weapons" || i.Category == "Muskets"
                : i.Category.Equals(_itemCategory, StringComparison.OrdinalIgnoreCase))) &&
            (string.IsNullOrWhiteSpace(query) || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)));
        var filtered = items.ToList();
        _filteredItemCount = filtered.Count;
        int pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)ItemPageSize));
        _itemPage = Math.Clamp(_itemPage, 0, pages - 1);
        VisiblePlayerItems.Clear();
        foreach (var item in filtered.Skip(_itemPage * ItemPageSize).Take(ItemPageSize))
            VisiblePlayerItems.Add(item);
        PlayerItemCountText.Text = filtered.Count == 0
            ? "No matching items" : $"{filtered.Count:N0} items · Page {_itemPage + 1}/{pages}";
        UpdateGrantEnabled();
    }

    private void PlayerItemCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        _itemCategory = category;
        _itemPage = 0;
        foreach (var categoryButton in ItemCategoryBar.Children.OfType<Button>())
            categoryButton.Background = string.Equals(categoryButton.Tag?.ToString(), category,
                StringComparison.OrdinalIgnoreCase)
                ? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(47, 72, 69))
                : System.Windows.Media.Brushes.Transparent;
        RefreshItemGrid();
    }

    private void PlayerItemPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_itemPage > 0) { _itemPage--; RefreshItemGrid(); }
    }

    private void PlayerItemNext_Click(object sender, RoutedEventArgs e)
    {
        if ((_itemPage + 1) * ItemPageSize < _filteredItemCount)
        {
            _itemPage++;
            RefreshItemGrid();
        }
    }

    private void SelectCatalogItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServerItemEntry item }) return;
        _selectedCatalogItem = item;
        SelectedCatalogNameText.Text = $"{item.Name} · #{item.Id}";
        SelectedCatalogDetailsText.Text =
            $"Category: {item.Category}     Rarity: {item.Rarity}\n" +
            $"Slot: {item.Slot}     Minimum level: {item.MinimumLevel}\n" +
            (string.IsNullOrWhiteSpace(item.RequiredClass) ? "" : $"Class: {item.RequiredClass}\n") +
            (string.IsNullOrWhiteSpace(item.Description) ? "" : item.Description + "\n") +
            (string.IsNullOrWhiteSpace(item.BaseProperties) ? "" : $"Catalogue base properties: {item.BaseProperties}\n") +
            "A base-item grant does not guarantee generated level, durability, affixes or upgrade rank.";
        UpdateGrantEnabled();
    }
}
