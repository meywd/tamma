using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Build-out structure coverage for the <c>deployment-pipeline</c> workflow
/// (completeness audit 2026-06-22). Asserts the load-bearing guarantees of the
/// P0/P1 build-out by inspecting the BUILT Flowchart (the codebase convention —
/// see MergeApprovalWorkflowTests) rather than running the full Elsa runtime:
///   - P0.1 fail-closed: a failed stage routes through retry → loud FAILED →
///     terminal, never silently to the next stage or to success;
///   - P0.2 every edge emits a DEPLOY.* DCB event;
///   - P0.3 a Business-Mode production approval gate gates the prod deploy
///     (Approve → prod; Reject/Invalid → prod-failure, never a silent prod deploy);
///   - P0.4 a failed prod deploy routes through a rollback branch;
///   - P1.6 each stage retries (bounded) before failing.
/// </summary>
[TestFixture]
public class DeploymentPipelineWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DeploymentPipelineWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DeploymentPipelineWorkflow());
        builder.Object.DefinitionId.Should().Be("deployment-pipeline");
    }

    [Test]
    public void Workflow_UsesContinueWithIncidentsStrategy_SoAFaultDoesNotHaltSilently()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DeploymentPipelineWorkflow());
        builder.Object.WorkflowOptions.IncidentStrategyType
            .Should().Be(typeof(Elsa.Workflows.IncidentStrategies.ContinueWithIncidentsStrategy),
                "a faulted deploy stage must not halt the pipeline with an incident and report no status");
    }

    [Test]
    public void DeploymentStatus_DefaultsToFailed_FailClosed()
    {
        // P0.1 — the status variable must default to "failed" so a fault before a
        // terminal never yields a silent success. The variables are tracked on the
        // builder (the mock collects every WithVariable call).
        var builder = WorkflowTestHelper.BuildWorkflow(new DeploymentPipelineWorkflow());
        var statusVar = builder.Object.Variables.FirstOrDefault(v => v.Name == "DeploymentStatus");
        statusVar.Should().NotBeNull();
        statusVar!.Value.Should().Be("failed",
            "the deployment status must be fail-closed by default (never an optimistic success)");
    }

    // ================================================================
    // P0.1 — fail-closed stage gating: each stage decides on `== "success"`,
    // a non-success routes to retry/failure, NEVER straight to the next stage.
    // ================================================================

    [Test]
    public void EachStageDecision_PromotesOnlyOnExplicitSuccess()
    {
        // The FlowDecision conditions are opaque lambdas, but their wiring is not:
        // the True edge of each *Ok decision goes to the stage SUCCESS emit, the
        // False edge goes to that stage's retry check (then failure). A non-success
        // can therefore never reach the next stage's deploy directly.
        HasEdge("QAOk", "True", "EmitQaSuccess").Should().BeTrue();
        HasEdge("QAOk", "False", "QaRetryCheck").Should().BeTrue();
        HasEdge("UATOk", "True", "EmitUatSuccess").Should().BeTrue();
        HasEdge("UATOk", "False", "UatRetryCheck").Should().BeTrue();
        HasEdge("ProdOk", "True", "EmitProdSuccess").Should().BeTrue();
        HasEdge("ProdOk", "False", "ProdRetryCheck").Should().BeTrue();
    }

    [Test]
    public void QaFailure_NeverReachesUatOrSuccess()
    {
        // A QA failure (retry exhausted) must reach the QA failure terminal +
        // PIPELINE.FAILED, and must NOT reach UAT deploy, prod deploy, or success.
        var reach = ReachableFromPort("QaRetryCheck", "False");
        reach.Should().Contain("SetQAFailed");
        reach.Should().Contain("EmitPipelineFailed");
        reach.Should().NotContain("UATDeploy", "a failed QA must never deploy UAT");
        reach.Should().NotContain("ProdDeploy", "a failed QA must never deploy prod");
        reach.Should().NotContain("SetSuccess", "a failed QA must never reach success");
        reach.Should().NotContain("EmitPipelineSuccess");
    }

    [Test]
    public void UatFailure_NeverReachesProdOrSuccess()
    {
        var reach = ReachableFromPort("UatRetryCheck", "False");
        reach.Should().Contain("SetUATFailed");
        reach.Should().Contain("EmitPipelineFailed");
        reach.Should().NotContain("ProdDeploy", "a failed UAT must never deploy prod");
        reach.Should().NotContain("SetSuccess");
    }

    [Test]
    public void ParseStageStatus_IsFailClosed_OnMissingEmptyOrGarbledResult()
    {
        // P0.1 regression (audit item 1 / spec test 13): the OLD code defaulted to
        // "success" and swallowed parse failures. These must ALL be "failed" now.
        DeploymentPipelineWorkflow.ParseStageStatus(null).Status.Should().Be("failed",
            "a null result must NOT promote (silent false success)");

        DeploymentPipelineWorkflow.ParseStageStatus(
            new Dictionary<string, object>()).Status.Should().Be("failed",
            "a result missing llmResponse must NOT promote");

        DeploymentPipelineWorkflow.ParseStageStatus(
            new Dictionary<string, object> { ["llmResponse"] = "" }).Status.Should().Be("failed",
            "an empty llmResponse must NOT promote");

        DeploymentPipelineWorkflow.ParseStageStatus(
            new Dictionary<string, object> { ["llmResponse"] = "this is not json at all" })
            .Status.Should().Be("failed", "a non-JSON response must NOT promote");

        DeploymentPipelineWorkflow.ParseStageStatus(
            new Dictionary<string, object> { ["llmResponse"] = "{\"foo\":\"bar\"}" })
            .Status.Should().Be("failed", "a JSON response with no status field must NOT promote");

        DeploymentPipelineWorkflow.ParseStageStatus(
            new Dictionary<string, object> { ["llmResponse"] = "{\"status\":\"failed\"}" })
            .Status.Should().Be("failed");
    }

    [Test]
    public void ParseStageStatus_PromotesOnlyOnExplicitSuccess()
    {
        DeploymentPipelineWorkflow.ParseStageStatus(
            new Dictionary<string, object> { ["llmResponse"] = "Deploying...\n{\"status\":\"success\"}\nDone." })
            .Status.Should().Be("success", "an explicit status:success embedded in agent text must promote");

        DeploymentPipelineWorkflow.ParseStageStatus(
            new Dictionary<string, object> { ["llmResponse"] = "{\"status\":\"SUCCESS\"}" })
            .Status.Should().Be("success", "status matching is case-insensitive");
    }

    // ================================================================
    // P0.2 — DCB audit events on every meaningful edge.
    // ================================================================

    [Test]
    public void EveryStage_EmitsStartedSuccessAndFailedEvents()
    {
        var emitIds = _flowchart.Activities
            .OfType<EmitDeploymentEventActivity>()
            .Select(a => a.Id)
            .ToList();

        foreach (var prefix in new[] { "Qa", "Uat", "Prod" })
        {
            emitIds.Should().Contain($"Emit{prefix}Started");
            emitIds.Should().Contain($"Emit{prefix}Success");
            emitIds.Should().Contain($"Emit{prefix}Failed");
        }
    }

    [Test]
    public void PipelineTerminals_EmitSuccessAndFailedEvents()
    {
        var emitIds = _flowchart.Activities
            .OfType<EmitDeploymentEventActivity>()
            .Select(a => a.Id)
            .ToList();

        emitIds.Should().Contain("EmitPipelineSuccess");
        emitIds.Should().Contain("EmitPipelineFailed");
    }

    [Test]
    public void StartEdge_EmitsStageStarted_BeforeDispatch()
    {
        HasEdge("Init", null, "EmitQaStarted").Should().BeTrue("the pipeline must emit STAGE.STARTED before the first dispatch");
        HasEdge("EmitQaStarted", null, "QADeploy").Should().BeTrue();
    }

    // ================================================================
    // P0.3 — production approval gate (Business Mode).
    // ================================================================

    [Test]
    public void ProdApprovalGate_ExistsAsBookmarkActivity_WithThreeOutcomes()
    {
        var gate = _flowchart.Activities
            .OfType<WaitForDeploymentApprovalActivity>()
            .FirstOrDefault(a => a.Id == "WaitProdApproval");
        gate.Should().NotBeNull("a bookmark-based production approval gate must exist before prod");

        var ports = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "WaitProdApproval")
            .Select(c => c.Source.Port)
            .ToList();
        ports.Should().Contain("Approve");
        ports.Should().Contain("Reject");
        ports.Should().Contain("Invalid");
    }

    [Test]
    public void ApprovalNeeded_GatesProd_DevModeBypassesToProd()
    {
        // After UAT success the pipeline decides whether prod needs approval.
        // 43-9 AC11 inserted the governance gate BETWEEN the two, so this is now a
        // two-hop path rather than a direct edge. Asserting the hops AND every one of
        // the gate's outcomes is strictly stronger than the single edge this replaced:
        // it also pins that no outcome can dead-end the pipeline after UAT.
        HasEdge("EmitUatSuccess", null, "CheckProdDeployGate").Should().BeTrue(
            "UAT success consults the prod-deploy gate before the approval decision");
        HasEdge("CheckProdDeployGate", "Automated", "ProdApprovalNeeded").Should().BeTrue(
            "an automated gate resolution still reaches the pre-existing approval decision");
        HasEdge("CheckProdDeployGate", "RequiresHuman", "ProdApprovalNeeded").Should().BeTrue(
            "a requires-human gate resolution routes into the SAME decision — the gate ADDS "
            + "a term to the approval predicate, it never replaces or bypasses the decision");
        // 2026-08-01 finding F1 — the THIRD outcome, and the one that must NOT reach
        // this decision. A denial (the action disabled, or an AllowedRoles restriction)
        // is not something the deployment-approval human may approve past, so it goes
        // to the refusal terminal. Detail lives in DeploymentPipelineGateTests.
        HasEdge("CheckProdDeployGate", "Denied", "SetProdGateDenied").Should().BeTrue(
            "a denied gate resolution is a hard refusal with its own terminal");
        HasEdge("CheckProdDeployGate", "Denied", "ProdApprovalNeeded").Should().BeFalse(
            "and it must NOT be answerable by the standing approval flow");
        // Business mode (True) → the human gate; dev mode (False) → straight to prod start.
        HasEdge("ProdApprovalNeeded", "True", "WaitProdApproval").Should().BeTrue(
            "Business Mode must route to the human approval gate before prod");
        HasEdge("ProdApprovalNeeded", "False", "EmitProdStarted").Should().BeTrue(
            "dev mode deploys to prod without a human gate");
    }

    [Test]
    public void ApprovalApprove_ProceedsToProd_RejectAndInvalidDoNot()
    {
        HasEdge("WaitProdApproval", "Approve", "EmitProdApproved").Should().BeTrue();
        HasEdge("EmitProdApproved", null, "EmitProdStarted").Should().BeTrue(
            "an approved prod deploy proceeds to the prod stage");

        // Reject and Invalid must BOTH emit a loud PRODUCTION.REJECTED and route to
        // the prod-failure terminal — NEVER to a prod deploy.
        HasEdge("WaitProdApproval", "Reject", "EmitProdRejected").Should().BeTrue();
        HasEdge("WaitProdApproval", "Invalid", "EmitProdRejected").Should().BeTrue();
        HasEdge("EmitProdRejected", null, "SetProdFailed").Should().BeTrue();

        var rejectReach = Reachable("EmitProdRejected");
        rejectReach.Should().NotContain("ProdDeploy",
            "a rejected/invalid prod approval must never reach the prod deploy");
        rejectReach.Should().NotContain("SetSuccess");
    }

    [Test]
    public void ApprovalGate_NormalizeIsFailClosed_UnknownIsInvalidNotApprove()
    {
        WaitForDeploymentApprovalActivity.Normalize("approve").Outcome.Should().Be("Approve");
        WaitForDeploymentApprovalActivity.Normalize("reject").Outcome.Should().Be("Reject");
        foreach (var bad in new[] { null, "", "  ", "yes", "ok", "deploy" })
        {
            WaitForDeploymentApprovalActivity.Normalize(bad).Outcome.Should().Be("Invalid",
                "an unknown/empty decision must be Invalid — never a silent approve");
        }
    }

    // ================================================================
    // P0.4 — rollback on production failure.
    // ================================================================

    [Test]
    public void ProdFailure_RoutesThroughRollbackBranch_BeforeFailing()
    {
        // Exhausted prod retries → loud PROD FAILED → rollback started → rollback
        // dispatch → extract → rollback OK? → (success or failed) → SetProdFailed.
        HasEdge("ProdRetryCheck", "False", "EmitProdFailed").Should().BeTrue();
        HasEdge("EmitProdFailed", null, "EmitRollbackStarted").Should().BeTrue(
            "a failed prod deploy must trigger a rollback, not just stop");
        HasEdge("EmitRollbackStarted", null, "RollbackProd").Should().BeTrue();
        HasEdge("RollbackProd", null, "ExtractRollback").Should().BeTrue();
        HasEdge("ExtractRollback", null, "RollbackOk").Should().BeTrue();
        HasEdge("RollbackOk", "True", "EmitRollbackSuccess").Should().BeTrue();
        HasEdge("RollbackOk", "False", "EmitRollbackFailed").Should().BeTrue();
        HasEdge("EmitRollbackSuccess", null, "SetProdFailed").Should().BeTrue();
        HasEdge("EmitRollbackFailed", null, "SetProdFailed").Should().BeTrue();
    }

    [Test]
    public void RollbackDispatch_UsesLlmCallMediation_NotADirectProvider()
    {
        // Mediation rule — the rollback step must dispatch llm-call, never a provider.
        var rollback = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "RollbackProd");
        rollback.Should().NotBeNull();
        ReadDefinitionId(rollback!).Should().Be("llm-call",
            "rollback must route through the llm-call mediation endpoint");
    }

    [Test]
    public void RollbackEvents_AreEmitted()
    {
        var emitIds = _flowchart.Activities
            .OfType<EmitDeploymentEventActivity>()
            .Select(a => a.Id)
            .ToList();
        emitIds.Should().Contain("EmitRollbackStarted");
        emitIds.Should().Contain("EmitRollbackSuccess");
        emitIds.Should().Contain("EmitRollbackFailed");
    }

    [Test]
    public void RollbackEmit_SourcesStatusFromRollbackStatus_NotStaleProdFailed()
    {
        // MINOR fix (2026-06-22): in the rollback branch stageResult is still the
        // stale prod "failed", so DEPLOY.ROLLBACK.SUCCESS used to carry
        // status:"failed". A rollback emit must source status from rollbackStatus.

        // ROLLBACK.SUCCESS — rollback succeeded → status:"success" (NOT the stale "failed").
        DeploymentPipelineWorkflow.SelectEmitStatus(isRollback: true, stageResult: "failed", rollbackStatus: "success")
            .Should().Be("success",
                "DEPLOY.ROLLBACK.SUCCESS must carry status:success, not the stale prod 'failed'");

        // ROLLBACK.FAILED — rollback itself failed.
        DeploymentPipelineWorkflow.SelectEmitStatus(true, "failed", "failed").Should().Be("failed");

        // ROLLBACK.STARTED — runs before the rollback dispatch → rollbackStatus empty → "started".
        DeploymentPipelineWorkflow.SelectEmitStatus(true, "failed", "").Should().Be("started");
        DeploymentPipelineWorkflow.SelectEmitStatus(true, "failed", null).Should().Be("started");

        // A non-rollback (stage) emit still reports stageResult unchanged.
        DeploymentPipelineWorkflow.SelectEmitStatus(isRollback: false, stageResult: "success", rollbackStatus: "")
            .Should().Be("success");
        DeploymentPipelineWorkflow.SelectEmitStatus(false, "failed", "success").Should().Be("failed",
            "a stage emit must report the stage result, not a rollback status");
    }

    [Test]
    public void RollbackEmitNodes_AreNotStageResultSourced()
    {
        // Regression guard: the three rollback emit nodes must be the rollback-sourced
        // variant (isRollback=true). We assert the wiring exists; the value mapping is
        // covered by RollbackEmit_SourcesStatusFromRollbackStatus_NotStaleProdFailed.
        foreach (var id in new[] { "EmitRollbackStarted", "EmitRollbackSuccess", "EmitRollbackFailed" })
        {
            _flowchart.Activities.OfType<EmitDeploymentEventActivity>()
                .Any(a => a.Id == id).Should().BeTrue($"{id} must exist as a rollback emit node");
        }
    }

    // ================================================================
    // P1.6 — bounded per-stage retry then escalation (no silent bypass).
    // ================================================================

    [Test]
    public void EachStage_RetriesUnderCap_ThenFails()
    {
        // Under the cap → increment → re-dispatch the SAME stage's STARTED emit.
        HasEdge("QaRetryCheck", "True", "QaIncrement").Should().BeTrue();
        HasEdge("QaIncrement", null, "EmitQaStarted").Should().BeTrue("under the cap QA re-runs");
        HasEdge("UatRetryCheck", "True", "UatIncrement").Should().BeTrue();
        HasEdge("UatIncrement", null, "EmitUatStarted").Should().BeTrue();
        HasEdge("ProdRetryCheck", "True", "ProdIncrement").Should().BeTrue();
        HasEdge("ProdIncrement", null, "EmitProdStarted").Should().BeTrue();
    }

    // ================================================================
    // Mediation — deploy dispatches go through llm-call (no direct provider).
    // ================================================================

    [Test]
    public void AllStageDeploys_DispatchLlmCall_NoDirectProvider()
    {
        foreach (var id in new[] { "QADeploy", "UATDeploy", "ProdDeploy" })
        {
            var dispatch = _flowchart.Activities.OfType<DispatchWorkflow>().FirstOrDefault(d => d.Id == id);
            dispatch.Should().NotBeNull($"{id} must exist as a DispatchWorkflow");
            ReadDefinitionId(dispatch!).Should().Be("llm-call",
                $"{id} must route through the llm-call mediation endpoint, never a provider");
        }
    }

    // ================================================================
    // Output contract — preserved + additive.
    // ================================================================

    [Test]
    public void Outputs_PreserveContract_AndAddNewOnesAdditively()
    {
        var outputSeq = _flowchart.Activities.OfType<Sequence>().FirstOrDefault(s => s.Id == "SetOutputs");
        outputSeq.Should().NotBeNull();
        var outputIds = outputSeq!.Activities.OfType<SetOutput>().Select(o => o.Id ?? "").ToList();

        // Preserved contract.
        outputIds.Should().Contain("OutStatus");
        outputIds.Should().Contain("OutStages");
        // Additive only.
        outputIds.Should().Contain("OutReleaseTag");
        outputIds.Should().Contain("OutReleaseStatus");
        outputIds.Should().Contain("OutRollbackStatus");
    }

    [Test]
    public void HappyPath_ReachesSuccess_ThroughPipelineSuccessEvent()
    {
        // QA success → UAT success → (dev) prod start → prod success → release tag
        // → CreateRelease → set success → PIPELINE.SUCCESS → outputs → finish.
        HasEdge("EmitProdSuccess", null, "SetReleaseTag").Should().BeTrue();
        HasEdge("SetReleaseTag", null, "CreateRelease").Should().BeTrue();
        HasEdge("CreateRelease", "Created", "SetReleaseCreated").Should().BeTrue();
        HasEdge("SetReleaseCreated", null, "SetSuccess").Should().BeTrue();
        HasEdge("SetSuccess", null, "EmitPipelineSuccess").Should().BeTrue();
        HasEdge("EmitPipelineSuccess", null, "SetOutputs").Should().BeTrue();
        HasEdge("SetOutputs", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // Epic 38 follow-up #21 — the mediated release step (after prod success).
    // ================================================================

    [Test]
    public void ReleaseStep_ExistsAsMediatedActivity_AfterASuccessfulProdDeploy()
    {
        // The real release is cut by CreateReleaseActivity (a thin TammaApiClient
        // client — the engine holds NO git credential), and ONLY after prod success.
        var release = _flowchart.Activities
            .OfType<CreateReleaseActivity>()
            .FirstOrDefault(a => a.Id == "CreateRelease");
        release.Should().NotBeNull("a real CreateReleaseActivity must exist (the release is no longer 'deferred')");

        // It sits on the prod-success path: prod success → compute tag → create release.
        HasEdge("EmitProdSuccess", null, "SetReleaseTag").Should().BeTrue();
        HasEdge("SetReleaseTag", null, "CreateRelease").Should().BeTrue();
    }

    [Test]
    public void ReleaseStep_BothOutcomesReachSuccessTerminal_FailureIsSurfacedNotSilent()
    {
        // The deploy already succeeded; a release-create failure is surfaced
        // (releaseStatus=failed via SetReleaseFailed) but does NOT undo the deploy —
        // both outcomes proceed to the success terminal.
        HasEdge("CreateRelease", "Created", "SetReleaseCreated").Should().BeTrue();
        HasEdge("CreateRelease", "Error", "SetReleaseFailed").Should().BeTrue();
        HasEdge("SetReleaseCreated", null, "SetSuccess").Should().BeTrue();
        HasEdge("SetReleaseFailed", null, "SetSuccess").Should().BeTrue();

        // The failure edge still records the loud FAILED signal + reaches the
        // pipeline-success terminal, never a silent drop.
        var reach = ReachableFromPort("CreateRelease", "Error");
        reach.Should().Contain("SetReleaseFailed");
        reach.Should().Contain("EmitPipelineSuccess");
    }

    [Test]
    public void ReleaseStep_NeverReachedWhenProdFails()
    {
        // A failed prod deploy (retry exhausted) rolls back and terminates — it must
        // NOT cut a release for a version that never shipped.
        var reach = ReachableFromPort("ProdRetryCheck", "False");
        reach.Should().NotContain("CreateRelease", "a failed prod deploy must never cut a release");
    }

    [Test]
    public void ReleaseStep_OutputsCarryStatusAndUrl()
    {
        var outputSeq = _flowchart.Activities.OfType<Sequence>().FirstOrDefault(s => s.Id == "SetOutputs");
        outputSeq.Should().NotBeNull();
        var outputIds = outputSeq!.Activities.OfType<SetOutput>().Select(o => o.Id ?? "").ToList();
        outputIds.Should().Contain("OutReleaseStatus");
        outputIds.Should().Contain("OutReleaseUrl");
    }

    // ================================================================
    // Epic 38 follow-up #21 (audit fidelity) — a PIPELINE.SUCCESS event must not
    // carry a release-step error as its `reason`.
    // ================================================================

    [Test]
    public void ReleaseError_IsDecoupledFromStageError_SoPipelineSuccessReasonStaysClean()
    {
        // The release-step error (CreateRelease.ErrorCode) must be captured in its OWN
        // variable, NOT the shared StageError that seeds a PIPELINE.SUCCESS `reason`.
        // Routing it into StageError makes a successful pipeline emit a misleading
        // release error as `reason` (status still "success").
        var release = _flowchart.Activities
            .OfType<CreateReleaseActivity>()
            .Single(a => a.Id == "CreateRelease");

        var boundVariable = ReadOutputVariableName(release.ErrorCode);
        boundVariable.Should().Be("ReleaseError");
        boundVariable.Should().NotBe("StageError",
            "the release-step error must not pollute the shared StageError → PIPELINE.SUCCESS reason");
    }

    [Test]
    public void PipelineSuccessAuditData_WithFailedRelease_CarriesFailedStatusButNoReason()
    {
        // A run where prod succeeded (the shared stage-error is empty) but CreateRelease
        // returned Error: the emitted PIPELINE.SUCCESS payload is status=success +
        // releaseStatus=failed, and its `reason` is ABSENT (reserved for stage failures).
        var data = DeploymentPipelineWorkflow.BuildDeployEventData(
            status: "success",
            completedStages: "[\"qa\",\"uat\",\"production\"]",
            reason: "",              // stageError is "" on the success path
            rollbackStatus: "",
            releaseStatus: "failed",
            releaseUrl: "");

        data["status"].Should().Be("success");
        data["releaseStatus"].Should().Be("failed");
        data.Should().NotContainKey("reason",
            "a PIPELINE.SUCCESS event must not carry a release-step error as its reason");
    }

    [Test]
    public void PipelineFailedAuditData_StillCarriesStageErrorAsReason()
    {
        // A genuine stage failure still populates `reason` on PIPELINE.FAILED, as before.
        var data = DeploymentPipelineWorkflow.BuildDeployEventData(
            status: "failed",
            completedStages: "[\"qa\"]",
            reason: "deploy reported status='failed'",
            rollbackStatus: "");

        data["status"].Should().Be("failed");
        data["reason"].Should().Be("deploy reported status='failed'");
    }

    // ================================================================
    // Helpers (mirrors MergeApprovalWorkflowTests)
    // ================================================================

    private static string? ReadOutputVariableName(Elsa.Workflows.Models.Output? output)
        => (output?.MemoryBlockReference() as Variable)?.Name;

    private bool HasEdge(string sourceId, string? port, string targetId)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port == null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);

    private HashSet<string> Reachable(string startId)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var c in _flowchart.Connections.Where(c => c.Source.Activity.Id == id))
            {
                var t = c.Target.Activity.Id;
                if (t != null && seen.Add(t)) queue.Enqueue(t);
            }
        }
        return seen;
    }

    private HashSet<string> ReachableFromPort(string sourceId, string port)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        foreach (var c in _flowchart.Connections.Where(c =>
            c.Source.Activity.Id == sourceId && c.Source.Port == port))
        {
            if (c.Target.Activity.Id is { } t && seen.Add(t)) queue.Enqueue(t);
        }
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var c in _flowchart.Connections.Where(c => c.Source.Activity.Id == id))
            {
                if (c.Target.Activity.Id is { } t && seen.Add(t)) queue.Enqueue(t);
            }
        }
        return seen;
    }

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}
