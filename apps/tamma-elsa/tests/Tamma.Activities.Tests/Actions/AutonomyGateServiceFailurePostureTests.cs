using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-5 follow-up F6, closed 2026-07-30 — the composed service's FAILURE
/// POSTURE, end to end: an unreadable policy input produces a FAIL-CLOSED
/// decision with <see cref="ActionAssignmentSource.Unavailable"/> provenance and
/// an audit row that is queryably different from a normal one, while a
/// SUCCESSFUL read that found no overrides keeps automating exactly as a
/// zero-config deployment must.
///
/// <para>The pre-fix behaviour these tests forbid: <c>ResolveBaseRulesAsync</c>
/// caught every exception and substituted <c>AcceptanceDefaults.Rules</c>, whose
/// <c>AlwaysEscalate</c> list is EMPTY — so a tenant-DB blip silently deleted the
/// principal's legacy human floor and the gate answered "automated".</para>
/// </summary>
[TestFixture]
public class AutonomyGateServiceFailurePostureTests
{
    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class FixedPrincipal(GovernancePrincipal principal)
        : IGovernancePrincipalResolver
    {
        public Task<GovernancePrincipal> ResolveAsync(
            ClaimsPrincipal? caller = null, CancellationToken ct = default)
            => Task.FromResult(principal);
    }

    private sealed class FixedSnapshots(GovernancePolicySnapshot snapshot)
        : IGovernancePolicySnapshotProvider
    {
        public GovernancePolicySnapshot GetSnapshot(GovernancePrincipal principal) => snapshot;
        public GovernancePolicySnapshot GetSnapshotForAmbient(Guid? tenantId) => snapshot;
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Resolver whose BASE reads either throw (the read failed) or return the
    /// supplied rules (the read succeeded). The distinction this whole fixture
    /// exists to protect.
    /// </summary>
    private sealed class ScriptedRules(bool throwOnBase, AcceptanceRules? rules = null)
        : IAcceptanceRulesResolver
    {
        public Task<ResolvedAcceptanceRules> ResolveAsync(
            Guid? userId, DocumentTypeKey documentType, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ResolvedAcceptanceRules> ResolveForTenantAsync(
            Guid tenantId, DocumentTypeKey documentType, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ResolvedAcceptanceRules> ResolveBaseAsync(
            Guid? userId, CancellationToken ct = default) => Base();

        public Task<ResolvedAcceptanceRules> ResolveBaseForTenantAsync(
            Guid tenantId, CancellationToken ct = default) => Base();

        private Task<ResolvedAcceptanceRules> Base()
        {
            if (throwOnBase)
            {
                throw new Npgsql.NpgsqlException("tenant database unreachable");
            }
            return Task.FromResult(new ResolvedAcceptanceRules(
                rules ?? AcceptanceDefaults.Rules,
                AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow));
        }
    }

    private sealed class FixedBreakGlass(BreakGlassState state) : IGovernanceBreakGlass
    {
        public BreakGlassState Current() => state;
    }

    private sealed class FakeEventRepository : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();

        /// <summary>Event types whose append should blow up (F11's
        /// "the audit row cannot be suppressed" pin).</summary>
        public HashSet<string> FailOnTypes { get; } = new(StringComparer.Ordinal);

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            if (FailOnTypes.Contains(evt.Type))
            {
                throw new InvalidOperationException("event store unavailable");
            }
            Appended.Add(evt);
            return Task.FromResult(evt);
        }

        public Task<DomainEvent?> GetByIdAsync(Guid id) => Task.FromResult<DomainEvent?>(null);
        public Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<DomainEvent>());
        public Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
            => Task.FromResult<DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId) => Task.CompletedTask;
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult<(IReadOnlyList<DomainEvent>, int)>((Array.Empty<DomainEvent>(), 0));
        public Task<(IReadOnlyList<DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult<(IReadOnlyList<DomainEvent>, int)>((Array.Empty<DomainEvent>(), 0));
    }

    private static readonly GovernancePrincipal Principal =
        GovernancePrincipal.ForUser(Guid.NewGuid());

    private static (AutonomyGateService Gate, FakeEventRepository Events) Build(
        bool baseReadThrows,
        GovernancePolicySnapshot? snapshot = null,
        AcceptanceRules? rules = null,
        BreakGlassState? breakGlass = null,
        FakeEventRepository? events = null)
    {
        events ??= new FakeEventRepository();
        var gate = new AutonomyGateService(
            new FixedPrincipal(Principal),
            new FixedSnapshots(snapshot ?? GovernancePolicySnapshot.Empty),
            new ScriptedRules(baseReadThrows, rules),
            new ActionGateEventsService(events),
            breakGlass is null ? null : new FixedBreakGlass(breakGlass));
        return (gate, events);
    }

    private static readonly BreakGlassState EngagedOverride =
        BreakGlassState.Engaged(
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"), "control plane unreachable, INC-4412");

    private static AutonomyQuery Query(string wire) =>
        new(ActionKey.Parse(wire), Principal, Role: "senior_developer", CorrelationId: "wf-1");

    private static string? Tag(DomainEvent evt, string key)
    {
        using var doc = JsonDocument.Parse(evt.Tags!);
        return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
    }

    // ── The pair ────────────────────────────────────────────────────────────

    /// <summary>
    /// Read SUCCEEDED and found no always-escalate entry: automated. This is the
    /// answer the old fail-open code produced for BOTH cases.
    /// </summary>
    [Test]
    public async Task BaseRulesReadSucceedsWithNoOverrides_Automates_AndEmitsNoDegradedTag()
    {
        var (gate, events) = Build(baseReadThrows: false);

        var decision = await gate.EvaluateAsync(Query("agent-action:triage-intake"));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.Source.Should().Be(ActionAssignmentSource.SystemDefault);
        // The .ALLOWED volume gate suppresses pure system-default allows, so the
        // absence of an event is itself the "nothing happened" signal.
        events.Appended.Should().BeEmpty();
    }

    /// <summary>
    /// Read FAILED: the same action, the same rows, the opposite answer. The
    /// legacy always-escalate floor lives in the body we could not read, so it
    /// cannot be ruled out.
    /// </summary>
    [Test]
    public async Task BaseRulesReadFails_FailsClosed_WithUnavailableProvenance()
    {
        var (gate, _) = Build(baseReadThrows: true);

        var decision = await gate.EvaluateAsync(Query("agent-action:triage-intake"));

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
        decision.EffectiveMinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);
        decision.Source.Should().Be(ActionAssignmentSource.Unavailable);
        decision.Enforced.Should().BeTrue();
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonAcceptanceRulesUnavailable);
    }

    /// <summary>
    /// The audit half: a degraded decision is queryable. Before F6 a degraded
    /// evaluation was indistinguishable from a shipped-default one in the event
    /// stream — and, being an ALLOWED/system-default pair, usually emitted
    /// nothing at all.
    /// </summary>
    [Test]
    public async Task ADegradedDecision_EmitsAnEventTaggedDegraded_WithPolicyUnavailableSource()
    {
        var (gate, events) = Build(baseReadThrows: true);

        await gate.EvaluateAsync(Query("agent-action:triage-intake"));

        events.Appended.Should().ContainSingle();
        var evt = events.Appended[0];
        evt.Type.Should().Be(ActionGateEventsService.RequiresHumanType);
        Tag(evt, "degraded").Should().Be("true");
        Tag(evt, "assignmentSource").Should().Be("policy-unavailable",
            "'system-default' and 'we could not read policy' must never share a wire value");
        Tag(evt, "enforced").Should().Be("true");
    }

    /// <summary>The snapshot half of the same posture, through the service.</summary>
    [Test]
    public async Task AnUnavailableSnapshot_FailsClosed_ThroughTheService()
    {
        var (gate, events) = Build(
            baseReadThrows: false, snapshot: GovernancePolicySnapshot.Unavailable);

        var decision = await gate.EvaluateAsync(Query("tool:file_write"));

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
        decision.Source.Should().Be(ActionAssignmentSource.Unavailable);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonPolicySnapshotUnavailable);
        Tag(events.Appended.Single(), "degraded").Should().Be("true");
    }

    /// <summary>
    /// Review 2.1 (2026-07-30) — THE claim this fixture is here to make good on:
    /// "a degraded governance decision is never silent". It was false for
    /// UNCATALOGUED keys, which is the worst place for it to be false: during a
    /// control-plane outage the uncatalogued surface is the one that stays OPEN,
    /// so it is the one an auditor wants a record of. The old short-circuit
    /// returned <c>system-default</c> provenance before the degradation check, and
    /// the <c>.ALLOWED</c> volume gate then suppressed the row entirely
    /// (<c>appended == 0</c>).
    ///
    /// <para>The outcome is deliberately UNCHANGED (epic D2 — still Automated,
    /// still observe-only); only the provenance moved, and with it the audit
    /// row.</para>
    /// </summary>
    [Test]
    public async Task AnUncataloguedKey_UnderDegradation_StaysAutomated_AndStillEmitsADegradedRow()
    {
        var (gate, events) = Build(
            baseReadThrows: false, snapshot: GovernancePolicySnapshot.Unavailable);

        var decision = await gate.EvaluateAsync(Query("tool:not_a_tool"));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated,
            "epic D2 — an unread policy table does not create a catalog entry");
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonUncatalogued);
        decision.Enforced.Should().BeFalse("an uncatalogued allow stays observe-only");
        decision.Source.Should().Be(ActionAssignmentSource.Unavailable);

        var evt = events.Appended.Should().ContainSingle(
            "an allow decided over unreadable policy is exactly the fact an auditor needs; "
            + "the volume gate only suppresses genuine system-default 'nothing happened' rows")
            .Subject;
        evt.Type.Should().Be(ActionGateEventsService.AllowedType);
        Tag(evt, "degraded").Should().Be("true");
        Tag(evt, "assignmentSource").Should().Be("policy-unavailable");
        Tag(evt, "actionKey").Should().Be("tool:not_a_tool");
    }

    /// <summary>The control for the test above: with policy READABLE, the same
    /// uncatalogued key is a genuine "nothing happened" and the volume gate
    /// suppresses it — so the new row is a degradation signal, not new noise on
    /// the healthy path.</summary>
    [Test]
    public async Task AnUncataloguedKey_WithReadablePolicy_EmitsNothing()
    {
        var (gate, events) = Build(baseReadThrows: false);

        var decision = await gate.EvaluateAsync(Query("tool:not_a_tool"));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.Source.Should().Be(ActionAssignmentSource.SystemDefault);
        events.Appended.Should().BeEmpty();
    }

    /// <summary>
    /// A live legacy floor still resolves normally when the read WORKS — the
    /// fail-closed branch must not have swallowed the ordinary path.
    /// </summary>
    [Test]
    public async Task ALiveLegacyFloor_StillResolvesAsAlwaysEscalateLegacy_WhenTheReadSucceeds()
    {
        var rules = AcceptanceDefaults.Rules with
        {
            AlwaysEscalate = new[]
            {
                new EscalationClass(EscalationClassKind.AgentAction, "triage-intake"),
            },
        };
        var (gate, _) = Build(baseReadThrows: false, rules: rules);

        var decision = await gate.EvaluateAsync(Query("agent-action:triage-intake"));

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
        decision.Source.Should().Be(ActionAssignmentSource.AlwaysEscalateLegacy,
            "a READ floor is attributed to the legacy list, never to the degraded branch");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // F11 (closed 2026-07-30) — the BREAK-GLASS override, through the composed
    // service. The fixture above proves the fail-closed posture; this half
    // proves the operator's lever out of it, and — more importantly — proves the
    // lever's boundary.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Engaged + degraded ⇒ the work proceeds, with bypass provenance AND its own
    /// audit row. Before F11 this evaluation denied, with no way for an operator
    /// who had diagnosed the outage and accepted the risk to keep the fleet
    /// working short of editing code.
    /// </summary>
    [Test]
    public async Task BreakGlassEngaged_OverADegradedRead_Proceeds_AndWritesItsOwnAuditRow()
    {
        var (gate, events) = Build(baseReadThrows: true, breakGlass: EngagedOverride);

        var decision = await gate.EvaluateAsync(Query("agent-action:triage-intake"));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.Source.Should().Be(ActionAssignmentSource.BreakGlass);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonBreakGlassBypass);

        var bypass = events.Appended.Should()
            .ContainSingle(e => e.Type == ActionGateEventsService.BreakGlassBypassType).Subject;
        Tag(bypass, "breakGlass").Should().Be("true");
        Tag(bypass, "degraded").Should().Be("true");
        Tag(bypass, "assignmentSource").Should().Be("break-glass",
            "'break-glass' must share a wire value with neither 'policy-unavailable' (the gate "
            + "REFUSED) nor 'system-default' (nothing was wrong)");
        Tag(bypass, "actionKey").Should().Be("agent-action:triage-intake");
        Tag(bypass, "seam").Should().Be("autonomy-gate");
        Tag(bypass, "expiresAtUtc").Should().NotBeNullOrEmpty(
            "an auditor must be able to see the window the operator opened");

        using var data = JsonDocument.Parse(bypass.Data!);
        data.RootElement.GetProperty("reason").GetString()
            .Should().Be("control plane unreachable, INC-4412");
        data.RootElement.GetProperty("degradedReason").GetString()
            .Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// <b>THE anti-backdoor pin at the service level.</b> A policy row that was
    /// READ SUCCESSFULLY denies; the acceptance-rules read then fails; the
    /// override is engaged. The answer must still be the policy's.
    /// </summary>
    [Test]
    public async Task BreakGlassEngaged_AgainstARealPolicyDenial_IsSTILLDenied()
    {
        var snapshot = GovernancePolicySnapshot.FromSuccessfulRead(
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal)
            {
                ["agent-action:deploy"] = new(AutonomyDial.AlwaysHuman, null, null, null),
            },
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal));

        var (gate, events) = Build(
            baseReadThrows: true, snapshot: snapshot, breakGlass: EngagedOverride);

        var decision = await gate.EvaluateAsync(Query("agent-action:deploy"));

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman,
            "break-glass suspends the fail-closed SUBSTITUTION for an unreadable input; it is "
            + "not an off switch for policy that was successfully read");
        decision.EffectiveMinAutonomy.Should().Be(AutonomyDial.AlwaysHuman);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonAlwaysHuman);
        decision.Source.Should().Be(ActionAssignmentSource.ActionOverride,
            "review MEDIUM-1 (2026-07-31) — the ROW denied, so the row is what the decision "
            + "reports; it used to report BreakGlass");

        // Audited as the block it is — and NOT as a bypass.
        var blockRow = events.Appended.Should()
            .ContainSingle(e => e.Type == ActionGateEventsService.RequiresHumanType).Subject;
        Tag(blockRow, "breakGlass").Should().Be("false",
            "a dashboard filtering breakGlass=true must return the decisions the operator's "
            + "lever let through, not every decision taken while it happened to be engaged");
        Tag(blockRow, "assignmentSource").Should().Be("action-override");
        events.Appended.Should().NotContain(
            e => e.Type == ActionGateEventsService.BreakGlassBypassType,
            "MEDIUM-1: the override bypassed nothing here — it was a successfully-read policy "
            + "row that denied — so there is no bypass to record. This assertion used to be its "
            + "own inverse (`Should().Contain`), which is what made the bug look intended");
    }

    /// <summary>
    /// <b>MEDIUM-2, the sharp end.</b> Before the MEDIUM-1 fix, a denial the
    /// override had not touched was stamped <c>BreakGlass</c>, which routed it
    /// through <c>EmitBreakGlassBypassAsync(mustNotSwallow: true)</c> — sequenced
    /// BEFORE the decision row. So an event store that could not take the (bogus)
    /// bypass row made <c>EvaluateAsync</c> THROW, and the <c>ACTION.GATE.DENIED</c>
    /// row was never written either: a disabled action plus an engaged override
    /// plus a blipping event store took out the gate entirely.
    /// </summary>
    [Test]
    public async Task ADenialUnderAnEngagedOverride_SurvivesAFailingBypassAppend()
    {
        var snapshot = GovernancePolicySnapshot.FromSuccessfulRead(
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal),
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal)
            {
                ["agent-action:triage-intake"] = new(null, null, false, null),
            },
            new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal));

        var events = new FakeEventRepository();
        events.FailOnTypes.Add(ActionGateEventsService.BreakGlassBypassType);
        var (gate, _) = Build(
            baseReadThrows: true, snapshot: snapshot,
            breakGlass: EngagedOverride, events: events);

        var decision = await gate.EvaluateAsync(Query("agent-action:triage-intake"));

        decision.Outcome.Should().Be(AutonomyOutcome.Denied);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonDisabled);
        events.Appended.Should().ContainSingle(e => e.Type == ActionGateEventsService.DeniedType,
            "the denial's own audit row is the compliance artefact and must not be collateral "
            + "damage of a bypass row that should never have been attempted");
    }

    /// <summary>
    /// The other half of MEDIUM-2: <c>effect:secret.reveal</c> is
    /// <c>Enforceable = false</c>, so it is Automated whatever policy says and the
    /// override bypasses nothing. It used to carry <c>BreakGlass</c> anyway, so an
    /// unavailable snapshot plus an engaged override plus a failing append threw
    /// on a credential read.
    /// </summary>
    [Test]
    public async Task ANonEnforceableAllow_UnderAnEngagedOverride_IsNotAnAuditableBypass()
    {
        var events = new FakeEventRepository();
        events.FailOnTypes.Add(ActionGateEventsService.BreakGlassBypassType);
        var (gate, _) = Build(
            baseReadThrows: false, snapshot: GovernancePolicySnapshot.Unavailable,
            breakGlass: EngagedOverride, events: events);

        var decision = await gate.EvaluateAsync(Query("effect:secret.reveal"));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonNotEnforceable);
        decision.Source.Should().Be(ActionAssignmentSource.Unavailable);
        events.Appended.Should().NotContain(
            e => e.Type == ActionGateEventsService.BreakGlassBypassType);
    }

    /// <summary>Not engaged ⇒ byte-identical to the F6 posture the rest of this
    /// fixture pins.</summary>
    [Test]
    public async Task BreakGlassNotEngaged_LeavesTheFailClosedPostureUntouched()
    {
        var (gate, _) = Build(baseReadThrows: true, breakGlass: BreakGlassState.NotEngaged);

        var decision = await gate.EvaluateAsync(Query("agent-action:triage-intake"));

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
        decision.Source.Should().Be(ActionAssignmentSource.Unavailable);
    }

    /// <summary>
    /// An engaged override is INERT on a healthy evaluation. It is a fallback for
    /// an unreadable input, not a mode the system runs in — so leaving it
    /// configured after the outage does not quietly change behaviour (the expiry
    /// is the belt; this is the braces).
    /// </summary>
    [Test]
    public async Task BreakGlassEngaged_ChangesNothing_WhenEveryReadSucceeds()
    {
        var (gate, events) = Build(baseReadThrows: false, breakGlass: EngagedOverride);

        var decision = await gate.EvaluateAsync(Query("agent-action:triage-intake"));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.Source.Should().Be(ActionAssignmentSource.SystemDefault);
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonAutomated);
        events.Appended.Should().BeEmpty("nothing degraded, so nothing was bypassed");
    }

    /// <summary>
    /// <b>The audit row cannot be suppressed.</b> The bypass append rides the
    /// NON-swallowing path, so an event store that refuses it fails the
    /// evaluation instead of letting the bypass happen quietly.
    ///
    /// <para>This is a deliberate departure from the F6 close's reasoning that
    /// rethrowing on a failed append for an ALLOW would turn an event-store blip
    /// into a second outage. That reasoning still governs <c>.ALLOWED</c> (see the
    /// control below). Break-glass is the exception because the audit row is not
    /// commentary on the decision — it is the entire justification for having a
    /// bypass: an unrecorded bypass is indistinguishable from an unauthorised
    /// one.</para>
    /// </summary>
    [Test]
    public async Task ABypassThatCannotBeAudited_FailsRatherThanHappeningQuietly()
    {
        var events = new FakeEventRepository();
        events.FailOnTypes.Add(ActionGateEventsService.BreakGlassBypassType);
        var (gate, _) = Build(baseReadThrows: true, breakGlass: EngagedOverride, events: events);

        var act = async () => await gate.EvaluateAsync(Query("agent-action:triage-intake"));

        // Adversarial review F2 (2026-08-01) — the throw is now TYPED. It still
        // propagates (that is this test's property, unchanged) but it carries the
        // decision, so a seam can tell "the gate could not decide" from "the gate
        // decided and we could not record it" instead of reading every rethrown
        // audit failure as a transient fault and failing OPEN. The original
        // exception is the InnerException, asserted here so the wrapper cannot
        // become a way to lose what actually went wrong.
        (await act.Should().ThrowAsync<AutonomyGateDecisionUnrecordedException>())
            .Which.InnerException.Should().BeOfType<InvalidOperationException>();
        events.Appended.Should().BeEmpty();
    }

    /// <summary>The control: an ordinary degraded ALLOW keeps the F6 posture — its
    /// emission failure is swallowed, because that surface is deliberately
    /// staying open and must not acquire a second outage.</summary>
    [Test]
    public async Task AnOrdinaryDegradedAllow_StillSwallowsItsEmissionFailure()
    {
        var events = new FakeEventRepository();
        events.FailOnTypes.Add(ActionGateEventsService.AllowedType);
        var (gate, _) = Build(
            baseReadThrows: false, snapshot: GovernancePolicySnapshot.Unavailable, events: events);

        var decision = await gate.EvaluateAsync(Query("tool:not_a_tool"));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
    }
}
