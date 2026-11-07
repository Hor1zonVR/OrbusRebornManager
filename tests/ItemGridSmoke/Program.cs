using OrbusRebornManager;
using System;
using System.Collections.Generic;
var bow = new ServerItemEntry(619, "Ancient Bow", "bw_t_01.png",
    "Test bow", "rare", "classweapon", "weapon", "archer", 30, "");
var dye = new ServerItemEntry(1337, "Red Dye", "test.png",
    "Dye description", "common", "item", "none", "", 1, "");
if (bow.Category != "Bows" || dye.Category != "Dyes")
    throw new Exception("Catalogue item classification failed.");
var items = new Dictionary<int,ServerItemEntry> { [619]=bow };
var slot = OrbusInventorySlot.Parse(199,
    "619%1%{\"itemLevel\":30,\"armor\":40,\"maxDurability\":120,\"durability\":88,\"affixes\":[\"plusdotdmg\"]}",
    items);
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
