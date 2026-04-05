using System.Reflection;
using Elsa.Extensions;
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
/// Structural tests for LlmCallWorkflow.
/// Validates the workflow has expected activities, variables, and outputs.
/// Uses a custom builder because LlmCallWorkflow uses Variable&lt;object&gt;.
/// </summary>
[TestFixture]
public class LlmCallWorkflowTests
{
    private Flowchart _flowchart = null!;
    private Mock<IWorkflowBuilder> _builder = null!;

    /// <summary>
    /// Builds LlmCallWorkflow with extra Variable&lt;object&gt; mock support.
    /// </summary>
    private static Mock<IWorkflowBuilder> BuildLlmCallWorkflow(WorkflowBase workflow)
    {
        var mockBuilder = new Mock<IWorkflowBuilder>();

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

        // No-arg variants
        mockBuilder.Setup(b => b.WithVariable<string>())
            .Returns(() => { var v = new Variable<string>(); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<int>())
            .Returns(() => { var v = new Variable<int>(); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<bool>())
            .Returns(() => { var v = new Variable<bool>(); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<string[]>())
            .Returns(() => { var v = new Variable<string[]>(); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<IDictionary<string, object>?>())
            .Returns(() => { var v = new Variable<IDictionary<string, object>?>(); variables.Add(v); return v; });

        // Named variants — object first (least specific), then more specific types override
        mockBuilder.Setup(b => b.WithVariable<object>(It.IsAny<string>(), It.IsAny<object>()))
            .Returns((string varName, object defaultValue) => { var v = new Variable<object>(varName, defaultValue); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<string>(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string varName, string defaultValue) => { var v = new Variable<string>(varName, defaultValue); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<int>(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string varName, int defaultValue) => { var v = new Variable<int>(varName, defaultValue); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<bool>(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string varName, bool defaultValue) => { var v = new Variable<bool>(varName, defaultValue); variables.Add(v); return v; });

        var buildMethod = workflow.GetType().GetMethod(
            "Build", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(IWorkflowBuilder) }, null);
        buildMethod.Should().NotBeNull();
        buildMethod!.Invoke(workflow, new object[] { mockBuilder.Object });

        return mockBuilder;
    }

    [SetUp]
    public void SetUp()
    {
        var workflow = new LlmCallWorkflow();
        _builder = BuildLlmCallWorkflow(workflow);
        var root = _builder.Object.Root;
        root.Should().BeOfType<Flowchart>();
        _flowchart = (Flowchart)root;
    }

    [Test]
    public void BuildsWithoutError()
    {
        var workflow = new LlmCallWorkflow();
        var act = () => BuildLlmCallWorkflow(workflow);
        act.Should().NotThrow();
    }

    [Test]
    public void HasCorrectDefinitionId()
    {
        _builder.Object.DefinitionId.Should().Be("llm-call");
    }

    [Test]
    public void HasInitInputsActivity()
    {
        var init = _flowchart.Activities.FirstOrDefault(a => a.Id == "InitInputs");
        init.Should().NotBeNull("LlmCallWorkflow should have an InitInputs activity");
    }

    [Test]
    public void HasBudgetGuardNode()
    {
        var budgetNodes = _flowchart.Activities
            .Where(a => a.Id != null && (a.Id.Contains("Budget") || a.Id.Contains("budget")))
            .ToList();

        budgetNodes.Should().HaveCountGreaterOrEqualTo(1,
            "LlmCallWorkflow should have at least one budget-related node");
    }

    [Test]
    public void HasRetryGuardForTransientErrors()
    {
        var retryNodes = _flowchart.Activities
            .OfType<FlowDecision>()
            .Where(fd => fd.Id != null &&
                (fd.Id.Contains("Retry") || fd.Id.Contains("retry") ||
                 fd.Id.Contains("Attempt") || fd.Id.Contains("attempt") ||
                 fd.Id.Contains("FailureCheck") || fd.Id.Contains("Success")))
            .ToList();

        retryNodes.Should().HaveCountGreaterOrEqualTo(1,
            "LlmCallWorkflow should have at least one retry/failure check decision node");
    }

    [Test]
    public void HasOutputActivities()
    {
        var allActivities = WorkflowTestHelper.GetAllActivities(_flowchart);

        var setOutputs = allActivities
            .Where(a => a.GetType().Name == "SetOutput")
            .ToList();

        setOutputs.Should().HaveCountGreaterOrEqualTo(3,
            "LlmCallWorkflow should output at least llmResponse, providerUsed, success");
    }

    [Test]
    public void HasProviderChainVariables()
    {
        var variables = _builder.Object.Variables;
        var varNames = variables.Where(v => v.Name != null).Select(v => v.Name).ToList();

        varNames.Should().Contain("CurrentProvider");
        varNames.Should().Contain("CallSucceeded");
        varNames.Should().Contain("AttemptNumber");
        varNames.Should().Contain("MaxRetries");
    }

    [Test]
    public void TopLevelActivitiesHaveDisplayText()
    {
        foreach (var activity in _flowchart.Activities)
        {
            var displayText = activity.GetDisplayText();
            displayText.Should().NotBeNullOrEmpty(
                $"Activity '{activity.GetType().Name}' (Id: {activity.Id}) should have DisplayText set");
        }
    }

    [Test]
    public void HasSetOutputsNode()
    {
        var setOutputs = _flowchart.Activities
            .FirstOrDefault(a => a.Id == "SetOutputs");

        setOutputs.Should().NotBeNull(
            "LlmCallWorkflow should have a SetOutputs terminal node");
    }
}
