namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-7 — parses the <c>ConsumerRef.Identifier</c> string used by
/// the postgres rotation handler: <c>role=&lt;rolename&gt;;db=&lt;dbname&gt;</c>.
/// Carriage returns / whitespace around each key=value are tolerated.
/// </summary>
/// <param name="Role">Postgres role name (required).</param>
/// <param name="Db">Target database name; null when the secret doesn't pin one.</param>
public sealed record PostgresConsumerIdentifier(string Role, string? Db)
{
    public static PostgresConsumerIdentifier Parse(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException(
                "Postgres consumer identifier is empty.", nameof(identifier));

        string? role = null;
        string? db = null;
        foreach (var part in identifier.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim().ToLowerInvariant();
            var value = trimmed[(eq + 1)..].Trim();
            switch (key)
            {
                case "role":
                    role = value;
                    break;
                case "db":
                case "database":
                    db = value;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException(
                "Postgres consumer identifier must include 'role=<name>'.",
                nameof(identifier));

        return new PostgresConsumerIdentifier(role, string.IsNullOrWhiteSpace(db) ? null : db);
    }
}
