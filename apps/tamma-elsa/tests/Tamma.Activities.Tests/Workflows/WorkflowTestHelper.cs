using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using FluentAssertions;
using Moq;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Shared helper for building workflows via mock IWorkflowBuilder.
/// Extracted from WorkflowStructureTests for reuse across test files.
/// </summary>
public static class WorkflowTestHelper
{
    /// <summary>
    /// Creates a mock IWorkflowBuilder, invokes the protected Build() method
    /// on the given WorkflowBase instance, and returns the mock for inspection.
    /// </summary>
    public static Mock<IWorkflowBuilder> BuildWorkflow(WorkflowBase workflow)
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

        // WithVariable<T>() — no args
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

        // WithVariable<T>(string name, T defaultValue)
        mockBuilder.Setup(b => b.WithVariable<string>(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string varName, string defaultValue) => { var v = new Variable<string>(varName, defaultValue); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<int>(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string varName, int defaultValue) => { var v = new Variable<int>(varName, defaultValue); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<bool>(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string varName, bool defaultValue) => { var v = new Variable<bool>(varName, defaultValue); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<Guid>(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns((string varName, Guid defaultValue) => { var v = new Variable<Guid>(varName, defaultValue); variables.Add(v); return v; });
        mockBuilder.Setup(b => b.WithVariable<string[]>(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns((string varName, string[] defaultValue) => { var v = new Variable<string[]>(varName, defaultValue); variables.Add(v); return v; });
        // DateTime overload — used by Story 28-10's HourlyAnalyticsRollupWorkflow.
        mockBuilder.Setup(b => b.WithVariable<DateTime>(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns((string varName, DateTime defaultValue) => { var v = new Variable<DateTime>(varName, defaultValue); variables.Add(v); return v; });

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
    public static Flowchart GetFlowchart(Mock<IWorkflowBuilder> mockBuilder)
    {
        var root = mockBuilder.Object.Root;
        root.Should().NotBeNull("workflow should set builder.Root");
        root.Should().BeOfType<Flowchart>("workflow root should be a Flowchart");
        return (Flowchart)root;
    }

    /// <summary>
    /// Gets all activities from a Flowchart, including nested activities inside Sequence nodes.
    /// </summary>
    public static List<IActivity> GetAllActivities(Flowchart flowchart)
    {
        var activities = new List<IActivity>();
        foreach (var activity in flowchart.Activities)
        {
            activities.Add(activity);
            if (activity is Sequence seq)
            {
                activities.AddRange(seq.Activities);
            }
        }
        return activities;
    }
}
