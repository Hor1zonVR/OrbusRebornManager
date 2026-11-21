$ErrorActionPreference = "Stop"
if (-not $env:GITHUB_ACTIONS) { throw "This test modifies a temporary instance registry. Only run on GitHub Actions." }

$project = Join-Path $env:RUNNER_TEMP "OrbusRM-GameUpdateSmoke"
dotnet new console -n Smoke -o $project -f net8.0 --force
if ($LASTEXITCODE -ne 0) { throw "Could not create test project" }

$projectFile = Join-Path $project "Smoke.csproj"
$refs = @("InstanceStore.cs", "GameClientLayout.cs", "GitHubReleaseLookup.cs", "GameReleaseDownloadService.cs", "GameUpdateService.cs")
$links = ($refs | ForEach-Object {
    '    <Compile Include="' + (Join-Path $env:GITHUB_WORKSPACE $_) + '" Link="' + $_ + '" />'
}) -join [Environment]::NewLine
$xml = Get-Content -Raw $projectFile
$xml = $xml.Replace("</Project>", "  <ItemGroup>" + [Environment]::NewLine + $links +
    [Environment]::NewLine + "  </ItemGroup>" + [Environment]::NewLine + "</Project>")
Set-Content -Path $projectFile -Value $xml -Encoding utf8

@'
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using OrbusRebornManager;

static class Smoke
{
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Smoke test failed: " + message);
    }

    static void MakeClient(string folder, string value)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "OrbusVR.exe"), value);
        File.WriteAllText(Path.Combine(folder, "GameAssembly.dll"), value);
        string data = Path.Combine(folder, "OrbusVR_Data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "content.dat"), value);
    }

    static async Task Main()
    {
        var store = new InstanceStore();
        if (store.Load().Count != 0)
            throw new Exception("CI instance registry is not empty.");

        string root = Path.Combine(Path.GetTempPath(), "orbus-smoke-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "My Renewed");
        string future = Path.Combine(root, "archive-source", "New-Client");
        Directory.CreateDirectory(root);
        try
        {
            MakeClient(game, "v1");
            string bepinex = Path.Combine(game, "BepInEx");
            Directory.CreateDirectory(Path.Combine(bepinex, "plugins"));
            Directory.CreateDirectory(Path.Combine(bepinex, "config"));
            Directory.CreateDirectory(Path.Combine(bepinex, "interop"));
            File.WriteAllText(Path.Combine(bepinex, "plugins", "RebornCam.dll"), "plugin");
            File.WriteAllText(Path.Combine(bepinex, "config", "camera.cfg"), "settings");
            File.WriteAllText(Path.Combine(bepinex, "interop", "old-generated.dll"), "stale");
            File.WriteAllText(Path.Combine(game, "winhttp.dll"), "loader");
            File.WriteAllText(Path.Combine(game, "doorstop_config.ini"), "config");
            Directory.CreateDirectory(Path.Combine(bepinex, "core"));
            File.WriteAllText(Path.Combine(bepinex, "core", "BepInEx.Unity.IL2CPP.dll"), "core");
            Directory.CreateDirectory(Path.Combine(bepinex, "cache"));
            File.WriteAllText(Path.Combine(bepinex, "cache", "old-generated.bin"), "stale-cache");
            // Regression: a Spades 1.13 -> 1.14 update previously lost the
            // IL2CPP BepInEx runtime because only BepInEx and winhttp were kept.
            string runtime = Path.Combine(game, "dotnet");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "coreclr.dll"), "original-coreclr");
            File.WriteAllText(Path.Combine(runtime, "System.Private.CoreLib.dll"), "original-corlib");

            var old = new GameInstance {
                Name = "My Renewed", Path = game, CreatedByManager = true,
                Edition = GameReleaseDownloadService.Renewed, GameVersion = "v1"
            };
            store.Save(new[] { old });

            MakeClient(future, "v2");
            string zip = Path.Combine(root, "update.zip");
            ZipFile.CreateFromDirectory(Path.Combine(root, "archive-source"), zip);
            long size = new FileInfo(zip).Length;
            string sha;
            using (var stream = File.OpenRead(zip))
                sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            string cache = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "OrbusRebornManager", "GameDownloadCache");
            Directory.CreateDirectory(cache);
            File.Copy(zip, Path.Combine(cache, "renewed-" + sha + ".zip"));

            var release = new GameReleaseDownloadService.GameReleaseInfo(
                GameReleaseDownloadService.Renewed, "v2",
                "https://github.com/FourOfSpades4/reborn-server/releases/download/v2/OrbusVRRenewed.zip",
                sha, size);

            using (var cancel = new System.Threading.CancellationTokenSource())
            {
                cancel.Cancel();
                try {
                    await GameUpdateService.UpdateAsync(old, release, null, cancel.Token);
                    throw new Exception("Cancellation was ignored.");
                } catch (OperationCanceledException) { }
            }
            Assert(File.ReadAllText(Path.Combine(game, "OrbusVR.exe")) == "v1", "cancel touched original");

            await GameUpdateService.UpdateAsync(old, release, null);
            var updated = store.Load().Single();
            Assert(updated.GameVersion == "v2", "updated version not recorded");
            Assert(File.ReadAllText(Path.Combine(game, "OrbusVR.exe")) == "v2", "new game not installed");
            Assert(File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "RebornCam.dll")) == "plugin", "mod lost");
            Assert(File.ReadAllText(Path.Combine(game, "BepInEx", "config", "camera.cfg")) == "settings", "config lost");
            Assert(!File.Exists(Path.Combine(game, "BepInEx", "interop", "old-generated.dll")), "stale interop kept");
            Assert(File.ReadAllText(Path.Combine(game, "dotnet", "coreclr.dll")) == "original-coreclr",
                "update lost IL2CPP coreclr runtime; BepInEx would not start");
            Assert(File.ReadAllText(Path.Combine(game, "dotnet", "System.Private.CoreLib.dll")) == "original-corlib",
                "update lost managed runtime libraries");
            Assert(File.ReadAllText(Path.Combine(game, "doorstop_config.ini")) == "config",
                "doorstop config was lost");
            Assert(!File.Exists(Path.Combine(game, "BepInEx", "cache", "old-generated.bin")),
                "stale generated BepInEx cache retained");
            Assert(Directory.Exists(updated.BackupPath), "rollback folder not created");
            Assert(File.ReadAllText(Path.Combine(updated.BackupPath, "OrbusVR.exe")) == "v1", "backup incorrect");

            // Simulate the native Spades in-game updater deleting BepInEx
            // after RM updated the game. Recover without downgrading the
            // current executable or overwriting a player's existing plugins.
            Directory.Delete(Path.Combine(game, "BepInEx"), recursive: true);
            Directory.Delete(Path.Combine(game, "dotnet"), recursive: true);
            File.Delete(Path.Combine(game, "winhttp.dll"));
            File.Delete(Path.Combine(game, "doorstop_config.ini"));
            Directory.CreateDirectory(Path.Combine(game, "BepInEx", "plugins"));
            File.WriteAllText(Path.Combine(game, "BepInEx", "plugins", "RebornCam.dll"),
                "user-modified");
            File.WriteAllText(Path.Combine(game, "BepInEx", "plugins", "Manual.dll"),
                "user-owned");
            int recovered = await GameUpdateService.RestoreMissingModSetupAsync(updated);
            Assert(recovered > 0, "recover operation restored no missing files");
            Assert(File.ReadAllText(Path.Combine(game, "OrbusVR.exe")) == "v2",
                "recovery changed current game executable");
            Assert(File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "RebornCam.dll")) == "user-modified",
                "recovery overwrote an existing plugin");
            Assert(File.ReadAllText(Path.Combine(game, "BepInEx", "plugins", "Manual.dll")) == "user-owned",
                "recovery removed a manually installed plugin");
            Assert(File.ReadAllText(Path.Combine(game, "BepInEx", "config", "camera.cfg")) == "settings",
                "recovery lost config");
            Assert(File.ReadAllText(Path.Combine(game, "winhttp.dll")) == "loader",
                "recovery did not restore loader bootstrap");
            Assert(File.ReadAllText(Path.Combine(game, "dotnet", "coreclr.dll")) == "original-coreclr",
                "recovery did not restore runtime");
            Assert(!File.Exists(Path.Combine(game, "BepInEx", "interop", "old-generated.dll")),
                "recovery copied stale interop for the older game");
            Assert(store.Load().Single().GameVersion == "v2",
                "mod setup recovery altered game version registry");

            await GameUpdateService.RollbackAsync(updated);
            var restored = store.Load().Single();
            Assert(restored.GameVersion == "v1", "rollback version not recorded");
            Assert(File.ReadAllText(Path.Combine(game, "OrbusVR.exe")) == "v1", "rollback failed");
            Assert(File.ReadAllText(Path.Combine(game, "BepInEx", "interop", "old-generated.dll")) == "stale", "original not restored");
            Assert(File.ReadAllText(Path.Combine(game, "dotnet", "coreclr.dll")) == "original-coreclr",
                "rollback lost IL2CPP runtime");
            Assert(File.ReadAllText(Path.Combine(restored.BackupPath, "OrbusVR.exe")) == "v2", "newer build lost");

            await GameUpdateService.RollbackAsync(restored);
            Assert(store.Load().Single().GameVersion == "v2", "redo version not recorded");
            Assert(File.ReadAllText(Path.Combine(game, "dotnet", "coreclr.dll")) == "original-coreclr",
                "reapply lost IL2CPP runtime");
            Console.WriteLine("PASS: cancellation, SHA-256 cache, verified mod preservation, native-updater wipe recovery without overwrites, interop regeneration, update and two-way rollback");
        }
        finally
        {
            store.Save(Array.Empty<GameInstance>());
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
'@ | Set-Content -Path (Join-Path $project "Program.cs") -Encoding utf8

dotnet run --project $projectFile -c Release
if ($LASTEXITCODE -ne 0) { throw "Game update smoke test failed" }
