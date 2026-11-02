using System.Security.Cryptography;
using System.Text;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Extraction;

/// <summary>
/// checkId = first 16 hex chars of SHA-256 over table, column, path, operatorClass and value joined
/// with U+001F (unit separator), UTF-8, exact case. Decisions in selections.json are keyed by it, so
/// the formula must never change without a migration.
/// </summary>
public static class CheckIdentity
{
    private const char Separator = '\u001f';

    public static string Compute(string table, string column, string? path, string operatorClass, string value)
    {
        var text = string.Join(Separator, table, column, path ?? "", operatorClass, value);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static string Compute(CheckKey key) =>
        Compute(key.Table, key.Column, key.Path, key.OperatorClass, key.Value);

    public static string Compute(ValueCheck check) =>
        Compute(check.Table, check.Column, check.Path, check.OperatorClass, check.Value);
}
