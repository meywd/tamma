namespace Tamma.Activities.ADL;

/// <summary>
/// Story 2.4 (AC8) / Story 4.5 AC3 — central catalogue of <c>BRANCH.*</c> DCB
/// event types emitted by the built-out <c>branch-creation</c> workflow via
/// <see cref="EmitBranchEventActivity"/>.
///
/// <para>The branch-creation step is issue-scoped, so the events land in the
/// tenant <c>domain_events</c> store (which carries a first-class
/// <c>IssueNumber</c> column). They are emitted via <c>TammaEventEmitter.Emit</c>
/// into the workflow's <c>tamma:events</c> transient list and persisted durably
/// by the engine event drain (<c>EventPersistenceMiddleware</c>) — see
/// <see cref="EmitBranchEventActivity.BuildTammaEvent"/> for the DCB mapping
/// (tags carry <c>issueId</c>/<c>issueNumber</c>/<c>repository</c>/<c>tenantId</c>;
/// data carries <c>baseBranch</c>/<c>baseSha</c>/<c>finalName</c>/<c>durationMs</c>).</para>
///
/// <list type="bullet">
///   <item><description><c>BRANCH.CREATED.SUCCESS</c> — a branch was created (or
///     an existing branch was reused under the idempotent conflict strategy).</description></item>
///   <item><description><c>BRANCH.CREATED.FAILED</c> — branch creation failed
///     (permission / protected base / missing base / conflict-exhausted / API
///     error). ALWAYS emitted on the failure edge so the loop never reports a
///     silent false success — closes the headline thin-wrapper bug.</description></item>
/// </list>
/// </summary>
public static class BranchEvents
{
    public const string CreatedSuccess = "BRANCH.CREATED.SUCCESS";
    public const string CreatedFailed = "BRANCH.CREATED.FAILED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (branch events in single-user mode are platform-scope, TenantId null).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;
}
