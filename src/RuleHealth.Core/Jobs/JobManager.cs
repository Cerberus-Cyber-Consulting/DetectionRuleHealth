namespace RuleHealth.Core.Jobs;

public static class JobStates
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>States of one stage of a job (the wizard's progress list).</summary>
public static class StageStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    /// <summary>Not applicable (no workspace) or not possible (an earlier stage failed): Detail says why.</summary>
    public const string Skipped = "skipped";
    /// <summary>This stage failed, the job went on with the others (e.g. no Sentinel role, Defender works).</summary>
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One stage of a job ("Sentinel analytics rules", "Incidents", ...), reported through
/// <see cref="IProgress{T}"/>. A report replaces the stage with the same name; stages keep the order
/// of their first report, so an operation can announce every stage as pending up front.
/// </summary>
/// <param name="Count">What the stage found (rules, incidents, tables, checks); null while running.</param>
/// <param name="Hint">For a failed stage: what to do, e.g. the missing permission or role.</param>
public sealed record JobStage(string Name, string State, string? Detail = null, int? Count = null, string? Hint = null);

/// <summary>What the API returns for a job: a snapshot, safe to serialize while the job runs.</summary>
/// <param name="Hint">What to do about the failure (the missing permission or role), next to Error;
/// for a job that succeeded with a failed stage, that stage's hint.</param>
public sealed record JobView(
    string Id, string Tenant, string Kind, string State, DateTime StartedAt, DateTime? FinishedAt,
    string? Summary, string? Error, IReadOnlyList<string> Log, IReadOnlyList<JobStage> Stages, string? Hint);

/// <summary>
/// Runs long engine operations (collect, extract, validate) in the background for the serve API, so
/// the UI can start them and poll their log and stages. In memory only: jobs are gone when serve
/// stops, the files they wrote are not. At most one job per tenant at a time (two collects writing
/// the same files would interleave), any number across tenants.
/// </summary>
public sealed class JobManager
{
    private const int KeepFinished = 20;
    private const int MaxLogLines = 2000;

    private readonly object _lock = new();
    private readonly List<Job> _jobs = new();
    private readonly Action<string> _serverLog;

    /// <param name="serverLog">
    /// The serve console (= docker logs, kept on the host): only a start and an end line per job. The job's
    /// own lines name workspaces, rules and alert titles, so they stay in the job (memory, shown in the UI).
    /// </param>
    public JobManager(Action<string> serverLog) => _serverLog = serverLog;

    /// <summary>A job without stages: <paramref name="work"/> gets a log callback and a cancellation token.</summary>
    public JobView Start(string tenant, string kind, Func<Action<string>, CancellationToken, Task<string>> work) =>
        Start(tenant, kind, (log, _, token) => work(log, token));

    /// <summary>
    /// Starts <paramref name="work"/> in the background. It gets a log callback, a stage reporter and a
    /// cancellation token, and returns a one-line summary. Throws <see cref="RuleHealthException"/>
    /// (HttpStatus 409) when a job already runs for the tenant.
    /// </summary>
    public JobView Start(string tenant, string kind, Func<Action<string>, IProgress<JobStage>, CancellationToken, Task<string>> work)
    {
        Job job;
        lock (_lock)
        {
            var running = _jobs.FirstOrDefault(j => j.Tenant == tenant && j.State == JobStates.Running);
            if (running is not null)
                throw new RuleHealthException($"A {running.Kind} job is already running for '{tenant}'. Wait for it or cancel it.") { HttpStatus = 409 };

            job = new Job(Guid.NewGuid().ToString("N")[..12], tenant, kind);
            _jobs.Add(job);
            // Forget the oldest finished jobs; running ones always stay.
            foreach (var old in _jobs.Where(j => j.State != JobStates.Running).OrderBy(j => j.StartedAt).SkipLast(KeepFinished).ToList())
                _jobs.Remove(old);
        }

        _serverLog($"  [{job.Kind} {job.Tenant}] started");
        _ = Task.Run(async () =>
        {
            void Log(string line) => job.Append(line);

            try
            {
                var summary = await work(Log, new StageReporter(job), job.Cancellation.Token);
                job.Finish(JobStates.Succeeded, summary, null, null);
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                job.Finish(JobStates.Cancelled, null, "Cancelled. Files are only written at the end of a step, so nothing half-done was saved.", null);
            }
            catch (RuleHealthException ex)
            {
                job.Finish(JobStates.Failed, null, ex.Message, ex.Hint);
            }
            catch (Exception ex)
            {
                // A bug, not an expected failure: keep the stack in the serve console, a short message in the UI.
                _serverLog($"  [{job.Kind} {job.Tenant}] unexpected error: {ex}");
                job.Finish(JobStates.Failed, null, $"Unexpected error: {ex.Message} (details in the serve console)", null);
            }
            _serverLog($"  [{job.Kind} {job.Tenant}] {job.State}");
        });

        return job.View();
    }

    public JobView? Get(string id)
    {
        lock (_lock)
            return _jobs.FirstOrDefault(j => j.Id == id)?.View();
    }

    /// <summary>The tenant's jobs, newest first.</summary>
    public IReadOnlyList<JobView> ForTenant(string tenant)
    {
        lock (_lock)
            return _jobs.Where(j => j.Tenant == tenant).OrderByDescending(j => j.StartedAt).Select(j => j.View()).ToList();
    }

    /// <summary>True while a job runs for the tenant (removing a tenant must wait for it).</summary>
    public bool IsRunning(string tenant)
    {
        lock (_lock)
            return _jobs.Any(j => j.Tenant == tenant && j.State == JobStates.Running);
    }

    /// <summary>Requests cancellation; false when the job does not exist or already finished.</summary>
    public bool Cancel(string id)
    {
        Job? job;
        lock (_lock)
            job = _jobs.FirstOrDefault(j => j.Id == id);
        if (job is null || job.State != JobStates.Running)
            return false;
        job.Cancellation.Cancel();
        return true;
    }

    /// <summary>Waits for a job to finish (tests and shutdown).</summary>
    public async Task<JobView> WaitAsync(string id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var view = Get(id) ?? throw new InvalidOperationException($"No job {id}.");
            if (view.State != JobStates.Running || DateTime.UtcNow > deadline)
                return view;
            await Task.Delay(20);
        }
    }

    // IProgress<T> without Progress<T>: Progress<T> posts to a SynchronizationContext or the thread
    // pool, so a poll right after a report could miss it. This applies the report at once.
    private sealed class StageReporter(Job job) : IProgress<JobStage>
    {
        public void Report(JobStage value) => job.SetStage(value);
    }

    private sealed class Job
    {
        private readonly object _lock = new();
        private readonly List<string> _log = new();
        private readonly List<JobStage> _stages = new();

        public Job(string id, string tenant, string kind)
        {
            Id = id;
            Tenant = tenant;
            Kind = kind;
        }

        public string Id { get; }
        public string Tenant { get; }
        public string Kind { get; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public CancellationTokenSource Cancellation { get; } = new();
        public string State { get; private set; } = JobStates.Running;
        private DateTime? _finishedAt;
        private string? _summary;
        private string? _error;
        private string? _hint;

        public void Append(string line)
        {
            lock (_lock)
            {
                // Multi-line messages (errors with request ids) become separate lines.
                foreach (var part in line.Split('\n'))
                    _log.Add(part.TrimEnd('\r'));
                if (_log.Count > MaxLogLines)
                    _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
        }

        public void SetStage(JobStage stage)
        {
            lock (_lock)
            {
                var index = _stages.FindIndex(s => s.Name == stage.Name);
                if (index >= 0)
                    _stages[index] = stage;
                else
                    _stages.Add(stage);
            }
        }

        public void Finish(string state, string? summary, string? error, string? hint)
        {
            lock (_lock)
            {
                State = state;
                _summary = summary;
                _error = error;
                _finishedAt = DateTime.UtcNow;
                // Stages still open when the job ends did not finish: cancelled with the job, or cut
                // short by the failure that ended it (the error says which).
                for (var i = 0; i < _stages.Count; i++)
                {
                    if (_stages[i].State == StageStates.Running)
                        _stages[i] = _stages[i] with { State = state == JobStates.Cancelled ? StageStates.Cancelled : StageStates.Failed, Hint = _stages[i].Hint ?? hint };
                    else if (_stages[i].State == StageStates.Pending && state != JobStates.Succeeded)
                        _stages[i] = _stages[i] with { State = StageStates.Skipped, Detail = state == JobStates.Cancelled ? "Cancelled" : "Not run: an earlier stage failed" };
                }
                _hint = hint ?? _stages.FirstOrDefault(s => s.State == StageStates.Failed && s.Hint is not null)?.Hint;
            }
        }

        public JobView View()
        {
            lock (_lock)
                return new JobView(Id, Tenant, Kind, State, StartedAt, _finishedAt, _summary, _error, _log.ToList(), _stages.ToList(), _hint);
        }
    }
}
