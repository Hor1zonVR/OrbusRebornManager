using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private readonly ServerEquipmentInstanceWriter _equipmentWriter = new();
    private bool _equipmentGrantBusy;

    private static bool CanGenerateEquipment(ServerItemEntry? item) =>
        item != null && (item.Type.Equals("classweapon", StringComparison.OrdinalIgnoreCase) ||
                         item.Type.Equals("equipment", StringComparison.OrdinalIgnoreCase) ||
                         item.Type.Equals("equpiment", StringComparison.OrdinalIgnoreCase));

    private void RefreshEquipmentControls()
    {
        if (PlayerPrepareGearButton == null) return;
        bool allowed = CanGenerateEquipment(_selectedCatalogItem) &&
                       _chosenPlayer != null && !_equipmentGrantBusy && !_giftBusy && !_playerBusy;
        PlayerPrepareGearButton.IsEnabled = allowed;
        GiveExperimentalGearButton.IsEnabled = allowed;
        if (!CanGenerateEquipment(_selectedCatalogItem) || _chosenPlayer == null)
            GearEditorPanel.Visibility = Visibility.Collapsed;
    }

    private void ToggleGearEditor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!CanGenerateEquipment(_selectedCatalogItem) || _chosenPlayer == null) return;
            GearEditorPanel.Visibility = GearEditorPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            GearGiftStatusText.Text = "Could not open equipment editor: " + ex.Message;
        }
    }

    private static int? OptionalExperimentalStat(string raw, string label)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw.Trim(), out int n) || n < 0 || n > 1000000)
            throw new InvalidOperationException(label + " must be a whole number from 0 to 1,000,000, or left blank.");
        return n;
    }

    private async void GiveExperimentalGear_Click(object sender, RoutedEventArgs e)
    {
        if (_equipmentGrantBusy || _giftBusy || _chosenPlayer is not { } character ||
            _selectedCatalogItem is not { } item || !CanGenerateEquipment(item))
            return;
        string? folder = ServerFolder();
        if (folder == null) return;
        string name = character.Name;
        string? backupPath = null;
        bool adminGrantIssued = false;
        try
        {
            if (!int.TryParse(GearItemLevelText.Text.Trim(), out int itemLevel) ||
                !int.TryParse(GearPlusLevelText.Text.Trim(), out int plus))
                throw new InvalidOperationException("Enter whole numbers for item level and enhancement rank.");
            string rarity = (GearRarityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "uncommon";
            var affixes = GearAffixesText.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string metadata = ServerEquipmentInstanceWriter.MakeMetadata(itemLevel, plus, rarity, affixes,
                OptionalExperimentalStat(GearDurabilityText.Text, "Maximum durability"),
                OptionalExperimentalStat(GearArmorText.Text, "Armour"),
                OptionalExperimentalStat(GearAttackText.Text, "Attack"));

            if (MessageBox.Show(this,
                    $"Experimental grant to {name}: {item.Name} (#{item.Id})\n\n" +
                    $"Item level: {itemLevel}    Plus: +{plus}    Rarity: {rarity}\n" +
                    $"Metadata: {metadata}\n\n" +
                    "This is for a PRIVATE server and may be ignored or rejected by game systems. " +
                    "The character MUST be fully logged out; active sessions overwrite saved inventory. " +
                    "RM will back up the entire saved inventory, use the standard admin grant, " +
                    "then patch only the newly granted slot.\n\nProceed?",
                    "Confirm experimental equipment", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            _equipmentGrantBusy = true;
            UpdateGrantEnabled();
            GearGiftStatusText.Text = "Backing up saved inventory before the admin grant…";

            var before = await _characterInspector.ReadAsync(folder, name);
            if (before.SerializedSlots.Count == 0 || before.SerializedSlots.Count >= 250)
                throw new InvalidOperationException("Could not safely snapshot the complete saved inventory.");
            if (_selectedCatalogItem?.Id != item.Id ||
                !string.Equals(_chosenPlayer?.Name, name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_selectedServerPath, folder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Character, server or selected item changed. Restart the grant.");

            string backupDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornManager", "inventory-backups");
            Directory.CreateDirectory(backupDirectory);
            string safeName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(folder + "|" + name)))[..20];
            backupPath = Path.Combine(backupDirectory, safeName + "-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
            await File.WriteAllTextAsync(backupPath, JsonSerializer.Serialize(new
            {
                timestampUtc = DateTime.UtcNow,
                serverFolder = folder,
                characterName = name,
                operation = "pre-experimental-equipment-grant",
                requestedItemId = item.Id,
                requestedMetadata = JsonSerializer.Deserialize<JsonElement>(metadata),
                originalInventory = before.SerializedSlots
            }, new JsonSerializerOptions { WriteIndented = true }));

            GearGiftStatusText.Text = "Sending standard admin grant; then locating the changed saved slot…";
            adminGrantIssued = true;
            await _serverPlayerService.GiveItemAsync(folder, name, item.Id);
            var after = await _characterInspector.ReadAsync(folder, name);
            if (!string.Equals(_selectedServerPath, folder, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_chosenPlayer?.Name, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Selected server or character changed during the gift.");
            if (after.SerializedSlots.Count >= 250)
                throw new InvalidOperationException("Inventory snapshot may be truncated; refusing to patch.");

            int changedSlot = -1;
            int changedCount = 0;
            for (int i = 0; i < Math.Max(before.SerializedSlots.Count, after.SerializedSlots.Count); i++)
            {
                string oldRaw = i < before.SerializedSlots.Count ? before.SerializedSlots[i] : "";
                string newRaw = i < after.SerializedSlots.Count ? after.SerializedSlots[i] : "";
                if (string.Equals(oldRaw, newRaw, StringComparison.Ordinal)) continue;
                changedCount++;
                // Only accept a newly inserted unmodified base item in a previously
                // empty slot. Never rewrite something already equipped or stacked.
                if (string.IsNullOrEmpty(oldRaw) || oldRaw == "0")
                {
                    string[] parts = newRaw.Split('%',3);
                    if (int.TryParse(parts[0], out int id) && id == item.Id &&
                        (parts.Length == 1 ||
                         (parts.Length >= 2 && parts[1] == "1" &&
                          (parts.Length == 2 || string.IsNullOrWhiteSpace(parts[2])))))
                        changedSlot = i;
                }
            }
            if (changedCount != 1 || changedSlot < 0 || changedSlot >= after.SerializedSlots.Count)
                throw new InvalidOperationException("The admin command did not create exactly one identifiable new equipment slot. " +
                    "No metadata was applied; inspect the saved inventory before retrying.");

            GearGiftStatusText.Text = "Attaching level/rarity/affix metadata to the newly granted saved slot…";
            int verifiedSlot = await _equipmentWriter.PatchGrantedSlotAsync(
                folder, name, item.Id, changedSlot, after.SerializedSlots[changedSlot],
                metadata, after.SerializedSlots);

            GearGiftStatusText.Text = $"Server verified new equipment metadata in inventory slot {verifiedSlot}. " +
                "Log in and inspect the item; weapon attack scaling is still experimental.";
            Log($"Experimental equipment metadata attached: {item.Name} lvl {itemLevel} +{plus}, slot {verifiedSlot}.");
            await LoadCharacterSnapshotAsync();
        }
        catch (Exception ex)
        {
            GearGiftStatusText.Text = "Equipment experiment failed: " + ex.Message +
                (adminGrantIssued ? "\nThe base admin grant may already have inserted an unmodified item." : "") +
                (backupPath == null ? "" : "\nInventory backup: " + backupPath);
            Log("Experimental equipment grant failed: " + ex.Message);
            MessageBox.Show(this, GearGiftStatusText.Text, "Experimental equipment", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _equipmentGrantBusy = false;
            UpdateGrantEnabled();
        }
    }
}
