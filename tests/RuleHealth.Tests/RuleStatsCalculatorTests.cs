using RuleHealth.Core.Model;
using RuleHealth.Core.Stats;
using RuleHealth.Core.Storage;

namespace RuleHealth.Tests;

public sealed class RuleStatsCalculatorTests
{
    private static readonly CollectionWindow Window = new()
    {
        From = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
        To = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc),
        Days = 90,
    };

    private static Incident Incident(string id, string? classification, params string[] ruleIds) => new()
    {
        IncidentId = id,
        Source = RuleSources.Sentinel,
        Title = id,
        CreatedAt = new DateTime(2026, 8, int.Parse(id), 0, 0, 0, DateTimeKind.Utc),
        Status = classification is null ? IncidentStatuses.Open : IncidentStatuses.Closed,
        Classification = classification,
        RuleIds = ruleIds.ToList(),
    };

    private static Rule Rule(string id) => new()
    {
        RuleId = id, Source = RuleSources.Sentinel, Kind = "Scheduled", DisplayName = id, Enabled = true,
        QueryText = null, SchemaContext = SchemaContexts.Sentinel,
    };

    [Fact]
    public void Rates_use_verdicted_incidents_and_undetermined_uses_closed()
    {
        var incidents = new IncidentsDocument
        {
            Tenant = "t", GeneratedAt = Window.To, Window = Window,
            Incidents =
            {
                Incident("1", Classifications.TruePositive, "a"),
                Incident("2", Classifications.FalsePositive, "a"),
                Incident("3", Classifications.FalsePositive, "a"),
                Incident("4", Classifications.Undetermined, "a"),
                Incident("5", null, "a"),
            },
        };

        var stats = Assert.Single(RuleStatsCalculator.Calculate("t", incidents, new[] { Rule("a") }, Window.To).Rules);

        Assert.Equal((5, 1, 4), (stats.Incidents, stats.Open, stats.Closed));
        Assert.Equal((1, 0, 2, 1), (stats.Tp, stats.Bp, stats.Fp, stats.Undetermined));
        Assert.Equal(0.3333, stats.TpRate);
        Assert.Equal(0.0, stats.BpRate);
        Assert.Equal(0.6667, stats.FpRate);
        Assert.Equal(0.25, stats.UndeterminedRate);
        Assert.Equal(new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc), stats.LastIncidentAt);
    }

    [Fact]
    public void Rates_are_null_without_a_denominator_and_silent_rules_get_a_row()
    {
        var incidents = new IncidentsDocument
        {
            Tenant = "t", GeneratedAt = Window.To, Window = Window,
            Incidents = { Incident("1", null, "open-only"), Incident("2", Classifications.Undetermined, "open-only") },
        };

        var stats = RuleStatsCalculator.Calculate("t", incidents, new[] { Rule("open-only"), Rule("silent") }, Window.To)
            .Rules.ToDictionary(r => r.RuleId);

        Assert.Null(stats["open-only"].TpRate);
        Assert.Null(stats["open-only"].FpRate);
        Assert.Equal(1.0, stats["open-only"].UndeterminedRate);
        Assert.Equal(0, stats["silent"].Incidents);
        Assert.Null(stats["silent"].UndeterminedRate);
        Assert.Null(stats["silent"].LastIncidentAt);
    }

    [Fact]
    public void Shared_incidents_count_for_each_rule_and_unknown_rules_are_kept()
    {
        var incidents = new IncidentsDocument
        {
            Tenant = "t", GeneratedAt = Window.To, Window = Window,
            Incidents =
            {
                Incident("1", Classifications.TruePositive, "a", "b"),
                Incident("2", Classifications.TruePositive, "a", "a"), // same rule twice = one rule
                Incident("3", Classifications.FalsePositive, "deleted-rule"),
            },
        };

        var stats = RuleStatsCalculator.Calculate("t", incidents, new[] { Rule("a"), Rule("b") }, Window.To)
            .Rules.ToDictionary(r => r.RuleId);

        Assert.Equal((2, 1), (stats["a"].Incidents, stats["a"].SharedIncidents));
        Assert.Equal((1, 1), (stats["b"].Incidents, stats["b"].SharedIncidents));
        Assert.Equal(1, stats["deleted-rule"].Fp);
    }

    [Fact]
    public void Recomputing_the_sample_gives_the_sample_rule_stats()
    {
        var folder = Path.Combine(TestPaths.Samples, "hybridbrothers");
        var incidents = JsonStore.ReadFile<IncidentsDocument>(Path.Combine(folder, "incidents.json"));
        var rules = JsonStore.ReadFile<RulesDocument>(Path.Combine(folder, "rules.json")).Rules;
        var expected = JsonStore.ReadFile<RuleStatsDocument>(Path.Combine(folder, "rule-stats.json"));

        var actual = RuleStatsCalculator.Calculate(expected.Tenant, incidents, rules, expected.GeneratedAt);

        Assert.Equal(expected.Window, actual.Window);
        Assert.Equal(expected.Rules.Select(r => r.RuleId).Order(StringComparer.Ordinal), actual.Rules.Select(r => r.RuleId).Order(StringComparer.Ordinal));
        var byId = actual.Rules.ToDictionary(r => r.RuleId);
        foreach (var rule in expected.Rules)
            Assert.Equal(rule, byId[rule.RuleId]);
    }
}
