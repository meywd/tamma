using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Bookmark-based human <b>PRODUCTION DEPLOY APPROVAL GATE</b> (PRD FR-32 +
/// "Smart Friction" strategic checkpoint before production; Business Mode requires
/// approval — "zero deployments without approval in Business Mode"). Suspends the
/// <c>deployment-pipeline</c> before the production stage until a human decides
/// whether to <i>approve</i> or <i>reject</i> the prod deploy, then surfaces that
/// decision (+ approver + feedback) as a typed outcome the parent flowchart
/// branches on.
///
/// <para>Modelled on <see cref="WaitForMergeApprovalActivity"/>. Resume via the
/// tenant+repo-scoped bookmark
/// <c>adl-deploy-prod-approval-{tenant}-{repo}-{issue}-{mergeSha}</c> (SECURITY —
/// see <see cref="BookmarkName"/>) with the workflow input keys
/// <c>{ decision, feedback, approver }</c>.</para>
///
/// <para>Outcomes (the parent routes each to a distinct edge — no fall-through):
/// <list type="bullet">
///   <item><description><c>Approve</c> — approved to deploy to production.</description></item>
///   <item><description><c>Reject</c> — production deploy rejected.</description></item>
///   <item><description><c>Invalid</c> — unknown / empty decision. Emitted
///     instead of silently defaulting to "approve" (no-silent-failure /
///     fail-closed rule); the parent routes it to the prod-failure terminal, NOT
///     to a deploy.</description></item>
/// </list></para>
///
/// <para>Events: this is a <see cref="TammaOutcomeActivity"/> with
/// <c>EventType = DEPLOY.PRODUCTION.APPROVAL_REQUESTED</c>, so the engine
/// auto-emits <c>DEPLOY.PRODUCTION.APPROVAL_REQUESTED.STARTED</c> at suspend and
/// <c>…FAILED</c> on exception. The decision itself is emitted as a
/// <c>DEPLOY.PRODUCTION.APPROVED</c> / <c>DEPLOY.PRODUCTION.REJECTED</c> DCB event
/// on the resuming edge (see <see cref="EmitDeploymentEventActivity"/> in the
/// workflow graph) so the approver / feedback context is captured durably.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Wait For Deployment Approval",
    "Suspend the deployment pipeline and wait for a production approve/reject decision",
    Kind = ActivityKind.Task
)]
[FlowNode("Approve", "Reject", "Invalid")]
public class WaitForDeploymentApprovalActivity : TammaOutcomeActivity
{
    /// <summary>Recognised decisions (case-insensitive). Anything else → <c>Invalid</c>.</summary>
    public const string DecisionApprove = "approve";
    public const string DecisionReject = "reject";

    public override string? EventType => DeployEvents.ProductionApprovalRequested;

    [Input(Description = "Issue number for bookmark identification")]
    public Input<int> IssueNumber { get; set; } = default!;

    /// <summary>
    /// Merged commit SHA being deployed — folded into the bookmark name so a
    /// re-deploy of a different SHA on the same issue can't resume a stale gate.
    /// </summary>
    [Input(Description = "Merged commit SHA (bookmark scoping — distinguishes re-deploys)")]
    public Input<string?> MergeSha { get; set; } = new((string?)null);

    /// <summary>
    /// Tenant id (the workflow's <c>TenantId</c> variable, a GUID string or empty
    /// for single-user). Folded into the bookmark name so two tenants on the same
    /// issue/SHA can never share — and so collide on — a bookmark. The resume
    /// caller (scoped to ITS tenant) can only target a bookmark whose name carries
    /// its own tenant id.
    /// </summary>
    [Input(Description = "Tenant id (bookmark scoping — prevents cross-tenant collision)")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    /// <summary>
    /// Repository slug (<c>owner/repo</c>). Folded into the bookmark name alongside
    /// the tenant id so the same issue/SHA across different repos can't collide.
    /// </summary>
    [Input(Description = "Repository slug (bookmark scoping — prevents cross-repo collision)")]
    public Input<string?> Repository { get; set; } = new((string?)null);

    [Output(Description = "Approval decision (approve|reject|invalid)")]
    public Output<string?> Decision { get; set; } = default!;

    [Output(Description = "Feedback from approver")]
    public Output<string?> Feedback { get; set; } = default!;

    [Output(Description = "Approver identity captured from the resume payload")]
    public Output<string?> Approver { get; set; } = default!;

    [JsonConstructor]
    public WaitForDeploymentApprovalActivity() { }

    public WaitForDeploymentApprovalActivity(ILogger<WaitForDeploymentApprovalActivity> logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// Canonical, globally-unique bookmark name for a production-deploy approval
    /// gate. Includes <paramref name="tenantId"/> + <paramref name="repository"/>
    /// + <paramref name="mergeSha"/> (in addition to issue) so two tenants — or two
    /// repos, or two distinct merged commits — on the same issue get DISTINCT
    /// bookmarks and can never resume each other's gate. Both the activity (suspend
    /// side) and the engine endpoint (resume side) MUST call this single method so
    /// the names match byte-for-byte. Mirrors
    /// <see cref="WaitForMergeApprovalActivity.BookmarkName"/>.
    /// </summary>
    public static string BookmarkName(string? tenantId, string? repository, int issueNumber, string? mergeSha)
    {
        var tenant = WaitForMergeApprovalActivity.NormalizeSegment(tenantId);
        var repo = WaitForMergeApprovalActivity.NormalizeSegment(repository);
        var sha = WaitForMergeApprovalActivity.NormalizeSegment(mergeSha);
        return $"adl-deploy-prod-approval-{tenant}-{repo}-{issueNumber}-{sha}";
    }

    protected override Task RunAsync(ActivityExecutionContext context)
    {
        var issueNumber = IssueNumber.Get(context);
        var mergeSha = MergeSha.Get(context);
        var tenantId = TenantId.Get(context);
        var repository = Repository.Get(context);
        var bookmarkName = BookmarkName(tenantId, repository, issueNumber, mergeSha);

        Logger?.LogInformation(
            "Creating production-deploy approval bookmark {BookmarkName} for issue #{IssueNumber}",
            bookmarkName, issueNumber);

        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Callback = OnDeployDecisionAsync,
                AutoBurn = true,
                IncludeActivityInstanceId = false,
            });

        return Task.CompletedTask;
    }

    private async ValueTask OnDeployDecisionAsync(ActivityExecutionContext context)
    {
        var input = context.WorkflowInput;

        var decisionStr = input.TryGetValue("decision", out var d) ? d?.ToString() : null;
        var feedback = input.TryGetValue("feedback", out var f) ? f?.ToString() : null;
        var approver = input.TryGetValue("approver", out var a) ? a?.ToString() : null;

        var (outcome, normalized) = Normalize(decisionStr);

        // Surface the NORMALIZED decision (so downstream edges + the emitted DCB
        // event agree on a canonical token), plus the captured approver/feedback.
        Decision.Set(context, normalized);
        Feedback.Set(context, feedback);
        Approver.Set(context, approver);

        Logger?.LogInformation(
            "Production-deploy decision received for issue #{IssueNumber}: {Decision} (outcome={Outcome}, approver={Approver})",
            IssueNumber.Get(context), decisionStr ?? "<null>", outcome, approver ?? "<none>");

        await context.CompleteActivityWithOutcomesAsync(outcome);
    }

    /// <summary>
    /// Map a raw decision string to a typed outcome + canonical token. Unknown /
    /// empty → <c>(Invalid, "invalid")</c> — NEVER a silent "approve" (fail-closed:
    /// an ambiguous decision must NOT promote to production). Pure — exposed for
    /// unit testing.
    /// </summary>
    public static (string Outcome, string Normalized) Normalize(string? decision)
        => decision?.Trim().ToLowerInvariant() switch
        {
            DecisionApprove => ("Approve", DecisionApprove),
            DecisionReject => ("Reject", DecisionReject),
            _ => ("Invalid", "invalid"),
        };

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
        ["mergeSha"] = MergeSha.Get(context) ?? "",
        ["stage"] = "production",
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
        ["decision"] = this.GetOutput<string?>(context, nameof(Decision)) ?? "",
        ["approver"] = this.GetOutput<string?>(context, nameof(Approver)) ?? "",
    };
}
