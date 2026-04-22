namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Typed reference to a downstream consumer of a secret per Story 29-1
/// AC8. The pair <c>(System, Identifier)</c> is opaque at the storage
/// layer — interpretation lives in <see cref="ConsumerRefLookup"/>,
/// which renders human labels like <c>"Tamma API
/// (TammaAppDbContext)"</c> in the admin UI rather than raw
/// <c>"postgres" / "role=tamma_app"</c> strings.
///
/// <para>Examples:</para>
/// <list type="bullet">
///   <item><description><c>{ System = "postgres",
///     Identifier = "role=tamma_app" }</c> — a Postgres role
///     consuming a DB-credential secret.</description></item>
///   <item><description><c>{ System = "cranl",
///     Identifier = "app_id=app_xyz" }</c> — a Cranl application
///     consuming an env-var secret.</description></item>
///   <item><description><c>{ System = "github-webhook",
///     Identifier = "owner=acme,repo=api" }</c> — a GitHub webhook
///     consuming a verification secret.</description></item>
/// </list>
///
/// <para>Cross-tenant linking is intentionally out of scope (research
/// notes §6); <c>ConsumerRef</c> is interpreted within the secret's
/// own scope (Story 29-1 open question Q4).</para>
/// </summary>
/// <param name="System">Stable system key. Lower-kebab-case.</param>
/// <param name="Identifier">System-specific identifier. Free-form;
/// rendered verbatim by the lookup if no parser is registered.</param>
public sealed record ConsumerRef(string System, string Identifier)
{
    public ConsumerRef WithIdentifier(string identifier) =>
        this with { Identifier = identifier };
}
