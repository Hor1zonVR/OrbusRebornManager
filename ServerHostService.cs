using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OrbusRebornManager;

// Runs Docker Compose locally with an argument list: NEVER through cmd.exe.
public sealed class ServerHostService
{
    public static string ValidateServerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Choose the folder containing your Orbus server's docker-compose.yml.");
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        if (File.Exists(full) && Path.GetFileName(full).Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase))
            full = Path.GetDirectoryName(full)!;
        if (!File.Exists(Path.Combine(full, "docker-compose.yml")) ||
            !Directory.Exists(Path.Combine(full, "mods")) ||
            !Directory.Exists(Path.Combine(full, "content")))
            throw new InvalidOperationException("This folder isn't an Orbus Reborn server checkout (expected docker-compose.yml, mods and content).");
        return full;
    }

    public async Task<string?> DetectAsync()
    {
        // Prefer a known running Compose project, even if it lives outside Documents.
        try
        {
            string output = await RunDockerAsync(null, 12000, "compose", "ls", "--format", "json");
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var project in document.RootElement.EnumerateArray())
                {
                    if (!project.TryGetProperty("Name", out var name) ||
                        !name.GetString()!.Contains("reborn", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!project.TryGetProperty("ConfigFiles", out var configs)) continue;
                    string listed = configs.ValueKind == JsonValueKind.String ? configs.GetString() ?? "" : "";
                    foreach (string candidate in listed.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try { return ValidateServerPath(candidate.Trim()); }
                        catch (InvalidOperationException) { }
                        catch (ArgumentException) { }
                    }
                }
            }
        }
        catch { /* Docker may not be running; try local folders next. */ }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates = {
            Path.Combine(home, "OneDrive", "Documents", "OrbusRebornServer"),
            Path.Combine(home, "Documents", "OrbusRebornServer"),
            Path.Combine(home, "OrbusRebornServer")
        };
        foreach (string folder in candidates)
        {
            try { return ValidateServerPath(folder); }
            catch { /* Not installed in this location. */ }
        }
        return null;
    }

    public Task<string> ComposeAsync(string serverPath, int timeoutMs, params string[] args)
    {
        string folder = ValidateServerPath(serverPath);
        return RunDockerAsync(folder, timeoutMs, new[] { "compose", "-f", Path.Combine(folder, "docker-compose.yml") }.Concat(args).ToArray());
    }

    public async Task<ServerHostStatus> GetStatusAsync(string serverPath)
    {
        string output = await ComposeAsync(serverPath, 20000, "ps", "--format", "json");
        var services = new List<ServerServiceStatus>();
        string[] lines = output.TrimStart().StartsWith("[", StringComparison.Ordinal)
            ? new[] { output } : output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                    services.Add(ParseStatus(element));
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                services.Add(ParseStatus(doc.RootElement));
        }
        return new ServerHostStatus(services);
    }

    // Docker Desktop's engine is separate from the Orbus containers. On Windows it
    // may be stopped even though Docker Compose and the docker.exe CLI are installed.
    public async Task<bool> DockerReadyAsync()
    {
        try
        {
            await RunDockerAsync(null, 7000, "info", "--format", "{{.ServerVersion}}");
            return true;
        }
        catch { return false; }
    }

    private static string? FindDockerDesktopExe()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Docker", "Docker", "Docker Desktop.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "DockerDesktop", "Docker Desktop.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Docker", "Docker", "Docker Desktop.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task EnsureDockerStartedAsync()
    {
        if (await DockerReadyAsync()) return;

        // Supported on recent Docker Desktop versions. Do not start the Orbus
        // server until the daemon can actually answer "docker info".
        bool started = false;
        try
        {
            await RunDockerAsync(null, 20000, "desktop", "start", "--detach");
            started = true;
        }
        catch
        {
            // Older desktop builds may not have the docker desktop CLI plugin.
        }

        if (!started && !await DockerReadyAsync())
        {
            string? executable = FindDockerDesktopExe();
            if (executable == null)
                throw new InvalidOperationException(
                    "Docker Desktop is not responding and its executable could not be found. Install or open Docker Desktop.");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executable)!
                });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not launch Docker Desktop: " + ex.Message, ex);
            }
        }

        // Docker Desktop often launches before its Linux engine is ready.
        // Poll for readiness; do not mistake a launched window for a running daemon.
        for (int i = 0; i < 45; i++)
        {
            if (await DockerReadyAsync()) return;
            await Task.Delay(2500);
        }
        throw new TimeoutException(
            "Docker Desktop was launched but its engine did not become ready. Open Docker Desktop to check its startup status.");
    }

    public async Task<string> StartAsync(string serverPath)
    {
        string folder = ValidateServerPath(serverPath);
        await EnsureDockerStartedAsync();
        return await ComposeAsync(folder, 180000, "up", "-d");
    }
    // stop, NOT down, to preserve running containers and the database volume
    public Task<string> StopAsync(string serverPath) => ComposeAsync(serverPath, 60000, "stop");
    public Task<string> RestartAsync(string serverPath) => ComposeAsync(serverPath, 90000, "restart", "game", "api");
    public Task<string> ReloadComponentsAsync(string serverPath) => ComposeAsync(serverPath, 60000, "restart", "game");
    // Poll the latest lines rather than opening a long-running Docker stream. A console tab
    // refreshes this on a short timer while it is visible; it never accumulates processes.
    public Task<string> LogsAsync(string serverPath) => ComposeAsync(serverPath, 30000,
        "logs", "--no-color", "--timestamps", "--tail", "180", "game", "api");

    private static ServerServiceStatus ParseStatus(JsonElement obj)
    {
        static string Value(JsonElement o, string property) =>
            o.TryGetProperty(property, out var x) && x.ValueKind == JsonValueKind.String
                ? x.GetString() ?? "" : "";
        return new ServerServiceStatus(Value(obj,"Service"),Value(obj,"State"),Value(obj,"Status"));
    }

    private static async Task<string> RunDockerAsync(string? cwd, int timeoutMs, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory
        };
        foreach (var a in args) process.StartInfo.ArgumentList.Add(a);
        try { process.Start(); }
        catch (Exception ex) { throw new InvalidOperationException("Docker Desktop couldn't be launched. Check Docker is installed and running. " + ex.Message, ex); }
        using var timeout = new CancellationTokenSource(timeoutMs);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout;
            string error = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Docker command failed: " + (string.IsNullOrWhiteSpace(error) ? output : error).Trim()[..Math.Min(1000, (string.IsNullOrWhiteSpace(error) ? output : error).Trim().Length)]);
            return output.Trim();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Docker took too long to respond. Check Docker Desktop.");
        }
    }
}

public sealed record ServerServiceStatus(string Service, string State, string Detail);
public sealed class ServerHostStatus
{
    public IReadOnlyList<ServerServiceStatus> Services { get; }
    public ServerHostStatus(IReadOnlyList<ServerServiceStatus> services) => Services = services;
    public bool Running => Services.Any(s => s.Service == "game" && s.State.Equals("running", StringComparison.OrdinalIgnoreCase));
    public string Summary => Services.Count == 0 ? "Stopped (no running containers)" :
        string.Join("  ·  ", Services.Where(s => s.Service is "game" or "api" or "redis" or "rethinkdb")
            .Select(s => s.Service + ": " + (string.IsNullOrWhiteSpace(s.State) ? "unknown" : s.State)));
}
