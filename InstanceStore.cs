
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrbusRebornManager;

public sealed class GameInstance
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool CreatedByManager { get; set; }
    public string CustomIconPath { get; set; } = "";
    // Artwork framing uses logical pixels based on a 164x137 library cover.
    // Defaults are intentionally compatible with instances created before v0.4.11.
    public bool IconFit { get; set; }
    public double IconZoom { get; set; } = 1.0;
    public double IconOffsetX { get; set; }
    public double IconOffsetY { get; set; }
    // True when a user has explicitly selected a crop in the full-image editor.
    // The source image remains preserved for future re-cropping.
    public bool IconHasCrop { get; set; }
    public double IconCropX { get; set; }
    public double IconCropY { get; set; }
    public double IconCropWidth { get; set; } = 1;
    public double IconCropHeight { get; set; } = 1;
}

public sealed class InstanceStore
{
    private static readonly StringComparison PathComparison =
        StringComparison.OrdinalIgnoreCase;

    private readonly string _registryPath = System.IO.Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "OrbusRebornManager",
        "game-instances.json");

    public List<GameInstance> Load()
    {
        if (!File.Exists(_registryPath))
            return new List<GameInstance>();

        try
        {
            return JsonSerializer.Deserialize<List<GameInstance>>(
                File.ReadAllText(_registryPath))
                ?? new List<GameInstance>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Cannot read your saved game instances: " +
                ex.Message, ex);
        }
    }

    public void Save(IEnumerable<GameInstance> instances)
    {
        Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(_registryPath)!);

        string temporary = _registryPath + ".tmp";

        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                instances,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

        File.Move(temporary, _registryPath, true);
    }

    public GameInstance AddExisting(
        List<GameInstance> instances,
        string path,
        string name = "Original installation")
    {
        path = System.IO.Path.GetFullPath(path);

        var existing = instances.FirstOrDefault(
            x => SamePath(x.Path, path));

        if (existing != null)
            return existing;

        var item = new GameInstance
        {
            Name = name,
            Path = path,
            CreatedByManager = false
        };

        instances.Add(item);
        Save(instances);
        return item;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(left)),
            System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(right)),
            PathComparison);

    public async Task<GameInstance> CreateCleanCopyAsync(
        string source,
        string destinationParent,
        string name,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        source = System.IO.Path.GetFullPath(source);
        destinationParent =
            System.IO.Path.GetFullPath(destinationParent);

        name = name.Trim();

        if (!Regex.IsMatch(
                name,
                @"^[a-zA-Z0-9][a-zA-Z0-9 _-]{0,49}$") ||
            name.EndsWith(' ') ||
            Regex.IsMatch(
                name,
                @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
                RegexOptions.IgnoreCase))
        {
            throw new InvalidOperationException(
                "Use a name of up to 50 characters " +
                "(letters, numbers, spaces, - or _).");
        }

        if (!Directory.Exists(destinationParent))
            throw new InvalidOperationException(
                "Choose an existing destination folder.");

        if (!File.Exists(
                System.IO.Path.Combine(source, "vrclient.exe")))
            throw new InvalidOperationException(
                "Choose an existing OrbusVR Reborn " +
                "installation as the source.");

        string destination =
            System.IO.Path.Combine(destinationParent, name);

        string sourceRoot =
            System.IO.Path.TrimEndingDirectorySeparator(source)
            + System.IO.Path.DirectorySeparatorChar;

        string destinationRoot =
            System.IO.Path.TrimEndingDirectorySeparator(destination)
            + System.IO.Path.DirectorySeparatorChar;

        if (destination.StartsWith(sourceRoot, PathComparison) ||
            source.StartsWith(destinationRoot, PathComparison) ||
            SamePath(source, destination))
        {
            throw new InvalidOperationException(
                "The new instance cannot be inside the source " +
                "game folder or contain it.");
        }

        if (Directory.Exists(destination) ||
            File.Exists(destination))
        {
            throw new InvalidOperationException(
                "A folder with that name already exists. " +
                "Choose another instance name.");
        }

        static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Cannot copy a linked file or folder: " + path);
            }
        }

        return await Task.Run(() =>
        {
            RejectLink(source);
            RejectLink(destinationParent);

            var files =
                new List<(string Source, string Relative)>();

            void Collect(
                string folder,
                string relativeFolder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLink(folder);

                foreach (var file in
                    Directory.EnumerateFiles(folder))
                {
                    RejectLink(file);

                    string filename =
                        System.IO.Path.GetFileName(file);

                    if (relativeFolder.Length == 0 &&
                        (filename.Equals(
                            "winhttp.dll", PathComparison) ||
                         filename.Equals(
                            "doorstop_config.ini", PathComparison) ||
                         filename.Equals(
                            ".doorstop_version", PathComparison)))
                    {
                        continue;
                    }

                    files.Add((
                        file,
                        System.IO.Path.Combine(
                            relativeFolder, filename)));
                }

                foreach (var subfolder in
                    Directory.EnumerateDirectories(folder))
                {
                    RejectLink(subfolder);

                    string filename =
                        System.IO.Path.GetFileName(subfolder);

                    if (relativeFolder.Length == 0 &&
                        (filename.Equals(
                            "BepInEx", PathComparison) ||
                         filename.Equals(
                            "OrbusRebornManager",
                            PathComparison)))
                    {
                        continue;
                    }

                    Collect(
                        subfolder,
                        System.IO.Path.Combine(
                            relativeFolder, filename));
                }
            }

            Collect(source, "");
            cancellationToken.ThrowIfCancellationRequested();

            // Check free space before writing anything. The copied game
            // can be large, especially when the default lives on C:.
            long requiredBytes = 0;
            foreach (var (file, _) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checked { requiredBytes += new FileInfo(file).Length; }
            }

            try
            {
                var drive = new DriveInfo(
                    System.IO.Path.GetPathRoot(destinationParent)!);
                long reserve = Math.Min(256L * 1024 * 1024,
                    Math.Max(64L * 1024 * 1024, requiredBytes / 20));
                if (drive.IsReady &&
                    drive.AvailableFreeSpace < requiredBytes + reserve)
                {
                    throw new InvalidOperationException(
                        "There isn't enough free space to copy OrbusVR to " +
                        destinationParent + ". Choose another storage drive " +
                        "from the New Instance window.");
                }
            }
            catch (ArgumentException)
            {
                // Some network and virtual filesystems do not expose capacity.
            }
            catch (IOException)
            {
                // Let the copy attempt surface the actual filesystem error.
            }


            // Copy into a temporary folder first.
            // Never risk deleting someone else's destination.
            string staging = System.IO.Path.Combine(
                destinationParent,
                ".orbus-copy-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(staging);

            try
            {
                int done = 0;
                progress?.Report((done, files.Count));

                foreach (var (file, relative) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string target =
                        System.IO.Path.Combine(staging, relative);

                    Directory.CreateDirectory(
                        System.IO.Path.GetDirectoryName(target)!);

                    File.Copy(file, target, overwrite: false);

                    progress?.Report((++done, files.Count));
                }

                if (Directory.Exists(destination) ||
                    File.Exists(destination))
                {
                    throw new InvalidOperationException(
                        "The destination was created by another " +
                        "process during copying.");
                }

                Directory.Move(staging, destination);

                return new GameInstance
                {
                    Name = name,
                    Path = destination,
                    CreatedByManager = true
                };
            }
            catch
            {
                try
                {
                    Directory.Delete(
                        staging, recursive: true);
                }
                catch
                {
                    // Leave incomplete staging files for inspection
                    // if Windows won't let us delete them.
                }

                throw;
            }
        }, cancellationToken);
    }
}
