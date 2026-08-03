using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Story 43-15 (43-11 Amendment 2-E, closes OQ6) — the level-ownership predicate
/// <see cref="AutonomyGateEvaluator.ResolveLadderWithoutActionRow"/>. It mirrors
/// the gate's own ladder minus the principal ACTION row, so the greying/409 rule
/// cannot drift from enforcement. These pin the four composition legs; the
/// BEHAVIOURAL proof (409 on the toggle PUT, group-row bypass close) lives in the
/// endpoint tests, which is why this fixture asserts the resolution, not an HTTP
/// status.
/// </summary>
[TestFixture]
public class AutonomyGateEvaluatorLadderWithoutRowTests
{
    // agent-action:deploy ships at level 90 (> the shipped dial 70), dial-governed,
    // escalatable, in the deploy-control group — the canonical "above the dial" row.
    private static readonly ActionDescriptor Deploy =
        ActionCatalog.ByKey[ActionKey.Parse("agent-action:deploy")];

    // tool:file_write ships at level 25 (< the shipped dial), the "already owned by
    // the level" row.
    private static readonly ActionDescriptor FileWrite =
        ActionCatalog.ByKey[ActionKey.Parse("tool:file_write")];

    private static ResolvedAcceptanceRules BaseRules =>
        new(AcceptanceDefaults.Rules, AcceptanceRulesSource.SystemDefault, 1, "base",
            DateTimeOffset.UtcNow);

    private static GovernancePolicySnapshot Snapshot(
        Dictionary<string, ActionAssignmentValue>? platformActions = null,
        Dictionary<string, ActionAssignmentValue>? platformGroups = null,
        Dictionary<string, ActionAssignmentValue>? principalActions = null,
        Dictionary<string, ActionAssignmentValue>? principalGroups = null)
        => GovernancePolicySnapshot.FromSuccessfulRead(
            platformActions ?? new(StringComparer.Ordinal),
            platformGroups ?? new(StringComparer.Ordinal),
            principalActions ?? new(StringComparer.Ordinal),
            principalGroups ?? new(StringComparer.Ordinal));

    private static ActionAssignmentValue Min => new(AutonomyDial.Min, null, null, null);
    private static ActionAssignmentValue At(int v) => new(v, null, null, null);

    [Test]
    public void WithoutRow_ActionRowIsIgnored_GroupAndShippedRemain()
    {
        // A principal action row at Min (a toggle) must NOT lower the
        // without-the-row resolution — the whole point is "what the ladder says
        // EXCLUDING my own action row". With only an action-Min row present, the
        // resolution is the shipped default (90), not Min.
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                ["agent-action:deploy"] = Min,
            });

        var (min, source) = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
            Deploy, snapshot, BaseRules);

        min.Should().Be(Deploy.DefaultMinAutonomy,
            "the principal action row is dropped, so the shipped level stands");
        min.Should().Be(90);
        source.Should().Be(ActionAssignmentSource.SystemDefault);
    }

    [Test]
    public void WithoutRow_GroupRowStillCounts()
    {
        // A group row at Min covering deploy-control automates the whole group.
        // The without-the-row resolution is the group's Min — this is the
        // group-row bypass, made visible: dial >= Min ⇒ level-owned.
        var snapshot = Snapshot(
            principalGroups: new(StringComparer.Ordinal)
            {
                [Deploy.Group.ToWire()] = Min,
            });

        var (min, source) = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
            Deploy, snapshot, BaseRules);

        min.Should().Be(AutonomyDial.Min, "the group row automates the member");
        source.Should().Be(ActionAssignmentSource.GroupOverride);
    }

    [Test]
    public void WithoutRow_CeilingStillCounts_AndHoldsABelowDialActionShut()
    {
        // file_write ships at 25 (≤ dial), so the shipped level owns it — UNLESS
        // a ceiling raises it. A platform ceiling at AlwaysHuman on file_write
        // makes the without-the-row resolution AlwaysHuman, so at any dial it is
        // NOT level-owned and stays editable (AC3's symmetric clause).
        var snapshot = Snapshot(
            platformActions: new(StringComparer.Ordinal)
            {
                ["tool:file_write"] = At(AutonomyDial.AlwaysHuman),
            });

        var (min, source) = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
            FileWrite, snapshot, BaseRules);

        min.Should().Be(AutonomyDial.AlwaysHuman, "the ceiling raised it by max()");
        source.Should().Be(ActionAssignmentSource.PlatformCeiling);
    }

    [Test]
    public void WithoutRow_CeilingRaisesGroupResolution()
    {
        // A ceiling GROUP row also counts (both platform kinds stay in). Group
        // ceiling on deploy-control at AlwaysHuman raises deploy's resolution.
        var snapshot = Snapshot(
            platformGroups: new(StringComparer.Ordinal)
            {
                [Deploy.Group.ToWire()] = At(AutonomyDial.AlwaysHuman),
            });

        var (min, source) = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
            Deploy, snapshot, BaseRules);

        min.Should().Be(AutonomyDial.AlwaysHuman);
        source.Should().Be(ActionAssignmentSource.PlatformCeiling);
    }

    [Test]
    public void WithoutRow_FloorStillCounts()
    {
        // A legacy always-escalate class on deploy's key raises the resolution to
        // AlwaysHuman via the same internal helper the gate uses.
        var rules = AcceptanceDefaults.Rules with
        {
            AlwaysEscalate = new[]
            {
                new EscalationClass(EscalationClassKind.AgentAction, "deploy"),
            },
        };
        var baseRules = new ResolvedAcceptanceRules(
            rules, AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow);

        var (min, source) = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
            Deploy, Snapshot(), baseRules);

        min.Should().Be(AutonomyDial.AlwaysHuman);
        source.Should().Be(ActionAssignmentSource.AlwaysEscalateLegacy);
    }

    [Test]
    public void WithoutRow_NonAuthoritativeSnapshot_FailsClosed()
    {
        var (min, source) = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
            Deploy, GovernancePolicySnapshot.Unavailable, BaseRules);

        min.Should().Be(AutonomyDial.AlwaysHuman, "ignorance is not absence (F6)");
        source.Should().Be(ActionAssignmentSource.Unavailable);
    }

    [Test]
    public void WithoutRow_NullBaseRules_FailsClosed()
    {
        // A failed base-rules read cannot rule out a legacy floor → fail closed.
        var (min, source) = AutonomyGateEvaluator.ResolveLadderWithoutActionRow(
            Deploy, Snapshot(), baseRules: null);

        min.Should().Be(AutonomyDial.AlwaysHuman);
        source.Should().Be(ActionAssignmentSource.Unavailable);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Story 43-15 — THE ENCODING PROOF (the whole point). A toggle stored at
    // AutonomyDial.Min is a CONSTANT function of the dial: automated at every
    // legal dial position, so lowering the dial below the action's shipped level
    // does NOT change the toggle's effect. The REJECTED encoding (dial-at-mint)
    // fails exactly here — a drop below the mint value silently kills it.
    // ─────────────────────────────────────────────────────────────────────

    private static AutonomyDecision EvaluateDeploy(int toggleValue, int dial)
    {
        var snapshot = Snapshot(
            principalActions: new(StringComparer.Ordinal)
            {
                ["agent-action:deploy"] = new(toggleValue, null, null, null),
            });
        var rules = AcceptanceDefaults.Rules with { AutonomyLevel = dial };
        var baseRules = new ResolvedAcceptanceRules(
            rules, AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow);
        return AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(Deploy.Key, GovernancePrincipal.ForUser(Guid.NewGuid())),
            snapshot, baseRules);
    }

    [Test]
    public void ToggleAtMin_StaysAutomated_AtEveryDialPosition()
    {
        // agent-action:deploy ships at 90. A toggle at Min automates it at 100,
        // 90, 70 AND 60 — lowering the dial below 90 does NOT flip it off.
        foreach (var dial in new[] { 100, 90, 70, 60, AutonomyDial.Min })
        {
            EvaluateDeploy(AutonomyDial.Min, dial).Outcome
                .Should().Be(AutonomyOutcome.Automated,
                    $"a toggle at Min is 'automated, period' — dial {dial} must not kill it");
        }
    }

    [Test]
    public void DialAtMintEncoding_SilentlyKillsOnADrop_WhichIsWhyMinIsUsed()
    {
        // The REJECTED encoding: a row at dial-at-mint (70). Automated while the
        // dial stays ≥ 70, but a drop to 60 SILENTLY makes it require a human —
        // the exact Amendment 2-E failure the Min encoding removes. This test
        // documents WHY the code stores Min, not the mint dial.
        EvaluateDeploy(70, 70).Outcome.Should().Be(AutonomyOutcome.Automated);
        EvaluateDeploy(70, 60).Outcome.Should().NotBe(AutonomyOutcome.Automated,
            "dial-at-mint is an inequality against a moving value — the drop kills it "
            + "silently; storing Min is what prevents this");
    }
}
