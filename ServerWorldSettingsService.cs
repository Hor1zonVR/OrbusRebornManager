using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrbusRebornManager;

public sealed record ServerWorldSnapshot(
    string ServerName,
    string Motd,
    bool AllowNewAccounts,
    int MaxCharacters,
    double VeryRareDropChance,
    string FileSha256);

public sealed record ServerWorldEdit(
    string ServerName,
    string Motd,
    bool AllowNewAccounts,
    int MaxCharacters,
    double VeryRareDropChance);

public sealed class ServerWorldSettingsService
{
    private static string ConfigPath(string serverPath) =>
        Path.Combine(ServerHostService.ValidateServerPath(serverPath), "config", "orbus.config.json");

    private static string Fingerprint(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content));

    public ServerWorldSnapshot Load(string serverPath)
    {
        string path = ConfigPath(serverPath);
        if (!File.Exists(path))
            throw new FileNotFoundException("The server config/orbus.config.json file does not exist.", path);
        byte[] source = File.ReadAllBytes(path);
        var root = JsonNode.Parse(source) as JsonObject
            ?? throw new InvalidOperationException("Server configuration is not a JSON object.");
        static string ReadString(JsonObject obj, string key, string fallback) =>
            obj[key] == null ? fallback : obj[key]!.GetValue<string>();
        static int ReadInt(JsonObject obj, string key, int fallback) =>
            obj[key] == null ? fallback : obj[key]!.GetValue<int>();
        static bool ReadBool(JsonObject obj, string key, bool fallback) =>
            obj[key] == null ? fallback : obj[key]!.GetValue<bool>();
        static double ReadDouble(JsonObject obj, string key, double fallback) =>
            obj[key] == null ? fallback : obj[key]!.GetValue<double>();

        return new ServerWorldSnapshot(
            ReadString(root, "serverName", "OrbusVR Community Server"),
            ReadString(root, "motd", ""),
            ReadBool(root, "allowNewAccounts", true),
            ReadInt(root, "maxCharacters", 2),
            ReadDouble(root, "veryRareDropChance", 10),
            Fingerprint(source));
    }

    public string Save(string serverPath, ServerWorldEdit edit, string expectedSha256)
    {
        // Preserve all unrecognised keys (including port / voice configuration).
        // Nothing inside the live character database is edited here.
        if (string.IsNullOrWhiteSpace(edit.ServerName) || edit.ServerName.Length > 90 ||
            edit.ServerName.Any(char.IsControl))
            throw new InvalidOperationException("Server name must be 1–90 characters on one line.");
        if (edit.Motd.Length > 400 || edit.Motd.Any(c => char.IsControl(c) && c != '\n'))
            throw new InvalidOperationException("Welcome message must be no longer than 400 characters.");
        if (edit.MaxCharacters < 1 || edit.MaxCharacters > 20)
            throw new InvalidOperationException("Characters per account must be between 1 and 20.");
        if (double.IsNaN(edit.VeryRareDropChance) || double.IsInfinity(edit.VeryRareDropChance) ||
            edit.VeryRareDropChance < 0 || edit.VeryRareDropChance > 100)
            throw new InvalidOperationException("Rare cosmetic drop chance must be between 0 and 100 percent.");
        string path = ConfigPath(serverPath);
        byte[] oldBytes = File.ReadAllBytes(path);
        if (string.IsNullOrWhiteSpace(expectedSha256) ||
            !Fingerprint(oldBytes).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The server configuration changed since it was opened. Refresh World settings before saving.");
        var root = JsonNode.Parse(oldBytes) as JsonObject
            ?? throw new InvalidOperationException("Server configuration is not a JSON object.");
        root["serverName"] = edit.ServerName.Trim();
        root["motd"] = edit.Motd;
        root["allowNewAccounts"] = edit.AllowNewAccounts;
        root["maxCharacters"] = edit.MaxCharacters;
        root["veryRareDropChance"] = edit.VeryRareDropChance;

        string folder = ServerHostService.ValidateServerPath(serverPath);
        string backupDir = Path.Combine(folder, ".orbusmanager", "backups", "world-settings");
        Directory.CreateDirectory(backupDir);
        string backup = Path.Combine(backupDir,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllBytes(backup, oldBytes);
        string newText = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        string tempPath = Path.Combine(Path.GetDirectoryName(path)!,
            ".orbusmanager-config-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(tempPath, newText, new UTF8Encoding(false));
            File.Replace(tempPath, path, null);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        return backup;
    }
}
