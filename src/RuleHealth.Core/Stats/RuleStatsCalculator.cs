using RuleHealth.Core.Model;

namespace RuleHealth.Core.Stats;

/// <summary>
/// incidents.json + rules.json → rule-stats.json. Pure: no IO, so the rules below are pinned by tests.
///
/// - An incident counts once for every rule that contributed an alert to it; `sharedIncidents` says
///   how many of a rule's incidents also belong to another rule, so overlap is visible, not hidden.
/// - TP/BP/FP rates share the denominator TP+BP+FP (verdicted incidents). Undetermined and open
///   incidents say nothing about precision, so they are left out of it.
/// - undeterminedRate = undetermined / closed: how often the SOC closed without a verdict.
/// - A rate is null when its denominator is 0. Rates are rounded to 4 decimals.
/// - Every rule in rules.json gets a row (a silent rule is a finding), plus every rule id an incident
///   references that is not in rules.json (deleted or renamed rules still produced incidents).
/// </summary>
public static class RuleStatsCalculator
{
    public static RuleStatsDocument Calculate(string tenant, IncidentsDocument incidents, IEnumerable<Rule> rules, DateTime generatedAt)
    {
        var byRule = new Dictionary<string, List<Incident>>(StringComparer.Ordinal);
        foreach (var rule in rules)
            byRule.TryAdd(rule.RuleId, new List<Incident>());
        foreach (var incident in incidents.Incidents)
        {
            foreach (var ruleId in incident.RuleIds.Distinct(StringComparer.Ordinal))
            {
                if (!byRule.TryGetValue(ruleId, out var list))
                    byRule[ruleId] = list = new List<Incident>();
                list.Add(incident);
            }
        }

        return new RuleStatsDocument
        {
            Tenant = tenant,
            GeneratedAt = generatedAt,
            Window = incidents.Window,
            Rules = byRule.Select(pair => ForRule(pair.Key, pair.Value)).ToList(),
        };
    }

    public static RuleStats ForRule(string ruleId, IReadOnlyCollection<Incident> incidents)
    {
        int Count(string classification) => incidents.Count(i => i.Classification == classification);

        var closed = incidents.Count(i => i.Status == IncidentStatuses.Closed);
        int tp = Count(Classifications.TruePositive), bp = Count(Classifications.BenignPositive), fp = Count(Classifications.FalsePositive);
        var undetermined = Count(Classifications.Undetermined);
        var verdicted = tp + bp + fp;

        return new RuleStats
        {
            RuleId = ruleId,
            Incidents = incidents.Count,
            Open = incidents.Count - closed,
            Closed = closed,
            Tp = tp,
            Bp = bp,
            Fp = fp,
            Undetermined = undetermined,
            TpRate = Rate(tp, verdicted),
            BpRate = Rate(bp, verdicted),
            FpRate = Rate(fp, verdicted),
            UndeterminedRate = Rate(undetermined, closed),
            SharedIncidents = incidents.Count(i => i.RuleIds.Distinct(StringComparer.Ordinal).Count() > 1),
            LastIncidentAt = incidents.Count == 0 ? null : incidents.Max(i => i.CreatedAt),
        };
    }

    private static double? Rate(int part, int whole) =>
        // Half-to-even, like the sample generator: 9/32 = 0.28125 → 0.2812.
        whole == 0 ? null : Math.Round((double)part / whole, 4, MidpointRounding.ToEven);
}
