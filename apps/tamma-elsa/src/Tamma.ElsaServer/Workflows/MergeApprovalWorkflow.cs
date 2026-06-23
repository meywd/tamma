using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Merge Approval — the human <b>APPROVAL_GATE</b> of the 14-step loop
/// (<c>docs/architecture.md</c> line 840, between CI_CHECK and MERGE). A
/// completed PR suspends on a bookmark until a human decides
/// <i>merge / test / reject</i>; the gate then <b>acts</b> on that decision
/// (dispatching the real <c>merge</c> / <c>testing</c> workflows or rejecting),
/// rather than emitting a bare decision string.
///
/// <para>Build-out (FR-19 / FR-34 / Story 4-6): the activity's 3-way (now 4-way)
/// outcome is branched with typed <see cref="FlowEndpoint"/> edges — every
/// outcome routes to a distinct, explicit path and no edge falls through to
/// success. An unknown / empty decision is an explicit <c>Invalid</c> outcome
/// (NOT a silent "reject") that escalates. Each decision edge emits a
/// <c>MERGE_APPROVAL.*</c> / <c>MERGE.*</c> DCB event via
/// <see cref="EmitMergeApprovalEventActivity"/> through the durable engine event
/// drain (the gate activity itself, a <c>TammaOutcomeActivity</c>, also auto-emits
/// <c>APPROVAL.GATE.STARTED/.FAILED</c>).</para>
///
/// Flow:
///   ReadInputs → WaitMergeApproval (bookmark)
///     ├─ Merge   → EmitMergeRequested → DispatchMerge → (read `success`)
///     │             ├─ success → EmitMerged → MergeOutputs (outcome="merge") → Finish
///     │             └─ failure → EmitMergeFailed → [escalate terminal] (outcome="escalated")
///     ├─ Test    → EmitTestDecision → increment → cap check (>=3)
///     │             ├─ under cap → EmitTestRequested → DispatchTesting (ci-with-debug-retry) → loop back to gate
///     │             └─ over cap  → [escalate terminal] (outcome="escalated")
///     ├─ Reject  → EmitRejected → NotifyRejected → RejectOutputs (outcome="reject") → Finish
///     └─ Invalid → EmitInvalid → [escalate terminal] (outcome="escalated")
///
///   [escalate terminal] = EmitEscalated → NotifyEscalated → EscalateOutputs → Finish
///   (shared by Invalid, a failed merge, and an exceeded test-loop cap)
///
/// <para>Deferred (reported for confirmation): breaking-change detection +
/// mandatory-approval enforcement (FR-34), mode-aware approver policy (FR-32),
/// a finite timeout/reminder arm, and the resume HTTP endpoint
/// (<c>POST /api/adl/{instanceId}/merge-approval</c>) with RBAC. The bookmark
/// resume contract (<c>{decision, feedback, approver}</c>) is honoured.</para>
/// </summary>
public class MergeApprovalWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Merge Approval";
        builder.DefinitionId = "merge-approval";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Human merge/test/reject gate — branches on the decision, acts on it, and audits every edge";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var branchName = builder.WithVariable<string>("BranchName", "");
        var issueNumberVar = builder.WithVariable<int>("IssueNumber", 0);
        var prNumberVar = builder.WithVariable<int>("PrNumber", 0);
        var prUrlVar = builder.WithVariable<string>("PrUrl", "");
        var tenantIdVar = builder.WithVariable<string>("TenantId", "");
        var breakingChangeVar = builder.WithVariable<bool>("BreakingChange", false);

        var decisionVar = builder.WithVariable<string>("Decision", "");
        var feedbackVar = builder.WithVariable<string>("Feedback", "");
        var approverVar = builder.WithVariable<string>("Approver", "");

        // Result of the dispatched merge sub-workflow (CRITICAL-2) + the test loop
        // iteration counter (CRITICAL-3). subResult captures any DispatchWorkflow
        // outputs; mergeSuccessVar is the `success` flag the merge workflow emits.
        var subResult = builder.WithVariable<IDictionary<string, object>?>();
        var mergeSuccessVar = builder.WithVariable<bool>("MergeSuccess", false);
        var testIterationCountVar = builder.WithVariable<int>("TestIterationCount", 0);

        // Max test re-runs before escalating — mirrors PlanMaxRevisions /
        // TaskMaxRevisions (>=3) in SingleIssueCycleWorkflow so an unbounded
        // test → gate → test loop can never spin forever.
        const int MaxTestIterations = 3;

        // ================================================================
        // 1. Read inputs
        // ================================================================
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                branchName.Set(ctx, ctx.GetInput<string>("branchName") ?? "");
                issueNumberVar.Set(ctx, ctx.GetInput<int>("issueNumber"));
                prNumberVar.Set(ctx, ctx.GetInput<int>("prNumber"));
                prUrlVar.Set(ctx, ctx.GetInput<string>("prUrl") ?? "");
                tenantIdVar.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                breakingChangeVar.Set(ctx, ctx.GetInput<bool>("breakingChange"));
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ================================================================
        // 2. Wait for the human decision (bookmark; 4 typed outcomes)
        // ================================================================
        var waitMerge = new WaitForMergeApprovalActivity
        {
            Id = "WaitMergeApproval", Name = "Wait Merge Approval",
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumberVar.Get(ctx)),
            PrUrl = new Input<string?>(ctx => prUrlVar.Get(ctx)),
            BreakingChange = new Input<bool>(ctx => breakingChangeVar.Get(ctx)),
            Decision = new Output<string?>(decisionVar),
            Feedback = new Output<string?>(feedbackVar),
            Approver = new Output<string?>(approverVar),
        };
        waitMerge.SetDisplayText("Wait Merge Approval");

        // ================================================================
        // 3a. Merge path — emit MERGE.REQUESTED → dispatch the real merge workflow
        // ================================================================
        var emitMergeRequested = EmitGateEvent(
            "EmitMergeRequested", "Emit MERGE.REQUESTED",
            MergeApprovalEvents.MergeRequested,
            issueNumberVar, prNumberVar, tenantIdVar, decisionVar, approverVar, feedbackVar);

        var dispatchMerge = new DispatchWorkflow
        {
            Id = "DispatchMerge", Name = "Dispatch Merge",
            WorkflowDefinitionId = new("merge"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["prNumber"] = prNumberVar.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["issueNumber"] = issueNumberVar.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult), // CRITICAL-2 — read the merge `success` output
        };
        dispatchMerge.SetDisplayText("Dispatch Merge");

        // CRITICAL-2 — a swallowed merge failure used to route to the merge/success
        // terminal anyway (the cycle then hung on a `pr-merged` webhook that never
        // fires). Read the dispatched merge workflow's `success` output and branch:
        // success → MergeOutputs (outcome="merge"); failure → the escalate terminal
        // (loud, error-status MERGE.FAILED + ESCALATED events) with outcome="escalated".
        var extractMergeSuccess = Assign(mergeSuccessVar, ctx =>
        {
            var result = subResult.Get(ctx);
            if (result != null && result.TryGetValue("success", out var s))
            {
                return (object)(s switch
                {
                    bool b => b,
                    string str => bool.TryParse(str, out var r) && r,
                    _ => false,
                });
            }
            // No readable success flag → treat as a failed merge (never a silent
            // success). The cycle's CRITICAL-1 switch routes escalated → reportError.
            return (object)false;
        }, "ExtractMergeSuccess", "Extract Merge Success");

        var mergeSucceededCheck = new FlowDecision(ctx => mergeSuccessVar.Get(ctx))
        {
            Id = "MergeSucceeded",
            Name = "Merge Succeeded?",
        };
        mergeSucceededCheck.SetDisplayText("Merge Succeeded?");

        var emitMergeFailed = EmitGateEvent(
            "EmitMergeFailed", "Emit MERGE.FAILED",
            MergeApprovalEvents.MergeFailed,
            issueNumberVar, prNumberVar, tenantIdVar, decisionVar, approverVar, feedbackVar);

        // MINOR-1 — emit the DECISION.MERGED audit event on the successful-merge
        // edge (the constant existed but was never emitted).
        var emitMerged = EmitGateEvent(
            "EmitMerged", "Emit MERGE_APPROVAL.DECISION.MERGED",
            MergeApprovalEvents.DecisionMerged,
            issueNumberVar, prNumberVar, tenantIdVar, decisionVar, approverVar, feedbackVar);

        var mergeOutputs = OutputsSequence(
            "MergeOutputs", "Merge Outputs", "merge", decisionVar, feedbackVar, approverVar);

        // ================================================================
        // 3b. Test path — emit DECISION.TEST → cap check → TEST_REQUESTED →
        //     dispatch testing → loop back to the gate (bounded, CRITICAL-3)
        // ================================================================
        // MINOR-1 — emit the DECISION.TEST audit event on the test edge (the
        // constant existed but was never emitted). TEST_REQUESTED stays the
        // dispatch-time event below.
        var emitTestDecision = EmitGateEvent(
            "EmitTestDecision", "Emit MERGE_APPROVAL.DECISION.TEST",
            MergeApprovalEvents.DecisionTest,
            issueNumberVar, prNumberVar, tenantIdVar, decisionVar, approverVar, feedbackVar);

        var emitTestRequested = EmitGateEvent(
            "EmitTestRequested", "Emit MERGE_APPROVAL.TEST_REQUESTED",
            MergeApprovalEvents.TestRequested,
            issueNumberVar, prNumberVar, tenantIdVar, decisionVar, approverVar, feedbackVar);

        // CRITICAL-3 — bound the test → gate → test loop. Increment a counter on
        // every Test decision and cap it (mirrors PlanMaxRevisions / TaskMaxRevisions
        // >=3 in SingleIssueCycleWorkflow). Over the cap → escalate (loud) instead of
        // re-running CI forever.
        var incrementTestIteration = new SetVariable
        {
            Id = "IncrementTestIteration", Name = "Increment Test Iteration",
            Variable = testIterationCountVar,
            Value = new Input<object?>(ctx => (object)(testIterationCountVar.Get(ctx) + 1)),
        };
        incrementTestIteration.SetDisplayText("Increment Test Iteration");

        var testMaxIterationsCheck = new FlowDecision(
            ctx => testIterationCountVar.Get(ctx) >= MaxTestIterations)
        {
            Id = "TestMaxIterations",
            Name = "Test Max Iterations?",
        };
        testMaxIterationsCheck.SetDisplayText("Test Max Iterations?");

        // The platform has no standalone "testing" definition; the loop's
        // test/CI sub-workflow is "ci-with-debug-retry" (the build-out spec lists
        // it as the alternative). Re-run it, then loop back to the gate.
        var dispatchTesting = new DispatchWorkflow
        {
            Id = "DispatchTesting", Name = "Dispatch Testing",
            WorkflowDefinitionId = new("ci-with-debug-retry"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["prNumber"] = prNumberVar.Get(ctx),
                ["issueNumber"] = issueNumberVar.Get(ctx),
                ["tenantId"] = tenantIdVar.Get(ctx),
            }),
            WaitForCompletion = new(true), // need the test result before re-deciding
        };
        dispatchTesting.SetDisplayText("Dispatch Testing");

        // ================================================================
        // 3c. Reject path — emit REJECTED → label/comment the PR → terminal
        // ================================================================
        var emitRejected = EmitGateEvent(
            "EmitRejected", "Emit MERGE_APPROVAL.DECISION.REJECTED",
            MergeApprovalEvents.DecisionRejected,
            issueNumberVar, prNumberVar, tenantIdVar, decisionVar, approverVar, feedbackVar);

        var notifyRejected = NotifyIssue(
            "NotifyRejected", repository, issueNumberVar,
            "🚫 PR rejected at the merge-approval gate.",
            addLabels: new[] { "tamma-rejected" },
            removeLabels: new[] { "tamma-processing" });

        var rejectOutputs = OutputsSequence(
            "RejectOutputs", "Reject Outputs", "reject", decisionVar, feedbackVar, approverVar);

        // ================================================================
        // 3d. Invalid path — emit DECISION.INVALID → ESCALATED → notify owners →
        //     terminal (loud). Unknown / empty decision NEVER silently rejects a
        //     good PR — and it ESCALATES (it does NOT loop back to the gate).
        // ================================================================
        // MINOR-1 — emit the DECISION.INVALID audit event on the invalid edge
        // (the constant existed but was never emitted) before escalating.
        var emitInvalid = EmitGateEvent(
            "EmitInvalid", "Emit MERGE_APPROVAL.DECISION.INVALID",
            MergeApprovalEvents.DecisionInvalid,
            issueNumberVar, prNumberVar, tenantIdVar, decisionVar, approverVar, feedbackVar);

        var emitEscalated = EmitGateEvent(
            "EmitEscalated", "Emit MERGE_APPROVAL.ESCALATED",
            MergeApprovalEvents.Escalated,
            issueNumberVar, prNumberVar, tenantIdVar, decisionVar, approverVar, feedbackVar);

        var notifyEscalated = NotifyIssue(
            "NotifyEscalated", repository, issueNumberVar,
            "⚠️ Merge-approval gate received an invalid decision — needs human attention.",
            addLabels: new[] { "tamma-needs-human" });

        var escalateOutputs = OutputsSequence(
            "EscalateOutputs", "Escalate Outputs", "escalated", decisionVar, feedbackVar, approverVar);

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart — typed-outcome branching, no dangling edge, no fall-through
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "MergeApprovalFlowchart",
            Name = "Merge Approval Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, waitMerge,
                emitMergeRequested, dispatchMerge,
                extractMergeSuccess, mergeSucceededCheck,
                emitMerged, mergeOutputs, emitMergeFailed,
                emitTestDecision, incrementTestIteration, testMaxIterationsCheck,
                emitTestRequested, dispatchTesting,
                emitRejected, notifyRejected, rejectOutputs,
                emitInvalid, emitEscalated, notifyEscalated, escalateOutputs,
                finish,
            },
            Connections =
            {
                Connect(readInputs, waitMerge),

                // ── Merge → emit MERGE.REQUESTED → dispatch merge → READ success ──
                // CRITICAL-2: branch on the merge sub-workflow's `success` output.
                //   success → DECISION.MERGED → merge outputs → finish
                //   failure → MERGE.FAILED → escalate terminal (loud), never success
                ConnectOutcome(waitMerge, "Merge", emitMergeRequested),
                Connect(emitMergeRequested, dispatchMerge),
                Connect(dispatchMerge, extractMergeSuccess),
                Connect(extractMergeSuccess, mergeSucceededCheck),
                ConnectOutcome(mergeSucceededCheck, "True", emitMerged),
                Connect(emitMerged, mergeOutputs),
                Connect(mergeOutputs, finish),
                ConnectOutcome(mergeSucceededCheck, "False", emitMergeFailed),
                Connect(emitMergeFailed, emitEscalated), // funnel into the loud escalate terminal

                // ── Test → emit DECISION.TEST → increment → CAP CHECK ──
                // CRITICAL-3: bounded loop. Under the cap → TEST_REQUESTED → re-run
                // CI → loop back to the gate. At/over the cap → escalate (loud).
                ConnectOutcome(waitMerge, "Test", emitTestDecision),
                Connect(emitTestDecision, incrementTestIteration),
                Connect(incrementTestIteration, testMaxIterationsCheck),
                ConnectOutcome(testMaxIterationsCheck, "False", emitTestRequested),
                Connect(emitTestRequested, dispatchTesting),
                Connect(dispatchTesting, waitMerge), // loop back to the gate for a re-decision
                ConnectOutcome(testMaxIterationsCheck, "True", emitEscalated), // cap → escalate

                // ── Reject → emit → label/comment → reject outputs → finish (NOT success) ──
                ConnectOutcome(waitMerge, "Reject", emitRejected),
                Connect(emitRejected, notifyRejected),
                Connect(notifyRejected, rejectOutputs),
                Connect(rejectOutputs, finish),

                // ── Invalid → emit DECISION.INVALID → ESCALATED → notify → outputs → finish ──
                // (explicit failure terminal — no silent reject, no fall-through to success,
                //  no loop back to the gate)
                ConnectOutcome(waitMerge, "Invalid", emitInvalid),
                Connect(emitInvalid, emitEscalated),

                // Shared loud escalate terminal (invalid decision / failed merge /
                // test-loop cap exceeded) → notify owners → escalate outputs → finish
                Connect(emitEscalated, notifyEscalated),
                Connect(notifyEscalated, escalateOutputs),
                Connect(escalateOutputs, finish),
            }
        };
    }

    /// <summary>
    /// A gate event-emit node carrying the issue/pr/tenant + decision context.
    /// </summary>
    private static EmitMergeApprovalEventActivity EmitGateEvent(
        string id, string label, string eventType,
        Variable<int> issueNumber, Variable<int> prNumber, Variable<string> tenantId,
        Variable<string> decision, Variable<string> approver, Variable<string> feedback)
    {
        var emit = new EmitMergeApprovalEventActivity
        {
            Id = id, Name = label,
            EventType = new Input<string>(_ => eventType),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumber.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Decision = new Input<string?>(ctx => decision.Get(ctx)),
            Approver = new Input<string?>(ctx => approver.Get(ctx)),
            Feedback = new Input<string?>(ctx => feedback.Get(ctx)),
        };
        emit.SetDisplayText(label);
        return emit;
    }

    /// <summary>
    /// A <see cref="SetVariable"/> node that assigns <paramref name="valueFunc"/>
    /// to <paramref name="variable"/> — mirrors the cycle's Assign helper.
    /// </summary>
    private static SetVariable Assign(
        Variable variable,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, object?> valueFunc,
        string id, string name)
    {
        var sv = new SetVariable { Id = id, Name = name, Variable = variable, Value = new Input<object?>(valueFunc) };
        sv.SetDisplayText(name);
        return sv;
    }

    /// <summary>
    /// Terminal output sequence: surfaces <c>decision</c> / <c>feedback</c> /
    /// <c>approver</c> plus an explicit <c>outcome</c> token so the parent (and
    /// the cycle) can read the gate result without re-deriving it.
    /// </summary>
    private static Sequence OutputsSequence(
        string id, string label, string outcome,
        Variable<string> decision, Variable<string> feedback, Variable<string> approver)
    {
        var seq = new Sequence
        {
            Id = id, Name = label,
            Activities =
            {
                WithLabel(new SetOutput { Id = $"{id}_Outcome", OutputName = new("outcome"), OutputValue = new(_ => (object)outcome) }, "Output outcome"),
                WithLabel(new SetOutput { Id = $"{id}_Decision", OutputName = new("decision"), OutputValue = new(ctx => (object)(decision.Get(ctx) ?? "")) }, "Output decision"),
                WithLabel(new SetOutput { Id = $"{id}_Feedback", OutputName = new("feedback"), OutputValue = new(ctx => (object)(feedback.Get(ctx) ?? "")) }, "Output feedback"),
                WithLabel(new SetOutput { Id = $"{id}_Approver", OutputName = new("approver"), OutputValue = new(ctx => (object)(approver.Get(ctx) ?? "")) }, "Output approver"),
            }
        };
        seq.SetDisplayText(label);
        return seq;
    }

    /// <summary>
    /// Fire-and-forget dispatch to the update-issue-status sub-workflow — same
    /// helper shape the cycle uses to label / comment an issue.
    /// </summary>
    private static DispatchWorkflow NotifyIssue(
        string id,
        Variable<string> repository,
        Variable<int> issueNumber,
        string message,
        string[]? addLabels = null,
        string[]? removeLabels = null)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id,
            Name = $"Notify: {message[..Math.Min(message.Length, 30)]}",
            WorkflowDefinitionId = new("update-issue-status"),
            Input = new(ctx =>
            {
                var input = new Dictionary<string, object>
                {
                    ["repository"] = repository.Get(ctx),
                    ["issueNumber"] = issueNumber.Get(ctx),
                    ["message"] = message,
                };
                if (addLabels != null) input["addLabels"] = addLabels;
                if (removeLabels != null) input["removeLabels"] = removeLabels;
                return input;
            }),
            WaitForCompletion = new(false), // fire and forget
        };
        dispatch.SetDisplayText($"Notify: {message[..Math.Min(message.Length, 30)]}");
        return dispatch;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
