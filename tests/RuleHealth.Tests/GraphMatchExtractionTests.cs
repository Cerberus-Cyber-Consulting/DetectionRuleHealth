using RuleHealth.Core.Extraction;
using RuleHealth.Core.Model;

namespace RuleHealth.Tests;

/// <summary>
/// graph-match predicates (`where SourceNode.NodeLabel == "device"`) read columns of the node and edge
/// tables. Before the fix they were UnboundExpression, so values like NodeLabel "device" could not be
/// selected (found by the extraction-verifier on the M8 re-baseline).
/// </summary>
public class GraphMatchExtractionTests
{
    private static readonly SchemaDocument Schema = new()
    {
        Tenant = "unit",
        GeneratedAt = DateTime.UtcNow,
        Source = "unit-test",
        Tables =
        {
            ["ExposureGraphNodes"] = new() { ["NodeId"] = "string", ["NodeLabel"] = "string", ["NodeName"] = "string", ["NodeProperties"] = "dynamic" },
            ["ExposureGraphEdges"] = new() { ["SourceNodeId"] = "string", ["TargetNodeId"] = "string", ["EdgeLabel"] = "string" },
        },
    };

    private static CandidatesDocument Extract(string kql) => new RuleExtractor(Schema).Extract("unit", new[]
    {
        new Rule
        {
            RuleId = "r1", Source = RuleSources.Markdown, Kind = "test", DisplayName = "test",
            Enabled = true, QueryText = kql, SchemaContext = SchemaContexts.Unified,
        },
    }).Candidates;

    private const string Graph = """
        ExposureGraphEdges
        | make-graph SourceNodeId --> TargetNodeId with ExposureGraphNodes on NodeId
        """;

    [Fact]
    public void Node_and_edge_predicates_become_checks_on_the_node_and_edge_tables()
    {
        var candidates = Extract(Graph + """

            | graph-match (SourceNode)-[e]->(TargetNode)
                where SourceNode.NodeLabel in ("device", "microsoft.compute/virtualmachines") and TargetNode.NodeLabel == "user"
                    and e.EdgeLabel == "can authenticate to"
                project SourceNode.NodeName
            """);

        Assert.Empty(candidates.Unresolved);
        Assert.Equal(new[]
            {
                ("ExposureGraphEdges", "EdgeLabel", (string?)null, "can authenticate to"),
                ("ExposureGraphNodes", "NodeLabel", null, "device"),
                ("ExposureGraphNodes", "NodeLabel", null, "microsoft.compute/virtualmachines"),
                ("ExposureGraphNodes", "NodeLabel", null, "user"),
            },
            candidates.Checks.Select(c => (c.Table, c.Column, c.Path, c.Value)).OrderBy(c => c.Table).ThenBy(c => c.Value, StringComparer.Ordinal));
        Assert.All(candidates.Checks, c => Assert.True(c.Validatable));
        Assert.All(candidates.Checks, c => Assert.Equal(Lineages.Direct, c.Lineage));
    }

    [Fact]
    public void A_path_below_a_node_property_keeps_the_path_without_the_variable()
    {
        var check = Assert.Single(Extract(Graph + """

            | graph-match (SourceNode)-[e]->(TargetNode)
                where TargetNode.NodeProperties.rawData.criticalityLevel == "0"
                project TargetNode.NodeName
            """).Checks);

        Assert.Equal(("ExposureGraphNodes", "NodeProperties", "NodeProperties.rawData.criticalityLevel", "0"),
            (check.Table, check.Column, check.Path, check.Value));
    }

    [Fact]
    public void A_runtime_list_on_a_node_property_is_a_runtime_value()
    {
        var candidates = Extract("""
            let critical_users = toscalar(ExposureGraphNodes | where NodeLabel == "user" | summarize make_set(NodeName));
            """ + Graph + """

            | graph-match (SourceNode)-[e]->(TargetNode)
                where TargetNode.NodeName in (critical_users)
                project TargetNode.NodeName
            """);

        var unresolved = Assert.Single(candidates.Unresolved);
        Assert.Equal(UnresolvedStatuses.RuntimeValue, unresolved.Status);
        Assert.Equal(("ExposureGraphNodes", "NodeLabel", "user"), (candidates.Checks.Single().Table, candidates.Checks.Single().Column, candidates.Checks.Single().Value));
    }
}
