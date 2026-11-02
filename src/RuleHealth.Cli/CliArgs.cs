using RuleHealth.Core;

namespace RuleHealth.Cli;

/// <summary>
/// Minimal `verb [subverb] --key value --flag` parser. Options may repeat (--workspace a --workspace b).
/// Deliberately hand-rolled: the CLI is small and this keeps the dependency list empty.
/// </summary>
public sealed class CliArgs
{
    // Options that never take a value. Everything else starting with -- expects one.
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "verbose", "dry-run", "help", "save-raw", "include-basic", "open", "version" };

    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = new();

    /// <summary>The RULEHEALTH_* environment variables; options on the command line win over them.</summary>
    public AppSettings Settings { get; private init; } = new();

    /// <summary>--data, else RULEHEALTH_DATA, else ./data.</summary>
    public string DataDirectory => Get("data") ?? Settings.DataDir;

    /// <summary>--verbose or RULEHEALTH_LOG_LEVEL=debug: log one line per HTTP call (never tokens).</summary>
    public bool Verbose => Has("verbose") || Settings.Debug;

    public static CliArgs Parse(IReadOnlyList<string> args, AppSettings? settings = null)
    {
        var result = new CliArgs { Settings = settings ?? new AppSettings() };
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "/?")
            {
                result._flags.Add("help");
                continue;
            }
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? inlineValue = null;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                inlineValue = name[(eq + 1)..];
                name = name[..eq];
            }

            if (Flags.Contains(name))
            {
                result._flags.Add(name);
                continue;
            }

            var value = inlineValue;
            if (value is null)
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new RuleHealthException($"Option --{name} needs a value.");
                value = args[++i];
            }
            if (!result._options.TryGetValue(name, out var list))
                result._options[name] = list = new List<string>();
            list.Add(value);
        }
        return result;
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Get(string name) =>
        _options.TryGetValue(name, out var values) ? values[^1] : null;

    public string Get(string name, string defaultValue) => Get(name) ?? defaultValue;

    public string Require(string name) =>
        Get(name) ?? throw new RuleHealthException($"Option --{name} is required.");

    public IReadOnlyList<string> GetAll(string name) =>
        _options.TryGetValue(name, out var values) ? values : Array.Empty<string>();

    public int GetInt(string name, int defaultValue)
    {
        var text = Get(name);
        if (text is null)
            return defaultValue;
        return int.TryParse(text, out var value)
            ? value
            : throw new RuleHealthException($"Option --{name} must be a number, got '{text}'.");
    }

    /// <summary>Fails on options the verb does not know, so typos do not silently fall back to defaults.</summary>
    public void AllowOnly(params string[] names)
    {
        var allowed = new HashSet<string>(names, StringComparer.Ordinal) { "data", "verbose", "help" };
        var unknown = _options.Keys.Concat(_flags).Where(name => !allowed.Contains(name)).ToList();
        if (unknown.Count > 0)
            throw new RuleHealthException($"Unknown option(s): {string.Join(", ", unknown.Select(n => "--" + n))}.");
    }
}
