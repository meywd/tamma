using System;
using System.Linq;
using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 41-2 — structural pins for <see cref="AcceptanceCriteriaAuthoringWorkflow"/>, the
/// GREENFIELD thin binding over <c>document-lifecycle</c> (consumes <c>clarification</c> +
/// <c>findings</c>, produces <c>acceptance-criteria</c>). Clause-for-clause the epic-41 rule-1
/// (a)–(f) set that <see cref="TaskCreationWorkflowStructureTests"/> is the reference shape for.
/// Covers AC1 (thin binding, no bespoke parse/terminal) and AC5 (resumable, no allowlist entry).
/// </summary>
[TestFixture]
public class AcceptanceCriteriaAuthoringWorkflowStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new AcceptanceCriteriaAuthoringWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new AcceptanceCriteriaAuthoringWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasTheDeclaredDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new AcceptanceCriteriaAuthoringWorkflow()).Object.DefinitionId
            .Should().Be("acceptance-criteria-authoring",
                "D1 — the id is deliberately NOT 'acceptance-criteria' so it never reads as the document-type wire");

    [Test]
    public void Workflow_ThreadsTenantId()
        => WorkflowTestHelper.BuildWorkflow(new AcceptanceCriteriaAuthoringWorkflow()).Object.Variables
            .Any(v => v.Name == "TenantId").Should().BeTrue();

    [Test]
    public void Workflow_HasNoRetryPlumbingVariables()
    {
        // Clause (d) — a thin binding owns no validate/retry plumbing; validation flows through
        // the lifecycle's repair/review/escalation rings (AC2's "never a dead end").
        var names = WorkflowTestHelper.BuildWorkflow(new AcceptanceCriteriaAuthoringWorkflow())
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
    public void DispatchLifecycle_MaterializesCanonicalPair_AcceptanceCriteriaType_AndDeclaredFeedbackCarrier()
    {
        // Clause (e) — the canonical produce pair + documentType + a DECLARED feedback carrier.
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "AcceptanceCriteriaAuthoringWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.ProductOwner.ToWire() && p.Action == AgentAction.DefineAcceptanceCriteria.ToWire(),
            "the binding hands the canonical (product_owner, define-acceptance-criteria) producer pair");

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("AcceptanceCriteriaAuthoringWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("acceptance-criteria");
        (input!["feedbackVariableName"] as string).Should().Be("contextFindings",
            "D3 — repair/revise notes must land in a carrier the cell's front matter DECLARES " +
            "(role, workItemJson, contextFindings, conventions); an undeclared key is dropped at render");
    }

    [Test]
    public void DispatchLifecycle_ThreadsASessionId_AndTheBindingExposesIt()
    {
        // 41-2 follow-up F7 (2026-07-29). This binding dispatched document-lifecycle
        // with NO "sessionId" key and exposed no sessionId output, unlike
        // AdrAuthoringWorkflow:245/:332, DesignProposalWorkflow:157/:247 and
        // DocumentLifecycleWorkflow:653. It never crashed — the lifecycle mints a
        // UUIDv7 when the input is Guid.Empty — which is exactly why nothing caught
        // it: the accept decision was correlatable only to an id no caller ever saw.
        // Pinned here because NO structure suite in the tree pinned this for ANY
        // binding (verified 2026-07-29: zero "sessionId" hits across the three
        // *WorkflowStructureTests files), so there was nothing to copy.
        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput(
            "AcceptanceCriteriaAuthoringWorkflow", "DispatchLifecycle");

        input.Should().NotBeNull();
        input!.Should().ContainKey("sessionId",
            "the dispatched lifecycle must receive the binding's decision-session handle, so the "
            + "accept decision can be correlated back to this run when 39-17/39-19 land");

        AllActivities().OfType<SetOutput>().Select(a => a.Id).Should().Contain("OutputSessionId",
            "a session handle the binding does not EXPOSE is no handle at all — the caller cannot "
            + "correlate what it never receives");
    }

    [Test]
    public void Workflow_CarriesTheReEntryAndBothConsumedDocumentFetchNodes()
    {
        AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle(
            "clause (c) of ResumableStandardStructuralTests — the LatestStateReEntry declaration is " +
            "honoured by a real node, not a hand-rolled guard");
        AllActivities().OfType<FetchLatestAcceptedDocumentActivity>().Select(a => a.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "FetchConsumedClarification", "FetchConsumedFindings" },
                "D2 — both consumed documents are read through the 39-14 fail-closed store seam");
    }

    [Test]
    public void Workflow_DeclaresLatestStateReEntry()
    {
        // AC5, as corrected: a thin binding owns no bookmark, so Both would fail clause (b) of
        // the 39-10 gate. Every landed producer declares LatestStateReEntry.
        var decl = typeof(AcceptanceCriteriaAuthoringWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
    }

    [Test]
    public void Workflow_HasNoBookmarkSuspendActivity()
        => AllActivities().Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Should().BeEmpty("the accept-gate suspend is inside the dispatched document-lifecycle child");

    [Test]
    public void Workflow_RoutesOnlyTypedLifecycleValues()
    {
        // Every FlowDecision routes a TYPED value off the lifecycle exit (39-12 D2's resolution
        // of "no bespoke branch"): the re-entry stage, whether a document was drafted, and
        // whether it was accepted. No parse-derived gate.
        AllActivities().OfType<FlowDecision>().Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal)
            .Should().BeEquivalentTo(new[] { "DocumentDrafted", "FreshRun", "LifecycleAccepted" });
    }

    [Test]
    public void Workflow_EmitsTheFourMemberAcceptanceCriteriaFamily()
    {
        // Rule 2 — a domain family alongside DOCUMENT.*, with a LOUD terminal.
        var emitted = AllActivities().OfType<EmitDomainLifecycleEventActivity>().Select(a => a.Id)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        emitted.Should().BeEquivalentTo(new[]
        {
            "EmitAcceptanceCriteriaAccepted",
            "EmitAcceptanceCriteriaDrafted",
            "EmitAcceptanceCriteriaFailed",
            "EmitAcceptanceCriteriaStarted",
        });

        EmitDomainLifecycleEventActivity.StatusForEvent(AcceptanceCriteriaEvents.Failed).Should().Be("error",
            "a non-accepted exit must be a LOUD error row, never a false success");
        EmitDomainLifecycleEventActivity.StatusForEvent(AcceptanceCriteriaEvents.Started).Should().Be("started");
        EmitDomainLifecycleEventActivity.StatusForEvent(AcceptanceCriteriaEvents.Accepted).Should().Be("success");
    }

    [Test]
    public void Registry_DeclaresTheProducingEdge()
    {
        // Clause (f) — the WorkflowDocumentInterface row, non-provisional (a real binding).
        var edge = DocumentTypeRegistry.WorkflowInterfaces
            .Single(i => i.WorkflowDefinitionId == "acceptance-criteria-authoring");
        edge.Produces.Should().Be(DocumentTypeKey.AcceptanceCriteria);
        edge.Consumes.Should().BeEquivalentTo(new[] { DocumentTypeKey.Clarification, DocumentTypeKey.Findings });
        edge.Provisional.Should().BeFalse();
    }

    [Test]
    public void AcceptanceCriteria_AcceptancePostureIsChosen_NotTheCatchAll()
    {
        // AC-adjacent (41-2 D8 / 41-1b AC5): acceptance-criteria must NOT silently take the
        // single-architect `_ => Rules` base row. 41-1b chose the 7-role majority panel for it
        // (it is the merge gate's definition of done and 41-15 verifies against it), so this
        // asserts the LANDED row rather than 41-2's plan-time guess of a single PO reviewer.
        var rules = Tamma.Core.Documents.Policy.AcceptanceDefaults.For(DocumentTypeKey.AcceptanceCriteria);
        rules.Should().NotBeSameAs(Tamma.Core.Documents.Policy.AcceptanceDefaults.Rules,
            "a silent fall-through to the base row is the failure mode 41-1b D1 exists to prevent");
        rules.ReviewerSelection.Mode.Should().Be(Tamma.Core.Documents.Policy.ReviewerMode.Panel);
        rules.ReviewerSelection.PanelRoles.Should().NotBeEmpty();
    }
}
