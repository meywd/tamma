using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Tamma.Api.Services.TenantStatus;

/// <summary>
/// Story 28-8 AC2 — central translation from <c>tenants.Status</c>
/// values to HTTP responses. Used by <c>TenantContextMiddleware</c>
/// and <c>ApiKeyAuthHandler</c> (tenant-scoped key path) so a status
/// flip surfaces the same shape regardless of which authentication
/// surface the caller hit.
///
/// <para>Mapping (Doc 04 §8.1 + Doc 03 §6.1, verified 2026-05-30
/// against Story 28-8 AC2 by audit-residual closure):</para>
/// <list type="table">
///   <listheader>
///     <term>Status</term>
///     <description>HTTP code + payload</description>
///   </listheader>
///   <item>
///     <term><c>active</c> / <c>null</c> (legacy)</term>
///     <description>n/a — caller proceeds.</description>
///   </item>
///   <item>
///     <term><c>pending_verification</c></term>
///     <description>503 + <c>tenant_not_ready</c> + <c>Retry-After: 60</c>.</description>
///   </item>
///   <item>
///     <term><c>provisioning</c></term>
///     <description>503 + <c>tenant_not_ready</c> + <c>Retry-After: 5</c>.</description>
///   </item>
///   <item>
///     <term><c>failed</c></term>
///     <description>424 Failed Dependency + <c>tenant_provisioning_failed</c>
///     (Retry-After deliberately absent so the client stops polling).</description>
///   </item>
///   <item>
///     <term><c>suspended</c></term>
///     <description>402 Payment Required + <c>tenant_suspended</c>. Plan
///     / billing remediation; not retryable on its own.</description>
///   </item>
///   <item>
///     <term><c>delete_requested</c> (grace expired) / <c>dropping</c> /
///       <c>deleting</c></term>
///     <description>503 + <c>tenant_deleting</c> + <c>Retry-After: 0</c>.
///     Doc 04 §8.1 footnote — client should NOT retry, the data plane
///     is being torn down. (Caller is responsible for short-circuiting
///     <c>delete_requested</c> with grace-not-expired as
///     "pass through" per AC2; this evaluator only handles the
///     terminal branch.)</description>
///   </item>
///   <item>
///     <term><c>draining</c></term>
///     <description>Phase 4 (unified tenancy) read-only window — a tenant
///     move is in progress. Safe verbs (GET/HEAD/OPTIONS) pass through
///     (see <see cref="AllowsRequest"/>); mutating verbs get 503 +
///     <c>tenant_read_only</c> + <c>Retry-After: 5</c> so clients retry
///     once the move lands.</description>
///   </item>
///   <item>
///     <term><c>deleted</c></term>
///     <description>410 Gone + <c>tenant_deleted</c>.</description>
///   </item>
///   <item>
///     <term>(any other / not found)</term>
///     <description>404 Not Found + <c>tenant_not_found</c>.</description>
///   </item>
/// </list>
/// </summary>
public static class TenantStatusEvaluator
{
    public const string StatusActive = "active";
    public const string StatusPendingVerification = "pending_verification";
    public const string StatusProvisioning = "provisioning";
    public const string StatusFailed = "failed";
    public const string StatusSuspended = "suspended";
    public const string StatusDeleteRequested = "delete_requested";
    public const string StatusDropping = "dropping";
    public const string StatusDeleting = "deleting";
    public const string StatusDeleted = "deleted";

    /// <summary>
    /// Phase 4 (unified tenancy) — the tenant is mid-move to another pool
    /// database and is serving a brief READ-ONLY window (parent plan
    /// decision 4). Not a teardown state: clients should retry mutations
    /// after <c>Retry-After</c>.
    /// </summary>
    public const string StatusDraining = "draining";

    /// <summary>
    /// Returns true when <paramref name="status"/> permits the request to
    /// continue. <c>null</c> and <c>active</c> both qualify (null treats
    /// legacy rows as active per Doc 04 §2.2).
    /// </summary>
    public static bool IsActive(string? status) =>
        status is null
        || string.Equals(status, StatusActive, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when <paramref name="status"/> is the Phase 4
    /// read-only window (<c>draining</c>) — safe verbs may proceed,
    /// mutating verbs must be 503'd via
    /// <see cref="WriteNonActiveResponseAsync"/>.
    /// </summary>
    public static bool IsReadOnly(string? status) =>
        string.Equals(status, StatusDraining, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// RFC 9110 §9.2.1 safe methods that a read-only tenant may still
    /// serve: GET, HEAD, OPTIONS.
    /// </summary>
    public static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method)
        || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method);

    /// <summary>
    /// Verb-aware gate: <c>active</c>/<c>null</c> allow every verb;
    /// <c>draining</c> allows only <see cref="IsSafeMethod">safe</see>
    /// verbs; every other status blocks regardless of verb. Callers that
    /// get <c>false</c> must write
    /// <see cref="WriteNonActiveResponseAsync"/> and short-circuit.
    /// </summary>
    public static bool AllowsRequest(string? status, string method) =>
        IsActive(status)
        || (IsReadOnly(status) && IsSafeMethod(method));

    /// <summary>
    /// Writes the HTTP response for a non-active status. The caller MUST
    /// short-circuit the pipeline after this returns. Idempotent against
    /// <see cref="HttpResponse.HasStarted"/> — bails silently if the
    /// response has already been committed (caller already logged).
    /// </summary>
    public static async Task WriteNonActiveResponseAsync(
        HttpContext context,
        Guid tenantId,
        string? status,
        CancellationToken cancellationToken = default)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.ContentType = "application/json; charset=utf-8";

        switch ((status ?? string.Empty).ToLowerInvariant())
        {
            case StatusPendingVerification:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers["Retry-After"] = "60";
                await WriteJsonAsync(context, new
                {
                    error = "tenant_not_ready",
                    status = StatusPendingVerification,
                    retryAfter = 60,
                    action = "verify email",
                }, cancellationToken).ConfigureAwait(false);
                return;

            case StatusProvisioning:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers["Retry-After"] = "5";
                await WriteJsonAsync(context, new
                {
                    error = "tenant_not_ready",
                    status = StatusProvisioning,
                    retryAfter = 5,
                    progressUrl = $"/api/v1/tenants/{tenantId:D}/provisioning-status",
                }, cancellationToken).ConfigureAwait(false);
                return;

            case StatusFailed:
                // 424 Failed Dependency. lastError omitted at this layer
                // — the public progress endpoint surfaces the sanitized
                // failure summary so the middleware doesn't have to read
                // the row a second time.
                context.Response.StatusCode = StatusCodes.Status424FailedDependency;
                await WriteJsonAsync(context, new
                {
                    error = "tenant_provisioning_failed",
                    status = StatusFailed,
                    retryUrl = $"/api/v1/tenants/{tenantId:D}/provisioning-status",
                }, cancellationToken).ConfigureAwait(false);
                return;

            case StatusSuspended:
                // 402 Payment Required — plan downgraded / billing failed.
                // Doc 04 §8.1 + Story 28-8 AC2. Not retryable; the
                // tenant_owner must remediate via the billing portal.
                context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                await WriteJsonAsync(context, new
                {
                    error = "tenant_suspended",
                    status = StatusSuspended,
                }, cancellationToken).ConfigureAwait(false);
                return;

            case StatusDeleteRequested:
            case StatusDropping:
            case StatusDeleting:
                // Doc 04 §8.1 footnote — `delete_requested` (grace
                // expired), `dropping`, and `deleting` are all terminal
                // teardown states from the client's perspective. 503 +
                // Retry-After:0 signals "we're going away; don't poll".
                // (`delete_requested` with grace NOT expired must be
                // short-circuited by the caller as pass-through before
                // reaching this evaluator — AC2.)
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers["Retry-After"] = "0";
                await WriteJsonAsync(context, new
                {
                    error = "tenant_deleting",
                    status = (status ?? string.Empty).ToLowerInvariant(),
                }, cancellationToken).ConfigureAwait(false);
                return;

            case StatusDraining:
                // Phase 4 read-only window — a mutating verb reached the
                // writer while a tenant move is in progress. 503 +
                // Retry-After: 5 (the window is brief by design); the body
                // names the move so this is distinguishable from
                // teardown (`tenant_deleting`) and provisioning
                // (`tenant_not_ready`).
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers["Retry-After"] = "5";
                await WriteJsonAsync(context, new
                {
                    error = "tenant_read_only",
                    status = StatusDraining,
                    retryAfter = 5,
                    message = "tenant move in progress — writes are "
                        + "temporarily unavailable; retry shortly",
                }, cancellationToken).ConfigureAwait(false);
                return;

            case StatusDeleted:
                context.Response.StatusCode = StatusCodes.Status410Gone;
                await WriteJsonAsync(context, new
                {
                    error = "tenant_deleted",
                    status = StatusDeleted,
                }, cancellationToken).ConfigureAwait(false);
                return;

            default:
                // Unknown / not-found / corrupt status — 404 so a stale
                // JWT pointing at a vanished tenant gets a re-login signal.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(context, new
                {
                    error = "tenant_not_found",
                }, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Convenience overload for the not-found case (no row in CP at all).
    /// </summary>
    public static Task WriteNotFoundResponseAsync(
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        WriteNonActiveResponseAsync(context, Guid.Empty, status: "not_found", cancellationToken);

    private static async Task WriteJsonAsync(
        HttpContext context,
        object payload,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await context.Response.Body
            .WriteAsync(bytes.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }
}
