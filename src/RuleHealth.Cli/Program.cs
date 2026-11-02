using RuleHealth.Cli;
using RuleHealth.Cli.Commands;
using RuleHealth.Core;

// Entry point: parse the verb, dispatch to a command, map exceptions to exit codes.
// Expected failures (RuleHealthException) print a message only; anything else prints the stack.
try
{
    var cli = CliArgs.Parse(args, AppSettings.FromEnvironment());
    var verb = cli.Positionals.Count > 0 ? cli.Positionals[0] : null;

    if (cli.Has("version") && verb is null)
    {
        Console.WriteLine(AppInfo.Version);
        return 0;
    }

    if (verb is null || verb is "help" || (cli.Has("help") && cli.Positionals.Count == 0))
    {
        Console.WriteLine(Usage.Text);
        return verb is null ? 1 : 0;
    }

    return verb switch
    {
        "serve" => await ServeCommand.RunAsync(cli),
        "health" => await HealthCommand.RunAsync(cli),
        "extract" => ExtractCommand.Run(cli),
        "validate" => ValidateCommand.Run(cli),
        "schema" => SchemaCommand.Run(cli),
        // Sign-in is a browser redirect to the running server, so these live in the UI now.
        "tenant" or "collect" => throw new RuleHealthException(
            $"`{verb}` moved to the UI: run `rulehealth serve`, open http://localhost:5180 and use Add tenant / Update from tenant."),
        _ => throw new RuleHealthException($"Unknown command '{verb}'. Run `rulehealth help`."),
    };
}
catch (RuleHealthException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"unexpected error: {ex}");
    return 1;
}

internal static class Usage
{
    public const string Text = """
        rulehealth — Detection Rule Health: detection rule efficacy and value checks

        Usage:
          rulehealth serve [--data ./data] [--ui <dir>] [--samples <dir>] [--open]
          rulehealth health
          rulehealth --version
          rulehealth extract --tenant <alias> [--from-markdown <repo> --schema-file <path>]
          rulehealth validate --tenant <alias> --dry-run [--days 30] [--include-basic]
          rulehealth schema export --tenant <alias> --out <file> [--include-custom]

        Adding a tenant, signing in, collecting and validating happen in the UI (rulehealth serve).

        Global options:
          --data <dir>   tenant data folder (default RULEHEALTH_DATA, else ./data)
          --verbose      log HTTP calls (never tokens)

        Environment:
          RULEHEALTH_DATA, RULEHEALTH_LISTEN (default http://localhost:5180/), RULEHEALTH_PUBLIC_URL,
          RULEHEALTH_UPDATE_CHECK (true|false), RULEHEALTH_SAMPLES, RULEHEALTH_UI, RULEHEALTH_LOG_LEVEL (info|debug)
        """;
}
