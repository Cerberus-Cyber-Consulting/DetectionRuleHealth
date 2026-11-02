using RuleHealth.Core;
using RuleHealth.Core.Operations;
using RuleHealth.Core.Storage;

namespace RuleHealth.Cli.Commands;

/// <summary>
/// `rulehealth schema export --tenant x --out <file> [--include-custom]`: a tenant's collected schema.json
/// as a fixture for offline extraction and the golden test. Thin shell around <see cref="SchemaExportOperation"/>.
/// </summary>
public static class SchemaCommand
{
    public static int Run(CliArgs cli)
    {
        if (cli.Positionals.Count < 2 || cli.Positionals[1] != "export")
            throw new RuleHealthException("Use `rulehealth schema export --tenant <alias> --out <file> [--include-custom]`.");
        cli.AllowOnly("tenant", "out", "include-custom");
        SchemaExportOperation.Run(new JsonStore(cli.DataDirectory), cli.Require("tenant"), cli.Require("out"),
            cli.Has("include-custom"), Console.WriteLine);
        return 0;
    }
}
