using System.Net;
using System.Text;
using System.Text.Json;
using RuleHealth.Core.Updates;

namespace RuleHealth.Tests;

/// <summary>The "new version available" check (skill rulehealth-release). GitHub is a fake handler.</summary>
public sealed class UpdateCheckTests : IDisposable
{
    private readonly string _data = TestPaths.NewTempDirectory();

    public void Dispose() => Directory.Delete(_data, recursive: true);

    // ------------------------------------------------------------------ SemVer

    [Fact]
    public void Beta_numbers_compare_as_numbers_and_the_release_ranks_above_its_betas()
    {
        var order = new[] { "0.1.0-beta.2", "0.1.0-beta.10", "0.1.0" }.Select(Parse).ToList();

        Assert.True(order[0] < order[1]);
        Assert.True(order[1] < order[2]);
        Assert.True(order[0] < order[2]);
    }

    [Fact]
    public void Precedence_follows_the_semver_spec_example()
    {
        // semver.org §11: 1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-alpha.beta < 1.0.0-beta < 1.0.0-beta.2 < 1.0.0-beta.11 < 1.0.0-rc.1 < 1.0.0
        var spec = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0" };

        var shuffled = spec.Reverse().Select(Parse).OrderBy(v => v).Select(v => v.ToString());

        Assert.Equal(spec, shuffled);
        Assert.True(Parse("2.0.0") > Parse("1.10.0"));
        Assert.True(Parse("1.10.0") > Parse("1.9.0"));
    }

    [Theory]
    [InlineData("v0.1.0-beta.1", "0.1.0-beta.1")]
    [InlineData("0.1.0+3f2a9c1", "0.1.0")]        // build metadata is ignored
    [InlineData("V1.2.3", "1.2.3")]
    public void Tags_parse_with_a_v_prefix_and_build_metadata(string tag, string expected)
    {
        Assert.Equal(expected, Parse(tag).ToString());
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("")]
    [InlineData(null)]
    public void Tags_that_are_not_semver_are_rejected(string? tag)
    {
        Assert.False(SemVersion.TryParse(tag, out _));
    }

    // ------------------------------------------------------------------ what is offered

    private static readonly UpdateCheckCache StableAndBeta = new()
    {
        CheckedAt = DateTime.UtcNow,
        LatestStable = new ReleaseInfo("0.1.0", "https://example.test/0.1.0", null, null),
        Latest = new ReleaseInfo("0.2.0-beta.1", "https://example.test/0.2.0-beta.1", null, null),
    };

    [Theory]
    [InlineData("0.1.0", null)]                    // stable and up to date: never pushed to the newer beta
    [InlineData("0.0.9", "0.1.0")]                 // stable user: offered the newest stable only
    [InlineData("0.1.0-beta.2", "0.2.0-beta.1")]   // beta user: offered the newest release, betas included
    [InlineData("0.2.0-beta.1", null)]             // already on the newest
    [InlineData("0.0.0-dev", "0.2.0-beta.1")]      // a local build counts as a prerelease
    [InlineData("not-a-version", null)]
    public void Stable_users_never_get_a_beta(string current, string? offered)
    {
        Assert.Equal(offered, UpdateChecker.Offer(current, StableAndBeta)?.Latest);
    }

    [Fact]
    public void A_stable_user_is_offered_nothing_when_only_betas_exist()
    {
        var onlyBetas = StableAndBeta with { LatestStable = null };

        Assert.Null(UpdateChecker.Offer("0.1.0", onlyBetas));
        Assert.Equal("0.2.0-beta.1", UpdateChecker.Offer("0.1.0-beta.9", onlyBetas)?.Latest);
    }

    [Fact]
    public void Drafts_and_foreign_tags_are_ignored_and_notes_are_cut_at_500_characters()
    {
        using var json = JsonDocument.Parse(Releases(
            Release("v0.3.0", draft: true),
            Release("nightly-2026-10-30"),
            Release("v0.2.0-beta.1", body: new string('x', 900)),
            Release("v0.1.0"),
            Release("v0.1.0-beta.10")));

        var (stable, latest) = UpdateChecker.Pick(json.RootElement);

        Assert.Equal("0.1.0", stable?.Version);
        Assert.Equal("0.2.0-beta.1", latest?.Version);
        Assert.Equal(500, latest?.Notes?.Length);
        Assert.Equal("https://github.com/Cerberus-Cyber-Consulting/DetectionRuleHealth/releases/tag/v0.2.0-beta.1", latest?.Url);
        Assert.Equal(new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc), latest?.PublishedAt);
    }

    // ------------------------------------------------------------------ the call, the cache and failures

    [Fact]
    public async Task A_check_sends_the_github_headers_and_writes_the_cache()
    {
        var github = new FakeGitHub().Answer(HttpStatusCode.OK, Releases(Release("v0.1.0-beta.2")), etag: "\"abc\"");
        var checker = new UpdateChecker(_data, "0.1.0-beta.1", new HttpClient(github));

        await checker.CheckAsync(CancellationToken.None);

        var request = Assert.Single(github.Requests);
        Assert.Equal(UpdateChecker.ReleasesUrl, request.Url);
        Assert.Equal("application/vnd.github+json", request.Headers["Accept"]);
        Assert.Equal("2022-11-28", request.Headers["X-GitHub-Api-Version"]);
        Assert.Equal("DetectionRuleHealth/0.1.0-beta.1", request.Headers["User-Agent"]);
        Assert.False(request.Headers.ContainsKey("If-None-Match"));
        Assert.Equal(("0.1.0-beta.1", "0.1.0-beta.2"), (checker.Update?.Current, checker.Update?.Latest));

        var cacheFile = Path.Combine(_data, ".app", "update-check.json");
        using var written = JsonDocument.Parse(File.ReadAllText(cacheFile));
        var errors = new MiniJsonSchema(Path.Combine(TestPaths.Schemas, "update-check.schema.json")).Validate(written.RootElement);
        Assert.Empty(errors);
        Assert.Contains("\"etag\": \"\\\"abc\\\"\"", File.ReadAllText(cacheFile));
    }

    [Fact]
    public async Task The_next_check_sends_the_etag_and_a_304_keeps_the_result()
    {
        var first = new FakeGitHub().Answer(HttpStatusCode.OK, Releases(Release("v0.1.0-beta.2")), etag: "\"abc\"");
        await new UpdateChecker(_data, "0.1.0-beta.1", new HttpClient(first)).CheckAsync(CancellationToken.None);

        // A restart: the new checker starts from the cache file.
        var second = new FakeGitHub().Answer(HttpStatusCode.NotModified, "");
        var checker = new UpdateChecker(_data, "0.1.0-beta.1", new HttpClient(second));
        Assert.Equal("0.1.0-beta.2", checker.Update?.Latest); // known before any call
        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal("\"abc\"", second.Requests.Single().Headers["If-None-Match"]);
        Assert.Equal("0.1.0-beta.2", checker.Update?.Latest);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]          // rate limit
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_failing_check_is_silent_and_keeps_the_last_result(HttpStatusCode status)
    {
        await new UpdateChecker(_data, "0.1.0-beta.1", new HttpClient(new FakeGitHub().Answer(HttpStatusCode.OK, Releases(Release("v0.1.0-beta.2")))))
            .CheckAsync(CancellationToken.None);
        var debug = new List<string>();
        var checker = new UpdateChecker(_data, "0.1.0-beta.1", new HttpClient(new FakeGitHub().Answer(status, "{}")), debug.Add);

        await checker.CheckAsync(CancellationToken.None);

        Assert.Equal("0.1.0-beta.2", checker.Update?.Latest);
        Assert.Contains(debug, line => line.Contains(((int)status).ToString()));
    }

    [Fact]
    public async Task No_network_is_silent()
    {
        var debug = new List<string>();
        var checker = new UpdateChecker(_data, "0.1.0", new HttpClient(new FakeGitHub { Offline = true }), debug.Add);

        await checker.CheckAsync(CancellationToken.None);

        Assert.Null(checker.Update);
        Assert.Single(debug);
        Assert.False(File.Exists(Path.Combine(_data, ".app", "update-check.json")));
    }

    [Fact]
    public async Task The_cache_folder_is_never_listed_as_a_tenant()
    {
        await new UpdateChecker(_data, "0.1.0", new HttpClient(new FakeGitHub().Answer(HttpStatusCode.OK, Releases(Release("v0.2.0")))))
            .CheckAsync(CancellationToken.None);

        Assert.Empty(new Core.Storage.JsonStore(_data).ListTenants());
    }

    [Fact]
    public async Task App_route_reports_the_update()
    {
        var checker = new UpdateChecker(_data, "0.1.0-beta.1", new HttpClient(new FakeGitHub().Answer(HttpStatusCode.OK,
            Releases(Release("v0.1.0-beta.3")))));
        await checker.CheckAsync(CancellationToken.None);
        var port = TestServer.FreePort();
        using var server = new Cli.Serve.StaticServer(_data, TestPaths.Ui, $"http://localhost:{port}/", $"http://localhost:{port}", _ => { })
        {
            Updates = checker,
        };
        server.Start();
        using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };

        using var json = JsonDocument.Parse(await http.GetStringAsync("/api/app"));
        var update = json.RootElement.GetProperty("update");

        Assert.Equal(("0.1.0-beta.1", "0.1.0-beta.3"), (update.GetProperty("current").GetString(), update.GetProperty("latest").GetString()));
        Assert.StartsWith("https://github.com/", update.GetProperty("url").GetString());
        // The .NET container images set DOTNET_RUNNING_IN_CONTAINER, so the test follows the environment.
        var inContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        Assert.Equal(inContainer ? JsonValueKind.True : JsonValueKind.False, json.RootElement.GetProperty("runningInContainer").ValueKind);
    }

    // ------------------------------------------------------------------ helpers

    private static SemVersion Parse(string text) => SemVersion.TryParse(text, out var v) ? v : throw new FormatException(text);

    private static string Release(string tag, bool draft = false, string? body = "Notes") => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease = tag.Contains('-'), body,
        html_url = $"https://github.com/Cerberus-Cyber-Consulting/DetectionRuleHealth/releases/tag/{tag}",
        published_at = "2026-10-20T08:00:00Z",
    });

    private static string Releases(params string[] releases) => "[" + string.Join(",", releases) + "]";

    private sealed class FakeGitHub : HttpMessageHandler
    {
        private (HttpStatusCode Status, string Body, string? Etag) _answer = (HttpStatusCode.OK, "[]", null);

        public bool Offline { get; init; }
        public List<(string Url, Dictionary<string, string> Headers)> Requests { get; } = new();

        public FakeGitHub Answer(HttpStatusCode status, string body, string? etag = null)
        {
            _answer = (status, body, etag);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
                throw new HttpRequestException("No such host is known.");
            Requests.Add((request.RequestUri!.ToString(), request.Headers.ToDictionary(h => h.Key, h => string.Join(" ", h.Value))));
            var response = new HttpResponseMessage(_answer.Status) { Content = new StringContent(_answer.Body, Encoding.UTF8, "application/json") };
            if (_answer.Etag is not null)
                response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(_answer.Etag);
            return Task.FromResult(response);
        }
    }
}
