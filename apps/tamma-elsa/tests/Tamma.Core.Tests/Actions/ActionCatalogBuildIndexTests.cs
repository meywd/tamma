using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// The fail-loud <c>BuildIndex</c> contract (Story 43-2 AC11, D7/D8; 43-3 AC1):
/// eight DISTINCT throw codes, each landing at boot on the developer who just
/// broke the catalog, each message naming the offending member — the message is
/// the entire remediation UX. Exercised through the internal seam
/// (<c>InternalsVisibleTo Tamma.Core.Tests</c>) so the real code path validates
/// deliberately-bad descriptor arrays without touching the shipped table.
/// </summary>
[TestFixture]
public class ActionCatalogBuildIndexTests
{
    private static List<ActionDescriptor> ValidTable() => ActionCatalog.All.ToList();

    private static TammaError InvokeExpectingThrow(IReadOnlyList<ActionDescriptor> table)
    {
        var act = () => ActionCatalog.BuildIndex(table);
        return act.Should().Throw<TammaError>().Which;
    }

    [Test]
    public void The_shipped_table_builds_cleanly()
    {
        var index = ActionCatalog.BuildIndex(ValidTable());

        index.ByKey.Should().HaveCount(ActionCatalog.All.Count);
        index.ByGroup.Values.Sum(set => set.Count).Should().Be(ActionCatalog.All.Count);
    }

    [Test]
    public void Duplicate_key_throws_DUPLICATE_KEY_naming_the_member()
    {
        var table = ValidTable();
        table.Add(table[0]);

        var error = InvokeExpectingThrow(table);

        error.Code.Should().Be("ACTION.CATALOG.DUPLICATE_KEY");
        error.Message.Should().Contain(table[0].Key.ToWire());
    }

    [Test]
    public void Missing_descriptor_throws_MISSING_DESCRIPTOR_naming_the_wire()
    {
        var table = ValidTable();
        var removed = table.Single(d => d.Key.ToWire() == "agent-action:deploy");
        table.Remove(removed);

        var error = InvokeExpectingThrow(table);

        error.Code.Should().Be("ACTION.CATALOG.MISSING_DESCRIPTOR");
        error.Message.Should().Contain("agent-action:deploy");
    }

    [Test]
    public void Orphan_descriptor_throws_ORPHAN_DESCRIPTOR_naming_the_key()
    {
        var table = ValidTable();
        table.Add(table[0] with { Key = new ActionKey(ActionNamespace.AgentAction, "not-a-real-action") });

        var error = InvokeExpectingThrow(table);

        error.Code.Should().Be("ACTION.CATALOG.ORPHAN_DESCRIPTOR");
        error.Message.Should().Contain("agent-action:not-a-real-action");
    }

    [Test]
    public void Undefined_namespace_value_throws_UNKNOWN_NAMESPACE_KEY()
    {
        var table = ValidTable();
        table.Add(table[0] with { Key = new ActionKey((ActionNamespace)999, "deploy") });

        var error = InvokeExpectingThrow(table);

        error.Code.Should().Be("ACTION.CATALOG.UNKNOWN_NAMESPACE_KEY");
        error.Message.Should().Contain("999");
    }

    [Test]
    public void Out_of_range_default_throws_INVALID_DEFAULT()
    {
        var table = ValidTable();
        // AlwaysHuman + 1 is outside [Min, Max] ∪ {AlwaysHuman} — the sentinel is
        // a closed set, not an open tail.
        table[0] = table[0] with { DefaultMinAutonomy = AutonomyDial.AlwaysHuman + 1 };

        var error = InvokeExpectingThrow(table);

        error.Code.Should().Be("ACTION.CATALOG.INVALID_DEFAULT");
        error.Message.Should().Contain(table[0].Key.ToWire());
    }

    [Test]
    public void Empty_metadata_throws_EMPTY_METADATA()
    {
        var table = ValidTable();
        table[0] = table[0] with { Title = "   " };

        var error = InvokeExpectingThrow(table);

        error.Code.Should().Be("ACTION.CATALOG.EMPTY_METADATA");
        error.Message.Should().Contain(table[0].Key.ToWire());
    }

    [Test]
    public void Repeated_site_key_in_a_unique_namespace_throws_DUPLICATE_SITE_KEY()
    {
        var table = ValidTable();
        var effects = table.Where(d => d.Key.Ns == ActionNamespace.Effect).Take(2).ToArray();
        var index = table.IndexOf(effects[1]);
        table[index] = effects[1] with { SiteKey = effects[0].SiteKey };

        var error = InvokeExpectingThrow(table);

        error.Code.Should().Be("ACTION.CATALOG.DUPLICATE_SITE_KEY");
        error.Message.Should().Contain(effects[0].SiteKey);
    }

    [Test]
    public void An_emptied_group_throws_GROUP_EMPTY_naming_the_group()
    {
        var table = ValidTable();
        // code-write has exactly one member (tool:file_write); reassigning it
        // empties the group — a group must never rot into a dead label (43-3 AC1).
        var fileWrite = table.Single(d => d.Key.ToWire() == "tool:file_write");
        table[table.IndexOf(fileWrite)] = fileWrite with { Group = ActionGroup.CodeRead };

        var error = InvokeExpectingThrow(table);

        error.Code.Should().Be("ACTION.CATALOG.GROUP_EMPTY");
        error.Message.Should().Contain("code-write");
    }

    [Test]
    public void Unknown_member_lookup_throws_ACTION_CATALOG_UNKNOWN_MEMBER()
    {
        var act = () => ActionCatalog.Get(new ActionKey(ActionNamespace.Tool, "not-a-tool"));

        act.Should().Throw<TammaError>()
            .Which.Code.Should().Be("ACTION.CATALOG.UNKNOWN_MEMBER");
    }
}
