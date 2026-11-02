using System.Text.RegularExpressions;
using RuleHealth.Core.Extraction;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Storage;

/// <summary>
/// Checks a selections document received from the UI before it is written. Lives in Core so the
/// v2 ASP.NET Core host applies exactly the same rules as `rulehealth serve`.
/// </summary>
public static class SelectionsValidator
{
    private static readonly Regex NoteIdPattern = new("^[A-Za-z0-9-]{1,40}$", RegexOptions.CultureInvariant);

    /// <summary>Returns a list of problems; empty means the document can be saved.</summary>
    public static IReadOnlyList<string> Validate(SelectionsDocument document, string alias)
    {
        var errors = new List<string>();

        if (document.SchemaVersion != Documents.SchemaVersion)
            errors.Add($"schemaVersion must be {Documents.SchemaVersion}.");
        if (document.Tenant != alias)
            errors.Add($"tenant is '{document.Tenant}' but the URL is for '{alias}'.");
        if (document.Decisions is null)
        {
            errors.Add("decisions is missing.");
            return errors;
        }

        foreach (var (checkId, decision) in document.Decisions)
        {
            if (!Documents.IsValidCheckId(checkId))
            {
                errors.Add($"'{checkId}' is not a checkId (16 lowercase hex characters).");
                continue;
            }
            if (decision is null)
            {
                errors.Add($"{checkId}: decision is null.");
                continue;
            }
            if (decision.State != DecisionStates.Validate && decision.State != DecisionStates.Ignore)
                errors.Add($"{checkId}: state must be 'validate' or 'ignore', not '{decision.State}'.");
            if (decision.Check is null)
            {
                errors.Add($"{checkId}: check is missing.");
                continue;
            }
            if (!OperatorClasses.All.Contains(decision.Check.OperatorClass))
                errors.Add($"{checkId}: unknown operatorClass '{decision.Check.OperatorClass}'.");
            // The key must match the copied check, otherwise validate would test a different value
            // than the one the user looked at.
            else if (CheckIdentity.Compute(decision.Check) != checkId)
                errors.Add($"{checkId}: the check fields do not hash to this checkId.");
        }

        foreach (var (ruleId, status) in document.RuleStatuses ?? new Dictionary<string, RuleStatusDecision>())
        {
            if (string.IsNullOrWhiteSpace(ruleId) || ruleId.Length > 512)
            {
                errors.Add("ruleStatuses: a rule id is empty or too long.");
                continue;
            }
            if (status is null)
            {
                errors.Add($"ruleStatuses[{ruleId}]: value is null.");
                continue;
            }
            if (status.State != RuleStatusStates.Ok)
                errors.Add($"ruleStatuses[{ruleId}]: state must be 'ok', not '{status.State}'.");
            if (status.CoveredCheckIds is null || status.CoveredCheckIds.Any(id => !Documents.IsValidCheckId(id)))
                errors.Add($"ruleStatuses[{ruleId}]: coveredCheckIds must be checkIds (16 lowercase hex characters).");
        }

        foreach (var (ruleId, assignment) in document.RuleCategories ?? new Dictionary<string, RuleCategoryAssignment>())
        {
            if (string.IsNullOrWhiteSpace(ruleId) || ruleId.Length > 512)
            {
                errors.Add("ruleCategories: a rule id is empty or too long.");
                continue;
            }
            if (assignment?.Categories is null)
            {
                errors.Add($"ruleCategories[{ruleId}]: categories is missing.");
                continue;
            }
            if (assignment.Categories.Count is 0 or > RuleCategoryAssignment.MaxCategories)
                errors.Add($"ruleCategories[{ruleId}]: needs 1-{RuleCategoryAssignment.MaxCategories} categories (remove the entry instead of leaving it empty).");
            if (assignment.Categories.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > RuleCategoryAssignment.MaxNameLength || c != c.Trim()))
                errors.Add($"ruleCategories[{ruleId}]: a category name must be 1-{RuleCategoryAssignment.MaxNameLength} characters without surrounding spaces.");
            // Case-insensitive: "Missing events" and "missing events" would split one category in the report.
            else if (assignment.Categories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != assignment.Categories.Count)
                errors.Add($"ruleCategories[{ruleId}]: a category is listed twice.");
        }

        foreach (var (ruleId, notes) in document.RuleNotes ?? new Dictionary<string, List<RuleNote>>())
        {
            if (string.IsNullOrWhiteSpace(ruleId) || ruleId.Length > 512)
            {
                errors.Add("ruleNotes: a rule id is empty or too long.");
                continue;
            }
            if (notes is null)
            {
                errors.Add($"ruleNotes[{ruleId}]: value is null.");
                continue;
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var note in notes)
            {
                if (note is null)
                {
                    errors.Add($"ruleNotes[{ruleId}]: a note is null.");
                    continue;
                }
                if (note.Id is null || !NoteIdPattern.IsMatch(note.Id))
                    errors.Add($"ruleNotes[{ruleId}]: note id '{note.Id}' must be 1-40 letters, digits or dashes.");
                else if (!ids.Add(note.Id))
                    errors.Add($"ruleNotes[{ruleId}]: note id '{note.Id}' is used twice.");
                if (string.IsNullOrWhiteSpace(note.Text) || note.Text.Length > RuleNote.MaxTextLength)
                    errors.Add($"ruleNotes[{ruleId}]: note text must be 1-{RuleNote.MaxTextLength} characters.");
                if (note.Author is { Length: > RuleNote.MaxAuthorLength })
                    errors.Add($"ruleNotes[{ruleId}]: author is longer than {RuleNote.MaxAuthorLength} characters.");
            }
        }

        return errors;
    }
}
