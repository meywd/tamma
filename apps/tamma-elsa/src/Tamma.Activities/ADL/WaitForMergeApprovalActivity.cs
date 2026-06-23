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
/// Bookmark-based human <b>APPROVAL_GATE</b> (FR-19 / FR-34): suspend the loop
/// until a human decides whether to <i>merge</i>, run more <i>tests</i>, or
/// <i>reject</i> the PR, then surface that decision (+ approver + feedback) as a
/// typed outcome the parent flowchart branches on.
///
/// <para>Resume via the bookmark <c>adl-merge-approval-{issue}-{pr}</c> with the
/// workflow input keys <c>{ decision, feedback, approver }</c> (the documented
/// resume contract; the API surface that injects them is a follow-on — see the
/// build-out report).</para>
///
/// <para>Outcomes (the parent routes each to a distinct edge — no fall-through):
/// <list type="bullet">
///   <item><description><c>Merge</c> — approved to merge.</description></item>
///   <item><description><c>Test</c> — run additional tests before merging.</description></item>
///   <item><description><c>Reject</c> — PR rejected.</description></item>
///   <item><description><c>Invalid</c> — unknown / empty decision. Emitted
///     instead of silently defaulting to "reject" (no-silent-failure rule); the
///     parent routes it to escalation / re-prompt.</description></item>
/// </list></para>
///
/// <para>Events: this is a <see cref="TammaOutcomeActivity"/> with
/// <c>EventType = APPROVAL.GATE</c>, so the engine auto-emits
/// <c>APPROVAL.GATE.STARTED</c> at suspend and <c>APPROVAL.GATE.FAILED</c> on
/// exception. The decision itself is emitted as a <c>MERGE_APPROVAL.DECISION.*</c>
/// DCB event on the resuming edge (see <c>EmitMergeApprovalEventActivity</c> in
/// the workflow graph) so the approver / feedback context is captured durably.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Wait For Merge Approval",
    "Suspend workflow and wait for merge/test/reject decision",
    Kind = ActivityKind.Task
)]
[FlowNode("Merge", "Test", "Reject", "Invalid")]
public class WaitForMergeApprovalActivity : TammaOutcomeActivity
{
    /// <summary>Recognised decisions (case-insensitive). Anything else → <c>Invalid</c>.</summary>
    public const string DecisionMerge = "merge";
    public const string DecisionTest = "test";
    public const string DecisionReject = "reject";

    public override string? EventType => MergeApprovalEvents.GatePrefix;

    [Input(Description = "Issue number for bookmark identification")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "PR number")]
    public Input<int> PrNumber { get; set; } = default!;

    [Input(Description = "PR URL")]
    public Input<string?> PrUrl { get; set; } = default!;

    /// <summary>
    /// Whether the PR carries a breaking change. Threaded into the gate's audit
    /// <c>start</c> data for forward-compatible audit (IMPORTANT-1 / FR-34). The
    /// ENFORCEMENT arm (mandatory-approver policy on breaking changes, FR-34) is
    /// deferred — this only records the signal so it is queryable now and the
    /// enforcement can light up later without an audit-schema change.
    /// </summary>
    [Input(Description = "Whether the PR carries a breaking change (audit signal; enforcement deferred)")]
    public Input<bool> BreakingChange { get; set; } = new(false);

    [Output(Description = "Approval decision (merge|test|reject|invalid)")]
    public Output<string?> Decision { get; set; } = default!;

    [Output(Description = "Feedback from reviewer")]
    public Output<string?> Feedback { get; set; } = default!;

    [Output(Description = "Approver identity captured from the resume payload")]
    public Output<string?> Approver { get; set; } = default!;

    [JsonConstructor]
    public WaitForMergeApprovalActivity() { }

    public WaitForMergeApprovalActivity(ILogger<WaitForMergeApprovalActivity> logger)
    {
        Logger = logger;
    }

    protected override Task RunAsync(ActivityExecutionContext context)
    {
        var issueNumber = IssueNumber.Get(context);
        var prNumber = PrNumber.Get(context);
        var bookmarkName = $"adl-merge-approval-{issueNumber}-{prNumber}";

        Logger?.LogInformation(
            "Creating merge approval bookmark {BookmarkName} for PR #{PrNumber}",
            bookmarkName, prNumber);

        context.CreateBookmark(
            new CreateBookmarkArgs
            {
                BookmarkName = bookmarkName,
                Callback = OnMergeDecisionAsync,
                AutoBurn = true,
                IncludeActivityInstanceId = false,
            });

        return Task.CompletedTask;
    }

    private async ValueTask OnMergeDecisionAsync(ActivityExecutionContext context)
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
            "Merge decision received for PR #{PrNumber}: {Decision} (outcome={Outcome}, approver={Approver})",
            PrNumber.Get(context), decisionStr ?? "<null>", outcome, approver ?? "<none>");

        await context.CompleteActivityWithOutcomesAsync(outcome);
    }

    /// <summary>
    /// Map a raw decision string to a typed outcome + canonical token. Unknown /
    /// empty → <c>(Invalid, "invalid")</c> — NEVER a silent "reject" (the prior
    /// skeleton's bug, violating the no-silent-failure / no-false-result rule).
    /// Pure — exposed for unit testing.
    /// </summary>
    public static (string Outcome, string Normalized) Normalize(string? decision)
        => decision?.Trim().ToLowerInvariant() switch
        {
            DecisionMerge => ("Merge", DecisionMerge),
            DecisionTest => ("Test", DecisionTest),
            DecisionReject => ("Reject", DecisionReject),
            _ => ("Invalid", "invalid"),
        };

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
        ["prNumber"] = PrNumber.Get(context),
        ["prUrl"] = PrUrl.Get(context) ?? "",
        // IMPORTANT-1 — forward-compatible audit signal; enforcement (FR-34) deferred.
        ["breakingChange"] = BreakingChange.Get(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
        ["prNumber"] = PrNumber.Get(context),
        ["decision"] = this.GetOutput<string?>(context, nameof(Decision)) ?? "",
        ["approver"] = this.GetOutput<string?>(context, nameof(Approver)) ?? "",
    };
}
