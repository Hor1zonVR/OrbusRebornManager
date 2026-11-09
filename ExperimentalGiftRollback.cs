using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace OrbusRebornManager;

/// <summary>
/// Reverts ONLY one newly granted item to its pre-experiment empty slot.
/// Never replaces a whole inventory or any existing equipment.
/// </summary>
public sealed class ExperimentalGiftRollback
{
    private readonly ServerHostService _host = new();

    private const string RollbackJs = """
(async function () {
 const mark = "ORBUS_GIFT_ROLLBACK:";
 let finished = false;
 const timer = setTimeout(function() { finish({ok:false,error:"Rollback timed out. Refresh inventory before retrying."}); },17000);
 function finish(v) {
   if (finished) return;
   finished = true; clearTimeout(timer);
   process.stdout.write(mark+JSON.stringify(v)+"\n",function(){process.exit(0);});
 }
 try {
   const name = process.argv[1], slot = Number(process.argv[2]);
   const grantedItem = Number(process.argv[3]), hash = process.argv[4];
   const expected = process.argv[5], original = process.argv[6];
   if (!name || name.length > 80 || !Number.isInteger(slot) || slot < 0 || slot > 249 ||
       !Number.isInteger(grantedItem) || grantedItem < 1 ||
       !/^[0-9a-f]{64}$/.test(hash) || !expected || expected.length > 4096 ||
       !["","0"].includes(original) ||
       !(expected.startsWith(String(grantedItem) + "%1%") ||
         expected.startsWith(String(grantedItem) + "@1@"))) {
     finish({ok:false,error:"Invalid or unsafe rollback request."}); return;
   }
   let r, run;
   const host = process.env.ORBUS_DB_HOST || "rethinkdb";
   try {
     r=require("rethinkdbdash")({servers:[{host:host,port:28015}],timeout:10,buffer:10,silent:true});
     run=async function(q) { const v=await q.run(); return v && typeof v.toArray==="function" ? await v.toArray() : v; };
   } catch(e) {
     r=require("rethinkdb");
     const conn=await r.connect({host:host,port:28015,timeout:10});
     run=async function(q) { const v=await q.run(conn); return v && typeof v.toArray==="function" ? await v.toArray() : v; };
   }
   const table=r.db("orbus").table("Player");
   const rows=await run(table.filter({name:name}).limit(2));
   if (!Array.isArray(rows) || rows.length !== 1 || rows[0].id===undefined) {
     finish({ok:false,error:"Expected exactly one saved player record."}); return;
   }
   const player=rows[0], inv=player.inventory;
   if (!Array.isArray(inv) || inv.length > 250 || inv.some(x=>typeof x!=="string") ||
       slot >= inv.length || inv[slot]!==expected) {
     finish({ok:false,error:"Inventory changed or slot mismatch. No rollback applied."}); return;
   }
   const digest=require("crypto").createHash("sha256").update(inv.join("\u001f"),"utf8").digest("hex");
   if (digest!==hash) {
     finish({ok:false,error:"Saved inventory changed after the preview. No rollback applied."}); return;
   }
   const changed=await run(table.get(player.id).update(function(row) {
     return r.branch(
       row("name").eq(name).and(row("inventory").eq(inv)),
       {inventory:row("inventory").changeAt(slot,original)},
       {}
     );
   }));
   if (!changed || changed.replaced !== 1) {
     finish({ok:false,error:"Concurrent player save blocked the rollback."}); return;
   }
   const verified=await run(table.get(player.id).pluck("name","inventory"));
   if (!verified || verified.name!==name || !Array.isArray(verified.inventory) ||
       verified.inventory[slot]!==original) {
     finish({ok:false,error:"Write could not be verified. Do not retry blindly."}); return;
   }
   finish({ok:true,slot:slot+1});
 } catch(e) { finish({ok:false,error:String(e.message||e).slice(0,300)}); }
})().catch(function(e){ console.log("ORBUS_GIFT_ROLLBACK:"+JSON.stringify({ok:false,error:String(e.message||e).slice(0,300)})); });
""";

    public async Task RevertOneAsync(string folder, string name, int slot,
        int itemId, string beforeRaw, string currentRaw, IReadOnlyList<string> currentInventory)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ||
            slot < 0 || slot >= currentInventory.Count ||
            (beforeRaw != "" && beforeRaw != "0") ||
            currentRaw != currentInventory[slot] ||
            !(currentRaw.StartsWith(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%1%",
                 StringComparison.Ordinal) ||
              currentRaw.StartsWith(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "@1@",
                 StringComparison.Ordinal)))
            throw new InvalidOperationException("This recovery only removes a freshly granted item from its previously empty slot.");
        string output=await _host.ComposeAsync(folder,30000,"exec","-T","api","node","-e",
            RollbackJs,name,slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
            itemId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ServerEquipmentInstanceWriter.Fingerprint(currentInventory),currentRaw,beforeRaw);
        string? line=output.Split('\n').LastOrDefault(x=>x.StartsWith("ORBUS_GIFT_ROLLBACK:",StringComparison.Ordinal));
        if (line==null) throw new InvalidOperationException("Server did not confirm inventory rollback.");
        using var doc=JsonDocument.Parse(line["ORBUS_GIFT_ROLLBACK:".Length..]);
        if (!doc.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(doc.RootElement.TryGetProperty("error",out var e)
                ? e.GetString() : "Rollback failed.");
    }
}

public partial class MainWindow
{
    private readonly ExperimentalGiftRollback _giftRollback=new();
    private bool _giftRollbackBusy;

    private async void RecoverExperimentalInventory_Click(object sender, RoutedEventArgs e)
    {
        if (_giftRollbackBusy || _chosenPlayer==null) return;
        string? folder=ServerFolder();
        if (folder==null) return;
        string name=_chosenPlayer.Name;
        try
        {
            var dialog=new OpenFileDialog
            {
                Title="Choose pre-experimental-grant inventory backup",
                Filter="Inventory backup (*.json)|*.json",
                InitialDirectory=Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OrbusRebornManager","inventory-backups")
            };
            if (dialog.ShowDialog(this)!=true) return;
            using var file=JsonDocument.Parse(await File.ReadAllTextAsync(dialog.FileName));
            var root=file.RootElement;
            if (root.GetProperty("operation").GetString()!="pre-experimental-equipment-grant" ||
                !string.Equals(root.GetProperty("characterName").GetString(),name,StringComparison.Ordinal) ||
                !string.Equals(Path.GetFullPath(root.GetProperty("serverFolder").GetString() ?? ""),
                    Path.GetFullPath(folder),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("That backup does not belong to this selected character and server.");
            int itemId=root.GetProperty("requestedItemId").GetInt32();
            var old=root.GetProperty("originalInventory").EnumerateArray()
                .Select(x=>x.GetString() ?? "").ToList();
            var now=await _characterInspector.ReadAsync(folder,name);
            if (now.SerializedSlots.Count!=old.Count || old.Count==0 ||
                old.Count>=250 || now.SerializedSlots.Count>=250)
                throw new InvalidOperationException("Inventory length differs from the backup, so automated recovery is unsafe.");
            if (!string.Equals(folder,_selectedServerPath,StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(name,_chosenPlayer?.Name,StringComparison.Ordinal))
                throw new InvalidOperationException("Character selection changed during recovery.");
            var changed=old.Select((previous,i)=>(previous,i))
                .Where(x=>!string.Equals(x.previous,now.SerializedSlots[x.i],StringComparison.Ordinal))
                .ToList();
            if (changed.Count==0)
            {
                MessageBox.Show(this,"This backup already matches the saved inventory. Nothing was changed.",
                    "Inventory recovery",MessageBoxButton.OK,MessageBoxImage.Information);
                return;
            }
            if (changed.Count!=1)
                throw new InvalidOperationException(
                    $"This backup differs in {changed.Count} slots. Automatic recovery intentionally stops: " +
                    "do not overwrite unrelated items or changes. Preserve the backup for manual comparison.");
            var (original,slot)=changed[0];
            string current=now.SerializedSlots[slot];
            if ((original!="" && original!="0") ||
                !current.StartsWith(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture)+"%",
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Changed slot does not look like this experiment's newly granted item. No restoration performed.");

            if (MessageBox.Show(this,
                    $"The backup and current saved inventory differ in exactly one slot ({slot+1}).\n\n" +
                    $"Current item: {current[..Math.Min(200,current.Length)]}\n\n" +
                    "Only this newly added item will be removed. Every other inventory slot " +
                    "and all character levels will be left unchanged.\n\n" +
                    "The character MUST be logged out. Confirm recovery?",
                    "Recover saved inventory",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
                return;

            _giftRollbackBusy=true;
            // Snapshot the present database version before attempting even a one-slot rollback.
            string backupDir=Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornManager","inventory-backups");
            Directory.CreateDirectory(backupDir);
            string savePath=Path.Combine(backupDir,"before-recovery-"+
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".json");
            await File.WriteAllTextAsync(savePath,JsonSerializer.Serialize(new {
                timestampUtc=DateTime.UtcNow,characterName=name,serverFolder=folder,
                operation="pre-recovery-snapshot",currentInventory=now.SerializedSlots,
                sourceBackup=Path.GetFileName(dialog.FileName)
            },new JsonSerializerOptions{WriteIndented=true}));
            await _giftRollback.RevertOneAsync(folder,name,slot,itemId,original,current,now.SerializedSlots);
            await LoadCharacterSnapshotAsync();
            MessageBox.Show(this,
                $"Removed only the experimental gift from saved slot {slot+1}.\n\n" +
                $"Current inventory backup: {savePath}\n\n" +
                "Restart the client before checking all in-game bag pages.",
                "Inventory rollback verified",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception ex)
        {
            Log("Guarded experimental inventory recovery: "+ex.Message);
            MessageBox.Show(this,"No inventory rollback was confirmed.\n\n"+ex.Message,
                "Inventory recovery",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally { _giftRollbackBusy=false; }
    }
}
