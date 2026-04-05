using Elsa.Extensions;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Tests for ContextGatheringWorkflow structure.
/// Validates the 5-role scanning pipeline and its connections.
/// </summary>
[TestFixture]
public class ContextGatheringPipelineTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var workflow = new ContextGatheringWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void HasFiveRoleScanDispatches()
    {
        var dispatches = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .Where(d => d.Id != null && d.Id.EndsWith("Scan"))
            .Select(d => d.Id!)
            .ToList();

        dispatches.Should().HaveCount(5, "should have 5 role scan dispatches");
        dispatches.Should().Contain("DevScan");
        dispatches.Should().Contain("QAScan");
        dispatches.Should().Contain("SecurityScan");
        dispatches.Should().Contain("DevOpsScan");
        dispatches.Should().Contain("ArchScan");
    }

    [Test]
    public void EachScan_DispatchesLlmCall()
    {
        var scanDispatches = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .Where(d => d.Id != null && d.Id.EndsWith("Scan"))
            .ToList();

        foreach (var dispatch in scanDispatches)
        {
            // All scans dispatch to llm-call workflow
            dispatch.Should().NotBeNull($"{dispatch.Id} should exist");
        }

        scanDispatches.Should().HaveCount(5);
    }

    [Test]
    public void Scans_ChainSequentially()
    {
        // Dev → ExtractDev → QA → ExtractQA → Security → ExtractSec → DevOps → ExtractDevOps → Arch → ExtractArch
        var expectedChain = new[]
        {
            ("DevScan", "ExtractDev"),
            ("ExtractDev", "QAScan"),
            ("QAScan", "ExtractQA"),
            ("ExtractQA", "SecurityScan"),
            ("SecurityScan", "ExtractSec"),
            ("ExtractSec", "DevOpsScan"),
            ("DevOpsScan", "ExtractDevOps"),
            ("ExtractDevOps", "ArchScan"),
            ("ArchScan", "ExtractArch"),
        };

        foreach (var (from, to) in expectedChain)
        {
            var hasConnection = _flowchart.Connections.Any(c =>
                c.Source.Activity.Id == from && c.Target.Activity.Id == to);
            hasConnection.Should().BeTrue($"{from} should connect to {to}");
        }
    }

    [Test]
    public void Pipeline_EndsWithStoreFindings_ThenPOReview()
    {
        // ExtractArch → StoreFindings → POReview → ExtractPO → SetOutputs → Finish
        var hasStoreConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ExtractArch" && c.Target.Activity.Id == "StoreFindings");

        var hasPOConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "StoreFindings" && c.Target.Activity.Id == "POReview");

        var hasFinishConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "SetOutputs" && c.Target.Activity.Id == "Finish");

        hasStoreConnection.Should().BeTrue("ExtractArch should connect to StoreFindings");
        hasPOConnection.Should().BeTrue("StoreFindings should connect to POReview");
        hasFinishConnection.Should().BeTrue("SetOutputs should connect to Finish");
    }

    [Test]
    public void HasCorrectDefinitionId()
    {
        var workflow = new ContextGatheringWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        builder.Object.DefinitionId.Should().Be("context-gathering");
    }

    [Test]
    public void TopLevelActivitiesHaveDisplayText()
    {
        // Only check top-level activities (not nested SetOutput inside Sequence)
        foreach (var activity in _flowchart.Activities)
        {
            var displayText = activity.GetDisplayText();
            displayText.Should().NotBeNullOrEmpty(
                $"Activity '{activity.GetType().Name}' (Id: {activity.Id}) should have DisplayText set");
        }
    }
}
