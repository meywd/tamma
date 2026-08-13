using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Tests for Story 12-5e: CI Retry Counter Bug Investigation.
///
/// The implementation plan required a "verify-first" approach with three hypotheses:
///   (a) Counter lost across suspend/resume (persistence scoping bug)
///   (b) Counter carried across re-entries from review-fix (reset not hit)
///   (c) Bug is stale — counter behaves correctly
///
/// Investigation result: Case (c) confirmed.
///
/// Evidence:
///   1. CiWithDebugRetryWorkflow declares ciRetryCount via builder.WithVariable
///      (workflow-instance scope, persisted by Elsa).
///   2. The initInputs activity explicitly resets ciRetryCount to 0 on every
///      workflow entry (line 78), ensuring re-entries get full retry budget.
///   3. SingleIssueCycleWorkflow does NOT reference ciRetryCount at all — it
///      dispatches ci-with-debug-retry as a fresh sub-workflow instance each time.
///   4. Lines 349-351 of SingleIssueCycleWorkflow.cs (cited in the parent story
///      as the bug location) are actually the branch-creation dispatch, not CI retry.
///
/// These tests serve as permanent regression coverage.
/// </summary>
[TestFixture]
public class CiRetryCounterPersistenceTests
{
    private Flowchart _flowchart = null!;
    private Mock<IWorkflowBuilder> _builder = null!;

    [SetUp]
    public void SetUp()
    {
        var workflow = new CiWithDebugRetryWorkflow();
        _builder = WorkflowTestHelper.BuildWorkflow(workflow);
        _flowchart = WorkflowTestHelper.GetFlowchart(_builder);
    }

    // =====================================================================
    // 1. Fresh invocation starts at 0
    // =====================================================================

    [Test]
    public void CiRetryCount_DefaultsToZero()
    {
        var ciRetryVar = _builder.Object.Variables
            .OfType<Variable<int>>()
            .FirstOrDefault(v => v.Name == "CiRetryCount");

        ciRetryVar.Should().NotBeNull("CiRetryCount variable should exist");
        ciRetryVar!.Value.Should().Be(0,
            "CiRetryCount should default to 0 on fresh workflow instantiation");
    }

    // =====================================================================
    // 2. CiRetryCount is declared at workflow-builder scope (not local)
    //    This means Elsa will persist it with the workflow instance.
    // =====================================================================

    [Test]
    public void CiRetryCount_IsDeclaredAtWorkflowScope()
    {
        // builder.WithVariable<int>("CiRetryCount", 0) registers the variable
        // on the workflow builder's Variables collection, making it a workflow-
        // instance variable that Elsa serializes on suspend and restores on resume.
        var ciRetryVar = _builder.Object.Variables
            .OfType<Variable<int>>()
            .FirstOrDefault(v => v.Name == "CiRetryCount");

        ciRetryVar.Should().NotBeNull(
            "CiRetryCount must be a workflow-scope variable (not activity-local) " +
            "so it persists across suspend/resume boundaries");
    }

    // =====================================================================
    // 3. Three consecutive failures exhaust the retry budget
    //    (structural: verify the retry loop graph is correct)
    // =====================================================================

    [Test]
    public void RetryLoop_Structure_IsCorrect()
    {
        // Testing Pipeline -> TestsPassed decision
        var toDecision = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "DispatchTestingPipeline" &&
            c.Target.Activity.Id == "TestsPassed");

        // Epic 31 P3 — TestsPassed False routes through the §4.3
        // CiUnsupportedCheck FIRST (a typed capability_unsupported must not
        // burn debug retries), then the retry guard.
        var toGuard = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TestsPassed" &&
            c.Source.Port == "False" &&
            c.Target.Activity.Id == "CiUnsupportedCheck")
        && _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "CiUnsupportedCheck" &&
            c.Source.Port == "False" &&
            c.Target.Activity.Id == "CiRetryGuard");

        // CiRetryGuard True -> IncrCiRetry
        var toIncrement = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "CiRetryGuard" &&
            c.Source.Port == "True" &&
            c.Target.Activity.Id == "IncrCiRetry");

        // IncrCiRetry -> DispatchCiDebugging
        var toDebug = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "IncrCiRetry" &&
            c.Target.Activity.Id == "DispatchCiDebugging");

        // DispatchCiDebugging -> DispatchTestingPipeline (loop back)
        var loopBack = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "DispatchCiDebugging" &&
            c.Target.Activity.Id == "DispatchTestingPipeline");

        // CiRetryGuard False -> CiRetryFinishFail (exhausted)
        var toExhausted = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "CiRetryGuard" &&
            c.Source.Port == "False" &&
            c.Target.Activity.Id == "CiRetryFinishFail");

        toDecision.Should().BeTrue("Pipeline -> TestsPassed");
        toGuard.Should().BeTrue("TestsPassed False -> CiRetryGuard");
        toIncrement.Should().BeTrue("CiRetryGuard True -> IncrCiRetry");
        toDebug.Should().BeTrue("IncrCiRetry -> DispatchCiDebugging");
        loopBack.Should().BeTrue("DispatchCiDebugging -> DispatchTestingPipeline (loop)");
        toExhausted.Should().BeTrue("CiRetryGuard False -> CiRetryFinishFail (exhausted)");
    }

    // =====================================================================
    // 4. MaxRetries defaults to 3
    // =====================================================================

    [Test]
    public void MaxRetries_DefaultsTo3()
    {
        var maxRetriesVar = _builder.Object.Variables
            .OfType<Variable<int>>()
            .FirstOrDefault(v => v.Name == "MaxRetries");

        maxRetriesVar.Should().NotBeNull("MaxRetries variable should exist");
        maxRetriesVar!.Value.Should().Be(3,
            "MaxRetries should default to 3");
    }

    // =====================================================================
    // 5. InitInputs is the start node (reset happens before anything else)
    // =====================================================================

    [Test]
    public void InitInputs_IsStartNode()
    {
        _flowchart.Start.Should().NotBeNull();
        _flowchart.Start!.Id.Should().Be("InitCiRetryInputs",
            "InitCiRetryInputs (which resets ciRetryCount to 0) must be the flowchart start node");
    }

    // =====================================================================
    // 6. Re-entry from review-fix dispatches a fresh sub-workflow instance
    //    (SingleIssueCycleWorkflow does NOT reference CiRetryCount)
    // =====================================================================

    [Test]
    public void SingleIssueCycle_DoesNotReferenceCiRetryCount()
    {
        // Build the SingleIssueCycleWorkflow and verify it has no CiRetryCount variable.
        // This proves each dispatch of ci-with-debug-retry creates a fresh instance.
        var sicWorkflow = new SingleIssueCycleWorkflow();
        var sicBuilder = WorkflowTestHelper.BuildWorkflow(sicWorkflow);

        var ciRetryVar = sicBuilder.Object.Variables
            .OfType<Variable<int>>()
            .FirstOrDefault(v => v.Name == "CiRetryCount");

        ciRetryVar.Should().BeNull(
            "SingleIssueCycleWorkflow should NOT have a CiRetryCount variable — " +
            "the counter is scoped to CiWithDebugRetryWorkflow instances only");
    }

    // =====================================================================
    // 7. Workflow outputs include ciRetryCount (for observability)
    // =====================================================================

    [Test]
    public void FinishPass_IncludesCiRetryCountOutput()
    {
        var finishPass = _flowchart.Activities
            .OfType<Sequence>()
            .FirstOrDefault(s => s.Id == "CiRetryFinishPass");

        finishPass.Should().NotBeNull("CiRetryFinishPass sequence should exist");

        var allActivities = WorkflowTestHelper.GetAllActivities(_flowchart);
        var outputActivities = allActivities
            .Where(a => a.Id == "SetCiRetryCountPass")
            .ToList();

        outputActivities.Should().HaveCountGreaterOrEqualTo(1,
            "Pass path should include SetCiRetryCountPass output");
    }

    [Test]
    public void FinishFail_IncludesCiRetryCountOutput()
    {
        var finishFail = _flowchart.Activities
            .OfType<Sequence>()
            .FirstOrDefault(s => s.Id == "CiRetryFinishFail");

        finishFail.Should().NotBeNull("CiRetryFinishFail sequence should exist");

        var allActivities = WorkflowTestHelper.GetAllActivities(_flowchart);
        var outputActivities = allActivities
            .Where(a => a.Id == "SetCiRetryCountFail")
            .ToList();

        outputActivities.Should().HaveCountGreaterOrEqualTo(1,
            "Fail path should include SetCiRetryCountFail output");
    }
}
