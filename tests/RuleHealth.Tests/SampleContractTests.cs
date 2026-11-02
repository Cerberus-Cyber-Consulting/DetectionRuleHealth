using System.Text.Json;
using RuleHealth.Core.Model;
using RuleHealth.Core.Storage;

namespace RuleHealth.Tests;

/// <summary>
/// samples/ must always match schemas/ (CLAUDE.md), and the Model records must match both:
/// every sample round-trips through its record with unknown properties disallowed, and the output
/// still validates and equals the input.
/// </summary>
public class SampleContractTests
{
    private static readonly Dictionary<string, Type> DocumentTypes = new(StringComparer.Ordinal)
    {
        [Documents.Tenant] = typeof(TenantDocument),
        [Documents.Rules] = typeof(RulesDocument),
        [Documents.Schema] = typeof(SchemaDocument),
        [Documents.Incidents] = typeof(IncidentsDocument),
        [Documents.RuleStats] = typeof(RuleStatsDocument),
        [Documents.Candidates] = typeof(CandidatesDocument),
        [Documents.Selections] = typeof(SelectionsDocument),
        [Documents.Validation] = typeof(ValidationDocument),
    };

    public static IEnumerable<object[]> Samples() =>
        TestPaths.SampleFiles().Select(s => new object[] { s.Tenant + "/" + s.Document });

    [Fact]
    public void Every_document_has_a_schema_and_a_model_type()
    {
        foreach (var document in Documents.All)
        {
            Assert.True(File.Exists(SchemaPath(document)), $"schemas/{document}.schema.json missing");
            Assert.True(DocumentTypes.ContainsKey(document), $"no Model type for {document}");
        }
        Assert.NotEmpty(Samples());
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_validates_against_schema(string sample)
    {
        var (path, document) = Resolve(sample);
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));

        var errors = new MiniJsonSchema(SchemaPath(document)).Validate(json.RootElement);

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Take(25)));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_round_trips_through_model(string sample)
    {
        var (path, document) = Resolve(sample);
        var bytes = File.ReadAllBytes(path);
        var type = DocumentTypes[document];

        var model = JsonSerializer.Deserialize(bytes, type, Json.StrictOptions);
        var written = JsonSerializer.SerializeToUtf8Bytes(model, type, Json.Options);

        using var original = JsonDocument.Parse(bytes);
        using var roundTripped = JsonDocument.Parse(written);
        var errors = new MiniJsonSchema(SchemaPath(document)).Validate(roundTripped.RootElement);
        Assert.True(errors.Count == 0, "re-serialized document breaks the schema:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Take(25)));
        Assert.True(MiniJsonSchema.JsonEquals(original.RootElement, roundTripped.RootElement), "round-trip changed the document");
    }

    [Fact]
    public void Sample_selections_are_accepted_by_the_put_validator()
    {
        var selections = JsonStore.ReadFile<SelectionsDocument>(Path.Combine(TestPaths.Samples, "hybridbrothers", "selections.json"));

        var errors = SelectionsValidator.Validate(selections, "hybridbrothers");

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Take(25)));
    }

    private static string SchemaPath(string document) => Path.Combine(TestPaths.Schemas, document + ".schema.json");

    private static (string Path, string Document) Resolve(string sample)
    {
        var match = TestPaths.SampleFiles().Single(s => s.Tenant + "/" + s.Document == sample);
        return (match.Path, match.Document);
    }
}
