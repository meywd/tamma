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

    private sealed class FakeEventRepository : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
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
        AcceptanceRules? rules = null)
    {
        var events = new FakeEventRepository();
        var gate = new AutonomyGateService(
            new FixedPrincipal(Principal),
            new FixedSnapshots(snapshot ?? GovernancePolicySnapshot.Empty),
            new ScriptedRules(baseReadThrows, rules),
            new ActionGateEventsService(events));
        return (gate, events);
    }

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
}
