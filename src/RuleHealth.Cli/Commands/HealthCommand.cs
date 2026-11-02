using System.Text.Json;

namespace RuleHealth.Cli.Commands;

/// <summary>
/// `rulehealth health`: asks the running serve process for /healthz and exits 0 (healthy) or 1.
/// This is the Docker HEALTHCHECK; the chiseled image has no shell or curl, so the binary checks itself.
/// </summary>
public static class HealthCommand
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static async Task<int> RunAsync(CliArgs cli)
    {
        cli.AllowOnly();
        var port = cli.Settings.ListenPort;
        if (await CheckAsync(port, Timeout))
        {
            Console.WriteLine($"healthy (port {port})");
            return 0;
        }
        Console.WriteLine($"unhealthy: no healthy answer from http://127.0.0.1:{port}/healthz");
        return 1;
    }

    /// <summary>
    /// True when /healthz on loopback answers 200 with status "ok". It connects to 127.0.0.1 and then
    /// [::1] (no name lookup): on Linux a listener bound to the name localhost listens on ::1 only.
    /// It sends Host: localhost, because outside Docker the listener is bound to the name localhost
    /// and HttpListener rejects other host names.
    /// </summary>
    public static async Task<bool> CheckAsync(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = timeout };
        foreach (var address in new[] { "127.0.0.1", "[::1]" })
        {
            if (await CheckAsync(http, $"http://{address}:{port}/healthz", port))
                return true;
        }
        return false;
    }

    private static async Task<bool> CheckAsync(HttpClient http, string url, int port)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Host = $"localhost:{port}";
        try
        {
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.TryGetProperty("status", out var status) && status.GetString() == "ok";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }
}
