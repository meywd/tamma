namespace Tamma.Activities.ADL;

/// <summary>
/// Story 2-18 (Phases 4 &amp; 5) / Story 2-9 — central catalogue of the
/// <c>REVIEW_FIX.*</c> DCB event types emitted by the <c>review-fix</c> workflow.
///
/// <para>The review-fix phase closes the autonomous loop's review cycle: it
/// fetches a PR's review comments, decides which are actionable, generates code
/// fixes via the mediated <c>llm-call</c> path, applies them, and (follow-on)
/// pushes them back so CI/review can re-run. Every meaningful transition of that
/// phase is an auditable event for the 100%-audit-trail invariant
/// (<c>CLAUDE.md</c> "Event Sourcing (DCB Pattern)").</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same
/// pattern <see cref="EmitPrEventActivity"/> /
/// <see cref="EmitMergeApprovalEventActivity"/> use. No activity holds a DB /
/// repository dependency of its own (none is registered in the Elsa engine — a
/// directly-injected <c>IEventRepository</c> would be inert and silently drop
/// every event).</para>
///
/// <para>Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c>
/// convention.</para>
///
/// <list type="bullet">
///   <item><description><c>REVIEW_FIX.ANALYZED.SUCCESS</c> — review comments were
///     fetched and analyzed (carries actionable / total counts).</description></item>
///   <item><description><c>REVIEW_FIX.ANALYZED.FAILED</c> — fetching / analyzing
///     review comments failed (GitHub error). Loud (error-status) — the workflow
///     routes to its failure terminal, never a silent success.</description></item>
///   <item><description><c>REVIEW_FIX.GENERATED.SUCCESS</c> — the dispatched
///     <c>llm-call</c> returned a fix-generation response
///     (<c>success=true</c>).</description></item>
///   <item><description><c>REVIEW_FIX.GENERATED.FAILED</c> — the dispatched
///     <c>llm-call</c> reported <c>success=false</c>. Loud — the workflow routes
///     to its failure terminal instead of flowing the empty response into "apply"
///     as a false success.</description></item>
///   <item><description><c>REVIEW_FIX.APPLIED.SUCCESS</c> — the generated fix was
///     applied (carries the count of files fixed).</description></item>
///   <item><description><c>REVIEW_FIX.APPLIED.FAILED</c> — applying the fix
///     failed (or the apply activity faulted). Loud — the workflow routes to its
///     failure terminal, never reports <c>fixesApplied=true</c> when nothing was
///     applied.</description></item>
///   <item><description><c>REVIEW_FIX.ESCALATED</c> — the fix loop hit its
///     graph-enforced iteration cap without converging and escalated to a human
///     instead of spinning forever (mirrors the merge-approval test-loop cap).</description></item>
/// </list>
/// </summary>
public static class ReviewFixEvents
{
    public const string AnalyzedSuccess = "REVIEW_FIX.ANALYZED.SUCCESS";
    public const string AnalyzedFailed = "REVIEW_FIX.ANALYZED.FAILED";
    public const string GeneratedSuccess = "REVIEW_FIX.GENERATED.SUCCESS";
    public const string GeneratedFailed = "REVIEW_FIX.GENERATED.FAILED";
    public const string AppliedSuccess = "REVIEW_FIX.APPLIED.SUCCESS";
    public const string AppliedFailed = "REVIEW_FIX.APPLIED.FAILED";
    public const string Escalated = "REVIEW_FIX.ESCALATED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (review-fix events in single-user mode are platform-scope, TenantId null).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// The <c>.FAILED</c> / <c>.ESCALATED</c> transitions are loud (error-status)
    /// audit rows — they are NOT a false success. The <c>.SUCCESS</c> transitions
    /// are normal progress.
    /// </summary>
    public static bool IsFailureEvent(string type)
        => type is AnalyzedFailed or GeneratedFailed or AppliedFailed or Escalated;
}
