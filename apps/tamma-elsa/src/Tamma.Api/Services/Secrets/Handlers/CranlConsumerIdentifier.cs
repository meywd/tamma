namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-8 — parses Cranl consumer-ref identifiers:
/// <c>app=&lt;appId&gt;;env=&lt;VAR_NAME&gt;</c>. Both keys are
/// required; extra keys are tolerated for forward compatibility.
/// </summary>
/// <param name="AppId">Cranl application id.</param>
/// <param name="EnvVarName">Env-var name to rotate.</param>
public sealed record CranlConsumerIdentifier(string AppId, string EnvVarName)
{
    public static CranlConsumerIdentifier Parse(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException(
                "Cranl consumer identifier is empty.", nameof(identifier));

        string? app = null;
        string? env = null;
        foreach (var part in identifier.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim().ToLowerInvariant();
            var value = trimmed[(eq + 1)..].Trim();
            switch (key)
            {
                case "app":
                case "application":
                    app = value;
                    break;
                case "env":
                case "envvar":
                case "name":
                    env = value;
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(app))
            throw new ArgumentException(
                "Cranl consumer identifier must include 'app=<appId>'.",
                nameof(identifier));
        if (string.IsNullOrWhiteSpace(env))
            throw new ArgumentException(
                "Cranl consumer identifier must include 'env=<VAR_NAME>'.",
                nameof(identifier));
        return new CranlConsumerIdentifier(app, env);
    }
}
