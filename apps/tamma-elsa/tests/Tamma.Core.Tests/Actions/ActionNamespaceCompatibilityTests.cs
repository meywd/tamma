using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// The superset pin (Story 43-2 AC1): <see cref="ActionNamespace"/> deliberately
/// preserves the two wire strings <see cref="EscalationClassKind"/> already uses,
/// so <c>agent-action:*</c>/<c>document-type:*</c> action keys are a strict
/// superset of a vocabulary already PERSISTED in
/// <c>acceptance_rules_overrides</c>. If this test fails, live rows stop mapping
/// into the catalog and 43-3's AlwaysEscalate absorption becomes a migration.
/// </summary>
[TestFixture]
public class ActionNamespaceCompatibilityTests
{
    [Test]
    public void AgentAction_wire_is_byte_identical_to_EscalationClassKind()
    {
        ActionNamespace.AgentAction.ToWire()
            .Should().Be(EscalationClassKind.AgentAction.ToWire());
    }

    [Test]
    public void DocumentType_wire_is_byte_identical_to_EscalationClassKind()
    {
        ActionNamespace.DocumentType.ToWire()
            .Should().Be(EscalationClassKind.DocumentType.ToWire());
    }

    [Test]
    public void Every_persistable_escalation_class_parses_as_an_action_key()
    {
        // Any EscalationClass that AcceptanceRules.Validate accepts today must be
        // addressable as "kind:key" in the catalog's key space.
        foreach (var kind in Enum.GetValues<EscalationClassKind>())
        {
            ActionKey.TryParse($"{kind.ToWire()}:anything", out var key).Should().BeTrue();
            key.Ns.ToWire().Should().Be(kind.ToWire());
        }
    }
}
