using RuleHealth.Core;
using RuleHealth.Core.Jobs;

namespace RuleHealth.Tests;

public sealed class JobManagerTests
{
    private readonly JobManager _jobs = new(_ => { });

    [Fact]
    public async Task A_job_logs_and_finishes_with_its_summary()
    {
        var job = _jobs.Start("t", "collect", (log, _) =>
        {
            log("first");
            log("two\nlines");
            return Task.FromResult("done");
        });

        var finished = await _jobs.WaitAsync(job.Id, TimeSpan.FromSeconds(5));

        Assert.Equal((JobStates.Succeeded, "done"), (finished.State, finished.Summary));
        Assert.Equal(new[] { "first", "two", "lines" }, finished.Log);
        Assert.NotNull(finished.FinishedAt);
    }

    [Fact]
    public async Task One_job_per_tenant_but_tenants_run_side_by_side()
    {
        var gate = new TaskCompletionSource<string>();
        var first = _jobs.Start("t", "collect", (_, _) => gate.Task);

        var ex = Assert.Throws<RuleHealthException>(() => _jobs.Start("t", "extract", (_, _) => Task.FromResult("x")));
        var other = _jobs.Start("other", "extract", (_, _) => Task.FromResult("x"));

        Assert.Equal(409, ex.HttpStatus);
        Assert.Equal(JobStates.Succeeded, (await _jobs.WaitAsync(other.Id, TimeSpan.FromSeconds(5))).State);
        gate.SetResult("ok");
        Assert.Equal(JobStates.Succeeded, (await _jobs.WaitAsync(first.Id, TimeSpan.FromSeconds(5))).State);
        _jobs.Start("t", "extract", (_, _) => Task.FromResult("again")); // allowed once the first finished
    }

    [Fact]
    public async Task Cancel_stops_a_running_job()
    {
        var job = _jobs.Start("t", "validate", async (log, token) =>
        {
            log("waiting");
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        });

        Assert.True(_jobs.Cancel(job.Id));
        var finished = await _jobs.WaitAsync(job.Id, TimeSpan.FromSeconds(5));

        Assert.Equal(JobStates.Cancelled, finished.State);
        Assert.False(_jobs.Cancel(job.Id)); // already finished
    }

    [Fact]
    public async Task Expected_failures_keep_their_message_and_bugs_are_reported_briefly()
    {
        var expected = _jobs.Start("a", "collect", (_, _) => throw new RuleHealthException("Needs CustomDetection.Read.All."));
        var bug = _jobs.Start("b", "collect", (_, _) => throw new InvalidOperationException("boom"));

        var a = await _jobs.WaitAsync(expected.Id, TimeSpan.FromSeconds(5));
        var b = await _jobs.WaitAsync(bug.Id, TimeSpan.FromSeconds(5));

        Assert.Equal((JobStates.Failed, "Needs CustomDetection.Read.All."), (a.State, a.Error));
        Assert.Equal(JobStates.Failed, b.State);
        Assert.Contains("boom", b.Error);
    }
}
