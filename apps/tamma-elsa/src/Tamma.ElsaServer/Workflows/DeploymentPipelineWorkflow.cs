using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.IncidentStrategies;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using Tamma.Activities.ADL;
using Tamma.Api.Services.Agents;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Deployment Pipeline — post-merge promotion through QA → UAT → Production
/// (step 15 of the autonomous loop, invoked by <c>SingleIssueCycleWorkflow</c>).
///
/// <para><b>Build-out (completeness audit 2026-06-22):</b></para>
/// <list type="bullet">
///   <item><description><b>P0 item 1 — fail-closed stage result.</b>
///     <see cref="ExtractStageResult"/> defaults the stage status to
///     <c>failed</c> and only promotes on an explicitly-parsed
///     <c>status:"success"</c>. A missing / empty / unparseable / dispatch-error
///     result takes the failure edge (never a silent false success). The failure
///     reason is captured into a <c>StageError</c> variable for the audit
///     event.</description></item>
///   <item><description><b>P0 item 2 — full DCB audit trail.</b> Every edge emits
///     a typed <c>DEPLOY.*</c> event via <see cref="EmitDeploymentEventActivity"/>
///     through the durable engine event drain: <c>STAGE.STARTED</c> before each
///     dispatch, <c>STAGE.SUCCESS</c>/<c>STAGE.FAILED</c> after each extract,
///     <c>PRODUCTION.APPROVAL_REQUESTED/APPROVED/REJECTED</c> around the prod
///     gate, <c>ROLLBACK.*</c> in the rollback branch, and
///     <c>PIPELINE.SUCCESS</c>/<c>PIPELINE.FAILED</c> at the terminals.</description></item>
///   <item><description><b>P0 item 3 — production approval gate.</b> Before the
///     production deploy a <c>FlowDecision(mode == business || requireProdApproval)</c>
///     routes to <see cref="WaitForDeploymentApprovalActivity"/> (bookmark-based
///     human gate). Business Mode requires approval; dev mode deploys straight
///     through. Approve → ProdDeploy; Reject / Invalid → prod-failure terminal
///     (NEVER a silent prod deploy).</description></item>
///   <item><description><b>P0 item 4 — rollback on prod failure.</b> A failed
///     production deploy routes through <c>RollbackProduction</c> (dispatches
///     <c>llm-call</c> with the <c>rollback</c> action to revert prod to the
///     previous release), emitting <c>ROLLBACK.STARTED</c> →
///     <c>ROLLBACK.SUCCESS</c>/<c>ROLLBACK.FAILED</c>, then the prod-failure
///     terminal.</description></item>
///   <item><description><b>P1 item 6 — bounded gate retry + escalation.</b> Each
///     stage retries up to <c>MaxStageRetries</c> (3, per FR-16) before routing to
///     its failure terminal — no silent bypass.</description></item>
///   <item><description><b>P1 item 7 — doc-comment matches the code</b> (this
///     header; the synchronous <c>WaitForCompletion</c> dispatch model is
///     documented honestly — the async-bookmark deploy-confirmation model is the
///     follow-up).</description></item>
/// </list>
///
/// <para><b>Honoured rules:</b> all LLM/agent work routes through
/// <c>DispatchWorkflow(llm-call)</c> — no step calls a provider directly;
/// fail-closed everywhere (never an empty/plain fallback); a fault uses
/// continue-with-incidents and the pre-seeded <c>deploymentStatus = "failed"</c>
/// so an internal fault never reports a silent success.</para>
///
/// <para><b>Deferred (P1 item 5 — release/tag):</b> no GitHub-release / git-tag
/// activity exists anywhere in <c>apps/tamma-elsa/src</c> (a real seam is a
/// follow-up — a <c>CreateReleaseActivity</c> wrapping the platform's GitHub
/// client). Rather than FAKE a release, the successful-prod terminal surfaces
/// <c>releaseStatus = "deferred"</c> + a computed <c>releaseTag</c> output and
/// records it in the pipeline-success event, so the gap is explicit and queryable
/// instead of silently claimed-done. P2/P3 items (post-deploy health probe, typed
/// mediation contract, per-stage timeout, notifications) remain follow-ups.</para>
///
/// Flow:
///   Init (status=failed default) → QA STARTED → QA Deploy (llm-call) → Extract → QA OK?
///     ├─ Yes → QA SUCCESS → UAT STARTED → UAT Deploy → Extract → UAT OK?
///     │   ├─ Yes → UAT SUCCESS → ProdApprovalNeeded?
///     │   │   ├─ Yes(business) → APPROVAL_REQUESTED gate
///     │   │   │     ├─ Approve → APPROVED → Prod STARTED → Prod Deploy → Extract → Prod OK?
///     │   │   │     ├─ Reject  → REJECTED → SetProdFailed
///     │   │   │     └─ Invalid → REJECTED → SetProdFailed
///     │   │   └─ No(dev) → Prod STARTED → Prod Deploy → Extract → Prod OK?
///     │   │         ├─ Yes → Prod SUCCESS → PIPELINE.SUCCESS (releaseStatus=deferred) → Output
///     │   │         └─ No  → Prod FAILED → Rollback (ROLLBACK.*) → SetProdFailed
///     │   └─ No → UAT FAILED → SetUATFailed
///     └─ No → QA FAILED → SetQAFailed
///   (each SetXxxFailed → PIPELINE.FAILED → Output → Finish)
///
/// Inputs: repository, mergeSha, issueNumber, branchName, mode, tenantId, requireProdApproval
/// Outputs: deploymentStatus (success/failed:&lt;stage&gt;), completedStages (JSON array),
///          releaseTag, releaseStatus (deferred), rollbackStatus
/// </summary>
public class DeploymentPipelineWorkflow : WorkflowBase
{
    /// <summary>Max re-dispatch attempts per stage before routing to the failure
    /// terminal (FR-16: 3-retry limit, mandatory escalation, no bypass).</summary>
    private const int MaxStageRetries = 3;

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Deployment Pipeline";
        builder.DefinitionId = "deployment-pipeline";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Deploy through QA -> UAT -> Prod with a fail-closed gate per stage, a Business-Mode production approval gate, rollback on prod failure, and a full DCB audit trail. Release/tag deferred (no seam).";

        // Fail-closed on internal fault — a faulted activity must not halt the
        // instance with no output. Continue-with-incidents keeps the flow alive;
        // `deploymentStatus` is seeded to "failed" at Init so a fault that stops
        // the flow before a terminal still yields a fail-closed status the parent
        // cycle reads (never a silent success). Mirrors MergeApprovalWorkflow I1.
        builder.WorkflowOptions.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy);

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var mergeSha = builder.WithVariable<string>("MergeSha", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var branchName = builder.WithVariable<string>("BranchName", "");
        var mode = builder.WithVariable<string>("Mode", "");
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var requireProdApproval = builder.WithVariable<bool>("RequireProdApproval", false);

        // Fail-closed default — overwritten only by an explicit terminal.
        var deploymentStatus = builder.WithVariable<string>("DeploymentStatus", "failed");
        var completedStages = builder.WithVariable<string>("CompletedStages", "[]");
        var currentStage = builder.WithVariable<string>("CurrentStage", "");
        var stageResult = builder.WithVariable<string>("StageResult", "");
        var stageError = builder.WithVariable<string>("StageError", "");
        var rollbackStatus = builder.WithVariable<string>("RollbackStatus", "");
        var releaseTag = builder.WithVariable<string>("ReleaseTag", "");

        var decisionVar = builder.WithVariable<string>("Decision", "");
        var feedbackVar = builder.WithVariable<string>("Feedback", "");
        var approverVar = builder.WithVariable<string>("Approver", "");

        // Per-stage retry counters (FR-16 — bounded retry + escalation).
        var qaRetries = builder.WithVariable<int>("QaRetries", 0);
        var uatRetries = builder.WithVariable<int>("UatRetries", 0);
        var prodRetries = builder.WithVariable<int>("ProdRetries", 0);

        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 1. Init
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Initialize",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                mergeSha.Set(ctx, ctx.GetInput<string>("mergeSha") ?? "");
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                branchName.Set(ctx, ctx.GetInput<string>("branchName") ?? "");
                mode.Set(ctx, ctx.GetInput<string>("mode") ?? "");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                requireProdApproval.Set(ctx, ctx.GetInput<bool>("requireProdApproval"));
                completedStages.Set(ctx, "[]");
                // Fail-closed: status stays "failed" until a terminal explicitly sets it.
                deploymentStatus.Set(ctx, "failed");
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. QA Stage
        // ================================================================
        var emitQaStarted = EmitDeployEvent("EmitQaStarted", "Emit QA STARTED",
            DeployEvents.StageStarted, "qa",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus);

        var qaDeployCall = StageDeployDispatch("QADeploy", "QA Deploy", "qa", deploy: true,
            repository, mergeSha, issueNumber, branchName, tenantId, completedStages, llmResult);

        var extractQaResult = ExtractStageResult("ExtractQA", "Extract QA Result",
            stageResult, currentStage, stageError, "qa", llmResult, completedStages);

        var qaOk = new FlowDecision(ctx => stageResult.Get(ctx) == "success")
        { Id = "QAOk", Name = "QA OK?" };
        qaOk.SetDisplayText("QA OK?");

        var emitQaSuccess = EmitDeployEvent("EmitQaSuccess", "Emit QA SUCCESS",
            DeployEvents.StageSuccess, "qa",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus);

        // FR-16 — bounded QA retry. Under the cap → re-dispatch; at the cap → fail.
        var qaRetryCheck = RetryCheck("QaRetryCheck", "QA Under Retry Cap?", qaRetries);
        var qaIncrement = IncrementRetry("QaIncrement", "QA Retry++", qaRetries);
        var emitQaFailed = EmitDeployEvent("EmitQaFailed", "Emit QA FAILED",
            DeployEvents.StageFailed, "qa",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus);

        // ================================================================
        // 3. UAT Stage
        // ================================================================
        var emitUatStarted = EmitDeployEvent("EmitUatStarted", "Emit UAT STARTED",
            DeployEvents.StageStarted, "uat",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus);

        var uatDeployCall = StageDeployDispatch("UATDeploy", "UAT Deploy", "uat", deploy: true,
            repository, mergeSha, issueNumber, branchName, tenantId, completedStages, llmResult);

        var extractUatResult = ExtractStageResult("ExtractUAT", "Extract UAT Result",
            stageResult, currentStage, stageError, "uat", llmResult, completedStages);

        var uatOk = new FlowDecision(ctx => stageResult.Get(ctx) == "success")
        { Id = "UATOk", Name = "UAT OK?" };
        uatOk.SetDisplayText("UAT OK?");

        var emitUatSuccess = EmitDeployEvent("EmitUatSuccess", "Emit UAT SUCCESS",
            DeployEvents.StageSuccess, "uat",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus);

        var uatRetryCheck = RetryCheck("UatRetryCheck", "UAT Under Retry Cap?", uatRetries);
        var uatIncrement = IncrementRetry("UatIncrement", "UAT Retry++", uatRetries);
        var emitUatFailed = EmitDeployEvent("EmitUatFailed", "Emit UAT FAILED",
            DeployEvents.StageFailed, "uat",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus);

        // ================================================================
        // 4. Production approval gate (P0 item 3) — Business Mode (or an explicit
        //    requireProdApproval flag) requires a human checkpoint before prod.
        // ================================================================
        var prodApprovalNeeded = new FlowDecision(ctx =>
            string.Equals(mode.Get(ctx)?.Trim(), "business", StringComparison.OrdinalIgnoreCase)
            || requireProdApproval.Get(ctx))
        { Id = "ProdApprovalNeeded", Name = "Prod Approval Needed?" };
        prodApprovalNeeded.SetDisplayText("Prod Approval Needed?");

        var waitProdApproval = new WaitForDeploymentApprovalActivity
        {
            Id = "WaitProdApproval", Name = "Wait Prod Approval",
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            MergeSha = new Input<string?>(ctx => mergeSha.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Repository = new Input<string?>(ctx => repository.Get(ctx)),
            Decision = new Output<string?>(decisionVar),
            Feedback = new Output<string?>(feedbackVar),
            Approver = new Output<string?>(approverVar),
        };
        waitProdApproval.SetDisplayText("Wait Prod Approval");

        var emitProdApproved = EmitDeployEvent("EmitProdApproved", "Emit PRODUCTION.APPROVED",
            DeployEvents.ProductionApproved, "production",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus,
            approver: approverVar, feedback: feedbackVar);

        var emitProdRejected = EmitDeployEvent("EmitProdRejected", "Emit PRODUCTION.REJECTED",
            DeployEvents.ProductionRejected, "production",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus,
            approver: approverVar, feedback: feedbackVar);

        // ================================================================
        // 5. Production Stage
        // ================================================================
        var emitProdStarted = EmitDeployEvent("EmitProdStarted", "Emit PROD STARTED",
            DeployEvents.StageStarted, "production",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus);

        var prodDeployCall = StageDeployDispatch("ProdDeploy", "Prod Deploy", "production", deploy: true,
            repository, mergeSha, issueNumber, branchName, tenantId, completedStages, llmResult);

        var extractProdResult = ExtractStageResult("ExtractProd", "Extract Prod Result",
            stageResult, currentStage, stageError, "production", llmResult, completedStages);

        var prodOk = new FlowDecision(ctx => stageResult.Get(ctx) == "success")
        { Id = "ProdOk", Name = "Prod OK?" };
        prodOk.SetDisplayText("Prod OK?");

        var emitProdSuccess = EmitDeployEvent("EmitProdSuccess", "Emit PROD SUCCESS",
            DeployEvents.StageSuccess, "production",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus);

        var prodRetryCheck = RetryCheck("ProdRetryCheck", "Prod Under Retry Cap?", prodRetries);
        var prodIncrement = IncrementRetry("ProdIncrement", "Prod Retry++", prodRetries);
        var emitProdFailed = EmitDeployEvent("EmitProdFailed", "Emit PROD FAILED",
            DeployEvents.StageFailed, "production",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus);

        // ================================================================
        // 6. Rollback on production failure (P0 item 4)
        // ================================================================
        // Rollback emits source their `status` from rollbackStatus (NOT stageResult,
        // which in this branch is still the stale prod "failed"). isRollback=true.
        var emitRollbackStarted = EmitDeployEvent("EmitRollbackStarted", "Emit ROLLBACK.STARTED",
            DeployEvents.RollbackStarted, "production",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus,
            isRollback: true);

        // Rollback dispatches the mediated llm-call with the `rollback` action —
        // it does NOT call a provider directly (mediation rule). Reverts prod to
        // the previous release.
        var rollbackCall = StageDeployDispatch("RollbackProd", "Rollback Prod", "production", deploy: false,
            repository, mergeSha, issueNumber, branchName, tenantId, completedStages, llmResult);

        var extractRollbackResult = ExtractRollbackResult("ExtractRollback", "Extract Rollback Result",
            rollbackStatus, llmResult);

        var rollbackOk = new FlowDecision(ctx => rollbackStatus.Get(ctx) == "success")
        { Id = "RollbackOk", Name = "Rollback OK?" };
        rollbackOk.SetDisplayText("Rollback OK?");

        var emitRollbackSuccess = EmitDeployEvent("EmitRollbackSuccess", "Emit ROLLBACK.SUCCESS",
            DeployEvents.RollbackSuccess, "production",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus,
            isRollback: true);

        var emitRollbackFailed = EmitDeployEvent("EmitRollbackFailed", "Emit ROLLBACK.FAILED",
            DeployEvents.RollbackFailed, "production",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus,
            isRollback: true);

        // ================================================================
        // 7. Success terminal — compute the (deferred) release tag, emit
        //    PIPELINE.SUCCESS, set status=success.
        // ================================================================
        var setReleaseTag = new SetVariable
        {
            Id = "SetReleaseTag", Name = "Compute Release Tag",
            Variable = releaseTag,
            // P1 item 5 (deferred): no release/tag activity exists; we compute a
            // candidate tag from the merged SHA so the audit row and output carry
            // it, but DO NOT cut a real release (releaseStatus=deferred). Never a
            // fake "release created".
            Value = new Input<object?>(ctx =>
            {
                var sha = mergeSha.Get(ctx) ?? "";
                var shortSha = sha.Length >= 7 ? sha[..7] : sha;
                return (object)(string.IsNullOrEmpty(shortSha) ? "" : $"deploy-{shortSha}");
            })
        };
        setReleaseTag.SetDisplayText("Compute Release Tag");

        var setSuccess = new SetVariable
        {
            Id = "SetSuccess", Name = "Set Success",
            Variable = deploymentStatus,
            Value = new Input<object?>(_ => (object)"success")
        };
        setSuccess.SetDisplayText("Set Success");

        var emitPipelineSuccess = EmitDeployEvent("EmitPipelineSuccess", "Emit PIPELINE.SUCCESS",
            DeployEvents.PipelineSuccess, /*stage*/ "",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus,
            releaseTag: releaseTag);

        // ================================================================
        // 8. Failure terminals (one per stage) → PIPELINE.FAILED → outputs
        // ================================================================
        var setQaFailed = CreateFailureNode("SetQAFailed", "QA Failed", deploymentStatus, "qa");
        var setUatFailed = CreateFailureNode("SetUATFailed", "UAT Failed", deploymentStatus, "uat");
        var setProdFailed = CreateFailureNode("SetProdFailed", "Prod Failed", deploymentStatus, "production");

        var emitPipelineFailed = EmitDeployEvent("EmitPipelineFailed", "Emit PIPELINE.FAILED",
            DeployEvents.PipelineFailed, /*stage*/ "",
            repository, mergeSha, issueNumber, mode, tenantId, stageResult, stageError, completedStages, rollbackStatus);

        // ================================================================
        // 9. Set Outputs
        // ================================================================
        var setOutputs = new Sequence
        {
            Id = "SetOutputs", Name = "Set Outputs",
            Activities =
            {
                new SetOutput
                    { Id = "OutStatus", OutputName = new("deploymentStatus"), OutputValue = new(ctx => (object)deploymentStatus.Get(ctx)) },
                new SetOutput
                    { Id = "OutStages", OutputName = new("completedStages"), OutputValue = new(ctx => (object)completedStages.Get(ctx)) },
                new SetOutput
                    { Id = "OutReleaseTag", OutputName = new("releaseTag"), OutputValue = new(ctx => (object)releaseTag.Get(ctx)) },
                // P1 item 5 deferral surfaced explicitly so the gap is queryable.
                new SetOutput
                    { Id = "OutReleaseStatus", OutputName = new("releaseStatus"), OutputValue = new(_ => (object)"deferred") },
                new SetOutput
                    { Id = "OutRollbackStatus", OutputName = new("rollbackStatus"), OutputValue = new(ctx => (object)rollbackStatus.Get(ctx)) },
            }
        };
        setOutputs.SetDisplayText("Set Outputs");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "DeploymentPipelineFlowchart",
            Start = init,
            Activities =
            {
                init,
                // QA
                emitQaStarted, qaDeployCall, extractQaResult, qaOk, emitQaSuccess,
                qaRetryCheck, qaIncrement, emitQaFailed,
                // UAT
                emitUatStarted, uatDeployCall, extractUatResult, uatOk, emitUatSuccess,
                uatRetryCheck, uatIncrement, emitUatFailed,
                // Prod approval gate
                prodApprovalNeeded, waitProdApproval, emitProdApproved, emitProdRejected,
                // Prod
                emitProdStarted, prodDeployCall, extractProdResult, prodOk, emitProdSuccess,
                prodRetryCheck, prodIncrement, emitProdFailed,
                // Rollback
                emitRollbackStarted, rollbackCall, extractRollbackResult, rollbackOk,
                emitRollbackSuccess, emitRollbackFailed,
                // Success terminal
                setReleaseTag, setSuccess, emitPipelineSuccess,
                // Failure terminals
                setQaFailed, setUatFailed, setProdFailed, emitPipelineFailed,
                setOutputs, finish,
            },
            Connections =
            {
                // ── Init → QA ──
                Connect(init, emitQaStarted),
                Connect(emitQaStarted, qaDeployCall),
                Connect(qaDeployCall, extractQaResult),
                Connect(extractQaResult, qaOk),
                // QA OK → emit success → UAT
                ConnectOutcome(qaOk, "True", emitQaSuccess),
                Connect(emitQaSuccess, emitUatStarted),
                // QA not-OK → bounded retry: under cap → increment → re-dispatch;
                //            at cap → loud FAILED → terminal
                ConnectOutcome(qaOk, "False", qaRetryCheck),
                ConnectOutcome(qaRetryCheck, "True", qaIncrement),
                Connect(qaIncrement, emitQaStarted),         // re-run QA
                ConnectOutcome(qaRetryCheck, "False", emitQaFailed),
                Connect(emitQaFailed, setQaFailed),
                Connect(setQaFailed, emitPipelineFailed),

                // ── UAT ──
                Connect(emitUatStarted, uatDeployCall),
                Connect(uatDeployCall, extractUatResult),
                Connect(extractUatResult, uatOk),
                ConnectOutcome(uatOk, "True", emitUatSuccess),
                Connect(emitUatSuccess, prodApprovalNeeded),
                ConnectOutcome(uatOk, "False", uatRetryCheck),
                ConnectOutcome(uatRetryCheck, "True", uatIncrement),
                Connect(uatIncrement, emitUatStarted),       // re-run UAT
                ConnectOutcome(uatRetryCheck, "False", emitUatFailed),
                Connect(emitUatFailed, setUatFailed),
                Connect(setUatFailed, emitPipelineFailed),

                // ── Production approval gate (P0 item 3) ──
                // Business mode (or requireProdApproval) → human gate; dev → straight to prod.
                ConnectOutcome(prodApprovalNeeded, "True", waitProdApproval),
                ConnectOutcome(prodApprovalNeeded, "False", emitProdStarted),
                // Gate outcomes — Approve → prod; Reject/Invalid → loud reject → prod-failure
                ConnectOutcome(waitProdApproval, "Approve", emitProdApproved),
                Connect(emitProdApproved, emitProdStarted),
                ConnectOutcome(waitProdApproval, "Reject", emitProdRejected),
                ConnectOutcome(waitProdApproval, "Invalid", emitProdRejected),
                Connect(emitProdRejected, setProdFailed),

                // ── Production deploy ──
                Connect(emitProdStarted, prodDeployCall),
                Connect(prodDeployCall, extractProdResult),
                Connect(extractProdResult, prodOk),
                ConnectOutcome(prodOk, "True", emitProdSuccess),
                Connect(emitProdSuccess, setReleaseTag),
                // Prod not-OK → bounded retry; at cap → FAILED → rollback → terminal
                ConnectOutcome(prodOk, "False", prodRetryCheck),
                ConnectOutcome(prodRetryCheck, "True", prodIncrement),
                Connect(prodIncrement, emitProdStarted),     // re-run prod
                ConnectOutcome(prodRetryCheck, "False", emitProdFailed),

                // ── Rollback on prod failure (P0 item 4) ──
                Connect(emitProdFailed, emitRollbackStarted),
                Connect(emitRollbackStarted, rollbackCall),
                Connect(rollbackCall, extractRollbackResult),
                Connect(extractRollbackResult, rollbackOk),
                ConnectOutcome(rollbackOk, "True", emitRollbackSuccess),
                Connect(emitRollbackSuccess, setProdFailed),
                ConnectOutcome(rollbackOk, "False", emitRollbackFailed),
                Connect(emitRollbackFailed, setProdFailed),

                // ── Success terminal ──
                Connect(setReleaseTag, setSuccess),
                Connect(setSuccess, emitPipelineSuccess),
                Connect(emitPipelineSuccess, setOutputs),

                // ── Failure terminals → PIPELINE.FAILED → outputs ──
                Connect(setProdFailed, emitPipelineFailed),
                Connect(emitPipelineFailed, setOutputs),

                // Outputs → finish
                Connect(setOutputs, finish),
            }
        };
    }

    // ================================================================
    // Helpers
    // ================================================================

    /// <summary>
    /// A stage deploy/rollback dispatch through the mediated <c>llm-call</c>
    /// endpoint (NEVER a direct provider call). <paramref name="deploy"/> selects
    /// the <c>deploy</c> action; <c>false</c> selects the <c>rollback</c> action
    /// for the prod-failure rollback branch.
    /// </summary>
    private static DispatchWorkflow StageDeployDispatch(
        string id, string displayName, string stage, bool deploy,
        Variable<string> repository, Variable<string> mergeSha,
        Variable<int> issueNumber, Variable<string> branchName,
        Variable<string> tenantId, Variable<string> completedStages,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = displayName,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = AgentRole.Devops.ToWire(),
                ["action"] = (deploy ? AgentAction.Deploy : AgentAction.Rollback).ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["stage"] = stage,
                    ["operation"] = deploy ? "deploy" : "rollback",
                    ["repository"] = repository.Get(ctx),
                    ["mergeSha"] = mergeSha.Get(ctx),
                    ["issueNumber"] = issueNumber.Get(ctx),
                    ["branchName"] = branchName.Get(ctx),
                    ["completedStages"] = completedStages.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(result),
        };
        dispatch.SetDisplayText(displayName);
        return dispatch;
    }

    /// <summary>
    /// Parse the mediated deploy result into a stage status — <b>fail-closed</b>
    /// (P0 item 1). Defaults to <c>failed</c> and only sets <c>success</c> when the
    /// result is present AND carries an explicit <c>status:"success"</c>. A null /
    /// missing / empty / non-JSON / parse-error result stays <c>failed</c> and the
    /// reason is captured into <paramref name="stageError"/>. Only an explicit
    /// success appends the stage to <c>completedStages</c>.
    /// </summary>
    private static SetVariable ExtractStageResult(
        string id, string displayName,
        Variable<string> stageResult, Variable<string> currentStage, Variable<string> stageError,
        string stageName,
        Variable<IDictionary<string, object>?> llmResult,
        Variable<string> completedStages)
    {
        var sv = new SetVariable
        {
            Id = id, Name = displayName,
            Variable = stageResult,
            Value = new Input<object?>(ctx =>
            {
                currentStage.Set(ctx, stageName);
                var (status, error) = ParseStageStatus(llmResult.Get(ctx));
                stageError.Set(ctx, error);

                // Only an EXPLICIT success promotes / appends the stage.
                if (status == "success")
                {
                    var stages = ParseCompletedStages(completedStages.Get(ctx));
                    if (!stages.Contains(stageName)) stages.Add(stageName);
                    completedStages.Set(ctx, JsonSerializer.Serialize(stages));
                }

                return (object)status;
            })
        };
        sv.SetDisplayText(displayName);
        return sv;
    }

    /// <summary>
    /// Fail-closed parse of the mediated deploy/rollback result. Returns
    /// (<c>"success"</c>, "") only on an explicit <c>status:"success"</c>;
    /// otherwise (<c>"failed"</c>, reason). Pure — exposed for unit testing the
    /// silent-false-success regression (item 1 / test 13).
    /// </summary>
    public static (string Status, string Error) ParseStageStatus(IDictionary<string, object>? result)
    {
        if (result == null)
            return ("failed", "no result from deploy dispatch (null)");
        if (!result.TryGetValue("llmResponse", out var r))
            return ("failed", "deploy result missing llmResponse field");

        var output = r?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(output))
            return ("failed", "empty deploy response");

        try
        {
            var jsonStart = output.IndexOf('{');
            var jsonEnd = output.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return ("failed", "deploy response is not parseable JSON");

            using var doc = JsonDocument.Parse(output[jsonStart..(jsonEnd + 1)]);
            if (!doc.RootElement.TryGetProperty("status", out var s))
                return ("failed", "deploy response has no status field");

            var statusValue = s.GetString();
            // Fail-closed: ONLY an explicit "success" promotes. "failed" /
            // anything-else / null is a failure.
            return string.Equals(statusValue, "success", StringComparison.OrdinalIgnoreCase)
                ? ("success", "")
                : ("failed", $"deploy reported status='{statusValue ?? "<null>"}'");
        }
        catch (JsonException ex)
        {
            return ("failed", $"deploy response JSON parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Pick the <c>status</c> value an emit node writes into its audit payload.
    /// For a rollback emit, source it from <paramref name="rollbackStatus"/> (the
    /// rollback's own outcome) — an empty value means the rollback hasn't run yet
    /// (the ROLLBACK.STARTED row) so it reads <c>"started"</c>. For a stage emit,
    /// source it from <paramref name="stageResult"/>.
    ///
    /// <para>Fixes the MINOR audit-data bug (2026-06-22): the rollback branch ran
    /// with <c>stageResult == "failed"</c> (the stale prod failure), so
    /// <c>DEPLOY.ROLLBACK.SUCCESS</c> was carrying <c>status:"failed"</c>. Pure +
    /// exposed for unit testing.</para>
    /// </summary>
    public static string SelectEmitStatus(bool isRollback, string? stageResult, string? rollbackStatus)
    {
        if (!isRollback)
            return stageResult ?? "";
        return string.IsNullOrEmpty(rollbackStatus) ? "started" : rollbackStatus;
    }

    /// <summary>
    /// Fail-closed parse of the rollback dispatch result into a rollback status.
    /// Same contract as <see cref="ParseStageStatus"/> — only an explicit
    /// <c>status:"success"</c> is a successful rollback.
    /// </summary>
    private static SetVariable ExtractRollbackResult(
        string id, string displayName,
        Variable<string> rollbackStatus,
        Variable<IDictionary<string, object>?> llmResult)
    {
        var sv = new SetVariable
        {
            Id = id, Name = displayName,
            Variable = rollbackStatus,
            Value = new Input<object?>(ctx =>
            {
                var (status, _) = ParseStageStatus(llmResult.Get(ctx));
                return (object)status;
            })
        };
        sv.SetDisplayText(displayName);
        return sv;
    }

    /// <summary>Deserialize the completed-stages JSON array; empty list on error.</summary>
    private static List<string> ParseCompletedStages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>A <c>FlowDecision</c> True when the stage is still under the retry
    /// cap (FR-16 — bounded retry, then escalate).</summary>
    private static FlowDecision RetryCheck(string id, string name, Variable<int> retries)
    {
        var fd = new FlowDecision(ctx => retries.Get(ctx) < MaxStageRetries) { Id = id, Name = name };
        fd.SetDisplayText(name);
        return fd;
    }

    /// <summary>Increment a stage retry counter before re-dispatching.</summary>
    private static SetVariable IncrementRetry(string id, string name, Variable<int> retries)
    {
        var sv = new SetVariable
        {
            Id = id, Name = name,
            Variable = retries,
            Value = new Input<object?>(ctx => (object)(retries.Get(ctx) + 1)),
        };
        sv.SetDisplayText(name);
        return sv;
    }

    private static SetVariable CreateFailureNode(
        string id, string displayName,
        Variable<string> deploymentStatus, string failedStage)
    {
        var sv = new SetVariable
        {
            Id = id, Name = displayName,
            Variable = deploymentStatus,
            Value = new Input<object?>(_ => (object)$"failed:{failedStage}")
        };
        sv.SetDisplayText(displayName);
        return sv;
    }

    /// <summary>
    /// A <c>DEPLOY.*</c> event-emit node. Threads the issue/repo/sha/stage/mode +
    /// tenant tags and serialises the audit data (status, reason, completedStages,
    /// rollbackStatus, optional approver/feedback/releaseTag) into the
    /// <c>DataJson</c> input so the durable drain persists it.
    ///
    /// <para><paramref name="isRollback"/> sources the payload <c>status</c> from
    /// <c>rollbackStatus</c> (the rollback's own outcome) instead of
    /// <c>stageResult</c> — which in the rollback branch is still the STALE prod
    /// "failed". Without this, <c>DEPLOY.ROLLBACK.SUCCESS</c> would carry
    /// <c>status:"failed"</c> (MINOR audit-data bug, 2026-06-22).</para>
    /// </summary>
    private static EmitDeploymentEventActivity EmitDeployEvent(
        string id, string label, string eventType, string stage,
        Variable<string> repository, Variable<string> mergeSha, Variable<int> issueNumber,
        Variable<string> mode, Variable<string> tenantId,
        Variable<string> stageResult, Variable<string> stageError,
        Variable<string> completedStages, Variable<string> rollbackStatus,
        Variable<string>? approver = null, Variable<string>? feedback = null,
        Variable<string>? releaseTag = null, bool isRollback = false)
    {
        var emit = new EmitDeploymentEventActivity
        {
            Id = id, Name = label,
            EventType = new Input<string>(_ => eventType),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Repository = new Input<string?>(ctx => repository.Get(ctx)),
            MergeSha = new Input<string?>(ctx => mergeSha.Get(ctx)),
            Stage = new Input<string?>(_ => stage),
            Mode = new Input<string?>(ctx => mode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            DataJson = new Input<string?>(ctx =>
            {
                // Rollback emits report the rollback's own status (started rows have
                // no rollbackStatus yet → "started"); stage emits report stageResult.
                var status = SelectEmitStatus(isRollback, stageResult.Get(ctx), rollbackStatus.Get(ctx));
                var data = new Dictionary<string, object?>
                {
                    ["status"] = status,
                    ["completedStages"] = completedStages.Get(ctx),
                };
                var err = stageError.Get(ctx);
                if (!string.IsNullOrEmpty(err)) data["reason"] = err;
                var rb = rollbackStatus.Get(ctx);
                if (!string.IsNullOrEmpty(rb)) data["rollbackStatus"] = rb;
                if (approver != null) data["approver"] = approver.Get(ctx);
                if (feedback != null) data["feedback"] = feedback.Get(ctx);
                if (releaseTag != null)
                {
                    data["releaseTag"] = releaseTag.Get(ctx);
                    data["releaseStatus"] = "deferred"; // P1 item 5 — no real release seam yet.
                }
                return JsonSerializer.Serialize(data);
            }),
        };
        emit.SetDisplayText(label);
        return emit;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
