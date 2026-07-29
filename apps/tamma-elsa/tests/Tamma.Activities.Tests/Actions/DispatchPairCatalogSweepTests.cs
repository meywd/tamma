using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Tests.Workflows;
using Tamma.Core.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-8 (AC7) — every <c>(role, action)</c> pair a COMPILED workflow actually
/// dispatches must resolve to an <c>agent-action:*</c> member of the Action Catalog.
///
/// <para><b>What it reflects over, and why that is the strong version.</b> It reuses
/// <c>TaxonomyDriftBuildTests.EnumerateAllDispatchPairs()</c> — the existing
/// reflection over compiled Elsa workflow graphs, which materialises each dispatch
/// site's real action wire by INVOKING the site's <c>Input</c> delegate against a
/// synthetic execution context. So the wires checked here are the ones the running
/// engine emits, not a second declaration of them. That enumeration is protected by
/// four anti-no-op tripwires in its own fixture, which this sweep inherits for
/// free — plus a local one
/// (<see cref="The_dispatch_enumeration_is_not_empty"/>), because a harness that
/// depends on another fixture's tripwires should still fail loudly on its own if
/// that dependency ever returns nothing.</para>
///
/// <para><b>Why a separate fixture rather than an assertion grafted into
/// <c>TaxonomyDriftBuildTests</c></b> (which is what AC7 literally asks for): the
/// enumeration is <c>internal</c> and therefore reusable from here with no change to
/// the file that owns it, and keeping the catalog binding in the Actions folder puts
/// it next to the other Epic 43 sweeps and inside the epic's CI drift filter. The
/// assertion is the one AC7 specifies.</para>
///
/// <para><b>WHAT THIS SWEEP CANNOT SEE:</b> pairs that no compiled site emits.
/// Data-driven dispatches (<c>DocumentLifecycleWorkflow</c>,
/// <c>SingleReviewerWorkflow</c>) read their <c>(role, action)</c> from workflow
/// variables, so their wires never materialise here; policy-reachable reviewer pairs
/// are likewise invisible. Those planes are classified by
/// <c>ContractBindingTests</c>, not here. The complement is covered by the catalog's
/// own totality check — every <c>AgentAction</c> member has a descriptor — so a pair
/// that is dispatchable-but-not-dispatched still cannot be uncatalogued.</para>
/// </summary>
[TestFixture]
public class DispatchPairCatalogSweepTests
{
    [Test]
    public void The_dispatch_enumeration_is_not_empty()
    {
        // ANTI-NO-OP TRIPWIRE (local). If the shared enumeration returns nothing,
        // the assertion below would pass over an empty set and read as coverage.
        TaxonomyDriftBuildTests.EnumerateAllDispatchPairs().Should().NotBeEmpty(
            "the compiled-graph dispatch enumeration returned nothing — this sweep would be a no-op "
            + "(TaxonomyDriftBuildTests' own tripwires should also be failing)");
    }

    [Test]
    public void EveryDispatchedActionWire_ResolvesInTheActionCatalog()
    {
        var unresolved = TaxonomyDriftBuildTests.EnumerateAllDispatchPairs()
            .Where(p => !ActionCatalog.ByKey.ContainsKey(new ActionKey(ActionNamespace.AgentAction, p.Action)))
            .Select(p => $"  {p.Workflow}.{p.DispatchId}: agent-action:{p.Action}")
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        unresolved.Should().BeEmpty(
            "every action wire a compiled llm-call dispatch site emits must be a catalogued "
            + "agent-action:* member — an agent step that no catalog row covers is an ungoverned "
            + "capability the admin surface cannot show, let alone gate:"
            + Environment.NewLine + string.Join(Environment.NewLine, unresolved));
    }

    [Test]
    public void EveryDispatchedRole_IsACanonicalWire()
    {
        // The role half has no catalog namespace of its own (the catalog keys on the
        // ACTION), so the binding that matters for the role is canonical-wire
        // membership. Asserted here so the pair is checked as a pair, not half of one.
        var pairs = TaxonomyDriftBuildTests.EnumerateAllDispatchPairs();

        var unknownRoles = pairs
            .Where(p => !Tamma.Api.Services.Agents.RolePhaseMap.ValidRoles.Contains(p.Role))
            .Select(p => $"  {p.Workflow}.{p.DispatchId}: role='{p.Role}'")
            .Distinct()
            .ToList();

        unknownRoles.Should().BeEmpty(
            "a dispatch site emitting a non-canonical role wire would resolve to no catalog row:"
            + Environment.NewLine + string.Join(Environment.NewLine, unknownRoles));
    }

    // ====================================================================
    // DISCRIMINATION PROOF
    // ====================================================================

    [Test]
    public void Discrimination_anUncataloguedActionWireWouldNotResolve()
    {
        // The lookup this sweep relies on must actually be capable of MISSING. If
        // ActionCatalog.ByKey answered true for everything (a wildcard, a fallback),
        // the assertion above would be inert.
        ActionCatalog.ByKey.ContainsKey(new ActionKey(ActionNamespace.AgentAction, "no-such-action"))
            .Should().BeFalse();

        ActionCatalog.ByKey.ContainsKey(new ActionKey(ActionNamespace.AgentAction, "deploy"))
            .Should().BeTrue("a real dispatched wire must resolve, or the sweep is inverted");
    }
}
