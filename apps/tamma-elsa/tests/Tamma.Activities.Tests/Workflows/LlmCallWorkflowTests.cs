using System.Reflection;
using Elsa.Expressions.Models;
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
using Tamma.Activities.LlmCall;
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

    // ================================================================
    // Typed-dispatch prompt fix (Story 32-5 T6 follow-up) — the
    // registry-rendered prompt / role / action / variables / registry
    // MaxTokens must be wired from the workflow variables into
    // CallLlmInlineActivity, and the ResolvePrompt MaxTokens output must
    // be bound (it was previously dropped).
    // ================================================================

    [Test]
    public void HasRegistryMaxTokensVariable()
    {
        var varNames = _builder.Object.Variables
            .Where(v => v.Name != null).Select(v => v.Name).ToList();

        varNames.Should().Contain("RegistryMaxTokens",
            "the registry-resolved MaxTokens output needs a variable to land in");
    }

    [Test]
    public void ResolvePrompt_BindsMaxTokensOutput_ToRegistryMaxTokensVariable()
    {
        var resolvePrompt = _flowchart.Activities
            .OfType<ResolvePromptFromRegistryActivity>()
            .FirstOrDefault(a => a.Id == "ResolvePrompt");
        resolvePrompt.Should().NotBeNull();

        resolvePrompt!.MaxTokens.Should().NotBeNull(
            "the MaxTokens output must be bound — previously it was dropped, so the registry MaxTokens never reached the provider");

        var boundBlock = resolvePrompt.MaxTokens.MemoryBlockReference();
        var registryVar = _builder.Object.Variables.First(v => v.Name == "RegistryMaxTokens");
        boundBlock.Should().BeSameAs(registryVar,
            "the MaxTokens output must write the RegistryMaxTokens workflow variable");
    }

    [Test]
    public void CallLlm_TypedDispatchProps_ReadTheWorkflowVariables()
    {
        // The load-bearing wiring for THE BUG: on typed dispatches the wire
        // request is built from these props, so each must evaluate to the
        // corresponding workflow variable's value.
        var callLlm = WalkAll(_flowchart)
            .OfType<Tamma.Activities.LlmCall.CallLlmInlineActivity>()
            .FirstOrDefault(a => a.Id == "CallLlm");
        callLlm.Should().NotBeNull("the retry loop must contain the CallLlm activity");

        var ctx = CreateContextWithVariables(new Dictionary<string, object?>
        {
            ["TaskPrompt"] = "RENDERED-REGISTRY-PROMPT",
            ["AgentRole"] = "architect",
            ["Action"] = "plan-implementation",
            ["VariablesJson"] = "{\"conventions\":\"use tabs\"}",
            ["RegistryMaxTokens"] = 8192,
        });

        EvaluateInput(callLlm!.RenderedPromptProp, ctx).Should().Be("RENDERED-REGISTRY-PROMPT",
            "RenderedPromptProp must read the registry-rendered TaskPrompt variable");
        EvaluateInput(callLlm.AgentRoleProp, ctx).Should().Be("architect",
            "AgentRoleProp must read the AgentRole variable");
        EvaluateInput(callLlm.ActionProp, ctx).Should().Be("plan-implementation",
            "ActionProp must read the Action variable");
        EvaluateInput(callLlm.VariablesJsonProp, ctx).Should().Be("{\"conventions\":\"use tabs\"}",
            "VariablesJsonProp must read the merged VariablesJson variable");
        EvaluateInput(callLlm.RegistryMaxTokensProp, ctx).Should().Be(8192,
            "RegistryMaxTokensProp must read the RegistryMaxTokens variable");
    }

    // ================================================================
    // Helpers for the typed-dispatch wiring tests
    // ================================================================

    /// <summary>Depth-first walk over the built activity graph, descending into
    /// the container types LlmCallWorkflow uses (Flowchart / Sequence / If /
    /// While / ForEach&lt;string&gt;).</summary>
    private static IEnumerable<IActivity> WalkAll(IActivity activity)
    {
        yield return activity;

        var children = activity switch
        {
            Flowchart f => f.Activities.AsEnumerable(),
            Sequence s => s.Activities.AsEnumerable(),
            If i => new[] { i.Then, i.Else }.Where(a => a != null).Cast<IActivity>(),
            While w => w.Body != null ? new[] { w.Body } : Enumerable.Empty<IActivity>(),
            ForEach<string> fe => fe.Body != null ? new[] { fe.Body } : Enumerable.Empty<IActivity>(),
            _ => Enumerable.Empty<IActivity>(),
        };

        foreach (var child in children)
            foreach (var descendant in WalkAll(child))
                yield return descendant;
    }

    /// <summary>Builds a minimal <see cref="ExpressionExecutionContext"/> with
    /// every workflow variable declared, and the named ones pre-set — so a
    /// delegate-backed <c>Input&lt;T&gt;</c> can be evaluated for real (the
    /// pattern established by <c>UpdateIssueStatusWorkflowTests</c>).</summary>
    private ExpressionExecutionContext CreateContextWithVariables(Dictionary<string, object?> values)
    {
        var memory = new MemoryRegister(new Dictionary<string, MemoryBlock>());
        var ctx = new ExpressionExecutionContext(
            NullServiceProvider.Instance, memory, null, null, null, default);

        var counter = 0;
        foreach (var variable in _builder.Object.Variables)
        {
            EnsureUniqueId(variable, ref counter);
            try { memory.Declare(variable); } catch { /* duplicate ids resolve below */ }
        }

        foreach (var variable in _builder.Object.Variables)
        {
            if (variable.Name != null && values.TryGetValue(variable.Name, out var value))
                variable.Set(ctx, value);
        }

        return ctx;
    }

    /// <summary>Extracts the delegate behind a delegate-backed input and invokes
    /// it against the given context.</summary>
    private static object? EvaluateInput(object input, ExpressionExecutionContext ctx)
    {
        var expression = input.GetType()
            .GetProperty("Expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(input) as Expression;

        expression.Should().NotBeNull("the prop must be wired to an expression (not left at its literal default)");
        expression!.Value.Should().BeAssignableTo<Delegate>(
            "the prop must be delegate-backed (reading a workflow variable), not a literal");

        return ((Delegate)expression.Value!).DynamicInvoke(ctx);
    }

    private static void EnsureUniqueId(MemoryBlockReference reference, ref int counter)
    {
        try { if (!string.IsNullOrEmpty(reference.Id)) return; }
        catch { return; }
        var idProp = typeof(MemoryBlockReference).GetProperty("Id",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (idProp?.CanWrite == true)
        {
            try { idProp.SetValue(reference, $"__llmwf_{counter++}"); }
            catch { /* leave as-is */ }
        }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
