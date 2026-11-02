using RuleHealth.Core.Extraction;
using RuleHealth.Core.Markdown;
using RuleHealth.Core.Model;

namespace RuleHealth.Tests;

/// <summary>Small, self-contained queries against a tiny schema; one behaviour per test.</summary>
public class ExtractionUnitTests
{
    private static readonly SchemaDocument Schema = new()
    {
        Tenant = "unit",
        GeneratedAt = DateTime.UtcNow,
        Source = "unit-test",
        Tables =
        {
            ["DeviceEvents"] = new() { ["Timestamp"] = "datetime", ["ActionType"] = "string", ["AdditionalFields"] = "dynamic", ["DeviceName"] = "string" },
            ["DeviceProcessEvents"] = new() { ["Timestamp"] = "datetime", ["ActionType"] = "string", ["FileName"] = "string", ["SHA1"] = "string" },
            ["IdentityInfo"] = new() { ["AccountUPN"] = "string", ["AccountUpn"] = "string" },
        },
    };

    private static CandidatesDocument Extract(string kql) =>
        new RuleExtractor(Schema).Extract("unit", new[] { RuleWith(kql) }).Candidates;

    private static Rule RuleWith(string kql) => new()
    {
        RuleId = "r1", Source = RuleSources.Markdown, Kind = "test", DisplayName = "test",
        Enabled = true, QueryText = kql, SchemaContext = SchemaContexts.Unified,
    };

    private static ValueCheck Single(CandidatesDocument doc) => Assert.Single(doc.Checks);

    [Theory]
    [InlineData("==", "eq", false)]
    [InlineData("!=", "eq", true)]
    [InlineData("=~", "eq_ci", false)]
    [InlineData("!~", "eq_ci", true)]
    [InlineData("in", "eq", false)]
    [InlineData("!in~", "eq_ci", true)]
    [InlineData("has_any", "has", false)]
    [InlineData("!has_cs", "has_cs", true)]
    [InlineData("!startswith", "startswith", true)]
    [InlineData("matches regex", "regex", false)]
    public void Operator_classes(string op, string expectedClass, bool expectedNegated)
    {
        Assert.Equal((expectedClass, expectedNegated), OperatorClassifier.Classify(op));
    }

    [Fact]
    public void Unknown_operator_has_no_class() => Assert.Null(OperatorClassifier.Classify("~~"));

    [Fact]
    public void Direct_comparison_becomes_a_validatable_check()
    {
        var check = Single(Extract("DeviceEvents | where ActionType == \"PowerShellCommand\""));

        Assert.Equal(("DeviceEvents", "ActionType", "eq", "PowerShellCommand"), (check.Table, check.Column, check.OperatorClass, check.Value));
        Assert.Equal(Lineages.Direct, check.Lineage);
        Assert.True(check.Validatable);
        Assert.Equal(CheckIdentity.Compute(check), check.CheckId);
    }

    [Fact]
    public void Reversed_comparison_is_found()
    {
        var check = Single(Extract("DeviceEvents | where \"PowerShellCommand\" == ActionType"));

        Assert.Equal("PowerShellCommand", check.Value);
    }

    [Fact]
    public void Let_list_is_resolved_to_one_check_per_value()
    {
        var doc = Extract("""
            let actions = dynamic(["A", "B"]);
            let more = pack_array("C");
            DeviceEvents | where ActionType in (actions) or ActionType has_any (more)
            """);

        Assert.Equal(new[] { "A", "B", "C" }, doc.Checks.Select(c => c.Value).OrderBy(v => v));
    }

    [Fact]
    public void Negation_is_on_the_occurrence_and_shares_the_check()
    {
        var doc = Extract("DeviceEvents | where ActionType == \"X\" | where ActionType != \"X\"");

        var check = Single(doc);
        Assert.Equal(new[] { false, true }, check.Occurrences.Select(o => o.Negated));
    }

    [Fact]
    public void Comparisons_inside_not_are_exclusions_and_double_not_cancels()
    {
        var doc = Extract("""
            DeviceEvents
            | where not(ActionType == "A" and DeviceName in ("B"))
            | where not(not(ActionType == "C"))
            | where not(ActionType != "D")
            """);

        var negated = doc.Checks.ToDictionary(c => c.Value, c => Assert.Single(c.Occurrences).Negated);
        Assert.Equal(new Dictionary<string, bool> { ["A"] = true, ["B"] = true, ["C"] = false, ["D"] = false }, negated);
    }

    [Fact]
    public void Wrapped_column_and_dynamic_path_are_unwrapped()
    {
        var check = Single(Extract("DeviceEvents | where tolower(tostring(AdditionalFields.Command.Name)) == \"x\""));

        Assert.Equal(("AdditionalFields", "AdditionalFields.Command.Name"), (check.Column, check.Path));
    }

    [Fact]
    public void Extended_column_has_expression_lineage_and_definition()
    {
        var check = Single(Extract("DeviceProcessEvents | extend Name = tolower(FileName) | where Name == \"cmd.exe\""));

        Assert.Equal(("DeviceProcessEvents", "FileName", Lineages.Expression), (check.Table, check.Column, check.Lineage));
        Assert.False(check.Validatable);
        var occurrence = Assert.Single(check.Occurrences);
        Assert.Equal("Name", occurrence.DerivedColumn);
        Assert.Equal("tolower(FileName)", occurrence.ColumnDefinition);
    }

    [Fact]
    public void Assignment_of_a_bare_column_is_a_rename_and_the_path_is_rebased()
    {
        var doc = Extract("""
            DeviceEvents
            | project Act = ActionType, Extra = AdditionalFields
            | where Act == "A" and Extra.Command == "B"
            """);

        Assert.All(doc.Checks, c => Assert.Equal(Lineages.Renamed, c.Lineage));
        Assert.All(doc.Checks, c => Assert.True(c.Validatable));
        Assert.Equal("AdditionalFields.Command", doc.Checks.Single(c => c.Value == "B").Path);
    }

    [Fact]
    public void Parse_output_is_not_a_rename()
    {
        var check = Single(Extract("DeviceEvents | parse ActionType with Prefix \"_\" * | where Prefix == \"Process\""));

        Assert.Equal(Lineages.Expression, check.Lineage);
    }

    [Fact]
    public void Value_transforming_wrapper_makes_the_check_derived()
    {
        var check = Single(Extract("DeviceEvents | where split(ActionType, \"_\")[0] == \"Process\""));

        Assert.Equal(("ActionType", Lineages.Expression), (check.Column, check.Lineage));
        Assert.False(check.Validatable);
    }

    [Fact]
    public void Wrapper_without_value_flow_is_a_computed_column()
    {
        var doc = Extract("DeviceEvents | where array_length(AdditionalFields) == 0");

        Assert.Empty(doc.Checks);
        var unresolved = Assert.Single(doc.Unresolved);
        Assert.Equal(UnresolvedStatuses.ComputedColumn, unresolved.Status);
        Assert.Contains("array_length(AdditionalFields)", unresolved.Detail);
    }

    [Fact]
    public void Iff_condition_columns_are_not_sources_of_the_value()
    {
        // TimeGenerated/DeviceName only decide which branch is taken; the value comes from the branches.
        var doc = Extract("""
            DeviceEvents
            | extend Flag = iff(isempty(DeviceName) and Timestamp > ago(1d), true, false)
            | extend Kind = iff(DeviceName == "x", ActionType, "none")
            | where Flag == false and Kind == "Started"
            """);

        Assert.Equal(UnresolvedStatuses.ComputedColumn, Assert.Single(doc.Unresolved).Status); // Flag == false
        var kind = Assert.Single(doc.Checks, c => c.Value == "Started");                       // Kind == "Started"
        Assert.Equal(("ActionType", Lineages.Expression), (kind.Column, kind.Lineage));
        // The condition `DeviceName == "x"` is a comparison of its own and stays a direct check.
        var condition = Assert.Single(doc.Checks, c => c.Value == "x");
        Assert.Equal(("DeviceName", Lineages.Direct), (condition.Column, condition.Lineage));
        Assert.DoesNotContain(doc.Checks, c => c.Column is "Timestamp" || (c.Column == "DeviceName" && c.Value != "x"));
    }

    [Fact]
    public void Mv_expanded_column_is_derived_and_loses_its_path()
    {
        var check = Single(Extract("DeviceEvents | mv-expand AdditionalFields | where AdditionalFields.type == \"X\""));

        Assert.Equal((Lineages.Expression, (string?)null), (check.Lineage, check.Path));
        var occurrence = Assert.Single(check.Occurrences);
        Assert.Equal("AdditionalFields.type", occurrence.DerivedPath);
        Assert.Equal("mv-expand AdditionalFields", occurrence.ColumnDefinition);
    }

    [Fact]
    public void Selecting_a_key_from_a_packed_object_follows_only_that_value()
    {
        var doc = Extract("""
            DeviceEvents
            | extend Obj = pack("Name", DeviceName, "Action", ActionType)
            | summarize Objs = make_set(Obj) by Timestamp
            | where Objs.Action == "Started" or Objs["Name"] == "host1"
            """);

        Assert.Equal(new[] { ("ActionType", "Started"), ("DeviceName", "host1") },
            doc.Checks.Select(c => (c.Column, c.Value)).OrderBy(x => x.Column).ToArray());
    }

    [Fact]
    public void Unknown_key_on_a_packed_object_falls_back_to_every_value()
    {
        var doc = Extract("""
            DeviceEvents
            | extend Obj = pack("Name", DeviceName, "Action", ActionType)
            | where Obj.Other == "x"
            """);

        Assert.Equal(new[] { "ActionType", "DeviceName" }, doc.Checks.Select(c => c.Column).OrderBy(c => c));
    }

    [Theory]
    [InlineData("parse_json(AdditionalFields).Command.Name == \"x\"", "AdditionalFields.Command.Name")]
    [InlineData("tostring(todynamic(AdditionalFields).SignatureName) =~ \"x\"", "AdditionalFields.SignatureName")]
    [InlineData("tostring(parse_json(AdditionalFields)[\"Command\"]) == \"x\"", "AdditionalFields[\"Command\"]")]
    public void Path_through_a_type_wrapper_is_rebased_onto_the_column(string comparison, string expectedPath)
    {
        var check = Single(Extract($"DeviceEvents | where {comparison}"));

        Assert.Equal(("AdditionalFields", expectedPath, Lineages.Direct), (check.Column, check.Path, check.Lineage));
        Assert.True(check.Validatable);
    }

    [Fact]
    public void Comparison_text_does_not_include_a_leading_comment()
    {
        var check = Single(Extract("""
            DeviceEvents
            | where
                // only process starts
                ActionType == "ProcessCreated"
            """));

        Assert.Equal("ActionType == \"ProcessCreated\"", Assert.Single(check.Occurrences).Expression);
    }

    [Fact]
    public void Extend_of_a_parsed_column_is_a_rename_that_keeps_each_field_apart()
    {
        var doc = Extract("""
            DeviceEvents
            | extend Auth = parse_json(AdditionalFields)
            | where Auth.SPF == "fail" and Auth.DMARC == "fail"
            """);

        Assert.Equal(new[] { "AdditionalFields.DMARC", "AdditionalFields.SPF" }, doc.Checks.Select(c => c.Path).OrderBy(p => p));
        Assert.All(doc.Checks, c => Assert.True(c.Validatable && c.Lineage == Lineages.Renamed));
    }

    [Theory]
    [InlineData("extend FileType = tostring(parse_json(AdditionalFields).FileType) | where FileType contains \"Exe\"", "AdditionalFields.FileType")]
    [InlineData("extend A = parse_json(AdditionalFields) | extend B = A.x | where B.y == \"v\"", "AdditionalFields.x.y")]
    [InlineData("extend Role = AdditionalFields[\"rawData\"][\"role\"] | where Role has \"DC\"", "AdditionalFields[\"rawData\"][\"role\"]")]
    public void Path_only_definitions_compose_into_the_check_path(string pipeline, string expectedPath)
    {
        var check = Single(Extract($"DeviceEvents | {pipeline}"));

        Assert.Equal(("AdditionalFields", expectedPath, Lineages.Renamed), (check.Column, check.Path, check.Lineage));
    }

    [Fact]
    public void Unnamed_type_wrapper_in_distinct_keeps_the_path()
    {
        var check = Single(Extract("""
            DeviceEvents
            | extend Level = todynamic(AdditionalFields).rawData.level
            | distinct DeviceName, tostring(Level)
            | where Level == "0"
            """));

        Assert.Equal(("AdditionalFields.rawData.level", Lineages.Renamed), (check.Path, check.Lineage));
    }

    [Fact]
    public void Path_through_an_expanded_column_is_not_a_table_path()
    {
        // After mv-expand, Items is one array element; Items.Name is not a path on AdditionalFields itself.
        var check = Single(Extract("""
            DeviceEvents
            | extend Items = todynamic(AdditionalFields)
            | mv-expand Items
            | extend Name = tostring(Items.Name)
            | where Name == "v"
            """));

        Assert.Equal((Lineages.Expression, (string?)null), (check.Lineage, check.Path));
        Assert.False(check.Validatable);
    }

    [Fact]
    public void Value_changing_wrapper_around_a_path_is_not_a_rename()
    {
        var check = Single(Extract("DeviceEvents | extend L = toupper(tostring(AdditionalFields.a)) | where L == \"V\""));

        Assert.Equal((Lineages.Expression, (string?)null), (check.Lineage, check.Path));
    }

    [Fact]
    public void Union_column_yields_one_check_per_table()
    {
        var doc = Extract("union DeviceEvents, DeviceProcessEvents | where ActionType == \"X\"");

        Assert.Equal(new[] { "DeviceEvents", "DeviceProcessEvents" }, doc.Checks.Select(c => c.Table));
        Assert.All(doc.Checks, c => Assert.Equal(Lineages.Renamed, c.Lineage));
    }

    [Fact]
    public void Column_names_are_case_sensitive()
    {
        var doc = Extract("IdentityInfo | where AccountUPN == \"a\" | where AccountUpn == \"b\"");

        Assert.Equal(new[] { "AccountUPN", "AccountUpn" }, doc.Checks.Select(c => c.Column).OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void Join_keys_and_column_comparisons_are_skipped()
    {
        var doc = Extract("""
            DeviceEvents
            | join (DeviceProcessEvents) on $left.DeviceName == $right.FileName
            | where ActionType == ActionType1
            """);

        Assert.Empty(doc.Checks);
        Assert.Equal(0, Assert.Single(doc.RuleSummaries).Comparisons);
    }

    [Fact]
    public void Empty_string_comparison_is_a_null_check()
    {
        var doc = Extract("DeviceEvents | where ActionType != \"\"");

        Assert.Empty(doc.Checks);
        Assert.Equal(1, Assert.Single(doc.RuleSummaries).NullChecks);
    }

    [Fact]
    public void Toscalar_value_is_unresolved_runtime()
    {
        var doc = Extract("""
            let names = toscalar(DeviceProcessEvents | summarize make_set(FileName));
            DeviceEvents | where ActionType in (names)
            """);

        var unresolved = Assert.Single(doc.Unresolved);
        Assert.Equal(UnresolvedStatuses.RuntimeValue, unresolved.Status);
        Assert.Equal("Runtime:ToScalarExpression", unresolved.Detail);
    }

    [Fact]
    public void FileProfile_output_is_a_computed_column()
    {
        var doc = Extract("DeviceProcessEvents | invoke FileProfile(SHA1, 1000) | where SignatureState =~ \"Unsigned\"");

        var unresolved = Assert.Single(doc.Unresolved);
        Assert.Equal(UnresolvedStatuses.ComputedColumn, unresolved.Status);
        Assert.Empty(Assert.Single(doc.RuleSummaries).BindErrors);
    }

    [Fact]
    public void Unknown_column_is_a_bind_error_and_unbound()
    {
        var doc = Extract("DeviceEvents | where NoSuchColumn == \"x\"");

        Assert.Equal(UnresolvedStatuses.UnboundExpression, Assert.Single(doc.Unresolved).Status);
        Assert.NotEmpty(Assert.Single(doc.RuleSummaries).BindErrors);
    }

    [Theory]
    [InlineData("DeviceEvents | where ActionType == \"x\"", "DeviceEvents")]
    [InlineData("union DeviceEvents, DeviceProcessEvents | take 1", "DeviceEvents,DeviceProcessEvents")]
    [InlineData("union Device* | take 1", "DeviceEvents,DeviceProcessEvents")]
    [InlineData("let p = materialize(DeviceProcessEvents | take 5); DeviceEvents | join kind=inner (p) on DeviceName", "DeviceEvents,DeviceProcessEvents")]
    [InlineData("DeviceEvents | join (IdentityInfo | project AccountUpn) on $left.DeviceName == $right.AccountUpn", "DeviceEvents,IdentityInfo")]
    [InlineData("table(\"IdentityInfo\") | take 1", "IdentityInfo")]
    [InlineData("UnknownTable | take 1", "")]
    [InlineData("DeviceEvents | extend IdentityInfo = DeviceName | where IdentityInfo == \"x\"", "DeviceEvents")]
    [InlineData("let IdentityInfo = DeviceEvents | take 5; IdentityInfo | take 1", "DeviceEvents")]
    [InlineData("print x = 1", "")]
    public void Rule_summary_lists_the_tables_the_query_reads(string kql, string expected)
    {
        var summary = Assert.Single(Extract(kql).RuleSummaries);

        Assert.Equal(expected, string.Join(",", summary.Tables!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  N/A \n")]
    [InlineData("n/a")]
    public void Placeholder_queries_are_not_analysed(string text)
    {
        var result = new RuleExtractor(Schema).Extract("unit", new[] { RuleWith(text) });

        Assert.Equal(0, result.Stats.QueriesAnalysed);
        Assert.Empty(result.Candidates.RuleSummaries);
    }

    [Fact]
    public void Magic_function_argument_is_quoted()
    {
        Assert.Equal("T | invoke FileProfile('SHA1', 1000)", KqlPreprocessor.Prepare("T | invoke FileProfile(SHA1, 1000)"));
        Assert.Equal("T | invoke DeviceFromIP('IP')", KqlPreprocessor.Prepare("T | invoke DeviceFromIP(IP)"));
    }

    [Fact]
    public void Unified_merge_keeps_both_spellings_and_adds_TimeGenerated()
    {
        var merged = SchemaFileReader.MergeUnified(
            new() { ["IdentityInfo"] = new() { ["AccountUPN"] = "string", ["TimeGenerated"] = "datetime" } },
            new() { ["IdentityInfo"] = new() { ["AccountUpn"] = "string" }, ["DeviceEvents"] = new() { ["Timestamp"] = "datetime" } });

        Assert.Equal(new[] { "AccountUPN", "TimeGenerated", "AccountUpn" }, merged["IdentityInfo"].Keys);
        Assert.Equal(new[] { "Timestamp", "TimeGenerated" }, merged["DeviceEvents"].Keys);
    }
}

public sealed class MarkdownRuleReaderTests : IDisposable
{
    private readonly string _repo = TestPaths.NewTempDirectory();

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    [Fact]
    public void Reads_blocks_with_stable_ids_and_skips_placeholders()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "Endpoint"));
        File.WriteAllText(Path.Combine(_repo, "Endpoint", "Rule.md"), """
            # *My rule*
            ## Defender XDR
            ```KQL
            N/A
            ```
            ## Sentinel
            ```kql
            DeviceEvents
            | take 1
            ```
            """);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# Readme\n```KQL\nT\n```\n");

        var rule = Assert.Single(MarkdownRuleReader.Read(_repo));

        Assert.Equal("md:Endpoint/Rule.md#2", rule.RuleId);
        Assert.Equal("My rule (Sentinel)", rule.DisplayName);
        Assert.Equal("Sentinel", rule.Kind);
        Assert.Equal("DeviceEvents\n| take 1\n", rule.QueryText);
        Assert.Equal(SchemaContexts.Unified, rule.SchemaContext);
    }

    [Fact]
    public void Origin_links_to_the_git_remote()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        File.WriteAllText(Path.Combine(_repo, ".git", "config"), "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = git@github.com:Owner/Rules.git\n");
        File.WriteAllText(Path.Combine(_repo, ".git", "HEAD"), "ref: refs/heads/main\n");

        Assert.Equal("https://github.com/Owner/Rules/blob/main/", GitOrigin.TryGetBlobBaseUrl(_repo));
    }
}
