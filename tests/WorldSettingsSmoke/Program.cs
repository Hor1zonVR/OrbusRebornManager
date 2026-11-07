using System;
using System.IO;
using System.Text.Json.Nodes;
using OrbusRebornManager;

string folder = Path.Combine(Path.GetTempPath(), "OrbusWorldSmoke-" + Guid.NewGuid().ToString("N"));
string configPath = Path.Combine(folder, "config", "orbus.config.json");
try
{
    Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
    Directory.CreateDirectory(Path.Combine(folder, "mods"));
    Directory.CreateDirectory(Path.Combine(folder, "content"));
    File.WriteAllText(Path.Combine(folder, "docker-compose.yml"), "services: {}");
    string original = """
{
  "serverName": "Original",
  "motd": "Welcome",
  "maxCharacters": 2,
  "allowNewAccounts": true,
  "veryRareDropChance": 10,
  "adminIps": ["127.0.0.1"],
  "vivox": { "key": "unmodified" },
  "customFutureOption": { "nested": true }
}
""";
    File.WriteAllText(configPath, original);
    var service = new ServerWorldSettingsService();
    var snapshot = service.Load(folder);
    if (snapshot.ServerName != "Original" || snapshot.MaxCharacters != 2)
        throw new Exception("Load did not preserve the original settings");

    string backup = service.Save(folder,
        new ServerWorldEdit("World Test", "Changed message", false, 4, 15),
        snapshot.FileSha256);
    if (!File.Exists(backup) || File.ReadAllText(backup) != original)
        throw new Exception("Backup missing or does not exactly match original");

    var result = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
    if (result["serverName"]!.GetValue<string>() != "World Test" ||
        result["maxCharacters"]!.GetValue<int>() != 4 ||
        result["allowNewAccounts"]!.GetValue<bool>() ||
        result["veryRareDropChance"]!.GetValue<int>() != 15 ||
        result["vivox"]!["key"]!.GetValue<string>() != "unmodified" ||
        result["customFutureOption"]!["nested"]!.GetValue<bool>() != true)
        throw new Exception("Modified values incorrect or unrelated config keys lost");

    bool refusedStale = false;
    try
    {
        service.Save(folder,
            new ServerWorldEdit("Stale", "No", false, 2, 50),
            snapshot.FileSha256);
    }
    catch (InvalidOperationException) { refusedStale = true; }
    if (!refusedStale) throw new Exception("Stale editor write was accepted");

    bool refusedBadValue = false;
    try
    {
        var latest = service.Load(folder);
        service.Save(folder,
            new ServerWorldEdit("Bad", "", false, 0, 110),
            latest.FileSha256);
    }
    catch (InvalidOperationException) { refusedBadValue = true; }
    if (!refusedBadValue) throw new Exception("Unsafe world config values accepted");

    Console.WriteLine("PASS: World settings backups, unrelated keys, validation and stale-edit checks.");
}
finally
{
    if (Directory.Exists(folder)) Directory.Delete(folder, true);
}

namespace OrbusRebornManager
{
    // Only this stub is used to test the service outside the Windows WPF app.
    public sealed class ServerHostService
    {
        public static string ValidateServerPath(string path)
        {
            string full = Path.GetFullPath(path);
            foreach (string name in new[] { "docker-compose.yml", "mods", "content" })
            {
                string check = Path.Combine(full, name);
                if (!File.Exists(check) && !Directory.Exists(check))
                    throw new InvalidOperationException("Invalid test server directory");
            }
            return full;
        }
    }
}
