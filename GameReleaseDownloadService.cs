using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OrbusRebornManager;

/// <summary>
/// Downloads an intact official PC release and installs it as a new, isolated instance.
/// Never installs into or overwrites an existing game folder.
/// </summary>
public static class GameReleaseDownloadService
{
    public const string Community = "community";
    public const string Renewed = "renewed";

    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private const long MaxArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxExtractedBytes = 12L * 1024 * 1024 * 1024;

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string Tag { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("assets")] public List<Asset> Assets { get; set; } = new();
    }

    private sealed class Asset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
        [JsonPropertyName("browser_download_url")] public string Url { get; set; } = "";
    }

    public readonly record struct Status(string Message, double Percent);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrbusRebornManager/0.4 (+https://github.com/Hor1zonVR/OrbusRebornManager)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<GameInstance> InstallLatestAsync(
        string edition, string destinationParent, string instanceName,
        IProgress<Status>? progress, CancellationToken cancellationToken)
    {
        var (owner, repo, assetName) = edition switch
        {
            Community => ("OrbusVR-Community", "reborn-server", "OrbusVR-Reborn-Community-PC.zip"),
            Renewed => ("FourOfSpades4", "reborn-server", "OrbusVRRenewed.zip"),
            _ => throw new InvalidOperationException("Choose a supported game edition.")
        };

        if (string.IsNullOrWhiteSpace(instanceName) ||
            !Regex.IsMatch(instanceName, @"^[a-zA-Z0-9][a-zA-Z0-9 _-]{0,49}$") ||
            instanceName.EndsWith(' ') ||
            Regex.IsMatch(instanceName, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Instance name must be up to 50 characters using letters, digits, spaces, - or _.");

        destinationParent = Path.GetFullPath(destinationParent);
        if (!Directory.Exists(destinationParent))
            throw new DirectoryNotFoundException("Select an existing instance library location.");

        string destination = Path.Combine(destinationParent, instanceName);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InvalidOperationException("An instance folder with this name already exists.");

        if ((File.GetAttributes(destinationParent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Choose a normal, non-linked instance storage directory.");

        progress?.Report(new Status("Checking the developer's GitHub release...", 0));
        string api = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        using var response = await Client.GetAsync(api, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        Release release = await JsonSerializer.DeserializeAsync<Release>(responseStream, Json, cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty release.");
        if (release.Draft || string.IsNullOrWhiteSpace(release.Tag))
            throw new InvalidOperationException("No published game release was found.");

        var matches = release.Assets.Where(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException($"Release {release.Tag} does not contain exactly one {assetName}.");

        Asset asset = matches[0];
        if (asset.Size <= 0 || asset.Size > MaxArchiveBytes)
            throw new InvalidOperationException("The game archive is missing or exceeds the safety size limit.");
        string expectedHash = asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
            ? asset.Digest[7..].ToLowerInvariant() : "";
        if (!Regex.IsMatch(expectedHash, "^[a-f0-9]{64}$"))
            throw new InvalidOperationException("GitHub did not provide a valid SHA-256 checksum for this release.");

        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var download) ||
            download.Scheme != Uri.UriSchemeHttps || download.Host != "github.com" ||
            !download.AbsolutePath.StartsWith($"/{owner}/{repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected game download URL; refusing the file.");

        // The ZIP and extracted game coexist until the install is verified.
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(destinationParent)!);
            long needed = checked(asset.Size * 3 + 256L * 1024 * 1024);
            if (drive.IsReady && drive.AvailableFreeSpace < needed)
                throw new InvalidOperationException("Not enough free disk space for the download, extraction and safety margin. Choose another drive.");
        }
        catch (ArgumentException) { /* Some virtual/network drives cannot report capacity. */ }
        catch (IOException) { /* Report a real write error during installation. */ }

        string working = Path.Combine(destinationParent, ".orbus-download-" + Guid.NewGuid().ToString("N"));
        string archive = Path.Combine(working, "client.zip");
        string extracted = Path.Combine(working, "extracted");
        Directory.CreateDirectory(working);
        try
        {
            await DownloadVerifiedAsync(asset.Url, archive, asset.Size, expectedHash, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(extracted);
            await ExtractSafelyAsync(archive, extracted, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            string source = FindGameRoot(extracted);
            GameClientLayout.FindExecutable(source);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new InvalidOperationException("Another folder with this instance name was created during download.");

            // Same-volume atomic directory move. No existing user files are overwritten.
            Directory.Move(source, destination);
            progress?.Report(new Status("Game installed and verified.", 100));

            return new GameInstance
            {
                Name = instanceName,
                Path = destination,
                CreatedByManager = true,
                Edition = edition,
                GameVersion = release.Tag
            };
        }
        finally
        {
            try { if (Directory.Exists(working)) Directory.Delete(working, recursive: true); }
            catch { /* Do not delete user files or hide the primary error. */ }
        }
    }

    private static async Task DownloadVerifiedAsync(
        string url, string destination, long announcedSize, string expectedHash,
        IProgress<Status>? progress, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long declaredSize && declaredSize != announcedSize)
            throw new InvalidOperationException("The downloaded file size does not match the GitHub release.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long downloaded = 0;
        int previousPercent = -1;
        int n;
        while ((n = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            downloaded += n;
            if (downloaded > announcedSize)
                throw new InvalidOperationException("Download exceeded its published size.");
            await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            digest.AppendData(buffer, 0, n);
            int percent = (int)(downloaded * 65 / announcedSize);
            if (percent != previousPercent)
            {
                progress?.Report(new Status($"Downloading game: {downloaded / 1048576:N0} of {announcedSize / 1048576:N0} MB", percent));
                previousPercent = percent;
            }
        }
        if (downloaded != announcedSize ||
            !Convert.ToHexString(digest.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The game download failed SHA-256 verification. No instance was installed.");
    }

    private static async Task ExtractSafelyAsync(
        string archive, string outputFolder, IProgress<Status>? progress, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 100000)
            throw new InvalidOperationException("Game archive contains too many files.");

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            checked { total += entry.Length; }
            if (total > MaxExtractedBytes)
                throw new InvalidOperationException("Game archive exceeds the extracted size limit.");
        }

        string rootPrefix = Path.GetFullPath(outputFolder) + Path.DirectorySeparatorChar;
        long extracted = 0;
        int previousPercent = -1;
        byte[] buffer = new byte[1024 * 1024];

        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
                throw new InvalidOperationException("Game archive contains a symbolic link.");

            string fullPath = Path.GetFullPath(Path.Combine(outputFolder,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Game archive contains an unsafe path.");

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(fullPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            if (File.Exists(fullPath))
                throw new InvalidOperationException("Game archive contains duplicate file paths.");

            await using var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            using var input = entry.Open();
            int n;
            while ((n = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                extracted += n;
                int percent = 65 + (int)(extracted * 34 / Math.Max(1L, total));
                if (percent != previousPercent)
                {
                    progress?.Report(new Status($"Extracting game files: {percent - 65}% complete", percent));
                    previousPercent = percent;
                }
            }
        }
    }

    private static string FindGameRoot(string extracted)
    {
        var candidates = new[] { extracted }
            .Concat(Directory.EnumerateDirectories(extracted));
        var matches = new List<string>();
        foreach (string candidate in candidates)
        {
            try { GameClientLayout.FindExecutable(candidate); matches.Add(candidate); }
            catch (InvalidOperationException) { }
        }
        if (matches.Count != 1)
            throw new InvalidOperationException("The downloaded archive does not contain exactly one supported OrbusVR PC client.");
        return matches[0];
    }
}
