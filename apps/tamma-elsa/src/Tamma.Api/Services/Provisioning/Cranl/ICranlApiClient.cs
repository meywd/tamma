namespace Tamma.Api.Services.Provisioning.Cranl;

/// <summary>
/// Typed HTTP client for the Cranl REST API
/// (<c>https://app.cranl.com/api</c>). Wraps the endpoints needed for
/// per-tenant provisioning: projects, databases, applications + their
/// lifecycle / environment / domains.
///
/// <para>Out of scope (intentionally): analytics, monitoring, ai-fix,
/// purge-cache, deployment logs, custom domains, project members.</para>
///
/// <para>All methods throw <see cref="CranlApiException"/> on non-success
/// responses with the status code + parsed error body. 429 responses are
/// retried with backoff inside the client (up to 3 attempts) before
/// surfacing.</para>
/// </summary>
public interface ICranlApiClient
{
    // ─── Projects ────────────────────────────────────────────────────────────

    Task<CranlProject> CreateProjectAsync(string name, string organizationId, CancellationToken ct = default);
    Task DeleteProjectAsync(string projectId, CancellationToken ct = default);

    // ─── Databases ───────────────────────────────────────────────────────────

    Task<CranlDatabase> CreateDatabaseAsync(CreateDatabaseRequest req, CancellationToken ct = default);
    Task<CranlDatabase> GetDatabaseAsync(string id, CancellationToken ct = default);
    Task DeleteDatabaseAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Lifecycle action: <c>start</c>, <c>stop</c>, <c>reload</c>,
    /// <c>rebuild</c>, <c>deploy</c>. Cranl uses
    /// <c>POST /api/databases/:id/:action</c> (action in URL, no body).
    /// </summary>
    Task DatabaseLifecycleAsync(string id, string action, CancellationToken ct = default);

    // ─── Applications ────────────────────────────────────────────────────────

    Task<CranlApplication> CreateApplicationAsync(CreateApplicationRequest req, CancellationToken ct = default);
    Task<CranlApplication> GetApplicationAsync(string id, CancellationToken ct = default);
    Task DeleteApplicationAsync(string id, CancellationToken ct = default);

    /// <summary>Trigger a deployment from the configured branch.</summary>
    Task DeployApplicationAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Lifecycle action on an app: <c>start</c>, <c>stop</c>, <c>reload</c>,
    /// <c>rebuild</c>. Cranl uses <c>POST /api/applications/:id/lifecycle</c>
    /// with <c>{ "action": "&lt;action&gt;" }</c>.
    /// </summary>
    Task ApplicationLifecycleAsync(string id, string action, CancellationToken ct = default);

    /// <summary>
    /// Replace the entire env-var set for an application. Body shape is
    /// <c>{ "env": "KEY=VALUE\nKEY2=VALUE2\n..." }</c>. Caller is responsible
    /// for newline-joining and shell-safe quoting (Cranl does not escape
    /// values; passing a value containing a literal newline will split into
    /// two vars).
    /// </summary>
    Task PutEnvironmentAsync(string id, string envText, CancellationToken ct = default);

    Task<CranlAppDomains> GetApplicationDomainsAsync(string id, CancellationToken ct = default);
}
