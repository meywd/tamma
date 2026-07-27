using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Actions;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// <see cref="ActionKey"/> parse/round-trip contract (Story 43-2 AC2, D6):
/// first-<c>':'</c> ordinal split, fail-loud <c>ACTION.KEY.INVALID</c>, casing
/// ordinal-strict (the <c>EnumWire</c> posture — non-canonical casing rejected,
/// never silently accepted).
/// </summary>
[TestFixture]
public class ActionKeyTests
{
    [Test]
    public void Every_catalogued_member_round_trips_through_the_wire()
    {
        foreach (var key in ActionCatalog.ByKey.Keys)
        {
            var wire = key.ToWire();
            ActionKey.Parse(wire).Should().Be(key, $"'{wire}' must round-trip");
        }
    }

    [Test]
    public void Parse_splits_on_the_first_colon_ordinal()
    {
        // Keys containing '.' but no ':' must survive intact.
        ActionKey.Parse("tool:git_operations.read")
            .Should().Be(new ActionKey(ActionNamespace.Tool, "git_operations.read"));

        // A hypothetical key containing a ':' stays in the key half — first split only.
        ActionKey.Parse("effect:deploy:promote")
            .Should().Be(new ActionKey(ActionNamespace.Effect, "deploy:promote"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("no-colon")]
    [TestCase(":missing-namespace")]
    [TestCase("agent-action:")]
    [TestCase("not-a-namespace:deploy")]
    public void Parse_rejects_malformed_wires_with_ACTION_KEY_INVALID(string? wire)
    {
        var act = () => ActionKey.Parse(wire!);

        act.Should().Throw<TammaError>()
            .Which.Code.Should().Be("ACTION.KEY.INVALID");
    }

    [TestCase("Agent-Action:deploy")]
    [TestCase("AGENT-ACTION:deploy")]
    public void Parse_rejects_non_canonical_namespace_casing(string wire)
    {
        // Ordinal-strict, matching EnumWire: 'Agent-Action:deploy' is rejected.
        var act = () => ActionKey.Parse(wire);

        act.Should().Throw<TammaError>()
            .Which.Code.Should().Be("ACTION.KEY.INVALID");
    }

    [Test]
    public void Non_canonical_key_casing_is_not_a_catalogued_member()
    {
        // The namespace parses, but the catalog's ordinal keyset rejects the key half.
        var parsed = ActionKey.Parse("agent-action:Deploy");

        ActionCatalog.TryGet(parsed, out _).Should().BeFalse(
            "key halves are ordinal case-sensitive; 'Deploy' is not the 'deploy' wire");
    }

    [Test]
    public void TryParse_mirrors_Parse_without_throwing()
    {
        ActionKey.TryParse("agent-action:deploy", out var key).Should().BeTrue();
        key.Should().Be(new ActionKey(ActionNamespace.AgentAction, "deploy"));

        ActionKey.TryParse("garbage", out _).Should().BeFalse();
        ActionKey.TryParse(null, out _).Should().BeFalse();
    }

    [Test]
    public void Parse_error_carries_the_offending_wire_in_context()
    {
        var act = () => ActionKey.Parse("bogus");

        act.Should().Throw<TammaError>()
            .Which.Context.Should().ContainKey("wire").WhoseValue.Should().Be("bogus");
    }
}
