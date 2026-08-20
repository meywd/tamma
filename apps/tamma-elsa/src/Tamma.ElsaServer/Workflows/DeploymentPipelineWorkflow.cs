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
using Tamma.Activities.Policy;
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
///     production deploy the autonomy gate evaluates <c>effect:deploy.prod</c>
///     against the dial and the DECISION routes on that answer (owner directive
///     2026-08-18: "check the automation level, then go to orchestrator or
///     human"): <c>automated</c> → deploy proceeds; below the dial →
///     <see cref="WaitForDeploymentApprovalActivity"/> (bookmark-based human
///     gate); <c>denied</c> → refusal terminal. An unreadable gate fails CLOSED
///     to the human wait, and <c>requireProdApproval</c> remains an explicit
///     operator override forcing the wait regardless of the dial. Mode no
///     longer forces approval on its own. Approve → ProdDeploy; Reject /
///     Invalid → prod-failure terminal (NEVER a silent prod deploy).</description></item>
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
/// <para><b>Release/tag (Epic 38 follow-up #21 — IMPLEMENTED):</b> after a
/// successful production deploy the pipeline cuts a REAL git-platform release via
/// <see cref="Tamma.Activities.ADL.CreateReleaseActivity"/> — the MEDIATED release
/// step (the engine holds NO git credential; the release create routes through
/// <c>POST /api/v1/git/{owner}/{repo}/releases</c> via <c>TammaApiClient</c>, where
/// the per-tenant token lives, so <c>TAMMA001</c> stays satisfied). The successful
/// terminal now surfaces <c>releaseStatus = "created"</c> + <c>releaseUrl</c>; a
/// release-create failure surfaces <c>releaseStatus = "failed"</c> + a loud
/// <c>RELEASE.CREATED.FAILED</c> event (never silently swallowed) without undoing
/// the successful deploy. P2/P3 items (post-deploy health probe, per-stage timeout,
/// notifications) remain follow-ups.</para>
///
/// Flow:
///   Init (status=failed default) → QA STARTED → QA Deploy (llm-call) → Extract → QA OK?
///     ├─ Yes → QA SUCCESS → UAT STARTED → UAT Deploy → Extract → UAT OK?
///     │   ├─ Yes → UAT SUCCESS → CheckProdDeployGate (43-9 Seam E)
///     │   │   ├─ Denied → SetProdGateDenied → REJECTED → SetProdFailed (NO prod deploy,
///     │   │   │            and NOT the approval wait — see finding F1 at the gate node)
///     │   │   └─ Automated | RequiresHuman → ProdApprovalNeeded?
///     │   │   ├─ Yes(business) → APPROVAL_REQUESTED gate
///     │   │   │     ├─ Approve → APPROVED → Prod STARTED → Prod Deploy → Extract → Prod OK?
///     │   │   │     ├─ Reject  → REJECTED → SetProdFailed
///     │   │   │     └─ Invalid → REJECTED → SetProdFailed
///     │   │   └─ No(dev) → Prod STARTED → Prod Deploy → Extract → Prod OK?
///     │   │         ├─ Yes → Prod SUCCESS → Compute Tag → CreateRelease (mediated) → PIPELINE.SUCCESS (releaseStatus=created|failed) → Output
///     │   │         └─ No  → Prod FAILED → Rollback (ROLLBACK.*) → SetProdFailed
///     │   └─ No → UAT FAILED → SetUATFailed
///     └─ No → QA FAILED → SetQAFailed
///   (each SetXxxFailed → PIPELINE.FAILED → Output → Finish)
///
/// Inputs: repository, mergeSha, issueNumber, branchName, mode, tenantId, requireProdApproval
/// Outputs: deploymentStatus (success/failed:&lt;stage&gt;), completedStages (JSON array),
///          releaseTag, releaseStatus (created|failed), releaseUrl, rollbackStatus
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
        builder.Description = "Deploy through QA -> UAT -> Prod with a fail-closed gate per stage, a Business-Mode production approval gate, rollback on prod failure, a full DCB audit trail, and a real git-platform release cut via the mediated integration seam after a successful prod deploy.";

        // Fail-closed on internal fault — a faulted activity must not halt the
        // instance with no output. Continue-with-incidents keeps the flow alive;
        // `deploymentStatus` is seeded to "failed" at Init so a fault that stops
        // the flow before a terminal still yields a fail-closed status the parent
        // cycle reads (never a silent success). Mirrors MergeApprovalWorkflow I1.
        builder.WorkflowOptions.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy);

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "").Persisted();
        var mergeSha = builder.WithVariable<string>("MergeSha", "").Persisted();
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0).Persisted();
        var branchName = builder.WithVariable<string>("BranchName", "").Persisted();
        var mode = builder.WithVariable<string>("Mode", "").Persisted();
        var tenantId = builder.WithVariable<string>("TenantId", "").Persisted();
        var requireProdApproval = builder.WithVariable<bool>("RequireProdApproval", false).Persisted();

        // Fail-closed default — overwritten only by an explicit terminal.
        var deploymentStatus = builder.WithVariable<string>("DeploymentStatus", "failed").Persisted();
        var completedStages = builder.WithVariable<string>("CompletedStages", "[]").Persisted();
        var currentStage = builder.WithVariable<string>("CurrentStage", "").Persisted();
        var stageResult = builder.WithVariable<string>("StageResult", "").Persisted();
        var stageError = builder.WithVariable<string>("StageError", "").Persisted();
        var rollbackStatus = builder.WithVariable<string>("RollbackStatus", "").Persisted();
        var releaseTag = builder.WithVariable<string>("ReleaseTag", "").Persisted();
        // Epic 38 follow-up #21 — the real release-step outputs (was the hardcoded
        // "deferred"). CreateReleaseActivity sets these on its outcome.
        var releaseStatus = builder.WithVariable<string>("ReleaseStatus", "").Persisted();
        var releaseUrl = builder.WithVariable<string>("ReleaseUrl", "").Persisted();
        // #21 audit fidelity — CreateRelease's ErrorCode is captured in its OWN
        // variable, kept SEPARATE from the shared `stageError` that seeds a
        // PIPELINE.SUCCESS `reason`. A release-step failure is surfaced via
        // releaseStatus="failed" + the loud RELEASE.CREATED.FAILED event, never as the
        // success event's reason (which stays reserved for genuine stage failures).
        var releaseError = builder.WithVariable<string>("ReleaseError", "").Persisted();

        var decisionVar = builder.WithVariable<string>("Decision", "").Persisted();
        var feedbackVar = builder.WithVariable<string>("Feedback", "").Persisted();
        var approverVar = builder.WithVariable<string>("Approver", "").Persisted();

        // Per-stage retry counters (FR-16 — bounded retry + escalation).
        var qaRetries = builder.WithVariable<int>("QaRetries", 0).Persisted();
        var uatRetries = builder.WithVariable<int>("UatRetries", 0).Persisted();
        var prodRetries = builder.WithVariable<int>("ProdRetries", 0).Persisted();

        var llmResult = builder.WithVariable<IDictionary<string, object>?>().Persisted();

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
        // 4. Production approval gate (P0 item 3) — the AUTONOMY DIAL decides;
        //    the human wait is the below-dial outcome, not the unconditional one.
        // ================================================================
        // ── Story 43-9 Seam E (AC11, D10), re-based on the owner directive of
        //    2026-08-18: "it needs to check the automation level and then go to
        //    orchestrator or human."
        //
        //    ON THE EFFECT, NOT THE AGENT-ACTION. StageDeployDispatch (below) is
        //    SHARED across qa / uat / production, so one `agent-action:deploy`
        //    member cannot tell a staging deploy from a production one. Gating
        //    `effect:deploy.prod` at the prod-approval DECISION can — and
        //    it is what the admin's deploy-control dial actually names. The shared
        //    dispatch is deliberately NOT gated (pinned by
        //    Gate_is_on_the_effect_not_the_shared_dispatch).
        //
        //    THE DIAL DECIDES (2026-08-18; supersedes 43-9's "BY OR, NEVER BY
        //    REPLACEMENT"). 43-9 adopted the gate additively — `mode == business`
        //    kept forcing a human wait no matter what the dial said, because
        //    removing an existing gate term was outside that story's authority.
        //    The owner has now directed the replacement: the gate's answer routes
        //    the decision. `automated` (dial >= the action's level — default dial
        //    70 vs deploy.prod's 90 — or an admin's observe-only Enforce=false) →
        //    production proceeds under the orchestrator; anything the gate blocks
        //    → the existing human wait; `denied` → the refusal terminal. Mode is
        //    audit/event context now, not a gate term.
        //
        //    THE FAIL POSTURE FLIPS WITH THE AUTHORITY. While business mode was
        //    the unconditional backstop, an UNREADABLE gate could fail open — a
        //    null answer contributed nothing and the mode term still protected
        //    production. With the dial as the decider there is no backstop behind
        //    it, so `unavailable` (and the unwritten "") now routes to the HUMAN
        //    wait: absence of evidence that automation was granted is not a
        //    grant. Same rule as ResolveMode's null-config arm (finding 36).
        //
        //    `requireProdApproval` SURVIVES as the explicit operator override —
        //    config that forces the wait even at a dial that automates. It can
        //    only ADD a wait, never remove one.
        //
        // ── 2026-08-01 review finding F1 — A DENIAL IS NOT AN ESCALATION ──
        //    As shipped, this seam had a MONOTONICITY INVERSION. The activity
        //    folded `denied` onto its RequiresHuman EDGE, but BOTH edges were wired
        //    to `prodApprovalNeeded`, so the edge choice was behaviourally inert;
        //    the only thing that routed was `gateOutcome`, and the predicate
        //    compared it against "requires-human" ONLY. Result, proved by invoking
        //    the real FlowDecision delegate:
        //        dev / requireProdApproval=false / "requires-human" -> True  (wait)
        //        dev / requireProdApproval=false / "denied"         -> False (NO WAIT)
        //    i.e. setting effect:deploy.prod to AlwaysHuman added a
        //    production wait, but DISABLING the action — the strictly stronger
        //    admin setting — added nothing and production deployed with no human.
        //    (`denied` is reachable here two ways: an Enabled=false row on the
        //    action or its deploy-control group, and ANY AllowedRoles restriction,
        //    because this call passes Role unset so every restriction excludes it.)
        //
        //    THE FIX, AND WHY IT IS A HARD STOP RATHER THAN A WAIT. `denied` now
        //    takes its own edge into a REFUSAL terminal, not into
        //    WaitForDeploymentApprovalActivity. A denial is not "a person may
        //    approve this": the human on that wait is approving a DEPLOYMENT, and
        //    letting them approve past an action an admin DISABLED would make the
        //    standing approval flow an override for governance — the deploy-control
        //    dial would be advisory. So a denial ends the pipeline at
        //    PRODUCTION.REJECTED → SetProdFailed → PIPELINE.FAILED: loud, terminal,
        //    attributable (the gate's reason rides `stageError` into both events),
        //    and with no production deploy.
        //
        //    THE PREDICATE STILL TREATS `denied` AS BLOCKING TOO. That term is a
        //    safety net, not the routing: today the Denied edge means the predicate
        //    never sees `denied`. But the whole defect above was a predicate that
        //    silently disagreed with the edges, so the predicate is now monotone on
        //    its own — if a future author re-points the Denied edge back at this
        //    decision, the worst case is an extra human wait, never a free deploy.
        var gateOutcome = builder.WithVariable<string>("ProdGateOutcome", "").Persisted();
        var gateReason = builder.WithVariable<string>("ProdGateReason", "").Persisted();
        // Enforced=false is the admin's explicit "report but do not block"
        // (observe-only), and the DECISION must see it, not just the edge — the
        // predicate would otherwise block on the raw requires-human wire the
        // admin asked to only observe. Defaults TRUE so an unwritten value can
        // never read as observe-only (fail closed).
        var gateEnforced = builder.WithVariable<bool>("ProdGateEnforced", true).Persisted();
        var checkProdGate = new CheckActionGateActivity
        {
            Id = "CheckProdDeployGate", Name = "Check Prod Deploy Gate",
            // Story 43-12 — rebound from the retired coarse effect:deploy.promote-prod
            // to the per-target effect:deploy.prod (zone level 90). Behaviour-identical
            // at every dial position (the descriptor grading is carried verbatim).
            ActionKey = new Input<string>("effect:deploy.prod"),
            Operation = new Input<string?>("deployment-pipeline:prod-approval"),
            Target = new Input<string?>(ctx => repository.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            // CorrelationId is left unset ON PURPOSE: the activity defaults it to
            // its own WorkflowExecutionContext.Id, which is the pipeline INSTANCE
            // id — stable across every retry inside this run, which is exactly the
            // scope one human grant must cover. (An Input<> lambda receives an
            // ExpressionExecutionContext and cannot reach the workflow context, so
            // spelling it here is not merely redundant, it is not expressible.)
            Outcome = new Output<string?>(gateOutcome),
            Enforced = new Output<bool>(gateEnforced),
            // Bound so a refusal can NAME what refused it: `denied` with no reason
            // is an operator staring at a stopped pipeline with nothing to act on.
            Reason = new Output<string?>(gateReason),
        };
        checkProdGate.SetDisplayText("Check Prod Deploy Gate");

        var prodApprovalNeeded = new FlowDecision(ctx =>
            // Operator override first: config can force a human even at a dial
            // that automates. Then the dial's answer routes, through the pure
            // helper below — automation must be POSITIVELY granted, and anything
            // unreadable or unknown lands on the human wait.
            requireProdApproval.Get(ctx)
            || !ProductionGateAutomates(gateEnforced.Get(ctx), gateOutcome.Get(ctx)))
        { Id = "ProdApprovalNeeded", Name = "Prod Approval Needed?" };
        prodApprovalNeeded.SetDisplayText("Prod Approval Needed?");

        // The refusal terminal for a DENIED gate resolution. It writes the gate's
        // reason into the shared stage-error variable, which EmitDeployEvent maps
        // onto the audit payload's `reason` — so both PRODUCTION.REJECTED and the
        // PIPELINE.FAILED terminal say WHY production was refused.
        var setProdGateDenied = new SetVariable
        {
            Id = "SetProdGateDenied", Name = "Prod Gate Denied",
            Variable = stageError,
            Value = new Input<object?>(ctx =>
            {
                var reason = gateReason.Get(ctx);
                return (object)("production promotion refused by the autonomy gate"
                    + (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason.Trim()}"));
            })
        };
        setProdGateDenied.SetDisplayText("Prod Gate Denied");

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
        // 7. Success terminal — compute the release tag, CUT the real release
        //    (Epic 38 follow-up #21, mediated), emit PIPELINE.SUCCESS, status=success.
        // ================================================================
        var setReleaseTag = new SetVariable
        {
            Id = "SetReleaseTag", Name = "Compute Release Tag",
            Variable = releaseTag,
            // Compute the version tag from the merged SHA; CreateRelease (below) cuts
            // the real git-platform release from it via the mediated integration seam.
            Value = new Input<object?>(ctx =>
            {
                var sha = mergeSha.Get(ctx) ?? "";
                var shortSha = sha.Length >= 7 ? sha[..7] : sha;
                return (object)(string.IsNullOrEmpty(shortSha) ? "" : $"deploy-{shortSha}");
            })
        };
        setReleaseTag.SetDisplayText("Compute Release Tag");

        // Epic 38 follow-up #21 — the real release step. Cuts a git-platform release
        // for the shipped version through the MEDIATED integration seam (the engine
        // holds NO git credential — the release create routes through
        // POST /api/v1/git/{owner}/{repo}/releases via TammaApiClient, where the
        // per-tenant token lives). Emits RELEASE.CREATED.SUCCESS/FAILED itself.
        var createRelease = new CreateReleaseActivity
        {
            Id = "CreateRelease", Name = "Create Release",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            TagName = new Input<string>(ctx => releaseTag.Get(ctx)),
            TargetRef = new Input<string?>(ctx => mergeSha.Get(ctx)),
            ReleaseName = new Input<string?>(ctx => $"Release {releaseTag.Get(ctx)}"),
            Body = new Input<string?>(ctx =>
            {
                var sha = mergeSha.Get(ctx) ?? "";
                var shortSha = sha.Length >= 7 ? sha[..7] : sha;
                var issue = issueNumber.Get(ctx);
                var stages = completedStages.Get(ctx);
                return $"Automated release for the shipped version." +
                       (issue > 0 ? $" Resolves #{issue}." : "") +
                       (string.IsNullOrEmpty(shortSha) ? "" : $"\n\nMerge commit: `{shortSha}`.") +
                       $"\n\nDeployed through: {stages}.";
            }),
            Draft = new Input<bool>(false),
            Prerelease = new Input<bool>(false),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            ReleaseUrl = new Output<string?>(releaseUrl),
            ReleaseTag = new Output<string?>(releaseTag),
            // #21 — route the release-step error into its OWN variable, NOT the shared
            // `stageError`. That keeps a PIPELINE.SUCCESS event's `reason` reserved for
            // genuine STAGE failures; the release failure stays observable via
            // releaseStatus="failed" + the loud RELEASE.CREATED.FAILED DCB event.
            ErrorCode = new Output<string?>(releaseError),
        };
        createRelease.SetDisplayText("Create Release");

        // The deploy itself already succeeded; a release-create failure is surfaced
        // (releaseStatus=failed + the loud RELEASE.CREATED.FAILED event the activity
        // emits) but does NOT flip the deploy to failed — never silently swallowed.
        var setReleaseCreated = new SetVariable
        {
            Id = "SetReleaseCreated", Name = "Release Created",
            Variable = releaseStatus,
            Value = new Input<object?>(_ => (object)"created")
        };
        setReleaseCreated.SetDisplayText("Release Created");

        var setReleaseFailed = new SetVariable
        {
            Id = "SetReleaseFailed", Name = "Release Failed",
            Variable = releaseStatus,
            Value = new Input<object?>(_ => (object)"failed")
        };
        setReleaseFailed.SetDisplayText("Release Failed");

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
            releaseTag: releaseTag, releaseStatus: releaseStatus, releaseUrl: releaseUrl);

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
                // Epic 38 follow-up #21 — the REAL release status (created|failed),
                // replacing the prior hardcoded "deferred", plus the release URL.
                new SetOutput
                    { Id = "OutReleaseStatus", OutputName = new("releaseStatus"), OutputValue = new(ctx => (object)releaseStatus.Get(ctx)) },
                new SetOutput
                    { Id = "OutReleaseUrl", OutputName = new("releaseUrl"), OutputValue = new(ctx => (object)releaseUrl.Get(ctx)) },
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
                // Prod approval gate (+ 43-9 Seam E's additive check)
                checkProdGate, setProdGateDenied,
                prodApprovalNeeded, waitProdApproval, emitProdApproved, emitProdRejected,
                // Prod
                emitProdStarted, prodDeployCall, extractProdResult, prodOk, emitProdSuccess,
                prodRetryCheck, prodIncrement, emitProdFailed,
                // Rollback
                emitRollbackStarted, rollbackCall, extractRollbackResult, rollbackOk,
                emitRollbackSuccess, emitRollbackFailed,
                // Success terminal (+ Epic 38 follow-up #21 release step)
                setReleaseTag, createRelease, setReleaseCreated, setReleaseFailed,
                setSuccess, emitPipelineSuccess,
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
                // 43-9 Seam E — the gate check sits BETWEEN the UAT success event
                // and the approval decision, so the decision reads a variable that
                // has already been written. Automated and RequiresHuman converge on
                // the SAME next node: for those two the activity's job is to SET
                // `gateOutcome` and the routing decision stays where it already was
                // — splitting them would be a second, competing prod gate.
                //
                // DENIED DOES NOT CONVERGE (F1). It is the one resolution the
                // approval decision must not be allowed to answer, because the only
                // answer it has is "a human may approve", and a denial is precisely
                // the case where no human on this graph may. It goes to a refusal
                // terminal instead. All three edges are wired — pinned by
                // DeploymentPipelineGateTests.EveryGateOutcome_isWired_noDanglingEdge.
                Connect(emitUatSuccess, checkProdGate),
                ConnectOutcome(checkProdGate, CheckActionGateActivity.EdgeAutomated, prodApprovalNeeded),
                ConnectOutcome(checkProdGate, CheckActionGateActivity.EdgeRequiresHuman, prodApprovalNeeded),
                ConnectOutcome(checkProdGate, CheckActionGateActivity.EdgeDenied, setProdGateDenied),
                Connect(setProdGateDenied, emitProdRejected),
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

                // ── Success terminal (+ Epic 38 follow-up #21 release step) ──
                // Prod succeeded → compute tag → cut the real release (mediated).
                // A release failure is surfaced (releaseStatus=failed + the loud
                // RELEASE.CREATED.FAILED event) but does NOT undo the successful
                // deploy — both outcomes proceed to the success terminal.
                Connect(setReleaseTag, createRelease),
                ConnectOutcome(createRelease, "Created", setReleaseCreated),
                ConnectOutcome(createRelease, "Error", setReleaseFailed),
                Connect(setReleaseCreated, setSuccess),
                Connect(setReleaseFailed, setSuccess),
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

    /// <summary>
    /// The gate half of the production-approval predicate (owner directive
    /// 2026-08-18: the automation level decides; replaces the additive
    /// <c>IsBlockingGateOutcome</c> of Story 43-9/F1). Returns true only when
    /// automation is POSITIVELY granted — everything else is the human wait.
    ///
    /// <list type="bullet">
    ///   <item><c>automated</c> → true: the dial (or an admin action row) grants
    ///   the orchestrator this deploy.</item>
    ///   <item><c>unavailable</c> and the unwritten <c>""</c> → false. This is
    ///   the fail-posture flip that comes with the dial being the DECIDER rather
    ///   than an additive term: there is no business-mode backstop behind it any
    ///   more, so an unreadable gate cannot be a grant.</item>
    ///   <item><c>!enforced</c> (with a READABLE wire) → true. Observe-only is
    ///   the admin's explicit "report but do not block" and is honoured for every
    ///   real decision INCLUDING an unenforced denial — SelectEdge routes that
    ///   combination down the Automated edge, so this predicate is its only
    ///   decider, and parking it at an approvable human wait would be neither
    ///   observe-only nor F1's hard stop.</item>
    ///   <item>ENFORCED <c>denied</c> → false. Safety net only — the activity's
    ///   Denied EDGE routes to the refusal terminal before the predicate runs,
    ///   but F1 proved what happens when a predicate silently disagrees with its
    ///   edges: if a future author re-points that edge back here, the worst case
    ///   is a human wait, never a free deploy.</item>
    ///   <item>Any other ENFORCED wire this build does not recognise fails
    ///   closed onto the wait.</item>
    /// </list>
    /// Public + pure so the test drives the same function the workflow does.
    /// </summary>
    public static bool ProductionGateAutomates(bool enforced, string? outcomeWire)
    {
        var wire = outcomeWire?.Trim();

        // Unreadable first, unconditionally: the activity's error arm writes
        // "unavailable" WITH Enforced=false, and an error posture must never be
        // mistaken for the admin's observe-only. Fail closed.
        if (string.IsNullOrEmpty(wire)
            || string.Equals(wire, CheckActionGateActivity.OutcomeUnavailable, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Observe-only next — BEFORE the denied arm (review finding, 2026-08-19:
        // checking denied first sent an unenforced denial to the approvable human
        // wait, which is neither observe-only's pass-through nor F1's hard stop,
        // and let a human approve past a denied action). An unenforced denial is
        // reachable: an Enabled=false row or any AllowedRoles restriction under an
        // admin's Enforce=false resolves to denied/unenforced, and SelectEdge
        // honours observe-only first, so it arrives here on the Automated edge.
        // "Report but do not block" means exactly that — the denial is in the
        // audit row, and production proceeds.
        if (!enforced)
        {
            return true;
        }

        if (string.Equals(wire, GovernanceEvaluateResponse.OutcomeDenied, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(wire, GovernanceEvaluateResponse.OutcomeAutomated, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // An ENFORCED wire this build does not recognise fails closed onto the wait.
        return false;
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
        Variable<string>? releaseTag = null, Variable<string>? releaseStatus = null,
        Variable<string>? releaseUrl = null, bool isRollback = false)
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
                // `reason` is sourced ONLY from the shared stage-error variable: a
                // release-step failure is surfaced via releaseStatus="failed" (+ the
                // loud RELEASE.CREATED.FAILED event), NOT here — so a PIPELINE.SUCCESS
                // event's reason stays reserved for genuine stage failures (#21).
                var data = BuildDeployEventData(
                    status,
                    completedStages.Get(ctx),
                    reason: stageError.Get(ctx),
                    rollbackStatus: rollbackStatus.Get(ctx),
                    approver: approver != null ? approver.Get(ctx) : null,
                    feedback: feedback != null ? feedback.Get(ctx) : null,
                    releaseTag: releaseTag != null ? releaseTag.Get(ctx) : null,
                    releaseStatus: releaseStatus != null ? releaseStatus.Get(ctx) : null,
                    releaseUrl: releaseUrl != null ? releaseUrl.Get(ctx) : null);
                return JsonSerializer.Serialize(data);
            }),
        };
        emit.SetDisplayText(label);
        return emit;
    }

    /// <summary>
    /// Build the audit-data dictionary an <see cref="EmitDeploymentEventActivity"/>
    /// node serialises into its <c>DataJson</c> payload. Pure (no Elsa context) —
    /// exposed for unit testing.
    ///
    /// <para><b>#21 audit fidelity:</b> <paramref name="reason"/> is sourced ONLY from
    /// the shared stage-error variable — a genuine STAGE/deploy failure. A release-step
    /// failure is surfaced via <paramref name="releaseStatus"/><c>="failed"</c> (+ the
    /// loud <c>RELEASE.CREATED.FAILED</c> event) and NEVER as <paramref name="reason"/>,
    /// so a <c>PIPELINE.SUCCESS</c> event (<c>status:"success"</c>) can never carry a
    /// misleading release-step error as its reason.</para>
    ///
    /// <para>Presence rules mirror the emit node exactly: <paramref name="reason"/> /
    /// <paramref name="rollbackStatus"/> / <paramref name="releaseStatus"/> /
    /// <paramref name="releaseUrl"/> are omitted when null-or-empty; the optional
    /// <paramref name="approver"/> / <paramref name="feedback"/> / <paramref name="releaseTag"/>
    /// are included whenever supplied (non-null), even if empty.</para>
    /// </summary>
    public static Dictionary<string, object?> BuildDeployEventData(
        string status,
        string completedStages,
        string? reason,
        string? rollbackStatus,
        string? approver = null,
        string? feedback = null,
        string? releaseTag = null,
        string? releaseStatus = null,
        string? releaseUrl = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["status"] = status,
            ["completedStages"] = completedStages,
        };
        if (!string.IsNullOrEmpty(reason)) data["reason"] = reason;
        if (!string.IsNullOrEmpty(rollbackStatus)) data["rollbackStatus"] = rollbackStatus;
        if (approver != null) data["approver"] = approver;
        if (feedback != null) data["feedback"] = feedback;
        if (releaseTag != null) data["releaseTag"] = releaseTag;
        if (!string.IsNullOrEmpty(releaseStatus)) data["releaseStatus"] = releaseStatus;
        if (!string.IsNullOrEmpty(releaseUrl)) data["releaseUrl"] = releaseUrl;
        return data;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
