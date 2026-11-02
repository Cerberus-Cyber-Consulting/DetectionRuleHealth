using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RuleHealth.Core.Storage;

/// <summary>The one set of serializer options used for every document (engine, serve API and tests).</summary>
public static class Json
{
    /// <summary>camelCase, indented, UTC timestamps with a trailing Z.</summary>
    public static readonly JsonSerializerOptions Options = Create(strict: false);

    /// <summary>
    /// Same as <see cref="Options"/>, but unknown properties are an error. Used for input we do not
    /// control (PUT bodies) and in tests that prove the Model records match the schemas.
    /// </summary>
    public static readonly JsonSerializerOptions StrictOptions = Create(strict: true);

    private static JsonSerializerOptions Create(bool strict)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            // Keep KQL readable in the files: do not escape quotes, <, > and non-ASCII as \uXXXX.
            // The files are read by our own UI through fetch(), never embedded in HTML.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            UnmappedMemberHandling = strict ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip,
            // Reflection-based metadata; must be set explicitly before MakeReadOnly() on .NET 8.
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new UtcDateTimeConverter());
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>
/// Writes DateTime as ISO-8601 UTC ("2026-10-16T04:00:00Z") and normalises everything read to UTC.
/// The default converter would write local times with an offset, which breaks the "UTC everywhere" rule.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
            throw new JsonException($"'{text}' is not an ISO-8601 timestamp.");
        return value.UtcDateTime;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        // FFFFFFF drops trailing zero fractions, so whole seconds are written without ".0000000".
        writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
