using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace OrbusRebornManager;

/// <summary>
/// Quarantines only malformed items from our experimental 2026-11-09 grant
/// serializer. The current inventory is never replaced with an old backup.
/// Instead, individual newly inserted slots are set back to their original
/// empty values, using an atomic full-inventory compare-and-set.
/// </summary>
public sealed class MalformedExperimentalItemRecovery
{
    private readonly ServerHostService _host = new();
    public sealed record Change(int Slot, string Expected, string Original);

    private const string RecoverJs = """
(async function() {
 const marker="ORBUS_MALFORMED_RECOVERY:";
 let finished=false;
 const timeout=setTimeout(function(){ finish({ok:false,error:"Recovery timed out. Refresh inventory before trying anything else."}); },17000);
 function finish(value) {
   if (finished) return;
   finished=true; clearTimeout(timeout);
   process.stdout.write(marker+JSON.stringify(value)+"\n",function(){process.exit(0);});
 }
 try {
   const name=process.argv[1], hash=process.argv[2], changes=JSON.parse(process.argv[3]);
   if (!name || name.length>80 || !/^[a-f0-9]{64}$/.test(hash) ||
       !Array.isArray(changes) || changes.length<1 || changes.length>10) {
     finish({ok:false,error:"Invalid recovery arguments."}); return;
   }
   const slots={};
   for (const x of changes) {
     if (!x || !Number.isInteger(x.slot) || x.slot<0 || x.slot>249 ||
         Object.prototype.hasOwnProperty.call(slots,String(x.slot)) ||
         typeof x.expected!=="string" || x.expected.length>4096 ||
         typeof x.original!=="string" || !["","0"].includes(x.original) ||
         !/^[1-9][0-9]*%1%\{.*\}$/.test(x.expected)) {
       finish({ok:false,error:"Recovery contains an unsafe or duplicate slot."}); return;
     }
     const parsed=JSON.parse(x.expected.split("%",3)[2]);
     if (!parsed || typeof parsed!=="object" || !Number.isInteger(parsed.itemLevel) ||
         !Number.isInteger(parsed.itemPlusLevel) ||
         !Array.isArray(parsed.affixes) || typeof parsed.rarity!=="string") {
       finish({ok:false,error:"Item metadata does not resemble the known broken serializer."}); return;
     }
     slots[String(x.slot)]=true;
   }
   let r, run;
   const host=process.env.ORBUS_DB_HOST || "rethinkdb";
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
   if (!Array.isArray(rows) || rows.length!==1 || rows[0].id===undefined) {
     finish({ok:false,error:"Could not identify exactly one saved player record."}); return;
   }
   const player=rows[0], inv=player.inventory;
   if (!Array.isArray(inv) || inv.length>250 || inv.some(x=>typeof x!=="string") ||
       changes.some(x=>x.slot>=inv.length || inv[x.slot]!==x.expected)) {
     finish({ok:false,error:"The saved inventory differs from the reviewed plan. Nothing changed."}); return;
   }
   const actual=require("crypto").createHash("sha256").update(inv.join("\u001f"),"utf8").digest("hex");
   if (actual!==hash) {
     finish({ok:false,error:"Saved inventory changed since the preview. Nothing changed."}); return;
   }
   const cleaned=inv.slice();
   for (const x of changes) cleaned[x.slot]=x.original;
   const result=await run(table.get(player.id).update(function(row) {
     return r.branch(
       row("name").eq(name).and(row("inventory").eq(inv)),
       {inventory:r.expr(cleaned)},
       {}
     );
   }));
   if (!result || result.replaced!==1) {
     finish({ok:false,error:"A concurrent save prevented recovery. No verified change."}); return;
   }
   const verified=await run(table.get(player.id).pluck("name","inventory"));
   if (!verified || verified.name!==name || !Array.isArray(verified.inventory) ||
       verified.inventory.length!==cleaned.length ||
       verified.inventory.some((x,i)=>x!==cleaned[i])) {
     finish({ok:false,error:"Read-back verification failed. Preserve all backups."}); return;
   }
   finish({ok:true,removed:changes.length});
 } catch(e) { finish({ok:false,error:String(e.message||e).slice(0,300)}); }
})().catch(function(e){ console.log("ORBUS_MALFORMED_RECOVERY:"+JSON.stringify({ok:false,error:String(e.message||e).slice(0,300)})); });
""";

    public async Task RecoverAsync(string serverFolder, string character, IReadOnlyList<Change> changes,
        IReadOnlyList<string> expectedCurrentInventory)
    {
        if (character.Length is < 1 or > 80 || character.Any(char.IsControl) ||
            changes.Count is < 1 or > 10 || expectedCurrentInventory.Count is < 1 or > 250 ||
            changes.Any(x=>x.Slot<0 || x.Slot>=expectedCurrentInventory.Count ||
                x.Expected!=expectedCurrentInventory[x.Slot] || (x.Original!="" && x.Original!="0")))
            throw new InvalidOperationException("Recovery inputs are not safe.");
        string args=JsonSerializer.Serialize(changes,
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        string output=await _host.ComposeAsync(serverFolder,30000,"exec","-T","api","node","-e",
            RecoverJs,character,ServerEquipmentInstanceWriter.Fingerprint(expectedCurrentInventory),args);
        string? line=output.Split('\n').LastOrDefault(x=>x.StartsWith("ORBUS_MALFORMED_RECOVERY:",StringComparison.Ordinal));
        if (line==null) throw new InvalidOperationException("Server did not return a recovery verification.");
        using var doc=JsonDocument.Parse(line["ORBUS_MALFORMED_RECOVERY:".Length..]);
        if (!doc.RootElement.TryGetProperty("ok",out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(doc.RootElement.TryGetProperty("error",out var e)
                ? e.GetString() : "Recovery was not verified.");
        if (doc.RootElement.GetProperty("removed").GetInt32()!=changes.Count)
            throw new InvalidOperationException("Recovery count does not match requested changes.");
    }
}

public partial class MainWindow
{
    private readonly MalformedExperimentalItemRecovery _malformedRecovery=new();
    private bool _malformedRecoveryBusy;

    private static bool MatchesExactExperimentalMetadata(string raw, int itemId, JsonElement requested)
    {
        int first=raw.IndexOf('%');
        if (first<1 || raw.AsSpan(0,first).IndexOf('@')>=0 ||
            !int.TryParse(raw.AsSpan(0,first),NumberStyles.None,CultureInfo.InvariantCulture,out int actualId) ||
            actualId!=itemId || !raw.AsSpan(first).StartsWith("%1%"))
            return false;
        try
        {
            using var doc=JsonDocument.Parse(raw[(first+3)..]);
            var actual=doc.RootElement;
            if (actual.ValueKind!=JsonValueKind.Object || requested.ValueKind!=JsonValueKind.Object ||
                !actual.TryGetProperty("itemLevel",out _) ||
                !actual.TryGetProperty("itemPlusLevel",out _) ||
                !actual.TryGetProperty("rarity",out _) ||
                !actual.TryGetProperty("affixes",out _) ||
                actual.EnumerateObject().Count()!=requested.EnumerateObject().Count())
                return false;
            foreach(var p in requested.EnumerateObject())
                if (!actual.TryGetProperty(p.Name,out var v) ||
                    JsonSerializer.Serialize(v)!=JsonSerializer.Serialize(p.Value))
                    return false;
            return true;
        }
        catch(JsonException) { return false; }
    }

    private sealed record ExperimentalRequest(int ItemId,JsonElement Metadata);
    private static List<ExperimentalRequest> GetMatchingGrantRequests(string directory,string server,string name)
    {
        var results=new List<ExperimentalRequest>();
        foreach(string path in Directory.EnumerateFiles(directory,"*.json").Take(500))
        {
            try
            {
                if(new FileInfo(path).Length>1000000) continue;
                using var doc=JsonDocument.Parse(File.ReadAllText(path));
                var root=doc.RootElement;
                if(root.GetProperty("operation").GetString()!="pre-experimental-equipment-grant" ||
                   root.GetProperty("characterName").GetString()!=name ||
                   !string.Equals(Path.GetFullPath(root.GetProperty("serverFolder").GetString()??""),
                       Path.GetFullPath(server),StringComparison.OrdinalIgnoreCase))
                    continue;
                results.Add(new ExperimentalRequest(
                    root.GetProperty("requestedItemId").GetInt32(),
                    root.GetProperty("requestedMetadata").Clone()));
            }
            catch(Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                // Unrelated/malformed backup files cannot authorise a recovery.
            }
        }
        return results;
    }

    private async void RepairMalformedInventory_Click(object sender,RoutedEventArgs e)
    {
        if(_malformedRecoveryBusy || _chosenPlayer==null) return;
        string? folder=ServerFolder();
        if(folder==null) return;
        string name=_chosenPlayer.Name;
        try
        {
            var dialog=new OpenFileDialog
            {
                Title="Choose EARLIEST backup from before the broken experimental grants",
                Filter="Pre-experiment inventory backup (*.json)|*.json",
                InitialDirectory=Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OrbusRebornManager","inventory-backups")
            };
            if(dialog.ShowDialog(this)!=true) return;
            using var file=JsonDocument.Parse(await File.ReadAllTextAsync(dialog.FileName));
            var root=file.RootElement;
            if(root.GetProperty("operation").GetString()!="pre-experimental-equipment-grant" ||
               root.GetProperty("characterName").GetString()!=name ||
               !string.Equals(Path.GetFullPath(root.GetProperty("serverFolder").GetString()??""),
                   Path.GetFullPath(folder),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected backup does not belong to this character and server.");
            var original=root.GetProperty("originalInventory").EnumerateArray()
                .Select(x=>x.GetString()??"").ToList();
            var live=await _characterInspector.ReadAsync(folder,name);
            if(live.SerializedSlots.Count!=original.Count ||
                original.Count is < 1 or >= 250)
                throw new InvalidOperationException("Slot counts don't match, so an automated recovery isn't safe.");
            if(!string.Equals(folder,_selectedServerPath,StringComparison.OrdinalIgnoreCase) ||
                name!=_chosenPlayer?.Name)
                throw new InvalidOperationException("Selected character or server changed during recovery.");
            var requests=GetMatchingGrantRequests(Path.GetDirectoryName(dialog.FileName)!,folder,name);
            if(requests.Count==0)
                throw new InvalidOperationException("No matching experiment records found in the backup directory.");

            var toRemove=new List<MalformedExperimentalItemRecovery.Change>();
            int otherChanges=0;
            for(int i=0;i<original.Count;i++)
            {
                string old=original[i], now=live.SerializedSlots[i];
                if(old==now) continue;
                if((old=="" || old=="0") &&
                    requests.Any(r=>MatchesExactExperimentalMetadata(now,r.ItemId,r.Metadata)))
                    toRemove.Add(new MalformedExperimentalItemRecovery.Change(i,now,old));
                else otherChanges++;
            }
            if(toRemove.Count==0)
            {
                MessageBox.Show(this,
                    $"No currently saved malformed experimental items match this backup.\n" +
                    $"Other inventory differences: {otherChanges}.\n\n" +
                    "No data was changed. If the game remains empty, investigate the saved-to-client synchronisation.",
                    "Inventory recovery",MessageBoxButton.OK,MessageBoxImage.Information);
                return;
            }
            if(toRemove.Count>10)
                throw new InvalidOperationException("More than ten suspicious entries; manual review required.");

            string description=string.Join("\n",toRemove.Select(x=>
                $"Slot {x.Slot+1}: {x.Expected[..Math.Min(85,x.Expected.Length)]}"));
            if(MessageBox.Show(this,
                $"The backup identifies {toRemove.Count} experimental entry/entries with the WRONG outer '%' delimiter:\n\n" +
                description+"\n\n"+
                $"Other differences from the backup: {otherChanges} (WILL NOT BE CHANGED).\n\n" +
                "Only the listed entries will be cleared back to their original empty slots. " +
                "ALL OTHER INVENTORY, ALL CLASS LEVELS AND ALL PROGRESSION WILL BE PRESERVED.\n\n" +
                "The player MUST be logged out. Continue?",
                "Confirm targeted inventory repair",MessageBoxButton.YesNo,
                MessageBoxImage.Warning)!=MessageBoxResult.Yes)
                return;

            _malformedRecoveryBusy=true;
            string folderBackups=Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornManager","inventory-backups");
            Directory.CreateDirectory(folderBackups);
            string save=Path.Combine(folderBackups,"before-malformed-recovery-"+
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".json");
            await File.WriteAllTextAsync(save,JsonSerializer.Serialize(new
            {
                timestampUtc=DateTime.UtcNow,
                characterName=name,serverFolder=folder,
                operation="before-malformed-recovery",
                currentInventory=live.SerializedSlots,
                sourceBackup=Path.GetFileName(dialog.FileName),
                slotsToRemove=toRemove
            },new JsonSerializerOptions{WriteIndented=true}));

            await _malformedRecovery.RecoverAsync(folder,name,toRemove,live.SerializedSlots);
            await LoadCharacterSnapshotAsync();
            MessageBox.Show(this,
                $"Verified removal of {toRemove.Count} malformed experimental item(s).\n\n" +
                $"Snapshot of the previous live inventory: {save}\n\n" +
                "Restart the game before checking inventory. No other slots or class levels were touched.",
                "Targeted recovery complete",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        catch(Exception ex)
        {
            Log("Malformed equipment recovery: "+ex.Message);
            MessageBox.Show(this,"No successful targeted repair was confirmed.\n\n"+ex.Message,
                "Inventory recovery",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally { _malformedRecoveryBusy=false; }
    }
}
