namespace Tamma.Activities.ADL;

/// <summary>
/// Story 2.8 (AC6) / FR-20 — central catalogue of <c>PR.*</c> DCB event types
/// emitted by <see cref="EmitPrEventActivity"/>.
///
/// <para>The autonomous loop's pull-request step is issue-scoped, so PR events
/// land in the tenant <c>domain_events</c> store (which carries a first-class
/// <c>IssueNumber</c> column). The events themselves are emitted via
/// <c>TammaEventEmitter.Emit</c> into the workflow's <c>tamma:events</c>
/// transient list and persisted durably by the engine event drain
/// (<c>EventPersistenceMiddleware</c>) — see
/// <see cref="EmitPrEventActivity.BuildTammaEvent"/> for the DCB mapping
/// (tags carry <c>issueId</c>/<c>issueNumber</c>/<c>repository</c>/<c>prNumber</c>/
/// <c>tenantId</c>; data carries the metrics payload).</para>
///
/// <list type="bullet">
///   <item><description><c>PR.CREATED.SUCCESS</c> — a PR was opened (or an
///     existing open PR for the same head→base was reused / updated).</description></item>
///   <item><description><c>PR.CREATED.FAILED</c> — PR creation failed
///     (permission, conflict, rate-limit, API error). Always emitted on the
///     failure edge so the loop never reports a silent false success.</description></item>
///   <item><description><c>PR.MARKED_READY.SUCCESS</c> — reserved for the
///     parent-driven draft→ready flip (follow-on, not yet wired).</description></item>
/// </list>
/// </summary>
public static class PrEvents
{
    public const string CreatedSuccess = "PR.CREATED.SUCCESS";
    public const string CreatedFailed = "PR.CREATED.FAILED";
    public const string MarkedReadySuccess = "PR.MARKED_READY.SUCCESS";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the
    /// workflow inputs. Returns <c>null</c> for empty / single-user / unparseable
    /// values (PR events in single-user mode are platform-scope, TenantId null).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;
}
