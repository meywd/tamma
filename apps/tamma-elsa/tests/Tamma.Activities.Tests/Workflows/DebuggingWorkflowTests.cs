using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Debug;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Build-out graph tests for the <c>debugging</c> sub-workflow (completeness audit
/// 2026-06-22, Debugging.md). Structural (the flowchart is not runnable in a unit
/// test without the Elsa runtime — the codebase convention is to assert the graph
/// shape via <see cref="WorkflowTestHelper"/>). Covers the P0/P1 build-out items:
/// #1 debugResultJson populated before BOTH terminals, #3 applyFix failure edge,
/// #4 regression-must-fail guard, #8 DCB events, #10 tenantId threading, #11 durable
/// context-collection timeout, #12 graph-enforced loop bound, #16 status output.
/// </summary>
[TestFixture]
public class DebuggingWorkflowTests
{
    private Flowchart _flowchart = null!;
    private Mock<IWorkflowBuilder> _builder = null!;

    [SetUp]
    public void SetUp()
    {
        _builder = WorkflowTestHelper.BuildWorkflow(new DebuggingWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(_builder);
    }

    private bool HasConnection(string sourceId, string targetId, string? sourcePort = null) =>
        _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            c.Target.Activity.Id == targetId &&
            (sourcePort == null || c.Source.Port == sourcePort));

    private T? Find<T>(string id) where T : class, IActivity =>
        WorkflowTestHelper.GetAllActivities(_flowchart).OfType<T>().FirstOrDefault(a => a.Id == id);

    // ── #1 / #16 — debugResultJson is populated before BOTH terminal output sequences ──

    [Test]
    public void ResolvedTerminal_SerializesDebugResult_BeforeSettingOutputs()
    {
        // serializeResolvedResult must run on the resolved path before setResolvedOutputs.
        Find<SetVariable<string>>("serializeResolvedResult").Should().NotBeNull(
            "the resolved terminal must serialize a real DebugResult into debugResultJson (#1)");
        HasConnection("UpdateCodeIndex", "serializeResolvedResult").Should().BeTrue();
        // setResolvedStatus -> emitResolved -> setResolvedOutputs (status before outputs).
        HasConnection("serializeResolvedResult", "setResolvedStatus").Should().BeTrue();
        HasConnection("emitResolved", "setResolvedOutputs").Should().BeTrue();
    }

    [Test]
    public void EscalatedTerminal_SerializesDebugResult_BeforeSettingOutputs()
    {
        Find<SetVariable<string>>("serializeEscalatedResult").Should().NotBeNull(
            "the escalation terminal must serialize a real DebugResult into debugResultJson (#1)");
        HasConnection("compileReport", "setEscalatedStatus").Should().BeTrue();
        HasConnection("setEscalatedStatus", "serializeEscalatedResult").Should().BeTrue();
        HasConnection("serializeEscalatedResult", "emitEscalated").Should().BeTrue();
        HasConnection("emitEscalated", "setEscalatedOutputs").Should().BeTrue();
    }

    [Test]
    public void CompileReport_ResultIsCaptured_NotDiscarded()
    {
        // #1: CompileDebugReport.Result must be wired (previously discarded → empty report).
        var compile = Find<CompileDebugReportActivity>("compileReport");
        compile.Should().NotBeNull();
        compile!.Result.Should().NotBeNull(
            "CompileDebugReport.Result must be captured so the escalated DebugResult carries the report text");
    }

    [Test]
    public void BothTerminals_EmitStatusOutput()
    {
        // #16: a real status enum is emitted on BOTH terminals (not only boolean success).
        Find<SetOutput>("outputResolvedStatus").Should().NotBeNull("resolved terminal emits status (#16)");
        Find<SetOutput>("outputEscalatedStatus").Should().NotBeNull("escalated terminal emits status (#16)");
    }

    // ── #3 — applyFix captures result + branches on success (no false success) ──

    [Test]
    public void ApplyFix_CapturesResult_AndBranchesOnSuccess()
    {
        var applyFix = Find<DispatchWorkflow>("applyFix");
        applyFix.Should().NotBeNull();
        applyFix!.Result.Should().NotBeNull("applyFix must capture the llm-call result (#3)");

        Find<FlowDecision>("fixApplied").Should().NotBeNull("a fixApplied? decision must gate the result (#3)");
    }

    [Test]
    public void ApplyFix_Failure_RoutesToRefine_NotRunTests()
    {
        // #3 no-false-success: a failed fix dispatch must NOT silently proceed to runTests.
        HasConnection("fixApplied", "runTests", "True").Should().BeTrue(
            "a successful fix proceeds to run tests");
        HasConnection("fixApplied", "recordFailedAttempt", "False").Should().BeTrue(
            "a failed fix is counted as a failed attempt and routed to refine, never a silent success");
        // The failed-fix edge must NOT go straight to runTests.
        HasConnection("fixApplied", "runTests", "False").Should().BeFalse();
    }

    // ── #4 — BugInvestigation regression-test-must-fail guard (AC7) ──

    [Test]
    public void BugMode_RunsRegressionTest_AndRequiresItToFail_BeforeFixing()
    {
        Find<DispatchWorkflow>("runRegressionTest").Should().NotBeNull(
            "the written regression test must be RUN before fixing (AC7, #4)");
        Find<FlowDecision>("regressionFailsAsExpected").Should().NotBeNull(
            "a regression-fails-as-expected? guard must gate the fix (AC7, #4)");

        // Write -> capture file -> run -> guard.
        HasConnection("writeRegressionTest", "captureRegressionFile").Should().BeTrue();
        HasConnection("captureRegressionFile", "runRegressionTest").Should().BeTrue();
        HasConnection("runRegressionTest", "regressionFailsAsExpected").Should().BeTrue();

        // Fails-as-expected -> mark written -> applyFix.
        HasConnection("regressionFailsAsExpected", "markRegressionTestWritten", "True").Should().BeTrue();
        HasConnection("markRegressionTestWritten", "applyFix").Should().BeTrue();
    }

    [Test]
    public void BugMode_RegressionTestPasses_Escalates_DoesNotFix()
    {
        // AC7: a regression test that PASSES (does not reproduce the bug) must abort/escalate,
        // not silently proceed to apply a fix.
        HasConnection("regressionFailsAsExpected", "setRegressionInvalidReason", "False").Should().BeTrue();
        HasConnection("setRegressionInvalidReason", "emitRegressionInvalid").Should().BeTrue();
        HasConnection("emitRegressionInvalid", "compileReport").Should().BeTrue(
            "an invalid (passing) regression test escalates with a compiled report, never applies a fix");
    }

    // ── #11 — durable context-collection timeout bounds the Fork/Join ──

    [Test]
    public void ContextCollection_HasDurableTimeoutGuard_RacingTheCollectors()
    {
        Find<ContextCollectionTimeoutActivity>("contextTimeout").Should().NotBeNull(
            "a durable DelayFor-based timeout must bound the context Fork/Join (AC4, #11)");

        // The timeout is a fork branch.
        HasConnection("contextFork", "contextTimeout", "Timeout").Should().BeTrue();
        // On timeout it funnels through the gate (proceed with partial context).
        HasConnection("contextTimeout", "contextGatherGate", "TimedOut").Should().BeTrue();
        // The WaitAll join also funnels through the gate.
        HasConnection("contextJoin", "contextGatherGate").Should().BeTrue();
        // The gate proceeds to serialization on the first arrival.
        HasConnection("contextGatherGate", "joinLog", "True").Should().BeTrue();
    }

    // ── #12 — graph-enforced loop bound ──

    [Test]
    public void LoopBound_IsGraphEnforced_OnIterationIncrement()
    {
        Find<FlowDecision>("iterationsExhausted").Should().NotBeNull(
            "the iteration cap must be an explicit graph FlowDecision, not only internal to SelectHypothesis (#12)");
        HasConnection("incrementIteration", "iterationsExhausted").Should().BeTrue();
        // Exhausted -> escalate; within budget -> loop back to select.
        HasConnection("iterationsExhausted", "setMaxIterationsReason", "True").Should().BeTrue();
        HasConnection("setMaxIterationsReason", "compileReport").Should().BeTrue();
        HasConnection("iterationsExhausted", "selectHypothesis", "False").Should().BeTrue();
    }

    // ── #8 — DCB events at graph boundaries ──

    [Test]
    public void EmitsDcbEvents_AtAllGraphBoundaries()
    {
        var emitters = WorkflowTestHelper.GetAllActivities(_flowchart)
            .OfType<EmitDebugEventActivity>()
            .ToList();
        emitters.Should().HaveCountGreaterThanOrEqualTo(8,
            "DEBUG.* events must be emitted at session-start, diagnosis, hypothesis-selected, "
            + "fix-attempted, tests-passed/failed, resolved and escalated (#8)");

        new[]
        {
            "emitSessionStarted", "emitDiagnosisSuccess", "emitDiagnosisFailed",
            "emitHypothesisSelected", "emitFixAttempted", "emitTestsPassed",
            "emitTestsFailed", "emitResolved", "emitEscalated",
        }.Should().OnlyContain(id => Find<EmitDebugEventActivity>(id) != null);
    }

    // ── #10 — tenantId is threaded into the llm-call and testing-pipeline dispatches ──

    [Test]
    public void ApplyFix_ThreadsTenantId()
    {
        var applyFix = Find<DispatchWorkflow>("applyFix");
        applyFix.Should().NotBeNull();
        applyFix!.Input.Should().NotBeNull("applyFix must forward a tenantId in its llm-call input (#10)");
    }

    [Test]
    public void RunTests_ThreadsTenantId()
    {
        var runTests = Find<DispatchWorkflow>("runTests");
        runTests.Should().NotBeNull();
        runTests!.Input.Should().NotBeNull("runTests must forward a tenantId in its testing-pipeline input (#10)");
    }

    [Test]
    public void DeclaresTenantIdVariable_NamedForMediatedResolution()
    {
        // MediatedLlmText.ResolveTenantId reads the "TenantId" workflow variable.
        _builder.Object.Variables.Should().Contain(v => v.Name == "TenantId",
            "the workflow must declare a TenantId variable so the mediated call-LLM resolves tenant scope (#10)");
    }

    // ── Sanity: definition id is stable ──

    [Test]
    public void DefinitionId_IsDebugging()
    {
        _builder.Object.DefinitionId.Should().Be("debugging");
    }
}
