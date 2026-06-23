namespace Tamma.Activities.ADL;

/// <summary>
/// Central catalogue of <c>ISSUE_STATUS.*</c> DCB event types emitted by the
/// built-out <c>update-issue-status</c> workflow via
/// <see cref="EmitIssueStatusEventActivity"/>.
///
/// <para>The status-update step is issue-scoped, so the events land in the
/// tenant <c>domain_events</c> store (which carries a first-class
/// <c>IssueNumber</c> column). They are emitted via
/// <c>TammaEventEmitter.Emit</c> into the workflow's <c>tamma:events</c>
/// transient list and persisted durably by the engine event drain
/// (<c>EventPersistenceMiddleware</c>) — see
/// <see cref="EmitIssueStatusEventActivity.BuildTammaEvent"/> for the DCB
/// mapping (tags carry <c>issueId</c>/<c>issueNumber</c>/<c>repository</c>/
/// <c>tenantId</c>; data carries the operation payload).</para>
///
/// <list type="bullet">
///   <item><description><c>ISSUE_STATUS.UPDATED.SUCCESS</c> — the status
///     comment / label update (and, when supplied, the close transition) was
///     applied.</description></item>
///   <item><description><c>ISSUE_STATUS.UPDATED.FAILED</c> — the update failed
///     after retries (callback error / API failure). ALWAYS emitted on the
///     failure edge so the loop never reports a silent false success — closes
///     the headline swallow-failure bug.</description></item>
/// </list>
/// </summary>
public static class IssueStatusEvents
{
    public const string UpdatedSuccess = "ISSUE_STATUS.UPDATED.SUCCESS";
    public const string UpdatedFailed = "ISSUE_STATUS.UPDATED.FAILED";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the
    /// workflow inputs. Returns <c>null</c> for empty / single-user /
    /// unparseable values (status events in single-user mode are
    /// platform-scope, TenantId null).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;
}
