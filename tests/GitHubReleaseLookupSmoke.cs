using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using OrbusRebornManager;

sealed class CountingHandler : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"tag_name\":\"v3.4.5\",\"draft\":false,\"prerelease\":false,\"assets\":[]}"),
            Headers = { ETag = new EntityTagHeaderValue("\"cache-test\"") }
        });
    }
}

internal static class Program
{
    private static async Task Main()
    {
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler);

        string address =
            "https://api.github.com/repos/Hor1zonVR/ReleaseLookupTests-" +
            Guid.NewGuid().ToString("N") + "/releases/latest";
        var a = await GitHubReleaseLookup.GetAsync(http, address);
        var b = await GitHubReleaseLookup.GetAsync(http, address);
        if (a.Stale || b.Stale || !a.Json.Contains("v3.4.5") ||
            a.Json != b.Json || handler.Calls != 1)
            throw new Exception("Release metadata should be cached between checks.");

        // Even forced checks have a minimum one-minute guard, to stop a
        // clicking user from issuing dozens of requests per minute.
        var c = await GitHubReleaseLookup.GetAsync(http, address, forceRefresh: true);
        if (c.Stale || handler.Calls != 1)
            throw new Exception("The forced-refresh anti-spam guard failed.");

        try
        {
            _ = await GitHubReleaseLookup.GetAsync(
                http, "https://api.example.com/fake/releases/latest");
            throw new Exception("Non-GitHub metadata request was accepted.");
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("Unsupported GitHub release")) { }

        Console.WriteLine("PASS: GitHub release cache, forced-refresh throttle and host validation.");
    }
}
