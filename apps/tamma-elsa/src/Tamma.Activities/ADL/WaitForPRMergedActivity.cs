using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Bookmark-based activity that blocks until a PR is merged — OR a durable SLA deadline
/// elapses, whichever happens first. Resumed by a merged-PR webhook (GitHub
/// <c>pull_request.closed(merged=true)</c>, Gitea/Forgejo equivalent, GitLab
/// <c>merge_request action=merge</c>) via the engine-side <c>PrMergedResumeEndpoint</c>
/// → <c>Merged</c>; or by the durable SLA timer → <c>TimedOut</c>.
///
/// <para><b>Epic 31 P4 M2 (DG-6) — bookmark naming.</b> The bookmark now carries a
/// tenant + repo qualifier (<see cref="BookmarkName"/>:
/// <c>pr-merged-{tenant}-{repo}-{n}</c>, the <c>WaitForMergeApprovalActivity</c>
/// SECURITY C2 convention) so two tenants — or two repos — with the same PR number
/// can never resume each other's wait. Rollout-safe: NEW suspensions register only
/// the qualified name; the resume endpoint matches BOTH the qualified name and the
/// legacy <c>pr-merged-{n}</c> (<see cref="LegacyBookmarkName"/>) during the
/// transition so instances suspended before this deploy stay resumable.</para>
///
/// <para><b>Durable merge SLA timeout (SingleIssueCycle.md §Missing #6 — the tracked
/// follow-up; landed 2026-07-02).</b> The merge is approved + performed synchronously inside
/// the merge-approval gate, so the <c>pr-merged-{pr}</c> webhook should arrive within
/// moments. But if that webhook is never delivered (a lost/failed delivery, or a merge that
/// silently didn't complete) the cycle would otherwise wait on the bookmark forever with no
/// escalation. Two resume paths are now armed when the activity suspends:</para>
/// <list type="number">
///   <item><description>the merge bookmark (<c>pr-merged-{pr}</c>) resumed by the GitHub
///     webhook → <c>Merged</c> outcome (the unchanged happy path); and</description></item>
///   <item><description>a DURABLE delay bookmark via
///     <see cref="Elsa.Extensions.DelayActivityExecutionContextExtensions.DelayFor(ActivityExecutionContext, System.TimeSpan, ExecuteActivityDelegate)"/>
///     at the SLA (<c>Adl:PrMergeTimeoutMinutes</c>, default 720 = 12h) → <c>TimedOut</c>
///     outcome, on which the parent cycle escalates to a human handoff instead of hanging.</description></item>
/// </list>
/// <para>The Delay bookmark is EF-persisted and re-armed by <c>Elsa.Scheduling</c>'s startup
/// task on rehydration (mirrors <see cref="Tamma.Activities.Blocker.EscalateToSeniorActivity"/>
/// and <see cref="Tamma.Activities.Testing.WaitForCIResultsActivity"/>), so a host restart mid-wait
/// does NOT drop the SLA. Whichever path resumes first completes the activity; Elsa burns the
/// remaining bookmark, so there is no orphaned timer / stale double-resume.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Wait for PR Merged",
    "Block until the pull request is merged, or escalate when the merge SLA elapses",
    Kind = ActivityKind.Task
)]
[FlowNode("Merged", "TimedOut")]
public class WaitForPRMergedActivity : TammaOutcomeActivity
{
    /// <summary>
    /// Default merge-webhook SLA (minutes) when <c>Adl:PrMergeTimeoutMinutes</c> is unset.
    /// 12h — generous enough to absorb a delayed / retried webhook delivery, short enough
    /// that an unattended loop never stalls indefinitely on a lost merge webhook.
    /// </summary>
    public const int DefaultTimeoutMinutes = 720;

    private readonly IConfiguration? _configuration;
    private readonly PendingPrMergeBuffer? _pendingMerges;

    public override string? EventType => "CYCLE.PR.MERGE.WAIT";

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "PR number")]
    public Input<int> PRNumber { get; set; } = default!;

    /// <summary>
    /// Epic 31 P4 M2 — owning tenant, folded into the bookmark name so the
    /// resume seam is tenant-scoped. Optional: single-user mode leaves it
    /// empty and the name carries the stable "none" segment.
    /// </summary>
    [Input(Description = "Owning tenant id (folded into the bookmark name; empty in single-user mode)")]
    public Input<string?> TenantId { get; set; } = default!;

    [Output(Description = "Merge commit SHA")]
    public Output<string?> MergeSha { get; set; } = default!;

    [JsonConstructor]
    public WaitForPRMergedActivity() { }

    public WaitForPRMergedActivity(
        ILogger<WaitForPRMergedActivity> logger,
        IConfiguration configuration,
        PendingPrMergeBuffer? pendingMerges = null)
    {
        Logger = logger;
        _configuration = configuration;
        _pendingMerges = pendingMerges;
    }

    /// <summary>
    /// Canonical tenant+repo-qualified bookmark name (Epic 31 P4 M2). BOTH the
    /// suspend side (this activity) and the resume side
    /// (<c>PrMergedResumeEndpoint</c>) MUST call this single builder so the
    /// names match byte-for-byte. Segments are normalized via
    /// <see cref="WaitForMergeApprovalActivity.NormalizeSegment"/> — the same
    /// transform the merge-approval gate uses.
    /// </summary>
    public static string BookmarkName(string? tenantId, string? repository, int prNumber)
    {
        var tenant = WaitForMergeApprovalActivity.NormalizeSegment(tenantId);
        var repo = WaitForMergeApprovalActivity.NormalizeSegment(repository);
        return $"pr-merged-{tenant}-{repo}-{prNumber}";
    }

    /// <summary>
    /// Pre-P4 bookmark name. NEW suspensions never register it; the resume
    /// endpoint still matches it so instances suspended before the deploy stay
    /// resumable during the transition window. Delete once no pre-P4 instance
    /// can still be suspended (12h SLA bounds that window).
    /// </summary>
    public static string LegacyBookmarkName(int prNumber) => $"pr-merged-{prNumber}";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var prNumber = PRNumber.Get(context);
        var tenantId = TenantId.GetOrDefault(context);
        var repository = Repository.GetOrDefault(context);

        // 0) RECONCILE-ON-REGISTER (2026-08-13, engine-driven E2E run 39): when
        //    Tamma performs the merge ITSELF, the platform's merged-PR webhook
        //    fires immediately — observed 1 s BEFORE this activity registered
        //    its bookmark, so the once-only forward 404'd and the merged cycle
        //    sat on the 12 h SLA. The resume endpoint buffers such early
        //    notifications keyed by this exact bookmark name; consume it here
        //    and short-circuit to Merged instead of suspending.
        var buffer = _pendingMerges ?? context.GetService<PendingPrMergeBuffer>();
        if (buffer is not null
            && buffer.TryConsume(BookmarkName(tenantId, repository, prNumber), out var bufferedSha))
        {
            MergeSha.Set(context, bufferedSha);
            Logger?.LogInformation(
                "PR #{PRNumber} merge notification arrived BEFORE the wait registered — " +
                "consumed from the reconcile buffer (sha: {MergeSha}); completing Merged without suspending",
                prNumber, bufferedSha ?? "unknown");
            await context.CompleteActivityWithOutcomesAsync("Merged");
            return;
        }

        // 1) Merge bookmark — resumed by the merged-PR webhook through the
        //    engine-side PrMergedResumeEndpoint (tenant+repo-qualified name).
        context.CreateBookmark(new CreateBookmarkArgs
        {
            Callback = OnMerged,
            BookmarkName = BookmarkName(tenantId, repository, prNumber),
            IncludeActivityInstanceId = false,
        });

        // 2) Durable merge SLA — a DelayFor (Delay) bookmark that Elsa.Scheduling's startup
        //    background task RE-ARMS after a host restart (EF-persisted, not an in-memory
        //    timer). A never-delivered merge webhook now escalates to a real TimedOut terminal
        //    even across a VPS restart inside the (default 12h) SLA window instead of hanging.
        var slaMinutes = _configuration?.GetValue<int?>("Adl:PrMergeTimeoutMinutes") ?? DefaultTimeoutMinutes;
        slaMinutes = Math.Max(1, slaMinutes);
        context.DelayFor(TimeSpan.FromMinutes(slaMinutes), OnTimeoutAsync);

        Logger?.LogInformation(
            "Waiting for PR #{PRNumber} to be merged; durable SLA timeout armed at +{SlaMinutes}min",
            prNumber, slaMinutes);
    }

    /// <summary>
    /// Merge webhook resume path (happy path): the PR was merged. Elsa burns the still-armed
    /// SLA Delay bookmark on completion (no orphaned timer).
    /// </summary>
    private async ValueTask OnMerged(ActivityExecutionContext context)
    {
        var sha = context.WorkflowInput.GetValueOrDefault("mergeSha")?.ToString();
        MergeSha.Set(context, sha);

        Logger?.LogInformation("PR #{PRNumber} merged. SHA: {MergeSha}",
            PRNumber.Get(context), sha ?? "unknown");

        await context.CompleteActivityWithOutcomesAsync("Merged");
    }

    /// <summary>
    /// Durable timeout path: the merge SLA elapsed with no merge webhook. The Delay bookmark
    /// scheduler resumes the activity here (and re-arms across a host restart). Takes the
    /// deterministic <c>TimedOut</c> edge so the cycle escalates to a human handoff instead of
    /// suspending forever; the still-armed merge bookmark is burned on completion.
    /// </summary>
    private async ValueTask OnTimeoutAsync(ActivityExecutionContext context)
    {
        MergeSha.Set(context, (string?)null);

        Logger?.LogWarning(
            "Merge SLA expired (durable timeout) for PR #{PRNumber} — taking the TimedOut edge",
            PRNumber.Get(context));

        await context.CompleteActivityWithOutcomesAsync("TimedOut");
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["prNumber"] = PRNumber.Get(context),
        ["mergeSha"] = this.GetOutput<string?>(context, nameof(MergeSha)),
    };
}
