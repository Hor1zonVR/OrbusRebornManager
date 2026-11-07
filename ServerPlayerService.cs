using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OrbusRebornManager;

// Only character names are read and cached. No identity keys, login tokens,
// account hashes, inventories, or arbitrary database records leave the server.
public sealed record ServerPlayerEntry(string Name, bool? Online)
{
    public string PresenceText => Online == true ? "● Online" :
        Online == false ? "○ Offline" : "— Presence unknown";
    public string StatusHint => Online == null ? "No verified live player-name source" :
        Online == true ? "Connected now" : "Not connected now";
}
public sealed record ServerItemEntry(int Id, string Name)
{
    public string Label => $"#{Id} · {Name}";
}
public sealed class ServerPlayerDirectoryResult
{
    public List<ServerPlayerEntry> Players { get; init; } = new();
    public string HistorySource { get; init; } = "";
    public string PresenceSource { get; init; } = "";
    public string Diagnostic { get; init; } = "";
    public bool PresenceVerified { get; init; }
}
public sealed class ServerPlayerService
{
    private readonly ServerHostService _host = new();
    private readonly string _cacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OrbusRebornManager", "server-player-history");

    // Runs in the already-installed server's Node.js process and only uses read queries.
    // It never queries arbitrary account identity fields, serializes complete DB rows,
    // updates RethinkDB, or assumes that a database field named "online" is live.
    // Driver variants reflect different Community Server image dependencies.
    private const string CharacterQueryJs = """
(async () => {
 const prefix = "ORBUS_MANAGER_PLAYERS:";
 const emit = obj => console.log(prefix + JSON.stringify(obj));
 try {
   let r, run, close = async () => {};
   const host = process.env.ORBUS_DB_HOST || "rethinkdb";
   try {
     const factory = require("rethinkdbdash");
     r = factory({servers:[{host,port:28015}], timeout:10, buffer:10, silent:true});
     run = async q => {
       let value = await q.run();
       return value && typeof value.toArray === "function" ? await value.toArray() : value;
     };
   } catch (e) {
     try {
       r = require("rethinkdb");
       const connection = await r.connect({host, port:28015, timeout:10});
       run = async q => {
         let value = await q.run(connection);
         return value && typeof value.toArray === "function" ? await value.toArray() : value;
       };
       close = async () => connection.close();
     } catch (inner) {
       emit({ok:false,error:"RethinkDB Node driver unavailable in server container. Read-only player discovery requires a compatible driver."});
       return;
     }
   }
   const keyNames = ["characterName","charactername","character_name","charName","charname","playerName","playername","player_name","name"];
   function pickName(record, table) {
     if (!record || typeof record !== "object") return null;
     const likely = /char|player|avatar/i.test(table);
     for (const key of keyNames) {
       if (key === "name" && !likely) continue;
       const val = record[key];
       if (typeof val === "string" && val.trim().length > 0 && val.trim().length <= 80)
         return val.trim();
     }
     for (const key of ["character","player","profile","data"]) {
       const child = record[key];
       if (child && typeof child === "object" && !Array.isArray(child)) {
         for (const attr of keyNames) {
           if (typeof child[attr] === "string" && child[attr].trim().length > 0 &&
               child[attr].trim().length <= 80) return child[attr].trim();
         }
       }
     }
     return null;
   }
   function namesFrom(row, table) {
     const names = [];
     const direct = pickName(row, table);
     if (direct) names.push(direct);
     for (const key of ["characters","chars","avatars"]) {
       if (Array.isArray(row?.[key])) {
         for (const item of row[key]) {
           const name = pickName(item, "characters");
           if (name) names.push(name);
         }
       }
     }
     return names;
   }
   const databases = (await run(r.dbList())).filter(x => x !== "rethinkdb").slice(0,12);
   const candidates = [];
   const examined = [];
   for (const db of databases) {
     const tables = (await run(r.db(db).tableList())).filter(t => /char|player|account|avatar|user/i.test(t)).slice(0,50);
     for (const table of tables) {
       try {
         const sample = await run(r.db(db).table(table).limit(3));
         const records = Array.isArray(sample) ? sample : [];
         const names = records.flatMap(row => namesFrom(row, table));
         examined.push(db + "." + table);
         if (names.length) candidates.push({db,table,score: names.length * 4 + (/char/i.test(table) ? 25 : /player/i.test(table) ? 12 : 0)});
       } catch { /* An unrelated table might not be accessible; skip it. */ }
     }
   }
   candidates.sort((a,b) => b.score - a.score);
   const selected = candidates.slice(0,5);
   const set = new Set();
   for (const item of selected) {
     const records = await run(r.db(item.db).table(item.table).limit(12000));
     for (const row of (Array.isArray(records) ? records : [])) {
       for (const name of namesFrom(row, item.table)) set.add(name);
     }
   }
   await close();
   emit({ok: selected.length > 0, names:[...set].sort().slice(0,20000),
     source:selected.map(x => x.db+"."+x.table).join(", "),
     diagnostic:selected.length ? "" : "Could not identify a character table automatically. Candidate tables examined: " + examined.join(", ")});
 } catch (e) {
   emit({ok:false,error:String(e.message || e).slice(0,320)});
 }
})().catch(e => console.log("ORBUS_MANAGER_PLAYERS:" + JSON.stringify({ok:false,error:String(e.message||e).slice(0,300)})));
""";

    public List<ServerItemEntry> ReadItems(string serverPath)
    {
        string path = Path.Combine(ServerHostService.ValidateServerPath(serverPath),
            "content", "items", "item-serverdata.txt");
        if (!File.Exists(path)) throw new FileNotFoundException("Orbus item catalogue not found", path);
        var result = new List<ServerItemEntry>();
        using var parser = new TextFieldParser(path, Encoding.UTF8);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        parser.TrimWhiteSpace = false;
        string[]? header = parser.ReadFields();
        if (header == null) return result;
        int idIndex = Array.FindIndex(header, s => s.Equals("id", StringComparison.OrdinalIgnoreCase));
        int nameIndex = Array.FindIndex(header, s => s.Equals("name", StringComparison.OrdinalIgnoreCase));
        if (idIndex < 0 || nameIndex < 0) throw new InvalidOperationException("Unknown item catalogue format.");
        while (!parser.EndOfData && result.Count < 60000)
        {
            string[]? fields = parser.ReadFields();
            if (fields == null || fields.Length <= Math.Max(idIndex, nameIndex)) continue;
            if (int.TryParse(fields[idIndex], out int id) && id > 1 &&
                !string.IsNullOrWhiteSpace(fields[nameIndex]))
                result.Add(new ServerItemEntry(id, fields[nameIndex]));
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ServerPlayerDirectoryResult> LoadAsync(string serverPath)
    {
        string folder = ServerHostService.ValidateServerPath(serverPath);
        string cachePath = GetCachePath(folder);
        var history = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(cachePath))
                foreach (var name in JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(cachePath)) ?? new())
                    if (NameAllowed(name)) history.Add(name);
        }
        catch { /* History is supplemental, not authoritative. */ }

        string historySource = history.Count > 0 ? "Previous local snapshots" : "No previous snapshots";
        var diagnostics = new List<string>();
        try
        {
            string output = await _host.ComposeAsync(folder, 35000,
                "exec", "-T", "api", "node", "-e", CharacterQueryJs);
            string? data = output.Split('\n').Reverse()
                .FirstOrDefault(line => line.StartsWith("ORBUS_MANAGER_PLAYERS:", StringComparison.Ordinal));
            if (data == null) throw new InvalidOperationException("No player data returned by the server bridge.");
            using var doc = JsonDocument.Parse(data["ORBUS_MANAGER_PLAYERS:".Length..]);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okField) && okField.GetBoolean();
            if (!ok)
                throw new InvalidOperationException(root.TryGetProperty("error", out var e) ? e.GetString() :
                    root.TryGetProperty("diagnostic", out var d) ? d.GetString() :
                    "Character table was not recognised.");
            if (root.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
                foreach (var item in names.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && NameAllowed(item.GetString()))
                        history.Add(item.GetString()!);
                }
            historySource = root.TryGetProperty("source", out var source) ? "RethinkDB: " + source.GetString() : "RethinkDB";
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string temp = cachePath + ".new";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(history.OrderBy(x => x).ToArray()));
            File.Move(temp, cachePath, true);
        }
        catch (Exception e) { diagnostics.Add("Character history: " + e.Message); }

        // Only a specifically named live-list array is treated as online presence.
        // The documented /serverinfo may not include character names: in that case
        // presence is UNKNOWN, not falsely reported Offline.
        HashSet<string>? online = null;
        string presenceSource = "Live named-player list not exposed by this server version";
        try
        {
            string response = await _host.ComposeAsync(folder, 14000,
                "exec", "-T", "api", "curl", "-fsS", "--max-time", "9",
                "http://localhost:3000/serverinfo");
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] {"onlinePlayers", "playersOnline", "connectedPlayers", "online_players"})
                {
                    if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool valid = true;
                    foreach (var item in arr.EnumerateArray())
                    {
                        string? name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                        if (item.ValueKind == JsonValueKind.Object)
                            foreach (string field in new[] {"characterName","name","playerName"})
                                if (item.TryGetProperty(field, out var value) &&
                                    value.ValueKind == JsonValueKind.String)
                                { name = value.GetString(); break; }
                        if (!NameAllowed(name)) { valid = false; break; }
                        names.Add(name!);
                    }
                    if (!valid) continue;
                    online = names;
                    foreach (string name in names) history.Add(name);
                    presenceSource = "Live API: /serverinfo." + key;
                    break;
                }
            }
        }
        catch (Exception e) { diagnostics.Add("Live presence: " + e.Message); }
        return new ServerPlayerDirectoryResult
        {
            Players = history.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(name => new ServerPlayerEntry(name, online == null ? null : online.Contains(name))).ToList(),
            HistorySource = historySource,
            PresenceSource = presenceSource,
            PresenceVerified = online != null,
            Diagnostic = string.Join("\n", diagnostics)
        };
    }

    public async Task<string> GiveItemAsync(string serverPath, string characterName, int itemId)
    {
        if (!NameAllowed(characterName)) throw new InvalidOperationException("Select a valid saved character.");
        if (!ReadItems(serverPath).Any(i => i.Id == itemId))
            throw new InvalidOperationException("Select a valid item from this server's catalogue.");
        string url = "http://localhost:3000/admin/updateinventory/" +
            Uri.EscapeDataString(characterName) + "/" + itemId;
        string result = await _host.ComposeAsync(serverPath, 20000,
            "exec", "-T", "api", "curl", "-fsS", "--max-time", "12", url);
        // A successful command indicates the admin endpoint replied; in-game delivery
        // must still be confirmed. Never call a success response "item received".
        return result.Length > 1000 ? result[..1000] : result;
    }

    private string GetCachePath(string folder)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToUpperInvariant()));
        return Path.Combine(_cacheRoot, Convert.ToHexString(hash)[..24] + ".json");
    }

    private static bool NameAllowed(string? name) =>
        name != null && name.Length is > 0 and <= 80 &&
        name.Trim() == name && !name.Any(char.IsControl) &&
        !name.Contains('/') && !name.Contains('\\') &&
        Regex.IsMatch(name, @"[\p{L}\p{N}]");
}
