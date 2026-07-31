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
/// </list>
///
/// <para>The tests below are deliberately paired: for each input that can
/// degrade, one test proves the bypass works and its neighbour proves a real
/// denial survives it.</para>
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
        decision.Source.Should().Be(ActionAssignmentSource.BreakGlass,
            "the decision was still taken under an engaged override, so it stays auditable "
            + "as one — but the ANSWER is the policy's, not the override's");
    }

    /// <summary>The platform ceiling is the load-bearing tenant protection
    /// (epic OQ4). It is a successfully-read row, so it survives too.</summary>
    [Test]
    public void EngagedButAPlatformCeilingDenies_IsStillDenied()
    {
        var snapshot = Snapshot(platformActions: new(StringComparer.Ordinal)
        {
            ["tool:shell_execute"] = new(AutonomyDial.AlwaysHuman, null, null, null),
        });

        Evaluate("tool:shell_execute", snapshot, baseRules: null, Engaged)
            .Outcome.Should().NotBe(AutonomyOutcome.Automated,
                "a tenant-facing outage lever must never lower a PLATFORM ceiling");
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
    }

    /// <summary>A resolved <c>Enabled = false</c> is checked ABOVE the degradation
    /// branch, deliberately, so it survives the override.</summary>
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
