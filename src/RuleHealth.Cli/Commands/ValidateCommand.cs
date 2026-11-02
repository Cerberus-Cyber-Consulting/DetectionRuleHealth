using RuleHealth.Core;
using RuleHealth.Core.Model;
using RuleHealth.Core.Operations;
using RuleHealth.Core.Storage;
using RuleHealth.Core.Validation;

namespace RuleHealth.Cli.Commands;

/// <summary>
/// `rulehealth validate --tenant contoso --dry-run [--days 30] [--include-basic]`
/// Prints the routing and the KQL per table for every check marked "validate", and writes nothing.
/// Needs no sign-in. Running the queries is a job in the UI: the sign-in lives in the serve process.
/// </summary>
public static class ValidateCommand
{
    public static int Run(CliArgs cli)
    {
        cli.AllowOnly("tenant", "days", "dry-run", "include-basic");
        if (!cli.Has("dry-run"))
            throw new RuleHealthException(
                "validate needs a sign-in, which only the UI has: use Validate in the Data view. " +
                "`rulehealth validate --tenant <alias> --dry-run` shows the queries without sending them.");
        var alias = cli.Require("tenant");
        var store = new JsonStore(cli.DataDirectory);
        int? days = cli.Get("days") is null ? null : cli.GetInt("days", 0);
        var prepared = ValidateOperation.Prepare(store, alias, new ValidateOptions(days, cli.Has("include-basic")));

        foreach (var note in prepared.Notes)
            Console.WriteLine("note: " + note);
        Console.WriteLine(ValidateOperation.Describe(prepared));
        PrintDecided(prepared.Plan);
        foreach (var batch in prepared.Plan.Batches)
        {
            Console.WriteLine();
            Console.WriteLine($"// {batch.Table}: {batch.Checks.Count} checks via {batch.Target.Describe()}, timespan P{prepared.Plan.Days}D");
            Console.WriteLine(batch.Kql(prepared.Plan.Days));
        }
        Console.WriteLine();
        Console.WriteLine("dry run: nothing was sent and nothing was written.");
        return 0;
    }

    private static void PrintDecided(ValidationPlan plan)
    {
        // Group the reasons so 40 Basic-table checks print as one line.
        foreach (var group in plan.Decided.GroupBy(r => (r.Verdict, Reason: r.Error ?? "")).OrderByDescending(g => g.Count()))
        {
            var example = group.First();
            Console.WriteLine($"  {group.Count()} {group.Key.Verdict}: {example.Error}" +
                              (group.Count() > 1 ? $" (e.g. {example.Table}.{example.Column} {example.OperatorClass} \"{example.Value}\")" : ""));
        }
    }
}
