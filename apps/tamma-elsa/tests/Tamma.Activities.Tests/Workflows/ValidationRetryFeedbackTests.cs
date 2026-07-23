using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Regression tests for the validate→retry feedback bug: PlanGeneration,
/// TaskCreation, and TestCaseCreation used to pass validation errors to the
/// llm-call dispatch under a <c>validationErrors</c> key that NO template
/// declares — <c>PromptStoreService.Render</c> substitutes only declared
/// {{placeholders}}, so the feedback was silently dropped and every retry
/// re-prompted blind.
///
/// The fix merges the errors INTO a variable the target template actually
/// declares (Plan family → <c>contextFindings</c>; WriteTests →
/// <c>testTarget</c>), as a clearly-delimited block. These tests materialise
/// each workflow's dispatch Input delegate against a minimal expression
/// context (the TaxonomyDriftBuildTests / UpdateIssueStatusWorkflowTests
/// idiom) and assert:
///  - retry dispatch: error text present under the DECLARED key, verbatim;
///  - no dead <c>validationErrors</c> key remains;
///  - first attempt: variables unchanged from before the fix (minus the dead key).
/// </summary>
[TestFixture]
public class ValidationRetryFeedbackTests
{
    private const string SampleErrors = "Missing 'tasks' or 'steps'; Missing file map";

    // ====================================================================
    // ValidationFeedbackHelper — pure formatting
    // ====================================================================

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void AppendFeedback_NoErrors_ReturnsBaseUnchanged(string? errors)
    {
        ValidationFeedbackHelper.AppendFeedback("base context", errors)
            .Should().Be("base context",
                "first attempt (no validation errors) must leave the declared variable's value untouched");
    }

    [Test]
    public void AppendFeedback_NoErrors_NullBase_ReturnsEmptyString()
    {
        ValidationFeedbackHelper.AppendFeedback(null, null).Should().BeEmpty();
    }

    [Test]
    public void AppendFeedback_WithErrors_AppendsDelimitedBulletBlock()
    {
        var merged = ValidationFeedbackHelper.AppendFeedback("PO summary text", SampleErrors);

        merged.Should().Be(
            "PO summary text\n\n" +
            ValidationFeedbackHelper.FeedbackHeader + "\n" +
            "- Missing 'tasks' or 'steps'\n" +
            "- Missing file map");
    }

    [Test]
    public void AppendFeedback_EmptyBase_ReturnsBlockWithoutLeadingBlankLines()
    {
        var merged = ValidationFeedbackHelper.AppendFeedback(null, "Empty plan");

        merged.Should().Be(
            ValidationFeedbackHelper.FeedbackHeader + "\n- Empty plan",
            "when the carrier variable has no base content the block must not start with blank lines");
    }

    [Test]
    public void AppendFeedback_ValidatorErrors_FlowVerbatim()
    {
        // The validate step joins individual errors with "; "; the helper must unpack
        // each into its own bullet verbatim. (The PlanGeneration retry-loop cases were
        // retired in Story 39-14 — the lifecycle now owns validate → repair/revise; the
        // render-drop contract stays pinned on ValidationFeedbackHelper here.)
        const string errors = "Missing 'tasks' or 'steps'; Missing file map";
        var merged = ValidationFeedbackHelper.AppendFeedback("ctx", errors);

        merged.Should().Contain("- Missing 'tasks' or 'steps'",
            "validator error strings must flow through verbatim, one bullet each");
        merged.Should().Contain("- Missing file map");
    }

    // ====================================================================
    // Story 39-14 — PlanGeneration's bespoke validate→retry loop is RETIRED.
    // Its carrier-merge contract now lives inside the document-lifecycle binding;
    // PlanBindingHelperTests pins the merged contextFindings arrives (with NO dead
    // decompositionJson/validationErrors key) at this story's dispatch seam. The
    // TaskCreation / TestCaseCreation sections below stay (39-15 scope).
    // ====================================================================

    // ====================================================================
    // TaskCreationWorkflow — carrier: contextFindings (shared Plan family template)
    // ====================================================================

    [Test]
    public void TaskCreation_FirstAttempt_VariableKeys_UnchangedFromToday()
    {
        var vars = MaterializeDispatchVariables(new TaskCreationWorkflow(), "GenerateTasks");

        vars.Keys.Should().BeEquivalentTo(new[]
        {
            "planJson", "contextIds", "workItemJson", "repository",
        }, "first attempt must not add contextFindings (declared-but-unsupplied today, so " +
           "supplying it — even empty — would change the rendered prompt) nor keep the dead validationErrors key");
    }

    [Test]
    public void TaskCreation_Retry_SuppliesContextFindingsCarryingErrors()
    {
        const string errors = "Missing 'tasks' array in response";

        var vars = MaterializeDispatchVariables(
            new TaskCreationWorkflow(), "GenerateTasks",
            new Dictionary<string, string>
            {
                ["PlanJson"] = "{\"tasks\":[]}",
                ["ValidationErrors"] = errors,
            });

        var contextFindings = vars["contextFindings"].Should().BeOfType<string>().Subject;
        contextFindings.Should().Contain(ValidationFeedbackHelper.FeedbackHeader);
        contextFindings.Should().Contain("- " + errors, "the validate step's error text must flow through verbatim");

        vars.Should().NotContainKey("validationErrors");
        vars["planJson"].Should().Be("{\"tasks\":[]}", "the rest of the retry dispatch variables must be unchanged");
    }

    // ====================================================================
    // TestCaseCreationWorkflow — carrier: testTarget (WriteTests template)
    // ====================================================================

    [Test]
    public void TestCaseCreation_FirstAttempt_VariableKeys_UnchangedFromToday()
    {
        var vars = MaterializeDispatchVariables(new TestCaseCreationWorkflow(), "GenerateTests");

        vars.Keys.Should().BeEquivalentTo(new[]
        {
            "tasksJson", "contextIds", "repository", "branchName",
        }, "first attempt must not add testTarget (declared-but-unsupplied today) nor keep the dead validationErrors key");
    }

    [Test]
    public void TestCaseCreation_Retry_SuppliesTestTargetCarryingErrors()
    {
        const string errors = "Missing 'testCases' or 'tests' property";

        var vars = MaterializeDispatchVariables(
            new TestCaseCreationWorkflow(), "GenerateTests",
            new Dictionary<string, string> { ["ValidationErrors"] = errors });

        var testTarget = vars["testTarget"].Should().BeOfType<string>().Subject;
        testTarget.Should().Contain(ValidationFeedbackHelper.FeedbackHeader);
        testTarget.Should().Contain("- " + errors, "the validate step's error text must flow through verbatim");

        vars.Should().NotContainKey("validationErrors");
    }

    // ====================================================================
    // Materialisation plumbing (TaxonomyDriftBuildTests / UpdateIssueStatus idiom)
    // ====================================================================

    /// <summary>
    /// Build <paramref name="workflow"/> via the mock builder, find the llm-call
    /// <see cref="DispatchWorkflow"/> with <paramref name="dispatchId"/>, and
    /// invoke its Input delegate against a minimal expression context in which
    /// every captured variable is declared at its default and any variable named
    /// in <paramref name="seedByName"/> is pre-set (simulating loop state, e.g.
    /// ValidationErrors after a failed validate). Returns the nested
    /// <c>["variables"]</c> dictionary handed to the llm-call dispatch.
    /// </summary>
    private static IDictionary<string, object> MaterializeDispatchVariables(
        WorkflowBase workflow,
        string dispatchId,
        IReadOnlyDictionary<string, string>? seedByName = null)
    {
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var dispatch = flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == dispatchId);
        dispatch.Should().NotBeNull($"dispatch activity '{dispatchId}' must exist in the flowchart");

        var inputValue = typeof(DispatchWorkflow).GetProperty("Input")!.GetValue(dispatch);
        var expression = inputValue!.GetType()
            .GetProperty("Expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(inputValue) as Expression;
        expression?.Value.Should().BeAssignableTo<Delegate>("the dispatch Input must be a delegate-backed input");
        var del = (Delegate)expression!.Value!;

        var memory = new MemoryRegister(new Dictionary<string, MemoryBlock>());
        var ctx = new ExpressionExecutionContext(
            NullServiceProvider.Instance, memory, null, null, null, default);

        var counter = 0;
        foreach (var reference in CapturedReferences(del))
        {
            EnsureUniqueId(reference, ref counter);
            try { memory.Declare(reference); } catch { /* default resolves below */ }

            if (seedByName != null &&
                reference is Variable<string> sv &&
                sv.Name is { Length: > 0 } name &&
                seedByName.TryGetValue(name, out var seeded))
            {
                sv.Set(ctx, seeded);
            }
        }

        var raw = Unwrap(del.DynamicInvoke(ctx));
        var input = raw.Should().BeAssignableTo<IDictionary<string, object>>().Subject;
        input.Should().ContainKey("variables");
        return input["variables"].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
    }

    private static IEnumerable<MemoryBlockReference> CapturedReferences(Delegate del)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        if (del.Target != null) stack.Push(del.Target);

        while (stack.Count > 0)
        {
            var obj = stack.Pop();
            if (obj == null || !seen.Add(obj)) continue;
            if (obj is string || obj.GetType().IsPrimitive) continue;

            if (obj is MemoryBlockReference reference)
            {
                yield return reference;
                continue;
            }

            foreach (var field in obj.GetType()
                         .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? value;
                try { value = field.GetValue(obj); }
                catch { continue; }
                if (value == null || value is string || value.GetType().IsPrimitive) continue;
                if (value is MemoryBlockReference r) yield return r;
                else if (value.GetType().IsClass) stack.Push(value);
            }
        }
    }

    private static void EnsureUniqueId(MemoryBlockReference reference, ref int counter)
    {
        try { if (!string.IsNullOrEmpty(reference.Id)) return; }
        catch { return; }
        var idProp = typeof(MemoryBlockReference).GetProperty("Id",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (idProp?.CanWrite == true)
        {
            try { idProp.SetValue(reference, $"__vrf_{counter++}"); }
            catch { /* leave as-is */ }
        }
    }

    private static object? Unwrap(object? raw)
    {
        if (raw == null) return null;
        var type = raw.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = type.GetMethod("AsTask")!.Invoke(raw, null);
            return asTask!.GetType().GetProperty("Result")!.GetValue(asTask);
        }
        return raw;
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
