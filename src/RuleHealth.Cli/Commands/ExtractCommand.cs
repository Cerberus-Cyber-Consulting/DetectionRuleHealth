using RuleHealth.Core.Operations;
using RuleHealth.Core.Storage;

namespace RuleHealth.Cli.Commands;

/// <summary>
/// `rulehealth extract --tenant x` parses the tenant's rules.json against its schema.json.
/// `--from-markdown <repo> --schema-file <path>` is the offline mode: rules come from a markdown
/// rules repo and the schema from a file, no tenant needed. Thin shell around <see cref="ExtractOperation"/>.
/// </summary>
public static class ExtractCommand
{
    public static int Run(CliArgs cli)
    {
        cli.AllowOnly("tenant", "from-markdown", "schema-file");
        var alias = cli.Require("tenant");
        var store = new JsonStore(cli.DataDirectory);
        ExtractOperation.Run(store, alias, cli.Get("from-markdown"), cli.Get("schema-file"), Console.WriteLine);
        return 0;
    }
}
