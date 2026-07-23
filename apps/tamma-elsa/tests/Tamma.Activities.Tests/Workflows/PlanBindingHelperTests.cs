using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents.Policy;
using Tamma.ElsaServer.Workflows;
using Tamma.ElsaServer.Workflows.Helpers;
using Exit = Tamma.ElsaServer.Workflows.Helpers.LifecycleBindingHelper.LifecycleExit;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-14 — property-level coverage for <see cref="PlanBindingHelper"/>, the pure
/// fail-closed core of the planning-family bindings. Covers AC1 (the consumes-carrier half +
/// exits), AC6 (round-projection half), and the technical-note render-drop regression at this
/// story's dispatch seam.
/// </summary>
[TestFixture]
public class PlanBindingHelperTests
{
    // ── DeriveIssueId ───────────────────────────────────────────────────

    [Test]
    public void DeriveIssueId_IsRepoHashNumber()
        => PlanBindingHelper.DeriveIssueId("meywd/tamma", 42).Should().Be("meywd/tamma#42");

    // ── MergeDecompositionIntoCarrier — the render lesson ───────────────

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void MergeDecompositionIntoCarrier_EmptyDecomposition_ReturnsPoSummaryByteIdentical(string? decomposition)
        => PlanBindingHelper.MergeDecompositionIntoCarrier("PO summary text", decomposition)
            .Should().Be("PO summary text",
                "an empty decomposition (legacy runs) leaves the declared carrier byte-identical — no render change");

    [Test]
    public void MergeDecompositionIntoCarrier_WithDecomposition_PlacesBlockAheadOfPoSummary()
    {
        const string decomposition = "{\"summary\":\"split it\",\"subtasks\":[]}";
        var merged = PlanBindingHelper.MergeDecompositionIntoCarrier("PO summary text", decomposition);

        merged.Should().StartWith("## Accepted decomposition",
            "the consumed decomposition is folded ahead of poSummary (D4)");
        merged.Should().Contain(decomposition);
        merged.Should().EndWith("PO summary text");
    }

    [Test]
    public void MergeDecompositionIntoCarrier_EmptyPoSummary_ReturnsBlockOnly()
    {
        const string decomposition = "{\"summary\":\"x\"}";
        var merged = PlanBindingHelper.MergeDecompositionIntoCarrier("", decomposition);
        merged.Should().Be("## Accepted decomposition\n" + decomposition);
    }

    // ── The render-drop regression proof at this story's dispatch seam ──

    [Test]
    public void DispatchLifecycle_MergedDecomposition_ArrivesUnderDeclaredContextFindings_NoDeadKeys()
    {
        // Seed POSummary + DecompositionJson (as the FetchDecomposition activity would leave them),
        // materialise the DispatchLifecycle Input, and inspect the SERIALIZED producerVariablesJson.
        var producerVars = MaterializeProducerVariables(new Dictionary<string, string>
        {
            ["POSummary"] = "PO summary text",
            ["DecompositionJson"] = "{\"summary\":\"split it\",\"subtasks\":[]}",
        });

        producerVars.Should().ContainKey("contextFindings");
        producerVars["contextFindings"].Should().Contain("## Accepted decomposition",
            "the consumed decomposition reaches the DECLARED contextFindings carrier at the dispatch seam");
        producerVars["contextFindings"].Should().Contain("PO summary text");

        // The technical-note regression: NO undeclared key the shared Plan-family template would drop.
        producerVars.Should().NotContainKey("decompositionJson",
            "a decompositionJson dispatch key would be silently dropped by the shared template (the render lesson)");
        producerVars.Should().NotContainKey("validationErrors",
            "the retired render-dropped validationErrors key must not reappear");
    }

    [Test]
    public void DispatchLifecycle_FirstAttempt_ContextFindingsIsPoSummary_WhenNoDecomposition()
    {
        var producerVars = MaterializeProducerVariables(new Dictionary<string, string>
        {
            ["POSummary"] = "PO summary text",
        });
        producerVars["contextFindings"].Should().Be("PO summary text",
            "with no accepted decomposition the carrier is byte-identical to poSummary");
    }

    // ── DefaultPlanRulesJson — D3 carry-over (rounds 3 / repair 2 / 7-role panel) ──

    [Test]
    public void DefaultPlanRulesJson_PinsRounds3_Repair2_SevenRolePanel()
    {
        // Drift-style pin: today's effective budgets (PlanReview maxRounds=3, PlanGeneration
        // maxRetries=2) carried over the 7-role plan/review panel, so the mechanism swap does not
        // silently change quality/cost (39-5 generic defaults are rounds 2 / repair 2 / single reviewer).
        var rules = AcceptanceRulesJson.Deserialize(PlanBindingHelper.DefaultPlanRulesJson());

        rules.MaxRevisionRounds.Should().Be(3);
        rules.MaxValidationRepairAttempts.Should().Be(2);
        rules.ReviewerSelection.Mode.Should().Be(ReviewerMode.Panel);
        rules.ReviewerSelection.PanelRoles.Should().HaveCount(7);
        rules.ReviewerSelection.DecisionRule.Should().Be(ReviewDecisionRule.Majority);
    }

    // ── MapDecisionForLegacyOutput + BuildFailureDetail matrix ──────────

    [Test]
    public void MapDecisionForLegacyOutput_Accepted_IsApproved()
    {
        PlanBindingHelper.MapDecisionForLegacyOutput(Accepted()).Should().Be("approved");
        PlanBindingHelper.MapDecisionForLegacyOutput(true).Should().Be("approved");
    }

    [TestCase("rejected", "")]
    [TestCase("escalated", "rounds-exhausted")]
    [TestCase("escalated", "validation-exhausted")]
    [TestCase("escalated", "review-undecidable")]
    public void MapDecisionForLegacyOutput_NonAccept_IsNeedsHuman(string status, string outcome)
    {
        var exit = new Exit(status, string.IsNullOrEmpty(outcome) ? null : outcome, null, "{}", "");
        PlanBindingHelper.MapDecisionForLegacyOutput(exit).Should().Be("needsHuman");
        PlanBindingHelper.MapDecisionForLegacyOutput(false).Should().Be("needsHuman");
    }

    [TestCase("rounds-exhausted")]
    [TestCase("validation-exhausted")]
    [TestCase("review-undecidable")]
    public void BuildFailureDetail_Escalated_NamesTheTypedOutcome(string outcome)
    {
        var detail = PlanBindingHelper.BuildFailureDetail(new Exit("escalated", outcome, null, "{}", ""));
        detail.Should().Contain("escalated").And.Contain(outcome);
    }

    [Test]
    public void BuildFailureDetail_Rejected_NamesTheStatus()
        => PlanBindingHelper.BuildFailureDetail(new Exit("rejected", null, null, "{}", ""))
            .Should().Contain("rejected");

    // ── BuildDiscussionLogProjection — AC6 round-projection half ────────

    [Test]
    public void BuildDiscussionLogProjection_FromRounds_ReconstructsOneEntryPerRound()
    {
        const string lineage = "{\"documentId\":\"0192a8b0-1111-7abc-8def-000000000001\",\"revision\":2,\"rounds\":[1,2]}";
        var log = PlanBindingHelper.BuildDiscussionLogProjection(lineage);

        using var doc = JsonDocument.Parse(log);
        doc.RootElement.GetArrayLength().Should().Be(2, "the round count is reconstructable from the lineage projection");
        doc.RootElement[0].GetProperty("round").GetInt32().Should().Be(1);
        doc.RootElement[1].GetProperty("round").GetInt32().Should().Be(2);
        doc.RootElement[0].GetProperty("documentId").GetString().Should().Be("0192a8b0-1111-7abc-8def-000000000001");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json")]
    [TestCase("{}")]
    public void BuildDiscussionLogProjection_EmptyOrUnreadable_ReturnsEmptyArray(string lineage)
        => PlanBindingHelper.BuildDiscussionLogProjection(lineage).Should().Be("[]");

    // ── materialisation plumbing (the ValidationRetryFeedbackTests idiom) ──

    /// <summary>
    /// Build <see cref="PlanGenerationWorkflow"/>, seed the named string variables, materialise the
    /// DispatchLifecycle Input delegate, and return the deserialised <c>producerVariablesJson</c>.
    /// </summary>
    private static Dictionary<string, string> MaterializeProducerVariables(IReadOnlyDictionary<string, string> seedByName)
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new PlanGenerationWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);
        var dispatch = flowchart.Activities.OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle");

        var inputValue = typeof(DispatchWorkflow).GetProperty("Input")!.GetValue(dispatch);
        var expression = inputValue!.GetType()
            .GetProperty("Expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(inputValue) as Expression;
        var del = (System.Delegate)expression!.Value!;

        var memory = new MemoryRegister(new Dictionary<string, MemoryBlock>());
        var ctx = new ExpressionExecutionContext(NullServiceProvider.Instance, memory, null, null, null, default);

        var counter = 0;
        foreach (var reference in CapturedReferences(del))
        {
            EnsureUniqueId(reference, ref counter);
            try { memory.Declare(reference); } catch { /* default resolves below */ }

            if (reference is Variable<string> sv && sv.Name is { Length: > 0 } name &&
                seedByName.TryGetValue(name, out var seeded))
                sv.Set(ctx, seeded);
        }

        var raw = Unwrap(del.DynamicInvoke(ctx));
        var input = (IDictionary<string, object>)raw!;
        var producerVarsJson = (string)input["producerVariablesJson"];
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(producerVarsJson)!
            .ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind == JsonValueKind.String
                ? kv.Value.GetString() ?? ""
                : kv.Value.GetRawText());
    }

    private static Exit Accepted() => new("accepted", null, "0192a8b0-1111-7abc-8def-000000000001", "{\"tasks\":[]}", "");

    private static IEnumerable<MemoryBlockReference> CapturedReferences(System.Delegate del)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        if (del.Target != null) stack.Push(del.Target);

        while (stack.Count > 0)
        {
            var obj = stack.Pop();
            if (obj == null || !seen.Add(obj)) continue;
            if (obj is string || obj.GetType().IsPrimitive) continue;

            if (obj is MemoryBlockReference reference) { yield return reference; continue; }

            foreach (var field in obj.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
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
        var idProp = typeof(MemoryBlockReference).GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (idProp?.CanWrite == true)
        {
            try { idProp.SetValue(reference, $"__pbh_{counter++}"); }
            catch { /* leave as-is */ }
        }
    }

    private static object? Unwrap(object? raw)
    {
        if (raw == null) return null;
        var type = raw.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.ValueTask<>))
        {
            var asTask = type.GetMethod("AsTask")!.Invoke(raw, null);
            return asTask!.GetType().GetProperty("Result")!.GetValue(asTask);
        }
        return raw;
    }

    private sealed class NullServiceProvider : System.IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        public object? GetService(System.Type serviceType) => null;
    }
}
