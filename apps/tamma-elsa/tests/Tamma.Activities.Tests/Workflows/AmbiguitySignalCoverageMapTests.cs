using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Core.Documents;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-25 (AC4/AC5, D6/D7) — the family × signal COVERAGE MAP as compiled fixtures plus
/// the derivation tests that keep them honest. Two fixtures:
///
/// <list type="number">
/// <item><b>Dispatcher map</b> — one row per workflow that dispatches <c>document-lifecycle</c>,
/// declaring its ambiguity signal: <c>SelfScored</c> (leg 2 — the lifecycle reads the
/// <c>ambiguity-assessment</c> payload itself), <c>Threaded</c> (leg 1 — the binding fetches the
/// latest accepted assessment and passes <c>ambiguityScore</c>), or <c>None</c> with a stated
/// reason. The tests DERIVE each workflow's actual signal from its built graph and assert
/// derived == declared, and that the key set equals the discovered dispatcher set — so a
/// dispatcher appearing, disappearing, or gaining/losing the signal without a fixture edit
/// FAILS THE BUILD.</item>
/// <item><b>Honesty table</b> — one row per <see cref="DocumentTypeKey"/> (17), the story's
/// family table flattened: ambiguity signal × no-agreement signal. Key set pinned to the enum,
/// cross-checked against fixture (1) through <see cref="DocumentTypeRegistry.WorkflowInterfaces"/>
/// — a new document type or a signal change without a map edit fails.</item>
/// </list>
///
/// <para>Plus the vocabulary pin (a 5th <see cref="DocumentLifecycleOutcome"/> — i.e. a new
/// escalation signal — fails until the map is updated), the stated tool-call/effects row
/// ("none — classification only"), and the D7 source-shape pin that the threaded score feeds
/// ONLY the ambiguity gate.</para>
/// </summary>
[TestFixture]
public class AmbiguitySignalCoverageMapTests
{
    private const string LifecycleDefinitionId = "document-lifecycle";
    private const string AmbiguityAssessmentType = "ambiguity-assessment";

    private enum AmbiguitySignal { SelfScored, Threaded, None }

    // ====================================================================
    // Fixture (a) — dispatcher map: 14 rows, one per lifecycle dispatcher
    // ====================================================================

    private static readonly IReadOnlyDictionary<string, (AmbiguitySignal Signal, string Reason)> DispatcherMap =
        new Dictionary<string, (AmbiguitySignal, string)>
        {
            ["AmbiguityScoringWorkflow"] = (AmbiguitySignal.SelfScored,
                "leg 2 — the lifecycle reads the ambiguity-assessment payload's own score (D4: " +
                "fetching here would pre-escalate every re-score on the PREVIOUS run's score)"),
            ["ClarifyingQuestionsWorkflow"] = (AmbiguitySignal.None,
                "resolution path (D3) — threading would escalate the resolution of ambiguity on the " +
                "very score it exists to resolve (Run A), or discard a human's already-given answers " +
                "(Run B); deliberate, fixture-recorded narrowing of the story's family table"),
            ["IssueDecompositionWorkflow"]           = (AmbiguitySignal.Threaded, "leg 1 (39-25)"),
            ["PlanGenerationWorkflow"]               = (AmbiguitySignal.Threaded, "leg 1 (39-25)"),
            ["TaskCreationWorkflow"]                 = (AmbiguitySignal.Threaded, "leg 1 (39-25) — fetched at the BASE issue id, not the producer scope"),
            ["TestCaseCreationWorkflow"]             = (AmbiguitySignal.Threaded, "leg 1 (39-25)"),
            ["DebugDiagnosisWorkflow"]               = (AmbiguitySignal.Threaded, "leg 1 (39-25)"),
            ["ResearchWorkflow"]                     = (AmbiguitySignal.Threaded, "leg 1 (39-25)"),
            ["DesignProposalWorkflow"]               = (AmbiguitySignal.Threaded, "leg 1 (39-25)"),
            ["AdrAuthoringWorkflow"]                 = (AmbiguitySignal.Threaded, "leg 1 (39-25) — fetched at the BASE issue id, not the prose producer scope"),
            ["AcceptanceCriteriaAuthoringWorkflow"]  = (AmbiguitySignal.Threaded, "leg 1 (39-25)"),
            ["BacklogPrioritizationWorkflow"]        = (AmbiguitySignal.Threaded, "leg 1 (39-25) — run-scoped on the backlog anchor; honest null in practice"),
            ["TriageContextGatheringWorkflow"]       = (AmbiguitySignal.Threaded, "leg 1 (39-25) — keyed on the triage-context scoped anchor; honest null in practice"),
            ["TriagePODecisionWorkflow"]             = (AmbiguitySignal.Threaded, "leg 1 (39-25)"),
        };

    // ====================================================================
    // Fixture (b) — honesty table: one row per DocumentTypeKey (17)
    // ====================================================================

    /// <summary>No-agreement is the review panel's job wherever one is configured; the
    /// split/below-quorum/empty/Critical-veto paths exit <c>review-undecidable</c> (verified 2-F).</summary>
    private const string PanelWhereConfigured = "panel where configured (review-undecidable)";

    private static readonly IReadOnlyDictionary<DocumentTypeKey, (AmbiguitySignal Ambiguity, string NoAgreement)> HonestyTable =
        new Dictionary<DocumentTypeKey, (AmbiguitySignal, string)>
        {
            [DocumentTypeKey.AmbiguityAssessment] = (AmbiguitySignal.SelfScored, PanelWhereConfigured),
            // Documents downstream of an assessment in the same run — leg 1 (this story).
            [DocumentTypeKey.Findings]           = (AmbiguitySignal.Threaded, PanelWhereConfigured),
            [DocumentTypeKey.Decomposition]      = (AmbiguitySignal.Threaded, PanelWhereConfigured),
            [DocumentTypeKey.Plan]               = (AmbiguitySignal.Threaded, PanelWhereConfigured),
            [DocumentTypeKey.Design]             = (AmbiguitySignal.Threaded, PanelWhereConfigured),
            [DocumentTypeKey.TriageDecision]     = (AmbiguitySignal.Threaded, PanelWhereConfigured),
            [DocumentTypeKey.Diagnosis]          = (AmbiguitySignal.Threaded, PanelWhereConfigured),
            [DocumentTypeKey.TestSpec]           = (AmbiguitySignal.Threaded, PanelWhereConfigured),
            [DocumentTypeKey.AcceptanceCriteria] = (AmbiguitySignal.Threaded, PanelWhereConfigured),
            [DocumentTypeKey.BacklogOrdering]    = (AmbiguitySignal.Threaded, PanelWhereConfigured),
            [DocumentTypeKey.Prose]              = (AmbiguitySignal.Threaded, PanelWhereConfigured),
            // The deliberate D3 narrowing: clarification is the RESOLUTION of a high score.
            [DocumentTypeKey.Clarification]      = (AmbiguitySignal.None, PanelWhereConfigured),
            // Types with no lifecycle-binding producer today — none, STATED, not implied.
            [DocumentTypeKey.Review]             = (AmbiguitySignal.None, PanelWhereConfigured),
            [DocumentTypeKey.SprintPlan]         = (AmbiguitySignal.None, PanelWhereConfigured),
            [DocumentTypeKey.TestPlan]           = (AmbiguitySignal.None, PanelWhereConfigured),
            [DocumentTypeKey.ThreatModel]        = (AmbiguitySignal.None, PanelWhereConfigured),
            [DocumentTypeKey.UxSpec]             = (AmbiguitySignal.None, PanelWhereConfigured),
        };

    /// <summary>The story table's fourth family, stated rather than hidden: tool calls /
    /// effects have NO content-ambiguity signal (classification only) and NO panel.</summary>
    private static readonly (string Ambiguity, string NoAgreement) ToolCallsAndEffectsRow =
        ("none — classification only (denylist)", "none");

    // ====================================================================
    // Fixture (a) tests — set equality + signal derivation
    // ====================================================================

    [Test]
    public void DispatcherMap_KeySetEqualsLifecycleDispatchers()
    {
        var discovered = LifecycleDispatchers().Select(d => d.Name).OrderBy(x => x, StringComparer.Ordinal);
        discovered.Should().BeEquivalentTo(
            DispatcherMap.Keys.OrderBy(x => x, StringComparer.Ordinal),
            "every workflow dispatching document-lifecycle must carry a coverage-map row, and every " +
            "row must correspond to a real dispatcher — a dispatcher appearing or disappearing " +
            "without a fixture edit fails here (39-25 AC4; same pattern as 39-24 AC10)");
        DispatcherMap.Should().HaveCount(14, "the 39-25 dispatcher census: 14 binding workflows");
    }

    [Test]
    public void DispatcherMap_MatchesDerivedSignals()
    {
        foreach (var (name, root) in LifecycleDispatchers())
        {
            DispatcherMap.Should().ContainKey(name);
            var declared = DispatcherMap[name].Signal;
            var derived = DeriveSignal(name, root);
            derived.Should().Be(declared,
                $"the coverage map declares {name} as {declared}, but its built graph derives " +
                $"{derived} — a dispatcher gaining or losing the ambiguity signal requires a " +
                "conscious fixture edit (39-25 AC4)");
        }

        DispatcherMap.Values.Count(v => v.Signal == AmbiguitySignal.Threaded)
            .Should().Be(12, "the 39-25 threading census: 12 threading sites");
        DispatcherMap.Values.Count(v => v.Signal == AmbiguitySignal.SelfScored)
            .Should().Be(1, "exactly one self-scored type (leg 2) exists");
    }

    [Test]
    public void SelfScoredDispatcher_DoesNotAlsoFetch_D4()
    {
        // D4 — a fetch inside AmbiguityScoringWorkflow would make a high score self-sealing
        // (every re-score pre-escalated on the previous run's score).
        var (_, root) = LifecycleDispatchers().Single(d => d.Name == "AmbiguityScoringWorkflow");
        AmbiguityFetches(root).Should().BeEmpty(
            "the self-scored producer must NOT thread its own previous score (39-25 D4)");
    }

    // ====================================================================
    // AC2 (structural half) — the key is OMITTED at default state, never 0.0
    // ====================================================================

    [Test]
    public void ThreadedSites_OmitAmbiguityScoreAtDefault()
    {
        foreach (var name in DispatcherMap.Where(kv => kv.Value.Signal == AmbiguitySignal.Threaded).Select(kv => kv.Key))
        {
            var input = TaxonomyDriftBuildTests.MaterializeDispatchInput(name, "DispatchLifecycle");
            input.Should().NotBeNull($"{name}'s DispatchLifecycle input must be materialisable");
            input!.Keys.Should().NotContain("ambiguityScore",
                $"{name} must OMIT the ambiguityScore key when no assessment was fetched " +
                "(default variable state) — an unconditional key (e.g. score ?? 0.0) would read " +
                "as 'measured unambiguous', the exact lie 39-25 AC2 forbids");
        }
    }

    // ====================================================================
    // Fixture (b) tests — honesty table pins
    // ====================================================================

    [Test]
    public void HonestyTable_CoversEveryDocumentTypeKey()
        => HonestyTable.Keys.Should().BeEquivalentTo(
            Enum.GetValues<DocumentTypeKey>(),
            "the honesty table carries exactly one row per document type (17) — a new " +
            "DocumentTypeKey member without a map edit fails here (39-25 AC4)");

    [Test]
    public void HonestyTable_AgreesWithDispatcherMap_ViaRegistry()
    {
        // Map each fixture-(a) workflow to its DefinitionId, then through the registry's
        // producing edges to the document types it mints — the type's declared ambiguity
        // signal must agree with its producers' declared signals (strongest wins:
        // SelfScored > Threaded > None). Types with no fixture-(a) producer must say None.
        var signalByDefinitionId = _dispatchers.ToDictionary(
            d => d.DefinitionId,
            d => DispatcherMap[d.Name].Signal,
            StringComparer.Ordinal);

        foreach (var typeKey in Enum.GetValues<DocumentTypeKey>())
        {
            var producerSignals = DocumentTypeRegistry.WorkflowInterfaces
                .Where(w => w.Produces == typeKey)
                .Where(w => signalByDefinitionId.ContainsKey(w.WorkflowDefinitionId))
                .Select(w => signalByDefinitionId[w.WorkflowDefinitionId])
                .ToList();

            var expected =
                producerSignals.Contains(AmbiguitySignal.SelfScored) ? AmbiguitySignal.SelfScored
                : producerSignals.Contains(AmbiguitySignal.Threaded) ? AmbiguitySignal.Threaded
                : AmbiguitySignal.None;

            HonestyTable[typeKey].Ambiguity.Should().Be(expected,
                $"the honesty table's '{typeKey.ToWire()}' row must reflect its lifecycle-binding " +
                "producers' signals (none ⇒ stated None, never implied coverage) — 39-25 AC4");
        }
    }

    [Test]
    public void HonestyTable_NoAgreementSignal_IsThePanelEverywhere_AndStatedForToolCalls()
    {
        // No-agreement already works where a panel is configured (2-F verified); the table
        // says so uniformly for document types, and says "none" for tool calls/effects
        // instead of hiding it.
        HonestyTable.Values.Select(v => v.NoAgreement).Should().OnlyContain(
            s => s == PanelWhereConfigured,
            "every document type's no-agreement signal is the review panel where configured");
        ToolCallsAndEffectsRow.Ambiguity.Should().StartWith("none",
            "tool/effect paths have no content-ambiguity signal beyond the denylist — stated, not hidden");
        ToolCallsAndEffectsRow.NoAgreement.Should().Be("none");
    }

    // ====================================================================
    // Signal-vocabulary pin — a new escalation signal fails until mapped
    // ====================================================================

    [Test]
    public void OutcomeVocabulary_IsExactlyFour()
        => Enum.GetValues<DocumentLifecycleOutcome>().Should().BeEquivalentTo(
            new[]
            {
                DocumentLifecycleOutcome.ReviewUndecidable,
                DocumentLifecycleOutcome.AmbiguityAboveThreshold,
                DocumentLifecycleOutcome.RoundsExhausted,
                DocumentLifecycleOutcome.ValidationExhausted,
            },
            "the lifecycle's escalation-signal vocabulary is pinned at 4 — a new signal (a 5th " +
            "member) must update this coverage map before it can land (39-25 AC4)");

    // ====================================================================
    // AC5 (D7 source-shape pin) — the threaded score feeds ONLY the gate
    // ====================================================================

    [Test]
    public void ThreadedInput_FeedsOnlyTheAmbiguityGate()
    {
        // (i) Source shape: state.AmbiguityScore is consumed at exactly ONE place in
        // DocumentLifecycleWorkflow — the AmbiguityCheck value. The score becoming an input
        // to anything but the gate would be a policy change 39-25 promises not to make.
        var src = ReadLifecycleWorkflowSource();
        System.Text.RegularExpressions.Regex.Matches(src, @"state\.AmbiguityScore").Count
            .Should().Be(1,
                "the threaded ambiguityScore must feed exactly one consumer — the AmbiguityCheck " +
                "read that drives the AmbiguityGate (39-25 AC5/D7)");

        // (ii) Graph shape: AmbiguityCheck → AmbiguityGate; True → SeedAmbiguity (the
        // level-independent human pull), False → EmitReviewRequested ("escalates BEFORE review").
        var root = BuildRoot(new DocumentLifecycleWorkflow());
        var fc = StructureWalk.All(root).OfType<Flowchart>().First();
        var intoGate = fc.Connections.Where(c => c.Target.Activity?.Id == "AmbiguityGate").ToList();
        intoGate.Should().ContainSingle().Which.Source.Activity!.Id.Should().Be("AmbiguityCheck");

        var fromGate = fc.Connections.Where(c => c.Source.Activity?.Id == "AmbiguityGate")
            .ToDictionary(c => c.Source.Port ?? "", c => c.Target.Activity!.Id);
        fromGate.Should().HaveCount(2);
        fromGate["True"].Should().Be("SeedAmbiguity",
            "a score at/above threshold pulls a person via the typed ambiguity-above-threshold escalation");
        fromGate["False"].Should().Be("EmitReviewRequested",
            "the check sits BEFORE review — below-threshold flows straight into the review ring");
    }

    // ====================================================================
    // Derivation machinery
    // ====================================================================

    private static AmbiguitySignal DeriveSignal(string workflowName, IActivity root)
    {
        var selfScored = StructureWalk.All(root)
            .OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == LifecycleDefinitionId)
            .Any(d =>
            {
                var input = TaxonomyDriftBuildTests.MaterializeDispatchInput(workflowName, d.Id ?? "");
                return input is not null
                    && input.TryGetValue("documentType", out var t)
                    && (t as string) == AmbiguityAssessmentType;
            });
        var threads = AmbiguityFetches(root).Any();

        (selfScored && threads).Should().BeFalse(
            $"{workflowName} cannot be BOTH self-scored and threading (D4 — a self-fetch makes a " +
            "high score self-sealing)");

        return selfScored ? AmbiguitySignal.SelfScored
            : threads ? AmbiguitySignal.Threaded
            : AmbiguitySignal.None;
    }

    private static IEnumerable<FetchLatestAcceptedDocumentActivity> AmbiguityFetches(IActivity root)
        => StructureWalk.All(root)
            .OfType<FetchLatestAcceptedDocumentActivity>()
            .Where(f => LiteralTypeKey(f) == AmbiguityAssessmentType);

    /// <summary>The fetch node's DocumentTypeKey literal (null when delegate-backed) — the
    /// StructureWalk.LiteralDefId idiom extended to the fetch seam.</summary>
    private static string? LiteralTypeKey(FetchLatestAcceptedDocumentActivity fetch)
    {
        var value = typeof(FetchLatestAcceptedDocumentActivity)
            .GetProperty(nameof(FetchLatestAcceptedDocumentActivity.DocumentTypeKey))?.GetValue(fetch);
        var expr = value?.GetType()
            .GetProperty("Expression", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(value) as Expression;
        return expr?.Value as string;
    }

    /// <summary>Every instantiable workflow in Tamma.ElsaServer whose graph dispatches
    /// <c>document-lifecycle</c> by literal definition id, with its built root + definition id.</summary>
    private static List<(string Name, string DefinitionId, IActivity Root)> _dispatchers = Discover();

    private static IEnumerable<(string Name, IActivity Root)> LifecycleDispatchers()
        => _dispatchers.Select(d => (d.Name, d.Root));

    private static List<(string Name, string DefinitionId, IActivity Root)> Discover()
    {
        var result = new List<(string, string, IActivity)>();
        var assembly = typeof(LlmCallWorkflow).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(WorkflowBase).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) == null) continue;

            WorkflowBase? instance = null;
            try { instance = (WorkflowBase)Activator.CreateInstance(type)!; }
            catch { continue; }

            var builder = WorkflowTestHelper.BuildWorkflow(instance);
            var root = builder.Object.Root;
            if (root == null) continue;

            var dispatchesLifecycle = StructureWalk.All(root)
                .OfType<DispatchWorkflow>()
                .Any(d => StructureWalk.LiteralDefId(d) == LifecycleDefinitionId);
            if (dispatchesLifecycle)
                result.Add((type.Name, builder.Object.DefinitionId, root));
        }
        return result;
    }

    private static IActivity BuildRoot(WorkflowBase workflow)
        => WorkflowTestHelper.BuildWorkflow(workflow).Object.Root!;

    private static string ReadLifecycleWorkflowSource()
    {
        // The CodeReviewWorkflowStructureTests source-resolution idiom.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var nested = Path.Combine(dir.FullName, "apps", "tamma-elsa", "src", "Tamma.ElsaServer", "Workflows", "DocumentLifecycleWorkflow.cs");
            if (File.Exists(nested)) return File.ReadAllText(nested);
            var flat = Path.Combine(dir.FullName, "src", "Tamma.ElsaServer", "Workflows", "DocumentLifecycleWorkflow.cs");
            if (File.Exists(flat)) return File.ReadAllText(flat);
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate DocumentLifecycleWorkflow.cs source.");
    }
}
