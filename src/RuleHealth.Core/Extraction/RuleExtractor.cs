using Kusto.Language;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Extraction;

/// <summary>Comparison-level counts; the golden test pins these (see the kql-literal-extraction skill).</summary>
public sealed record ExtractionStats
{
    public int QueriesAnalysed { get; set; }
    public int QueriesWithBindErrors { get; set; }
    public int Comparisons { get; set; }
    public int DirectOrRenamed { get; set; }
    public int ExpressionLineage { get; set; }
    public int NullChecks { get; set; }
    public int RuntimeValue { get; set; }
    public int UnboundExpression { get; set; }
    public int ComputedColumn { get; set; }

    /// <summary>Checkable comparisons whose operator has no operatorClass; should stay 0.</summary>
    public int UnsupportedOperator { get; set; }
}

public sealed record ExtractionResult(CandidatesDocument Candidates, ExtractionStats Stats);

/// <summary>
/// Runs the extraction pipeline for every rule with a query: preprocess → parse and bind → find
/// comparisons → resolve values and lineage → classify → emit checks. Port of
/// reference/spike/Invoke-KqlLiteralSpike.ps1; keep the two in step.
/// </summary>
public sealed class RuleExtractor
{
    private const int MaxColumnDefinitionLength = 500;

    private readonly GlobalState _globals;
    private readonly string _schemaSource;

    public RuleExtractor(SchemaDocument schema)
    {
        _globals = SchemaBuilder.Build(schema);
        _schemaSource = schema.Source;
    }

    public ExtractionResult Extract(string tenant, IEnumerable<Rule> rules)
    {
        var builder = new CheckBuilder();
        var stats = new ExtractionStats();

        foreach (var rule in rules)
        {
            if (KqlPreprocessor.IsPlaceholder(rule.QueryText))
                continue;
            ExtractRule(rule, builder, stats);
        }

        return new ExtractionResult(builder.Build(tenant, _schemaSource, DateTime.UtcNow), stats);
    }

    private void ExtractRule(Rule rule, CheckBuilder builder, ExtractionStats stats)
    {
        var code = KustoCode.ParseAndAnalyze(KqlPreprocessor.Prepare(rule.QueryText!), _globals);
        var bindErrors = code.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Message)
            .ToList();

        stats.QueriesAnalysed++;
        if (bindErrors.Count > 0)
            stats.QueriesWithBindErrors++;

        var lets = ValueResolver.CollectLets(code.Syntax);
        var expanded = CollectExpandedColumns(code.Syntax);
        var expandedNames = expanded.Keys.ToHashSet(StringComparer.Ordinal);
        var comparisons = ComparisonFinder.Find(code.Syntax);
        int nullChecks = 0, unresolved = 0;

        foreach (var comparison in comparisons)
        {
            stats.Comparisons++;
            var values = ValueResolver.Resolve(comparison.ValueExpression, lets);
            var lineage = comparison.Column is null
                ? new ColumnLineage(null, Array.Empty<SourceColumn>())
                : LineageResolver.Resolve(code.Globals, comparison.Column.Column, PathSuffix(comparison.Column) ?? "", expandedNames);

            // Order matters and matches the spike: a null check is a null check even when the column is unknown.
            if (values.IsLiteral && values.Values.Count > 0 && values.Values.All(v => v == ""))
            {
                stats.NullChecks++;
                nullChecks++;
            }
            else if (comparison.Column is null)
            {
                stats.UnboundExpression++;
                unresolved++;
                builder.AddUnresolved(Unresolved(rule, comparison, UnresolvedStatuses.UnboundExpression, "column not bound to a table"));
            }
            else if (comparison.Column.Computed is { } computed)
            {
                stats.ComputedColumn++;
                unresolved++;
                builder.AddUnresolved(Unresolved(rule, comparison, UnresolvedStatuses.ComputedColumn,
                    $"compares {computed}, a value computed from the column, not a value of it"));
            }
            else if (lineage.Kind is null)
            {
                stats.ComputedColumn++;
                unresolved++;
                builder.AddUnresolved(Unresolved(rule, comparison, UnresolvedStatuses.ComputedColumn,
                    "column computed by a function or data source without table lineage"));
            }
            else if (!values.IsLiteral)
            {
                stats.RuntimeValue++;
                unresolved++;
                builder.AddUnresolved(Unresolved(rule, comparison, UnresolvedStatuses.RuntimeValue, values.Kind));
            }
            else
            {
                var derivation = Derivation(comparison.Column, lineage, expanded);
                if (derivation is not null)
                    stats.ExpressionLineage++;
                else
                    stats.DirectOrRenamed++;
                if (!EmitChecks(rule, comparison, values, lineage, derivation, builder))
                    stats.UnsupportedOperator++;
            }
        }

        builder.AddSummary(new RuleExtractionSummary
        {
            RuleId = rule.RuleId,
            Comparisons = comparisons.Count,
            Checks = builder.CountChecksForRule(rule.RuleId),
            NullChecks = nullChecks,
            Unresolved = unresolved,
            BindErrors = bindErrors,
            Tables = ReferencedTables(code),
        });
    }

    /// <summary>
    /// The schema tables the query reads, from the binder: plain names, union/join legs, lets and
    /// materialize (the table name is referenced where the let is defined), wildcards
    /// (`union Device*` binds to a group of tables) and `table("X")`. Tables the binder does not
    /// know are left out; they already show up as bind errors.
    /// </summary>
    private static List<string> ReferencedTables(KustoCode code)
    {
        var tables = new SortedSet<string>(StringComparer.Ordinal);
        void Add(Kusto.Language.Symbols.Symbol? symbol)
        {
            switch (symbol)
            {
                case Kusto.Language.Symbols.TableSymbol table when code.Globals.IsDatabaseTable(table):
                    tables.Add(table.Name);
                    break;
                case Kusto.Language.Symbols.GroupSymbol group:
                    foreach (var member in group.Members)
                        Add(member);
                    break;
            }
        }

        foreach (var expression in code.Syntax.GetDescendantsOrSelf<Kusto.Language.Syntax.Expression>())
        {
            Add(expression.ReferencedSymbol);
            if (expression is Kusto.Language.Syntax.FunctionCallExpression { Name.SimpleName: "table" })
                Add(expression.ResultType);
            // _GetWatchlist reads the Watchlist table at runtime; our stub (SchemaBuilder) only prints
            // its columns, so the binder cannot see that.
            if (expression is Kusto.Language.Syntax.FunctionCallExpression { Name.SimpleName: "_GetWatchlist" })
                Add(code.Globals.Database.GetTable("Watchlist"));
        }
        return tables.ToList();
    }

    /// <summary>How the compared value differs from the table column's own values; null when it does not.</summary>
    private sealed record DerivationInfo(string DerivedColumn, string? Definition);

    /// <summary>
    /// A comparison is not on the table column's own values when the column was computed (expression
    /// lineage), wrapped in a value-changing function (array_length(X) == 0), or expanded in place
    /// by mv-expand/mv-apply (an array column becomes one row per element).
    /// </summary>
    private static DerivationInfo? Derivation(ColumnRef column, ColumnLineage lineage, IReadOnlyDictionary<string, string> expanded)
    {
        if (column.Transform is { } transform)
            return new DerivationInfo(transform, transform);
        if (PathSuffix(column) is null)
            return new DerivationInfo(column.Column.Name, column.Path);
        if (expanded.TryGetValue(column.Column.Name, out var expansion))
            return new DerivationInfo(column.Column.Name, expansion);
        if (lineage.Kind == Lineages.Expression)
            return new DerivationInfo(column.Column.Name, ColumnDefinition(column));
        return null;
    }

    /// <summary>One check per (source column × distinct non-empty value). Returns false for an unsupported operator.</summary>
    private static bool EmitChecks(Rule rule, Comparison comparison, ResolvedValues values, ColumnLineage lineage,
        DerivationInfo? derivation, CheckBuilder builder)
    {
        if (OperatorClassifier.Classify(comparison.Operator) is not var (operatorClass, negated))
            return false;

        var column = comparison.Column!;
        var occurrence = new CheckOccurrence
        {
            RuleId = rule.RuleId,
            Operator = comparison.Operator,
            Negated = negated ^ comparison.InsideNot,
            // For derived values the check targets the source table column; the rule's own column
            // name, path and definition are kept so the UI can show what was actually compared.
            DerivedColumn = derivation?.DerivedColumn,
            DerivedPath = derivation is null ? null : column.Path,
            ColumnDefinition = derivation?.Definition,
            Expression = comparison.Text,
        };
        var checkLineage = derivation is null ? lineage.Kind! : Lineages.Expression;

        foreach (var source in lineage.Sources)
        {
            // The lineage resolver puts the full path on the table column (through renames and
            // path-only definitions); a derived value has no meaningful path on the table column.
            var path = derivation is null ? source.Path : null;
            foreach (var value in values.Values.Where(v => v != "").Distinct(StringComparer.Ordinal))
                builder.AddOccurrence(source, path, operatorClass, value, checkLineage, occurrence);
        }
        return true;
    }

    /// <summary>
    /// What the comparison selects inside its column, as written after the column name
    /// (`AuthenticationDetails.SPF` → `.SPF`); "" for the column itself; null when the path is not
    /// written against the column (then the check cannot carry a path and is treated as derived).
    /// </summary>
    private static string? PathSuffix(ColumnRef column)
    {
        if (column.Path is null)
            return "";
        var name = column.Column.Name;
        return column.Path.StartsWith(name, StringComparison.Ordinal) ? column.Path[name.Length..] : null;
    }

    /// <summary>
    /// Column name → mv-expand/mv-apply text, for every column the query expands. Name-based and
    /// rule-wide on purpose: it over-marks comparisons made before the expansion, which only makes
    /// them non-validatable (safe), never wrongly validatable.
    /// </summary>
    private static Dictionary<string, string> CollectExpandedColumns(Kusto.Language.Syntax.SyntaxNode root)
    {
        var expanded = new Dictionary<string, string>(StringComparer.Ordinal);
        var expressions = root.GetDescendantsOrSelf<Kusto.Language.Syntax.MvExpandExpression>().Select(e => (Keyword: "mv-expand", e.Expression))
            .Concat(root.GetDescendantsOrSelf<Kusto.Language.Syntax.MvApplyExpression>().Select(e => (Keyword: "mv-apply", e.Expression)));
        foreach (var (keyword, expression) in expressions)
        {
            var name = expression is Kusto.Language.Syntax.SimpleNamedExpression named
                ? named.Name.SimpleName
                : ComparisonFinder.GetColumnRef(expression)?.Column.Name;
            if (name is not null)
                expanded.TryAdd(name, $"{keyword} {ComparisonFinder.Text(expression)}");
        }
        return expanded;
    }

    /// <summary>
    /// The syntax that computed the compared column. A join/union output column has no definition of
    /// its own, so follow OriginalColumns (breadth-first) to the first column that has one.
    /// </summary>
    private static string? ColumnDefinition(ColumnRef column)
    {
        var queue = new Queue<(Kusto.Language.Symbols.ColumnSymbol Column, int Depth)>();
        var seen = new HashSet<Kusto.Language.Symbols.ColumnSymbol>(ReferenceEqualityComparer.Instance);
        queue.Enqueue((column.Column, 0));
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth > 8 || !seen.Add(current))
                continue;
            if (current.Source is { } source)
            {
                var text = ComparisonFinder.Text(source);
                return text.Length <= MaxColumnDefinitionLength ? text : text[..MaxColumnDefinitionLength] + " …";
            }
            foreach (var original in current.OriginalColumns)
                queue.Enqueue((original, depth + 1));
        }
        return null;
    }

    private static UnresolvedComparison Unresolved(Rule rule, Comparison comparison, string status, string detail) => new()
    {
        RuleId = rule.RuleId,
        Status = status,
        Operator = comparison.Operator,
        Expression = comparison.Text,
        Detail = detail,
    };
}
