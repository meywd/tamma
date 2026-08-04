using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Tests.Actions;

/// <summary>
/// Story 43-13 — the caller-kind semantics on the PURE evaluator (D5 for Human,
/// D4's caller half for Machinery, D2 for the fail-closed default). The route
/// seams are covered by <c>CallerKindSeamTests</c> (Tamma.Api.Tests); this file
/// pins the ladder itself, exactly the way <see cref="AutonomyGateEvaluatorTests"/>
/// pins the threshold ladder.
/// </summary>
[TestFixture]
public class CallerKindEvaluatorTests
{
    private static readonly GovernancePrincipal User =
        GovernancePrincipal.ForUser(Guid.NewGuid());

    private static readonly ActionKey FileWrite = ActionKey.Parse("tool:file_write");

    private static ResolvedAcceptanceRules BaseRules(int? dial = null)
    {
        var rules = AcceptanceDefaults.Rules;
        if (dial is int d) rules = rules with { AutonomyLevel = d };
        return new ResolvedAcceptanceRules(
            rules, AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow);
    }

    private static GovernancePolicySnapshot AlwaysHumanRow(
        string wire, bool? enabled = null) =>
        GovernancePolicySnapshot.FromSuccessfulRead(
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal)
            {
                [wire] = new ActionAssignmentValue(
                    AutonomyDial.AlwaysHuman, Enforce: true, Enabled: enabled, AllowedRoles: null),
            },
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal));

    // ────────────────────────────────────────────────────────────────────
    // D5 — the Human short-circuit, BEFORE the policy checks
    // ────────────────────────────────────────────────────────────────────

    [Test]
    public void AHumanCaller_PassesBelowTheThreshold_WithReasonCallerHuman()
    {
        // The hostile row: AlwaysHuman, enforced, at dial Min. An LLM is gated
        // (proven below); a person is not — the dial is a control on the
        // SYSTEM's autonomy (43-11 Amendment 4).
        var decision = AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(FileWrite, User, Caller: CallerKind.Human),
            AlwaysHumanRow("tool:file_write"),
            BaseRules(dial: AutonomyDial.Min));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonCallerHuman);
        decision.Enforced.Should().BeFalse("there is nothing to enforce against a person");
        decision.Group.Should().Be(ActionGroup.CodeWrite,
            "the decision still carries the descriptor's group — the short-circuit "
            + "sits AFTER the catalog lookup");
    }

    [Test]
    public void AHumanCaller_PassesEvenWhenDisabled_AndUnderDegradation()
    {
        // D5's placement pin: the Human return sits BEFORE enabled/roles/
        // degradation — every one of those is a control on autonomous action,
        // and a governance row that can block a person cancelling their own
        // mentorship session is the exact failure this story removes. A human
        // passes even during a control-plane outage.
        var disabled = AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(FileWrite, User, Caller: CallerKind.Human),
            AlwaysHumanRow("tool:file_write", enabled: false),
            BaseRules());
        disabled.Outcome.Should().Be(AutonomyOutcome.Automated);
        disabled.Reason.Should().Be(AutonomyGateEvaluator.ReasonCallerHuman);

        var degraded = AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(FileWrite, User, Caller: CallerKind.Human),
            GovernancePolicySnapshot.Unavailable,
            baseRules: null);
        degraded.Outcome.Should().Be(AutonomyOutcome.Automated);
        degraded.Reason.Should().Be(AutonomyGateEvaluator.ReasonCallerHuman);
    }

    // ────────────────────────────────────────────────────────────────────
    // D2/AC3 — the default IS the acceptance criterion
    // ────────────────────────────────────────────────────────────────────

    [Test]
    public void TheDefaultCaller_IsLlm_AndIsGated()
    {
        // The SAME query as the Human test, minus the Caller argument: the
        // defaulted field must land on the gated path. Flip the default to
        // Human and this goes red — that is AC3's evaluator half.
        var decision = AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(FileWrite, User),
            AlwaysHumanRow("tool:file_write"),
            BaseRules(dial: AutonomyDial.Min));

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman,
            "an undeclared caller is the MODEL until proven otherwise (fail-closed)");
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonAlwaysHuman);
    }

    // ────────────────────────────────────────────────────────────────────
    // D4's caller half — a declared-Machinery caller on a DIAL row
    // ────────────────────────────────────────────────────────────────────

    [Test]
    public void AMachineryCaller_OnADialRow_IsNotDialGated()
    {
        // effect:notify.email.send is a dial row (NOT IsMachinery — the send
        // is the governed decision; the outbox SWEEPER that drains it is the
        // machinery). Seam D's helper declares Machinery in-process, and the
        // dial comparison is skipped for it — while the row stays fully
        // dial-governed for an LLM caller (the DUAL shape).
        var key = ActionKey.Parse("effect:notify.email.send");
        ActionCatalog.Get(key).IsMachinery.Should().BeFalse("the premise of this test");

        var machinery = AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(key, User, Caller: CallerKind.Machinery),
            AlwaysHumanRow("effect:notify.email.send"),
            BaseRules(dial: AutonomyDial.Min));
        machinery.Outcome.Should().Be(AutonomyOutcome.Automated);
        machinery.Reason.Should().Be(AutonomyGateEvaluator.ReasonMachineryNotDialGoverned);

        var llm = AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(key, User, Caller: CallerKind.Llm),
            AlwaysHumanRow("effect:notify.email.send"),
            BaseRules(dial: AutonomyDial.Min));
        llm.Outcome.Should().Be(AutonomyOutcome.RequiresHuman,
            "the same row still gates the model — that is the whole DUAL design");
    }

    [Test]
    public void AMachineryCaller_IsStillDenied_ByEnabledFalse()
    {
        // AC6's residue at the evaluator: the declaration is not a bypass.
        var decision = AutonomyGateEvaluator.Evaluate(
            new AutonomyQuery(FileWrite, User, Caller: CallerKind.Machinery),
            AlwaysHumanRow("tool:file_write", enabled: false),
            BaseRules());

        decision.Outcome.Should().Be(AutonomyOutcome.Denied);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonDisabled);
    }

    [Test]
    public void CallerKind_WireSpellings_AreExact()
    {
        // The audit stream consumes these (D9's `callerKind` tag).
        CallerKind.Human.ToWire().Should().Be("human");
        CallerKind.Machinery.ToWire().Should().Be("machinery");
        CallerKind.Llm.ToWire().Should().Be("llm");
    }
}
