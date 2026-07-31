using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Story 43-5 follow-up <b>F11</b>, closed 2026-07-30 — the BREAK-GLASS override
/// for the fail-closed posture, at the pure evaluator.
///
/// <para>The whole fixture exists to pin ONE boundary, because getting it wrong
/// turns an outage lever into a backdoor:</para>
///
/// <list type="bullet">
/// <item><b>Break-glass bypasses DEGRADATION.</b> "I could not read policy, so I
/// refuse" becomes "I could not read policy, so I proceed on what I do know."</item>
/// <item><b>Break-glass never bypasses POLICY.</b> Anything a SUCCESSFUL read
/// produced — a threshold row, a disable, a role restriction — and anything the
/// shipped catalog itself says, still applies in full.</item>
/// <item><b>Break-glass PROVENANCE is as narrow as the bypass</b> (review
/// MEDIUM-1, 2026-07-31). <see cref="ActionAssignmentSource.BreakGlass"/> appears
/// on a decision the override PERMITTED, and on no other. It is not a mood the
/// evaluation was in; it is the answer to "did the operator's lever decide
/// this?", and the <c>breakGlass</c> audit tag plus the dedicated
/// <c>BREAK_GLASS_BYPASS</c> row are gated on it.</item>
/// </list>
///
/// <para>The tests below are deliberately paired: for each input that can
/// degrade, one test proves the bypass works and its neighbour proves a real
/// denial survives it — and, since MEDIUM-1, proves the denial is attributed to
/// the thing that denied rather than to the override.</para>
/// </summary>
[TestFixture]
public class AutonomyGateEvaluatorBreakGlassTests
{
    private static readonly GovernancePrincipal User =
        GovernancePrincipal.ForUser(Guid.NewGuid());

    private static readonly BreakGlassState Engaged =
        BreakGlassState.Engaged(DateTimeOffset.UtcNow.AddHours(2), "control plane unreachable");

    private static ResolvedAcceptanceRules BaseRules(
        int? dial = null, IReadOnlyList<EscalationClass>? alwaysEscalate = null)
    {
        var rules = AcceptanceDefaults.Rules;
        if (dial is int d) rules = rules with { AutonomyLevel = d };
        if (alwaysEscalate is not null) rules = rules with { AlwaysEscalate = alwaysEscalate };
        return new ResolvedAcceptanceRules(
            rules, AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow);
    }

    private static GovernancePolicySnapshot Snapshot(
        Dictionary<string, ActionAssignmentValue>? platformActions = null,
        Dictionary<string, ActionAssignmentValue>? principalActions = null,
        Dictionary<string, ActionAssignmentValue>? principalGroups = null)
        => GovernancePolicySnapshot.FromSuccessfulRead(
            platformActions ?? new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
            principalActions ?? new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
            principalGroups ?? new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal));

    private static ActionKey Key(string wire) => ActionKey.Parse(wire);

    private static AutonomyDecision Evaluate(
        string wire,
        GovernancePolicySnapshot snapshot,
        ResolvedAcceptanceRules? baseRules,
        BreakGlassState? breakGlass,
        string? role = null)
        => AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(Key(wire), User, role), snapshot, baseRules, breakGlass);

    // ── 1. Engaged + degraded ⇒ PROCEEDS, with its own provenance ────────────

    /// <summary>
    /// The snapshot half: the assignment table has never loaded. Without the
    /// override this is the F6 fail-closed denial that made every agent loop
    /// inert during a control-plane outage; with it, the evaluation falls back to
    /// the shipped default — which is all that is knowable — and says so.
    /// </summary>
    [Test]
    public void EngagedAndSnapshotUnavailable_Proceeds_WithBreakGlassProvenance()
    {
        var without = Evaluate(
            "tool:file_write", GovernancePolicySnapshot.Unavailable, BaseRules(), null);
        var with = Evaluate(
            "tool:file_write", GovernancePolicySnapshot.Unavailable, BaseRules(), Engaged);

        without.Outcome.Should().Be(AutonomyOutcome.RequiresHuman,
            "the unmodified F6 posture — this is what the override exists to relieve");
        without.Source.Should().Be(ActionAssignmentSource.Unavailable);

        with.Outcome.Should().Be(AutonomyOutcome.Automated);
        with.EffectiveMinAutonomy.Should().Be(
            ActionCatalog.Get(Key("tool:file_write")).DefaultMinAutonomy);
        with.Reason.Should().Be(AutonomyGateEvaluator.ReasonBreakGlassBypass);
        with.Source.Should().Be(ActionAssignmentSource.BreakGlass,
            "a bypassed decision must be distinguishable in provenance from BOTH a healthy "
            + "one (system-default) and a degraded-denied one (policy-unavailable)");
    }

    /// <summary>The acceptance-rules half: the principal's base rules could not be
    /// read, so the legacy always-escalate floor cannot be ruled out.</summary>
    [Test]
    public void EngagedAndBaseRulesUnreadable_Proceeds_WithBreakGlassProvenance()
    {
        var without = Evaluate(
            "agent-action:triage-intake", GovernancePolicySnapshot.Empty, null, null);
        var with = Evaluate(
            "agent-action:triage-intake", GovernancePolicySnapshot.Empty, null, Engaged);

        without.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
        without.Reason.Should().Be(AutonomyGateEvaluator.ReasonAcceptanceRulesUnavailable);

        with.Outcome.Should().Be(AutonomyOutcome.Automated);
        with.Reason.Should().Be(AutonomyGateEvaluator.ReasonBreakGlassBypass);
        with.Source.Should().Be(ActionAssignmentSource.BreakGlass);
    }

    // ── 2. THE ANTI-BACKDOOR PINS — a real denial survives the override ──────

    /// <summary>
    /// THE test. A policy row that was READ SUCCESSFULLY says this action needs a
    /// person; the acceptance-rules read then failed. Break-glass relieves the
    /// SECOND fact and must not touch the first. If this ever goes green with
    /// <c>Automated</c>, the override has become an off switch for policy.
    /// </summary>
    [Test]
    public void EngagedButARealPolicyRowDenies_IsStillDenied()
    {
        var snapshot = Snapshot(principalActions: new(StringComparer.Ordinal)
        {
            ["agent-action:deploy"] = new(AutonomyDial.AlwaysHuman, null, null, null),
        });

        var decision = Evaluate("agent-action:deploy", snapshot, baseRules: null, Engaged);

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman,
            "break-glass suspends the fail-closed SUBSTITUTION, never a policy row that was "
            + "successfully read — that is the difference between an outage lever and a backdoor");
        decision.EffectiveMinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonAlwaysHuman,
            "the reason must name the POLICY, not the bypass");
        decision.Source.Should().Be(ActionAssignmentSource.ActionOverride,
            "review MEDIUM-1 (2026-07-31): this used to assert BreakGlass, which was the bug "
            + "written down as an expectation. The override did not decide this — a principal "
            + "action row did — so the provenance names that row. Stamping BreakGlass here made "
            + "AutonomyGateService emit a spurious ACTION.GATE.BREAK_GLASS_BYPASS row, tagged the "
            + "DENIED row breakGlass=true, logged 'the gate did NOT fail closed ... because the "
            + "break-glass override is engaged' for a denial the override had nothing to do with, "
            + "and destroyed the real provenance in the process");
    }

    /// <summary>The platform ceiling is the load-bearing tenant protection
    /// (epic OQ4). It is a successfully-read row, so it survives too — and, since
    /// MEDIUM-1, it is still ATTRIBUTED to the ceiling instead of being relabelled
    /// as a bypass.</summary>
    [Test]
    public void EngagedButAPlatformCeilingDenies_IsStillDenied()
    {
        var snapshot = Snapshot(platformActions: new(StringComparer.Ordinal)
        {
            ["tool:shell_execute"] = new(AutonomyDial.AlwaysHuman, null, null, null),
        });

        var decision = Evaluate("tool:shell_execute", snapshot, baseRules: null, Engaged);

        decision.Outcome.Should().NotBe(AutonomyOutcome.Automated,
            "a tenant-facing outage lever must never lower a PLATFORM ceiling");
        decision.Source.Should().Be(ActionAssignmentSource.PlatformCeiling,
            "the ceiling is what blocked, so the ceiling is what the audit row must name — "
            + "the pre-MEDIUM-1 blanket BreakGlass stamp lost this entirely");
    }

    /// <summary>
    /// The snapshot-degraded branch falls back to the SHIPPED DEFAULT, not to
    /// "allow". A member whose shipped default is AlwaysHuman therefore still
    /// blocks with the override engaged — which is the whole reason
    /// <c>effect:mcp.tool.invoke</c> was moved to AlwaysHuman rather than being
    /// special-cased in a gate.
    /// </summary>
    [TestCase("effect:mcp.tool.invoke")]
    [TestCase("document-type:design")]
    [TestCase("document-type:threat-model")]
    public void EngagedAndSnapshotUnavailable_AnAlwaysHumanShippedDefault_StillBlocks(string wire)
    {
        var decision = Evaluate(wire, GovernancePolicySnapshot.Unavailable, BaseRules(), Engaged);

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
        decision.EffectiveMinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonAlwaysHuman);
        decision.Source.Should().Be(ActionAssignmentSource.Unavailable,
            "MEDIUM-1: the override did not permit this — it blocked. The honest provenance is "
            + "the DEGRADED one (which is also what keeps the `degraded` audit tag true); "
            + "BreakGlass would claim a bypass that did not happen");
    }

    /// <summary>A resolved <c>Enabled = false</c> is checked ABOVE the degradation
    /// branch, deliberately, so it survives the override — and names the row.</summary>
    [Test]
    public void EngagedButTheActionIsDisabled_IsStillDenied()
    {
        var snapshot = Snapshot(principalActions: new(StringComparer.Ordinal)
        {
            ["tool:shell_execute"] = new(null, null, false, null),
        });

        var decision = Evaluate("tool:shell_execute", snapshot, baseRules: null, Engaged);

        decision.Outcome.Should().Be(AutonomyOutcome.Denied);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonDisabled);
        decision.Source.Should().Be(ActionAssignmentSource.ActionOverride,
            "MEDIUM-1 — a disabled row reports the DISABLED ROW; it used to inherit the "
            + "break-glass stamp and be filed as a bypass");
    }

    /// <summary>A platform disable is reported as the ceiling: it is the half a
    /// tenant admin cannot undo, so it is the half the audit row must name.</summary>
    [Test]
    public void EngagedButThePlatformDisabled_ReportsThePlatformCeiling()
    {
        var snapshot = Snapshot(platformActions: new(StringComparer.Ordinal)
        {
            ["tool:shell_execute"] = new(null, null, false, null),
        });

        var decision = Evaluate("tool:shell_execute", snapshot, baseRules: null, Engaged);

        decision.Outcome.Should().Be(AutonomyOutcome.Denied);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonDisabled);
        decision.Source.Should().Be(ActionAssignmentSource.PlatformCeiling);
    }

    /// <summary>A resolved role restriction likewise.</summary>
    [Test]
    public void EngagedButTheRoleIsNotAllowed_IsStillDenied()
    {
        var snapshot = Snapshot(principalActions: new(StringComparer.Ordinal)
        {
            ["tool:file_write"] = new(null, null, null, new[] { "architect" }),
        });

        var decision = Evaluate(
            "tool:file_write", snapshot, baseRules: null, Engaged, role: "qa_engineer");

        decision.Outcome.Should().Be(AutonomyOutcome.Denied);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonRoleNotAllowed);
        decision.Source.Should().Be(ActionAssignmentSource.ActionOverride,
            "MEDIUM-1 — the ROLE RULE decided this, not the override");
    }

    /// <summary>
    /// The non-enforceable carve-out (epic OQ2) under an engaged override.
    /// <c>effect:secret.reveal</c> is Automated whatever the rows say, degraded or
    /// not, so the override bypassed NOTHING — exactly the reasoning already
    /// written down for the uncatalogued carve-out, which had simply never been
    /// applied here (review MEDIUM-2, 2026-07-31). The stamp mattered: it drove
    /// the NON-swallowing bypass append, so a failing event store turned a
    /// credential read that nothing had bypassed into a thrown evaluation.
    /// </summary>
    [Test]
    public void EngagedAndDegraded_ANonEnforceableMember_IsNotStampedAsABypass()
    {
        var decision = Evaluate(
            "effect:secret.reveal", GovernancePolicySnapshot.Unavailable, BaseRules(), Engaged);

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonNotEnforceable);
        decision.Enforced.Should().BeFalse();
        decision.Source.Should().Be(ActionAssignmentSource.Unavailable,
            "the DEGRADED provenance is kept — matching the uncatalogued carve-out, an allow "
            + "decided over an unreadable input is exactly what an auditor needs a row for — "
            + "but this allow was never going to be blocked, so it is not a bypass");
    }

    /// <summary>
    /// The uncatalogued carve-out's existing exemption, restated as a pin next to
    /// its twin so the two cannot drift apart again.
    /// </summary>
    [Test]
    public void EngagedAndDegraded_AnUncataloguedKey_IsNotStampedAsABypass()
    {
        var decision = Evaluate(
            "tool:not_a_tool", GovernancePolicySnapshot.Unavailable, BaseRules(), Engaged);

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonUncatalogued);
        decision.Source.Should().Be(ActionAssignmentSource.Unavailable);
    }

    // ── 2b. THE INVARIANT, in both directions ───────────────────────────────

    /// <summary>
    /// <b>Direction one (MEDIUM-1): no decision the override did not permit ever
    /// carries break-glass provenance.</b> Swept over every catalog member and
    /// every shape of degradation, with rows that deny in each of the ways the
    /// evaluator can deny. If this goes red, some guard has started inheriting the
    /// stamp again and the audit stream has re-acquired phantom bypass rows.
    /// </summary>
    [Test]
    public void BreakGlassProvenance_NeverAppearsOnADecisionTheOverrideDidNotPermit()
    {
        var denyingRows = new (string Label, Func<string, ActionAssignmentValue> Row)[]
        {
            ("disabled", _ => new(null, null, false, null)),
            ("role-restricted", _ => new(null, null, null, new[] { "nobody" })),
            ("always-human threshold", _ => new(AutonomyDial.AlwaysHuman, null, null, null)),
        };

        foreach (var descriptor in ActionCatalog.All)
        {
            var wire = descriptor.Key.ToWire();
            foreach (var (label, row) in denyingRows)
            {
                foreach (var onPlatform in new[] { false, true })
                {
                    var rows = new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal)
                    {
                        [wire] = row(wire),
                    };
                    var snapshot = onPlatform
                        ? Snapshot(platformActions: rows)
                        : Snapshot(principalActions: rows);

                    // baseRules null ⇒ the acceptance-rules half is degraded, so
                    // the override IS in play; the assignment rows were read.
                    var decision = Evaluate(wire, snapshot, baseRules: null, Engaged, role: "developer");

                    if (decision.Outcome != AutonomyOutcome.Automated)
                    {
                        decision.Source.Should().NotBe(ActionAssignmentSource.BreakGlass,
                            $"'{wire}' denied by a successfully-read {label} row "
                            + $"({(onPlatform ? "platform" : "principal")} plane) is not a bypass");
                    }
                }
            }

            // The snapshot half: nothing readable at all, so the fallback is the
            // SHIPPED default. Where that default blocks, it is still not a bypass.
            var degraded = Evaluate(
                wire, GovernancePolicySnapshot.Unavailable, BaseRules(), Engaged, role: "developer");
            if (degraded.Outcome != AutonomyOutcome.Automated)
            {
                degraded.Source.Should().NotBe(ActionAssignmentSource.BreakGlass,
                    $"'{wire}' blocked by its shipped default is not something the override let through");
            }
        }
    }

    /// <summary>
    /// <b>Direction two: the stamp is still THERE when the override genuinely
    /// permitted something.</b> The narrowing in MEDIUM-1 must not have quietly
    /// deleted the provenance the audit row depends on — a bypass with no
    /// <c>BreakGlass</c> stamp is an unrecorded bypass, which is the failure the
    /// whole F11 audit path exists to prevent. Exactly one stamped decision per
    /// permitted call, for both degradation halves.
    /// </summary>
    [Test]
    public void BreakGlassProvenance_IsStillStamped_WhenTheOverrideGenuinelyPermitted()
    {
        // Snapshot half: nothing readable; the shipped default for a tool is Min,
        // and the shipped dial is Min, so the override turns the F6 block into an
        // allow. That allow is the bypass.
        var snapshotHalf = Evaluate(
            "tool:file_write", GovernancePolicySnapshot.Unavailable, BaseRules(), Engaged);
        snapshotHalf.Outcome.Should().Be(AutonomyOutcome.Automated);
        snapshotHalf.Source.Should().Be(ActionAssignmentSource.BreakGlass);
        snapshotHalf.Reason.Should().Be(AutonomyGateEvaluator.ReasonBreakGlassBypass);

        // Acceptance-rules half: the rows were read and permit; only the legacy
        // floor was unreadable, and skipping it is what let this through.
        var rulesHalf = Evaluate(
            "agent-action:triage-intake", GovernancePolicySnapshot.Empty, null, Engaged);
        rulesHalf.Outcome.Should().Be(AutonomyOutcome.Automated);
        rulesHalf.Source.Should().Be(ActionAssignmentSource.BreakGlass);
        rulesHalf.Reason.Should().Be(AutonomyGateEvaluator.ReasonBreakGlassBypass);

        // ...and a READ row that permits at the same time is still a bypass: the
        // unreadable floor is what was skipped, whatever else was readable.
        var withAReadRow = Evaluate(
            "agent-action:triage-intake",
            Snapshot(principalActions: new(StringComparer.Ordinal)
            {
                ["agent-action:triage-intake"] = new(AutonomyDial.Min, null, null, null),
            }),
            baseRules: null, Engaged);
        withAReadRow.Outcome.Should().Be(AutonomyOutcome.Automated);
        withAReadRow.Source.Should().Be(ActionAssignmentSource.BreakGlass);
    }

    /// <summary>
    /// A legacy always-escalate entry that WAS read still floors the action while
    /// the override is engaged (the snapshot is the degraded input here, not the
    /// rules). Deleting it in the acceptance-rules UI stays the only way to lower
    /// it — break-glass is not a second way.
    /// </summary>
    [Test]
    public void EngagedButALegacyAlwaysEscalateEntryWasRead_StillFloors()
    {
        var rules = BaseRules(alwaysEscalate: new[]
        {
            new EscalationClass(EscalationClassKind.AgentAction, "triage-intake"),
        });

        var decision = Evaluate(
            "agent-action:triage-intake", GovernancePolicySnapshot.Unavailable, rules, Engaged);

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
        decision.Source.Should().Be(ActionAssignmentSource.AlwaysEscalateLegacy,
            "a floor that was actually READ is attributed to the legacy list, and the more "
            + "specific provenance is the honest one");
    }

    // ── 3. Not engaged / expired ⇒ the F6 posture is untouched ───────────────

    [Test]
    public void NotEngaged_AndDegraded_StillDeniesExactlyAsBefore()
    {
        foreach (var breakGlass in new[] { null, BreakGlassState.NotEngaged })
        {
            var decision = Evaluate(
                "tool:file_write", GovernancePolicySnapshot.Unavailable, BaseRules(), breakGlass);

            decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
            decision.Source.Should().Be(ActionAssignmentSource.Unavailable);
            decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonPolicySnapshotUnavailable);
            decision.Enforced.Should().BeTrue();
        }
    }

    /// <summary>
    /// An engaged override changes NOTHING on a healthy evaluation — it is not a
    /// mode the system runs in, it is a fallback that only exists where a read
    /// failed. Iterated over the whole catalog so a future member cannot quietly
    /// acquire a break-glass-only behaviour.
    /// </summary>
    [Test]
    public void Engaged_ChangesNothing_WhenEveryInputIsReadable()
    {
        foreach (var descriptor in ActionCatalog.All)
        {
            var query = new AutonomyQuery(descriptor.Key, User);
            var healthy = AutonomyGateEvaluator.Evaluate(
                query, GovernancePolicySnapshot.Empty, BaseRules(), null);
            var engaged = AutonomyGateEvaluator.Evaluate(
                query, GovernancePolicySnapshot.Empty, BaseRules(), Engaged);

            engaged.Should().Be(healthy,
                $"'{descriptor.Key.ToWire()}' must resolve identically with and without an "
                + "engaged override while the control plane is healthy");
        }
    }

    // ── 4. The Seam B entry point honours the same rules ─────────────────────

    [Test]
    public void ResolveEffectiveMinAutonomy_Engaged_FallsBackToTheShippedDefault()
    {
        var descriptor = ActionCatalog.Get(Key("tool:file_read"));

        AutonomyGateEvaluator.ResolveEffectiveMinAutonomy(
                descriptor, GovernancePolicySnapshot.Unavailable, Engaged)
            .Should().Be((descriptor.DefaultMinAutonomy, ActionAssignmentSource.BreakGlass));

        AutonomyGateEvaluator.ResolveEffectiveMinAutonomy(
                descriptor, GovernancePolicySnapshot.Unavailable, BreakGlassState.NotEngaged)
            .Should().Be((AutonomyDial.AlwaysHuman, ActionAssignmentSource.Unavailable));
    }

    /// <summary>
    /// The bypass is sited INSIDE the non-authoritative branch, so it can never
    /// discard a row: an authoritative snapshot resolves identically whether or
    /// not the override is engaged.
    /// </summary>
    [Test]
    public void ResolveEffectiveMinAutonomy_Engaged_NeverDiscardsAReadRow()
    {
        var descriptor = ActionCatalog.Get(Key("tool:shell_execute"));
        var snapshot = Snapshot(principalActions: new(StringComparer.Ordinal)
        {
            ["tool:shell_execute"] = new(AutonomyDial.AlwaysHuman, null, null, null),
        });

        AutonomyGateEvaluator.ResolveEffectiveMinAutonomy(descriptor, snapshot, Engaged)
            .Should().Be(
                AutonomyGateEvaluator.ResolveEffectiveMinAutonomy(descriptor, snapshot, null));
    }
}
