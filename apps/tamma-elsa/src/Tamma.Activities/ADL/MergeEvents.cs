namespace Tamma.Activities.ADL;

/// <summary>
/// Story 2-10 (AC5) / Story 4.5 — central catalogue of the <c>MERGE.*</c>,
/// <c>ISSUE.CLOSED.*</c>, and <c>BRANCH.DELETED.*</c> DCB event types emitted by
/// the built-out <c>merge</c> workflow via <see cref="EmitMergeEventActivity"/>.
///
/// <para>The autonomous loop's MERGE step is issue-scoped, so merge events land
/// in the tenant <c>domain_events</c> store (which carries a first-class
/// <c>IssueNumber</c> column). The events are emitted via
/// <c>TammaEventEmitter.Emit</c> into the workflow's <c>tamma:events</c>
/// transient list and persisted <i>durably</i> by the engine event drain
/// (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same pattern
/// <see cref="EmitPrEventActivity"/> / <see cref="EmitBranchEventActivity"/>
/// use. No activity holds a DB / repository dependency of its own (none is
/// registered in the Elsa engine — a directly-injected <c>IEventRepository</c>
/// would be inert and silently drop the event).</para>
///
/// <para>Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c>
/// convention.</para>
///
/// <list type="bullet">
///   <item><description><c>MERGE.SUCCESS</c> — the PR merged (and post-merge
///     close/delete ran). The umbrella merge event.</description></item>
///   <item><description><c>MERGE.FAILED</c> — the merge did NOT happen
///     (not-mergeable / conflict / permission / branch-protected / api error).
///     ALWAYS emitted on the failure edge (loud, error-status) so the loop never
///     reports a silent false success — the headline thin-wrapper bug. The
///     merge-approval gate reads <c>success=false</c> and escalates.</description></item>
///   <item><description><c>MERGE.READINESS.CHECKED</c> — reserved for an explicit
///     pre-merge readiness gate (mergeable / CI / approvals). Currently the
///     mergeable check is folded into the merge activity's idempotency/pre-merge
///     read; this constant is the audit type if a standalone gate is added.</description></item>
///   <item><description><c>ISSUE.CLOSED.SUCCESS</c> / <c>ISSUE.CLOSED.FAILED</c> —
///     the post-merge issue-close sub-action result (surfaced separately so the
///     audit stream shows partial completion, not just the umbrella event).</description></item>
///   <item><description><c>BRANCH.DELETED.SUCCESS</c> / <c>BRANCH.DELETED.FAILED</c> —
///     the post-merge branch-delete sub-action result (best-effort; a failed
///     delete is a warning, not a merge failure).</description></item>
/// </list>
/// </summary>
public static class MergeEvents
{
    public const string Success = "MERGE.SUCCESS";
    public const string Failed = "MERGE.FAILED";
    public const string ReadinessChecked = "MERGE.READINESS.CHECKED";

    public const string IssueClosedSuccess = "ISSUE.CLOSED.SUCCESS";
    public const string IssueClosedFailed = "ISSUE.CLOSED.FAILED";

    public const string BranchDeletedSuccess = "BRANCH.DELETED.SUCCESS";
    public const string BranchDeletedFailed = "BRANCH.DELETED.FAILED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (merge events in single-user mode are platform-scope, TenantId null).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;
}
