using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// The strict-partition contract (Story 43-3 AC1/AC2): every catalogued action in
/// exactly one group, no empty group, and the by-group index PROJECTED from the
/// descriptors — never a hand-maintained second table.
/// </summary>
[TestFixture]
public class ActionGroupPartitionTests
{
    [Test]
    public void Every_member_has_exactly_one_group()
    {
        // Totality AND disjointness in one assertion: the union of the projected
        // group sets must equal the keyset, and the sizes must sum — a key in two
        // groups is structurally impossible (Group is one non-nullable field),
        // and this proves the projection does not drop or double-count.
        var union = ActionCatalog.ByGroup.Values.SelectMany(s => s).ToArray();

        union.Should().HaveCount(ActionCatalog.ByKey.Count, "no key may appear in two groups");
        union.Should().BeEquivalentTo(ActionCatalog.ByKey.Keys, "no key may be outside every group");
    }

    [Test]
    public void Every_group_has_at_least_one_member()
    {
        foreach (var group in Enum.GetValues<ActionGroup>())
        {
            ActionCatalog.ByGroup.Should().ContainKey(group);
            ActionCatalog.ByGroup[group].Should().NotBeEmpty(
                $"'{group.ToWire()}' must never rot into a dead label (ACTION.CATALOG.GROUP_EMPTY)");
        }
    }

    [Test]
    public void GroupCount_is_16()
    {
        // 43-3 C1/D2: the epic README and design both NAMED sixteen groups while
        // asserting "15", without saying which to drop. Resolution: ship 16 —
        // merging two semantically distinct groups to satisfy an arithmetic claim
        // is precisely the wrong-but-consistent partition this story exists to
        // avoid. Do not "fix" this downward: group wires become persisted
        // vocabulary the moment 43-5 stores a group-scope assignment.
        ActionCatalog.ByGroup.Should().HaveCount(16);
    }

    [Test]
    public void ByGroup_is_a_projection_of_the_descriptor_fields()
    {
        foreach (var descriptor in ActionCatalog.All)
            ActionCatalog.ByGroup[descriptor.Group].Should().Contain(descriptor.Key);
    }
}
