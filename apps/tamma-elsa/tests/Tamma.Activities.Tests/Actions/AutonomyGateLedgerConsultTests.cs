using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-9 <b>AC12's remaining three pieces</b>: the ledger CONSULT inside the
/// gate, the two <c>AutonomyDecision</c> fields it needs, and the one reader
/// <c>Tamma:Governance:AuthorizationTtlHours</c> now has.
///
/// <para>Story 43-5 shipped the ledger itself — <c>TryConsumeAsync</c>,
/// group-covers-member, the CAS transitions and the partial unique index, all
/// pinned by <c>ActionAssignmentStorageTests</c> against a real Postgres. None of
/// that is retested here. What is tested here is the part that did NOT exist: no
/// production code path consulted the ledger at all, <c>AutonomyDecision</c> had
/// nowhere to record the answer, and nothing read the TTL key both the entity and
/// the ledger doc-comments promised.</para>
/// </summary>
[TestFixture]
public class AutonomyGateLedgerConsultTests
{
    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class FixedPrincipal(GovernancePrincipal principal) : IGovernancePrincipalResolver
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

    private sealed class ShippedRules : IAcceptanceRulesResolver
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
        private static Task<ResolvedAcceptanceRules> Base() => Task.FromResult(
            new ResolvedAcceptanceRules(AcceptanceDefaults.Rules,
                AcceptanceRulesSource.SystemDefault, 1, "base", DateTimeOffset.UtcNow));
    }

    private sealed class RecordingEvents : IEventRepository
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

    /// <summary>
    /// A ledger that hands back one scripted grant (or throws). It is deliberately
    /// NOT a re-implementation of the real matching rules — those are 43-5's and
    /// are pinned against Postgres. What is under test here is whether the gate
    /// ASKS, and what it does with the answer.
    /// </summary>
    private sealed class ScriptedLedger(ActionAuthorization? grant, bool throws = false)
        : IActionAuthorizationLedger
    {
        public List<(string CorrelationId, string ActionKey)> Consulted { get; } = new();
        public List<(string TargetKind, string TargetKey, TimeSpan? Ttl)> Requested { get; } = new();

        public Task<ActionAuthorization> RequestAsync(
            Guid? tenantId, Guid? userId, string correlationId, string targetKind, string targetKey,
            string? reason, int? autonomyLevelAtRequest, TimeSpan? ttl = null,
            CancellationToken ct = default)
        {
            Requested.Add((targetKind, targetKey, ttl));
            return Task.FromResult(new ActionAuthorization
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                CorrelationId = correlationId,
                TargetKind = targetKind,
                TargetKey = targetKey,
                State = "pending",
                RequestedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow + (ttl ?? TimeSpan.FromHours(24)),
            });
        }

        public Task<ActionAuthorization?> TryConsumeAsync(
            Guid? tenantId, Guid? userId, string correlationId, string actionKeyWire,
            CancellationToken ct = default)
        {
            Consulted.Add((correlationId, actionKeyWire));
            if (throws) throw new InvalidOperationException("control plane unreachable");
            return Task.FromResult(grant);
        }

        public Task<ActionAuthorization?> DecideAsync(
            Guid id, bool granted, Guid decidedByUserId, string? reason,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ── Fixture helpers ─────────────────────────────────────────────────────

    private static readonly GovernancePrincipal Principal =
        GovernancePrincipal.ForUser(Guid.NewGuid());

    /// <summary>An action whose SHIPPED default pins it to a human, so the gate
    /// produces a real RequiresHuman with no policy rows at all.</summary>
    private static readonly ActionKey HumanPinned =
        new(ActionNamespace.DocumentType, DocumentTypeKey.Design.ToWire());

    private static (AutonomyGateService Gate, RecordingEvents Events) Build(
        IActionAuthorizationLedger? ledger)
    {
        var events = new RecordingEvents();
        var gate = new AutonomyGateService(
            new FixedPrincipal(Principal),
            new FixedSnapshots(GovernancePolicySnapshot.Empty),
            new ShippedRules(),
            new ActionGateEventsService(events),
            breakGlass: null,
            logger: null,
            timeProvider: null,
            ledger: ledger);
        return (gate, events);
    }

    private static ActionAuthorization Grant(string targetKind, string targetKey) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Principal.UserId,
        CorrelationId = "run-1",
        TargetKind = targetKind,
        TargetKey = targetKey,
        State = "granted",
        RequestedAtUtc = DateTime.UtcNow.AddMinutes(-5),
        ConsumedAtUtc = DateTime.UtcNow,
    };

    // ====================================================================
    // The consult exists at all, and the two new fields carry its answer
    // ====================================================================

    [Test]
    public async Task WithoutAGrant_theHumanPinnedActionStillRequiresAHuman()
    {
        // THE ANTI-NO-OP HALF. Without this, "a grant lets it through" is
        // satisfiable by a gate that lets everything through.
        var (gate, _) = Build(new ScriptedLedger(grant: null));

        var decision = await gate.EvaluateAsync(
            new AutonomyQuery(HumanPinned, Principal, CorrelationId: "run-1"));

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
        decision.AuthorizationId.Should().BeNull();
        decision.CoveredBy.Should().BeNull(
            "no grant was consumed, so nothing covered this decision");
    }

    [Test]
    public async Task AnActionScopedGrant_coversItself_andIsRecordedOnTheDecision()
    {
        var grant = Grant("action", HumanPinned.ToWire());
        var (gate, events) = Build(new ScriptedLedger(grant));

        var decision = await gate.EvaluateAsync(
            new AutonomyQuery(HumanPinned, Principal, CorrelationId: "run-1"));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated,
            "one human decision covers the run — that is what the ledger is for");
        decision.Reason.Should().Be(AutonomyGateEvaluator.ReasonCoveredByAuthorization);
        decision.AuthorizationId.Should().Be(grant.Id);
        decision.CoveredBy.Should().Be(HumanPinned.ToWire());

        events.Appended.Select(e => e.Type).Should().Contain(
            ActionGateEventsService.AuthorizedType,
            "a consumed grant must leave an ACTION.GATE.AUTHORIZED row — otherwise an auditor "
            + "sees an allow with no explanation for why the human pin did not bite");
    }

    [Test]
    public async Task SecondSeam_RecordsCoveredBy()
    {
        // AC12's named test. A GROUP-scoped grant covers a MEMBER, and the
        // decision says so — `group:` prefixed, so an auditor can tell at a glance
        // that a group decision (not a decision about this action) let it through.
        var grant = Grant("group", ActionGroup.ReviewAndAcceptance.ToWire());
        var (gate, _) = Build(new ScriptedLedger(grant));

        var decision = await gate.EvaluateAsync(
            new AutonomyQuery(HumanPinned, Principal, CorrelationId: "run-1"));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        decision.CoveredBy.Should().Be($"group:{ActionGroup.ReviewAndAcceptance.ToWire()}",
            "the group case is why CoveredBy is worth carrying at all: without the prefix a "
            + "group grant and an action grant would be indistinguishable in the audit stream");
        decision.AuthorizationId.Should().Be(grant.Id);
    }

    // ====================================================================
    // The four boundaries on when the consult happens
    // ====================================================================

    [Test]
    public async Task WithNoCorrelationId_theLedgerIsNotConsultedAtAll()
    {
        var ledger = new ScriptedLedger(Grant("action", HumanPinned.ToWire()));
        var (gate, _) = Build(ledger);

        var decision = await gate.EvaluateAsync(new AutonomyQuery(HumanPinned, Principal));

        ledger.Consulted.Should().BeEmpty(
            "the ledger is scoped by correlation by construction; without one there is no run "
            + "for a decision to cover, and consulting would burn a grant belonging to some "
            + "other run");
        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
    }

    [Test]
    public async Task AnAutomatedDecision_doesNotBurnAGrant()
    {
        // A shipped-default automatable action. If the consult ran here it would
        // silently consume a single-use human decision for a call that was never
        // going to block.
        var ledger = new ScriptedLedger(Grant("action", "effect:git.branch.create"));
        var (gate, _) = Build(ledger);

        var decision = await gate.EvaluateAsync(new AutonomyQuery(
            new ActionKey(ActionNamespace.Effect, ExternalEffect.GitBranchCreate.ToWire()),
            Principal, CorrelationId: "run-1"));

        decision.Outcome.Should().Be(AutonomyOutcome.Automated);
        ledger.Consulted.Should().BeEmpty();
    }

    [Test]
    public async Task ALedgerFailure_keepsTheBlock()
    {
        // "I could not read the grant table" is NOT "there is a grant". This is
        // the F6 fail-closed posture applied to the one input F6 never covered.
        var (gate, events) = Build(new ScriptedLedger(grant: null, throws: true));

        var decision = await gate.EvaluateAsync(
            new AutonomyQuery(HumanPinned, Principal, CorrelationId: "run-1"));

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman,
            "an unreadable ledger must never be read as a grant");
        decision.AuthorizationId.Should().BeNull();
        events.Appended.Select(e => e.Type).Should().Contain(
            ActionGateEventsService.EvaluationFailedType);
    }

    [Test]
    public async Task NoLedgerRegistered_isNotAnError_andStillBlocks()
    {
        // A host with no control-plane DbContext factory registers no ledger.
        var (gate, _) = Build(ledger: null);

        var decision = await gate.EvaluateAsync(
            new AutonomyQuery(HumanPinned, Principal, CorrelationId: "run-1"));

        decision.Outcome.Should().Be(AutonomyOutcome.RequiresHuman);
    }

    // ====================================================================
    // AC12(c) — the TTL config key finally has a reader
    // ====================================================================

    [Test]
    public void TheTtlConfigKey_isRead_andDefaultsTo24Hours()
    {
        // Before this story `Tamma:Governance:AuthorizationTtlHours` appeared ONLY
        // in two doc-comments — the entity's and the ledger's — and nothing
        // anywhere read it. A documented configuration key with no reader is a lie
        // in the operator's mental model.
        var configured = new ActionAuthorizationRequests(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ActionAuthorizationRequests.TtlConfigKey] = "6",
            }).Build());
        configured.Ttl.Should().Be(TimeSpan.FromHours(6));

        var defaulted = new ActionAuthorizationRequests(
            new ConfigurationBuilder().Build());
        defaulted.Ttl.Should().Be(
            TimeSpan.FromHours(ActionAuthorizationRequests.DefaultTtlHours));

        var nonsense = new ActionAuthorizationRequests(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ActionAuthorizationRequests.TtlConfigKey] = "0",
            }).Build());
        nonsense.Ttl.Should().Be(
            TimeSpan.FromHours(ActionAuthorizationRequests.DefaultTtlHours),
            "a zero TTL would make every grant unconsumable the instant it was minted, which "
            + "reads as 'the decide endpoint is broken' rather than as 'the config is wrong'");
    }

    [Test]
    public async Task TheRequestPath_passesTheResolvedTtl_andAlwaysRequestsTheACTION()
    {
        var ledger = new ScriptedLedger(grant: null);
        var requests = new ActionAuthorizationRequests(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ActionAuthorizationRequests.TtlConfigKey] = "3",
            }).Build(),
            ledger);

        var decision = new AutonomyDecision(
            AutonomyOutcome.RequiresHuman, HumanPinned, ActionGroup.ReviewAndAcceptance,
            ActionRisk.Mutating, 1, 99, ActionAssignmentSource.SystemDefault,
            Enforced: true, Enabled: true, AllowedRoles: null, Reason: "always-human");

        var id = await requests.RequestAsync(Principal, decision, "run-1");

        id.Should().NotBeNull();
        ledger.Requested.Should().ContainSingle();
        ledger.Requested[0].Ttl.Should().Be(TimeSpan.FromHours(3),
            "AC12 says '+24h FROM Tamma:Governance:AuthorizationTtlHours' — the ledger's "
            + "hard-coded default is the fallback, not the implementation");
        ledger.Requested[0].TargetKind.Should().Be("action",
            "a seam must NEVER request a group-scoped grant on a person's behalf: a group grant "
            + "covers every member, so it is strictly more powerful than the block that produced "
            + "it. A human who wants that does it deliberately from the admin surface.");
        ledger.Requested[0].TargetKey.Should().Be(HumanPinned.ToWire());
    }
}
