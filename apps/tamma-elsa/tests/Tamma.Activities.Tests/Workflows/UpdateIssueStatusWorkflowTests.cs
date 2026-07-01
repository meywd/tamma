using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 2.10 build-out — workflow-structure coverage for the built-out
/// <c>update-issue-status</c> graph. Asserts the load-bearing guarantees the
/// activity unit tests can't: the <b>failure edge exists</b> and the activity's
/// <c>Failed</c> outcome NEVER falls through to success (the headline
/// swallow-failure fix), both terminal transitions emit an <c>ISSUE_STATUS.*</c>
/// DCB event via the durable drain, every outcome is routed (no dangling edge),
/// and both paths reach Finish.
///
/// <para>Follows the codebase convention of inspecting the BUILT Flowchart via
/// <see cref="WorkflowTestHelper"/> rather than running the full Elsa runtime.</para>
/// </summary>
[TestFixture]
public class UpdateIssueStatusWorkflowTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new UpdateIssueStatusWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Identity
    // ================================================================

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new UpdateIssueStatusWorkflow());
        builder.Object.DefinitionId.Should().Be("update-issue-status");
    }

    [Test]
    public void Workflow_RootIsFlowchart_NotBareActivity()
    {
        // The old thin form set builder.Root = updateIssue (a bare activity). The
        // build-out must be a real flowchart with branches.
        _flowchart.Should().NotBeNull();
        _flowchart.Activities.OfType<UpdateIssueStatusActivity>()
            .Should().ContainSingle("the update activity must be a node in the flowchart");
    }

    // ================================================================
    // Flow shape
    // ================================================================

    [Test]
    public void Flow_ReadInputs_Then_UpdateIssue()
    {
        HasEdge("ReadInputs", null, "UpdateIssue").Should().BeTrue();
    }

    // ================================================================
    // No false success — the Failed outcome routes to the failure path ONLY
    // ================================================================

    [Test]
    public void UpdateIssue_FailedOutcome_RoutesToFailurePath_NotSuccess()
    {
        HasEdge("UpdateIssue", "Failed", "FailureOutputs").Should().BeTrue(
            "the Failed outcome must route to the explicit failure path");

        var failedTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "UpdateIssue" && c.Source.Port == "Failed")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        failedTargets.Should().NotContain("EmitSuccess");
        failedTargets.Should().NotContain("SuccessOutputs");
    }

    [Test]
    public void UpdateIssue_HasNoUnconditionalFallthrough()
    {
        // Every edge out of UpdateIssue must be outcome-qualified (Updated/Failed).
        // A portless edge would be the old silent fall-through bug.
        var fromUpdate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "UpdateIssue")
            .ToList();

        fromUpdate.Should().NotBeEmpty();
        fromUpdate.Should().OnlyContain(c => c.Source.Port == "Updated" || c.Source.Port == "Failed");
    }

    [Test]
    public void UpdateIssue_UpdatedOutcome_RoutesToSuccess()
    {
        HasEdge("UpdateIssue", "Updated", "EmitSuccess").Should().BeTrue();
    }

    // ================================================================
    // DCB events on every terminal transition (durable drain)
    // ================================================================

    [Test]
    public void SuccessPath_EmitsIssueStatusUpdatedSuccess()
    {
        var emit = _flowchart.Activities
            .OfType<EmitIssueStatusEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitSuccess");
        emit.Should().NotBeNull("success path must emit an ISSUE_STATUS DCB event");
        HasEdge("EmitSuccess", null, "SuccessOutputs").Should().BeTrue();
    }

    [Test]
    public void FailurePath_EmitsIssueStatusUpdatedFailed()
    {
        var emit = _flowchart.Activities
            .OfType<EmitIssueStatusEventActivity>()
            .FirstOrDefault(a => a.Id == "EmitFailed");
        emit.Should().NotBeNull("failure path must emit ISSUE_STATUS.UPDATED.FAILED");
        // success=false outputs must run before / into the failed-event emit.
        HasEdge("FailureOutputs", null, "EmitFailed").Should().BeTrue();
    }

    [Test]
    public void BothTerminalPaths_ReachFinish()
    {
        HasEdge("SuccessOutputs", null, "Finish").Should().BeTrue();
        HasEdge("EmitFailed", null, "Finish").Should().BeTrue();
    }

    // ================================================================
    // No dangling edge — every outcome / node routed to a terminal
    // ================================================================

    [Test]
    public void EveryConnection_PointsAtAKnownActivity()
    {
        var ids = _flowchart.Activities.Select(a => a.Id).ToHashSet();
        foreach (var c in _flowchart.Connections)
        {
            ids.Should().Contain(c.Source.Activity.Id);
            ids.Should().Contain(c.Target.Activity.Id);
        }
    }

    // ================================================================
    // Outputs — success=false on the failure path; success=true on success
    // ================================================================

    [Test]
    public void FailurePath_SetsSuccessFalse_And_ErrorCode()
    {
        var failureSeq = _flowchart.Activities
            .OfType<Sequence>()
            .First(s => s.Id == "FailureOutputs");

        var ids = failureSeq.Activities
            .OfType<SetOutput>()
            .Select(o => o.Id ?? "")
            .ToList();

        ids.Should().Contain("OutFailSuccess");   // success = false
        ids.Should().Contain("OutFailErrorCode"); // errorCode
        ids.Should().Contain("OutFailReason");    // exitReason
    }

    [Test]
    public void SuccessPath_SetsSuccessTrue()
    {
        var successSeq = _flowchart.Activities
            .OfType<Sequence>()
            .First(s => s.Id == "SuccessOutputs");

        var ids = successSeq.Activities
            .OfType<SetOutput>()
            .Select(o => o.Id ?? "")
            .ToList();

        ids.Should().Contain("OutSuccess");   // success = true
        ids.Should().Contain("OutIssueNumber");
    }

    // ================================================================
    // exitReason carries the REAL error reason (not a hard-coded constant)
    // ================================================================

    [Test]
    public void FailurePath_ExitReason_CarriesRealErrorReason_NotConstant()
    {
        // The activity exposes a rich human Error reason; the failure exitReason
        // output must surface THAT (so observability/callers see why it failed),
        // not a hard-coded "issue-update-failed" duplicate of errorCode.
        var outReason = FailureOutput("OutFailReason");

        const string realReason = "issue-comment 403 Forbidden: token lacks issues:write";
        EvaluateOutputValue(outReason, realReason)
            .Should().Be(realReason,
                "exitReason must bind to the workflow error variable, surfacing the real reason");
    }

    [Test]
    public void FailurePath_ExitReason_FallsBackToConstant_WhenNoReason()
    {
        // When no error reason was captured (null/empty), exitReason must still
        // produce the stable fallback rather than null.
        var outReason = FailureOutput("OutFailReason");

        EvaluateOutputValue(outReason, null)
            .Should().Be("issue-update-failed",
                "exitReason must fall back to the stable constant when no reason is present");
    }

    // ================================================================
    // Helpers
    // ================================================================

    private bool HasEdge(string sourceId, string? port, string targetId)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port == null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);

    private SetOutput FailureOutput(string id)
        => _flowchart.Activities
            .OfType<Sequence>()
            .First(s => s.Id == "FailureOutputs")
            .Activities
            .OfType<SetOutput>()
            .First(o => o.Id == id);

    /// <summary>
    /// Evaluate a <see cref="SetOutput"/>'s <c>OutputValue</c> delegate against a
    /// minimal expression context in which the workflow error variable (the only
    /// <see cref="MemoryBlockReference"/> the delegate captures) is declared and
    /// pre-set to <paramref name="errorReason"/>. Returns the materialised output
    /// so a test can assert the value the delegate actually computes — i.e. whether
    /// <c>exitReason</c> reads the variable or just returns a constant.
    /// </summary>
    private static object? EvaluateOutputValue(SetOutput setOutput, string? errorReason)
    {
        var input = typeof(SetOutput).GetProperty("OutputValue")!.GetValue(setOutput);
        var expression = input!.GetType()
            .GetProperty("Expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(input) as Expression;

        if (expression?.Value is not Delegate del)
            throw new InvalidOperationException("OutputValue is not a delegate-backed input");

        var memory = new MemoryRegister(new Dictionary<string, MemoryBlock>());
        var ctx = new ExpressionExecutionContext(
            NullServiceProvider.Instance, memory, null, null, null, default);

        // Declare every variable the delegate closes over, then set the captured
        // error variable to the chosen reason so .Get(ctx) returns it.
        var counter = 0;
        foreach (var reference in CapturedReferences(del))
        {
            EnsureUniqueId(reference, ref counter);
            try { memory.Declare(reference); } catch { /* default resolves below */ }
            if (reference is Variable<string> sv)
                sv.Set(ctx, errorReason);
        }

        var raw = del.DynamicInvoke(ctx);
        return Unwrap(raw);
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
            try { idProp.SetValue(reference, $"__uis_{counter++}"); }
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
