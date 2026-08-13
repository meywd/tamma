using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Merge Complete — the <b>MERGE</b> step of the 14-step autonomous loop
/// (<c>docs/architecture.md</c>): merge the PR (configurable strategy), close the
/// associated issue, delete the feature branch, and audit every transition.
/// Dispatched by the <c>merge-approval</c> gate with <c>WaitForCompletion=true</c>;
/// the gate READS this workflow's <c>success</c> output and routes a failed merge
/// (<c>success=false</c>) to its loud escalate terminal (never the merge/success
/// terminal, which would hang the cycle on a pr-merged webhook that never fires).
///
/// <para>Story 2-10 build-out. The thin wrapper was a four-node linear chain whose
/// <c>Error</c> outcome was UNWIRED — a failed merge dead-ended the flowchart with
/// no failure terminal, no <c>success</c> output, and no event (a silent stall),
/// and it inferred <c>success</c> from a non-empty SHA. This build-out:</para>
/// <list type="bullet">
///   <item><description>Wires the activity's <c>Error</c> outcome to an EXPLICIT
///     failure terminal: <c>success=false</c> + a loud <c>MERGE.FAILED</c> DCB
///     event with the failure code/reason. No dead-end, no false success.</description></item>
///   <item><description>Verifies (not infers) success: <c>success</c> = the merge
///     happened (Merged / MergedWithWarnings), NOT "SHA non-empty". A merged PR
///     whose post-merge issue-close failed routes to <c>MergedWithWarnings</c>
///     (success=true, partial=true) — honest, not a blanket clean success.</description></item>
///   <item><description>Emits <c>MERGE.SUCCESS</c>/<c>MERGE.FAILED</c> +
///     <c>ISSUE.CLOSED.*</c> + <c>BRANCH.DELETED.*</c> DCB events through the
///     durable engine drain (the activity itself, a <c>TammaOutcomeActivity</c>,
///     also auto-emits <c>PR.MERGE.STARTED/.COMPLETED/.FAILED</c>).</description></item>
///   <item><description>Idempotency + a configurable strategy live in the
///     activity (already-merged → success, no 405; <c>merge|squash|rebase</c>).</description></item>
/// </list>
///
/// Flow:
///   ReadInputs → MergePR
///     ├─ Merged             → EmitSuccess (MERGE.SUCCESS + sub-action events) → SuccessOutputs (success=true) → Finish
///     ├─ MergedWithWarnings → EmitSuccess (MERGE.SUCCESS partial + sub-action events) → SuccessOutputs (success=true, partial) → Finish
///     └─ Error              → FailureOutputs (success=false) → EmitFailed (MERGE.FAILED) → Finish
///
/// Inputs:  repository, prNumber, issueNumber, branchName, mergeStrategy?, tenantId?.
/// Outputs: success (bool — the gate's contract), mergeSha, partial, failureCode/failureReason.
/// </summary>
public class MergeWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Merge Complete";
        builder.DefinitionId = "merge";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Merge PR (configurable strategy), close issue, delete branch — with an explicit failure path, verified success, and durable MERGE.* DCB events";

        // ================================================================
        // Variables
        // ================================================================
        var repositoryVar = builder.WithVariable<string>("Repository", "").Persisted();
        var prNumberVar = builder.WithVariable<int>("PrNumber", 0).Persisted();
        var issueNumberVar = builder.WithVariable<int>("IssueNumber", 0).Persisted();
        var branchNameVar = builder.WithVariable<string>("BranchName", "").Persisted();
        var mergeStrategyVar = builder.WithVariable<string>("MergeStrategy", MergePullRequestActivity.DefaultStrategy).Persisted();
        var tenantIdVar = builder.WithVariable<string>("TenantId", "").Persisted();

        var mergeShaVar = builder.WithVariable<string>("MergeSha", "").Persisted();
        var issueClosedVar = builder.WithVariable<bool>("IssueClosed", false).Persisted();
        var branchDeletedVar = builder.WithVariable<bool>("BranchDeleted", false).Persisted();
        var alreadyMergedVar = builder.WithVariable<bool>("AlreadyMerged", false).Persisted();
        var failureCodeVar = builder.WithVariable<string>("FailureCode", "").Persisted();
        var failureReasonVar = builder.WithVariable<string>("FailureReason", "").Persisted();
        var startedAtTicksVar = builder.WithVariable<long>("StartedAtTicks", 0).Persisted();

        // ================================================================
        // 1. Read inputs (the merge-approval gate dispatches repository/prNumber/
        //    branchName/issueNumber; mergeStrategy/tenantId are optional — bound
        //    with safe defaults when absent so the gate's existing contract is
        //    untouched).
        // ================================================================
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = repositoryVar,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                prNumberVar.Set(ctx, ctx.GetInput<int>("prNumber"));
                issueNumberVar.Set(ctx, ctx.GetInput<int>("issueNumber"));
                branchNameVar.Set(ctx, ctx.GetInput<string>("branchName") ?? "");

                var strategy = ctx.GetInput<string>("mergeStrategy");
                mergeStrategyVar.Set(ctx, MergePullRequestActivity.NormalizeStrategy(strategy));

                tenantIdVar.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                startedAtTicksVar.Set(ctx, DateTime.UtcNow.Ticks);
                return (object)repo;
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ================================================================
        // 2. Merge the PR (outcome-bearing — Merged / MergedWithWarnings / Error)
        // ================================================================
        var mergePr = new MergePullRequestActivity
        {
            Id = "MergePR", Name = "Merge PR",
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumberVar.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            BranchName = new Input<string>(ctx => branchNameVar.Get(ctx)),
            MergeStrategy = new Input<string>(ctx => mergeStrategyVar.Get(ctx)),
            MergeSha = new Output<string?>(mergeShaVar),
            IssueClosed = new Output<bool>(issueClosedVar),
            BranchDeleted = new Output<bool>(branchDeletedVar),
            AlreadyMerged = new Output<bool>(alreadyMergedVar),
            FailureCode = new Output<string?>(failureCodeVar),
            FailureReason = new Output<string?>(failureReasonVar),
        };
        mergePr.SetDisplayText("Merge PR");

        // ================================================================
        // 3. Success path — emit MERGE.SUCCESS + the issue-close / branch-delete
        //    sub-action events, then the outputs. Shared by BOTH Merged and
        //    MergedWithWarnings (the success/partial distinction is in the data +
        //    the `partial` output; `success` is true for both).
        // ================================================================
        var emitMergeSuccess = new EmitMergeEventActivity
        {
            Id = "EmitSuccess", Name = "Emit MERGE.SUCCESS",
            EventType = new Input<string>(_ => MergeEvents.Success),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => BuildSuccessData(
                mergeShaVar.Get(ctx), mergeStrategyVar.Get(ctx),
                issueClosedVar.Get(ctx), branchDeletedVar.Get(ctx),
                alreadyMergedVar.Get(ctx), failureReasonVar.Get(ctx),
                ElapsedMs(startedAtTicksVar.Get(ctx)))),
        };
        emitMergeSuccess.SetDisplayText("Emit MERGE.SUCCESS");

        // Sub-action event: ISSUE.CLOSED.SUCCESS / .FAILED (verified, AC5).
        var emitIssueClosed = new EmitMergeEventActivity
        {
            Id = "EmitIssueClosed", Name = "Emit ISSUE.CLOSED.*",
            EventType = new Input<string>(ctx => issueClosedVar.Get(ctx)
                ? MergeEvents.IssueClosedSuccess : MergeEvents.IssueClosedFailed),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["issueClosed"] = issueClosedVar.Get(ctx),
                ["mergeSha"] = mergeShaVar.Get(ctx),
            })),
        };
        emitIssueClosed.SetDisplayText("Emit ISSUE.CLOSED.*");

        // Sub-action event: BRANCH.DELETED.SUCCESS / .FAILED (best-effort, AC5).
        var emitBranchDeleted = new EmitMergeEventActivity
        {
            Id = "EmitBranchDeleted", Name = "Emit BRANCH.DELETED.*",
            EventType = new Input<string>(ctx => branchDeletedVar.Get(ctx)
                ? MergeEvents.BranchDeletedSuccess : MergeEvents.BranchDeletedFailed),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["branchDeleted"] = branchDeletedVar.Get(ctx),
                ["branchName"] = branchNameVar.Get(ctx),
            })),
        };
        emitBranchDeleted.SetDisplayText("Emit BRANCH.DELETED.*");

        var successOutputs = new Sequence
        {
            Id = "SuccessOutputs", Name = "Success Outputs",
            Activities =
            {
                // `success` is the gate's contract — TRUE for a real merge
                // (Merged OR MergedWithWarnings), never inferred from a SHA.
                WithLabel(new SetOutput { Id = "OutSuccess", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Output success=true"),
                WithLabel(new SetOutput { Id = "OutMergeSha", OutputName = new("mergeSha"), OutputValue = new(ctx => (object)(mergeShaVar.Get(ctx) ?? "")) }, "Output mergeSha"),
                WithLabel(new SetOutput { Id = "OutMergeStrategy", OutputName = new("mergeStrategy"), OutputValue = new(ctx => (object)mergeStrategyVar.Get(ctx)) }, "Output mergeStrategy"),
                WithLabel(new SetOutput { Id = "OutIssueClosed", OutputName = new("issueClosed"), OutputValue = new(ctx => (object)issueClosedVar.Get(ctx)) }, "Output issueClosed"),
                WithLabel(new SetOutput { Id = "OutBranchDeleted", OutputName = new("branchDeleted"), OutputValue = new(ctx => (object)branchDeletedVar.Get(ctx)) }, "Output branchDeleted"),
                // partial = a post-merge sub-action failed (MergedWithWarnings) —
                // surfaced as data, not as a failure (success stays true).
                WithLabel(new SetOutput { Id = "OutPartial", OutputName = new("partial"), OutputValue = new(ctx => (object)(!string.IsNullOrEmpty(failureReasonVar.Get(ctx)))) }, "Output partial"),
            }
        };
        successOutputs.SetDisplayText("Success Outputs");

        // ================================================================
        // 4. Failure path — success=false (NO fall-through to success), then emit
        //    MERGE.FAILED (loud, error-status). The merge-approval gate reads
        //    success=false → escalate. mergeSha forced empty (no false merge).
        // ================================================================
        var failureOutputs = new Sequence
        {
            Id = "FailureOutputs", Name = "Failure Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutFailSuccess", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Output success=false"),
                WithLabel(new SetOutput { Id = "OutFailMergeSha", OutputName = new("mergeSha"), OutputValue = new(_ => (object)"") }, "Output mergeSha="),
                WithLabel(new SetOutput { Id = "OutFailCode", OutputName = new("failureCode"), OutputValue = new(ctx => (object)(NullIfBlank(failureCodeVar.Get(ctx)) ?? "merge-failed")) }, "Output failureCode"),
                WithLabel(new SetOutput { Id = "OutFailReason", OutputName = new("failureReason"), OutputValue = new(ctx => (object)(NullIfBlank(failureReasonVar.Get(ctx)) ?? "merge failed")) }, "Output failureReason"),
                WithLabel(new SetOutput { Id = "OutFailPartial", OutputName = new("partial"), OutputValue = new(_ => (object)false) }, "Output partial=false"),
            }
        };
        failureOutputs.SetDisplayText("Failure Outputs");

        var emitMergeFailed = new EmitMergeEventActivity
        {
            Id = "EmitFailed", Name = "Emit MERGE.FAILED",
            EventType = new Input<string>(_ => MergeEvents.Failed),
            IssueNumber = new Input<int>(ctx => issueNumberVar.Get(ctx)),
            PrNumber = new Input<int>(ctx => prNumberVar.Get(ctx)),
            Repository = new Input<string>(ctx => repositoryVar.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            DataJson = new Input<string?>(ctx => BuildFailureData(
                failureCodeVar.Get(ctx), failureReasonVar.Get(ctx),
                mergeStrategyVar.Get(ctx), ElapsedMs(startedAtTicksVar.Get(ctx)))),
        };
        emitMergeFailed.SetDisplayText("Emit MERGE.FAILED");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart — every outcome routed to a terminal, no dangling edge,
        // no fall-through to success (the headline thin-wrapper bug).
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "MergeFlowchart",
            Name = "Merge Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, mergePr,
                emitMergeSuccess, emitIssueClosed, emitBranchDeleted, successOutputs,
                failureOutputs, emitMergeFailed, finish,
            },
            Connections =
            {
                Connect(readInputs, mergePr),

                // Merged → success path (MERGE.SUCCESS → sub-action events → outputs)
                ConnectOutcome(mergePr, "Merged", emitMergeSuccess),
                // MergedWithWarnings → SAME success path (success=true, partial=true)
                ConnectOutcome(mergePr, "MergedWithWarnings", emitMergeSuccess),
                Connect(emitMergeSuccess, emitIssueClosed),
                Connect(emitIssueClosed, emitBranchDeleted),
                Connect(emitBranchDeleted, successOutputs),
                Connect(successOutputs, finish),

                // Error → explicit failure path (success=false → MERGE.FAILED).
                // NO fall-through to success — the dead-end is gone.
                ConnectOutcome(mergePr, "Error", failureOutputs),
                Connect(failureOutputs, emitMergeFailed),
                Connect(emitMergeFailed, finish),
            }
        };
    }

    private static string? NullIfBlank(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s;

    private static long ElapsedMs(long startedAtTicks)
    {
        if (startedAtTicks <= 0) return 0;
        var elapsed = DateTime.UtcNow.Ticks - startedAtTicks;
        return elapsed > 0 ? elapsed / TimeSpan.TicksPerMillisecond : 0;
    }

    private static string BuildSuccessData(
        string? mergeSha, string mergeStrategy, bool issueClosed, bool branchDeleted,
        bool alreadyMerged, string? warnings, long durationMs)
    {
        var data = new Dictionary<string, object?>
        {
            ["mergeSha"] = mergeSha ?? "",
            ["mergeStrategy"] = mergeStrategy,
            ["issueClosed"] = issueClosed,
            ["branchDeleted"] = branchDeleted,
            ["alreadyMerged"] = alreadyMerged,
            ["partial"] = !string.IsNullOrEmpty(warnings),
            ["warnings"] = Truncate(warnings, 280),
            ["durationMs"] = durationMs,
        };
        return JsonSerializer.Serialize(data);
    }

    private static string BuildFailureData(
        string? failureCode, string? failureReason, string mergeStrategy, long durationMs)
    {
        var data = new Dictionary<string, object?>
        {
            ["failureCode"] = NullIfBlank(failureCode) ?? "merge-failed",
            ["failureReason"] = Truncate(failureReason, 280),
            ["mergeStrategy"] = mergeStrategy,
            ["durationMs"] = durationMs,
        };
        return JsonSerializer.Serialize(data);
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max];
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
