using RuleHealth.Core.Extraction;
using RuleHealth.Core.Markdown;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Tests;

/// <summary>
/// Runs when RULEHEALTH_HB_REPO points to a clone of HybridBrothers/Hunting-Queries-Detection-Rules.
/// The schema is the committed fixture tests/Fixtures/schema/unified-schema.json, exported from the
/// owner's own lab tenant with `rulehealth schema export` (see the README there).
/// </summary>
public sealed class GoldenFactAttribute : FactAttribute
{
    public GoldenFactAttribute()
    {
        if (GoldenExtractionTests.RepoPath is null)
            Skip = "Set RULEHEALTH_HB_REPO to a clone of HybridBrothers/Hunting-Queries-Detection-Rules to run the extraction golden test.";
        else if (GoldenExtractionTests.SchemaPath is null)
            Skip = "tests/Fixtures/schema/unified-schema.json is missing: export it with `rulehealth schema export` (M8 step 5).";
    }
}

/// <summary>
/// The regression baseline from the kql-literal-extraction skill, plus a field-by-field comparison
/// with samples/hybridbrothers (generated from the same rules and schema). If a deliberate
/// improvement changes a number, update the skill's table and the samples in the same commit.
/// </summary>
public class GoldenExtractionTests
{
    public static readonly string? RepoPath = Existing(Environment.GetEnvironmentVariable("RULEHEALTH_HB_REPO"), Directory.Exists);
    public static readonly string? SchemaPath = Existing(Path.Combine(FakeApi.Fixtures, "schema", "unified-schema.json"), File.Exists);

    private static readonly Lazy<(List<Rule> Rules, ExtractionResult Result)> Run = new(() =>
    {
        var rules = MarkdownRuleReader.Read(RepoPath!);
        var schema = SchemaFileReader.Read(SchemaPath!, "hybridbrothers");
        return (rules, new RuleExtractor(schema).Extract("hybridbrothers", rules));
    });

    [GoldenFact]
    public void Baseline_numbers_match()
    {
        var stats = Run.Value.Result.Stats;

        Assert.Equal(73, stats.QueriesAnalysed);
        // M8 step 5 re-baseline against the lab tenant export (was 7 / 268 / 12 against the retired
        // third-party schema): MessageEvents.ThreadType and DeviceTvmSoftwareVulnerabilitiesKB.CvssVector /
        // EpssScore exist in the tenant, so 3 rules bind and 4 comparisons move from unbound to direct.
        // Then two engine fixes (2026-10-30): the Log Analytics standard columns (Type, ...) on every
        // workspace table make HuntMdeWithGsaEvents #1 bind again (5 → 4); graph-match predicates
        // (SourceNode.NodeLabel == "device") resolve to ExposureGraphNodes: 8 unbound → 6 direct + 2 runtime.
        // See the kql-literal-extraction skill.
        Assert.Equal(4, stats.QueriesWithBindErrors);
        Assert.Equal(426, stats.Comparisons);
        // Spike: 232 / 120 / 2 computed. M1 moved 11 comparisons to expression (mv-expand in place,
        // array_length wrapper) and 2 back to renamed (`project X = Col`); M2 value flow moved 5 from
        // expression to computed (iff conditions, ipv4_is_in_range, array_length). See the skill.
        // M2 path-renames moved 45 more from expression to renamed (`X = parse_json(Col)`,
        // `X = tostring(Col.a)`, `distinct tostring(X)`).
        Assert.Equal(278, stats.DirectOrRenamed);
        Assert.Equal(79, stats.ExpressionLineage);
        Assert.Equal(38, stats.NullChecks);
        Assert.Equal(24, stats.RuntimeValue);
        Assert.Equal(0, stats.UnboundExpression);
        Assert.Equal(7, stats.ComputedColumn);
        Assert.Equal(0, stats.UnsupportedOperator);
        Assert.Equal(248, Run.Value.Result.Candidates.Checks.Count);
        Assert.Equal(195, Run.Value.Result.Candidates.Checks.Count(c => c.Validatable));
    }

    [GoldenFact]
    public void Rules_match_the_sample()
    {
        var expected = SampleRules().Rules;
        var actual = Run.Value.Rules;

        Assert.Equal(expected.Select(r => r.RuleId), actual.Select(r => r.RuleId));
        foreach (var (e, a) in expected.Zip(actual))
            Assert.Equal(e, a); // Rule has only scalar members, so record equality compares every field
    }

    [GoldenFact]
    public void Candidates_match_the_sample()
    {
        var expected = SampleCandidates();
        var actual = Run.Value.Result.Candidates;
        var differences = new List<string>();

        Compare("check", expected.Checks.Select(CheckKey), actual.Checks.Select(CheckKey), differences);
        Compare("occurrence",
            expected.Checks.SelectMany(c => c.Occurrences.Select(o => OccurrenceKey(c, o))),
            actual.Checks.SelectMany(c => c.Occurrences.Select(o => OccurrenceKey(c, o))),
            differences);
        Compare("unresolved", expected.Unresolved.Select(UnresolvedKey), actual.Unresolved.Select(UnresolvedKey), differences);
        Compare("summary", expected.RuleSummaries.Select(SummaryKey), actual.RuleSummaries.Select(SummaryKey), differences);

        Assert.True(differences.Count == 0,
            $"{differences.Count} differences (- sample, + engine):{Environment.NewLine}{string.Join(Environment.NewLine, differences.Take(40))}");
    }

    [GoldenFact]
    public void Expression_occurrences_carry_their_column_definition()
    {
        var occurrences = Run.Value.Result.Candidates.Checks
            .Where(c => c.Lineage == Lineages.Expression)
            .SelectMany(c => c.Occurrences)
            .Where(o => o.DerivedColumn is not null)
            .ToList();

        Assert.NotEmpty(occurrences);
        var missing = occurrences.Where(o => string.IsNullOrWhiteSpace(o.ColumnDefinition)).Select(o => $"{o.RuleId} :: {o.DerivedColumn} :: {o.Expression}").Distinct().ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} without definition:{Environment.NewLine}{string.Join(Environment.NewLine, missing.Take(30))}");
    }

    private static string CheckKey(ValueCheck c) =>
        $"{c.CheckId} {c.Table}.{c.Column} path={c.Path} {c.OperatorClass} '{c.Value}' {c.Lineage} validatable={c.Validatable}";

    private static string OccurrenceKey(ValueCheck c, CheckOccurrence o) =>
        $"{c.CheckId} {o.RuleId} {o.Operator} negated={o.Negated} derived={o.DerivedColumn} derivedPath={o.DerivedPath} definition={o.ColumnDefinition} :: {o.Expression}";

    private static string UnresolvedKey(UnresolvedComparison u) =>
        $"{u.RuleId} {u.Status} {u.Operator} [{u.Detail}] :: {u.Expression}";

    private static string SummaryKey(RuleExtractionSummary s) =>
        $"{s.RuleId} comparisons={s.Comparisons} checks={s.Checks} null={s.NullChecks} unresolved={s.Unresolved} errors=[{string.Join(" | ", s.BindErrors)}]";

    /// <summary>Multiset difference, so order does not matter but duplicates do.</summary>
    private static void Compare(string what, IEnumerable<string> expected, IEnumerable<string> actual, List<string> differences)
    {
        var remaining = expected.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        foreach (var item in actual)
        {
            if (remaining.TryGetValue(item, out var count) && count > 0)
                remaining[item] = count - 1;
            else
                differences.Add($"+ {what}: {item}");
        }
        differences.AddRange(remaining.Where(kv => kv.Value > 0).SelectMany(kv => Enumerable.Repeat($"- {what}: {kv.Key}", kv.Value)));
    }

    private static RulesDocument SampleRules() =>
        JsonStore.ReadFile<RulesDocument>(Path.Combine(TestPaths.Samples, "hybridbrothers", "rules.json"));

    private static CandidatesDocument SampleCandidates() =>
        JsonStore.ReadFile<CandidatesDocument>(Path.Combine(TestPaths.Samples, "hybridbrothers", "candidates.json"));

    private static string? Existing(string? path, Func<string, bool> exists) =>
        !string.IsNullOrWhiteSpace(path) && exists(path) ? path : null;
}
