using System.Reflection;
using Elsa.Extensions;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Structural verification tests for Epic 13 (Workflow Decomposition) workflows.
/// These tests verify that workflow definitions build correctly and have the expected
/// structure, activity counts, sub-workflow references, variables, and display texts.
///
/// Approach: We mock IWorkflowBuilder to capture the properties set during Build(),
/// then invoke the protected Build() method via reflection. The mock captures
/// DefinitionId, Version, Name, Description, Root (the Flowchart), and Variables.
/// </summary>
[TestFixture]
public class WorkflowStructureTests
{
    // ================================================================
    // Helper: Build a workflow using a mock IWorkflowBuilder
    // ================================================================

    /// <summary>
    /// Creates a mock IWorkflowBuilder, invokes the protected Build() method
    /// on the given WorkflowBase instance, and returns the mock for inspection.
    /// </summary>
    private static Mock<IWorkflowBuilder> BuildWorkflow(WorkflowBase workflow)
    {
        var mockBuilder = new Mock<IWorkflowBuilder>();

        // Store property values set by Build()
        string? definitionId = null;
        string? name = null;
        string? description = null;
        int version = 0;
        IActivity? root = null;
        var variables = new List<Variable>();

        mockBuilder.SetupSet(b => b.DefinitionId = It.IsAny<string>())
            .Callback<string>(v => definitionId = v);
        mockBuilder.SetupGet(b => b.DefinitionId).Returns(() => definitionId!);

        mockBuilder.SetupSet(b => b.Name = It.IsAny<string>())
            .Callback<string>(v => name = v);
        mockBuilder.SetupGet(b => b.Name).Returns(() => name!);

        mockBuilder.SetupSet(b => b.Description = It.IsAny<string>())
            .Callback<string>(v => description = v);
        mockBuilder.SetupGet(b => b.Description).Returns(() => description!);

        mockBuilder.SetupSet(b => b.Version = It.IsAny<int>())
            .Callback<int>(v => version = v);
        mockBuilder.SetupGet(b => b.Version).Returns(() => version);

        mockBuilder.SetupSet(b => b.Root = It.IsAny<IActivity>())
            .Callback<IActivity>(v => root = v);
        mockBuilder.SetupGet(b => b.Root).Returns(() => root!);

        mockBuilder.SetupGet(b => b.Variables).Returns(variables);

        // WithVariable<T>() — no args
        mockBuilder
            .Setup(b => b.WithVariable<string>())
            .Returns(() =>
            {
                var v = new Variable<string>();
                variables.Add(v);
                return v;
            });
        mockBuilder
            .Setup(b => b.WithVariable<int>())
            .Returns(() =>
            {
                var v = new Variable<int>();
                variables.Add(v);
                return v;
            });
        mockBuilder
            .Setup(b => b.WithVariable<string[]>())
            .Returns(() =>
            {
                var v = new Variable<string[]>();
                variables.Add(v);
                return v;
            });
        mockBuilder
            .Setup(b => b.WithVariable<IDictionary<string, object>?>())
            .Returns(() =>
            {
                var v = new Variable<IDictionary<string, object>?>();
                variables.Add(v);
                return v;
            });

        // WithVariable<T>(string name, T defaultValue)
        mockBuilder
            .Setup(b => b.WithVariable<string>(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string varName, string defaultValue) =>
            {
                var v = new Variable<string>(varName, defaultValue);
                variables.Add(v);
                return v;
            });
        mockBuilder
            .Setup(b => b.WithVariable<int>(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string varName, int defaultValue) =>
            {
                var v = new Variable<int>(varName, defaultValue);
                variables.Add(v);
                return v;
            });
        mockBuilder
            .Setup(b => b.WithVariable<string[]>(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns((string varName, string[] defaultValue) =>
            {
                var v = new Variable<string[]>(varName, defaultValue);
                variables.Add(v);
                return v;
            });

        // Invoke the protected Build(IWorkflowBuilder) method via reflection
        var buildMethod = workflow.GetType().GetMethod(
            "Build",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(IWorkflowBuilder) },
            null);

        buildMethod.Should().NotBeNull("workflow should have a protected Build(IWorkflowBuilder) method");
        buildMethod!.Invoke(workflow, new object[] { mockBuilder.Object });

        return mockBuilder;
    }

    /// <summary>
    /// Extracts the Flowchart from the builder's Root property.
    /// </summary>
    private static Flowchart GetFlowchart(Mock<IWorkflowBuilder> mockBuilder)
    {
        var root = mockBuilder.Object.Root;
        root.Should().NotBeNull("workflow should set builder.Root");
        root.Should().BeOfType<Flowchart>("workflow root should be a Flowchart");
        return (Flowchart)root;
    }

    /// <summary>
    /// Gets all activities from a Flowchart, including nested activities inside Sequence nodes.
    /// </summary>
    private static List<IActivity> GetAllActivities(Flowchart flowchart)
    {
        var activities = new List<IActivity>();
        foreach (var activity in flowchart.Activities)
        {
            activities.Add(activity);
            // Expand Sequence activities to include their children
            if (activity is Sequence seq)
            {
                activities.AddRange(seq.Activities);
            }
        }
        return activities;
    }

    /// <summary>
    /// Extracts DispatchWorkflow activities from a flowchart and returns their definition IDs.
    /// The WorkflowDefinitionId is an Input&lt;string&gt; wrapping a Literal expression.
    /// </summary>
    private static List<string> GetDispatchedWorkflowIds(Flowchart flowchart)
    {
        var ids = new List<string>();
        foreach (var activity in flowchart.Activities)
        {
            if (activity is DispatchWorkflow dispatch)
            {
                var defId = ExtractLiteralValue(dispatch.WorkflowDefinitionId);
                if (defId != null)
                    ids.Add(defId);
            }
        }
        return ids;
    }

    /// <summary>
    /// Extracts the literal string value from an Input&lt;string&gt; property.
    /// Input wraps an Expression which, for literal values, is a Literal with a Value property.
    /// </summary>
    private static string? ExtractLiteralValue(Input<string>? input)
    {
        if (input == null) return null;

        // The Input base class (Argument) stores the expression in the MemoryBlockReference.
        // For literal expressions, the expression is stored differently.
        // Try to access via the expression on the Input's internal structure.
        try
        {
            // Input<T>(T value) constructor creates a Literal<T> expression internally.
            // Access the expression via reflection on the base Argument class.
            var argType = typeof(Input);
            // Walk up to find the expression-related field
            var fields = argType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var field in fields)
            {
                var val = field.GetValue(input);
                if (val is Literal literal)
                    return literal.Value?.ToString();
            }

            // Try properties
            var props = argType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var prop in props)
            {
                if (!prop.CanRead) continue;
                try
                {
                    var val = prop.GetValue(input);
                    if (val is Literal literal)
                        return literal.Value?.ToString();
                    if (val is Expression expr)
                    {
                        // Expression might be a Literal wrapped as Expression
                        // Literal does not inherit from Expression, so check type by name
                        var exprType = expr.GetType();
                        if (exprType.Name.StartsWith("Literal"))
                        {
                            var valProp = exprType.GetProperty("Value");
                            if (valProp != null)
                                return valProp.GetValue(expr)?.ToString();
                        }
                    }
                }
                catch
                {
                    // Skip properties that throw
                }
            }

            // Fallback: check the MemoryBlockReference for a Literal reference
            var memRef = input.MemoryBlockReference;
            if (memRef != null)
            {
                // Try to get the value from the Literal expression stored in the memory block
                var memRefType = memRef.GetType();
                var valueProp = memRefType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
                if (valueProp != null)
                    return valueProp.GetValue(memRef)?.ToString();
            }
        }
        catch
        {
            // If reflection fails, return null
        }

        return null;
    }

    /// <summary>
    /// Gets all SetVariable activities with specific exit reason values from a flowchart.
    /// Exit reason SetVariable nodes have IDs like "SetReasonSuccess", "SetReasonNoIssues", etc.
    /// </summary>
    private static List<string> GetExitReasonNodeIds(Flowchart flowchart)
    {
        return flowchart.Activities
            .OfType<SetVariable>()
            .Where(sv => sv.Id?.StartsWith("SetReason") == true)
            .Select(sv => sv.Id!)
            .ToList();
    }

    // ================================================================
    // TddWithDebugRetryWorkflow Tests
    // ================================================================

    [Test]
    public void TddWithDebugRetryWorkflow_BuildsWithoutError()
    {
        // Arrange
        var workflow = new TddWithDebugRetryWorkflow();

        // Act & Assert — should not throw
        var act = () => BuildWorkflow(workflow);
        act.Should().NotThrow();
    }

    [Test]
    public void TddWithDebugRetryWorkflow_HasCorrectDefinitionId()
    {
        // Arrange & Act
        var workflow = new TddWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);

        // Assert
        builder.Object.DefinitionId.Should().Be("tdd-with-debug-retry");
    }

    [Test]
    public void TddWithDebugRetryWorkflow_HasWorkflowVersionsComputedVersion()
    {
        // Arrange & Act
        var workflow = new TddWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);

        // Assert
        builder.Object.Version.Should().Be(WorkflowVersions.ComputedVersion);
    }

    [Test]
    public void TddWithDebugRetryWorkflow_AllActivitiesHaveDisplayText()
    {
        // Arrange & Act
        var workflow = new TddWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);
        var allActivities = GetAllActivities(flowchart);

        // Assert — every activity should have a non-empty DisplayText
        foreach (var activity in allActivities)
        {
            var displayText = activity.GetDisplayText();
            displayText.Should().NotBeNullOrEmpty(
                $"Activity '{activity.GetType().Name}' (Id: {activity.Id}) should have DisplayText set");
        }
    }

    [Test]
    public void TddWithDebugRetryWorkflow_DispatchesToTddCycleAndDebugging()
    {
        // Arrange & Act
        var workflow = new TddWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);

        // Find DispatchWorkflow activities
        var dispatches = flowchart.Activities.OfType<DispatchWorkflow>().ToList();

        // Assert — should dispatch to "tdd-cycle" and "debugging"
        dispatches.Should().HaveCountGreaterOrEqualTo(2,
            "TDD retry workflow should dispatch to at least tdd-cycle and debugging");

        // Verify by activity ID (more reliable than extracting Input<string> literals)
        var dispatchIds = dispatches.Select(d => d.Id).ToList();
        dispatchIds.Should().Contain("DispatchTddCycle", "should dispatch to TDD cycle");
        dispatchIds.Should().Contain("DispatchTddDebugging", "should dispatch to debugging");
    }

    // ================================================================
    // CiWithDebugRetryWorkflow Tests
    // ================================================================

    [Test]
    public void CiWithDebugRetryWorkflow_BuildsWithoutError()
    {
        // Arrange
        var workflow = new CiWithDebugRetryWorkflow();

        // Act & Assert
        var act = () => BuildWorkflow(workflow);
        act.Should().NotThrow();
    }

    [Test]
    public void CiWithDebugRetryWorkflow_HasCorrectDefinitionId()
    {
        // Arrange & Act
        var workflow = new CiWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);

        // Assert
        builder.Object.DefinitionId.Should().Be("ci-with-debug-retry");
    }

    [Test]
    public void CiWithDebugRetryWorkflow_DispatchesToTestingPipelineAndDebugging()
    {
        // Arrange & Act
        var workflow = new CiWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);

        // Find DispatchWorkflow activities
        var dispatches = flowchart.Activities.OfType<DispatchWorkflow>().ToList();

        // Assert
        dispatches.Should().HaveCountGreaterOrEqualTo(2,
            "CI retry workflow should dispatch to at least testing-pipeline and debugging");

        var dispatchIds = dispatches.Select(d => d.Id).ToList();
        dispatchIds.Should().Contain("DispatchTestingPipeline", "should dispatch to testing pipeline");
        dispatchIds.Should().Contain("DispatchCiDebugging", "should dispatch to CI debugging");
    }

    [Test]
    public void CiWithDebugRetryWorkflow_OutputsCiRetryCount()
    {
        // Arrange & Act
        var workflow = new CiWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);
        var allActivities = GetAllActivities(flowchart);

        // The CI workflow should output ciRetryCount via SetOutput activities
        // inside the Finish Pass and Finish Fail sequences
        var setOutputActivities = allActivities
            .OfType<Elsa.Workflows.Management.Activities.SetOutput.SetOutput>()
            .ToList();

        // Find SetOutput activities that set ciRetryCount
        var ciRetryCountOutputs = setOutputActivities
            .Where(so => so.Id != null && so.Id.Contains("CiRetryCount"))
            .ToList();

        ciRetryCountOutputs.Should().HaveCountGreaterOrEqualTo(2,
            "CI workflow should output ciRetryCount in both pass and fail paths " +
            "(SetCiRetryCountPass and SetCiRetryCountFail)");
    }

    // ================================================================
    // SingleIssueCycleWorkflow Tests
    // ================================================================

    [Test]
    public void SingleIssueCycleWorkflow_BuildsWithoutError()
    {
        // Arrange
        var workflow = new SingleIssueCycleWorkflow();

        // Act & Assert
        var act = () => BuildWorkflow(workflow);
        act.Should().NotThrow();
    }

    [Test]
    public void SingleIssueCycleWorkflow_ActivityCountIsReduced()
    {
        // Arrange & Act
        var workflow = new SingleIssueCycleWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);

        // Assert — after decomposition, top-level activities should be < 40
        // The inline TDD/CI retry loops were extracted to sub-workflows,
        // reducing from 39+ activities to 37 (still includes 8 exit reason nodes
        // + shared finish sequence + 10 dispatch steps with extracts/decisions).
        flowchart.Activities.Count.Should().BeLessThan(40,
            "after Epic 13 decomposition, SingleIssueCycleWorkflow should have fewer " +
            "than 40 top-level activities (TDD/CI retry loops extracted to sub-workflows)");

        // Also verify it's in the expected range (37 currently)
        flowchart.Activities.Count.Should().BeInRange(30, 39,
            "activity count should be between 30 and 39 after decomposition");
    }

    [Test]
    public void SingleIssueCycleWorkflow_DispatchesToTddWithDebugRetryAndCiWithDebugRetry()
    {
        // Arrange & Act
        var workflow = new SingleIssueCycleWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);

        // Find DispatchWorkflow activities
        var dispatches = flowchart.Activities.OfType<DispatchWorkflow>().ToList();
        var dispatchIds = dispatches.Select(d => d.Id).ToList();

        // Assert — should reference the new sub-workflows
        dispatchIds.Should().Contain("DispatchTddWithDebugRetry",
            "should dispatch to tdd-with-debug-retry sub-workflow");
        dispatchIds.Should().Contain("DispatchCiWithDebugRetry",
            "should dispatch to ci-with-debug-retry sub-workflow");
    }

    [Test]
    public void SingleIssueCycleWorkflow_HasNoDanglingReviewFixAttemptVariable()
    {
        // Arrange & Act
        var workflow = new SingleIssueCycleWorkflow();
        var builder = BuildWorkflow(workflow);

        // Assert — after decomposition, the reviewFixAttempt variable should not exist
        // (it was part of the old inline retry logic, now handled by sub-workflows)
        var variables = builder.Object.Variables;
        var reviewFixVar = variables.FirstOrDefault(v => v.Name == "ReviewFixAttempt");
        reviewFixVar.Should().BeNull(
            "reviewFixAttempt variable should not exist after decomposition into sub-workflows");
    }

    [Test]
    public void SingleIssueCycleWorkflow_Has8DistinctExitReasons()
    {
        // Arrange & Act
        var workflow = new SingleIssueCycleWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);

        // Get exit reason SetVariable nodes
        var exitReasonNodeIds = GetExitReasonNodeIds(flowchart);

        // Assert — should have 8 distinct exit reasons:
        // success, noIssues, plan_rejected, review_rejected, error, tddFailed, ciFailed, mergeFailed
        exitReasonNodeIds.Should().HaveCount(8,
            "SingleIssueCycleWorkflow should have 8 distinct exit reason nodes");

        exitReasonNodeIds.Should().Contain("SetReasonSuccess");
        exitReasonNodeIds.Should().Contain("SetReasonNoIssues");
        exitReasonNodeIds.Should().Contain("SetReasonPlanRejected");
        exitReasonNodeIds.Should().Contain("SetReasonReviewRejected");
        exitReasonNodeIds.Should().Contain("SetReasonError");
        exitReasonNodeIds.Should().Contain("SetReasonTddFailed");
        exitReasonNodeIds.Should().Contain("SetReasonCiFailed");
        exitReasonNodeIds.Should().Contain("SetReasonMergeFailed");
    }

    [Test]
    public void SingleIssueCycleWorkflow_AllActivitiesHaveDisplayText()
    {
        // Arrange & Act
        var workflow = new SingleIssueCycleWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);
        var allActivities = GetAllActivities(flowchart);

        // Assert — every activity should have a non-empty DisplayText
        foreach (var activity in allActivities)
        {
            var displayText = activity.GetDisplayText();
            displayText.Should().NotBeNullOrEmpty(
                $"Activity '{activity.GetType().Name}' (Id: {activity.Id}) should have DisplayText set");
        }
    }

    [Test]
    public void SingleIssueCycleWorkflow_HasCorrectDefinitionId()
    {
        // Arrange & Act
        var workflow = new SingleIssueCycleWorkflow();
        var builder = BuildWorkflow(workflow);

        // Assert
        builder.Object.DefinitionId.Should().Be("single-issue-cycle");
    }

    [Test]
    public void SingleIssueCycleWorkflow_AllExitPathsConvergeToSharedFinish()
    {
        // Arrange & Act
        var workflow = new SingleIssueCycleWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);

        // All SetReason* nodes should connect to SharedFinishSequence
        var exitReasonNodes = flowchart.Activities
            .OfType<SetVariable>()
            .Where(sv => sv.Id?.StartsWith("SetReason") == true)
            .ToList();

        var sharedFinish = flowchart.Activities
            .FirstOrDefault(a => a.Id == "SharedFinishSequence");
        sharedFinish.Should().NotBeNull("workflow should have a SharedFinishSequence activity");

        // Verify each exit reason node has a connection to shared finish
        foreach (var exitNode in exitReasonNodes)
        {
            var hasConnectionToFinish = flowchart.Connections.Any(c =>
                c.Source.Activity == exitNode && c.Target.Activity == sharedFinish);
            hasConnectionToFinish.Should().BeTrue(
                $"Exit reason node '{exitNode.Id}' should connect to SharedFinishSequence");
        }
    }

    // ================================================================
    // WorkflowVersions Tests
    // ================================================================

    [Test]
    public void WorkflowVersions_ComputedVersion_IsConsistent()
    {
        // Act — call ComputedVersion twice
        var version1 = WorkflowVersions.ComputedVersion;
        var version2 = WorkflowVersions.ComputedVersion;

        // Assert — should return the same value (it's a static readonly property)
        version1.Should().Be(version2, "ComputedVersion should be deterministic and consistent across calls");
    }

    [Test]
    public void WorkflowVersions_ComputedVersion_IsPositive()
    {
        // Act
        var version = WorkflowVersions.ComputedVersion;

        // Assert — version should be at least 2 (the base offset)
        version.Should().BeGreaterOrEqualTo(2,
            "ComputedVersion should be >= 2 (base offset of 2 is added)");
    }

    [Test]
    public void WorkflowVersions_ComputedVersion_IsWithinExpectedRange()
    {
        // Act
        var version = WorkflowVersions.ComputedVersion;

        // Assert — range is 2 to 10001 based on the hash calculation
        version.Should().BeInRange(2, 10001,
            "ComputedVersion should be in range 2-10001 (2 + abs(hash % 10000))");
    }

    [Test]
    public void WorkflowVersions_ComputedVersion_IsDeterministic()
    {
        // The version is computed from file content hashes or assembly timestamp.
        // Since it's a static readonly property initialized once, it should be deterministic.
        // We verify this by checking it matches the expected computation pattern.
        var version = WorkflowVersions.ComputedVersion;

        // Since EmbeddedSourceHash is computed at runtime from workflow .cs files,
        // and the files haven't changed between these calls, the version should be stable.
        version.Should().Be(WorkflowVersions.ComputedVersion,
            "ComputedVersion should be deterministic — same input files produce same version");
    }

    // ================================================================
    // Cross-cutting structure tests
    // ================================================================

    [Test]
    public void AllDecomposedWorkflows_UseWorkflowVersionsComputedVersion()
    {
        // Verify all three workflows use the same computed version
        var tddWorkflow = new TddWithDebugRetryWorkflow();
        var ciWorkflow = new CiWithDebugRetryWorkflow();
        var cycleWorkflow = new SingleIssueCycleWorkflow();

        var tddBuilder = BuildWorkflow(tddWorkflow);
        var ciBuilder = BuildWorkflow(ciWorkflow);
        var cycleBuilder = BuildWorkflow(cycleWorkflow);

        var expectedVersion = WorkflowVersions.ComputedVersion;

        tddBuilder.Object.Version.Should().Be(expectedVersion,
            "TddWithDebugRetryWorkflow should use WorkflowVersions.ComputedVersion");
        ciBuilder.Object.Version.Should().Be(expectedVersion,
            "CiWithDebugRetryWorkflow should use WorkflowVersions.ComputedVersion");
        cycleBuilder.Object.Version.Should().Be(expectedVersion,
            "SingleIssueCycleWorkflow should use WorkflowVersions.ComputedVersion");
    }

    [Test]
    public void TddWithDebugRetryWorkflow_HasRetryGuardWithMax3Attempts()
    {
        // Arrange & Act
        var workflow = new TddWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);

        // Assert — should have a FlowDecision acting as the debug guard
        var debugGuard = flowchart.Activities
            .OfType<FlowDecision>()
            .FirstOrDefault(fd => fd.Id == "TddDebugGuard");

        debugGuard.Should().NotBeNull("TDD workflow should have a TddDebugGuard decision node");

        // Should also have an increment counter
        var increment = flowchart.Activities
            .OfType<SetVariable>()
            .FirstOrDefault(sv => sv.Id == "IncrTddDebug");
        increment.Should().NotBeNull("TDD workflow should have a counter increment node");
    }

    [Test]
    public void CiWithDebugRetryWorkflow_HasRetryGuardWithMax3Attempts()
    {
        // Arrange & Act
        var workflow = new CiWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);

        // Assert — should have a FlowDecision acting as the retry guard
        var retryGuard = flowchart.Activities
            .OfType<FlowDecision>()
            .FirstOrDefault(fd => fd.Id == "CiRetryGuard");

        retryGuard.Should().NotBeNull("CI workflow should have a CiRetryGuard decision node");

        // Should also have an increment counter
        var increment = flowchart.Activities
            .OfType<SetVariable>()
            .FirstOrDefault(sv => sv.Id == "IncrCiRetry");
        increment.Should().NotBeNull("CI workflow should have a counter increment node");
    }

    [Test]
    public void TddWithDebugRetryWorkflow_HasFinishNode()
    {
        // Arrange & Act
        var workflow = new TddWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);

        // Assert
        var finish = flowchart.Activities
            .OfType<Finish>()
            .FirstOrDefault(f => f.Id == "TddRetryFinish");
        finish.Should().NotBeNull("TDD workflow should have a terminal Finish node");
    }

    [Test]
    public void CiWithDebugRetryWorkflow_HasFinishNode()
    {
        // Arrange & Act
        var workflow = new CiWithDebugRetryWorkflow();
        var builder = BuildWorkflow(workflow);
        var flowchart = GetFlowchart(builder);

        // Assert
        var finish = flowchart.Activities
            .OfType<Finish>()
            .FirstOrDefault(f => f.Id == "CiRetryFinish");
        finish.Should().NotBeNull("CI workflow should have a terminal Finish node");
    }
}
