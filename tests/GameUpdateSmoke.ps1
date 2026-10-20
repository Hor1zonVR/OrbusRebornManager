$ErrorActionPreference = "Stop"
if (-not $env:GITHUB_ACTIONS) { throw "This test modifies a temporary instance registry. Only run on GitHub Actions." }

$project = Join-Path $env:RUNNER_TEMP "OrbusRM-GameUpdateSmoke"
dotnet new console -n Smoke -o $project -f net8.0 --force
if ($LASTEXITCODE -ne 0) { throw "Could not create test project" }

$projectFile = Join-Path $project "Smoke.csproj"
$refs = @("InstanceStore.cs", "GameClientLayout.cs", "GameReleaseDownloadService.cs", "GameUpdateService.cs")
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
            Assert(Directory.Exists(updated.BackupPath), "rollback folder not created");
            Assert(File.ReadAllText(Path.Combine(updated.BackupPath, "OrbusVR.exe")) == "v1", "backup incorrect");

            await GameUpdateService.RollbackAsync(updated);
            var restored = store.Load().Single();
            Assert(restored.GameVersion == "v1", "rollback version not recorded");
            Assert(File.ReadAllText(Path.Combine(game, "OrbusVR.exe")) == "v1", "rollback failed");
            Assert(File.ReadAllText(Path.Combine(game, "BepInEx", "interop", "old-generated.dll")) == "stale", "original not restored");
            Assert(File.ReadAllText(Path.Combine(restored.BackupPath, "OrbusVR.exe")) == "v2", "newer build lost");

            await GameUpdateService.RollbackAsync(restored);
            Assert(store.Load().Single().GameVersion == "v2", "redo version not recorded");
            Console.WriteLine("PASS: cancellation, SHA-256 cache, preserved mods/config, interop regeneration, update and two-way rollback");
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
