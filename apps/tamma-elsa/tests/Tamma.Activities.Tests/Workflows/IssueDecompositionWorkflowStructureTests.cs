using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Decomposition;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-12 — structural pins for <see cref="IssueDecompositionWorkflow"/>, rebuilt as
/// a THIN binding over <c>document-lifecycle</c>. Covers AC1 (thin binding: two dispatches,
/// zero llm-call, canonical producer pair), AC3 (no dead ends: zero Finish, every leaf is
/// the single ExposeOutput region), AC6 (resume declaration half), AC8 (rewrite).
///
/// <para>The old bespoke pipeline's structure tests (a <c>DecompositionError</c> Finish
/// terminal, a <c>DecompositionLlmOk</c> success gate, a direct <c>DecomposeIssueLlm</c>
/// dispatch) are DELETED — those shapes are exactly what this story removes.</para>
/// </summary>
[TestFixture]
public class IssueDecompositionWorkflowStructureTests
{
    private static Flowchart Flowchart()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new IssueDecompositionWorkflow());
        return WorkflowTestHelper.GetFlowchart(builder);
    }

    private static List<IActivity> AllActivities()
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<IActivity>();
        stack.Push(Flowchart());
        var result = new List<IActivity>();
        while (stack.Count > 0)
        {
            var a = stack.Pop();
            if (a is null || !seen.Add(a)) continue;
            result.Add(a);
            foreach (var child in Children(a)) stack.Push(child);
        }
        return result;
    }

    private static IEnumerable<IActivity> Children(IActivity activity)
    {
        var type = activity.GetType();
        var members = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        foreach (var member in members)
        {
            object? value;
            try
            {
                value = member switch
                {
                    PropertyInfo p when p.CanRead && p.GetIndexParameters().Length == 0 => p.GetValue(activity),
                    FieldInfo f => f.GetValue(activity),
                    _ => null,
                };
            }
            catch { continue; }

            if (value is IActivity child) yield return child;
            else if (value is System.Collections.IEnumerable en and not string)
                foreach (var item in en) if (item is IActivity nested) yield return nested;
        }
    }

    // ── AC1 — thin binding surface ──────────────────────────────────────

    [Test]
    public void Workflow_BuildsWithoutError()
    {
        var act = () => WorkflowTestHelper.BuildWorkflow(new IssueDecompositionWorkflow());
        act.Should().NotThrow("IssueDecompositionWorkflow.Build() must complete without exceptions");
    }

    [Test]
    public void Workflow_HasStableDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new IssueDecompositionWorkflow());
        builder.Object.DefinitionId.Should().Be("issue-decomposition",
            "the binding keeps the public definition id byte-stable so dispatch call sites are untouched (D1)");
    }

    [Test]
    public void Workflow_ThreadsTenantId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new IssueDecompositionWorkflow());
        builder.Object.Variables.Any(v => v.Name == "TenantId")
            .Should().BeTrue("the binding threads TenantId onto the lifecycle dispatch, the events, and the re-entry node");
    }

    [Test]
    public void Workflow_HasExactlyTwoDispatches_ContextGatheringAndLifecycle_NoLlmCall()
    {
        var dispatchIds = AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x).ToList();
        dispatchIds.Should().BeEquivalentTo(new[] { "DispatchLifecycle", "GatherContext" },
            "the binding gathers context and dispatches the generic lifecycle — nothing else");

        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => ReadLiteralDefId(d) == "llm-call")
            .Should().BeEmpty("the binding contributes NO direct llm-call — all producer dispatch lives inside document-lifecycle (D1/D2)");

        ReadLiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "GatherContext"))
            .Should().Be("context-gathering");
        ReadLiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle"))
            .Should().Be("document-lifecycle");
    }

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalProducerPair_AndDecompositionType()
    {
        // The producer (role, action) is discovered THROUGH the lifecycle binding by the
        // 39-12 D5 drift walk — canonical (senior_developer, decompose-issue).
        var pairs = TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches();
        pairs.Should().Contain(p =>
            p.Workflow == "IssueDecompositionWorkflow" &&
            p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.SeniorDeveloper.ToWire() &&
            p.Action == AgentAction.DecomposeIssue.ToWire(),
            "the lifecycle binding hands the canonical (senior_developer, decompose-issue) producer pair");

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("IssueDecompositionWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull("the DispatchLifecycle Input delegate must materialise");
        (input!["documentType"] as string).Should().Be("decomposition",
            "the binding produces the decomposition document type");
    }

    // ── AC3 — typed outcomes only, no dead ends ─────────────────────────

    [Test]
    public void Workflow_HasNoFinishActivity()
    {
        AllActivities().OfType<Finish>().Should().BeEmpty(
            "the bespoke DecompositionError Finish terminal is deleted — every non-success exit is a typed " +
            "lifecycle outcome, never a dead terminal (D2/AC3)");
    }

    [Test]
    public void Workflow_EveryGraphLeaf_IsTheSingleExposeOutputRegion()
    {
        var fc = Flowchart();
        var sources = fc.Connections.Select(c => c.Source.Activity.Id).ToHashSet();
        var leaves = fc.Activities.Where(a => !sources.Contains(a.Id)).Select(a => a.Id).ToList();

        leaves.Should().BeEquivalentTo(new[] { "ExposeOutput" },
            "AC3 no-dead-end pin: the ONLY flowchart leaf is the single output-exposure region — every path " +
            "ends at ExposeOutput (accepted, complete-re-entry, or typed-failure)");
    }

    [Test]
    public void Workflow_HasExactlyTheThreeExpectedFlowDecisions()
    {
        var decisionIds = AllActivities().OfType<FlowDecision>().Select(d => d.Id).OrderBy(x => x).ToList();
        decisionIds.Should().BeEquivalentTo(new[] { "FreshRun", "LifecycleAccepted", "WasCompleteReEntry" },
            "D2 pins the binding's routing to exactly three typed FlowDecisions — no parse/success gate can " +
            "reappear unnoticed");
    }

    // ── AC4 — legacy event compatibility ────────────────────────────────

    [Test]
    public void Workflow_HasAllFourDecompositionEmitNodes()
    {
        var emitIds = AllActivities().OfType<EmitDecompositionEventActivity>().Select(a => a.Id).ToHashSet();
        emitIds.Should().Contain(new[]
        {
            "EmitDecompositionStarted", "EmitContextGathered",
            "EmitDecompositionCompleted", "EmitDecompositionFailed",
        }, "the DECOMPOSITION.* events are mirrored at the equivalent lifecycle transitions (D3/AC4)");
    }

    // ── AC6 — resumable per the standard ────────────────────────────────

    [Test]
    public void Workflow_DeclaresLatestStateReEntry_AndCarriesTheReEntryNode()
    {
        var decl = typeof(IssueDecompositionWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull("the binding declares its resume behaviour (39-10 AC2) — no allowlist entry");
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry,
            "the binding never suspends on a bookmark itself (the accept-gate suspend is inside the child " +
            "lifecycle) — it re-enters from the latest accepted state (D7)");

        AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle(
            "a LatestStateReEntry workflow must wire the generic ComputeReEntryPositionActivity (39-10 clause c)");
    }

    [Test]
    public void Workflow_HasNoBookmarkSuspendActivity()
    {
        AllActivities().Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Should().BeEmpty(
                "the binding itself never suspends — the accept-gate bookmark lives inside the dispatched " +
                "document-lifecycle child instance (D7)");
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static string? ReadLiteralDefId(DispatchWorkflow dispatch)
    {
        var value = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId")?.GetValue(dispatch);
        var expr = value?.GetType().GetProperty("Expression")?.GetValue(value) as Expression;
        return expr?.Value as string;
    }
}
