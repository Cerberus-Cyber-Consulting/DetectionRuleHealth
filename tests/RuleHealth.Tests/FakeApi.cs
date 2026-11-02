using System.Net;
using System.Text;
using RuleHealth.Core.Clients;

namespace RuleHealth.Tests;

/// <summary>
/// An ApiConnection whose three HttpClients answer from canned responses instead of the network.
/// Routes match on "contains" against the full request URL; the first match wins.
/// </summary>
public sealed class FakeApi : IDisposable
{
    public static readonly string Fixtures = Path.Combine(TestPaths.RepoRoot, "tests", "Fixtures");

    private readonly FakeHandler _handler = new();

    public FakeApi(RawResponseLog? raw = null)
    {
        Connection = new ApiConnection(new HttpClient(_handler, false), new HttpClient(_handler, false), new HttpClient(_handler, false), raw);
    }

    public ApiConnection Connection { get; }

    /// <summary>Every request URL, in order.</summary>
    public List<string> Requests => _handler.Requests;

    public FakeApi Route(string urlContains, string fixtureFile) =>
        RouteBody(urlContains, HttpStatusCode.OK, File.ReadAllText(Path.Combine(Fixtures, fixtureFile)));

    public FakeApi RouteBody(string urlContains, HttpStatusCode status, string body, string? requestId = null)
    {
        _handler.Routes.Add((urlContains, status, body, requestId));
        return this;
    }

    public void Dispose()
    {
        Connection.Dispose();
        _handler.Dispose();
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string UrlContains, HttpStatusCode Status, string Body, string? RequestId)> Routes { get; } = new();
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            foreach (var route in Routes)
            {
                if (!url.Contains(route.UrlContains, StringComparison.Ordinal))
                    continue;
                var response = new HttpResponseMessage(route.Status)
                {
                    Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
                };
                if (route.RequestId is not null)
                    response.Headers.Add("request-id", route.RequestId);
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($$$"""{"error":{"code":"NoRoute","message":"no fake route for {{{url}}}"}}"""),
            });
        }
    }
}
