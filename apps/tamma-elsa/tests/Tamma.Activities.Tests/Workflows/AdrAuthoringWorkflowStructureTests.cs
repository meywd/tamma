using System;
using System.Linq;
using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Adr;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 41-9 — structural pins for <see cref="AdrAuthoringWorkflow"/>, the DESIGNATED REFERENCE
/// IMPLEMENTATION of the prose-on-lifecycle path (41-4, 41-5, 41-8, 41-22, 41-24, 41-25 and
/// 41-26 inherit this shape). Clause-for-clause the epic-41 rule-1 (a)–(f) set that
/// <see cref="TaskCreationWorkflowStructureTests"/> is the reference shape for. Covers AC1
/// (structure half), AC2 (no bespoke parse/terminal) and AC4 (resumable, no allowlist entry).
/// </summary>
[TestFixture]
public class AdrAuthoringWorkflowStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new AdrAuthoringWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new AdrAuthoringWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasTheDeclaredDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new AdrAuthoringWorkflow()).Object.DefinitionId
            .Should().Be("adr-authoring", "D1 — a new definition id; no incumbent is rewired");

    [Test]
    public void Workflow_ThreadsTenantId()
        => WorkflowTestHelper.BuildWorkflow(new AdrAuthoringWorkflow()).Object.Variables
            .Any(v => v.Name == "TenantId").Should().BeTrue();

    [Test]
    public void Workflow_HasNoRetryPlumbingVariables()
    {
        var names = WorkflowTestHelper.BuildWorkflow(new AdrAuthoringWorkflow())
            .Object.Variables.Select(v => v.Name ?? "").ToList();
        names.Should().NotContain("ValidationErrors");
        names.Should().NotContain("RetryCount");
        names.Should().NotContain("MaxRetries");
        names.Should().NotContain(n => n.EndsWith("Valid", StringComparison.Ordinal));
    }

    [Test]
    public void Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall()
    {
        AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "DispatchLifecycle" });
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty();
        StructureWalk.LiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle"))
            .Should().Be("document-lifecycle");
    }

    [Test]
    public void Workflow_HasNoFinishActivity()
        => AllActivities().OfType<Finish>().Should().BeEmpty(
            "AC2 — every non-success exit is a typed lifecycle escalation, never a bespoke terminal");

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalPair_ProseType_AndDeclaredFeedbackCarrier()
    {
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "AdrAuthoringWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.Architect.ToWire() && p.Action == AgentAction.WriteAdr.ToWire(),
            "the binding hands the canonical (architect, write-adr) producer pair");

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("AdrAuthoringWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("prose",
            "41-9 consumes 41-1c's prose type with kind=adr — it must NOT mint an 'adr' document type");
        (input!["feedbackVariableName"] as string).Should().Be("findings",
            "D5 — write-adr.md declares role/workItemJson/findings/audience; a repair note routed to " +
            "an undeclared key is silently dropped at render (the 39-15 lesson)");
    }

    [Test]
    public void DispatchLifecycle_CarriesTheAdrKindAndAudienceInTheProducerVariables()
    {
        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("AdrAuthoringWorkflow", "DispatchLifecycle");
        var variables = input!["producerVariablesJson"] as string;
        variables.Should().NotBeNullOrWhiteSpace();
        variables.Should().Contain("\"audience\"",
            "the audience tag is the point of the prose type — it rides a DECLARED producer variable");
    }

    [Test]
    public void Workflow_CarriesTheReEntryAndBothConsumedDocumentFetchNodes()
    {
        AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();
        AllActivities().OfType<FetchLatestAcceptedDocumentActivity>().Select(a => a.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(
                new[] { "FetchAmbiguityAssessment", "FetchConsumedDesign", "FetchConsumedFindings" },
                "D2 — the ADR is seeded from the accepted Design (41-10 / design-proposal) and Findings; " +
                "39-25 adds the accepted ambiguity-assessment fetch that threads leg 1");
    }

    [Test]
    public void Workflow_DeclaresLatestStateReEntry()
    {
        // AC4, as corrected: Both would fail clause (b) of the 39-10 gate — a thin binding owns
        // no canonical suspend node; the accept-gate bookmark lives in the dispatched child.
        var decl = typeof(AdrAuthoringWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
    }

    [Test]
    public void Workflow_HasNoBookmarkSuspendActivity()
        => AllActivities().Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Should().BeEmpty("the accept-gate suspend is inside the dispatched document-lifecycle child");

    [Test]
    public void Workflow_RoutesOnlyTypedLifecycleValues()
        => AllActivities().OfType<FlowDecision>().Select(d => d.Id).OrderBy(x => x, StringComparer.Ordinal)
            .Should().BeEquivalentTo(new[] { "AdrAccepted", "DocumentDrafted", "FreshRun" });

    [Test]
    public void Workflow_EmitsTheFourMemberAdrFamily_WithALoudTerminal()
    {
        var emitted = AllActivities().OfType<EmitDomainLifecycleEventActivity>().Select(a => a.Id)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        emitted.Should().BeEquivalentTo(new[]
        {
            "EmitAdrAccepted", "EmitAdrDrafted", "EmitAdrFailed", "EmitAdrStarted",
        });

        // D6 — the story names three members; the fourth exists so a degraded exit is never
        // recorded as a success.
        EmitDomainLifecycleEventActivity.StatusForEvent(AdrEvents.Failed).Should().Be("error");
        EmitDomainLifecycleEventActivity.StatusForEvent(AdrEvents.Started).Should().Be("started");
        EmitDomainLifecycleEventActivity.StatusForEvent(AdrEvents.Drafted).Should().Be("success");
        EmitDomainLifecycleEventActivity.StatusForEvent(AdrEvents.Accepted).Should().Be("success");
    }

    [Test]
    public void Registry_DeclaresTheProducingEdge_ToProse()
    {
        var edge = DocumentTypeRegistry.WorkflowInterfaces.Single(i => i.WorkflowDefinitionId == "adr-authoring");
        edge.Produces.Should().Be(DocumentTypeKey.Prose,
            "41-9 must NOT mint an 'adr' document type (Correction 2) — it consumes 41-1c's prose");
        edge.Consumes.Should().BeEquivalentTo(new[] { DocumentTypeKey.Design, DocumentTypeKey.Findings });
        edge.Provisional.Should().BeFalse();
    }

    [Test]
    public void NoAdrDocumentTypeWasMinted()
        => DocumentTypeRegistry.All.Should().NotContain(t => t.Key == "adr",
            "Correction 2 — ADRs ride 41-1c's prose type with kind=adr; a dedicated type would be a " +
            "full vocabulary lockstep for a body with no schema to validate");
}
