namespace RuleHealth.Core;

/// <summary>
/// An expected failure with a message meant for the user (missing consent, bad input, wrong file).
/// The CLI prints the message without a stack trace; anything else is treated as a bug.
/// </summary>
public class RuleHealthException : Exception
{
    public RuleHealthException(string message) : base(message) { }
    public RuleHealthException(string message, Exception inner) : base(message, inner) { }

    /// <summary>The HTTP status when an API call failed, so callers can react to one (retry, fall back).</summary>
    public int? HttpStatus { get; init; }

    /// <summary>
    /// One sentence on what to do, shown next to the error (job `hint`): for a 401/403 the missing
    /// permission or role. Null when the message already says it all.
    /// </summary>
    public string? Hint { get; init; }
}
