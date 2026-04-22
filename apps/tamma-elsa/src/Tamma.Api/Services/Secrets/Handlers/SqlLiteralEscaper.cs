namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-7 — two-layer defence for SQL-literal interpolation:
/// 1) enforces the safe-character regex from
///    <see cref="PostgresPasswordGenerator"/>;
/// 2) doubles any single quote to its escaped form <c>''</c>.
///
/// Postgres's <c>ALTER ROLE ... WITH PASSWORD</c> does not accept
/// parameter placeholders so the password has to be interpolated into
/// the SQL text. The generator already excludes single quotes from its
/// alphabet — this class adds the belt-and-braces escape so the
/// ALTER statement is robust if an operator-supplied password somehow
/// slips past the regex.
/// </summary>
public static class SqlLiteralEscaper
{
    /// <summary>
    /// Validate + escape a candidate literal. Throws when the candidate
    /// contains characters not in the safe set (guards against operator
    /// mis-input or a tampered secret metadata row).
    /// </summary>
    public static string Escape(string literal)
    {
        if (!PostgresPasswordGenerator.IsSafe(literal))
            throw new ArgumentException(
                "Literal contains characters outside the Postgres-safe alphabet. " +
                "Generated passwords must pass PostgresPasswordGenerator.IsSafe.",
                nameof(literal));
        return literal.Replace("'", "''");
    }

    /// <summary>
    /// Escape a Postgres identifier (role / db name) by doubling any
    /// embedded double quote. Caller must wrap the return in
    /// double quotes when emitting SQL.
    /// </summary>
    public static string EscapeIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Identifier must be non-empty.", nameof(identifier));
        return identifier.Replace("\"", "\"\"");
    }
}
