using RuleHealth.Core.Extraction;
using RuleHealth.Core.Markdown;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Core.Operations;

/// <summary>
/// extract: parse every rule with a query against the tenant's schema → candidates.json.
/// Offline mode (markdown repo + schema file) also writes tenant.json and rules.json.
/// </summary>
public static class ExtractOperation
{
    /// <returns>A one-line summary.</returns>
    public static string Run(JsonStore store, string alias, string? markdownRepo, string? schemaFile, Action<string> log) =>
        Execute(store, alias, markdownRepo, schemaFile, log).Summary;

    /// <returns>The one-line summary and the number of value checks written (for the job's stage).</returns>
    public static (string Summary, int Checks) Execute(JsonStore store, string alias, string? markdownRepo, string? schemaFile, Action<string> log)
    {
        store.TenantDirectory(alias); // validates the alias
        if (markdownRepo is not null && schemaFile is null)
            throw new RuleHealthException("Extracting from markdown needs a schema file (an offline run has no tenant schema).");

        List<Rule> rules;
        if (markdownRepo is not null)
        {
            rules = MarkdownRuleReader.Read(markdownRepo);
            if (!store.Exists(alias, Documents.Tenant))
            {
                store.Write(alias, Documents.Tenant, new TenantDocument
                {
                    Alias = alias,
                    DisplayName = $"{Path.GetFileName(Path.GetFullPath(markdownRepo).TrimEnd(Path.DirectorySeparatorChar))} (offline markdown)",
                    Auth = new AuthSettings { Mode = AuthModes.Offline },
                });
                log($"wrote {store.DocumentPath(alias, Documents.Tenant)}");
            }
            store.Write(alias, Documents.Rules, new RulesDocument { Tenant = alias, GeneratedAt = DateTime.UtcNow, Rules = rules });
            log($"wrote {store.DocumentPath(alias, Documents.Rules)} ({rules.Count} queries from {markdownRepo})");
        }
        else
        {
            rules = (store.TryRead<RulesDocument>(alias, Documents.Rules)
                     ?? throw new RuleHealthException($"No rules.json for '{alias}'. Collect from the tenant first.")).Rules;
        }

        var schema = schemaFile is not null
            ? SchemaFileReader.Read(schemaFile, alias)
            : store.TryRead<SchemaDocument>(alias, Documents.Schema)
              ?? throw new RuleHealthException($"No schema.json for '{alias}'. Collect from the tenant first (or pass a schema file).");

        var result = new RuleExtractor(schema).Extract(alias, rules);
        store.Write(alias, Documents.Candidates, result.Candidates);
        log($"wrote {store.DocumentPath(alias, Documents.Candidates)}");

        var s = result.Stats;
        log($"{s.QueriesAnalysed} queries ({s.QueriesWithBindErrors} with bind errors), {s.Comparisons} comparisons: " +
            $"{s.DirectOrRenamed} direct/renamed, {s.ExpressionLineage} expression, {s.NullChecks} null checks, " +
            $"{s.RuntimeValue} runtime values, {s.UnboundExpression} unbound, {s.ComputedColumn} computed → " +
            $"{result.Candidates.Checks.Count} checks ({result.Candidates.Checks.Count(c => c.Validatable)} validatable), " +
            $"{result.Candidates.Unresolved.Count} unresolved");
        if (s.UnsupportedOperator > 0)
            log($"warning: {s.UnsupportedOperator} comparisons used an operator without an operatorClass and produced no checks.");

        return ($"Extracted {result.Candidates.Checks.Count} value checks from {s.QueriesAnalysed} queries " +
                $"({result.Candidates.Checks.Count(c => c.Validatable)} validatable, {s.QueriesWithBindErrors} queries with bind errors)",
            result.Candidates.Checks.Count);
    }
}
