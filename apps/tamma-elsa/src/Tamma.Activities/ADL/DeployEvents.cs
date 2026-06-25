namespace Tamma.Activities.ADL;

/// <summary>
/// Central catalogue of the <c>DEPLOY.*</c> DCB event types emitted by the
/// built-out <c>deployment-pipeline</c> workflow (step 15 of the autonomous loop
/// — post-merge QA → UAT → Production promotion) via
/// <see cref="EmitDeploymentEventActivity"/>.
///
/// <para>The deploy stage is issue-scoped, so deploy events land in the tenant
/// <c>domain_events</c> store (first-class <c>IssueNumber</c> column). Events are
/// emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same
/// pattern <see cref="EmitMergeEventActivity"/> / <see cref="EmitMergeApprovalEventActivity"/>
/// use. No activity holds a DB / repository dependency of its own (none is
/// registered in the Elsa engine — a directly-injected <c>IEventRepository</c>
/// would be inert and silently drop the event).</para>
///
/// <para>Closes the audit gap (completeness audit item 2): the prior pipeline
/// emitted <b>zero</b> events, breaking the SOC2 / time-travel requirement and
/// the auditor journey (filter by deployment events, reconstruct deploy-time
/// state). Every meaningful edge now emits a typed event.</para>
///
/// <para>Type pattern follows the platform's <c>AGGREGATE.ACTION.STATUS</c>
/// convention.</para>
/// </summary>
public static class DeployEvents
{
    /// <summary>A deploy stage (qa/uat/production) began dispatching.</summary>
    public const string StageStarted = "DEPLOY.STAGE.STARTED";

    /// <summary>A deploy stage reported success and was promoted.</summary>
    public const string StageSuccess = "DEPLOY.STAGE.SUCCESS";

    /// <summary>
    /// A deploy stage failed — missing / unparseable / empty result, a dispatch
    /// error, or an explicit <c>status:"failed"</c>. Loud (error-status) so the
    /// loop never reports a silent false success (completeness audit item 1).
    /// </summary>
    public const string StageFailed = "DEPLOY.STAGE.FAILED";

    /// <summary>The whole pipeline reached the success terminal (all stages OK).</summary>
    public const string PipelineSuccess = "DEPLOY.PIPELINE.SUCCESS";

    /// <summary>The pipeline terminated on a failed stage. Loud (error-status).</summary>
    public const string PipelineFailed = "DEPLOY.PIPELINE.FAILED";

    /// <summary>A human production-approval gate suspended on its bookmark.</summary>
    public const string ProductionApprovalRequested = "DEPLOY.PRODUCTION.APPROVAL_REQUESTED";

    /// <summary>A human approved the production deploy at the gate.</summary>
    public const string ProductionApproved = "DEPLOY.PRODUCTION.APPROVED";

    /// <summary>
    /// A human rejected the production deploy (or the gate received an invalid
    /// decision). Loud (error-status) — never a silent promote.
    /// </summary>
    public const string ProductionRejected = "DEPLOY.PRODUCTION.REJECTED";

    /// <summary>A production rollback began (revert to the previous release).</summary>
    public const string RollbackStarted = "DEPLOY.ROLLBACK.STARTED";

    /// <summary>A production rollback completed.</summary>
    public const string RollbackSuccess = "DEPLOY.ROLLBACK.SUCCESS";

    /// <summary>
    /// A production rollback itself failed — a critical operator-attention event.
    /// Loud (error-status).
    /// </summary>
    public const string RollbackFailed = "DEPLOY.ROLLBACK.FAILED";

    /// <summary>
    /// A <c>*.FAILED</c> / <c>*.REJECTED</c> deploy event carries an
    /// <c>error</c> status; everything else (<c>*.SUCCESS</c> /
    /// <c>*.STARTED</c> / <c>*.APPROVED</c> / <c>*.APPROVAL_REQUESTED</c>) is
    /// <c>success</c>. A failed/rejected deploy is a loud audit row, NOT a false
    /// success (no-silent-failure rule).
    /// </summary>
    public static bool IsFailureType(string type)
        => type is StageFailed or PipelineFailed or ProductionRejected or RollbackFailed;

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (deploy events in single-user mode are platform-scope, TenantId null).
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;
}
