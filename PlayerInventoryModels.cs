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
    // PNGs stay on the owner's computer. AssetStudio exports commonly have
    // nested "Sprite"/"Texture2D" directories; index their files by basename.
    public static string DirectoryPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrbusRebornManager", "ItemIcons");

    private static IReadOnlyDictionary<string, string> _indexed = new Dictionary<string, string>(
        StringComparer.OrdinalIgnoreCase);

    public static int IndexedFileCount => System.Threading.Volatile.Read(ref _indexed).Count;

    public static void RefreshIndex()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = DirectoryPath;
        if (Directory.Exists(root))
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 15
            };
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", options).Take(100000))
                {
                    if (!Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string basename = Path.GetFileName(file);
                    if (!found.TryGetValue(basename, out string? existing) ||
                        (file.Contains("Sprite", StringComparison.OrdinalIgnoreCase) &&
                         !existing.Contains("Sprite", StringComparison.OrdinalIgnoreCase)))
                        found[basename] = file;
                }
            }
            catch (IOException) { /* Use whichever PNGs were readable. */ }
            catch (UnauthorizedAccessException) { /* Inaccessible folders are skipped. */ }
        }
        if (string.Equals(root, DirectoryPath, StringComparison.OrdinalIgnoreCase))
            System.Threading.Volatile.Write(ref _indexed, found);
    }

    public static string? FindIcon(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !name.Equals(Path.GetFileName(name), StringComparison.Ordinal) ||
            !Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase))
            return null;
        var indexed = System.Threading.Volatile.Read(ref _indexed);
        if (indexed.TryGetValue(name, out string? result) && File.Exists(result))
            return result;
        string directPath = Path.Combine(DirectoryPath, name);
        return File.Exists(directPath) ? directPath : null;
    }
}

public sealed record ServerItemEntry(
    int Id, string Name, string IconName = "", string Description = "",
    string Rarity = "", string Type = "", string Slot = "",
    string RequiredClass = "", int MinimumLevel = 1, string BaseProperties = "")
{
    public string Label => $"#{Id} · {Name}";
    public string ShortId => $"#{Id}";
    public string Category
    {
        get
        {
            string n = Name.ToLowerInvariant(), t = Type.ToLowerInvariant();
            if (n.Contains("dye") || t.Contains("dye")) return "Dyes";
            if (n.Contains("potion") || n.Contains("elixir") || t.Contains("consumable")) return "Potions";
            // Armour must stay in Armour even if its required class is Bard/Runemage/etc.
            if (t == "equipment" || t == "equpiment") return "Armour";
            if (n.Contains("mallet") || RequiredClass.Equals("bard", StringComparison.OrdinalIgnoreCase)) return "Mallets";
            if (n.Contains("hammer") || RequiredClass.Equals("paladin", StringComparison.OrdinalIgnoreCase)) return "Hammers";
            if (n.Contains("bow") || RequiredClass.Equals("archer", StringComparison.OrdinalIgnoreCase)) return "Bows";
            if (n.Contains("wand") || RequiredClass.Equals("runemage", StringComparison.OrdinalIgnoreCase)) return "Wands";
            if (n.Contains("musket") || RequiredClass.Equals("orbhealer", StringComparison.OrdinalIgnoreCase)) return "Muskets";
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
    public string? IconPath => LocalItemArtwork.FindIcon(IconName);
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
    public bool TryGetDurability(out double current, out double maximum)
    {
        current = 0;
        maximum = 0;
        if (string.IsNullOrWhiteSpace(Metadata)) return false;
        try
        {
            using var doc = JsonDocument.Parse(Metadata);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("maxDurability", out var max) ||
                max.ValueKind != JsonValueKind.Number || !max.TryGetDouble(out maximum) ||
                maximum <= 0) return false;
            if ((!doc.RootElement.TryGetProperty("durability", out var val) &&
                !doc.RootElement.TryGetProperty("currentDurability", out val)) ||
                val.ValueKind != JsonValueKind.Number || !val.TryGetDouble(out current))
                return false;
            current = Math.Clamp(current, 0, maximum);
            return true;
        }
        catch (JsonException) { return false; }
    }
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
                            "upgrade", "upgradeLevel", "enhancementLevel", "itemPlusLevel", "affixes",
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

// These values are read from the saved skill_level and skill_xp objects.
// Do NOT invent XP thresholds or interpret raw XP as "to next level".
public sealed record ServerClassProgress(string Key, int Level, long RecordedXp)
{
    public string DisplayName => Key.ToLowerInvariant() switch
    {
        "orbhealer" => "Musketeer",
        "swordboard" => "Warrior",
        "fisher" => "Fishing",
        "gambler" => "Gambling",
        "runemage" => "Runemage",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Key.Replace('_', ' '))
    };
    public string LevelLabel => $"Level {Level}";
    public string XpLabel => $"{RecordedXp:N0} XP recorded";
    public string LevelColor => Level > 1 ? "#86D9BC" : "#D9DEE7";

    public static IReadOnlyList<ServerClassProgress> Read(
        IReadOnlyDictionary<string, string> fields)
    {
        Dictionary<string, long> Extract(string key)
        {
            var numbers = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (!fields.TryGetValue(key, out string? json) || string.IsNullOrWhiteSpace(json))
                return numbers;
            try
            {
                using var parsed = JsonDocument.Parse(json);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    return numbers;
                foreach (var entry in parsed.RootElement.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Number &&
                        entry.Value.TryGetInt64(out long n))
                        numbers[entry.Name] = n;
            }
            catch (JsonException) { /* Raw field remains available for diagnostics. */ }
            return numbers;
        }

        var levels = Extract("skill_level");
        var xp = Extract("skill_xp");
        return levels.Keys.Union(xp.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(key => new ServerClassProgress(
                key, levels.TryGetValue(key, out long level) ?
                    (int)Math.Clamp(level, 0, int.MaxValue) : 0,
                xp.TryGetValue(key, out long value) ? value : 0))
            .OrderByDescending(c => c.Level)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record ServerCharacterSnapshot(string CharacterName,
    IReadOnlyList<string> SerializedSlots, IReadOnlyDictionary<string,string> Progression,
    string Diagnostic);
