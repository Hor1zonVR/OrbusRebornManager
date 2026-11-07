using OrbusRebornManager;
using System;
using System.Collections.Generic;
var bow = new ServerItemEntry(619, "Ancient Bow", "bw_t_01.png",
    "Test bow", "rare", "classweapon", "weapon", "archer", 30, "");
var dye = new ServerItemEntry(1337, "Red Dye", "test.png",
    "Dye description", "common", "item", "none", "", 1, "");
var mageArmor = new ServerItemEntry(6, "Mages Shirt", "chest.png", "",
    "uncommon", "equipment", "chest", "runemage", 10, "");
if (bow.Category != "Bows" || dye.Category != "Dyes" || mageArmor.Category != "Armour")
    throw new Exception("Catalogue item classification failed.");
var items = new Dictionary<int,ServerItemEntry> { [619]=bow };
var slot = OrbusInventorySlot.Parse(199,
    "619%1%{\"itemLevel\":30,\"armor\":40,\"maxDurability\":120,\"durability\":88,\"affixes\":[\"plusdotdmg\"]}",
    items);
if (!slot.TryGetDurability(out var current, out var maxDurability) ||
    current != 88 || maxDurability != 120)
    throw new Exception("Saved durability bar values were not decoded.");
if (slot.Slot != 199 || slot.ItemId != 619 || slot.Name != "Ancient Bow" ||
    !slot.ItemStats.Contains("itemLevel: 30") ||
    !slot.ItemStats.Contains("maxDurability: 120") ||
    !slot.ItemStats.Contains("durability: 88") ||
    !slot.ItemStats.Contains("plusdotdmg"))
    throw new Exception("Saved item instance stats or durability were not decoded.");
var empty = OrbusInventorySlot.Parse(0, "0", items);
if (empty.ItemId != 0 || empty.ItemStats != "Empty")
    throw new Exception("Empty slot decoder failed.");
var plain = OrbusInventorySlot.Parse(1, "619%1", items);
if (!plain.ItemStats.Contains("No rolled item stats"))
    throw new Exception("Plain grant cannot fabricate equipment modifiers.");
Console.WriteLine("PASS: category filters and safe inventory instance stats.");

var fields = new Dictionary<string,string>
{
    ["skill_level"] = "{\"archer\":2,\"bard\":1,\"runemage\":30,\"orbhealer\":1,\"swordboard\":1}",
    ["skill_xp"] = "{\"archer\":3037,\"bard\":0,\"runemage\":42300,\"orbhealer\":0,\"swordboard\":0}"
};
var classes = ServerClassProgress.Read(fields);
if (classes.Count != 5 || classes[0].DisplayName != "Runemage" || classes[0].Level != 30 ||
    !classes.Any(x => x.DisplayName == "Archer" && x.Level == 2 && x.RecordedXp == 3037) ||
    !classes.Any(x => x.DisplayName == "Musketeer") ||
    !classes.Any(x => x.DisplayName == "Warrior"))
    throw new Exception("Saved per-class level/XP fields were not decoded.");
Console.WriteLine("PASS: real per-class progression fields.");

string artRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
    "OrbusItemArtTest-" + Guid.NewGuid().ToString("N"));
string originalArtRoot = LocalItemArtwork.DirectoryPath;
try
{
    string nested = System.IO.Path.Combine(artRoot, "Assets", "Sprite");
    System.IO.Directory.CreateDirectory(nested);
    string localIcon = System.IO.Path.Combine(nested, "BW_T_01.PNG");
    System.IO.File.WriteAllText(localIcon, "test"); // path-only match, not a real texture
    LocalItemArtwork.DirectoryPath = artRoot;
    LocalItemArtwork.RefreshIndex();
    if (LocalItemArtwork.IndexedFileCount != 1 ||
        !string.Equals(bow.IconPath, localIcon, StringComparison.OrdinalIgnoreCase) ||
        LocalItemArtwork.FindIcon("../BW_T_01.PNG") != null)
        throw new Exception("Recursive, case-insensitive asset icon lookup failed.");
    Console.WriteLine("PASS: local Sprite/Texture2D export folder indexing.");
}
finally
{
    LocalItemArtwork.DirectoryPath = originalArtRoot;
    LocalItemArtwork.RefreshIndex();
    if (System.IO.Directory.Exists(artRoot))
        System.IO.Directory.Delete(artRoot, true);
}
