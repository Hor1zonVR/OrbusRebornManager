using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace OrbusRebornManager;

// Catalogue properties describe base items. Rolled equipment properties live
// on individual inventory slots and MUST NOT be inferred from the catalogue.
public static class LocalItemArtwork
{
    public static string DirectoryPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrbusRebornManager", "ItemIcons");
}

public sealed record ServerItemEntry(
    int Id, string Name, string IconName = "", string Description = "",
    string Rarity = "", string Type = "", string Slot = "",
    string RequiredClass = "", int MinimumLevel = 1, string BaseProperties = "")
{
    public string Label => $"#{Id} · {Name}";
    public string Category
    {
        get
        {
            string n = Name.ToLowerInvariant(), t = Type.ToLowerInvariant();
            if (n.Contains("dye") || t.Contains("dye")) return "Dyes";
            if (n.Contains("potion") || n.Contains("elixir") || t.Contains("consumable")) return "Potions";
            if (n.Contains("mallet") || RequiredClass.Equals("bard", StringComparison.OrdinalIgnoreCase)) return "Mallets";
            if (n.Contains("hammer") || RequiredClass.Equals("paladin", StringComparison.OrdinalIgnoreCase)) return "Hammers";
            if (n.Contains("bow") || RequiredClass.Equals("archer", StringComparison.OrdinalIgnoreCase)) return "Bows";
            if (n.Contains("wand") || RequiredClass.Equals("runemage", StringComparison.OrdinalIgnoreCase)) return "Wands";
            if (n.Contains("musket") || RequiredClass.Equals("orbhealer", StringComparison.OrdinalIgnoreCase)) return "Muskets";
            if (t == "equipment" || t == "equpiment") return "Armour";
            if (t == "classweapon" || Slot.Equals("weapon", StringComparison.OrdinalIgnoreCase)) return "Weapons";
            if (t == "tool") return "Tools";
            if (n.Contains("mount") || n.Contains("pet")) return "Pets & Mounts";
            return "Materials & Other";
        }
    }
    public string IconGlyph => Category switch
    {
        "Bows" => "⌖", "Mallets" => "♫", "Hammers" => "⚒",
        "Wands" => "✧", "Muskets" => "◎", "Weapons" => "⚔",
        "Armour" => "♜", "Potions" => "♧", "Dyes" => "◈",
        "Tools" => "⚙", "Pets & Mounts" => "♞", _ => "◆"
    };
    public string RarityColor => Rarity.ToLowerInvariant() switch
    {
        "uncommon" => "#70B97C", "rare" => "#629CE6",
        "epic" => "#B78BE8", "legendary" => "#E5B65A", _ => "#A4ABB8"
    };
    // Optional local artwork directory: do not package game art into the app.
    // Owners may supply icons they have permission to use, named exactly as the CSV icon field.
    public string? IconPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IconName) ||
                !IconName.Equals(Path.GetFileName(IconName), StringComparison.Ordinal))
                return null;
            if (!IconName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
            string path = Path.Combine(LocalItemArtwork.DirectoryPath, IconName);
            return File.Exists(path) ? path : null;
        }
    }
    public Visibility IconImageVisibility => IconPath == null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility IconFallbackVisibility => IconPath == null ? Visibility.Visible : Visibility.Collapsed;
    public string TooltipText =>
        $"{Name}\n#{Id} · {Rarity} · {Category}\n" +
        (string.IsNullOrWhiteSpace(RequiredClass) ? "" : $"Class: {RequiredClass}\n") +
        $"Minimum level: {MinimumLevel}\n" +
        (string.IsNullOrWhiteSpace(Description) ? "" : Description + "\n") +
        "Base catalogue item — generated equipment stats are not guaranteed by standard gifting.";
}

public sealed record OrbusInventorySlot(int Slot, int ItemId, int Quantity, ServerItemEntry? Item,
    string Metadata, string ParseWarning)
{
    public string Name => Item?.Name ?? (ItemId == 0 ? "Empty slot" : $"Unknown item #{ItemId}");
    public string IconGlyph => Item?.IconGlyph ?? "◆";
    public string? IconPath => Item?.IconPath;
    public Visibility IconImageVisibility => Item?.IconImageVisibility ?? Visibility.Collapsed;
    public Visibility IconFallbackVisibility => Item?.IconFallbackVisibility ?? Visibility.Visible;
    public string RarityColor => Item?.RarityColor ?? "#707783";
    public string ShortLabel => ItemId == 0 ? "—" : (Quantity > 1 ? $"×{Quantity}" : "");
    public string TooltipText => $"Slot {Slot + 1}: {Name}\n" + ItemStats;
    public string ItemStats
    {
        get
        {
            var stats = new List<string>();
            if (ItemId == 0) return "Empty";
            if (Quantity > 1) stats.Add($"Quantity: {Quantity}");
            if (!string.IsNullOrWhiteSpace(ParseWarning)) stats.Add(ParseWarning);
            if (!string.IsNullOrWhiteSpace(Metadata))
            {
                try
                {
                    using var doc = JsonDocument.Parse(Metadata);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (string key in new[] {
                            "itemLevel", "level", "rarity", "armor", "damage",
                            "durability", "currentDurability", "maxDurability",
                            "upgrade", "upgradeLevel", "enhancementLevel", "affixes",
                            "strength", "intellect", "vitality", "defense", "criticalChance"
                        })
                        {
                            if (doc.RootElement.TryGetProperty(key, out var val))
                                stats.Add($"{key}: {val}");
                        }
                        if (stats.Count == 0)
                            stats.Add("Instance metadata exists; no recognised stat fields.");
                    }
                    else stats.Add("Unrecognised equipment metadata");
                }
                catch (JsonException) { stats.Add("Unrecognised equipment metadata format"); }
            }
            else stats.Add("No rolled item stats stored in this slot");
            return string.Join("\n", stats);
        }
    }

    public static OrbusInventorySlot Parse(int index, string? raw,
        IReadOnlyDictionary<int, ServerItemEntry> items)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "0")
            return new OrbusInventorySlot(index, 0, 0, null, "", "");
        string[] parts = raw.Split('%', 3);
        if (!int.TryParse(parts[0], out int id) || id < 0)
            return new OrbusInventorySlot(index, 0, 0, null, "", "Unknown inventory slot encoding");
        int quantity = 1;
        if (parts.Length > 1 && (!int.TryParse(parts[1], out quantity) || quantity <= 0))
            quantity = 1;
        items.TryGetValue(id, out var item);
        return new OrbusInventorySlot(index, id, quantity, item,
            parts.Length == 3 ? parts[2] : "", "");
    }
}

public sealed record ServerCharacterSnapshot(string CharacterName,
    IReadOnlyList<string> SerializedSlots, IReadOnlyDictionary<string,string> Progression,
    string Diagnostic);
