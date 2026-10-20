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
    private static readonly string[] PreservedDirectories =
        { "BepInEx", "OrbusRebornManager", "Mods", "UserData" };
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
        string parent = Path.GetDirectoryName(game)!;
        string working = Path.Combine(parent, ".orbus-update-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(parent, ".orbus-rollback-" + Guid.NewGuid().ToString("N"));
        CheckFreeSpace(parent, release.Size);
        Directory.CreateDirectory(working);

        bool oldMoved = false, newMoved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string staged = await GameReleaseDownloadService.ExtractReleaseAsync(
                release, working, progress, cancellationToken);

            await Task.Run(() =>
            {
                progress?.Report(new("Preserving BepInEx, mods and settings...", 99));
                PreserveUserFiles(game, staged, cancellationToken);
                GameClientLayout.FindExecutable(staged);
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(backup) || Directory.Exists(Path.Combine(parent, ".orbus-rollback")))
                throw new InvalidOperationException("An update backup location is already occupied.");
            if (GameClientLayout.IsClientRunning())
                throw new InvalidOperationException("Close OrbusVR before applying the update.");

            // Everything above can be cancelled. The following small critical
            // section is intentionally non-cancellable to prevent half-swaps.
            Directory.Move(game, backup);
            oldMoved = true;
            Directory.Move(staged, game);
            newMoved = true;
            SaveVersion(game, release.Version, backup, selected.GameVersion);
            progress?.Report(new("Update installed. Previous version retained for rollback.", 100));
        }
        catch
        {
            // If a filesystem or registry operation fails after the first move,
            // put the original files back. Do not delete either version.
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
