using RuleHealth.Core.Model;

namespace RuleHealth.Core.Extraction;

/// <summary>
/// Collects extracted values into checks, merging identical (table, column, path, operatorClass,
/// value) combinations across rules, and produces candidates.json.
/// </summary>
public sealed class CheckBuilder
{
    private readonly Dictionary<string, PendingCheck> _checks = new(StringComparer.Ordinal);
    private readonly List<UnresolvedComparison> _unresolved = new();
    private readonly List<RuleExtractionSummary> _summaries = new();

    public void AddOccurrence(SourceColumn source, string? path, string operatorClass, string value, string lineage, CheckOccurrence occurrence)
    {
        var checkId = CheckIdentity.Compute(source.Table, source.Column, path, operatorClass, value);
        if (!_checks.TryGetValue(checkId, out var check))
        {
            check = new PendingCheck(checkId, source.Table, source.Column, path, operatorClass, value);
            _checks[checkId] = check;
        }
        check.Add(lineage, occurrence);
    }

    public void AddUnresolved(UnresolvedComparison unresolved) => _unresolved.Add(unresolved);

    public void AddSummary(RuleExtractionSummary summary) => _summaries.Add(summary);

    /// <summary>Distinct checkIds that have an occurrence for this rule.</summary>
    public int CountChecksForRule(string ruleId) =>
        _checks.Values.Count(c => c.Occurrences.Any(o => o.RuleId == ruleId));

    public CandidatesDocument Build(string tenant, string schemaSource, DateTime generatedAt) => new()
    {
        Tenant = tenant,
        GeneratedAt = generatedAt,
        SchemaSource = schemaSource,
        Checks = _checks.Values
            .OrderBy(c => c.Table, StringComparer.Ordinal)
            .ThenBy(c => c.Column, StringComparer.Ordinal)
            .ThenBy(c => c.Path ?? "", StringComparer.Ordinal)
            .ThenBy(c => c.OperatorClass, StringComparer.Ordinal)
            .ThenBy(c => c.Value, StringComparer.Ordinal)
            .Select(c => c.ToCheck())
            .ToList(),
        Unresolved = _unresolved.ToList(),
        RuleSummaries = _summaries.ToList(),
    };

    private sealed class PendingCheck(string checkId, string table, string column, string? path, string operatorClass, string value)
    {
        // Lower rank wins when occurrences disagree: one rule comparing the table column directly is
        // enough to validate the value, even if another rule reaches it through an expression.
        private static readonly string[] LineageRank = { Lineages.Direct, Lineages.Renamed, Lineages.Expression };

        private readonly HashSet<(string, string, bool, string, string?, string?)> _occurrenceKeys = new();
        private string _lineage = Lineages.Expression;

        public string Table => table;
        public string Column => column;
        public string? Path => path;
        public string OperatorClass => operatorClass;
        public string Value => value;
        public List<CheckOccurrence> Occurrences { get; } = new();

        public void Add(string lineage, CheckOccurrence occurrence)
        {
            if (Array.IndexOf(LineageRank, lineage) < Array.IndexOf(LineageRank, _lineage))
                _lineage = lineage;
            // The same comparison can list a value twice, or appear twice in a rule: keep one occurrence.
            var key = (occurrence.RuleId, occurrence.Operator, occurrence.Negated, occurrence.Expression, occurrence.DerivedColumn, occurrence.DerivedPath);
            if (_occurrenceKeys.Add(key))
                Occurrences.Add(occurrence);
        }

        public ValueCheck ToCheck() => new()
        {
            CheckId = checkId,
            Table = table,
            Column = column,
            Path = path,
            OperatorClass = operatorClass,
            Value = value,
            Lineage = _lineage,
            Validatable = _lineage != Lineages.Expression,
            Occurrences = Occurrences,
        };
    }
}
