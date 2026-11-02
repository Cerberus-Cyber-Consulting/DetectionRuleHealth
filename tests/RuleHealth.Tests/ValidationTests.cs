using System.Net;
using System.Text.Json;
using RuleHealth.Core.Clients;
using RuleHealth.Core.Model;
using RuleHealth.Core.Validation;

namespace RuleHealth.Tests;

public sealed class ValidationTests
{
    // ------------------------------------------------------------------ fixtures

    private static ValueCheck Check(string table, string column, string value, string operatorClass = "eq",
        string? path = null, bool validatable = true, string ruleId = "s1", string expression = "")
    {
        var id = CheckIdOf(table, column, value);
        return new ValueCheck
        {
            CheckId = id, Table = table, Column = column, Path = path, OperatorClass = operatorClass, Value = value,
            Lineage = validatable ? Lineages.Direct : Lineages.Expression, Validatable = validatable,
            Occurrences = { new CheckOccurrence { RuleId = ruleId, Operator = "==", Negated = false, Expression = expression } },
        };
    }

    // Stable fake 16-hex ids; the planner does not recompute them.
    private static string CheckIdOf(params string[] parts) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts))))[..16].ToLowerInvariant();

    private static Rule Rule(string id, string source, string? workspace = null) => new()
    {
        RuleId = id, Source = source, Kind = source == RuleSources.Sentinel ? "Scheduled" : "customDetection", DisplayName = id,
        Enabled = true, QueryText = "x", SchemaContext = SchemaContexts.Unified,
        Origin = workspace is null ? null
            : $"/subscriptions/s/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/{workspace}/providers/Microsoft.SecurityInsights/alertRules/{id}",
    };

    private static readonly Rule[] Rules =
    {
        Rule("s1", RuleSources.Sentinel, "law-B"),
        Rule("d1", RuleSources.Defender),
    };

    private static SchemaDocument Schema() => new()
    {
        Tenant = "t", GeneratedAt = DateTime.UtcNow, Source = "test",
        Tables =
        {
            ["SigninLogs"] = new(StringComparer.Ordinal) { ["TimeGenerated"] = "datetime", ["ResultType"] = "string", ["UserPrincipalName"] = "string" },
            ["DeviceProcessEvents"] = new(StringComparer.Ordinal) { ["Timestamp"] = "datetime", ["TimeGenerated"] = "datetime", ["ActionType"] = "string", ["AdditionalFields"] = "dynamic" },
            ["SecurityEvent"] = new(StringComparer.Ordinal) { ["TimeGenerated"] = "datetime", ["EventID"] = "int", ["EventData"] = "string" },
            ["AppLogs_CL"] = new(StringComparer.Ordinal) { ["TimeGenerated"] = "datetime", ["Action"] = "string" },
        },
        TableInfo = new Dictionary<string, TableInfo>(StringComparer.Ordinal)
        {
            ["SigninLogs"] = new() { Workspaces = { "law-A", "law-B" }, Plan = TablePlans.Analytics, Hunting = false },
            ["DeviceProcessEvents"] = new() { Workspaces = { "law-A" }, Plan = TablePlans.Analytics, Hunting = true },
            ["SecurityEvent"] = new() { Workspaces = { "law-A" }, Plan = TablePlans.Analytics, Hunting = false },
            ["AppLogs_CL"] = new() { Workspaces = { "law-A" }, Plan = TablePlans.Basic, Hunting = false },
        },
    };

    private static SelectionsDocument Select(params ValueCheck[] checks)
    {
        var selections = new SelectionsDocument { Tenant = "t", UpdatedAt = DateTime.UtcNow };
        foreach (var check in checks)
        {
            selections.Decisions[check.CheckId] = new Decision
            {
                State = DecisionStates.Validate, DecidedAt = DateTime.UtcNow,
                Check = new CheckKey { Table = check.Table, Column = check.Column, Path = check.Path, OperatorClass = check.OperatorClass, Value = check.Value },
            };
        }
        return selections;
    }

    private static CandidatesDocument Candidates(params ValueCheck[] checks) => new()
    {
        Tenant = "t", GeneratedAt = DateTime.UtcNow, SchemaSource = "test", Checks = checks.ToList(),
    };

    // ------------------------------------------------------------------ predicates

    [Theory]
    [InlineData("eq", null, "string", "ResultType == @\"0\"")]
    [InlineData("eq_ci", null, "string", "ResultType =~ @\"0\"")]
    [InlineData("has", null, "string", "ResultType has @\"0\"")]
    [InlineData("regex", null, "string", "ResultType matches regex @\"0\"")]
    [InlineData("eq", null, "int", "ResultType == 0")]
    [InlineData("has", null, "int", "tostring(ResultType) has @\"0\"")]
    [InlineData("eq", null, "dynamic", "tostring(ResultType) == @\"0\"")]
    [InlineData("eq", null, null, "ResultType == @\"0\"")]
    public void Predicate_follows_column_type_and_operator(string operatorClass, string? path, string? type, string expected)
    {
        var (predicate, problem) = ValidationQueryBuilder.Predicate(Check("T", "ResultType", "0", operatorClass, path), type);

        Assert.Null(problem);
        Assert.Equal(expected, predicate);
    }

    [Theory]
    [InlineData("AdditionalFields.SignatureName", "dynamic", "tostring(AdditionalFields[\"SignatureName\"]) == @\"v\"")]
    [InlineData("AdditionalFields.SignatureName", "string", "tostring(parse_json(AdditionalFields)[\"SignatureName\"]) == @\"v\"")]
    [InlineData("AdditionalFields[\"raw Data\"][\"x-y\"]", "dynamic", "tostring(AdditionalFields[\"raw Data\"][\"x-y\"]) == @\"v\"")]
    [InlineData("AdditionalFields['a'].b[0]", "dynamic", "tostring(AdditionalFields[\"a\"][\"b\"][0]) == @\"v\"")]
    public void Paths_are_always_written_with_brackets(string path, string type, string expected)
    {
        var (predicate, _) = ValidationQueryBuilder.Predicate(Check("T", "AdditionalFields", "v", path: path), type);

        Assert.Equal(expected, predicate);
    }

    [Fact]
    public void Literals_double_quotes_and_switch_form_for_line_breaks()
    {
        Assert.Equal("@\"say \"\"hi\"\"\"", ValidationQueryBuilder.StringLiteral("say \"hi\""));
        Assert.Equal("@\"C:\\Windows\"", ValidationQueryBuilder.StringLiteral("C:\\Windows"));
        Assert.Equal("\"a\\nb \\\"c\\\" \\\\\"", ValidationQueryBuilder.StringLiteral("a\nb \"c\" \\"));
    }

    [Fact]
    public void Lowercased_column_in_the_rule_makes_eq_case_insensitive()
    {
        var check = Check("T", "FileName", "powershell.exe", expression: "tolower(FileName) == \"powershell.exe\"");

        Assert.Equal("FileName =~ @\"powershell.exe\"", ValidationQueryBuilder.Predicate(check, "string").Predicate);
    }

    [Fact]
    public void Like_and_odd_names_are_handled()
    {
        Assert.NotNull(ValidationQueryBuilder.Predicate(Check("T", "C", "x%", "like"), "string").Problem);
        Assert.Equal("['Odd Column']", ValidationQueryBuilder.Identifier("Odd Column"));
        Assert.Null(ValidationQueryBuilder.PathAccessors("Col", "Other.a"));
    }

    // ------------------------------------------------------------------ batch KQL (snapshots)

    [Fact]
    public void Log_analytics_batch_filters_time_in_the_query()
    {
        var kql = ValidationQueryBuilder.BatchQuery("SigninLogs", "TimeGenerated", timeFilterInQuery: true, 30,
            new[] { ("aaaaaaaaaaaaaaaa", "ResultType == @\"0\""), ("bbbbbbbbbbbbbbbb", "UserPrincipalName has @\"admin\"") });

        Assert.Equal("""
            SigninLogs
            | where TimeGenerated > ago(30d)
            | summarize TableRows = count(),
                c_aaaaaaaaaaaaaaaa = countif(ResultType == @"0"), l_aaaaaaaaaaaaaaaa = maxif(TimeGenerated, ResultType == @"0"),
                c_bbbbbbbbbbbbbbbb = countif(UserPrincipalName has @"admin"), l_bbbbbbbbbbbbbbbb = maxif(TimeGenerated, UserPrincipalName has @"admin")
            """.ReplaceLineEndings("\n"), kql);
    }

    [Fact]
    public void Hunting_batch_leaves_the_time_to_the_request_timespan()
    {
        var kql = ValidationQueryBuilder.BatchQuery("DeviceProcessEvents", "Timestamp", timeFilterInQuery: false, 30,
            new[] { ("aaaaaaaaaaaaaaaa", "ActionType == @\"ProcessCreated\"") });

        Assert.Equal("""
            DeviceProcessEvents
            | summarize TableRows = count(),
                c_aaaaaaaaaaaaaaaa = countif(ActionType == @"ProcessCreated"), l_aaaaaaaaaaaaaaaa = maxif(Timestamp, ActionType == @"ProcessCreated")
            """.ReplaceLineEndings("\n"), kql);
    }

    // ------------------------------------------------------------------ routing

    [Fact]
    public void Routing_follows_what_the_rules_read()
    {
        var sentinelSignin = Check("SigninLogs", "ResultType", "0", ruleId: "s1");
        var defenderXdr = Check("DeviceProcessEvents", "ActionType", "ProcessCreated", ruleId: "d1");
        var sentinelOnXdr = Check("DeviceProcessEvents", "ActionType", "FileCreated", ruleId: "s1");
        var defenderOnSentinelTable = Check("SecurityEvent", "EventID", "4624", ruleId: "d1");

        var plan = ValidationPlanner.Plan(Select(sentinelSignin, defenderXdr, sentinelOnXdr, defenderOnSentinelTable),
            Candidates(sentinelSignin, defenderXdr, sentinelOnXdr, defenderOnSentinelTable), Rules, Schema(), 30, includeBasic: false);

        Assert.Empty(plan.Decided);
        var targets = plan.Batches.SelectMany(b => b.Checks.Select(c => (c.Check.CheckId, b.Target))).ToDictionary(x => x.CheckId, x => x.Target);
        // Sentinel rule → the rule's own workspace (law-B), not the first workspace with the table.
        Assert.Equal(new QueryTarget(QueryEngines.LogAnalytics, "law-B", false), targets[sentinelSignin.CheckId]);
        Assert.Equal(new QueryTarget(QueryEngines.Hunting, null, false), targets[defenderXdr.CheckId]);
        Assert.Equal(new QueryTarget(QueryEngines.LogAnalytics, "law-A", false), targets[sentinelOnXdr.CheckId]);
        Assert.Equal(new QueryTarget(QueryEngines.LogAnalytics, "law-A", false), targets[defenderOnSentinelTable.CheckId]);

        var hunting = plan.Batches.Single(b => b.Target.Engine == QueryEngines.Hunting);
        Assert.Equal("Timestamp", hunting.TimeColumn);
        Assert.Contains("SecurityEvent\n| where TimeGenerated > ago(30d)", plan.Batches.Single(b => b.Table == "SecurityEvent").Kql(30));
        Assert.Contains("EventID == 4624", plan.Batches.Single(b => b.Table == "SecurityEvent").Kql(30));
    }

    [Fact]
    public void Basic_tables_are_opt_in_and_use_search()
    {
        var check = Check("AppLogs_CL", "Action", "login");

        var without = ValidationPlanner.Plan(Select(check), Candidates(check), Rules, Schema(), 30, includeBasic: false);
        var with = ValidationPlanner.Plan(Select(check), Candidates(check), Rules, Schema(), 30, includeBasic: true);

        var skipped = Assert.Single(without.Decided);
        Assert.Equal(Verdicts.Skipped, skipped.Verdict);
        Assert.Contains("--include-basic", skipped.Error);
        Assert.True(Assert.Single(with.Batches).Target.Search);
    }

    [Fact]
    public void Checks_that_cannot_run_are_skipped_or_errors_with_a_reason()
    {
        var derived = Check("SigninLogs", "ResultType", "1", validatable: false);
        var unknownTable = Check("NoSuchTable", "X", "y");
        var gone = Check("SigninLogs", "ResultType", "2");
        var unbound = Check("SigninLogs", "NoSuchColumn", "z");
        var like = Check("SigninLogs", "ResultType", "5%", "like");

        var plan = ValidationPlanner.Plan(Select(derived, unknownTable, gone, unbound, like),
            Candidates(derived, unknownTable, unbound, like), Rules, Schema(), 30, includeBasic: false);

        var byId = plan.Decided.ToDictionary(r => r.CheckId);
        Assert.Contains("expression lineage", byId[derived.CheckId].Error);
        Assert.Contains("NoSuchTable", byId[unknownTable.CheckId].Error);
        Assert.Contains("not in candidates.json", byId[gone.CheckId].Error);
        Assert.Equal(Verdicts.Error, byId[unbound.CheckId].Verdict); // pre-flight bind
        Assert.Contains("query does not bind", byId[unbound.CheckId].Error);
        Assert.Contains("like", byId[like.CheckId].Error);
        Assert.Empty(plan.Batches);
    }

    [Fact]
    public void Values_only_used_by_disabled_rules_are_skipped()
    {
        // Live 2026-10-19: a disabled Sentinel rule (superseded by a custom detection with the same name)
        // produced absent/table-empty results for a rule that does not run.
        var rules = new[] { Rule("s1", RuleSources.Sentinel, "law-B") with { Enabled = false }, Rule("s2", RuleSources.Sentinel, "law-B") };
        var onlyDisabled = Check("SigninLogs", "ResultType", "0", ruleId: "s1");
        var mixed = Check("SigninLogs", "ResultType", "1", ruleId: "s1");
        mixed.Occurrences.Add(new CheckOccurrence { RuleId = "s2", Operator = "==", Negated = false, Expression = "" });
        var unknownRule = Check("SigninLogs", "ResultType", "2", ruleId: "gone");

        var plan = ValidationPlanner.Plan(Select(onlyDisabled, mixed, unknownRule), Candidates(onlyDisabled, mixed, unknownRule),
            rules, Schema(), 30, includeBasic: false);

        var skipped = Assert.Single(plan.Decided);
        Assert.Equal((onlyDisabled.CheckId, Verdicts.Skipped), (skipped.CheckId, skipped.Verdict));
        Assert.Contains("disabled rules", skipped.Error);
        // Used by one enabled rule, or by a rule no longer in rules.json: still validated.
        Assert.Equal(new[] { mixed.CheckId, unknownRule.CheckId }.Order(), plan.Batches.SelectMany(b => b.Checks).Select(c => c.Check.CheckId).Order());
    }

    [Fact]
    public void Batches_hold_at_most_fifty_checks()
    {
        var checks = Enumerable.Range(0, 120).Select(i => Check("SigninLogs", "ResultType", i.ToString())).ToArray();

        var plan = ValidationPlanner.Plan(Select(checks), Candidates(checks), Rules, Schema(), 30, includeBasic: false);

        Assert.Equal(new[] { 50, 50, 20 }, plan.Batches.Select(b => b.Checks.Count));
    }

    [Fact]
    public void Schema_without_table_info_asks_for_a_collect()
    {
        var check = Check("SigninLogs", "ResultType", "0");

        var ex = Assert.Throws<RuleHealth.Core.RuleHealthException>(() =>
            ValidationPlanner.Plan(Select(check), Candidates(check), Rules, Schema() with { TableInfo = null }, 30, false));

        Assert.Contains("collect", ex.Message);
    }

    // ------------------------------------------------------------------ running

    /// <summary>Plays the service: answers each batch from a function of its checks.</summary>
    private sealed class FakeExecutor : IValidationExecutor
    {
        public Func<ValidationBatch, BatchOutcome> Answer { get; init; } = _ => new BatchOutcome(null, "no answer", null, null);
        public List<int> BatchSizes { get; } = new();

        public Task<BatchOutcome> RunAsync(ValidationBatch batch, string kql, int days, CancellationToken cancellationToken)
        {
            BatchSizes.Add(batch.Checks.Count);
            return Task.FromResult(Answer(batch));
        }
    }

    private static QueryRow Row(long tableRows, params (string CheckId, long Count, string? Last)[] values)
    {
        var json = new Dictionary<string, object?> { ["TableRows"] = tableRows };
        foreach (var (id, count, last) in values)
        {
            json[$"c_{id}"] = count;
            json[$"l_{id}"] = last;
        }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(json));
        var row = new QueryRow();
        foreach (var property in document.RootElement.EnumerateObject())
            row[property.Name] = property.Value.Clone();
        return row;
    }

    [Fact]
    public async Task Counts_become_present_absent_and_table_empty()
    {
        var seen = Check("SigninLogs", "ResultType", "0");
        var missing = Check("SigninLogs", "ResultType", "50126");
        var empty = Check("SecurityEvent", "EventID", "4624", ruleId: "s1");
        var plan = ValidationPlanner.Plan(Select(seen, missing, empty), Candidates(seen, missing, empty), Rules, Schema(), 30, false);
        var executor = new FakeExecutor
        {
            Answer = batch => batch.Table == "SecurityEvent"
                ? new BatchOutcome(Row(0, (empty.CheckId, 0, null)), null, null, null)
                : new BatchOutcome(Row(1000, (seen.CheckId, 12, "2026-10-18T09:00:00Z"), (missing.CheckId, 0, null)), null, null, null),
        };

        var document = await new ValidationRunner(executor, _ => { }).RunAsync("t", plan, DateTime.UtcNow, CancellationToken.None);

        var byId = document.Results.ToDictionary(r => r.CheckId);
        Assert.Equal((Verdicts.Present, 12L, 1000L), (byId[seen.CheckId].Verdict, byId[seen.CheckId].MatchCount!.Value, byId[seen.CheckId].TableRowCount!.Value));
        Assert.Equal(new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc), byId[seen.CheckId].LastSeenAt);
        Assert.Equal(Verdicts.Absent, byId[missing.CheckId].Verdict);
        Assert.Null(byId[missing.CheckId].LastSeenAt);
        Assert.Equal(Verdicts.TableEmpty, byId[empty.CheckId].Verdict);
        Assert.Equal(2, document.Queries.Count);
        Assert.Equal(30, document.WindowDays);
    }

    [Fact]
    public async Task A_rejected_batch_is_split_until_the_bad_check_is_found()
    {
        var checks = Enumerable.Range(0, 8).Select(i => Check("SigninLogs", "ResultType", "v" + i)).ToArray();
        var bad = checks[5].CheckId;
        var plan = ValidationPlanner.Plan(Select(checks), Candidates(checks), Rules, Schema(), 30, false);
        var executor = new FakeExecutor
        {
            Answer = batch => batch.Checks.Any(c => c.Check.CheckId == bad)
                ? new BatchOutcome(null, "HTTP 400 BadArgumentError: Query could not be parsed", 400, null)
                : new BatchOutcome(Row(10, batch.Checks.Select(c => (c.Check.CheckId, 1L, (string?)"2026-10-18T00:00:00Z")).ToArray()), null, null, null),
        };

        var document = await new ValidationRunner(executor, _ => { }).RunAsync("t", plan, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(Verdicts.Error, document.Results.Single(r => r.CheckId == bad).Verdict);
        Assert.Equal(7, document.Results.Count(r => r.Verdict == Verdicts.Present));
        // 8 → 4+4, the bad half → 2+2, the bad quarter → 1+1: 7 queries (order depends on checkId sorting).
        Assert.Equal(8, executor.BatchSizes[0]);
        Assert.Equal(new[] { 1, 1, 2, 2, 4, 4, 8 }, executor.BatchSizes.Order());
    }

    [Fact]
    public async Task Other_failures_mark_the_whole_batch_without_splitting()
    {
        var checks = Enumerable.Range(0, 4).Select(i => Check("SigninLogs", "ResultType", "v" + i)).ToArray();
        var plan = ValidationPlanner.Plan(Select(checks), Candidates(checks), Rules, Schema(), 30, false);
        var executor = new FakeExecutor { Answer = _ => new BatchOutcome(null, "HTTP 403 Forbidden", 403, null) };

        var document = await new ValidationRunner(executor, _ => { }).RunAsync("t", plan, DateTime.UtcNow, CancellationToken.None);

        Assert.All(document.Results, r => Assert.Equal((Verdicts.Error, "HTTP 403 Forbidden"), (r.Verdict, r.Error)));
        Assert.Equal(new[] { 4 }, executor.BatchSizes);
        Assert.Equal("HTTP 403 Forbidden", Assert.Single(document.Queries).Error);
    }

    // ------------------------------------------------------------------ API clients

    [Fact]
    public async Task Hunting_results_match_columns_case_insensitively()
    {
        using var api = new FakeApi().RouteBody("/v1.0/security/runHuntingQuery", HttpStatusCode.OK, """
            { "schema": [ { "name": "TableRows", "type": "Int64" }, { "name": "c_aaaaaaaaaaaaaaaa", "type": "Int64" } ],
              "results": [ { "tableRows": 42, "c_aaaaaaaaaaaaaaaa": 3, "l_aaaaaaaaaaaaaaaa": "2024-03-26T09:39:50.7688641Z" } ] }
            """);

        var result = await new HuntingClient(api.Connection).RunAsync("T | take 1", "P30D", "test", CancellationToken.None);

        var row = Assert.Single(result.Rows);
        Assert.Equal(42, row.Long("TableRows"));
        Assert.Equal(3, row.Long("c_aaaaaaaaaaaaaaaa"));
        Assert.Equal(new DateTime(2024, 3, 26, 9, 39, 50, DateTimeKind.Utc), row.Timestamp("l_aaaaaaaaaaaaaaaa")!.Value.AddTicks(-7688641));
    }

    [Fact]
    public async Task Log_analytics_errors_include_the_inner_error_chain()
    {
        using var api = new FakeApi().RouteBody("/query", HttpStatusCode.BadRequest, """
            { "error": { "code": "BadArgumentError", "message": "The request had some invalid properties",
              "innererror": { "code": "SemanticError", "message": "A semantic error occurred.",
                "innererror": { "code": "SEM0100", "message": "'where' operator: Failed to resolve column named 'NoSuchColumn'" } } } }
            """);

        var ex = await Assert.ThrowsAsync<RuleHealth.Core.RuleHealthException>(() =>
            new LogAnalyticsClient(api.Connection).QueryAsync("ws", "T", "P30D", "Validate T", CancellationToken.None));

        Assert.Equal(400, ex.HttpStatus);
        Assert.Contains("invalid properties → A semantic error occurred. → 'where' operator: Failed to resolve column named 'NoSuchColumn'", ex.Message);
    }

    [Fact]
    public async Task Search_puts_the_timespan_in_the_query_string()
    {
        using var api = new FakeApi().RouteBody("/search?timespan=P30D", HttpStatusCode.OK, """{ "tables": [] }""");

        await new LogAnalyticsClient(api.Connection).SearchAsync("ws", "T", "P30D", "Validate T", CancellationToken.None);

        Assert.EndsWith("/v1/workspaces/ws/search?timespan=P30D", Assert.Single(api.Requests));
    }
}
