using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RuleHealth.Cli.Serve;
using RuleHealth.Core.Auth;

namespace RuleHealth.Tests;

/// <summary>Starts a real <see cref="StaticServer"/> on a free loopback port and makes clients that behave like the UI.</summary>
public static class TestServer
{
    public static StaticServer Start(string dataRoot, EntraTokenClient? entra = null, bool updateCheck = true, string? samples = null)
    {
        var port = FreePort();
        var server = new StaticServer(dataRoot, TestPaths.Ui, $"http://localhost:{port}/", $"http://localhost:{port}", _ => { }, entra)
        {
            UpdateCheckEnabled = updateCheck,
            SamplesRoot = samples,
        };
        server.Start();
        return server;
    }

    /// <summary>
    /// What the UI does: get the session cookie and CSRF token from /api/session, then send the token
    /// and the page's Origin on every request. <paramref name="followRedirects"/> false to see the 302s.
    /// </summary>
    public static HttpClient Browser(StaticServer server, bool followRedirects = true)
    {
        var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = followRedirects })
        {
            BaseAddress = new Uri(server.BaseUrl),
        };
        using var session = JsonDocument.Parse(http.GetStringAsync("/api/session").GetAwaiter().GetResult());
        http.DefaultRequestHeaders.Add("Origin", Origin(server));
        http.DefaultRequestHeaders.Add(RequestGuard.CsrfHeader, session.RootElement.GetProperty("csrf").GetString());
        return http;
    }

    /// <summary>http://localhost:{port}: the only Origin the server accepts writes from.</summary>
    public static string Origin(StaticServer server) => server.BaseUrl.TrimEnd('/');

    public static int FreePort()
    {
        // Ask the OS for a free port, then release it for HttpListener (which cannot bind port 0).
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
