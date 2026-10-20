using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrbusRebornManager;

/// <summary>
/// Public GitHub release metadata only. Reduces anonymous API usage, honors
/// ETags, and gracefully handles rate limits. Never changes install targets
/// or trusts an unchecked download URL; callers validate assets as before.
/// </summary>
internal static class GitHubReleaseLookup
{
    private sealed class CacheEntry
    {
        public string Address { get; set; } = "";
        public string Json { get; set; } = "";
        public string? ETag { get; set; }
        public DateTimeOffset CheckedUtc { get; set; }
    }

    internal readonly record struct Lookup(string Json, bool Stale);
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly Regex ApprovedEndpoint = new(
        @"^https://api\.github\.com/repos/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9._-]+)/releases/(?<action>latest|tags/[A-Za-z0-9._+%\-]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static DateTimeOffset _anonymousBlockedUntil;
    private static bool _cliUnavailable;

    private static string CacheFile(string address)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(address));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrbusRebornManager", "ReleaseMetadataCache",
            Convert.ToHexString(hash) + ".json");
    }

    private static CacheEntry? ReadCache(string address)
    {
        try
        {
            string file = CacheFile(address);
            if (!File.Exists(file)) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file));
            return entry != null && entry.Address == address &&
                   entry.Json.Length is > 0 and < 2_000_000 &&
                   entry.CheckedUtc <= DateTimeOffset.UtcNow
                ? entry : null;
        }
        catch { return null; }
    }

    private static void WriteCache(string address, string json, string? etag)
    {
        try
        {
            // Validate that a plausible JSON document was received before
            // storing it. Full release/asset validation remains at call sites.
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) return;
            string file = CacheFile(address);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string staged = file + ".new";
            File.WriteAllText(staged, JsonSerializer.Serialize(new CacheEntry
            {
                Address = address,
                Json = json,
                ETag = etag,
                CheckedUtc = DateTimeOffset.UtcNow
            }));
            File.Move(staged, file, overwrite: true);
        }
        catch { /* An unwritable cache must never prevent a real update. */ }
    }

    private static DateTimeOffset ResetAt(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long seconds))
        {
            try
            {
                DateTimeOffset reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (reset > DateTimeOffset.UtcNow) return reset;
            }
            catch { }
        }
        return DateTimeOffset.UtcNow.AddMinutes(20);
    }

    private static bool IsRateLimited(HttpResponseMessage response, string body) =>
        response.StatusCode == HttpStatusCode.TooManyRequests ||
        (response.StatusCode == HttpStatusCode.Forbidden &&
         (body.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
          response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
          values.Any(v => v == "0")));

    /// <summary>
    /// If the user already has an authenticated gh CLI, use it to READ public
    /// release metadata after the anonymous API quota is exhausted. Does not
    /// print or request credentials, run a shell, or make account changes.
    /// </summary>
    private static async Task<string?> TryAuthenticatedGhAsync(
        string address, CancellationToken cancellationToken)
    {
        if (_cliUnavailable) return null;
        Match match = ApprovedEndpoint.Match(address);
        if (!match.Success) return null;

        string resource = "repos/" + match.Groups["owner"].Value + "/" +
                          match.Groups["repo"].Value + "/releases/" +
                          match.Groups["action"].Value;
        try
        {
            var start = new ProcessStartInfo("gh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("api");
            start.ArgumentList.Add(resource);
            using var process = Process.Start(start);
            if (process == null) { _cliUnavailable = true; return null; }
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (cancellationToken.IsCancellationRequested) throw;
                _cliUnavailable = true;
                return null;
            }

            string json = await output;
            _ = await errors; // Do not expose CLI stderr; it may contain account context.
            if (process.ExitCode != 0 || json.Length is < 2 or > 2_000_000)
            {
                _cliUnavailable = true;
                return null;
            }
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                !parsed.RootElement.TryGetProperty("tag_name", out _))
                return null;
            return json;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _cliUnavailable = true;
            return null;
        }
    }

    internal static async Task<Lookup> GetAsync(
        HttpClient client, string address,
        bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!ApprovedEndpoint.IsMatch(address))
            throw new InvalidOperationException("Unsupported GitHub release metadata address.");

        var gate = Gates.GetOrAdd(address, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            CacheEntry? cached = ReadCache(address);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // Even explicit refreshes should not issue dozens of requests a
            // minute. Force only bypasses the regular 15-minute cache window.
            TimeSpan minimum = forceRefresh ? TimeSpan.FromMinutes(1) : FreshFor;
            if (cached != null && now - cached.CheckedUtc < minimum)
                return new Lookup(cached.Json, Stale: false);

            if (now >= _anonymousBlockedUntil)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address);
                if (!string.IsNullOrWhiteSpace(cached?.ETag) &&
                    EntityTagHeaderValue.TryParse(cached.ETag, out var tag))
                    request.Headers.IfNoneMatch.Add(tag);

                try
                {
                    using var response = await client.SendAsync(
                        request, HttpCompletionOption.ResponseContentRead,
                        cancellationToken);
                    if (response.StatusCode == HttpStatusCode.NotModified &&
                        cached != null)
                    {
                        WriteCache(address, cached.Json, cached.ETag);
                        return new Lookup(cached.Json, Stale: false);
                    }
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (json.Length is < 2 or > 2_000_000)
                            throw new InvalidOperationException("Invalid GitHub release metadata size.");
                        WriteCache(address, json, response.Headers.ETag?.ToString());
                        return new Lookup(json, Stale: false);
                    }

                    string message = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (IsRateLimited(response, message))
                        _anonymousBlockedUntil = ResetAt(response);
                    else if (response.StatusCode == HttpStatusCode.NotFound)
                        throw new FileNotFoundException("No matching published GitHub release.");
                    else response.EnsureSuccessStatusCode();
                }
                catch (HttpRequestException) when (cached != null)
                {
                    // Network unavailable; use previously validated release
                    // metadata, while clearly labelling it as stale.
                    return new Lookup(cached.Json, Stale: true);
                }
            }

            // A local gh login is optional. This allows authenticated public
            // metadata checks without storing secrets in RebornManager.
            string? viaCli = await TryAuthenticatedGhAsync(address, cancellationToken);
            if (viaCli != null)
            {
                WriteCache(address, viaCli, null);
                return new Lookup(viaCli, Stale: false);
            }
            if (cached != null)
                return new Lookup(cached.Json, Stale: true);

            string reset = _anonymousBlockedUntil > DateTimeOffset.UtcNow
                ? _anonymousBlockedUntil.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture)
                : "later";
            throw new InvalidOperationException(
                "GitHub's anonymous API rate limit has been reached. " +
                "Release checks can resume around " + reset + ". " +
                "Leave RM open or try again later. If you've already signed in " +
                "using the GitHub CLI (gh), RM can use that login for read-only release checks.");
        }
        finally { gate.Release(); }
    }
}
