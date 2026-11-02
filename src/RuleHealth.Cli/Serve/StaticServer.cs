using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using RuleHealth.Core;
using RuleHealth.Core.Auth;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Jobs;
using RuleHealth.Core.Model;
using RuleHealth.Core.Operations;
using RuleHealth.Core.Storage;
using RuleHealth.Core.Updates;

namespace RuleHealth.Cli.Serve;

/// <summary>
/// The v1 "backend": a BCL HttpListener that serves ui/ and the tenant JSON files, and runs the
/// engine operations as background jobs so the UI can drive collect / extract / validate.
/// Routes (the v2 ASP.NET Core host must keep them identical):
///   GET  /  and  /ui/{path}                       static files from ui/
///   GET  /healthz                                 { status, version } for the Docker HEALTHCHECK; loopback callers only
///   GET  /api/app                                 { version, updateCheck, update }
///   GET  /api/session, /auth/login, /auth/callback, POST /auth/logout   sign-in (see AuthRoutes)
///   GET  /api/lookup-tenant?domain=               { tenantId } of a domain such as contoso.onmicrosoft.com (anonymous Entra lookup)
///   GET  /api/tenants                             [{ alias, displayName, demo, readOnly, signedIn, setupStep, files }]
///   POST /api/tenants                             { alias, tenantId, clientId, displayName? } → 201 { alias } (400/409 with the reason)
///   DELETE /api/tenants/{alias}                   removes data/{alias}/ and its sign-in → 204 (409 while a job runs)
///   GET  /api/tenants/{alias}/discover            { subscriptions, workspaces[], warnings[] } the user can see (needs the sign-in)
///   PUT  /api/tenants/{alias}/workspaces          { workspaces: [ARM id or portal URL] } → resolved through ARM (needs the sign-in)
///   PUT  /api/tenants/{alias}/settings            { displayName?, defaults?: { incidentDays?, validationDays? } } → tenant.json
///   GET  /api/tenants/{alias}/{doc}               the JSON document, 404 if missing
///   PUT  /api/tenants/{alias}/selections          full selections document → 204
///
/// Demo tenants come from <see cref="SamplesRoot"/> (RULEHEALTH_SAMPLES) and are read-only: every
/// write to them is a 403. A tenant folder in the data folder with the same alias wins.
///   GET  /api/tenants/{alias}/jobs                the tenant's jobs, newest first
///   POST /api/tenants/{alias}/jobs                { kind, days?, saveRaw?, includeBasic? } → 202 job (409 if one runs)
///   GET  /api/jobs/{id}                           job with its log
///   POST /api/jobs/{id}/cancel                    → 202 (409 if not running)
///   GET  /api/tenants/{alias}/validation-plan     dry run: routing, skips and KQL per table (?days=&amp;includeBasic=)
///
/// Safety (<see cref="RequestGuard"/>): only Host localhost:5180 is served (DNS rebinding), every write
/// needs the session's CSRF token and the right Origin (a page on another site has neither), no CORS
/// headers, strict security headers. Outside a container the listener also binds to localhost only.
/// Tokens live in <see cref="Tokens"/> (memory only).
/// </summary>
public sealed class StaticServer : IDisposable
{
    private const long MaxBodyBytes = 5 * 1024 * 1024;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".map"] = "application/json; charset=utf-8",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf", // ui/brand/fonts: Montserrat variable font
    };

    private readonly JsonStore _store;
    private JsonStore? _samples;
    private readonly string _uiRoot;
    private readonly Action<string> _log;
    private readonly HttpListener _listener = new();
    // Serialises selection writes; two browser tabs saving at once must not interleave.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly RequestGuard _guard;
    private readonly AuthRoutes _auth;
    private Task? _loop;

    /// <param name="listenPrefix">HttpListener prefix, e.g. http://localhost:5180/ (or http://+:5180/ in a container).</param>
    /// <param name="publicUrl">RULEHEALTH_PUBLIC_URL, e.g. http://localhost:5180: the only Host and Origin accepted, and the redirect URI's origin.</param>
    /// <param name="entra">For tests: the token endpoint client (default: login.microsoftonline.com).</param>
    public StaticServer(string dataRoot, string uiRoot, string listenPrefix, string publicUrl, Action<string> log, EntraTokenClient? entra = null)
    {
        _store = new JsonStore(dataRoot);
        _uiRoot = Path.GetFullPath(uiRoot);
        _log = log;
        Jobs = new JobManager(log);
        Tokens = new TokenStore(entra ?? new EntraTokenClient());
        SignIn = new PkceAuthorizationService(publicUrl, Tokens);
        _guard = new RequestGuard(publicUrl);
        _auth = new AuthRoutes(_store, Tokens, SignIn, _guard, log);
        BaseUrl = listenPrefix;
        _listener.Prefixes.Add(BaseUrl);
    }

    /// <summary>The listener prefix (for tests and the startup message; with + it is not a browsable URL).</summary>
    public string BaseUrl { get; }

    /// <summary>The signed-in tenants of this process (memory only; exposed for tests).</summary>
    public TokenStore Tokens { get; }

    /// <summary>The PKCE sign-in (exposed for tests).</summary>
    public PkceAuthorizationService SignIn { get; }

    /// <summary>RULEHEALTH_UPDATE_CHECK, reported by /api/app.</summary>
    public bool UpdateCheckEnabled { get; init; } = true;

    /// <summary>The daily GitHub Releases check (null when RULEHEALTH_UPDATE_CHECK=false): /api/app reports its result.</summary>
    public UpdateChecker? Updates { get; init; }

    /// <summary>
    /// RULEHEALTH_SAMPLES: read-only demo tenants offered by the wizard ("Explore with demo data").
    /// Null, a missing folder, or the data folder itself (serve --data samples) = no separate demo source.
    /// </summary>
    public string? SamplesRoot
    {
        get => _samples?.DataRoot;
        init
        {
            var full = value is null ? null : Path.GetFullPath(value);
            _samples = full is not null && Directory.Exists(full) &&
                       !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(_store.DataRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? new JsonStore(full)
                : null;
        }
    }

    /// <summary>Background jobs started through the API (exposed for tests).</summary>
    public JobManager Jobs { get; }

    /// <summary>For tests: replaces the client of the anonymous tenant-exists check (no live calls in tests).</summary>
    public HttpClient? TenantDiscoveryHttp { get; set; }

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // The port is fixed (the sign-in redirect URI contains it), so the fix is freeing it, not changing it.
            throw new RuleHealthException($"Cannot listen on {BaseUrl}: {ex.Message}. Is another instance (or another program) using the port? Stop it and start again.", ex);
        }
        _loop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        if (_listener.IsListening)
            _listener.Stop();
        _listener.Close();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* listener shutdown throws by design */ }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return; // Stop() was called
            }
            catch (HttpListenerException)
            {
                continue;
            }
            // Handle requests concurrently; the browser loads several files at once.
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var watch = Stopwatch.StartNew();
        try
        {
            RequestGuard.AddSecurityHeaders(response);
            await RouteAsync(request, response);
        }
        catch (Exception ex)
        {
            _log($"  error: {ex.Message}");
            TrySendError(response, 500, "Internal error; see the serve console.");
        }
        finally
        {
            _log($"{request.HttpMethod} {request.Url?.AbsolutePath} {response.StatusCode} {watch.ElapsedMilliseconds}ms");
            try { response.Close(); } catch { /* client went away */ }
        }
    }

    private async Task RouteAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var path = request.Url?.AbsolutePath ?? "/";
        // Split on raw "/" before decoding so an encoded "%2F" cannot add path segments.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var isRead = request.HttpMethod is "GET" or "HEAD";

        // The Docker HEALTHCHECK calls 127.0.0.1 from inside the container: before the Host check,
        // and only for loopback callers (others get a plain 404).
        if (segments is ["healthz"])
        {
            response.Headers["Cache-Control"] = "no-store";
            if (isRead && request.RemoteEndPoint is { } remote && IPAddress.IsLoopback(remote.Address))
                await SendJsonAsync(response, new { status = "ok", version = AppInfo.Version });
            else
                SendError(response, 404, "Not found.");
            return;
        }

        switch (_guard.ClassifyHost(request.Headers["Host"]))
        {
            case HostVerdict.RedirectToPublic when isRead:
                response.StatusCode = 302;
                response.RedirectLocation = _guard.PublicOrigin + (request.Url?.PathAndQuery ?? "/");
                return;
            case HostVerdict.RedirectToPublic:
            case HostVerdict.Misdirected:
                SendError(response, 421, $"Open Detection Rule Health at {_guard.PublicOrigin}.");
                return;
        }

        // Sign-in through Microsoft's apps comes back to "/" (they register http://localhost without a path).
        if (segments.Length == 0 && request.HttpMethod == "GET" && AuthRoutes.IsRootCallback(request.QueryString))
        {
            response.Headers["Cache-Control"] = "no-store";
            await _auth.RootCallbackAsync(request, response);
            return;
        }

        if (segments.Length > 0 && segments[0] is "api" or "auth")
        {
            response.Headers["Cache-Control"] = "no-store";
            // Refused before any routing: no side effects for a request from another site.
            if (!isRead && _guard.RefuseWrite(request) is { } refused)
            {
                SendError(response, 403, refused);
                return;
            }
            if (await _auth.TryRouteAsync(request, response, segments))
                return;
            await RouteApiAsync(request, response, segments);
            return;
        }

        if (!isRead)
        {
            SendError(response, 405, "Method not allowed.");
            return;
        }

        if (segments.Length == 0)
            await SendFileAsync(response, Path.Combine(_uiRoot, "index.html"));
        else if (segments[0] == "ui" && segments.Length > 1)
            await SendUiFileAsync(response, segments[1..]);
        else if (segments is ["favicon.ico"])
            response.StatusCode = 204;
        else
            SendError(response, 404, "Not found.");
    }

    private async Task RouteApiAsync(HttpListenerRequest request, HttpListenerResponse response, string[] segments)
    {
        // Demo tenants are read-only; refused first so no route below has to think about it.
        if (request.HttpMethod is not ("GET" or "HEAD") && segments is ["api", "tenants", var writeAlias, ..] && IsDemoSource(writeAlias))
        {
            SendError(response, 403, "Demo data is read-only. Connect your own tenant to collect, decide and validate.");
            return;
        }

        // /api/app — version for the UI footer; `update` is filled by the update check (rulehealth-release)
        if (segments is ["api", "app"])
        {
            if (request.HttpMethod != "GET") { SendError(response, 405, "Use GET."); return; }
            await SendJsonAsync(response, new
            {
                version = AppInfo.Version,
                updateCheck = UpdateCheckEnabled ? "enabled" : "disabled",
                update = Updates?.Update,
                updateCheckedAt = Updates?.CheckedAt,
                // The Microsoft .NET images set this; Settings then shows the docker upgrade commands.
                runningInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
                // Shown on the Settings page: where the tenant folders are (inside the container: /data).
                dataFolder = Path.GetFullPath(_store.DataRoot),
            });
            return;
        }

        // /api/lookup-tenant?domain=contoso.onmicrosoft.com
        if (segments is ["api", "lookup-tenant"])
        {
            if (request.HttpMethod != "GET") { SendError(response, 405, "Use GET."); return; }
            try
            {
                var tenantId = await TenantDiscovery.ResolveDomainAsync(request.QueryString["domain"] ?? "", CancellationToken.None, TenantDiscoveryHttp);
                await SendJsonAsync(response, new { tenantId });
            }
            catch (RuleHealthException ex)
            {
                SendError(response, ex.HttpStatus ?? 400, ex.Message);
            }
            return;
        }

        // DELETE /api/tenants/{alias}
        if (segments is ["api", "tenants", var removeAlias])
        {
            if (request.HttpMethod != "DELETE") { SendError(response, 405, "Use DELETE."); return; }
            RemoveTenant(response, removeAlias);
            return;
        }

        // /api/tenants/{alias}/discover
        if (segments is ["api", "tenants", var discoverAlias, "discover"])
        {
            if (request.HttpMethod != "GET") { SendError(response, 405, "Use GET."); return; }
            await DiscoverAsync(response, discoverAlias);
            return;
        }

        // /api/tenants/{alias}/settings
        if (segments is ["api", "tenants", var settingsAlias, "settings"])
        {
            if (request.HttpMethod != "PUT") { SendError(response, 405, "Use PUT."); return; }
            await PutSettingsAsync(request, response, settingsAlias);
            return;
        }

        // /api/jobs/{id} and /api/jobs/{id}/cancel
        if (segments is ["api", "jobs", var jobId, ..])
        {
            await RouteJobAsync(request, response, jobId, segments[3..]);
            return;
        }

        // /api/tenants/{alias}/jobs
        if (segments is ["api", "tenants", var jobsAlias, "jobs"])
        {
            await RouteTenantJobsAsync(request, response, jobsAlias);
            return;
        }

        // /api/tenants/{alias}/validation-plan
        if (segments is ["api", "tenants", var planAlias, "validation-plan"])
        {
            if (request.HttpMethod != "GET") { SendError(response, 405, "Use GET."); return; }
            await SendValidationPlanAsync(request, response, planAlias);
            return;
        }

        // /api/tenants
        if (segments is ["api", "tenants"])
        {
            if (request.HttpMethod == "GET")
                await SendJsonAsync(response, ListTenants());
            else if (request.HttpMethod == "POST")
                await AddTenantAsync(request, response);
            else
                SendError(response, 405, "Use GET or POST.");
            return;
        }

        // /api/tenants/{alias}/workspaces
        if (segments is ["api", "tenants", var workspacesAlias, "workspaces"])
        {
            if (request.HttpMethod != "PUT") { SendError(response, 405, "Use PUT."); return; }
            await PutWorkspacesAsync(request, response, workspacesAlias);
            return;
        }

        // /api/tenants/{alias}/{doc}
        if (segments is ["api", "tenants", var alias, var document])
        {
            if (!Documents.IsValidAlias(alias) || !Documents.Served.Contains(document))
            {
                SendError(response, 404, "Not found.");
                return;
            }

            if (request.HttpMethod == "GET")
            {
                var file = StoreFor(alias).DocumentPath(alias, document);
                if (File.Exists(file))
                    await SendFileAsync(response, file);
                else
                    SendError(response, 404, $"{document}.json does not exist for tenant '{alias}'.");
                return;
            }

            if (request.HttpMethod == "PUT" && document == Documents.Selections)
            {
                await PutSelectionsAsync(request, response, alias);
                return;
            }

            SendError(response, 405, document == Documents.Selections ? "Use GET or PUT." : "Use GET; only selections can be written.");
            return;
        }

        SendError(response, 404, "Not found.");
    }

    /// <summary>The data folder's tenants, then the demo tenants whose alias the data folder does not use.</summary>
    private object ListTenants()
    {
        var own = _store.ListTenants().Where(alias => _store.Exists(alias, Documents.Tenant)).ToList();
        var demo = _samples?.ListTenants().Where(alias => _samples.Exists(alias, Documents.Tenant) && !own.Contains(alias)).ToList()
                   ?? new List<string>();
        return own.Select(alias => Describe(_store, alias, readOnly: false))
            .Concat(demo.Select(alias => Describe(_samples!, alias, readOnly: true)))
            .ToList();
    }

    private object Describe(JsonStore store, string alias, bool readOnly)
    {
        TenantDocument? tenant = null;
        try
        {
            tenant = store.Read<TenantDocument>(alias, Documents.Tenant);
        }
        catch (RuleHealthException ex)
        {
            _log($"  warning: {ex.Message}");
        }
        var signedIn = Tokens.Get(alias) is not null;
        var files = store.ListDocuments(alias).Where(Documents.Served.Contains).ToList();
        return new
        {
            alias,
            displayName = tenant?.DisplayName ?? alias,
            demo = readOnly || tenant?.Demo == true,
            readOnly,
            offline = tenant?.Auth.Mode == AuthModes.Offline,
            signedIn,
            // Where the setup wizard continues for this tenant (derived from its files, see SetupProgress).
            setupStep = tenant is null ? SetupProgress.App : SetupProgress.StepFor(tenant, signedIn, files.Contains(Documents.Rules)),
            files,
        };
    }

    /// <summary>The store that holds <paramref name="alias"/>: the data folder, else the demo source.</summary>
    private JsonStore StoreFor(string alias) =>
        _samples is not null && Documents.IsValidAlias(alias) && !_store.Exists(alias, Documents.Tenant) && _samples.Exists(alias, Documents.Tenant)
            ? _samples
            : _store;

    private bool IsDemoSource(string alias) => Documents.IsValidAlias(alias) && ReferenceEquals(StoreFor(alias), _samples);

    // ------------------------------------------------------------------ tenants

    /// <summary>
    /// POST /api/tenants: add (or re-add) a tenant with the user's own app registration. Checks the
    /// input (400 / 409 with the reason) and that the tenant exists, then writes tenant.json: 201.
    /// No sign-in here; the UI sends the browser to /auth/login next.
    /// </summary>
    private async Task AddTenantAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var tenantRequest = await ReadJsonAsync<TenantRequest>(request, response, "tenant request");
        if (tenantRequest is null)
            return;
        try
        {
            if (Documents.IsValidAlias(tenantRequest.Alias) && IsDemoSource(tenantRequest.Alias))
                throw new RuleHealthException($"The alias '{tenantRequest.Alias}' is used by the demo data. Pick another alias.") { HttpStatus = 409 };
            var tenant = TenantOperation.Prepare(_store, tenantRequest);
            await TenantOperation.SaveAsync(_store, tenant, CancellationToken.None, TenantDiscoveryHttp);
            // A sign-in for another tenant or app registration under this alias is no longer valid.
            if (Tokens.Get(tenant.Alias) is { } session &&
                (session.TenantId != tenant.TenantId || session.Profile != TenantAuth.Require(tenant).Profile))
                Tokens.Remove(tenant.Alias);
            _log($"  saved tenant '{tenant.Alias}'");
            await SendJsonAsync(response, new { alias = tenant.Alias }, 201);
        }
        catch (RuleHealthException ex)
        {
            SendError(response, ex.HttpStatus ?? 400, ex.Message);
        }
    }

    /// <summary>
    /// PUT /api/tenants/{alias}/workspaces: resolve each workspace through ARM with the tenant's sign-in
    /// (the server never trusts a workspaceId the browser sends) and store them in tenant.json.
    /// </summary>
    private async Task PutWorkspacesAsync(HttpListenerRequest request, HttpListenerResponse response, string alias)
    {
        if (!Documents.IsValidAlias(alias) || _store.TryRead<TenantDocument>(alias, Documents.Tenant) is not { } tenant)
        {
            SendError(response, 404, $"Tenant '{alias}' does not exist.");
            return;
        }
        var body = await ReadJsonAsync<WorkspacesRequest>(request, response, "workspaces request");
        if (body is null)
            return;
        try
        {
            // No per-workspace lines in the console (docker logs): they name the workspaces. The response has them.
            using var api = TenantConnection.Connect(tenant, Tokens, _ => { }, null, null);
            var updated = await TenantOperation.SetWorkspacesAsync(_store, api, tenant, body.Workspaces ?? Array.Empty<string>(),
                _ => { }, CancellationToken.None);
            _log($"  saved {updated.Workspaces.Count} workspaces for '{alias}'");
            await SendJsonAsync(response, new { alias, workspaces = updated.Workspaces });
        }
        catch (RuleHealthException ex)
        {
            // Our own refusals keep their status; a failed ARM call is a bad gateway, not a 403 of ours.
            SendError(response, ex.HttpStatus is 400 or 401 or 409 ? ex.HttpStatus.Value : 502, ex.Message);
        }
    }

    /// <summary>
    /// GET /api/tenants/{alias}/discover: the subscriptions and Log Analytics workspaces the signed-in
    /// user can see, with their Microsoft Sentinel state, for the wizard's workspace step.
    /// </summary>
    private async Task DiscoverAsync(HttpListenerResponse response, string alias)
    {
        if (!Documents.IsValidAlias(alias) || _store.TryRead<TenantDocument>(alias, Documents.Tenant) is not { } tenant)
        {
            SendError(response, 404, $"Tenant '{alias}' does not exist.");
            return;
        }
        try
        {
            using var api = TenantConnection.Connect(tenant, Tokens, _ => { }, null, null);
            var found = await new DiscoveryClient(api).DiscoverAsync(tenant.TenantId!, CancellationToken.None);
            _log($"  discovered {found.Workspaces.Count} workspaces in {found.Subscriptions} subscriptions for '{alias}'");
            await SendJsonAsync(response, new { subscriptions = found.Subscriptions, workspaces = found.Workspaces, warnings = found.Warnings });
        }
        catch (RuleHealthException ex)
        {
            // 401 = sign in (again); 409 = offline / old sign-in mode; anything from ARM is a bad gateway.
            SendError(response, ex.HttpStatus is 401 or 409 ? ex.HttpStatus.Value : 502, ex.Message);
        }
    }

    /// <summary>PUT /api/tenants/{alias}/settings: display name and the incident / validation windows.</summary>
    private async Task PutSettingsAsync(HttpListenerRequest request, HttpListenerResponse response, string alias)
    {
        if (!Documents.IsValidAlias(alias) || _store.TryRead<TenantDocument>(alias, Documents.Tenant) is not { } tenant)
        {
            SendError(response, 404, $"Tenant '{alias}' does not exist.");
            return;
        }
        var body = await ReadJsonAsync<SettingsRequest>(request, response, "settings");
        if (body is null)
            return;
        try
        {
            var updated = TenantOperation.ApplySettings(_store, tenant, body);
            await SendJsonAsync(response, new { alias, displayName = updated.DisplayName, defaults = updated.Defaults });
        }
        catch (RuleHealthException ex)
        {
            SendError(response, ex.HttpStatus ?? 400, ex.Message);
        }
    }

    /// <summary>
    /// DELETE /api/tenants/{alias}: removes the tenant's folder (collected files, decisions, raw
    /// responses) and its sign-in. The UI asks the user to type the alias first.
    /// </summary>
    private void RemoveTenant(HttpListenerResponse response, string alias)
    {
        // The alias pattern (^[a-z0-9-]+$) keeps this inside the data folder: no dots, no separators.
        if (!Documents.IsValidAlias(alias) || !Directory.Exists(_store.TenantDirectory(alias)))
        {
            SendError(response, 404, $"Tenant '{alias}' does not exist.");
            return;
        }
        if (Jobs.IsRunning(alias))
        {
            SendError(response, 409, "A job is running for this tenant. Cancel it or wait for it to finish, then remove the tenant.");
            return;
        }
        Tokens.Remove(alias);
        try
        {
            Directory.Delete(_store.TenantDirectory(alias), recursive: true);
        }
        catch (IOException ex)
        {
            SendError(response, 409, $"Could not remove the folder of '{alias}': {ex.Message}");
            return;
        }
        _log($"  removed tenant '{alias}'");
        response.StatusCode = 204;
    }

    // ------------------------------------------------------------------ jobs

    private async Task RouteTenantJobsAsync(HttpListenerRequest request, HttpListenerResponse response, string alias)
    {
        if (!Documents.IsValidAlias(alias) || !StoreFor(alias).Exists(alias, Documents.Tenant))
        {
            SendError(response, 404, $"Tenant '{alias}' does not exist.");
            return;
        }

        if (request.HttpMethod == "GET")
        {
            await SendJsonAsync(response, Jobs.ForTenant(alias));
            return;
        }
        if (request.HttpMethod != "POST")
        {
            SendError(response, 405, "Use GET or POST.");
            return;
        }

        var body = await ReadBodyAsync(request, response);
        if (body is null)
            return;
        JobRequest? job;
        try
        {
            job = JsonSerializer.Deserialize<JobRequest>(body, Json.StrictOptions);
        }
        catch (JsonException ex)
        {
            SendError(response, 400, $"Invalid job request: {ex.Message}");
            return;
        }
        if (job is null || string.IsNullOrEmpty(job.Kind))
        {
            SendError(response, 400, $"Give a job kind: {string.Join(", ", JobRequests.Kinds)}.");
            return;
        }

        try
        {
            var started = Jobs.Start(alias, job.Kind, JobRequests.Work(_store, Tokens, alias, job));
            await SendJsonAsync(response, started, 202);
        }
        catch (RuleHealthException ex)
        {
            SendError(response, ex.HttpStatus ?? 400, ex.Message);
        }
    }

    private async Task RouteJobAsync(HttpListenerRequest request, HttpListenerResponse response, string id, string[] rest)
    {
        if (rest.Length == 0 && request.HttpMethod == "GET")
        {
            if (Jobs.Get(id) is { } job)
                await SendJsonAsync(response, job);
            else
                SendError(response, 404, "No such job (jobs are forgotten when serve restarts).");
            return;
        }
        if (rest is ["cancel"] && request.HttpMethod == "POST")
        {
            if (Jobs.Cancel(id))
                await SendJsonAsync(response, Jobs.Get(id)!, 202);
            else
                SendError(response, Jobs.Get(id) is null ? 404 : 409, "The job is not running.");
            return;
        }
        SendError(response, 405, "Use GET /api/jobs/{id} or POST /api/jobs/{id}/cancel.");
    }

    /// <summary>The dry run as JSON: what validate would query where, and what it skips and why.</summary>
    private async Task SendValidationPlanAsync(HttpListenerRequest request, HttpListenerResponse response, string alias)
    {
        if (!Documents.IsValidAlias(alias) || !StoreFor(alias).Exists(alias, Documents.Tenant))
        {
            SendError(response, 404, $"Tenant '{alias}' does not exist.");
            return;
        }
        int? days = int.TryParse(request.QueryString["days"], out var d) ? d : null;
        var includeBasic = request.QueryString["includeBasic"] is "true" or "1";

        PreparedValidation prepared;
        try
        {
            prepared = ValidateOperation.Prepare(StoreFor(alias), alias, new ValidateOptions(days, includeBasic));
        }
        catch (RuleHealthException ex)
        {
            // Missing selections/candidates etc.: a normal state the UI explains, not a server error.
            SendError(response, 409, ex.Message);
            return;
        }

        var plan = prepared.Plan;
        await SendJsonAsync(response, new
        {
            days = plan.Days,
            selected = prepared.Selected,
            summary = ValidateOperation.Describe(prepared),
            notes = prepared.Notes,
            offline = prepared.Tenant.Auth.Mode == AuthModes.Offline,
            batches = plan.Batches.Select(b => new
            {
                table = b.Table,
                engine = b.Target.Engine,
                workspace = b.Target.WorkspaceName,
                search = b.Target.Search,
                target = b.Target.Describe(),
                checks = b.Checks.Count,
                kql = b.Kql(plan.Days),
            }),
            decided = plan.Decided.Select(r => new
            {
                r.CheckId, r.Table, r.Column, r.Path, r.OperatorClass, r.Value, r.Verdict, reason = r.Error,
            }),
        });
    }

    // ------------------------------------------------------------------ selections

    private async Task PutSelectionsAsync(HttpListenerRequest request, HttpListenerResponse response, string alias)
    {
        if (!Directory.Exists(_store.TenantDirectory(alias)))
        {
            SendError(response, 404, $"Tenant '{alias}' does not exist.");
            return;
        }

        var body = await ReadBodyAsync(request, response);
        if (body is null)
            return;

        SelectionsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SelectionsDocument>(body, Json.StrictOptions);
        }
        catch (JsonException ex)
        {
            SendError(response, 400, $"Invalid selections document: {ex.Message}");
            return;
        }
        if (document is null)
        {
            SendError(response, 400, "Empty body.");
            return;
        }

        var errors = SelectionsValidator.Validate(document, alias);
        if (errors.Count > 0)
        {
            SendError(response, 400, string.Join(" ", errors.Take(20)));
            return;
        }

        await _writeLock.WaitAsync();
        try
        {
            _store.Write(alias, Documents.Selections, document);
        }
        finally
        {
            _writeLock.Release();
        }
        response.StatusCode = 204;
    }

    /// <summary>The body as <typeparamref name="T"/> (strict: unknown properties are refused); null after sending a 400/413.</summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request, HttpListenerResponse response, string what) where T : class
    {
        var body = await ReadBodyAsync(request, response);
        if (body is null)
            return null;
        try
        {
            if (JsonSerializer.Deserialize<T>(body, Json.StrictOptions) is { } value)
                return value;
            SendError(response, 400, "Empty body.");
        }
        catch (JsonException ex)
        {
            SendError(response, 400, $"Invalid {what}: {ex.Message}");
        }
        return null;
    }

    /// <summary>The request body, capped at 5 MB; null (and a 413 sent) when it is larger.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.ContentLength64 > MaxBodyBytes)
        {
            SendError(response, 413, "Body larger than 5 MB.");
            return null;
        }

        // ContentLength64 can be -1 (chunked), so also cap while reading.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
            {
                SendError(response, 413, "Body larger than 5 MB.");
                return null;
            }
        }
        return buffer.ToArray();
    }

    private async Task SendUiFileAsync(HttpListenerResponse response, string[] relativeSegments)
    {
        // Resolve and confirm the result is still inside ui/ (blocks ../ traversal and absolute paths).
        var full = Path.GetFullPath(Path.Combine(_uiRoot, Path.Combine(relativeSegments)));
        var rootWithSeparator = _uiRoot.EndsWith(Path.DirectorySeparatorChar) ? _uiRoot : _uiRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            SendError(response, 404, "Not found.");
            return;
        }
        await SendFileAsync(response, full);
    }

    private static async Task SendFileAsync(HttpListenerResponse response, string file)
    {
        response.StatusCode = 200;
        response.ContentType = ContentTypes.TryGetValue(Path.GetExtension(file), out var type) ? type : "application/octet-stream";
        // FileShare.ReadWrite | Delete: a concurrent atomic write (File.Replace) must not fail because we are reading.
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(response.OutputStream);
    }

    internal static async Task SendJsonAsync(HttpListenerResponse response, object value, int status = 200)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        response.StatusCode = status;
        response.ContentType = ContentTypes[".json"];
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    internal static void SendError(HttpListenerResponse response, int status, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message }, Json.Options));
        response.StatusCode = status;
        response.ContentType = ContentTypes[".json"];
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
    }

    private static void TrySendError(HttpListenerResponse response, int status, string message)
    {
        try { SendError(response, status, message); } catch { /* headers already sent */ }
    }
}
