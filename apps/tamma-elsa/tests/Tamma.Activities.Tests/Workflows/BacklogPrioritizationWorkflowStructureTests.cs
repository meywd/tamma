using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Api.Auth;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 41-3 — structural pins for <see cref="BacklogPrioritizationWorkflow"/>, the GREENFIELD
/// thin binding over <c>document-lifecycle</c> (consumes <c>triage-decision</c> + <c>findings</c>,
/// produces <c>backlog-ordering</c>). Clause-for-clause the epic-41 rule-1 (a)–(f) set that
/// <see cref="TaskCreationWorkflowStructureTests"/> is the reference shape for, plus the
/// story-AC2 EVIDENCE-ANCHOR gate: the three per-item reads are resolved out of the BUILT graph
/// and asserted by (anchor, documentType), so a single-anchor implementation fails here rather
/// than at runtime. Covers AC1, AC2 (anchor half), AC3 (anchor half) and AC4.
/// </summary>
[TestFixture]
public class BacklogPrioritizationWorkflowStructureTests
{
    private const string SampleItem = "meywd/tamma#9";

    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new BacklogPrioritizationWorkflow()));

    private static List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    // ── rule-1 clause (a)–(f) ──────────────────────────────────────────

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new BacklogPrioritizationWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasTheDeclaredDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new BacklogPrioritizationWorkflow()).Object.DefinitionId
            .Should().Be("backlog-prioritization",
                "D1 — the id is deliberately NOT 'backlog-ordering' so it never reads as the document-type wire");

    [Test]
    public void Workflow_ThreadsTenantId()
        => WorkflowTestHelper.BuildWorkflow(new BacklogPrioritizationWorkflow()).Object.Variables
            .Any(v => v.Name == "TenantId").Should().BeTrue();

    [Test]
    public void Workflow_HasNoRetryPlumbingVariables()
    {
        // Clause (d) — a thin binding owns no validate/retry plumbing; validation flows through
        // the lifecycle's repair/review/escalation rings.
        var names = WorkflowTestHelper.BuildWorkflow(new BacklogPrioritizationWorkflow())
            .Object.Variables.Select(v => v.Name ?? "").ToList();
        names.Should().NotContain("ValidationErrors");
        names.Should().NotContain("RetryCount");
        names.Should().NotContain("MaxRetries");
        names.Should().NotContain(n => n.EndsWith("Valid", StringComparison.Ordinal));
    }

    [Test]
    public void Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall()
    {
        // Clauses (a) + (b).
        AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "DispatchLifecycle" });
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty(
                "no bespoke llm-call — the producer dispatch lives inside document-lifecycle");
        StructureWalk.LiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle"))
            .Should().Be("document-lifecycle");
    }

    [Test]
    public void Workflow_HasNoFinishActivity()
        => AllActivities().OfType<Finish>().Should().BeEmpty(
            "clause (c) — every non-accept exit is a typed lifecycle outcome, never a bespoke terminal");

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalPair_BacklogOrderingType_AndDeclaredFeedbackCarrier()
    {
        // Clause (e) — the canonical produce pair + documentType + a DECLARED feedback carrier.
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "BacklogPrioritizationWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.ProductOwner.ToWire() && p.Action == AgentAction.PrioritizeBacklog.ToWire(),
            "the binding hands the canonical (product_owner, prioritize-backlog) producer pair");

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("BacklogPrioritizationWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("backlog-ordering");
        (input!["feedbackVariableName"] as string).Should().Be("evidence",
            "D4 — repair/revise notes must land in a carrier the cell's front matter DECLARES and " +
            "its BODY places; an undeclared key is dropped at render and an unplaced one is a no-op");
        input!.Should().ContainKey("sessionId");
        input!.Should().ContainKey("issueId");
        input!.Should().ContainKey("correlationId");
    }

    [Test]
    public void DispatchLifecycle_AnchorsOnTheSetScopedAnchor_NotOnAnIssue()
    {
        // D2 / AC3. A BacklogOrdering is not issue-scoped, but DocumentInstance.IssueId is the
        // store's only read key — so the lifecycle's issueId IS BuildAnchor(repository,
        // backlogScope). This pins that the binding hands the SAME string 41-6 and 41-4 will
        // recompute; an issueId built any other way makes their upstream read miss.
        var all = AllActivities();

        // (i) the anchor is computed ONCE, into the BacklogAnchor variable, by the entry node.
        var readInputs = all.OfType<SetVariable>().Single(a => a.Id == "ReadInputs");
        readInputs.Variable!.Name.Should().Be("BacklogAnchor",
            "ReadInputs' whole job is to fold (repository, backlogScope) into the single anchor " +
            "every downstream node reads — a second derivation is a second contract");

        // (ii) the lifecycle's issueId AND correlationId are that one variable's value.
        var dispatch = all.OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle");
        var expected = BacklogBindingHelper.BuildAnchor("MeyWd/Tamma", "Q3 Roadmap");
        expected.Should().Be("backlog:meywd-tamma:q3-roadmap");

        var input = MaterializeDelegate<IDictionary<string, object>>(
            ReadExpression(dispatch, nameof(DispatchWorkflow.Input)),
            ("BacklogAnchor", expected));

        input.Should().NotBeNull();
        (input!["issueId"] as string).Should().Be(expected,
            "the store's ONLY read key is issueId, so the ordering is written under the anchor");
        (input!["correlationId"] as string).Should().Be(expected);
    }

    [Test]
    public void Workflow_DeclaresLatestStateReEntry()
    {
        // AC4, as corrected: a thin binding owns no bookmark, so Both would fail clause (b) of
        // the 39-10 gate. Every landed producer declares LatestStateReEntry.
        var decl = typeof(BacklogPrioritizationWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
    }

    [Test]
    public void Workflow_HasNoBookmarkSuspendActivity()
        => AllActivities().Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Should().BeEmpty("the accept-gate suspend is inside the dispatched document-lifecycle child");

    [Test]
    public void Workflow_CarriesTheReEntryNode_KeyedOnTheAnchor()
    {
        var reEntry = AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle(
            "clause (c) of ResumableStandardStructuralTests — the LatestStateReEntry declaration is " +
            "honoured by a real node, not a hand-rolled guard").Subject;

        MaterializeDelegate<string>(
            ReadExpression(reEntry, nameof(ComputeReEntryPositionActivity.IssueId)),
            ("BacklogAnchor", "backlog:meywd-tamma:q3"))
            .Should().Be("backlog:meywd-tamma:q3",
                "D6 — re-entry is keyed on the D2 anchor; keyed on anything else, a resumed run " +
                "would look for the ordering somewhere it was never written");

        MaterializeDelegate<string>(
            ReadExpression(reEntry, nameof(ComputeReEntryPositionActivity.DocumentType)))
            .Should().Be("backlog-ordering");
    }

    [Test]
    public void Workflow_RoutesOnlyTypedLifecycleValues()
    {
        // Every FlowDecision routes a TYPED value: the re-entry stage, whether an ordering was
        // drafted, and whether it was accepted. No parse-derived gate.
        AllActivities().OfType<FlowDecision>().Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal)
            .Should().BeEquivalentTo(new[] { "FreshRun", "LifecycleAccepted", "OrderingDrafted" });
    }

    [Test]
    public void Workflow_EmitsTheFourMemberBacklogGroomingFamily()
    {
        // Rule 2 — a domain family alongside DOCUMENT.*, with a LOUD terminal.
        var emitted = AllActivities().OfType<EmitDomainLifecycleEventActivity>().Select(a => a.Id)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        emitted.Should().BeEquivalentTo(new[]
        {
            "EmitGroomingAccepted",
            "EmitGroomingFailed",
            "EmitGroomingOrdered",
            "EmitGroomingStarted",
        });

        EmitDomainLifecycleEventActivity.StatusForEvent(BacklogEvents.Failed).Should().Be("error",
            "a non-accepted exit must be a LOUD error row, never a false success");
        EmitDomainLifecycleEventActivity.StatusForEvent(BacklogEvents.Started).Should().Be("started");
        EmitDomainLifecycleEventActivity.StatusForEvent(BacklogEvents.Ordered).Should().Be("success");
        EmitDomainLifecycleEventActivity.StatusForEvent(BacklogEvents.Accepted).Should().Be("success");
    }

    [Test]
    public void Workflow_ExposesTheAnchorAndTheOrdering()
        => AllActivities().OfType<SetOutput>().Select(a => a.Id).Should().Contain(
            new[] { "OutputBacklogAnchor", "OutputOrdering", "OutputSessionId", "OutputDocumentId" },
            "41-6 and 41-4 need the anchor the ordering was actually written under");

    [Test]
    public void Registry_DeclaresTheProducingEdge()
    {
        // Clause (f) — the WorkflowDocumentInterface row, non-provisional (a real binding).
        var edge = DocumentTypeRegistry.WorkflowInterfaces
            .Single(i => i.WorkflowDefinitionId == "backlog-prioritization");
        edge.Produces.Should().Be(DocumentTypeKey.BacklogOrdering);
        edge.Consumes.Should().BeEquivalentTo(new[] { DocumentTypeKey.TriageDecision, DocumentTypeKey.Findings });
        edge.Provisional.Should().BeFalse();
    }

    [Test]
    public void BacklogOrdering_AcceptancePostureIsChosen_NotTheCatchAll()
    {
        // D8 regression guard: 41-1b landed DocumentTypeKey.BacklogOrdering => s_productOwnerRules
        // (AcceptanceDefaults.cs:215). A silent fall-through to the single-architect `_ => Rules`
        // base row is the failure mode 41-1b D1 exists to prevent.
        var rules = Tamma.Core.Documents.Policy.AcceptanceDefaults.For(DocumentTypeKey.BacklogOrdering);
        rules.Should().NotBeSameAs(Tamma.Core.Documents.Policy.AcceptanceDefaults.Rules);
        rules.ReviewerSelection.Mode.Should().Be(Tamma.Core.Documents.Policy.ReviewerMode.SingleReviewer);
        rules.ReviewerSelection.ReviewerRole.Should().Be(AgentRole.ProductOwner.ToWire(),
            "ranking a backlog is a product-owner judgment; an architect reviewer is nonsense");
    }

    // ====================================================================
    // Story AC2 — the evidence region: ONE bounded loop, THREE anchors
    // ====================================================================

    [Test]
    public void EvidenceGathering_IsOneBoundedForEach_NotNUnrolledFetchNodes()
    {
        // D3. N compiled fetch nodes would be unmaintainable AND would distort the drift gate's
        // dispatch-pair accounting; the cap lives in the parse, not in the graph.
        var loops = AllActivities().OfType<ForEach<string>>().ToList();
        loops.Should().ContainSingle("the evidence region is exactly one loop");
        loops[0].Id.Should().Be("GatherEvidence");

        var inLoop = StructureWalk.All(loops[0]).OfType<FetchLatestAcceptedDocumentActivity>().ToList();
        // 39-25 — the ONE run-scoped ambiguity-assessment fetch (keyed on the backlog anchor,
        // not a per-item id) legitimately lives OUTSIDE the loop; every PER-ITEM read must
        // still be inside it. Filter on the DocumentTypeKey literal to preserve the pin's intent.
        var everywhere = AllActivities().OfType<FetchLatestAcceptedDocumentActivity>()
            .Where(f => MaterializeDelegate<string>(
                ReadExpression(f, nameof(FetchLatestAcceptedDocumentActivity.DocumentTypeKey)))
                != "ambiguity-assessment")
            .ToList();
        everywhere.Should().HaveCount(inLoop.Count,
            "every per-item store read lives INSIDE the bounded loop — an unrolled read outside " +
            "it is an N-node graph in disguise (the single run-scoped 39-25 ambiguity fetch is " +
            "the only permitted outside read)");
        AllActivities().OfType<FetchLatestAcceptedDocumentActivity>()
            .Should().HaveCount(inLoop.Count + 1,
                "exactly one fetch — the 39-25 run-scoped ambiguity-assessment read — sits outside the loop");
    }

    [Test]
    public void EvidenceGathering_ReadsBothFindingsAnchorsPerItem_NotJustTheBareId()
    {
        // Story AC2 / Amendment A1 — THE failable test for this story's central defect fix.
        //
        // `findings` has TWO landed producers under TWO anchors: ResearchWorkflow writes at the
        // BARE caller-supplied issueId (ResearchWorkflow.cs:91), TriageContextGatheringWorkflow at
        // CreationBindingHelper.ScopeIssueId(baseId, "triage-context")
        // (TriageContextGatheringWorkflow.cs:96). The 39-11 store has exactly ONE read key and no
        // producer filter, so a read at the bare id NEVER returns the triage-context findings and,
        // when a research findings exists for the same issue, returns THAT instead — a different
        // workflow's document under the same type key.
        //
        // The reads are resolved out of the BUILT graph with the loop's per-item variable bound to
        // a real id, so an implementation that reads one findings anchor and calls it "the
        // findings" fails HERE with the missing (anchor, type) pair named.
        var loop = AllActivities().OfType<ForEach<string>>().Single(f => f.Id == "GatherEvidence");
        var fetches = StructureWalk.All(loop).OfType<FetchLatestAcceptedDocumentActivity>().ToList();

        var resolved = fetches
            .Select(f => (
                Anchor: MaterializeDelegate<string>(
                    ReadExpression(f, nameof(FetchLatestAcceptedDocumentActivity.IssueId)),
                    ("CurrentItemIssueId", SampleItem)),
                Type: MaterializeDelegate<string>(
                    ReadExpression(f, nameof(FetchLatestAcceptedDocumentActivity.DocumentTypeKey)),
                    ("CurrentItemIssueId", SampleItem))))
            .ToList();

        var scoped = CreationBindingHelper.ScopeIssueId(SampleItem, "triage-context");
        scoped.Should().Be(SampleItem + "#triage-context");

        resolved.Should().BeEquivalentTo(new[]
        {
            (Anchor: (string?)SampleItem, Type: (string?)"triage-decision"),
            (Anchor: (string?)SampleItem, Type: (string?)"findings"),
            (Anchor: (string?)scoped,     Type: (string?)"findings"),
        }, "AC2 requires exactly three bounded per-item reads: the triage decision and the "
         + "research findings at the BARE item id, and the triage-context findings at the SCOPED "
         + "id. A single-anchor implementation cannot see TriageContextGatheringWorkflow's output "
         + "at all, and silently presents ResearchWorkflow's as 'the findings'.");
    }

    [Test]
    public void EvidenceReads_AreTenantScoped()
    {
        var loop = AllActivities().OfType<ForEach<string>>().Single(f => f.Id == "GatherEvidence");
        foreach (var fetch in StructureWalk.All(loop).OfType<FetchLatestAcceptedDocumentActivity>())
        {
            MaterializeDelegate<string>(
                ReadExpression(fetch, nameof(FetchLatestAcceptedDocumentActivity.TenantId)),
                ("TenantId", "0192a8b0-1111-7abc-8def-000000000001"))
                .Should().Be("0192a8b0-1111-7abc-8def-000000000001",
                    "a per-item read that is not tenant-scoped reads another tenant's backlog evidence");
        }
    }

    // ====================================================================
    // Story AC7(c) — the carrier must be DECLARED *and* PLACED
    // ====================================================================

    [Test]
    public void TheRewrittenCell_DeclaresEveryVariableItPlaces_AndPlacesEveryVariableItDeclares()
    {
        // PromptStoreService.Render (:559-589) substitutes on the BODY's {{…}} occurrences, not on
        // the front-matter list. So the two halves fail in opposite, equally silent ways:
        //   • declared but not placed  ⇒ the value is computed, handed over, and never rendered;
        //   • placed but not declared  ⇒ the front matter lies, and the (39-15 render-drop) rule
        //     that a producer variable must name a DECLARED carrier stops meaning anything.
        // Neither is caught by the token gate (which only greps the body) nor by the conformance
        // gate (which only validates the JSON fence), and PromptFileLoader does not cross-check
        // them either — so this is the pin.
        var cell = SystemPrompts.GetRoleAction("product_owner", "prioritize-backlog");
        cell.Should().NotBeNull();

        var declared = cell!.Variables.ToHashSet(StringComparer.Ordinal);
        declared.Should().BeEquivalentTo(new[] { "role", "itemsJson", "repoContext", "evidence" });

        var placed = System.Text.RegularExpressions.Regex
            .Matches(cell.Template, @"\{\{([^}]{1,64})\}\}")
            .Select(m => m.Groups[1].Value.Trim())
            .ToHashSet(StringComparer.Ordinal);

        placed.Should().BeEquivalentTo(declared,
            "every declared variable must appear as a {{placeholder}} in the BODY (a declared-but-" +
            "unplaced carrier is a silent no-op) and every placeholder must be declared (a placed-" +
            "but-undeclared one leaks a literal {{…}} into the rendered prompt)");
    }

    [Test]
    public void TheRewrittenCell_CarriesTheDispatchesFeedbackVariable()
    {
        // The two artifacts are edited in different files by different hands; this ties them.
        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput(
            "BacklogPrioritizationWorkflow", "DispatchLifecycle");
        var carrier = input!["feedbackVariableName"] as string;

        SystemPrompts.GetRoleAction("product_owner", "prioritize-backlog")!.Variables
            .Should().Contain(carrier!,
                "repair/revise notes land in feedbackVariableName — a carrier the cell does not " +
                "declare is dropped at render, so every repair turn re-prompts blind");
        SystemPrompts.GetRoleAction("product_owner", "prioritize-backlog")!.Template
            .Should().Contain("{{" + carrier + "}}");
    }

    [Test]
    public void TheRewrittenCell_HasHeadroomForNRationales_AndRecordsTheRewrite()
    {
        var cell = SystemPrompts.GetRoleAction("product_owner", "prioritize-backlog")!;
        cell.MaxTokens.Should().BeGreaterThan(2048,
            "AC7(d) — 2048 tokens cannot emit a rationale plus two estimates for every item of a " +
            "real backlog; the shipped single-item template did not need the room and this one does");
        cell.Version.Should().BeGreaterThanOrEqualTo(2, "AC7(e) — the rewrite is a version bump");
        cell.EnableTools.Should().BeFalse(
            "ranking is a judgement over supplied context, not a tool-using task");
    }

    // ====================================================================
    // Expression materialisation (the TaxonomyDriftBuildTests idiom, applied
    // to activity Inputs rather than to a dispatch Input)
    // ====================================================================

    private static Expression? ReadExpression(IActivity activity, string inputPropertyName)
    {
        var inputValue = activity.GetType().GetProperty(inputPropertyName)?.GetValue(activity);
        return inputValue?.GetType()
            .GetProperty("Expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(inputValue) as Expression;
    }

    /// <summary>
    /// Resolve an activity <c>Input&lt;T&gt;</c> expression. A LITERAL input carries its value
    /// directly; a delegate input is invoked against a memory register in which the named
    /// workflow variables the lambda captured are declared AND SET, so the resolved value is the
    /// one the running graph would compute for that state.
    /// </summary>
    private static T? MaterializeDelegate<T>(Expression? expression, params (string Name, object? Value)[] bindings)
        where T : class
    {
        expression.Should().NotBeNull("the activity input must carry an expression");

        if (expression!.Value is not Delegate del)
            return expression.Value as T;

        var memory = new MemoryRegister(new Dictionary<string, MemoryBlock>());
        var counter = 0;
        var captured = CollectCapturedVariables(del).ToList();
        foreach (var reference in captured)
        {
            EnsureUniqueId(reference, ref counter);
            try { memory.Declare(reference); }
            catch { /* an undeclarable reference just yields its default below */ }
        }

        var ctx = new ExpressionExecutionContext(NullServiceProvider.Instance, memory, null, null, null, default);

        foreach (var (name, value) in bindings)
        {
            var match = captured.OfType<Variable>().FirstOrDefault(v => v.Name == name);
            match.Should().NotBeNull(
                $"the expression must capture a workflow variable named '{name}' — otherwise this " +
                "test is asserting against a value the running graph never reads");
            match!.Set(ctx, value);
        }

        var raw = del.DynamicInvoke(ctx);
        return Unwrap(raw) as T;
    }

    private static object? Unwrap(object? raw)
    {
        if (raw is null) return null;
        var type = raw.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = type.GetMethod("AsTask")!.Invoke(raw, null);
            return asTask!.GetType().GetProperty("Result")!.GetValue(asTask);
        }
        return raw;
    }

    private static IEnumerable<MemoryBlockReference> CollectCapturedVariables(Delegate del)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        if (del.Target != null) stack.Push(del.Target);

        while (stack.Count > 0)
        {
            var obj = stack.Pop();
            if (obj is null || !seen.Add(obj)) continue;
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
                if (value is null) continue;

                if (value is MemoryBlockReference r) yield return r;
                else if (value is string || value.GetType().IsPrimitive) continue;
                else if (value.GetType().IsClass) stack.Push(value);
            }
        }
    }

    private static void EnsureUniqueId(MemoryBlockReference reference, ref int counter)
    {
        try
        {
            if (!string.IsNullOrEmpty(reference.Id)) return;
        }
        catch { return; }

        var idProp = typeof(MemoryBlockReference).GetProperty("Id",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (idProp?.CanWrite == true)
        {
            try { idProp.SetValue(reference, $"__backlog_{counter++}"); }
            catch { /* leave as-is */ }
        }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
