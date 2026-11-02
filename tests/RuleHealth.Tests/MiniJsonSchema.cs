using System.Text.Json;
using System.Text.RegularExpressions;

namespace RuleHealth.Tests;

/// <summary>
/// Just enough JSON Schema (draft 2020-12) to check our own schemas/*.json — no extra package.
/// Supported: $ref (local #/$defs/...), type, const, enum, properties, required,
/// additionalProperties, propertyNames, items, minItems, pattern, minimum, maximum, anyOf, allOf,
/// if/then/else, format date-time. Any other keyword makes validation fail, so a schema change that uses a new
/// keyword cannot be silently ignored.
/// </summary>
public sealed class MiniJsonSchema
{
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$defs", "title", "description", "default",
    };

    private static readonly Regex DateTimePattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant);

    private readonly JsonElement _root;

    public MiniJsonSchema(string schemaPath)
    {
        _root = JsonDocument.Parse(File.ReadAllText(schemaPath)).RootElement.Clone();
    }

    public List<string> Validate(JsonElement instance)
    {
        var errors = new List<string>();
        Check(_root, instance, "$", errors);
        return errors;
    }

    private void Check(JsonElement schema, JsonElement value, string at, List<string> errors)
    {
        foreach (var keyword in schema.EnumerateObject())
        {
            var k = keyword.Value;
            switch (keyword.Name)
            {
                case "$ref":
                    Check(Resolve(k.GetString()!), value, at, errors);
                    break;
                case "type":
                    var types = k.ValueKind == JsonValueKind.Array
                        ? k.EnumerateArray().Select(t => t.GetString()!).ToList()
                        : new List<string> { k.GetString()! };
                    if (!types.Any(t => IsType(value, t)))
                        errors.Add($"{at}: expected {string.Join("|", types)}, got {value.ValueKind}");
                    break;
                case "const":
                    if (!JsonEquals(k, value))
                        errors.Add($"{at}: must be {k.GetRawText()}");
                    break;
                case "enum":
                    if (!k.EnumerateArray().Any(option => JsonEquals(option, value)))
                        errors.Add($"{at}: {value.GetRawText()} not in enum");
                    break;
                case "properties":
                    if (value.ValueKind == JsonValueKind.Object)
                        foreach (var property in k.EnumerateObject())
                            if (value.TryGetProperty(property.Name, out var child))
                                Check(property.Value, child, $"{at}.{property.Name}", errors);
                    break;
                case "required":
                    if (value.ValueKind == JsonValueKind.Object)
                        foreach (var name in k.EnumerateArray())
                            if (!value.TryGetProperty(name.GetString()!, out _))
                                errors.Add($"{at}: missing required '{name.GetString()}'");
                    break;
                case "additionalProperties":
                    if (value.ValueKind != JsonValueKind.Object)
                        break;
                    var declared = schema.TryGetProperty("properties", out var props)
                        ? props.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                        : new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject().Where(p => !declared.Contains(p.Name)))
                    {
                        if (k.ValueKind == JsonValueKind.False)
                            errors.Add($"{at}: unexpected property '{property.Name}'");
                        else if (k.ValueKind == JsonValueKind.Object)
                            Check(k, property.Value, $"{at}.{property.Name}", errors);
                    }
                    break;
                case "propertyNames":
                    if (value.ValueKind == JsonValueKind.Object)
                        foreach (var property in value.EnumerateObject())
                            Check(k, JsonDocument.Parse(JsonSerializer.Serialize(property.Name)).RootElement, $"{at}[name {property.Name}]", errors);
                    break;
                case "items":
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        var i = 0;
                        foreach (var item in value.EnumerateArray())
                            Check(k, item, $"{at}[{i++}]", errors);
                    }
                    break;
                case "minItems":
                    if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() < k.GetInt32())
                        errors.Add($"{at}: fewer than {k.GetInt32()} items");
                    break;
                case "pattern":
                    if (value.ValueKind == JsonValueKind.String && !Regex.IsMatch(value.GetString()!, k.GetString()!))
                        errors.Add($"{at}: '{value.GetString()}' does not match {k.GetString()}");
                    break;
                case "minimum":
                    if (value.ValueKind == JsonValueKind.Number && value.GetDecimal() < k.GetDecimal())
                        errors.Add($"{at}: below minimum {k.GetDecimal()}");
                    break;
                case "maximum":
                    if (value.ValueKind == JsonValueKind.Number && value.GetDecimal() > k.GetDecimal())
                        errors.Add($"{at}: above maximum {k.GetDecimal()}");
                    break;
                case "anyOf":
                    if (!k.EnumerateArray().Any(option => { var e = new List<string>(); Check(option, value, at, e); return e.Count == 0; }))
                        errors.Add($"{at}: matches none of anyOf");
                    break;
                case "allOf":
                    foreach (var part in k.EnumerateArray())
                        Check(part, value, at, errors);
                    break;
                case "if":
                    // The condition's own errors are not reported; it only picks then or else.
                    var conditionErrors = new List<string>();
                    Check(k, value, at, conditionErrors);
                    if (schema.TryGetProperty(conditionErrors.Count == 0 ? "then" : "else", out var branch))
                        Check(branch, value, at, errors);
                    break;
                case "then":
                case "else":
                    break; // applied by "if"

                case "format":
                    if (k.GetString() == "date-time" && value.ValueKind == JsonValueKind.String && !DateTimePattern.IsMatch(value.GetString()!))
                        errors.Add($"{at}: '{value.GetString()}' is not an ISO-8601 date-time");
                    break;
                default:
                    if (!Annotations.Contains(keyword.Name))
                        errors.Add($"{at}: schema keyword '{keyword.Name}' is not supported by MiniJsonSchema");
                    break;
            }
        }
    }

    private JsonElement Resolve(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            throw new NotSupportedException("Only local $ref is supported: " + reference);
        var node = _root;
        foreach (var part in reference[2..].Split('/'))
            node = node.GetProperty(part);
        return node;
    }

    private static bool IsType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.GetDecimal() % 1 == 0,
        _ => throw new NotSupportedException("type " + type),
    };

    /// <summary>Structural equality; numbers compare by value (0 == 0.0), missing == null for object members.</summary>
    public static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
            return a.GetDecimal() == b.GetDecimal();
        if (a.ValueKind != b.ValueKind)
            return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.String:
                return a.GetString() == b.GetString();
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength()
                    && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => JsonEquals(pair.First, pair.Second));
            case JsonValueKind.Object:
                var names = a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name));
                foreach (var name in names)
                {
                    var hasA = a.TryGetProperty(name, out var va);
                    var hasB = b.TryGetProperty(name, out var vb);
                    if (hasA && hasB ? !JsonEquals(va, vb) : (hasA ? va : vb).ValueKind != JsonValueKind.Null)
                        return false;
                }
                return true;
            default:
                return true; // true/false/null: ValueKind equality is enough
        }
    }
}
