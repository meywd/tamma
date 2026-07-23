using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Core.Documents;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-6 — structural topology pins for <see cref="DocumentLifecycleWorkflow"/>
/// (Test Plan step 8). Covers AC1 (definition id / inputs), AC2 (stage graph),
/// AC5 (emit sites + constants), AC7 (structural half of the mediation invariant).
/// </summary>
[TestFixture]
public class DocumentLifecycleWorkflowStructureTests
{
    private static Flowchart Flowchart()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DocumentLifecycleWorkflow());
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

    // ── AC1 — definition id ────────────────────────────────────────────

    [Test]
    public void Workflow_BuildsWithoutError()
    {
        var act = () => WorkflowTestHelper.BuildWorkflow(new DocumentLifecycleWorkflow());
        act.Should().NotThrow();
    }

    [Test]
    public void Workflow_HasStableDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DocumentLifecycleWorkflow());
        builder.Object.DefinitionId.Should().Be("document-lifecycle");
    }

    [Test]
    public void Workflow_ThreadsTenantId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DocumentLifecycleWorkflow());
        builder.Object.Variables.Any(v => v.Name == "TenantId")
            .Should().BeTrue("the lifecycle must thread TenantId onto every event + the gate bookmark");
    }

    [Test]
    public void Workflow_HasInitNode_ThatValidatesProducerSpec()
    {
        // The Init node is where DocumentLifecycleHelper.ValidateProducerSpec runs
        // fail-loud (D2). The allowlist cross-check (TaxonomyDriftBuildTests /
        // ContractBindingTests) depends on this node existing; ValidateProducerSpec's
        // behaviour is proved by DocumentLifecycleHelperTests.
        Flowchart().Activities.Should().Contain(a => a.Id == "Init");
    }

    // ── AC2 / AC7 — mediation invariant (exactly three llm-call dispatches) ──

    [Test]
    public void Workflow_HasExactlyThreeLlmCallDispatches_ProduceRepairRevise()
    {
        var llmDispatches = AllActivities().OfType<DispatchWorkflow>()
            .Where(d => ReadLiteralDefId(d) == "llm-call")
            .Select(d => d.Id)
            .OrderBy(x => x)
            .ToList();

        llmDispatches.Should().BeEquivalentTo(new[] { "DispatchProduce", "DispatchRepair", "DispatchRevise" },
            "the ONLY LLM interactions are the three mediated llm-call dispatches (produce/repair/revise)");
    }

    [Test]
    public void Workflow_ReviewDispatch_IsVariableBacked_NotLlmCall()
    {
        var review = AllActivities().OfType<DispatchWorkflow>().SingleOrDefault(d => d.Id == "DispatchReview");
        review.Should().NotBeNull("the REVIEW step dispatches the 39-7 producer");
        ReadLiteralDefId(review!).Should().BeNull(
            "the review definition id is variable-backed (default 'document-review', overridable by input), " +
            "not a compile-time literal");
    }

    [Test]
    public void Workflow_HasNoInlineLlmActivity()
    {
        AllActivities().Any(a => a.GetType().Name == "CallLlmInlineActivity")
            .Should().BeFalse("the engine holds no LLM credential — every LLM call is the mediated llm-call dispatch");
    }

    // ── AC2 — ACCEPT stage: publish → gate, no branch between ───────────

    [Test]
    public void Workflow_HasExactlyOneGate_AndOnePublish()
    {
        AllActivities().OfType<WaitForDocumentDecisionActivity>().Should().ContainSingle(
            "the ACCEPT stage registers exactly ONE decision gate (39-8)");
        AllActivities().OfType<PublishAcceptanceRequestActivity>().Should().ContainSingle(
            "the ACCEPT stage publishes exactly ONE acceptance request");
    }

    [Test]
    public void Workflow_PublishFlowsDirectlyIntoGate_NoDecisionBetween()
    {
        var fc = Flowchart();
        var direct = fc.Connections.Any(c =>
            c.Source.Activity.Id == "PublishAcceptanceRequest" &&
            c.Target.Activity.Id == "WaitForDocumentDecision");
        direct.Should().BeTrue(
            "publish must flow DIRECTLY into the gate — the 'never an if-else that skips the decision' pin");

        // No FlowDecision may sit on the publish→gate path.
        var gateInbound = fc.Connections.Where(c => c.Target.Activity.Id == "WaitForDocumentDecision").ToList();
        gateInbound.Should().OnlyContain(c => c.Source.Activity.Id == "PublishAcceptanceRequest",
            "nothing but the publish step may feed the gate");
        fc.Connections.Where(c => c.Source.Activity.Id == "PublishAcceptanceRequest")
            .Should().OnlyContain(c => c.Target.Activity.Id == "WaitForDocumentDecision",
                "the publish step's only successor is the gate (no FlowDecision between)");
    }

    [Test]
    public void Workflow_GateRoutesThroughGuardrails_OnEveryDecisionEdge()
    {
        var fc = Flowchart();
        foreach (var edge in new[] { "Accept", "RequestRevision", "Reject", "Escalate" })
        {
            fc.Connections.Any(c =>
                c.Source.Activity.Id == "WaitForDocumentDecision" &&
                c.Source.Port == edge &&
                c.Target.Activity.Id == "ApplyGuardrails")
                .Should().BeTrue($"the gate's '{edge}' edge must route through ApplyGuardrails (39-5 clamp)");
        }
    }

    // ── AC5 — emit-site-per-transition ─────────────────────────────────

    [Test]
    public void Workflow_EmitsADocumentEventPerTransition()
    {
        var emitIds = AllActivities().OfType<EmitDocumentEventActivity>().Select(a => a.Id).ToHashSet();
        emitIds.Should().Contain(new[]
        {
            "EmitProduced", "EmitValidated", "EmitReviewRequested", "EmitReviewed",
            "EmitRevisionStarted", "EmitAccepted", "EmitRejected", "EmitEscalated",
        });
    }

    // ── Story 39-12 (D4) — terminal exposes the accepted payload body ──

    [Test]
    public void Workflow_TerminalExposes_DocumentJsonOutput()
    {
        // 39-12 D4 filed-back hook: the terminal SetOutputs must expose the accepted
        // revision's payload body under 'documentJson' so a lifecycle binding
        // (IssueDecompositionWorkflow) can project its own domain output from it — the
        // lineage on 'lifecycleResult' only carries id+state, not the body.
        var outputNames = AllActivities()
            .OfType<Elsa.Workflows.Management.Activities.SetOutput.SetOutput>()
            .Select(o => o.OutputName.Expression?.Value as string)
            .Where(n => n is not null)
            .ToList();

        outputNames.Should().Contain("documentJson",
            "the lifecycle must expose the accepted revision payload as the 'documentJson' output (39-12 D4)");
        outputNames.Should().Contain(new[] { "status", "outcome", "documentId", "lifecycleResult", "sessionId" },
            "the pre-39-12 output contract is preserved (additive-only)");
    }

    // ── Story 39-13 (D5/D6) — pre-ACCEPT delivery hook + decision notes ──

    [Test]
    public void Workflow_HasOptionalDeliveryDispatch_BeforePublish()
    {
        // D5 — the optional pre-ACCEPT delivery site (variable-backed definition id, gated by
        // HasDeliveryGate). It is NOT an llm-call/document-lifecycle literal, so it does not
        // perturb the mediation pins above.
        var delivery = AllActivities().OfType<DispatchWorkflow>().SingleOrDefault(d => d.Id == "DispatchDelivery");
        delivery.Should().NotBeNull("the lifecycle carries the optional pre-ACCEPT delivery dispatch (39-13 D5)");
        ReadLiteralDefId(delivery!).Should().BeNull("the delivery definition id is variable-backed (deliveryWorkflowDefinitionId input)");

        AllActivities().OfType<FlowDecision>().Select(d => d.Id).Should().Contain("HasDeliveryGate",
            "delivery is gated so a no-delivery lifecycle skips it entirely");

        var fc = Flowchart();
        fc.Connections.Any(c => c.Source.Activity.Id == "DispatchDelivery" && c.Target.Activity.Id == "PublishAcceptanceRequest")
            .Should().BeTrue("delivery flows into the publish step, before the gate");
    }

    [Test]
    public void Workflow_TerminalExposes_DecisionNotesOutput()
    {
        // D6d — the decider's notes are surfaced so a binding can mirror the legacy
        // DESIGN.PROPOSAL.APPROVED/REJECTED Detail.
        AllActivities()
            .OfType<Elsa.Workflows.Management.Activities.SetOutput.SetOutput>()
            .Select(o => o.OutputName.Expression?.Value as string)
            .Should().Contain("decisionNotes",
                "the lifecycle exposes the decider's notes as the additive 'decisionNotes' output (39-13 D6d)");
    }

    // ── AC5 — constant pins ────────────────────────────────────────────

    [Test]
    public void DocumentEvents_HasExactlyTheExpectedConstants()
    {
        var constants = typeof(DocumentEvents)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(x => x)
            .ToList();

        constants.Should().BeEquivalentTo(new[]
        {
            "DOCUMENT.ACCEPTED",
            "DOCUMENT.ESCALATED",
            "DOCUMENT.PRODUCED.FAILED",
            "DOCUMENT.PRODUCED.SUCCESS",
            "DOCUMENT.REJECTED",
            "DOCUMENT.REVIEWED",
            "DOCUMENT.REVIEW_REQUESTED",
            "DOCUMENT.REVISION_STARTED",
            "DOCUMENT.VALIDATED.FAILED",
            "DOCUMENT.VALIDATED.SUCCESS",
            // Story 39-7 — panel-producer markers appended to the same catalogue.
            "DOCUMENT.REVIEW_PANEL_STARTED",
            "DOCUMENT.REVIEW_PANEL_COMPLETED",
            "DOCUMENT.REVIEW_PANEL_UNDECIDABLE",
            // Story 39-10 — crash re-entry marker (D9 conscious pin bump).
            "DOCUMENT.REENTERED",
        }, "the DOCUMENT.* catalogue is the ten Story 39-6 event types, the three Story 39-7 panel markers, " +
           "and the Story 39-10 DOCUMENT.REENTERED re-entry marker");
    }

    [Test]
    public void DocumentLifecycleOutcome_HasExactlyFourMembers()
    {
        Enum.GetValues<DocumentLifecycleOutcome>().Should().BeEquivalentTo(new[]
        {
            DocumentLifecycleOutcome.ReviewUndecidable,
            DocumentLifecycleOutcome.AmbiguityAboveThreshold,
            DocumentLifecycleOutcome.RoundsExhausted,
            DocumentLifecycleOutcome.ValidationExhausted,
        }, "the closed outcome set is exactly four members (drift pin from the consumer side, AC3)");
    }

    // ── helpers ────────────────────────────────────────────────────────

    /// <summary>The literal WorkflowDefinitionId string, or null when it is a delegate (variable-backed).</summary>
    private static string? ReadLiteralDefId(DispatchWorkflow dispatch)
    {
        var value = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId")?.GetValue(dispatch);
        var expr = value?.GetType().GetProperty("Expression")?.GetValue(value) as Expression;
        return expr?.Value as string;
    }
}
