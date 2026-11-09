using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OrbusRebornManager;

/// <summary>
/// Edits only a selected class level on an exactly named, unique saved character.
/// Uses a compare-and-set database update so a stale UI cannot overwrite newer data.
/// This is NOT an item enhancement/affix writer.
/// </summary>
public sealed class ServerCharacterLevelEditor
{
    private readonly ServerHostService _host = new();
    private const string Script = """
(async function () {
 const marker = "ORBUS_LEVEL_EDIT:";
 var done = false;
 var timeout = setTimeout(function () {
   output({ok:false,error:"Level edit timed out. Refresh the character before retrying."});
 }, 16000);
 function output(data) {
   if (done) return;
   done = true; clearTimeout(timeout);
   process.stdout.write(marker + JSON.stringify(data) + "\n", function () { process.exit(0); });
 }
 try {
   var name = process.argv[1], key = process.argv[2];
   var expected = Number(process.argv[3]), next = Number(process.argv[4]);
   if (!name || name.length > 80 || !/^[a-zA-Z_][a-zA-Z0-9_]{0,49}$/.test(key) ||
       !Number.isInteger(expected) || expected < 0 || expected > 100 ||
       !Number.isInteger(next) || next < 1 || next > 100) {
     output({ok:false,error:"Invalid character/class/level input."}); return;
   }
   var r, run, host = process.env.ORBUS_DB_HOST || "rethinkdb";
   try {
     r = require("rethinkdbdash")({servers:[{host:host,port:28015}],timeout:10,buffer:10,silent:true});
     run = async function(q) { var v = await q.run(); return v && typeof v.toArray === "function" ? await v.toArray() : v; };
   } catch(e) {
     r = require("rethinkdb");
     var connection = await r.connect({host:host,port:28015,timeout:10});
     run = async function(q) { var v = await q.run(connection); return v && typeof v.toArray === "function" ? await v.toArray() : v; };
   }
   var table = r.db("orbus").table("Player");
   var matches = await run(table.filter({name:name}).limit(2));
   if (!Array.isArray(matches) || matches.length !== 1) {
     output({ok:false,error:"Expected exactly one matching saved Player record."}); return;
   }
   var player = matches[0], levels = player.skill_level;
   if (!levels || typeof levels !== "object" || Array.isArray(levels) ||
       !Object.prototype.hasOwnProperty.call(levels,key) ||
       levels[key] !== expected || player.id === undefined) {
     output({ok:false,error:"Saved class level changed or the selected class was not found. Refresh before retrying."});
     return;
   }
   // The guard runs atomically inside RethinkDB; never replace the whole player document.
   var update = await run(table.get(player.id).update(function(row) {
     return r.branch(
       row("name").eq(name).and(row("skill_level").default({}).getField(key).default(-1).eq(expected)),
       {skill_level:row("skill_level").default({}).merge(r.object(key,next))},
       {});
   }));
   if (!update || update.replaced !== 1) {
     output({ok:false,error:"No record was modified. The saved value may have changed."}); return;
   }
   var verified = await run(table.get(player.id).pluck("name","skill_level"));
   if (!verified || verified.name !== name || !verified.skill_level ||
       verified.skill_level[key] !== next) {
     output({ok:false,error:"The update returned but read-back did not verify the new level."}); return;
   }
   output({ok:true,level:verified.skill_level[key]});
 } catch(e) { output({ok:false,error:String(e.message || e).slice(0,300)}); }
})().catch(function(e) { console.log("ORBUS_LEVEL_EDIT:" + JSON.stringify({ok:false,error:String(e.message || e).slice(0,300)})); });
""";

    public async Task EditAsync(string serverPath, string name, string classKey,
        int expectedLevel, int newLevel)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new InvalidOperationException("Select a valid character.");
        if (string.IsNullOrWhiteSpace(classKey) || classKey.Length > 50 ||
            !System.Text.RegularExpressions.Regex.IsMatch(classKey, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            throw new InvalidOperationException("Invalid saved class key.");
        if (expectedLevel < 0 || expectedLevel > 100 || newLevel < 1 || newLevel > 100)
            throw new InvalidOperationException("Level must be within 1–100.");
        string folder = ServerHostService.ValidateServerPath(serverPath);
        string output = await _host.ComposeAsync(folder, 27000,
            "exec", "-T", "api", "node", "-e", Script,
            name, classKey, expectedLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
            newLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string? line = output.Split('\n').LastOrDefault(x =>
            x.StartsWith("ORBUS_LEVEL_EDIT:", StringComparison.Ordinal));
        if (line == null) throw new InvalidOperationException("No level-edit response from the server.");
        using var document = JsonDocument.Parse(line["ORBUS_LEVEL_EDIT:".Length..]);
        var result = document.RootElement;
        if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(result.TryGetProperty("error", out var err)
                ? err.GetString() : "Level edit was not verified.");
    }
}
