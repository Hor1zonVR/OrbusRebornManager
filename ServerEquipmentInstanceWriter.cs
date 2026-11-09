using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OrbusRebornManager;

/// <summary>
/// Patches only a newly admin-granted saved equipment slot, never an arbitrary
/// inventory position. The caller first runs the official admin gift endpoint,
/// compares the before/after snapshots, and makes a local backup.
/// Weapon-specific scaling is not guaranteed by the saved metadata format.
/// </summary>
public sealed class ServerEquipmentInstanceWriter
{
    private readonly ServerHostService _host = new();

    private const string PatchJs = """
(async function () {
 const marker = "ORBUS_EQUIPMENT_PATCH:";
 let done = false;
 const timeout = setTimeout(function () { output({ok:false,error:"Equipment patch timed out. Refresh the inventory before retrying."}); }, 17000);
 function output(obj) {
   if (done) return;
   done = true; clearTimeout(timeout);
   process.stdout.write(marker + JSON.stringify(obj) + "\n", function () { process.exit(0); });
 }
 try {
   const name = process.argv[1], itemId = Number(process.argv[2]);
   const slot = Number(process.argv[3]), oldRaw = process.argv[4];
   const metadata = process.argv[5], expectedHash = process.argv[6];
   if (!name || name.length > 80 || !Number.isInteger(itemId) || itemId < 1 ||
       !Number.isInteger(slot) || slot < 0 || slot > 249 ||
       !oldRaw || oldRaw.length > 4096 || !metadata || metadata.length > 2048 ||
       !/^[a-f0-9]{64}$/.test(expectedHash)) {
     output({ok:false,error:"Invalid equipment patch request."}); return;
   }
   const base = String(itemId);
   // Only patch the unique newly inserted BASE item, never an existing rolled item.
   if (oldRaw !== base && oldRaw !== base + "@1" && oldRaw !== base + "@1@{}") {
     output({ok:false,error:"The admin grant did not produce a plain, unmodified equipment item."}); return;
   }
   const info = JSON.parse(metadata);
   if (!info || typeof info !== "object" || Array.isArray(info) ||
       !Number.isInteger(info.itemLevel) || info.itemLevel < 1 || info.itemLevel > 100 ||
       !Number.isInteger(info.itemPlusLevel) || info.itemPlusLevel < 0 || info.itemPlusLevel > 100 ||
       !Array.isArray(info.affixes) || info.affixes.length > 6 ||
       info.affixes.some(x => typeof x !== "string" || !/^[a-zA-Z0-9_-]{1,48}$/.test(x)) ||
       !["common","uncommon","rare","epic","legendary"].includes(info.rarity) ||
       Object.keys(info).some(k => !["itemLevel","itemPlusLevel","rarity","affixes",
          "maxDurability","armor","attack"].includes(k))) {
     output({ok:false,error:"Experimental metadata did not pass validation."}); return;
   }
   for (const key of ["maxDurability","armor","attack"]) {
     if (Object.prototype.hasOwnProperty.call(info,key) &&
         (!Number.isInteger(info[key]) || info[key] < 0 || info[key] > 1000000)) {
       output({ok:false,error:"Invalid numeric equipment stat: " + key}); return;
     }
   }
   let r, run;
   const host = process.env.ORBUS_DB_HOST || "rethinkdb";
   try {
     r = require("rethinkdbdash")({servers:[{host:host,port:28015}],timeout:10,buffer:10,silent:true});
     run = async q => { const x = await q.run(); return x && typeof x.toArray === "function" ? await x.toArray() : x; };
   } catch (e) {
     r = require("rethinkdb");
     const conn = await r.connect({host:host,port:28015,timeout:10});
     run = async q => { const x = await q.run(conn); return x && typeof x.toArray === "function" ? await x.toArray() : x; };
   }
   const table = r.db("orbus").table("Player");
   const matches = await run(table.filter({name:name}).limit(2));
   if (!Array.isArray(matches) || matches.length !== 1 || matches[0].id === undefined) {
     output({ok:false,error:"Expected exactly one saved character record."}); return;
   }
   const player = matches[0], inv = player.inventory;
   // Refuse any existing malformed top-level experimental entry; recover first.
   if (Array.isArray(inv) && inv.some(x => typeof x === "string" && /^[0-9]+%[0-9]+%/.test(x))) {
     output({ok:false,error:"Existing malformed inventory entry detected. Recover it before new tests."}); return;
   }
   if (!Array.isArray(inv) || inv.length > 250 || slot >= inv.length ||
       inv.some(x => typeof x !== "string") || inv[slot] !== oldRaw) {
     output({ok:false,error:"Inventory slot changed or unsupported saved slot layout. Refresh before retrying."}); return;
   }
   const crypto = require("crypto");
   const actualHash = crypto.createHash("sha256").update(inv.join("\u001f"),"utf8").digest("hex");
   if (actualHash !== expectedHash) {
     output({ok:false,error:"Inventory changed after the grant. No equipment metadata was written."}); return;
   }
   // Player.inventory top-level delimiter is @; nested containedItems may use %.
   // Writer remains QUARANTINED in RM until game-client round-trip validation.
   const nextRaw = itemId + "@1@" + JSON.stringify(info);
   const write = await run(table.get(player.id).update(function (row) {
     return r.branch(
       row("name").eq(name).and(row("inventory").eq(inv)),
       {inventory:row("inventory").changeAt(slot,nextRaw)},
       {}
     );
   }));
   if (!write || write.replaced !== 1) {
     output({ok:false,error:"Inventory was modified during the patch. No new equipment metadata was written."}); return;
   }
   const verify = await run(table.get(player.id).pluck("inventory","name"));
   if (!verify || verify.name !== name || !Array.isArray(verify.inventory) ||
       verify.inventory[slot] !== nextRaw) {
     output({ok:false,error:"Equipment write returned, but saved read-back did not verify."}); return;
   }
   output({ok:true,slot:slot + 1,raw:nextRaw});
 } catch(e) { output({ok:false,error:String(e.message || e).slice(0,300)}); }
})().catch(e => console.log("ORBUS_EQUIPMENT_PATCH:" + JSON.stringify({ok:false,error:String(e.message || e).slice(0,300)})));
""";

    public static string Fingerprint(IReadOnlyList<string> inventory)
    {
        if (inventory.Count > 250)
            throw new InvalidOperationException("This server has more than 250 saved inventory slots. No patch attempted.");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", inventory)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string MakeMetadata(int itemLevel, int plus, string rarity,
        IReadOnlyList<string> affixes, int? maxDurability, int? armor, int? attack)
    {
        if (itemLevel is < 1 or > 100 || plus is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(itemLevel), "Experimental level and plus rank must be between 1–100 and 0–100.");
        if (!new[] { "common","uncommon","rare","epic","legendary" }.Contains(rarity))
            throw new InvalidOperationException("Choose a supported equipment rarity.");
        if (affixes.Count > 6 || affixes.Any(a => !System.Text.RegularExpressions.Regex.IsMatch(a, @"^[a-zA-Z0-9_-]{1,48}$")))
            throw new InvalidOperationException("Affixes must be server IDs (letters, numbers, underscore or dash), max six.");
        var data = new Dictionary<string,object>
        {
            ["itemLevel"] = itemLevel,
            ["itemPlusLevel"] = plus,
            ["rarity"] = rarity,
            ["affixes"] = affixes.ToArray()
        };
        void AddStat(string key, int? value)
        {
            if (value.HasValue)
            {
                if (value < 0 || value > 1000000)
                    throw new InvalidOperationException(key + " must be between 0 and 1,000,000.");
                data[key] = value.Value;
            }
        }
        AddStat("maxDurability", maxDurability);
        AddStat("armor", armor);
        AddStat("attack", attack);
        string json = JsonSerializer.Serialize(data);
        if (json.Length > 2048)
            throw new InvalidOperationException("Metadata is too long.");
        return json;
    }

    public static bool IsPlainBaseGrant(int itemId, string raw)
    {
        if (itemId <= 0) return false;
        string id = itemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return raw == id || raw == id + "@1" || raw == id + "@1@{}";
    }

    public static bool IsMalformedOuterEntry(string raw) =>
        System.Text.RegularExpressions.Regex.IsMatch(raw ?? "", @"^[0-9]+%[0-9]+%");

    public async Task<int> PatchGrantedSlotAsync(string serverPath, string name, int itemId,
        int slot, string beforeRaw, string metadata, IReadOnlyList<string> expectedInventory)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new InvalidOperationException("Select a valid character.");
        if (itemId <= 0 || slot < 0 || slot >= expectedInventory.Count ||
            beforeRaw != expectedInventory[slot] ||
            !IsPlainBaseGrant(itemId, beforeRaw))
            throw new InvalidOperationException("Invalid newly granted slot.");
        using (var parsed = JsonDocument.Parse(metadata))
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Equipment metadata must be an object.");
        string output = await _host.ComposeAsync(serverPath, 30000,
            "exec", "-T", "api", "node", "-e", PatchJs,
            name, itemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
            beforeRaw, metadata, Fingerprint(expectedInventory));
        string? line = output.Split('\n').LastOrDefault(x =>
            x.StartsWith("ORBUS_EQUIPMENT_PATCH:", StringComparison.Ordinal));
        if (line == null)
            throw new InvalidOperationException("The server did not return a verifiable equipment response.");
        using var result = JsonDocument.Parse(line["ORBUS_EQUIPMENT_PATCH:".Length..]);
        var root = result.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(root.TryGetProperty("error", out var err)
                ? err.GetString() : "The equipment metadata patch failed.");
        return root.GetProperty("slot").GetInt32();
    }
}
