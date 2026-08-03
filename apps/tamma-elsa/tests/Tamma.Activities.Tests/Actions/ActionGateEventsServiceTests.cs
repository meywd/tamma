using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-5 (AC13/D11) — the ACTION.GATE.* event family: exact type
/// strings, the decision tag set, the selective non-swallow (an ENFORCED
/// denial with no audit row is a compliance hole — those rethrow; everything
/// else is best-effort), and the .ALLOWED volume gate.
/// </summary>
[TestFixture]
public class ActionGateEventsServiceTests
{
    private sealed class FakeEventRepository : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();
        public bool Throw;

        public Task<DomainEvent> AppendAsync(DomainEvent evt)
        {
            if (Throw) throw new InvalidOperationException("event store down");
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

    private static readonly ActionKey Deploy = ActionKey.Parse("agent-action:deploy");

    private static AutonomyDecision Decision(
        AutonomyOutcome outcome,
        ActionAssignmentSource source = ActionAssignmentSource.ActionOverride,
        bool enforced = true)
        => new(outcome, Deploy, ActionGroup.DeployControl, ActionRisk.Destructive,
            AutonomyDial.Min, AutonomyDial.AlwaysHuman, source, enforced,
            Enabled: true, AllowedRoles: null, Reason: "always-human");

    private static AutonomyQuery Query(Guid? tenantId = null) => new(
        Deploy,
        tenantId is Guid t ? GovernancePrincipal.ForTenant(t) : GovernancePrincipal.ForUser(Guid.NewGuid()),
        Role: "devops", Operation: "promote", Target: "prod", CorrelationId: "wf-1");

    [Test]
    public void TheEightTypeStrings_AreExact()
    {
        ActionGateEventsService.AllowedType.Should().Be("ACTION.GATE.ALLOWED");
        ActionGateEventsService.RequiresHumanType.Should().Be("ACTION.GATE.REQUIRES_HUMAN");
        ActionGateEventsService.DeniedType.Should().Be("ACTION.GATE.DENIED");
        ActionGateEventsService.AuthorizedType.Should().Be("ACTION.GATE.AUTHORIZED");
        ActionGateEventsService.AuthorizationDeniedType.Should().Be("ACTION.GATE.AUTHORIZATION_DENIED");
        ActionGateEventsService.PrincipalUnresolvedType.Should().Be("ACTION.GATE.PRINCIPAL_UNRESOLVED");
        ActionGateEventsService.EvaluationFailedType.Should().Be("ACTION.GATE.EVALUATION_FAILED");
        ActionGateEventsService.AssignmentChangedType.Should().Be("ACTION.GATE.ASSIGNMENT_CHANGED");
    }

    [Test]
    public async Task DecisionEvent_CarriesTheTagSet()
    {
        var repo = new FakeEventRepository();
        var service = new ActionGateEventsService(repo);
        var tid = Guid.NewGuid();

        await service.EmitDecisionAsync(
            Decision(AutonomyOutcome.RequiresHuman), Query(tid), issueId: "42");

        var evt = repo.Appended.Single();
        evt.Type.Should().Be(ActionGateEventsService.RequiresHumanType);
        evt.TenantId.Should().Be(tid);

        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!)!;
        tags["actionKey"].Should().Be("agent-action:deploy");
        tags["actionGroup"].Should().Be("deploy-control");
        tags["risk"].Should().Be("destructive");
        tags["autonomyLevel"].Should().Be(AutonomyDial.Min.ToString());
        tags["effectiveMinAutonomy"].Should().Be(AutonomyDial.AlwaysHuman.ToString());
        tags["assignmentSource"].Should().Be("action-override");
        tags["outcome"].Should().Be("requireshuman");
        tags["enforced"].Should().Be("true");
        tags["role"].Should().Be("devops");
        tags["correlationId"].Should().Be("wf-1");
        tags["issueId"].Should().Be("42");
        tags["tenantId"].Should().Be(tid.ToString());
        tags["callerKind"].Should().Be("llm",
            "Story 43-13 AC8 — every decision row names WHO it was decided for; the "
            + "query above declares no caller, so the fail-closed default (llm) is what "
            + "must appear on the wire");
        tags.Should().NotContainKey("userId", "the principal is tenant-scoped");
    }

    [Test]
    public async Task DecisionEvent_CarriesTheDeclaredCallerKind()
    {
        // The other two wire spellings (43-13 D9): a Human decision and a
        // Machinery decision are distinguishable in the trail.
        var repo = new FakeEventRepository();
        var service = new ActionGateEventsService(repo);

        await service.EmitDecisionAsync(
            Decision(AutonomyOutcome.RequiresHuman),
            Query() with { Caller = CallerKind.Human });
        await service.EmitDecisionAsync(
            Decision(AutonomyOutcome.RequiresHuman),
            Query() with { Caller = CallerKind.Machinery });

        var kinds = repo.Appended
            .Select(e => JsonSerializer.Deserialize<Dictionary<string, string?>>(e.Tags!)!["callerKind"])
            .ToArray();
        kinds.Should().Equal("human", "machinery");
    }

    [Test]
    public async Task Allowed_IsSuppressed_ForSystemDefaultResolutions_AndEmittedOtherwise()
    {
        var repo = new FakeEventRepository();
        var service = new ActionGateEventsService(repo);

        await service.EmitDecisionAsync(
            Decision(AutonomyOutcome.Automated, ActionAssignmentSource.SystemDefault), Query());
        repo.Appended.Should().BeEmpty(
            "a 40-call tool loop must not write 40 rows saying nothing happened");

        await service.EmitDecisionAsync(
            Decision(AutonomyOutcome.Automated, ActionAssignmentSource.GroupOverride), Query());
        repo.Appended.Should().ContainSingle()
            .Which.Type.Should().Be(ActionGateEventsService.AllowedType);
    }

    [Test]
    public async Task AHumanAllow_IsNotVolumeGated()
    {
        // Story 43-13 D9 — a "passed because human" allow is the new predicate's
        // work product and must reach the trail even at SystemDefault source.
        var repo = new FakeEventRepository();
        var service = new ActionGateEventsService(repo);

        await service.EmitDecisionAsync(
            Decision(AutonomyOutcome.Automated, ActionAssignmentSource.SystemDefault) with
            {
                Reason = AutonomyGateEvaluator.ReasonCallerHuman,
            },
            Query() with { Caller = CallerKind.Human });

        var evt = repo.Appended.Should().ContainSingle().Which;
        evt.Type.Should().Be(ActionGateEventsService.AllowedType);
        JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!)!["callerKind"]
            .Should().Be("human");
    }

    [Test]
    public async Task AMachineryAllow_IsVolumeGated()
    {
        // Story 43-13 D9 — the machinery short-circuit keeps SystemDefault
        // source and STAYS suppressed: Seam D would otherwise emit one row per
        // actor per tick, all saying "nothing is ever going to happen here".
        var repo = new FakeEventRepository();
        var service = new ActionGateEventsService(repo);

        await service.EmitDecisionAsync(
            Decision(AutonomyOutcome.Automated, ActionAssignmentSource.SystemDefault) with
            {
                Reason = AutonomyGateEvaluator.ReasonMachineryNotDialGoverned,
            },
            Query() with { Caller = CallerKind.Machinery });

        repo.Appended.Should().BeEmpty();
    }

    [Test]
    public async Task AppendFailure_OnAllowed_IsSwallowed()
    {
        var repo = new FakeEventRepository { Throw = true };
        var service = new ActionGateEventsService(repo);

        await service.Invoking(s => s.EmitDecisionAsync(
                Decision(AutonomyOutcome.Automated, ActionAssignmentSource.ActionOverride), Query()))
            .Should().NotThrowAsync("non-denial emission is best-effort (the template's posture)");
    }

    [Test]
    public async Task AppendFailure_OnAnEnforcedDenial_Rethrows()
    {
        var repo = new FakeEventRepository { Throw = true };
        var service = new ActionGateEventsService(repo);

        await service.Invoking(s => s.EmitDecisionAsync(
                Decision(AutonomyOutcome.Denied, enforced: true), Query()))
            .Should().ThrowAsync<InvalidOperationException>(
                "a block with no audit row is a compliance hole (AC13)");

        await service.Invoking(s => s.EmitDecisionAsync(
                Decision(AutonomyOutcome.RequiresHuman, enforced: true), Query()))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task AppendFailure_OnAnUnenforcedDenial_IsSwallowed()
    {
        var repo = new FakeEventRepository { Throw = true };
        var service = new ActionGateEventsService(repo);

        await service.Invoking(s => s.EmitDecisionAsync(
                Decision(AutonomyOutcome.Denied, enforced: false), Query()))
            .Should().NotThrowAsync("observe-only decisions never break the caller");
    }

    [Test]
    public async Task AssignmentChanged_IsBestEffort_AndCarriesTheChange()
    {
        var repo = new FakeEventRepository();
        var service = new ActionGateEventsService(repo);
        var tid = Guid.NewGuid();

        await service.EmitAssignmentChangedAsync(
            tid, null, Guid.NewGuid(), "principal", "action", "agent-action:deploy",
            "minAutonomy", oldValue: null, newValue: AutonomyDial.AlwaysHuman);

        var evt = repo.Appended.Single();
        evt.Type.Should().Be(ActionGateEventsService.AssignmentChangedType);
        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!)!;
        tags["targetKey"].Should().Be("agent-action:deploy");
        tags["field"].Should().Be("minAutonomy");
        tags["scope"].Should().Be("principal");

        repo.Throw = true;
        await service.Invoking(s => s.EmitAssignmentChangedAsync(
                tid, null, null, "principal", "action", "x", "enforce", null, true))
            .Should().NotThrowAsync("the assignment row is the durable fact");
    }

    // ── Story 43-14 (AC8, D9) — the GRANT_MINTED audit event ──

    [Test]
    public async Task GrantMinted_CarriesActorInstanceScopeAndTargets()
    {
        var repo = new FakeEventRepository();
        var service = new ActionGateEventsService(repo);
        var tid = Guid.NewGuid();
        var decider = Guid.NewGuid();
        var targets = new[] { "effect:deploy.prod", "effect:git.release.create" };

        await service.EmitGrantMintedAsync(
            tid, null, "deploy-tail", "run-9", "wf-instance-3", decider, "alice@x.com", targets);

        var evt = repo.Appended.Should().ContainSingle().Which;
        evt.Type.Should().Be(ActionGateEventsService.GrantMintedType);
        var tags = JsonSerializer.Deserialize<Dictionary<string, string?>>(evt.Tags!)!;
        tags["chain"].Should().Be("deploy-tail");
        tags["correlationId"].Should().Be("run-9");
        tags["scope"].Should().Be("correlation-standing");
        tags["workflowInstanceId"].Should().Be("wf-instance-3");
        tags["decidedByUserId"].Should().Be(decider.ToString());
        tags["approver"].Should().Be("alice@x.com");
        tags["targetCount"].Should().Be("2");
    }

    [Test]
    public async Task GrantMintedEmissionFailure_IsSwallowed()
    {
        // D9 — the grant ROW is the durable record; a mint that cannot be audited
        // still mints (the block-not-recorded rule protects denials, not grants).
        var repo = new FakeEventRepository { Throw = true };
        var service = new ActionGateEventsService(repo);
        await service.Invoking(s => s.EmitGrantMintedAsync(
                Guid.NewGuid(), null, "merge-composite", "run-1", null, Guid.NewGuid(), null,
                new[] { "effect:git.merge.main" }))
            .Should().NotThrowAsync();
    }
}
