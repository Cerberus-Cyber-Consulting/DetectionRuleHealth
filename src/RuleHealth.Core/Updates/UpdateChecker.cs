using System.Net;
using System.Text.Json;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Core.Updates;

/// <summary>A GitHub release that could be offered.</summary>
public sealed record ReleaseInfo(string Version, string Url, DateTime? PublishedAt, string? Notes);

/// <summary>What /api/app returns as `update` when a newer version exists for this user.</summary>
public sealed record UpdateInfo(string Current, string Latest, string Url, DateTime? PublishedAt, string? Notes);

/// <summary>
/// &lt;data&gt;/.app/update-check.json (schemas/update-check.schema.json): app state, not tenant data.
/// Keeps the newest stable and the newest release overall, so the right one can be offered after an
/// upgrade from a beta to a stable version even when GitHub answers 304.
/// </summary>
public sealed record UpdateCheckCache
{
    public int SchemaVersion { get; init; } = Documents.SchemaVersion;
    public DateTime CheckedAt { get; init; }
    public string? Etag { get; init; }
    public ReleaseInfo? LatestStable { get; init; }
    /// <summary>The newest release including prereleases (offered to beta users only).</summary>
    public ReleaseInfo? Latest { get; init; }
}

/// <summary>
/// The daily "new version available" check against the GitHub Releases API (skill rulehealth-release).
/// Anonymous, one call at start-up and then every 24 hours, with the cached ETag (a 304 does not count
/// against GitHub's rate limit). It never downloads or installs anything. Any failure (offline, rate
/// limit, proxy) is silent: one debug line, and the cached result stays.
/// RULEHEALTH_UPDATE_CHECK=false means no instance is created: no call at all.
/// </summary>
public sealed class UpdateChecker
{
    public const string ReleasesUrl = "https://api.github.com/repos/Cerberus-Cyber-Consulting/DetectionRuleHealth/releases?per_page=20";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private const int NotesLength = 500;

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _cachePath;
    private readonly string _current;
    private readonly HttpClient _http;
    private readonly Action<string> _debugLog;
    private readonly Func<DateTime> _now;
    private UpdateCheckCache? _cache;

    /// <param name="dataRoot">The data folder; the cache goes to &lt;data&gt;/.app/update-check.json.</param>
    /// <param name="currentVersion">AppInfo.Version.</param>
    /// <param name="debugLog">Failures go here (RULEHEALTH_LOG_LEVEL=debug); never to the UI.</param>
    public UpdateChecker(string dataRoot, string currentVersion, HttpClient? http = null, Action<string>? debugLog = null, Func<DateTime>? now = null)
    {
        _cachePath = Path.Combine(dataRoot, ".app", "update-check.json");
        _current = currentVersion;
        _http = http ?? Shared;
        _debugLog = debugLog ?? (_ => { });
        _now = now ?? (() => DateTime.UtcNow);
        _cache = ReadCache();
    }

    /// <summary>The newer version for this user from the last successful check (or the cache), else null.</summary>
    public UpdateInfo? Update => Offer(_current, _cache);

    /// <summary>When GitHub last answered (200 or 304); null before the first successful check.</summary>
    public DateTime? CheckedAt => _cache?.CheckedAt;

    /// <summary>Checks now; then every <see cref="Interval"/> until <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await CheckAsync(cancellationToken);
            try { await Task.Delay(Interval, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One check. Never throws for network or GitHub problems.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.UserAgent.ParseAdd($"DetectionRuleHealth/{_current}");
            if (_cache?.Etag is { Length: > 0 } etag)
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified && _cache is not null)
            {
                Save(_cache with { CheckedAt = _now() });
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                _debugLog($"update check: GitHub answered {(int)response.StatusCode}; keeping the last result");
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var (stable, latest) = Pick(json.RootElement);
            Save(new UpdateCheckCache
            {
                CheckedAt = _now(),
                Etag = response.Headers.ETag?.ToString(),
                LatestStable = stable,
                Latest = latest,
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException
                                       && !cancellationToken.IsCancellationRequested)
        {
            _debugLog($"update check failed ({ex.GetType().Name}: {ex.Message}); keeping the last result");
        }
    }

    /// <summary>
    /// The newest stable release and the newest release overall from a GitHub releases array. Drafts
    /// and tags that are not SemVer are ignored.
    /// </summary>
    public static (ReleaseInfo? Stable, ReleaseInfo? Latest) Pick(JsonElement releases)
    {
        (SemVersion Version, ReleaseInfo Info)? stable = null, latest = null;
        if (releases.ValueKind != JsonValueKind.Array)
            return (null, null);
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            if (!SemVersion.TryParse(Text(release, "tag_name"), out var version) || Text(release, "html_url") is not { } url)
                continue;
            var notes = Text(release, "body");
            var info = new ReleaseInfo(version.ToString(), url,
                release.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String &&
                published.TryGetDateTime(out var at) ? at.ToUniversalTime() : null,
                notes is null ? null : notes.Length > NotesLength ? notes[..NotesLength] : notes);
            if (latest is null || version > latest.Value.Version)
                latest = (version, info);
            if (!version.IsPrerelease && (stable is null || version > stable.Value.Version))
                stable = (version, info);
        }
        return (stable?.Info, latest?.Info);
    }

    /// <summary>
    /// What to offer the user running <paramref name="current"/>: prereleases only to prerelease users
    /// (a stable user is never pushed to a beta), and only when it is newer. Null otherwise.
    /// </summary>
    public static UpdateInfo? Offer(string current, UpdateCheckCache? cache)
    {
        if (cache is null || !SemVersion.TryParse(current, out var running))
            return null;
        var candidate = running.IsPrerelease ? cache.Latest : cache.LatestStable;
        if (candidate is null || !SemVersion.TryParse(candidate.Version, out var offered) || !(offered > running))
            return null;
        return new UpdateInfo(running.ToString(), candidate.Version, candidate.Url, candidate.PublishedAt, candidate.Notes);
    }

    private UpdateCheckCache? ReadCache()
    {
        try
        {
            return File.Exists(_cachePath) ? JsonStore.ReadFile<UpdateCheckCache>(_cachePath) : null;
        }
        catch (Exception ex) when (ex is RuleHealthException or IOException or JsonException)
        {
            _debugLog($"update check: ignoring {_cachePath} ({ex.Message})");
            return null;
        }
    }

    private void Save(UpdateCheckCache cache)
    {
        _cache = cache;
        try
        {
            JsonStore.WriteFile(_cachePath, cache);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _debugLog($"update check: could not write {_cachePath} ({ex.Message})");
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
