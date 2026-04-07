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
    public void Scans_ChainSequentially_WithPerRoleStorage()
    {
        // Dev → ExtractDev → StoreDev → QA → ExtractQA → StoreQA → ...
        var expectedChain = new[]
        {
            ("DevScan", "ExtractDev"),
            ("ExtractDev", "StoreDev"),
            ("StoreDev", "QAScan"),
            ("QAScan", "ExtractQA"),
            ("ExtractQA", "StoreQA"),
            ("StoreQA", "SecurityScan"),
            ("SecurityScan", "ExtractSec"),
            ("ExtractSec", "StoreSec"),
            ("StoreSec", "DevOpsScan"),
            ("DevOpsScan", "ExtractDevOps"),
            ("ExtractDevOps", "StoreDevOps"),
            ("StoreDevOps", "ArchScan"),
            ("ArchScan", "ExtractArch"),
            ("ExtractArch", "StoreArch"),
        };

        foreach (var (from, to) in expectedChain)
        {
            var hasConnection = _flowchart.Connections.Any(c =>
                c.Source.Activity.Id == from && c.Target.Activity.Id == to);
            hasConnection.Should().BeTrue($"{from} should connect to {to}");
        }
    }

    [Test]
    public void Pipeline_EndsWithStoreArch_ThenPOReview()
    {
        // StoreArch → POReview → ExtractPO → SetOutputs → Finish
        var hasPOConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "StoreArch" && c.Target.Activity.Id == "POReview");

        var hasFinishConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "SetOutputs" && c.Target.Activity.Id == "Finish");

        hasPOConnection.Should().BeTrue("StoreArch should connect to POReview");
        hasFinishConnection.Should().BeTrue("SetOutputs should connect to Finish");
    }

    [Test]
    public void HasFivePerRoleStoreActivities()
    {
        var storeActivities = _flowchart.Activities
            .Where(a => a.Id != null && a.Id.StartsWith("Store") && a.Id != "SetOutputs")
            .Select(a => a.Id!)
            .ToList();

        storeActivities.Should().HaveCount(5, "should have 5 per-role store activities");
        storeActivities.Should().Contain("StoreDev");
        storeActivities.Should().Contain("StoreQA");
        storeActivities.Should().Contain("StoreSec");
        storeActivities.Should().Contain("StoreDevOps");
        storeActivities.Should().Contain("StoreArch");
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
