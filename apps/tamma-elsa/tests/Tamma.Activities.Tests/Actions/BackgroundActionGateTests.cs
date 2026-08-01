using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-9 <b>Seam D</b> (AC9) — the deny-only background gate.
///
/// <para>The other half of AC9 — that the admin API refuses a mid-range threshold
/// on a non-escalatable <c>automation:*</c> target with <c>ACTION_POLICY.INVALID</c>
/// — shipped with Story 43-6 and is pinned by
/// <c>ActionPolicyEndpointsTests.AutomationTarget_RejectsMidRangeThreshold</c>. It
/// is deliberately not duplicated here.</para>
/// </summary>
[TestFixture]
public class BackgroundActionGateTests
{
    /// <summary>A gate that answers with a scripted decision, or throws.</summary>
    private sealed class ScriptedGate(AutonomyDecision? decision, bool throws = false) : IAutonomyGate
    {
        public int Calls { get; private set; }
        public List<AutonomyQuery> Queries { get; } = new();

        public Task<AutonomyDecision> EvaluateAsync(AutonomyQuery query, CancellationToken ct = default)
        {
            Calls++;
            Queries.Add(query);
            if (throws) throw new InvalidOperationException("control plane unreachable");
            return Task.FromResult(decision!);
        }
    }

    private sealed class RecordingEvents : IEventRepository
    {
        public List<DomainEvent> Appended { get; } = new();
        public Task<DomainEvent> AppendAsync(DomainEvent evt) { Appended.Add(evt); return Task.FromResult(evt); }
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

    private static readonly BackgroundActor Actor = BackgroundActor.ChannelOutboxSweeper;

    private static AutonomyDecision Decision(AutonomyOutcome outcome, bool enforced = true) =>
        new(outcome,
            new ActionKey(ActionNamespace.Automation, Actor.ToWire()),
            ActionGroup.PlatformAutomation, ActionRisk.Mutating,
            AutonomyLevel: 1, EffectiveMinAutonomy: 99,
            ActionAssignmentSource.ActionOverride,
            Enforced: enforced, Enabled: true, AllowedRoles: null,
            Reason: "below-min-autonomy");

    private static (BackgroundActionGate Gate, ScriptedGate Inner, RecordingEvents Events) Build(
        AutonomyDecision? decision, bool throws = false, bool registerGate = true)
    {
        var inner = new ScriptedGate(decision, throws);
        var events = new RecordingEvents();
        var services = new ServiceCollection();
        // SCOPED, exactly as production registers it — which is the whole reason
        // the helper takes IServiceScopeFactory instead of IAutonomyGate.
        if (registerGate) services.AddScoped<IAutonomyGate>(_ => inner);
        services.AddScoped(_ => new ActionGateEventsService(events));
        var provider = services.BuildServiceProvider();
        return (new BackgroundActionGate(provider.GetRequiredService<IServiceScopeFactory>()),
                inner, events);
    }

    // ====================================================================
    // The two AC9 tests that did not exist
    // ====================================================================

    [Test]
    public async Task Denied_tick_is_skipped_and_audited()
    {
        var (gate, inner, _) = Build(Decision(AutonomyOutcome.Denied));

        var mayRun = await gate.MayRunAsync(Actor);

        mayRun.Should().BeFalse("an admin who switched this actor off must actually stop it");
        inner.Calls.Should().Be(1, "ONE gate call per tick — not per item, not per scope");
        inner.Queries[0].Action.Should().Be(new ActionKey(ActionNamespace.Automation, Actor.ToWire()));

        // The audit row is written by the gate itself on the NON-swallowing path
        // (an enforced denial is never swallowed), which is why the helper does
        // not — and must not — emit a second one. The ScriptedGate above stands in
        // for that, so what this asserts is that the helper does not swallow the
        // DECISION, which is the failure mode that matters.
        inner.Queries[0].Operation.Should().Be("background-tick");
    }

    [Test]
    public async Task Evaluation_failure_does_not_propagate_out_of_the_helper()
    {
        // BackgroundServiceExceptionBehavior defaults to StopHost, so an
        // exception escaping a tick KILLS THE PROCESS. This is the test that
        // stops a governance blip taking down the API host.
        var (gate, _, events) = Build(decision: null, throws: true);

        var act = async () => await gate.MayRunAsync(Actor);

        await act.Should().NotThrowAsync();
        (await gate.MayRunAsync(Actor)).Should().BeTrue(
            "fail OPEN on an evaluation ERROR — deny only on a DECISION. Fail-closed here would "
            + "stop every sweeper on the platform during a control-plane blip, which is a worse "
            + "failure than a few ungated ticks.");

        events.Appended.Select(e => e.Type).Should().Contain(
            ActionGateEventsService.EvaluationFailedType,
            "an ungated tick must leave a signal an operator can alert on — that is the whole "
            + "mitigation for fail-open");
    }

    // ====================================================================
    // The anti-no-op complement and the deny-only property
    // ====================================================================

    [Test]
    public async Task AnAutomatedDecision_runsTheTick()
    {
        // Without this, "a denial skips the tick" is satisfiable by a helper that
        // skips everything.
        var (gate, _, _) = Build(Decision(AutonomyOutcome.Automated));
        (await gate.MayRunAsync(Actor)).Should().BeTrue();
    }

    [Test]
    public async Task AnObserveOnlyDecision_runsTheTick_evenWhenTheOutcomeBlocks()
    {
        // `Enforce = false` is the admin's explicit "report but do not block".
        // A seam that ignored it would turn an observe-mode rollout into an outage.
        var (gate, _, _) = Build(Decision(AutonomyOutcome.Denied, enforced: false));
        (await gate.MayRunAsync(Actor)).Should().BeTrue();
    }

    [Test]
    public async Task ARequiresHumanDecision_alsoOnlySkips_becauseASweeperCannotWaitForAPerson()
    {
        // Belt and braces. The evaluator collapses RequiresHuman → Denied for
        // every automation:* member because the Automation(...) factory hard-codes
        // EscalatableToHuman = false, so this shape should be unreachable. If it
        // ever IS reached, the helper must still only SKIP — never suspend, never
        // escalate, never throw. There is no bookmark on this path and nobody
        // watching.
        var (gate, _, _) = Build(Decision(AutonomyOutcome.RequiresHuman));
        (await gate.MayRunAsync(Actor)).Should().BeFalse();
    }

    [Test]
    public async Task AHostWithNoGovernanceStack_runs()
    {
        // Tamma.ElsaServer registers no IAutonomyGate. Its actors are not gated by
        // Seam D at all — recorded honestly rather than papered over: the engine
        // would need a mediation hop, which this story does not build.
        var (gate, _, _) = Build(decision: null, registerGate: false);
        (await gate.MayRunAsync(Actor)).Should().BeTrue();
    }

    [Test]
    public async Task EveryBackgroundActor_isEvaluable()
    {
        // Drives the call from Enum.GetValues<BackgroundActor>() rather than a
        // literal (the plan said "the 25 hosted services"; the plane has 29
        // members). A member whose key does not resolve in the catalog would
        // otherwise only be discovered at runtime, on the one tick it gates.
        var (gate, inner, _) = Build(Decision(AutonomyOutcome.Automated));

        foreach (var actor in Enum.GetValues<BackgroundActor>())
        {
            (await gate.MayRunAsync(actor)).Should().BeTrue();
        }

        inner.Calls.Should().Be(Enum.GetValues<BackgroundActor>().Length);
        inner.Queries.Select(q => q.Action.ToWire()).Should().OnlyHaveUniqueItems();
        inner.Queries.Should().AllSatisfy(q =>
            ActionCatalog.ByKey.ContainsKey(q.Action).Should().BeTrue(
                $"{q.Action.ToWire()} must be a catalogued action — Seam D asks the gate about a "
                + "key, and an uncatalogued one silently answers 'automated'"));
    }

    [Test]
    public async Task TheTenantScopedOverload_resolvesForThatTenant()
    {
        var tenant = Guid.NewGuid();
        var (gate, inner, _) = Build(Decision(AutonomyOutcome.Automated));

        await gate.MayRunAsync(Actor, tenant);

        inner.Queries[0].Principal.TenantId.Should().Be(tenant,
            "a tenant-scoped sweep must resolve THAT tenant's policy; a cross-tenant sweeper "
            + "passes null and resolves against the platform scope");
    }

    [Test]
    public async Task Cancellation_propagates_ratherThanBeingLoggedAsAGovernanceFailure()
    {
        // A host shutting down mid-tick is not a governance failure and must not
        // pollute the EVALUATION_FAILED stream the fail-open mitigation alerts on.
        var (gate, _, events) = Build(Decision(AutonomyOutcome.Automated));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await gate.MayRunAsync(Actor, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        events.Appended.Should().BeEmpty();
    }
}
