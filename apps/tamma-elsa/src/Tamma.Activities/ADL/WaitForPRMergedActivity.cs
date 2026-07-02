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
/// elapses, whichever happens first. Resumed by a GitHub webhook (pull_request.closed with
/// merged=true) → <c>Merged</c>; or by the durable SLA timer → <c>TimedOut</c>.
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

    public override string? EventType => "CYCLE.PR.MERGE.WAIT";

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "PR number")]
    public Input<int> PRNumber { get; set; } = default!;

    [Output(Description = "Merge commit SHA")]
    public Output<string?> MergeSha { get; set; } = default!;

    [JsonConstructor]
    public WaitForPRMergedActivity() { }

    public WaitForPRMergedActivity(ILogger<WaitForPRMergedActivity> logger, IConfiguration configuration)
    {
        Logger = logger;
        _configuration = configuration;
    }

    protected override Task RunAsync(ActivityExecutionContext context)
    {
        var prNumber = PRNumber.Get(context);

        // 1) Merge bookmark — resumed by the GitHub pull_request.closed(merged=true) webhook.
        context.CreateBookmark(new CreateBookmarkArgs
        {
            Callback = OnMerged,
            BookmarkName = $"pr-merged-{prNumber}",
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

        return Task.CompletedTask;
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
        MergeSha.Set(context, null);

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
