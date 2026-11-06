using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace OrbusRebornManager;

public sealed record ServerComponentRow(string FileName, bool Enabled)
{
    public string Status => Enabled ? "Enabled · server restart needed after changes" : "Disabled";
    public string ActionLabel => Enabled ? "Disable" : "Enable";
}

public sealed class ServerComponentService
{
    private const int MaxComponentBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static string Active(string server) => Path.Combine(ServerHostService.ValidateServerPath(server), "mods", "components");
    private static string Inactive(string server) => Path.Combine(ServerHostService.ValidateServerPath(server), ".orbusmanager", "disabled-components");
    private static string RecordsPath(string server) => Path.Combine(ServerHostService.ValidateServerPath(server), ".orbusmanager", "installed-components.json");
    private static string SafeName(string path)
    {
        string name = Path.GetFileName(path);
        if (!name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            name.Length == 0 || name != path && (path.Contains('/') || path.Contains('\\')))
            throw new InvalidOperationException("Only a single .js server component file is supported.");
        return name;
    }

    public List<ServerComponentRow> List(string server)
    {
        string active = Active(server), inactive = Inactive(server);
        var entries = new List<ServerComponentRow>();
        if (Directory.Exists(active))
            entries.AddRange(Directory.GetFiles(active, "*.js", SearchOption.TopDirectoryOnly)
                .Select(p => new ServerComponentRow(Path.GetFileName(p), true)));
        if (Directory.Exists(inactive))
            entries.AddRange(Directory.GetFiles(inactive, "*.js", SearchOption.TopDirectoryOnly)
                .Select(p => new ServerComponentRow(Path.GetFileName(p), false)));
        return entries.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Import(string server, string source)
    {
        string file = SafeName(Path.GetFileName(source));
        if (new FileInfo(source).Length is <= 0 or > MaxComponentBytes)
            throw new InvalidOperationException("Component file must be between 1 byte and 2 MB.");
        string folder = Active(server);
        Directory.CreateDirectory(folder);
        if (File.Exists(Path.Combine(folder, file)) || File.Exists(Path.Combine(Inactive(server), file)))
            throw new InvalidOperationException("A component with that filename is already installed. Nothing was overwritten.");
        File.Copy(source, Path.Combine(folder, file), overwrite: false);
    }

    public void Toggle(string server, ServerComponentRow component)
    {
        string file = SafeName(component.FileName);
        string source = Path.Combine(component.Enabled ? Active(server) : Inactive(server), file);
        string destinationFolder = component.Enabled ? Inactive(server) : Active(server);
        if (!File.Exists(source)) throw new FileNotFoundException("Component file was moved or deleted.", source);
        Directory.CreateDirectory(destinationFolder);
        string destination = Path.Combine(destinationFolder, file);
        if (File.Exists(destination)) throw new InvalidOperationException("A file with the same name already exists at the destination.");
        File.Move(source, destination);
    }

    private sealed class InstalledRecord
    {
        public string Id { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Version { get; set; } = "";
    }

    // Curated server releases are a single .js component. ZIP packages and server binaries
    // require a separate reviewed manifest/installer and are NOT executed by this manager.
    public async Task<string> InstallCuratedAsync(string server, ModDefinition mod, ManagerService releases)
    {
        if (!string.Equals(mod.Target, "server", StringComparison.OrdinalIgnoreCase) ||
            !mod.AssetPattern.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This is not a reviewed single-file server component.");
        var (release, asset, stale) = await releases.GetLatestAsync(mod);
        if (stale) throw new InvalidOperationException("GitHub release data is stale. Retry when online.");
        string file = SafeName(asset.Name);
        if (asset.Size is <= 0 or > MaxComponentBytes)
            throw new InvalidOperationException("Server component exceeds 2 MB.");
        string root = ServerHostService.ValidateServerPath(server);
        string recordsPath = RecordsPath(root);
        var records = File.Exists(recordsPath)
            ? JsonSerializer.Deserialize<List<InstalledRecord>>(await File.ReadAllTextAsync(recordsPath)) ?? new()
            : new List<InstalledRecord>();
        var current = records.FirstOrDefault(x => x.Id == mod.Id);
        if (current != null && !current.FileName.Equals(file, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The published component changed filenames. Remove the old version manually first.");
        string active = Path.Combine(Active(root), file);
        string disabled = Path.Combine(Inactive(root), file);
        if (current == null && (File.Exists(active) || File.Exists(disabled)))
            throw new InvalidOperationException("A manually installed component already uses that name; won't overwrite it.");
        if (current != null && !File.Exists(active) && !File.Exists(disabled))
            throw new InvalidOperationException("The tracked component is missing. Reconcile the files before updating.");
        byte[] content = await Client.GetByteArrayAsync(asset.DownloadUrl);
        if (content.Length is <= 0 or > MaxComponentBytes)
            throw new InvalidOperationException("Downloaded component exceeds 2 MB.");
        if (asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true &&
            !Convert.ToHexString(SHA256.HashData(content)).Equals(asset.Digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SHA-256 mismatch in GitHub release asset.");
        bool enabled = File.Exists(active) || current == null;
        string destination = enabled ? active : disabled;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string tmp = destination + ".orbusmanager-new";
        if (File.Exists(tmp)) File.Delete(tmp);
        try
        {
            await File.WriteAllBytesAsync(tmp, content);
            // Updates keep a backup, even when the component was disabled.
            if (File.Exists(destination))
            {
                string backup = Path.Combine(root, ".orbusmanager", "backups", mod.Id,
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + file);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(destination, backup, overwrite: false);
            }
            File.Move(tmp, destination, overwrite: current != null);
            if (current == null) records.Add(new InstalledRecord { Id = mod.Id, FileName = file, Version = release.TagName });
            else current.Version = release.TagName;
            Directory.CreateDirectory(Path.GetDirectoryName(recordsPath)!);
            await File.WriteAllTextAsync(recordsPath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        return mod.Name + " " + release.TagName + " installed. Restart the game service to apply it.";
    }
}
