using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Actions;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Bidirectional keyset-equality drift tests (Story 43-2 AC12, D9) — SET
/// equality, both directions, so a missing descriptor AND an orphan descriptor
/// both fail.
///
/// <para>
/// HONESTY NOTE (43-2 D9 / epic risk 6): only the <c>agent-action</c> and
/// <c>document-type</c> assertions bind the catalog to a vocabulary the catalog
/// did not author. For <c>tool</c>/<c>effect</c>/<c>automation</c>/<c>platform-task</c>
/// the same assertion compares the catalog to enums THIS EPIC wrote — internal
/// consistency, not reality. The real bindings for those planes are the
/// reflection sweeps in <c>Tamma.Activities.Tests/Actions/</c> (tools, actors,
/// platform tasks, git subcommands) and, for the route-derived effects, Story
/// 43-8's route-table harness — until 43-8 lands, a new mutating route changes
/// nothing here. Pretending otherwise would be worse than the gap.
/// </para>
/// </summary>
[TestFixture]
public class ActionCatalogKeysetTests
{
    private static string[] CatalogKeysOf(ActionNamespace ns) =>
        ActionCatalog.ByKey.Keys.Where(k => k.Ns == ns).Select(k => k.Key).ToArray();

    [Test]
    public void AgentAction_plane_equals_the_AgentAction_wire_set()
    {
        CatalogKeysOf(ActionNamespace.AgentAction).Should().BeEquivalentTo(
            Enum.GetValues<AgentAction>().Select(a => a.ToWire()));
    }

    [Test]
    public void DocumentType_plane_equals_the_DocumentTypeKey_wire_set()
    {
        CatalogKeysOf(ActionNamespace.DocumentType).Should().BeEquivalentTo(
            Enum.GetValues<DocumentTypeKey>().Select(d => d.ToWire()));
    }

    [Test]
    public void Tool_plane_equals_the_ToolAction_wire_set()
    {
        // SELF-REFERENTIAL (see fixture doc): real binding is ToolExecutorCatalogSweepTests.
        CatalogKeysOf(ActionNamespace.Tool).Should().BeEquivalentTo(
            Enum.GetValues<ToolAction>().Select(t => t.ToWire()));
    }

    [Test]
    public void Effect_plane_equals_the_ExternalEffect_wire_set()
    {
        // SELF-REFERENTIAL (see fixture doc): real binding arrives with Story 43-8's
        // route-table harness — nothing authored in 43-2/43-3 fails on a new route.
        CatalogKeysOf(ActionNamespace.Effect).Should().BeEquivalentTo(
            Enum.GetValues<ExternalEffect>().Select(e => e.ToWire()));
    }

    [Test]
    public void Automation_plane_equals_the_BackgroundActor_wire_set()
    {
        // SELF-REFERENTIAL (see fixture doc): real binding is BackgroundActorCatalogSweepTests.
        CatalogKeysOf(ActionNamespace.Automation).Should().BeEquivalentTo(
            Enum.GetValues<BackgroundActor>().Select(b => b.ToWire()));
    }

    [Test]
    public void PlatformTask_plane_equals_the_PlatformTaskKind_wire_set()
    {
        // SELF-REFERENTIAL (see fixture doc): real binding is PlatformTaskCatalogSweepTests.
        CatalogKeysOf(ActionNamespace.PlatformTask).Should().BeEquivalentTo(
            Enum.GetValues<PlatformTaskKind>().Select(p => p.ToWire()));
    }
}
