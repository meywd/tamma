using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Story 43-13 AC4 — <b>the 42 machinery rows never consult the dial.</b> The
/// fixture below transcribes Story 43-11's machinery inventory (5 plumbing-only
/// effects + 29 <c>automation:*</c> + 8 <c>platform-task:*</c>) verbatim, and
/// doubles as the DRIFT PIN: a descriptor moving between the dial and machinery
/// sections without editing this list fails with the key named.
///
/// <para>The dial-1 leg deliberately does NOT wait for 43-11's dial widen:
/// <c>AcceptanceRules.Validate()</c> still rejects <c>&lt; 70</c>, but
/// <c>Validate()</c> is not on the pure-evaluator path, so the fixture
/// constructs <see cref="ResolvedAcceptanceRules"/> with <c>AutonomyLevel = 1</c>
/// directly — correct before and after 43-11 lands.</para>
/// </summary>
[TestFixture]
public class MachineryInventoryTests
{
    /// <summary>
    /// THE 42 — Story 43-11 "Machinery inventory (audited, never dial-gated)",
    /// transcribed 2026-08-03. Do not edit without a 43-11 amendment.
    /// AMENDED 2026-08-07 (Epic 31 P2): + automation:platform-driver-cache-
    /// invalidator (Story 31-2's designed cache-invalidation subscriber, built
    /// in P2) and + automation:github-installation-bridge-backfill (the seam-14
    /// registry-unification startup sweep) — 42 → 44; Epic 31 P3 (2026-08-08)
    /// + automation:ci-completion-poller (DG-5) — 44 → 45. All are background
    /// services with no human in the loop, the exact class this inventory
    /// exists for; neither is a new dial-gated capability.
    /// </summary>
    private static readonly string[] MachineryInventory =
    {
        // ── Effects fired only by plumbing (6) ─────────────────────────────
        "effect:secret.reveal",
        "effect:engine.events.append",
        "effect:engine.platform-events.append",
        "effect:engine.document.persist",
        "effect:engine.document.set-status",
        // Epic 31 P4 M3 (2026-08-08): git.webhook.register RESERVED → LIVE.
        // Its first caller is provisioning plumbing (the server-initiated
        // WebhookRegistrationService at platform connect / single-user
        // startup), so per the catalog row's own 43-12 note it joins the
        // machinery inventory rather than binding an LLM route. The gated
        // decision is the human's connect action; the registration is the
        // deterministic write executing it, audited via GIT.WEBHOOK_REGISTER.*.
        "effect:git.webhook.register",

        // ── Background services — all 32 automation:* ─────────────────────
        "automation:action-catalog-startup-validator",
        "automation:governance-policy-snapshot-priming-service",
        "automation:provider-settings-store-priming-service",
        "automation:agent-seeder",
        "automation:alert-rule-evaluator",
        "automation:audit-chain-checkpoint-scheduler",
        "automation:audit-projector",
        "automation:built-in-alert-rule-seeder",
        "automation:channel-outbox-sweeper",
        "automation:convention-store-seeder",
        "automation:engine-registry-heartbeat-service",
        "automation:entitlement-cache-invalidation-listener",
        "automation:hourly-analytics-rollup-scheduler",
        "automation:notification-dispatcher",
        "automation:platform-task-worker",
        "automation:pool-warmup-service",
        "automation:provider-session-cleanup-service",
        "automation:reveal-token-sweeper",
        "automation:task-queue-processor",
        "automation:tenant-scheduled-trigger-service",
        // 2026-08-18 — the autonomous-loop watchdog + the orphaned-cycle sweeper.
        "automation:adl-loop-watchdog-service",
        "automation:orphaned-cycle-recovery-service",
        "automation:tenant-status-invalidation-listener",
        "automation:workflow-seeder",
        "automation:workflow-sync-service",
        "automation:outbox-slack-sender",
        "automation:outbox-smtp-sender",
        "automation:retire-sweep",
        "automation:secret-auto-rotation-scheduler",
        "automation:tenant-cleanup-requested-trigger",
        "automation:tenant-delete-requested-trigger",
        "automation:platform-driver-cache-invalidator",
        "automation:github-installation-bridge-backfill",
        // Epic 31 P3 (2026-08-08, DG-5): the CI completion poller — a
        // background service with no human in the loop (44 → 45).
        "automation:ci-completion-poller",
        // Epic 31 P4 M3 (2026-08-08): the single-user startup webhook
        // registration pass — background service, machinery by class.
        "automation:webhook-registration-startup",

        // ── Task handlers — all 8 platform-task:* ──────────────────────────
        "platform-task:RETIRE_SECRET_VERSION",
        "platform-task:plan.activate_scheduled",
        "platform-task:billing.webhook.followup",
        "platform-task:billing.customer.create",
        "platform-task:provisioning.tenant",
        "platform-task:provisioning.tenant.v2",
        "platform-task:provisioning.tenant.deprovision",
        "platform-task:tenant.move",
    };

    private static readonly GovernancePrincipal User =
        GovernancePrincipal.ForUser(Guid.NewGuid());

    /// <summary>Rules at an arbitrary dial position, constructed DIRECTLY —
    /// bypassing <c>Validate()</c> is the point (dial 1 is below today's
    /// <see cref="AutonomyDial.Min"/> until 43-11 widens it).</summary>
    private static ResolvedAcceptanceRules RulesAtDial(int dial) => new(
        AcceptanceDefaults.Rules with { AutonomyLevel = dial },
        AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow);

    private static AutonomyDecision Evaluate(
        ActionKey key, int dial, GovernancePolicySnapshot? snapshot = null,
        CallerKind caller = CallerKind.Llm)
        => AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(key, User, Caller: caller),
            snapshot ?? GovernancePolicySnapshot.Empty,
            RulesAtDial(dial));

    /// <summary>The reason a machinery row terminates with. <c>effect:secret.reveal</c>
    /// is the ONE machinery row that is also <c>Enforceable = false</c>, and the
    /// not-enforceable carve-out (epic OQ2 — sited ABOVE the machinery
    /// short-circuit so its stronger under-degradation posture survives) answers
    /// first for it. Identical outcome, different reason string; pinned here so
    /// a reorder is a conscious change.</summary>
    private static string ExpectedTerminalReason(ActionDescriptor d) =>
        d.Enforceable
            ? AutonomyGateEvaluator.ReasonMachineryNotDialGoverned
            : AutonomyGateEvaluator.ReasonNotEnforceable;

    [Test]
    public void TheFixture_isExactlyTheMachineryFlaggedRows()
    {
        var flagged = ActionCatalog.All
            .Where(d => d.IsMachinery)
            .Select(d => d.Key.ToWire())
            .ToHashSet(StringComparer.Ordinal);
        var fixture = MachineryInventory.ToHashSet(StringComparer.Ordinal);

        MachineryInventory.Should().HaveCount(49, "6 + 35 + 8 = 49 (43-11's count check, amended by Epic 31 P2: +2 automation; Epic 31 P3 2026-08-08: +1 automation, the DG-5 CI completion poller; Epic 31 P4 M3 2026-08-08: +1 effect — git.webhook.register live as provisioning machinery per its 43-12 note — and +1 automation, the startup registration pass; 2026-08-18: +2 automation — the autonomous-loop watchdog and the orphaned-cycle sweeper)");
        MachineryInventory.Should().OnlyHaveUniqueItems();

        flagged.Except(fixture).Should().BeEmpty(
            "a row flagged IsMachinery that is not in 43-11's inventory is a "
            + "reclassification, which is a 43-11 amendment — not a code-only change");
        fixture.Except(flagged).Should().BeEmpty(
            "an inventory row without the IsMachinery flag would silently rejoin the dial");
    }

    [Test]
    public void EveryMachineryRow_DecidesIdenticallyAtDial1AndDial100()
    {
        foreach (var wire in MachineryInventory)
        {
            var descriptor = ActionCatalog.Get(ActionKey.Parse(wire));

            var atOne = Evaluate(descriptor.Key, dial: 1);
            var atHundred = Evaluate(descriptor.Key, dial: 100);

            atOne.Outcome.Should().Be(atHundred.Outcome,
                $"'{wire}' must decide identically at both dial extremes — a difference "
                + "means the dial is in the path");
            atOne.Reason.Should().Be(atHundred.Reason, wire);
            atOne.EffectiveMinAutonomy.Should().Be(atHundred.EffectiveMinAutonomy, wire);

            atOne.Outcome.Should().Be(AutonomyOutcome.Automated, wire);
            atOne.Reason.Should().Be(ExpectedTerminalReason(descriptor), wire);
            atOne.Enforced.Should().BeFalse(wire);
        }
    }

    [Test]
    public void AHostileLadder_CannotReachAMachineryRow()
    {
        // An AlwaysHuman ACTION row AND an AlwaysHuman platform ceiling on the
        // target — the most hostile ladder an admin can author. If any of it
        // reached the outcome, dial 1 would block. This is the "unreachable
        // through the dial resolver" proof.
        foreach (var wire in MachineryInventory)
        {
            var descriptor = ActionCatalog.Get(ActionKey.Parse(wire));
            var hostile = new ActionAssignmentValue(
                AutonomyDial.AlwaysHuman, Enforce: true, Enabled: null, AllowedRoles: null);
            var snapshot = GovernancePolicySnapshot.FromSuccessfulRead(
                platformActionRows: new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal)
                {
                    [wire] = hostile,
                },
                platformGroupRows: new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal)
                {
                    [descriptor.Group.ToWire()] = hostile,
                },
                principalActionRows: new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal)
                {
                    [wire] = hostile,
                },
                principalGroupRows: new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal));

            foreach (var dial in new[] { 1, 100 })
            {
                var decision = Evaluate(descriptor.Key, dial, snapshot);
                decision.Outcome.Should().Be(AutonomyOutcome.Automated,
                    $"'{wire}' at dial {dial}: an AlwaysHuman row on a machinery target is INERT "
                    + "(Story 43-13 — the recorded semantic change; enabled=false is the off-switch)");
                decision.Reason.Should().Be(ExpectedTerminalReason(descriptor), wire);
            }
        }
    }

    [Test]
    public void EnabledFalse_StillDeniesAMachineryRow()
    {
        // D4's placement pin, lever one: the short-circuit sits AT the dial
        // comparison, BELOW the enabled check — an admin's disable still bites.
        // (Implemented at the top of Evaluate instead, this goes red.)
        foreach (var wire in new[] { "automation:channel-outbox-sweeper", "effect:engine.events.append" })
        {
            var snapshot = GovernancePolicySnapshot.FromSuccessfulRead(
                platformActionRows: new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
                platformGroupRows: new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
                principalActionRows: new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal)
                {
                    [wire] = new ActionAssignmentValue(null, null, Enabled: false, null),
                },
                principalGroupRows: new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal));

            var decision = Evaluate(ActionKey.Parse(wire), dial: 100, snapshot);

            decision.Outcome.Should().Be(AutonomyOutcome.Denied,
                $"enabled=false is the admin's ONLY off-switch for '{wire}' once thresholds "
                + "are gone (43-11 M3 rule 3: orthogonal to the level)");
            decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonDisabled, wire);
        }
    }

    [Test]
    public void Degradation_StillFailsClosedForMachinery()
    {
        // D4's placement pin, lever two: an unreadable policy table cannot
        // testify that no disable row exists, so machinery still fails closed
        // under F6 — the short-circuit must sit BELOW the degradation branch.
        var automation = Evaluate(
            ActionKey.Parse("automation:channel-outbox-sweeper"), dial: 100,
            GovernancePolicySnapshot.Unavailable);
        automation.Outcome.Should().Be(AutonomyOutcome.Denied,
            "a sweeper cannot wait for a person — deny, not escalate");
        automation.Reason.Should().Be(AutonomyGateEvaluator.ReasonPolicySnapshotUnavailable);

        var plumbing = Evaluate(
            ActionKey.Parse("effect:engine.events.append"), dial: 100,
            GovernancePolicySnapshot.Unavailable);
        plumbing.Outcome.Should().Be(AutonomyOutcome.RequiresHuman,
            "the plumbing effects are escalatable, so fail-closed escalates");
        plumbing.Reason.Should().Be(AutonomyGateEvaluator.ReasonPolicySnapshotUnavailable);
    }
}
