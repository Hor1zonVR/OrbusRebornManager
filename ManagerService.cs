using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrbusRebornManager;

public sealed class ManagerService
{
    public const string LoaderVersion = "6.0.0-be.788";
    public const string LoaderUrl = "https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip";
    private const long MaxLoaderBytes = 160L * 1024 * 1024;
    private const long MaxModBytes = 100L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static readonly HttpClient Client = CreateClient();
    public readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrbusRebornManager");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrbusRebornManager/0.1 (+https://github.com)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public ManagerSettings LoadSettings()
    {
        Directory.CreateDirectory(DataRoot);
        string file = Path.Combine(DataRoot, "settings.json");
        if (!File.Exists(file)) return new ManagerSettings();
        try { return JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(file), Json) ?? new ManagerSettings(); }
        catch { return new ManagerSettings(); }
    }
    public void SaveSettings(ManagerSettings settings) => SaveJson(Path.Combine(DataRoot, "settings.json"), settings);

    public string NormalizeGamePath(string selection)
    {
        string path = selection.Trim().Trim('"');
        if (File.Exists(path) && Path.GetFileName(path).Equals("vrclient.exe", StringComparison.OrdinalIgnoreCase))
            path = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(path)) throw new InvalidOperationException("Choose the folder containing vrclient.exe.");
        path = Path.GetFullPath(path);
        if (!File.Exists(Path.Combine(path, "vrclient.exe")) ||
            !File.Exists(Path.Combine(path, "GameAssembly.dll")) ||
            !Directory.Exists(Path.Combine(path, "vrclient_Data")))
            throw new InvalidOperationException("This doesn't look like OrbusVR Reborn Community Edition. Select its game folder containing vrclient.exe, GameAssembly.dll and vrclient_Data.");
        return path;
    }

    public bool IsBepInExInstalled(string game) =>
        File.Exists(Path.Combine(game, "winhttp.dll")) &&
        File.Exists(Path.Combine(game, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll"));

    public bool IsGameRunning()
    {
        try { return Process.GetProcessesByName("vrclient").Length > 0; }
        catch { return false; }
    }

    public string GameManagerRoot(string game) => Path.Combine(game, "OrbusRebornManager");
    private string StatePath(string game)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(game).ToUpperInvariant()));
        return Path.Combine(DataRoot, "instances", Convert.ToHexString(hash).Substring(0, 16) + ".json");
    }
    public InstanceDocument LoadInstalled(string game)
    {
        string path = StatePath(game);
        if (!File.Exists(path)) return new InstanceDocument();
        try { return JsonSerializer.Deserialize<InstanceDocument>(File.ReadAllText(path), Json) ?? new InstanceDocument(); }
        catch (Exception ex) { throw new InvalidOperationException("Could not read installed-mod database: " + ex.Message); }
    }
    private void SaveInstalled(string game, InstanceDocument installed) => SaveJson(StatePath(game), installed);

    private static void SaveJson<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string tmp = file + ".new";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, file, true);
    }

    private static CatalogDocument ReadCatalog(string content)
    {
        CatalogDocument doc = JsonSerializer.Deserialize<CatalogDocument>(content, Json)
            ?? throw new InvalidOperationException("Empty catalogue response.");
        if (doc.SchemaVersion != 1) throw new InvalidOperationException("Unsupported mod catalogue schema.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in doc.Mods)
        {
            CheckId(mod.Id);
            if (!ids.Add(mod.Id)) throw new InvalidOperationException("Duplicate catalogue ID: " + mod.Id);
            if (string.IsNullOrWhiteSpace(mod.Name)) throw new InvalidOperationException("Unnamed mod: " + mod.Id);
            GetRepoParts(mod.Repository);
            if (string.IsNullOrWhiteSpace(mod.AssetPattern) ||
                (!mod.AssetPattern.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                 !mod.AssetPattern.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Mod asset pattern must end in .dll or .zip: " + mod.Id);
        }
        return doc;
    }

    public async Task<(CatalogDocument Catalog, string Status)> FetchCatalogAsync(string url)
    {
        CatalogDocument embedded;
        using (var stream = typeof(ManagerService).Assembly.GetManifestResourceStream("OrbusRebornManager.Catalog.json")
            ?? throw new InvalidOperationException("Bundled catalogue missing."))
        using (var reader = new StreamReader(stream)) embedded = ReadCatalog(await reader.ReadToEndAsync());

        var merged = new CatalogDocument();
        foreach (var m in embedded.Mods) merged.Mods.Add(m);
        string source = "Bundled catalogue";
        if (!string.IsNullOrWhiteSpace(url))
        {
            Uri address = new(url.Trim());
            if (address.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Catalogue URL must use HTTPS.");
            try
            {
                string remote = await Client.GetStringAsync(address);
                var catalog = ReadCatalog(remote);
                SaveJson(Path.Combine(DataRoot, "catalog-cache.json"), catalog);
                merged = catalog;
                source = "Online catalogue";
            }
            catch (Exception ex)
            {
                string cached = Path.Combine(DataRoot, "catalog-cache.json");
                if (!File.Exists(cached)) throw new InvalidOperationException("Catalogue download failed: " + ex.Message, ex);
                merged = ReadCatalog(await File.ReadAllTextAsync(cached));
                source = "Offline cache (network unavailable)";
            }
        }
        // Local drafts are explicitly not published to other users.
        string draft = Path.Combine(DataRoot, "catalog-local.json");
        if (File.Exists(draft))
        {
            try
            {
                var local = ReadCatalog(await File.ReadAllTextAsync(draft));
                foreach (var mod in local.Mods)
                {
                    merged.Mods.RemoveAll(x => x.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase));
                    merged.Mods.Add(mod);
                }
                source += " + local drafts";
            }
            catch { source += " (invalid local drafts skipped)"; }
        }
        ReadCatalog(JsonSerializer.Serialize(merged)); // Re-validate duplicates after merging.
        return (merged, source);
    }

    public void AddLocalDraft(string repoUrl, string displayName, string author, string assetPattern)
    {
        var parts = GetRepoParts(repoUrl);
        string id = Regex.Replace(parts.Owner + "-" + parts.Repository, "[^A-Za-z0-9_-]", "-").ToLowerInvariant();
        CheckId(id);
        string localPath = Path.Combine(DataRoot, "catalog-local.json");
        CatalogDocument document = File.Exists(localPath)
            ? ReadCatalog(File.ReadAllText(localPath)) : new CatalogDocument();
        document.Mods.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        document.Mods.Add(new ModDefinition
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(displayName) ? parts.Repository : displayName.Trim(),
            Author = string.IsNullOrWhiteSpace(author) ? parts.Owner : author.Trim(),
            Description = "Community mod for OrbusVR Reborn.",
            Repository = $"https://github.com/{parts.Owner}/{parts.Repository}",
            AssetPattern = assetPattern.Trim()
        });
        ReadCatalog(JsonSerializer.Serialize(document));
        SaveJson(localPath, document);
    }

    public void ExportLocalCatalog(string destination, CatalogDocument catalog) => SaveJson(destination, catalog);

    private static void CheckId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,60}$"))
            throw new InvalidOperationException("Invalid mod ID: " + id);
    }

    public static (string Owner, string Repository) GetRepoParts(string repo)
    {
        if (!Uri.TryCreate(repo, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != "https" || uri.Host != "github.com")
            throw new InvalidOperationException("Only public HTTPS GitHub repositories are supported.");
        string[] parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2) throw new InvalidOperationException("Use a GitHub repository link, e.g. https://github.com/owner/repo");
        string owner = parts[0];
        string name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (!Regex.IsMatch(owner, "^[A-Za-z0-9-]{1,39}$") || !Regex.IsMatch(name, "^[A-Za-z0-9._-]{1,100}$"))
            throw new InvalidOperationException("Invalid GitHub owner or repo name.");
        return (owner, name);
    }

    public async Task<(GitHubRelease Release, GitHubAsset Asset)> GetLatestAsync(ModDefinition mod)
    {
        var repo = GetRepoParts(mod.Repository);
        string address = $"https://api.github.com/repos/{repo.Owner}/{repo.Repository}/releases/latest";
        using var response = await Client.GetAsync(address);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("No published stable GitHub Release found. Upload a .dll or .zip release asset first.");
        response.EnsureSuccessStatusCode();
        var release = JsonSerializer.Deserialize<GitHubRelease>(await response.Content.ReadAsStringAsync(), Json)
            ?? throw new InvalidOperationException("Invalid GitHub release.");
        if (release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.TagName))
            throw new InvalidOperationException("Only published stable releases are available.");
        var matches = release.Assets.Where(a =>
            (a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) &&
            GlobMatches(mod.AssetPattern, a.Name)).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException($"Release {release.TagName} has {matches.Count} assets matching '{mod.AssetPattern}'. A curator must choose a unique asset pattern.");
        var asset = matches[0];
        if (asset.Size <= 0 || asset.Size > MaxModBytes) throw new InvalidOperationException("Mod asset exceeds maximum supported size (100 MB).");
        var download = new Uri(asset.DownloadUrl);
        if (download.Scheme != "https" || download.Host != "github.com" ||
            !download.AbsolutePath.StartsWith($"/{repo.Owner}/{repo.Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected GitHub release download URL.");
        return (release, asset);
    }

    /// <summary>
    /// Resolve the exact version exported in an .orbuspack. Never silently
    /// substitute /releases/latest for a missing or unpublished version.
    /// Only call with a trusted catalogue ModDefinition (not the untrusted
    /// manifest repository) after matching the approved source.
    /// </summary>
    public async Task<(GitHubRelease Release, GitHubAsset Asset)> GetPinnedReleaseAsync(
        ModDefinition mod, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) ||
            tag.Length > 80 ||
            !Regex.IsMatch(tag, @"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,79}$"))
            throw new InvalidOperationException("Invalid pinned mod version.");

        var repo = GetRepoParts(mod.Repository);
        string address = $"https://api.github.com/repos/{repo.Owner}/{repo.Repository}/releases/tags/{Uri.EscapeDataString(tag)}";
        using var response = await Client.GetAsync(address);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Version " + tag +
                " is no longer published as a stable GitHub release.");

        response.EnsureSuccessStatusCode();

        var release = JsonSerializer.Deserialize<GitHubRelease>(
            await response.Content.ReadAsStringAsync(), Json)
            ?? throw new InvalidOperationException("Invalid GitHub release.");

        if (release.Draft || release.Prerelease ||
            !release.TagName.Equals(tag, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Requested version isn't a matching published stable release.");

        var matches = release.Assets.Where(a =>
            (a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
             a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) &&
            GlobMatches(mod.AssetPattern, a.Name)).ToList();

        if (matches.Count != 1)
            throw new InvalidOperationException(
                "Version " + tag + " must contain one approved DLL or ZIP asset.");

        var asset = matches[0];
        if (asset.Size <= 0 || asset.Size > MaxModBytes)
            throw new InvalidOperationException(
                "Pinned mod release exceeds the 100 MB limit.");

        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var download) ||
            download.Scheme != "https" || download.Host != "github.com" ||
            !download.AbsolutePath.StartsWith(
                $"/{repo.Owner}/{repo.Repository}/releases/download/",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Unexpected GitHub release download URL.");

        return (release, asset);
    }

    private static bool GlobMatches(string glob, string filename)
    {
        string expression = "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(filename, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static async Task DownloadAsync(string url, string destination, long limit, string? digest = null)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long size && size > limit)
            throw new InvalidOperationException("Download is larger than the allowed limit.");
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(destination);
        using var sha = SHA256.Create();
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int bytes;
        while ((bytes = await input.ReadAsync(buffer)) > 0)
        {
            total += bytes;
            if (total > limit) throw new InvalidOperationException("Download exceeds the allowed limit.");
            await output.WriteAsync(buffer.AsMemory(0, bytes));
            sha.TransformBlock(buffer, 0, bytes, buffer, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        await output.FlushAsync();
        if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            string actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SHA-256 mismatch: downloaded release asset failed integrity verification.");
        }
    }

    private static string SafeRelative(string relative)
    {
        string path = relative.Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || path.Contains(':') || path.Split('/').Any(p => p is ".." or "." or ""))
            throw new InvalidOperationException("Unsafe path inside ZIP archive.");
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool IsSymlink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static void CopyZipEntry(ZipArchiveEntry entry, string destRoot, string relative)
    {
        if (IsSymlink(entry)) throw new InvalidOperationException("Archive contains a symbolic link.");
        string safe = SafeRelative(relative);
        string full = Path.GetFullPath(Path.Combine(destRoot, safe));
        if (!full.StartsWith(Path.GetFullPath(destRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Archive tried to write outside the install directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        entry.ExtractToFile(full, true);
    }

    public async Task InstallLoaderAsync(string game, Action<string>? log = null)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Close OrbusVR before installing BepInEx.");
        if (IsBepInExInstalled(game)) throw new InvalidOperationException("BepInEx is already present; leaving this installation untouched.");
        // Refuse to overwrite an existing unknown loader to avoid destroying another setup.
        if (File.Exists(Path.Combine(game, "winhttp.dll")) || File.Exists(Path.Combine(game, "doorstop_config.ini")))
            throw new InvalidOperationException("A loader already occupies winhttp.dll or doorstop_config.ini. Back it up or remove it manually before installing.");
        string managerRoot = GameManagerRoot(game);
        Directory.CreateDirectory(managerRoot);
        string archive = Path.Combine(managerRoot, "loader-download-" + Guid.NewGuid().ToString("N") + ".zip");
        string backup = Path.Combine(managerRoot, "backups", "loader-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var changed = new List<string>();
        var backups = new List<(string Original, string Saved)>();
        try
        {
            log?.Invoke("Downloading official BepInEx " + LoaderVersion + "...");
            await DownloadAsync(LoaderUrl, archive, MaxLoaderBytes);
            using var zip = ZipFile.OpenRead(archive);
            bool seenWinhttp = false, seenConfig = false, seenCore = false, seenRuntime = false;
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string relative = SafeRelative(entry.FullName);
                string normalized = relative.Replace('\\', '/');
                bool allowed = normalized.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith("dotnet/", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals(".doorstop_version", StringComparison.OrdinalIgnoreCase);
                if (!allowed) continue;
                if (normalized.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)) seenWinhttp = true;
                if (normalized.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase)) seenConfig = true;
                if (normalized.Equals("BepInEx/core/BepInEx.Unity.IL2CPP.dll", StringComparison.OrdinalIgnoreCase)) seenCore = true;
                if (normalized.Equals("dotnet/coreclr.dll", StringComparison.OrdinalIgnoreCase)) seenRuntime = true;
                if (IsSymlink(entry)) throw new InvalidOperationException("Loader archive contains a symbolic link.");
            }
            if (!(seenWinhttp && seenConfig && seenCore && seenRuntime))
                throw new InvalidOperationException("Downloaded archive is missing mandatory IL2CPP loader files. Nothing was installed.");
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string relative = SafeRelative(entry.FullName);
                string normalized = relative.Replace('\\', '/');
                bool allowed = normalized.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith("dotnet/", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals(".doorstop_version", StringComparison.OrdinalIgnoreCase);
                if (!allowed) continue;
                string full = Path.GetFullPath(Path.Combine(game, relative));
                if (!full.StartsWith(game.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Archive path escapes game directory.");
                if (File.Exists(full))
                {
                    string backupFile = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Copy(full, backupFile, true);
                    backups.Add((full, backupFile));
                }
                else changed.Add(full);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full, true);
            }
            if (!IsBepInExInstalled(game)) throw new InvalidOperationException("BepInEx installation failed verification.");
            File.WriteAllText(Path.Combine(managerRoot, "installed-loader.txt"), LoaderVersion + Environment.NewLine + LoaderUrl);
            log?.Invoke("BepInEx installed. Launch Orbus once to generate IL2CPP interop files.");
        }
        catch
        {
            foreach (string file in changed) { try { File.Delete(file); } catch { } }
            foreach (var item in backups) { try { File.Copy(item.Saved, item.Original, true); } catch { } }
            throw;
        }
        finally { try { File.Delete(archive); } catch { } }
    }

    private static string ModFolder(string game, string id, bool enabled)
    {
        CheckId(id);
        return enabled ? Path.Combine(game, "BepInEx", "plugins", id) :
            Path.Combine(game, "OrbusRebornManager", "disabled", id);
    }

    // Read-only path helper for the instance overview. The manager only
    // modifies these known managed folders, never other plugins.
    public string GetManagedModPath(string game, string id, bool enabled) =>
        ModFolder(game, id, enabled);

    // Avoid loading the same plugin filename twice in a modded instance.
    // In particular, don't install local-bettermirror alongside a manually
    // installed BepInEx/plugins/BetterMirror/BetterMirror.dll.
    private static void RejectDuplicatePluginDlls(string game, string staging, string destination)
    {
        string plugins = Path.Combine(game, "BepInEx", "plugins");
        if (!Directory.Exists(plugins)) return;
        string ownedRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var proposed = Directory.GetFiles(staging, "*.dll", SearchOption.AllDirectories);
        foreach (string file in Directory.GetFiles(plugins, "*.dll", SearchOption.AllDirectories))
        {
            if (Path.GetFullPath(file).StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (proposed.Any(p => Path.GetFileName(p).Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A DLL with the same name is already installed outside this managed mod: " + file +
                    ". Remove or migrate that older manual install first to avoid duplicate plugins.");
        }
    }

    public InstalledMod? FindInstalled(string game, string id) =>
        LoadInstalled(game).Mods.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public async Task InstallReleaseAsync(string game, ModDefinition mod, GitHubRelease release, GitHubAsset asset, Action<string>? log = null)
    {
        if (!IsBepInExInstalled(game)) throw new InvalidOperationException("Install BepInEx before installing mods.");
        if (IsGameRunning()) throw new InvalidOperationException("Close OrbusVR before modifying plugins.");
        var state = LoadInstalled(game);
        var existing = state.Mods.FirstOrDefault(x => x.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase));
        if (existing?.Local == true) throw new InvalidOperationException("This mod ID belongs to a local test build. Remove it first.");
        if (Path.GetFileName(asset.Name) != asset.Name || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Unsafe GitHub release asset name.");
        string managerRoot = GameManagerRoot(game);
        string tmpRoot = Path.Combine(managerRoot, "staging", Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(tmpRoot, "content");
        string zipOrDll = Path.Combine(tmpRoot, asset.Name);
        Directory.CreateDirectory(staging);
        try
        {
            log?.Invoke("Downloading " + mod.Name + " " + release.TagName + "...");
            await DownloadAsync(asset.DownloadUrl, zipOrDll, MaxModBytes, asset.Digest);
            if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(zipOrDll, Path.Combine(staging, Path.GetFileName(asset.Name)));
            }
            else
            {
                using var zip = ZipFile.OpenRead(zipOrDll);
                long uncompressed = 0;
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    uncompressed += entry.Length;
                    if (uncompressed > MaxModBytes) throw new InvalidOperationException("Mod archive expands beyond 100 MB.");
                    string relative = entry.FullName.Replace('\\', '/');
                    if (IsSymlink(entry)) throw new InvalidOperationException("Mod ZIP contains a symbolic link.");
                    if (relative.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase))
                        relative = relative["BepInEx/plugins/".Length..];
                    else if (relative.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase) ||
                             relative.StartsWith("dotnet/", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Mod ZIP tried to replace BepInEx loader files.");
                    string ext = Path.GetExtension(relative).ToLowerInvariant();
                    if (ext is not (".dll" or ".json" or ".cfg" or ".xml" or ".txt" or ".png" or ".bundle" or ".assets"))
                        continue; // No executables, PowerShell scripts or loader replacements.
                    CopyZipEntry(entry, staging, relative);
                }
            }
            if (Directory.GetFiles(staging, "*.dll", SearchOption.AllDirectories).Length == 0)
                throw new InvalidOperationException("Mod release contains no DLL files.");
            bool enabled = existing?.Enabled ?? true;
            string destination = ModFolder(game, mod.Id, enabled);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            RejectDuplicatePluginDlls(game, staging, destination);
            string oldBackup = Path.Combine(tmpRoot, "previous");
            bool hadOld = Directory.Exists(destination);
            if (hadOld) Directory.Move(destination, oldBackup);
            try { Directory.Move(staging, destination); }
            catch { if (hadOld && Directory.Exists(oldBackup)) Directory.Move(oldBackup, destination); throw; }
            try
            {
                if (existing == null)
                {
                    existing = new InstalledMod { Id = mod.Id };
                    state.Mods.Add(existing);
                }
                existing.Name = mod.Name;
                existing.Repository = mod.Repository;
                existing.Version = release.TagName;
                existing.AssetName = asset.Name;
                existing.Enabled = enabled;
                existing.Local = false;
                SaveInstalled(game, state);
            }
            catch
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                if (hadOld && Directory.Exists(oldBackup)) Directory.Move(oldBackup, destination);
                throw;
            }
            log?.Invoke("Installed " + mod.Name + " " + release.TagName + (enabled ? "." : " (still disabled)."));
        }
        finally { try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { } }
    }

    public void SetModEnabled(string game, string id, bool enabled)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Close OrbusVR before enabling or disabling mods.");
        var state = LoadInstalled(game);
        var mod = state.Mods.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Mod is not managed by OrbusRebornManager.");
        if (mod.Enabled == enabled) return;
        string source = ModFolder(game, id, mod.Enabled);
        string target = ModFolder(game, id, enabled);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Managed mod folder missing: " + source);
        if (Directory.Exists(target)) throw new InvalidOperationException("Destination already exists. Refusing to overwrite files.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
        try { mod.Enabled = enabled; SaveInstalled(game, state); }
        catch { Directory.Move(target, source); throw; }
    }

    public void UninstallMod(string game, string id)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Close OrbusVR before uninstalling mods.");
        var state = LoadInstalled(game);
        var mod = state.Mods.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Mod is not managed by OrbusRebornManager.");
        string source = ModFolder(game, id, mod.Enabled);
        string trash = Path.Combine(GameManagerRoot(game), "staging", "uninstall-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            Directory.Move(source, trash);
        }
        try { state.Mods.Remove(mod); SaveInstalled(game, state); }
        catch { if (Directory.Exists(trash)) Directory.Move(trash, source); throw; }
        try { if (Directory.Exists(trash)) Directory.Delete(trash, true); } catch { }
    }

    public void ImportLocalDll(string game, string file)
    {
        if (!IsBepInExInstalled(game)) throw new InvalidOperationException("Install BepInEx first.");
        if (IsGameRunning()) throw new InvalidOperationException("Close the game first.");
        if (!File.Exists(file) || !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a compiled BepInEx .dll file.");
        string name = Path.GetFileNameWithoutExtension(file);
        string id = "local-" + Regex.Replace(name, "[^a-zA-Z0-9_-]", "-").ToLowerInvariant();
        CheckId(id);
        var state = LoadInstalled(game);
        var previous = state.Mods.FirstOrDefault(x => x.Id == id);
        if (previous != null && !previous.Local)
            throw new InvalidOperationException("A catalogue mod already uses this ID.");
        string dir = ModFolder(game, id, previous?.Enabled ?? true);
        // Reject imports that would create two copies of an already installed plugin.
        string plugins = Path.Combine(game, "BepInEx", "plugins");
        if (Directory.Exists(plugins))
        {
            string owned = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string other in Directory.GetFiles(plugins, Path.GetFileName(file), SearchOption.AllDirectories))
            {
                if (!Path.GetFullPath(other).StartsWith(owned, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("This DLL is already installed manually at: " + other +
                        ". Remove the older copy before importing it to prevent duplicate loading.");
            }
        }
        Directory.CreateDirectory(dir);
        // Only update this specific DLL, preserving any other local companion files.
        File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), true);
        if (previous == null) { previous = new InstalledMod { Id = id }; state.Mods.Add(previous); }
        previous.Name = name + " (local test)";
        previous.Repository = "";
        previous.Version = "local";
        previous.AssetName = Path.GetFileName(file);
        previous.Local = true;
        SaveInstalled(game, state);
    }

    public void Launch(string game)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(game, "vrclient.exe"),
            WorkingDirectory = game,
            UseShellExecute = true
        });
    }
}
