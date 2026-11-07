using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OrbusRebornManager;

// Read-only bridge for a single selected character in the confirmed orbus.Player table.
// Avoid sending account identity fields, full player documents, or arbitrary data.
public sealed class ServerCharacterInspector
{
    private readonly ServerHostService _host = new();

    private const string QueryJs = """
(async () => {
 const marker = "ORBUS_CHARACTER_VIEW:";
 let done = false;
 const finish = data => {
   if (done) return;
   done = true;
   clearTimeout(timeout);
   process.stdout.write(marker + JSON.stringify(data) + "\n", function(){process.exit(0);});
 };
 const timeout = setTimeout(function(){ finish({ok:false,error:"Read-only character lookup timed out."}); }, 16000);
 try {
   const name = process.argv[1];
   if (!name || name.length > 80) { finish({ok:false,error:"Invalid selected character."}); return; }
   let r, run;
   const host = process.env.ORBUS_DB_HOST || "rethinkdb";
   try {
     r = require("rethinkdbdash")({servers:[{host:host,port:28015}],timeout:10,buffer:10,silent:true});
     run = async q => {
       const data = await q.run();
       return data && typeof data.toArray === "function" ? await data.toArray() : data;
     };
   } catch (e) {
     r = require("rethinkdb");
     const conn = await r.connect({host:host,port:28015,timeout:10});
     run = async q => {
       const data = await q.run(conn);
       return data && typeof data.toArray === "function" ? await data.toArray() : data;
     };
   }
   // Query by exact character name. Never export unrelated rows.
   let rows = await run(r.db("orbus").table("Player").filter({name:name}).limit(1));
   if (!Array.isArray(rows) || rows.length === 0) {
     finish({ok:false,error:"No current Player record matched the exact character name."}); return;
   }
   const player = rows[0];
   const inventory = Array.isArray(player.inventory) ? player.inventory.slice(0,250)
     .map(x => typeof x === "string" ? x.slice(0,4096) :
       typeof x === "number" ? String(x) : "") : [];
   // Only expose recognisable progression fields. Keys and values are bounded;
   // never send account IDs, login tokens, passwords, device IDs or IP addresses.
   const progression = {};
   const names = Object.keys(player).filter(x =>
     /class|level|experience|exp|xp|skill/i.test(x) &&
     !/token|secret|password|hash|device|auth|key|ip|account|email/i.test(x)).slice(0,30);
   for (const key of names) {
     const value = player[key];
     if (typeof value === "number" || typeof value === "boolean" ||
         typeof value === "string" && value.length < 180) progression[key] = String(value);
     else if (value && typeof value === "object") {
       const formatted = JSON.stringify(value);
       progression[key] = formatted.slice(0,600);
     }
   }
   finish({ok:true,name:name,inventory:inventory,progression:progression,
     diagnostic:Array.isArray(player.inventory)
       ? "Read-only Player inventory snapshot. Updates made while connected may not be persisted."
       : "No inventory array found on the selected Player record; slot format needs investigation."});
 } catch (e) { finish({ok:false,error:String(e.message || e).slice(0,300)}); }
})().catch(e => console.log("ORBUS_CHARACTER_VIEW:" + JSON.stringify({ok:false,error:String(e.message||e).slice(0,300)})));
""";

    public async Task<ServerCharacterSnapshot> ReadAsync(string serverPath, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new InvalidOperationException("Select a valid character.");
        string folder = ServerHostService.ValidateServerPath(serverPath);
        string output = await _host.ComposeAsync(folder, 29000,
            "exec", "-T", "api", "node", "-e", QueryJs, name);
        string? line = output.Split('\n')
            .LastOrDefault(x => x.StartsWith("ORBUS_CHARACTER_VIEW:", StringComparison.Ordinal));
        if (line == null)
            throw new InvalidOperationException("No character snapshot was returned.");
        using var json = JsonDocument.Parse(line["ORBUS_CHARACTER_VIEW:".Length..]);
        var root = json.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException(root.TryGetProperty("error", out var err)
                ? err.GetString() : "Could not read character inventory.");
        var slots = root.TryGetProperty("inventory", out var inventory) &&
                    inventory.ValueKind == JsonValueKind.Array
            ? inventory.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString() ?? "").ToList() : new List<string>();
        var fields = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("progression", out var prog) &&
            prog.ValueKind == JsonValueKind.Object)
            foreach (var field in prog.EnumerateObject())
                if (field.Value.ValueKind == JsonValueKind.String)
                    fields[field.Name] = field.Value.GetString() ?? "";
        return new ServerCharacterSnapshot(name, slots, fields,
            root.TryGetProperty("diagnostic", out var diag) ? diag.GetString() ?? "" : "");
    }
}
