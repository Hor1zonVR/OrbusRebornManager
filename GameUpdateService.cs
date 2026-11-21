using System.IO;
using System.Threading;

namespace OrbusRebornManager;

/// <summary>
/// Installs game updates by staging an entire clean build first, preserving mod
/// user-data, then swapping directories on the same volume. Previous game build
/// stays intact for explicit rollback; no update writes into the live game.
/// </summary>
public static class GameUpdateService
{
    // Doorstop's IL2CPP BepInEx bootstrap requires dotnet/coreclr.dll and
    // the managed runtime libraries shipped with the original mod-loader.
    // Preserve these alongside BepInEx and winhttp.dll across game updates.
    private static readonly string[] PreservedDirectories =
        { "BepInEx", "dotnet", "OrbusRebornManager", "Mods", "UserData" };
    private static readonly string[] PreservedFiles =
        { "winhttp.dll", "doorstop_config.ini", ".doorstop_version" };

    public static async Task UpdateAsync(GameInstance selected,
        GameReleaseDownloadService.GameReleaseInfo release,
        IProgress<GameReleaseDownloadService.Status>? progress,
        CancellationToken cancellationToken = default)
    {
        ValidateManagedInstance(selected);
        if (!string.Equals(selected.Edition, release.Edition, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This update is for a different game edition.");
        if (string.IsNullOrWhiteSpace(selected.GameVersion))
            throw new InvalidOperationException("The current game version is unknown. Existing copied instances require a known release version before in-place updates can be enabled.");
        if (string.Equals(selected.GameVersion, release.Version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The instance is already at the latest release.");
        if (GameClientLayout.IsClientRunning())
            throw new InvalidOperationException("Close all OrbusVR clients before updating.");

        string game = Path.GetFullPath(selected.Path);
        EnsureLoaderIsCompleteBeforeUpdate(game);
        string parent = Path.GetDirectoryName(game)!;
        string working = Path.Combine(parent, ".orbus-update-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(parent, ".orbus-rollback-" + Guid.NewGuid().ToString("N"));
        CheckFreeSpace(parent, release.Size);
        Directory.CreateDirectory(working);

        bool oldMoved = false, newMoved = false;
        string phase = "downloading and extracting the verified game archive";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string staged = await GameReleaseDownloadService.ExtractReleaseAsync(
                release, working, progress, cancellationToken);

            phase = "copying and checking BepInEx, plugins and settings";
            await Task.Run(() =>
            {
                progress?.Report(new("Preserving BepInEx, mods and settings...", 99));
                PreserveUserFiles(game, staged, cancellationToken);
                VerifyPreservedUserFiles(game, staged, cancellationToken);
                GameClientLayout.FindExecutable(staged);
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(backup) || Directory.Exists(Path.Combine(parent, ".orbus-rollback")))
                throw new InvalidOperationException("An update backup location is already occupied.");
            if (GameClientLayout.IsClientRunning())
                throw new InvalidOperationException("Close OrbusVR before applying the update.");

            // Everything above can be cancelled. The following critical
            // section is intentionally non-cancellable to prevent half-swaps.
            // Its operations are staged so a Windows access-denied error tells
            // users what to close, rather than encouraging unsafe ACL changes.
            phase = "renaming the existing game folder for rollback";
            Directory.Move(game, backup);
            oldMoved = true;
            phase = "putting the staged game at the original location";
            Directory.Move(staged, game);
            newMoved = true;
            phase = "saving the new version and rollback registration";
            SaveVersion(game, release.Version, backup, selected.GameVersion);
            progress?.Report(new("Update installed. Previous version retained for rollback.", 100));
        }
        catch (Exception failure)
        {
            // If a filesystem or registry operation fails after the first move,
            // put the original files back. Do not delete either version.
            try
            {
                if (oldMoved)
                {
                    if (newMoved && Directory.Exists(game))
                    {
                        string failed = Path.Combine(parent, ".orbus-failed-update-" + Guid.NewGuid().ToString("N"));
                        Directory.Move(game, failed);
                    }
                    if (!Directory.Exists(game) && Directory.Exists(backup))
                        Directory.Move(backup, game);
                }
            }
            catch (Exception recoveryFailure)
            {
                throw new IOException(
                    "Game update failed while " + phase +
                    ", and restoring the original game folder also failed. " +
                    "Do NOT delete either the game or .orbus-rollback-* folders. " +
                    "Recovery details: " + recoveryFailure.Message,
                    new AggregateException(failure, recoveryFailure));
            }
            if (failure is UnauthorizedAccessException or IOException)
                throw new IOException(
                    "Windows blocked the game update while " + phase + ". " +
                    "The existing game was left unchanged or restored. " +
                    "Close OrbusVR and any Spades updater/launcher, Explorer " +
                    "windows or terminals using this instance, then retry. " +
                    "If the issue persists, restart Windows; don't delete " +
                    "BepInEx or change directory permissions. Details: " +
                    failure.Message, failure);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(working)) Directory.Delete(working, recursive: true); }
            catch { /* Leave staging for recovery if Windows holds a file open. */ }
        }
    }

    public static async Task RollbackAsync(GameInstance selected,
        CancellationToken cancellationToken = default)
    {
        ValidateManagedInstance(selected);
        if (string.IsNullOrWhiteSpace(selected.BackupPath) ||
            string.IsNullOrWhiteSpace(selected.BackupGameVersion))
            throw new InvalidOperationException("No previous game version is recorded for this instance.");

        string game = Path.GetFullPath(selected.Path);
        string parent = Path.GetDirectoryName(game)!;
        string backup = Path.GetFullPath(selected.BackupPath);
        if (!string.Equals(parent, Path.GetDirectoryName(backup),
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(backup).StartsWith(".orbus-rollback-", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(backup) ||
            (File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The saved rollback folder is missing or unsafe.");
        if (GameClientLayout.IsRenewed(backup) != (selected.Edition == GameReleaseDownloadService.Renewed))
            throw new InvalidOperationException("The rollback folder contains a different game edition.");
        if (GameClientLayout.IsClientRunning())
            throw new InvalidOperationException("Close OrbusVR before rolling back.");

        cancellationToken.ThrowIfCancellationRequested();
        string swap = Path.Combine(parent, ".orbus-swap-" + Guid.NewGuid().ToString("N"));
        await Task.Run(() =>
        {
            bool movedCurrent = false, movedBackup = false, completedSwap = false;
            try
            {
                Directory.Move(game, swap);
                movedCurrent = true;
                Directory.Move(backup, game);
                movedBackup = true;
                Directory.Move(swap, backup);
                completedSwap = true;
                SaveVersion(game, selected.BackupGameVersion, backup, selected.GameVersion);
            }
            catch
            {
                // If saving registry metadata failed after the full swap, put
                // BOTH game folders back into their exact pre-rollback spots.
                if (completedSwap && Directory.Exists(game) && Directory.Exists(backup))
                {
                    string recovery = Path.Combine(parent,
                        ".orbus-recover-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(game, recovery);
                    Directory.Move(backup, game);
                    Directory.Move(recovery, backup);
                }
                else
                {
                    if (movedBackup && Directory.Exists(game) && !Directory.Exists(backup))
                        Directory.Move(game, backup);
                    if (movedCurrent && Directory.Exists(swap) && !Directory.Exists(game))
                        Directory.Move(swap, game);
                }
                throw;
            }
        });
    }

    /// <summary>
    /// Restore missing loader/mod files from RM's preserved *previous game*
    /// without downgrading or modifying the current game's executables.
    /// Existing files are never overwritten. A failed restore removes only
    /// newly created files and empty directories.
    /// </summary>
    public static async Task<int> RestoreMissingModSetupAsync(GameInstance selected,
        CancellationToken cancellationToken = default)
    {
        ValidateManagedInstance(selected);
        if (GameClientLayout.IsClientRunning())
            throw new InvalidOperationException("Close all OrbusVR clients and the Spades updater first.");
        string game = Path.GetFullPath(selected.Path);
        string parent = Path.GetDirectoryName(game)!;
        if (string.IsNullOrWhiteSpace(selected.BackupPath))
            throw new InvalidOperationException(
                "RM has no rollback backup for this game. It cannot recover files erased by another updater.");
        string backup = Path.GetFullPath(selected.BackupPath);
        if (!string.Equals(parent, Path.GetDirectoryName(backup),
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(backup).StartsWith(".orbus-rollback-",
                StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(backup) ||
            (File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The previous RM game backup is missing or unsafe.");
        if (GameClientLayout.IsRenewed(backup) !=
            (selected.Edition == GameReleaseDownloadService.Renewed))
            throw new InvalidOperationException("The backup belongs to a different game edition.");
        string[] essential =
        {
            "winhttp.dll", "doorstop_config.ini",
            Path.Combine("BepInEx", "core", "BepInEx.Unity.IL2CPP.dll"),
            Path.Combine("dotnet", "coreclr.dll")
        };
        if (essential.Any(name => !File.Exists(Path.Combine(backup, name))))
            throw new InvalidOperationException(
                "The RM rollback backup has no complete BepInEx loader; nothing was restored.");

        cancellationToken.ThrowIfCancellationRequested();
        string working = Path.Combine(parent, ".orbus-mod-recover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);
        try
        {
            await Task.Run(() =>
            {
                foreach (string folder in PreservedDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string source = Path.Combine(backup, folder);
                    if (Directory.Exists(source))
                        CopyTree(source, Path.Combine(working, folder),
                            folder == "BepInEx", cancellationToken);
                }
                foreach (string name in PreservedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string source = Path.Combine(backup, name);
                    if (File.Exists(source))
                        File.Copy(source, Path.Combine(working, name));
                }
                VerifyPreservedUserFiles(backup, working, cancellationToken);
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (GameClientLayout.IsClientRunning())
                throw new InvalidOperationException("OrbusVR started during recovery. Close it and retry.");

            var createdFiles = new List<string>();
            var createdDirectories = new List<string>();
            try
            {
                // Move only missing files into place. Any existing mod, config
                // or loader binary (including a user's manual changes) stays.
                foreach (string stagedFile in Directory.EnumerateFiles(working,
                             "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(working, stagedFile);
                    string target = Path.Combine(game, relative);
                    if (File.Exists(target)) continue;
                    if (Directory.Exists(target))
                        throw new IOException("A directory conflicts with restored file " + relative);
                    string dir = Path.GetDirectoryName(target)!;
                    var missing = new Stack<string>();
                    string candidate = dir;
                    while (!Directory.Exists(candidate))
                    {
                        missing.Push(candidate);
                        candidate = Path.GetDirectoryName(candidate)!;
                    }
                    // The closest existing parent is not sufficient: a
                    // higher ancestor (BepInEx/plugins, for example) could
                    // resolve through a directory junction. Never follow one.
                    string check = candidate;
                    while (check.StartsWith(game + Path.DirectorySeparatorChar,
                               StringComparison.OrdinalIgnoreCase) ||
                           check.Equals(game, StringComparison.OrdinalIgnoreCase))
                    {
                        if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidOperationException(
                                "Cannot restore through a linked game directory.");
                        if (check.Equals(game, StringComparison.OrdinalIgnoreCase)) break;
                        check = Path.GetDirectoryName(check)!;
                    }
                    while (missing.Count > 0)
                    {
                        string newDir = missing.Pop();
                        Directory.CreateDirectory(newDir);
                        createdDirectories.Add(newDir);
                    }
                    File.Move(stagedFile, target);
                    createdFiles.Add(target);
                }
                if (essential.Any(name => !File.Exists(Path.Combine(game, name)) ||
                    new FileInfo(Path.Combine(game, name)).Length == 0))
                    throw new IOException("Restored loader remains incomplete; newly added files were removed.");
                return createdFiles.Count;
            }
            catch
            {
                // The game binaries and all pre-existing files remain untouched.
                foreach (string file in createdFiles.AsEnumerable().Reverse())
                {
                    try { File.Delete(file); } catch { }
                }
                foreach (string directory in createdDirectories.AsEnumerable().Reverse())
                {
                    try { if (!Directory.EnumerateFileSystemEntries(directory).Any())
                            Directory.Delete(directory); }
                    catch { }
                }
                throw;
            }
        }
        finally
        {
            try { if (Directory.Exists(working)) Directory.Delete(working, true); }
            catch { /* Keep staging for recovery if an external process locked it. */ }
        }
    }

    private static void ValidateManagedInstance(GameInstance instance)
    {
        if (!instance.CreatedByManager ||
            instance.Edition is not (GameReleaseDownloadService.Community or GameReleaseDownloadService.Renewed))
            throw new InvalidOperationException("Only managed Community/Renewed game instances can be updated. Imported original installations are never overwritten.");
        if (!Directory.Exists(instance.Path))
            throw new DirectoryNotFoundException("The game instance is missing.");
        string game = Path.GetFullPath(instance.Path);
        string parent = Path.GetDirectoryName(game)!;
        if ((File.GetAttributes(game) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Linked game directories are not supported for automatic updates.");
        if (GameClientLayout.IsRenewed(game) != (instance.Edition == GameReleaseDownloadService.Renewed))
            throw new InvalidOperationException("The installed game doesn't match its recorded edition.");
        if (!new InstanceStore().Load().Any(x => Path.GetFullPath(x.Path).Equals(game,
                StringComparison.OrdinalIgnoreCase) && x.CreatedByManager))
            throw new InvalidOperationException("This instance is no longer registered with RM.");
    }

    private static void SaveVersion(string path, string version, string backupPath, string backupVersion)
    {
        var store = new InstanceStore();
        var instances = store.Load();
        var instance = instances.Single(x => Path.GetFullPath(x.Path).Equals(path, StringComparison.OrdinalIgnoreCase));
        instance.GameVersion = version;
        instance.BackupPath = backupPath;
        instance.BackupGameVersion = backupVersion;
        store.Save(instances);
    }

    private static void CheckFreeSpace(string folder, long archiveSize)
    {
        try
        {
            var disk = new DriveInfo(Path.GetPathRoot(folder)!);
            // Download archive lives in a separate verified cache. The new
            // extracted game coexists with the old one during preparation.
            long needed = checked(archiveSize * 3 + 256L * 1024 * 1024);
            if (disk.IsReady && disk.AvailableFreeSpace < needed)
                throw new InvalidOperationException("Not enough disk space to safely stage this update while preserving the previous game. Choose another drive or free space first.");
        }
        catch (IOException) { /* Let the underlying disk write report an error. */ }
        catch (ArgumentException) { }
    }

    private static void PreserveUserFiles(string oldGame, string newGame, CancellationToken token)
    {
        foreach (string folder in PreservedDirectories)
        {
            token.ThrowIfCancellationRequested();
            string origin = Path.Combine(oldGame, folder);
            if (!Directory.Exists(origin)) continue;
            string destination = Path.Combine(newGame, folder);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new InvalidOperationException("The new game ships its own " + folder + " folder; cannot safely merge modded user data.");
            CopyTree(origin, destination, folder == "BepInEx", token);
        }
        foreach (string name in PreservedFiles)
        {
            token.ThrowIfCancellationRequested();
            string origin = Path.Combine(oldGame, name);
            if (!File.Exists(origin)) continue;
            if ((File.GetAttributes(origin) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Cannot preserve a linked mod-loader file.");
            string destination = Path.Combine(newGame, name);
            if (File.Exists(destination))
                throw new InvalidOperationException("The new game ships a conflicting loader file: " + name);
            File.Copy(origin, destination);
        }
    }

    // A native in-game updater may remove only part of the loader. Refuse to
    // stage that broken state and later claim the mods were preserved.
    // Clean, unmodded instances remain eligible for ordinary game updates.
    private static void EnsureLoaderIsCompleteBeforeUpdate(string game)
    {
        string[] essentials =
        {
            "winhttp.dll", "doorstop_config.ini",
            Path.Combine("BepInEx", "core", "BepInEx.Unity.IL2CPP.dll"),
            Path.Combine("dotnet", "coreclr.dll")
        };
        bool hasLoader = Directory.Exists(Path.Combine(game, "BepInEx")) ||
                         Directory.Exists(Path.Combine(game, "dotnet")) ||
                         essentials.Any(name => File.Exists(Path.Combine(game, name)));
        if (!hasLoader) return;
        if (essentials.Any(name => !File.Exists(Path.Combine(game, name))))
            throw new InvalidOperationException(
                "The selected game already has an incomplete BepInEx installation. " +
                "Updating it now would preserve broken loader files. Use Restore mod " +
                "setup (if an RM backup exists) or explicitly repair BepInEx first. " +
                "Your existing game and mods were not changed.");
    }

    // Verify the staged mod overlay *before* swapping either game folder.
    // Exclude only build-specific generated interop/cache; every real plugin
    // and configuration file must survive intact. Never write to the source.
    private static void VerifyPreservedUserFiles(string oldGame, string staged,
        CancellationToken token)
    {
        foreach (string folder in PreservedDirectories)
        {
            token.ThrowIfCancellationRequested();
            string source = Path.Combine(oldGame, folder);
            if (!Directory.Exists(source)) continue;
            string target = Path.Combine(staged, folder);
            if (!Directory.Exists(target))
                throw new IOException("Staged update is missing protected folder " + folder);
            foreach (string file in Directory.EnumerateFiles(source, "*",
                         SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(source, file);
                if (folder == "BepInEx" &&
                    (relative.StartsWith("interop" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) ||
                     relative.StartsWith("cache" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
                    continue;
                string restored = Path.Combine(target, relative);
                if (!File.Exists(restored) ||
                    new FileInfo(file).Length != new FileInfo(restored).Length)
                    throw new IOException("Staged game failed to preserve " +
                        folder + " / " + relative);
            }
        }
        foreach (string name in PreservedFiles)
        {
            token.ThrowIfCancellationRequested();
            string source = Path.Combine(oldGame, name);
            if (!File.Exists(source)) continue;
            string target = Path.Combine(staged, name);
            if (!File.Exists(target) ||
                new FileInfo(source).Length != new FileInfo(target).Length)
                throw new IOException("Staged game failed to preserve loader file " + name);
        }
    }

    private static void CopyTree(string source, string target, bool skipGeneratedInterop, CancellationToken token)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Cannot preserve linked mod directories.");
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(directory);
            // Interop is compiled against one exact game build, regenerate it
            // after game updates instead of silently retaining stale assemblies.
            if (skipGeneratedInterop && (name.Equals("interop", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("cache", StringComparison.OrdinalIgnoreCase)))
                continue;
            CopyTree(directory, Path.Combine(target, name), false, token);
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Cannot preserve linked mod files.");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }
}
