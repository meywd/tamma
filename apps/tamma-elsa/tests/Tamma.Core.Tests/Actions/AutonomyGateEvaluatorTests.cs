using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Agents; // AgentAction (historical namespace, Tamma.Core assembly)
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Story 43-5 AC8/AC9/AC10 — the pure resolution ladder, exercised with no
/// database and no mocks (D6: the ladder is the part most likely to be subtly
/// wrong, so it must be the cheapest to test exhaustively).
/// </summary>
[TestFixture]
public class AutonomyGateEvaluatorTests
{
    private static readonly GovernancePrincipal User =
        GovernancePrincipal.ForUser(Guid.NewGuid());

    private static ResolvedAcceptanceRules BaseRules(
        int? dial = null, IReadOnlyList<EscalationClass>? alwaysEscalate = null,
        int? maxRevisionRounds = null)
    {
        var rules = AcceptanceDefaults.Rules;
        if (dial is int d) rules = rules with { AutonomyLevel = d };
        if (alwaysEscalate is not null) rules = rules with { AlwaysEscalate = alwaysEscalate };
        if (maxRevisionRounds is int m) rules = rules with { MaxRevisionRounds = m };
        return new ResolvedAcceptanceRules(
            rules, AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow);
    }

    private static GovernancePolicySnapshot Snapshot(
        Dictionary<string, ActionAssignmentValue>? platformActions = null,
        Dictionary<string, ActionAssignmentValue>? platformGroups = null,
        Dictionary<string, ActionAssignmentValue>? principalActions = null,
        Dictionary<string, ActionAssignmentValue>? principalGroups = null)
        => new(
            platformActions ?? new(StringComparer.Ordinal),
            platformGroups ?? new(StringComparer.Ordinal),
            principalActions ?? new(StringComparer.Ordinal),
            principalGroups ?? new(StringComparer.Ordinal));

    private static ActionAssignmentValue Value(
        int? min = null, bool? enforce = null, bool? enabled = null, string[]? roles = null)
        => new(min, enforce, enabled, roles);

    private static ActionKey Key(string wire) => ActionKey.Parse(wire);

    private static AutonomyDecision Evaluate(
        ActionKey key, GovernancePolicySnapshot snapshot,
        ResolvedAcceptanceRules? baseRules = null, string? role = null)
        => AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(key, User, role), snapshot, baseRules ?? BaseRules());

    // ─────────────────────────────────────────────────────────────────────
    // AC10 — THE ZERO-ROWS GOLDEN PROOF (behaviour-preserving mandate):
    // with an empty table, EVERY catalog member resolves to its shipped
    // DefaultMinAutonomy with source system-default. Iterates tree-truth
    // (ActionCatalog.All), so a concurrently-growing catalog stays covered.
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void EmptyTable_ResolvesEveryMemberToShippedDefault()
    {
        ActionCatalog.All.Should().NotBeEmpty();

        foreach (var descriptor in ActionCatalog.All)
        {
            var decision = Evaluate(descriptor.Key, GovernancePolicySnapshot.Empty);

            decision.EffectiveMinAutonomy.Should().Be(descriptor.DefaultMinAutonomy,
                $"zero rows must resolve '{descriptor.Key.ToWire()}' to its shipped default " +
                "(the zero-blast property: a fresh deployment behaves exactly as today)");
            decision.Source.Should().Be(ActionAssignmentSource.SystemDefault,
                $"'{descriptor.Key.ToWire()}' has no override, ceiling or legacy floor");
            decision.Enabled.Should().BeTrue();
            decision.AllowedRoles.Should().BeNull();
        }
    }

    [Test]
    public void EmptyTable_AtShippedDial_OutcomeMatchesShippedBehaviour()
    {
        // With the shipped dial (AcceptanceDefaults.DefaultAutonomyLevel ==
        // AutonomyDial.Min) every member automated today stays Automated; the
        // only non-automated members are the shipped AlwaysHuman acceptances.
        var baseRules = BaseRules(dial: AcceptanceDefaults.DefaultAutonomyLevel);

        foreach (var descriptor in ActionCatalog.All)
        {
            var decision = Evaluate(descriptor.Key, GovernancePolicySnapshot.Empty, baseRules);

            if (!descriptor.Enforceable)
            {
                decision.Outcome.Should().Be(AutonomyOutcome.Automated);
                decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonNotEnforceable);
                decision.Enforced.Should().BeFalse(
                    "an informational-only member may never be enforced (epic OQ2)");
            }
            else if (AcceptanceDefaults.DefaultAutonomyLevel >= descriptor.DefaultMinAutonomy)
            {
                decision.Outcome.Should().Be(AutonomyOutcome.Automated,
                    $"'{descriptor.Key.ToWire()}' is automated today and must stay so");
            }
            else
            {
                var expected = descriptor.EscalatableToHuman
                    ? AutonomyOutcome.RequiresHuman
                    : AutonomyOutcome.Denied;
                decision.Outcome.Should().Be(expected,
                    $"'{descriptor.Key.ToWire()}' ships above the dial (AlwaysHuman defaults)");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // The principal ladder (D7: ?? inside)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void ActionRow_BeatsGroupRow_Outright()
    {
        // file_write is in code-write; the action row LOWERS below its group —
        // "override" means override, not max() (the recorded risk).
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                ["tool:file_write"] = Value(min: AutonomyDial.Min),
            },
            principalGroups: new(StringComparer.Ordinal)
            {
                ["code-write"] = Value(min: AutonomyDial.AlwaysHuman),
            });

        var decision = Evaluate(Key("tool:file_write"), snapshot);

        decision.EffectiveMinAutonomy.Should().Be(AutonomyDial.Min);
        decision.Source.Should().Be(ActionAssignmentSource.ActionOverride);
    }

    [Test]
    public void GroupRow_Applies_WhereNoActionRow()
    {
        var snapshot = Snapshot(
            principalGroups: new(StringComparer.Ordinal)
            {
                ["code-write"] = Value(min: 90),
            });

        var decision = Evaluate(Key("tool:file_write"), snapshot);

        decision.EffectiveMinAutonomy.Should().Be(90);
        decision.Source.Should().Be(ActionAssignmentSource.GroupOverride);

        // A sibling in a different group is untouched.
        var other = Evaluate(Key("tool:file_read"), snapshot);
        other.Source.Should().Be(ActionAssignmentSource.SystemDefault);
    }

    // ─────────────────────────────────────────────────────────────────────
    // The platform ceiling (max() outside — the load-bearing protection)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void PlatformCeiling_Raises_ButNeverLowers()
    {
        // Ceiling BELOW the principal's value: principal wins, provenance stays.
        var below = Snapshot(
            platformActions: new(StringComparer.Ordinal)
            {
                ["tool:shell_execute"] = Value(min: 80),
            },
            principalActions: new(StringComparer.Ordinal)
            {
                ["tool:shell_execute"] = Value(min: 95),
            });
        var kept = Evaluate(Key("tool:shell_execute"), below);
        kept.EffectiveMinAutonomy.Should().Be(95);
        kept.Source.Should().Be(ActionAssignmentSource.ActionOverride);

        // Ceiling ABOVE: the ceiling wins with ceiling provenance — a tenant
        // admin cannot lower a platform gate.
        var above = Snapshot(
            platformActions: new(StringComparer.Ordinal)
            {
                ["tool:shell_execute"] = Value(min: AutonomyDial.AlwaysHuman),
            },
            principalActions: new(StringComparer.Ordinal)
            {
                ["tool:shell_execute"] = Value(min: AutonomyDial.Min),
            });
        var raised = Evaluate(Key("tool:shell_execute"), above);
        raised.EffectiveMinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);
        raised.Source.Should().Be(ActionAssignmentSource.PlatformCeiling);
    }

    [Test]
    public void PlatformGroupCeiling_CoversMembers_WithoutActionCeilingRows()
    {
        var snapshot = Snapshot(
            platformGroups: new(StringComparer.Ordinal)
            {
                ["deploy-control"] = Value(min: AutonomyDial.AlwaysHuman),
            },
            principalActions: new(StringComparer.Ordinal)
            {
                ["agent-action:deploy"] = Value(min: AutonomyDial.Min),
            });

        var decision = Evaluate(Key("agent-action:deploy"), snapshot);

        decision.EffectiveMinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);
        decision.Source.Should().Be(ActionAssignmentSource.PlatformCeiling);
    }

    // ─────────────────────────────────────────────────────────────────────
    // AC9 — the legacy always-escalate floor (the TryPreGate bridge, D8)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void LegacyAlwaysEscalate_CannotBeLoweredByAnActionRow()
    {
        var baseRules = BaseRules(alwaysEscalate: new[]
        {
            new EscalationClass(EscalationClassKind.AgentAction, AgentAction.Deploy.ToWire()),
        });
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                ["agent-action:deploy"] = Value(min: AutonomyDial.Min),
            });

        var decision = Evaluate(Key("agent-action:deploy"), snapshot, baseRules);

        decision.EffectiveMinAutonomy.Should().Be(AutonomyDial.AlwaysHuman,
            "a legacy AlwaysEscalate entry is a floor the new surface cannot lower — "
            + "only deleting it in the acceptance-rules UI removes it");
        decision.Source.Should().Be(ActionAssignmentSource.AlwaysEscalateLegacy);
    }

    [Test]
    public void ShippedTriageDefault_StillEscalates_ViaLegacyFloor()
    {
        // triage-intake ships MinAutonomy = Min in the catalog (43-3 D7): the
        // live TriageBindingHelper AlwaysEscalate entry supplies the floor via
        // max() — duplicating it as a catalog default would make deleting the
        // legacy entry fail to lower the threshold.
        ActionCatalog.Get(Key("agent-action:triage-intake")).DefaultMinAutonomy
            .Should().Be(AutonomyDial.Min);

        var baseRules = BaseRules(alwaysEscalate: new[]
        {
            new EscalationClass(EscalationClassKind.AgentAction, AgentAction.TriageIntake.ToWire()),
        });

        var withEntry = Evaluate(
            Key("agent-action:triage-intake"), GovernancePolicySnapshot.Empty, baseRules);
        withEntry.EffectiveMinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);
        withEntry.Source.Should().Be(ActionAssignmentSource.AlwaysEscalateLegacy);

        // Deleting the legacy entry lowers the threshold back to the default.
        var withoutEntry = Evaluate(
            Key("agent-action:triage-intake"), GovernancePolicySnapshot.Empty, BaseRules());
        withoutEntry.EffectiveMinAutonomy.Should().Be(AutonomyDial.Min);
        withoutEntry.Source.Should().Be(ActionAssignmentSource.SystemDefault);
    }

    [Test]
    public void LegacyFloor_ForDocumentTypeClass_AppliesToThatTypeOnly()
    {
        var baseRules = BaseRules(alwaysEscalate: new[]
        {
            new EscalationClass(EscalationClassKind.DocumentType, "plan"),
        });

        Evaluate(Key("document-type:plan"), GovernancePolicySnapshot.Empty, baseRules)
            .Source.Should().Be(ActionAssignmentSource.AlwaysEscalateLegacy);
        Evaluate(Key("document-type:review"), GovernancePolicySnapshot.Empty, baseRules)
            .Source.Should().Be(ActionAssignmentSource.SystemDefault);
        // A document-type class never bleeds onto the agent-action plane.
        Evaluate(Key("agent-action:plan-scope"), GovernancePolicySnapshot.Empty, baseRules)
            .Source.Should().Be(ActionAssignmentSource.SystemDefault);
    }

    [Test]
    public void RoundsExhausted_DoesNotAffectActionThreshold()
    {
        // A rules body whose rounds budget is exhausted for the document
        // lifecycle (MaxRevisionRounds = 1) and which carries an
        // always-escalate class for a DIFFERENT action: TryPreGate's
        // rounds-exhausted short-circuit must not leak into the threshold —
        // the document lifecycle keeps owning rounds.
        var baseRules = BaseRules(
            maxRevisionRounds: 1,
            alwaysEscalate: new[]
            {
                new EscalationClass(EscalationClassKind.AgentAction, AgentAction.Rollback.ToWire()),
            });

        var decision = Evaluate(
            Key("agent-action:deploy"), GovernancePolicySnapshot.Empty, baseRules);

        decision.EffectiveMinAutonomy.Should().Be(
            ActionCatalog.Get(Key("agent-action:deploy")).DefaultMinAutonomy);
        decision.Source.Should().Be(ActionAssignmentSource.SystemDefault);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Per-field independence (AC2/D4 — the bug class)
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void ThresholdOnlyRow_LeavesEnabledInherited()
    {
        // Group disables; a later action row that sets ONLY a threshold must
        // not silently re-enable (a non-nullable enabled DEFAULT TRUE would).
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                ["tool:file_write"] = Value(min: AutonomyDial.Min, enforce: null, enabled: null),
            },
            principalGroups: new(StringComparer.Ordinal)
            {
                ["code-write"] = Value(min: AutonomyDial.Min, enabled: false),
            });

        var decision = Evaluate(Key("tool:file_write"), snapshot);

        decision.Enabled.Should().BeFalse("the action row said NOTHING about enabled");
        decision.Outcome.Should().Be(AutonomyOutcome.Denied);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonDisabled);
    }

    [Test]
    public void EnabledFalseAtGroup_SurvivesAnActionThresholdRow_AndEnforceResolvesIndependently()
    {
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                ["tool:run_tests"] = Value(min: 90),
            },
            principalGroups: new(StringComparer.Ordinal)
            {
                ["ci-and-test"] = Value(min: AutonomyDial.Min, enforce: false, enabled: true),
            });

        var decision = Evaluate(Key("tool:run_tests"), snapshot);

        decision.EffectiveMinAutonomy.Should().Be(90, "the action threshold beats the group's");
        decision.Enforced.Should().BeFalse("enforce inherits from the group row independently");
        decision.Enabled.Should().BeTrue();
    }

    [Test]
    public void PlatformDisable_CannotBeReEnabledByAPrincipalRow()
    {
        var snapshot = Snapshot(
            platformActions: new(StringComparer.Ordinal)
            {
                ["tool:shell_execute"] = Value(min: AutonomyDial.Min, enabled: false),
            },
            principalActions: new(StringComparer.Ordinal)
            {
                ["tool:shell_execute"] = Value(min: AutonomyDial.Min, enabled: true),
            });

        var decision = Evaluate(Key("tool:shell_execute"), snapshot);

        decision.Enabled.Should().BeFalse("enabled composes monotone — either plane's FALSE wins");
        decision.Outcome.Should().Be(AutonomyOutcome.Denied);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Outcomes
    // ─────────────────────────────────────────────────────────────────────

    [Test]
    public void AlwaysHuman_IsNeverAutomatedAtAnyLevelInRange()
    {
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                ["agent-action:deploy"] = Value(min: AutonomyDial.AlwaysHuman),
            });

        foreach (var dial in AutonomyDial.ValidLevels())
        {
            var decision = Evaluate(
                Key("agent-action:deploy"), snapshot, BaseRules(dial: dial));

            decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman,
                $"AlwaysHuman must block at dial {dial}");
            decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonAlwaysHuman);
        }
    }

    [Test]
    public void Outcome_IsDeniedNotRequiresHuman_ForNonEscalatableTargets()
    {
        // Every automation:* member is non-escalatable (Seam D can only deny —
        // a sweeper cannot wait for a person).
        var actor = ActionCatalog.All.First(d => d.Key.Ns == ActionNamespace.Automation);
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                [actor.Key.ToWire()] = Value(min: AutonomyDial.AlwaysHuman),
            });

        var decision = Evaluate(actor.Key, snapshot);

        decision.Outcome.Should().Be(AutonomyOutcome.Denied,
            "there is no human on that path and calling it escalation would be a lie");
    }

    [Test]
    public void NonEnforceable_IsNeverDenied_WhateverTheRowsSay()
    {
        // effect:secret.reveal — informational only (epic OQ2).
        var reveal = ActionCatalog.All.Single(d => !d.Enforceable);
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                [reveal.Key.ToWire()] = Value(min: AutonomyDial.AlwaysHuman, enforce: true),
            });

        var decision = Evaluate(reveal.Key, snapshot);

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.Enforced.Should().BeFalse();
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonNotEnforceable);
    }

    [Test]
    public void UncataloguedKey_IsAllowedAtRuntime_EpicD2()
    {
        var decision = Evaluate(
            new ActionKey(ActionNamespace.Tool, "not_a_tool"), GovernancePolicySnapshot.Empty);

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonUncatalogued);
        decision.Enforced.Should().BeFalse();
    }

    [Test]
    public void RoleRestriction_DeniesOtherRoles_AndAllowsListedOnes()
    {
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                ["tool:git_operations.write"] = Value(
                    min: AutonomyDial.Min, roles: new[] { "developer" }),
            });

        Evaluate(Key("tool:git_operations.write"), snapshot, role: "developer")
            .Outcome.Should().Be(AutonomyOutcome.Automated);
        Evaluate(Key("tool:git_operations.write"), snapshot, role: "tester")
            .Outcome.Should().Be(AutonomyOutcome.Denied);
        Evaluate(Key("tool:git_operations.write"), snapshot, role: null)
            .Outcome.Should().Be(AutonomyOutcome.Denied,
                "an unknown caller role cannot satisfy an explicit allowlist");
    }

    [Test]
    public void BoundaryExact_DialEqualToThreshold_Automates()
    {
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                ["tool:file_write"] = Value(min: 85),
            });

        Evaluate(Key("tool:file_write"), snapshot, BaseRules(dial: 85))
            .Outcome.Should().Be(AutonomyOutcome.Automated, "automated iff dial >= MinAutonomy");
        Evaluate(Key("tool:file_write"), snapshot, BaseRules(dial: 84))
            .Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
    }
}
