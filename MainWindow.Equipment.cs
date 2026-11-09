using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private readonly ServerEquipmentInstanceWriter _equipmentWriter = new();
    private bool _equipmentGrantBusy;

    // Explicit opt-in only. Never auto-issue an equipment grant on selection/open.
    // The in-game result still requires a one-item login/reconnect test.
    private const bool ExperimentalEquipmentGrantsEnabled = true;

    private static bool CanGenerateEquipment(ServerItemEntry? item) =>
        ExperimentalEquipmentGrantsEnabled && item != null && (item.Type.Equals("classweapon", StringComparison.OrdinalIgnoreCase) ||
                         item.Type.Equals("equipment", StringComparison.OrdinalIgnoreCase) ||
                         item.Type.Equals("equpiment", StringComparison.OrdinalIgnoreCase));

    private void RefreshEquipmentControls()
    {
        if (PlayerPrepareGearButton == null) return;
        bool allowed = CanGenerateEquipment(_selectedCatalogItem) &&
                       _chosenPlayer != null && !_equipmentGrantBusy && !_giftBusy && !_playerBusy;
        PlayerPrepareGearButton.IsEnabled = allowed;
        GiveExperimentalGearButton.IsEnabled = allowed && GearSafetyOptInCheck.IsChecked == true;
        GearPreviewButton.IsEnabled = allowed;
        UndoEquipmentTestButton.IsEnabled = _chosenPlayer != null &&
            !_equipmentGrantBusy && !_giftBusy && !_playerBusy;
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

    private void GearOptIn_Changed(object sender, RoutedEventArgs e)
    {
        if (GiveExperimentalGearButton != null) RefreshEquipmentControls();
    }

    private string BuildGearMetadata()
    {
        if (!int.TryParse(GearItemLevelText.Text.Trim(), out int itemLevel) ||
            !int.TryParse(GearPlusLevelText.Text.Trim(), out int plus))
            throw new InvalidOperationException("Enter whole numbers for equipment level and + rank.");
        string rarity = (GearRarityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "uncommon";
        string[] affixes = GearAffixesText.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ServerEquipmentInstanceWriter.MakeMetadata(itemLevel, plus, rarity, affixes,
            OptionalExperimentalStat(GearDurabilityText.Text, "Maximum durability"),
            OptionalExperimentalStat(GearArmorText.Text, "Armour"),
            OptionalExperimentalStat(GearAttackText.Text, "Attack"));
    }

    private void PreviewGear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_selectedCatalogItem is not { } item || !CanGenerateEquipment(item))
                throw new InvalidOperationException("Select a class weapon or armour item first.");
            string metadata = BuildGearMetadata();
            GearPreviewText.Text = item.Id + "@1@" + metadata +
                "\n\nPreview only. No server/database write has occurred.";
        }
        catch (Exception ex)
        {
            GearPreviewText.Text = "Preview failed: " + ex.Message;
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
        if (_equipmentGrantBusy || _giftBusy || GearSafetyOptInCheck.IsChecked != true ||
            _chosenPlayer is not { } character ||
            _selectedCatalogItem is not { } item || !CanGenerateEquipment(item))
            return;
        string? folder = ServerFolder();
        if (folder == null) return;
        string name = character.Name;
        string? backupPath = null;
        bool adminGrantIssued = false;
        try
        {
            if (character.Online == true)
                throw new InvalidOperationException("The selected character is online. Fully log out before granting equipment.");
            string metadata = BuildGearMetadata();
            int itemLevel = int.Parse(GearItemLevelText.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            int plus = int.Parse(GearPlusLevelText.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            string rarity = (GearRarityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "uncommon";

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
            if (before.SerializedSlots.Any(ServerEquipmentInstanceWriter.IsMalformedOuterEntry))
                throw new InvalidOperationException("Saved inventory contains a malformed '%' top-level entry. Repair that first.");
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
            if (after.SerializedSlots.Count >= 250 || after.SerializedSlots.Count != before.SerializedSlots.Count)
                throw new InvalidOperationException("Inventory slot count differs from the pre-grant backup. Refusing to patch.");

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
                    if (ServerEquipmentInstanceWriter.IsPlainBaseGrant(item.Id, newRaw))
                        changedSlot = i;
                }
            }
            if (changedCount != 1 || changedSlot < 0 || changedSlot >= after.SerializedSlots.Count)
                throw new InvalidOperationException("The admin command did not create exactly one identifiable new equipment slot. " +
                    "No metadata was applied; inspect the saved inventory before retrying.");

            // Write the exact undo journal BEFORE metadata mutation. A disk
            // failure must never leave a generated item without an undo record.
            // The journal is harmless if the later CAS fails; Undo checks exact bytes.
            string testDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornManager", "equipment-tests");
            Directory.CreateDirectory(testDir);
            string journalFile = Path.Combine(testDir, safeName + "-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
            string finalRaw = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "@1@" + metadata;
            await File.WriteAllTextAsync(journalFile, JsonSerializer.Serialize(new
            {
                operation = "guarded-equipment-test-v2",
                timestampUtc = DateTime.UtcNow,
                serverFolder = folder, characterName = name,
                itemId = item.Id, itemName = item.Name, slot = changedSlot,
                originalEmptySlot = before.SerializedSlots[changedSlot],
                expectedGeneratedItem = finalRaw, preGrantBackupFile = backupPath
            }, new JsonSerializerOptions { WriteIndented = true }));

            GearGiftStatusText.Text = "Applying a single saved-item metadata change…";
            int verifiedSlot = await _equipmentWriter.PatchGrantedSlotAsync(
                folder, name, item.Id, changedSlot, after.SerializedSlots[changedSlot],
                metadata, after.SerializedSlots);

            GearGiftStatusText.Text = $"Saved equipment metadata verified at slot {verifiedSlot}. " +
                $"Rollback journal: {journalFile}\nCheck the game, LOG OUT, then test Undo if needed.";
            Log($"Guarded equipment test: {item.Name} lvl {itemLevel} +{plus}, slot {verifiedSlot}.");
            await LoadCharacterSnapshotAsync();
            MessageBox.Show(this,
                "One experimental equipment item was saved in slot " + verifiedSlot +
                ".\n\nRollback journal: " + journalFile +
                "\n\nLog in and confirm it loads. Do not test further variants until it survives a logout/rejoin.",
                "Equipment test saved", MessageBoxButton.OK, MessageBoxImage.Information);
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
            GearSafetyOptInCheck.IsChecked = false;
            UpdateGrantEnabled();
        }
    }

    private async void UndoEquipmentTest_Click(object sender, RoutedEventArgs e)
    {
        if (_equipmentGrantBusy || _giftBusy || _chosenPlayer is not { } character) return;
        string? folder = ServerFolder();
        if (folder == null) return;
        try
        {
            string testDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornManager", "equipment-tests");
            var picker = new OpenFileDialog
            {
                Title = "Select the exact equipment test rollback journal",
                Filter = "Equipment test (*.json)|*.json",
                InitialDirectory = testDir
            };
            if (picker.ShowDialog(this) != true) return;
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(picker.FileName));
            var record = json.RootElement;
            if (record.GetProperty("operation").GetString() != "guarded-equipment-test-v2" ||
                record.GetProperty("characterName").GetString() != character.Name ||
                !string.Equals(System.IO.Path.GetFullPath(record.GetProperty("serverFolder").GetString() ?? ""),
                    System.IO.Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The journal is for another character or server.");
            int slot = record.GetProperty("slot").GetInt32();
            int itemId = record.GetProperty("itemId").GetInt32();
            string expected = record.GetProperty("expectedGeneratedItem").GetString() ?? "";
            string original = record.GetProperty("originalEmptySlot").GetString() ?? "";
            if (slot is < 0 or >= 250 || (original != "" && original != "0") ||
                !expected.StartsWith(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "@1@",
                    StringComparison.Ordinal))
                throw new InvalidOperationException("That journal doesn't describe a safe, new single-item grant.");
            if (character.Online == true)
                throw new InvalidOperationException("The character appears online. Log out before undoing a test.");
            var current = await _characterInspector.ReadAsync(folder, character.Name);
            if (!string.Equals(_selectedServerPath, folder, StringComparison.OrdinalIgnoreCase) ||
                _chosenPlayer?.Name != character.Name)
                throw new InvalidOperationException("Character selection changed while checking the journal.");
            if (slot >= current.SerializedSlots.Count || current.SerializedSlots[slot] != expected)
                throw new InvalidOperationException("This item has moved, been equipped, or changed since the test. " +
                    "Undo refused to protect your current inventory.");

            if (MessageBox.Show(this,
                    $"Undo only the test item in saved slot {slot+1}?\n\n" +
                    $"Item ID: {itemId}\nOld slot contents: {(original == "" ? "(empty)" : original)}\n\n" +
                    "Every other item and class level stays untouched. Character MUST be logged out.",
                    "Confirm equipment-test undo",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _equipmentGrantBusy = true;
            UpdateGrantEnabled();
            string backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornManager", "inventory-backups");
            Directory.CreateDirectory(backupDir);
            string recoverySnapshot = Path.Combine(backupDir,
                "before-equipment-test-undo-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
            await File.WriteAllTextAsync(recoverySnapshot, JsonSerializer.Serialize(new
            {
                operation = "pre-equipment-test-undo",
                timestampUtc = DateTime.UtcNow,
                characterName = character.Name, serverFolder = folder,
                sourceJournal = System.IO.Path.GetFileName(picker.FileName),
                originalInventory = current.SerializedSlots
            }, new JsonSerializerOptions { WriteIndented = true }));
            await _giftRollback.RevertOneAsync(folder, character.Name, slot, itemId,
                original, expected, current.SerializedSlots);
            await LoadCharacterSnapshotAsync();
            GearGiftStatusText.Text = $"Verified one-slot equipment test undo at slot {slot+1}. " +
                $"Current inventory was backed up to {recoverySnapshot}";
            MessageBox.Show(this, "Experimental item removed from its original empty slot. " +
                "All other saved slots were preserved. Restart OrbusVR before inspecting inventory.",
                "Equipment test undone", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("Equipment test undo refused: " + ex.Message);
            MessageBox.Show(this, "No equipment-test undo was confirmed.\n\n" + ex.Message,
                "Equipment test undo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _equipmentGrantBusy = false;
            UpdateGrantEnabled();
        }
    }
}
