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

        // Resilient default-value generation: any unconfigured WithVariable<T>()
        // (no-arg) returns a real Variable<T> instead of null, so workflows that
        // declare variables of custom/uncommon types (Guid, DiagnosisResult,
        // Hypothesis?, …) build without NREs when their activity input delegates
        // call variable.Get(ctx). The explicit Setup() calls below still take
        // precedence; this only fills the gaps. Needed by Story 27-17's taxonomy
        // drift test, which builds EVERY workflow in the assembly.
        mockBuilder.DefaultValueProvider = new VariableDefaultValueProvider();

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
        // long overload — used by Story 29-6's RotateSecretWorkflow (GraceWindowSeconds).
        mockBuilder.Setup(b => b.WithVariable<long>(It.IsAny<string>(), It.IsAny<long>()))
            .Returns((string varName, long defaultValue) => { var v = new Variable<long>(varName, defaultValue); variables.Add(v); return v; });

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

    /// <summary>
    /// Moq default-value provider that materialises a real <see cref="Variable{T}"/>
    /// for any requested <c>Variable&lt;T&gt;</c> return type (so unconfigured
    /// <c>WithVariable&lt;T&gt;()</c> calls never yield null), delegating to Moq's
    /// empty provider for every other type.
    /// </summary>
    private sealed class VariableDefaultValueProvider : DefaultValueProvider
    {
        protected override object? GetDefaultValue(Type type, Mock mock)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Variable<>))
                return Activator.CreateInstance(type);

            // Mirror Moq's Empty provider for everything else: zero value for
            // value types, null for reference types.
            return type.IsValueType && Nullable.GetUnderlyingType(type) == null
                ? Activator.CreateInstance(type)
                : null;
        }
    }
}
