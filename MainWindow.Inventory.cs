using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace OrbusRebornManager;

public sealed record CharacterProgressionRow(string Field, string Value);

public partial class MainWindow
{
    private readonly ServerCharacterInspector _characterInspector = new();
    public ObservableCollection<OrbusInventorySlot> VisibleInventorySlots { get; } = new();
    public ObservableCollection<CharacterProgressionRow> CharacterProgression { get; } = new();

    private readonly List<OrbusInventorySlot> _inventoryAll = new();
    private ServerItemEntry? _selectedCatalogItem;
    private int _itemPage;
    private int _filteredItemCount;
    private int _slotPage;
    private string _itemCategory = "All";
    private int _characterRequest;
    private bool _giftBusy;
    private const int ItemPageSize = 36;
    private const int InventoryPageSize = 40;

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
        _slotPage = 0;
        _itemPage = 0;
        _selectedCatalogItem = null;
        _itemCategory = "All";
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
            foreach (var field in result.Progression.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                CharacterProgression.Add(new CharacterProgressionRow(field.Key, field.Value));
            CharacterInventoryStatusText.Text =
                $"{_inventoryAll.Count} saved slots · {CharacterProgression.Count} progression fields. " +
                result.Diagnostic;
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
