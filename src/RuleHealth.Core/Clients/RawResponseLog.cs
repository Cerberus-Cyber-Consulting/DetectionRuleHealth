using System.Text.RegularExpressions;

namespace RuleHealth.Core.Clients;

/// <summary>
/// Saves every API response body as-is to data/&lt;tenant&gt;/raw/ (`collect --save-raw`). Used on the
/// first live run to confirm the field names marked VERIFY in the ms-security-apis skill.
/// The files contain tenant data: they stay in data/, which is git-ignored and never served.
/// </summary>
public sealed partial class RawResponseLog
{
    private readonly object _lock = new();
    private int _sequence;

    public RawResponseLog(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
    }

    public string Folder { get; }

    public void Save(string label, HttpRequestMessage request, int status, string body)
    {
        lock (_lock)
        {
            var number = ++_sequence;
            var name = $"{number:000}-{Slug().Replace(label.ToLowerInvariant(), "-").Trim('-')}-{status}.json";
            File.WriteAllText(Path.Combine(Folder, name), body);
            // index.txt maps each file to its request, so the bodies stay valid JSON.
            File.AppendAllText(Path.Combine(Folder, "index.txt"), $"{name}\t{request.Method} {request.RequestUri}\n");
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex Slug();
}
