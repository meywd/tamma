using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Actions;
using Tamma.Api.Services.Agents;
using Tamma.Core.Actions;
using Tamma.Data;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 43-9 <b>AC3, arm (a)</b> — <c>LlmCallSeam_NeverBlocks_EvenUnderEnforce</c>.
///
/// <para><b>Seam A is observe-only in EVERY version, and that is a safety
/// decision, not a phase.</b> Two independent reasons, either sufficient:
/// (i) a <c>RequiresHuman</c> returned at <c>POST /api/v1/llm/call</c> reaches a
/// <c>DispatchWorkflow</c> whose CALLING workflow has no human route in 44 of 45
/// cases — the workflow would suspend with nobody able to resume it, which is
/// strictly worse than proceeding; (ii) blocking here AND at Seam E would
/// double-gate deploy, because the deployment pipeline reaches the model through
/// this very route while Seam E gates the prod-approval decision. Agent-action
/// enforcement lives ONLY at Seam E.</para>
///
/// <para><b>This is arm (a) of a two-arm pin, and it is the weaker arm.</b> It
/// tests the CONSEQUENCE — the handler proceeds — which a future author could
/// preserve by accident while still making the route blockable elsewhere. Arm (b),
/// <c>GovernedEndpointEnforcementSweepTests.LlmCallRoute_IsBound_ButNotEnforced</c>,
/// tests the WIRING: the route carries <c>.Governs(...)</c> and deliberately no
/// <c>.EnforcesGovernance()</c>. Both are required; neither alone survives a
/// change in how the filter decides.</para>
/// </summary>
[TestFixture]
public class LlmCallSeamAObserveOnlyTests
{
    // ── Doubles ─────────────────────────────────────────────────────────────

    private sealed class FixedTenant(Guid? tenantId) : ITenantContext
    {
        public Guid? TenantId { get; private set; } = tenantId;
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
        public void ClearTenantId() => TenantId = null;
    }

    private sealed class RecordingAgent : IManagedAgent
    {
        public int Runs { get; private set; }
        public Task<AgentRunResult> RunAsync(ManagedAgentRequest request, CancellationToken ct = default)
        {
            Runs++;
            return Task.FromResult(new AgentRunResult
            {
                Success = true,
                Role = request.Role,
                CorrelationId = request.CorrelationId,
            });
        }
    }

    private sealed class PassthroughMapper : ILlmCallResponseMapper
    {
        public LlmCallResponse ToResponse(AgentRunResult run) =>
            new() { Success = run.Success, CorrelationId = run.CorrelationId ?? "" };
        public IResult ToHttpResult(AgentRunResult run) => Results.Ok(new { success = run.Success });
    }

    /// <summary>
    /// A gate pinned to the most hostile answer a policy row can produce:
    /// <c>Denied</c>, <c>Enforced = true</c>, threshold at
    /// <see cref="AutonomyDial.AlwaysHuman"/> — i.e. exactly what an admin gets by
    /// setting the action to human-only at platform, tenant OR user scope, since
    /// all three compose into this one resolved decision.
    /// </summary>
    private sealed class AlwaysHumanGate : IAutonomyGate
    {
        public List<AutonomyQuery> Queries { get; } = new();
        public AutonomyOutcome Outcome { get; init; } = AutonomyOutcome.Denied;

        public Task<AutonomyDecision> EvaluateAsync(AutonomyQuery query, CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(new AutonomyDecision(
                Outcome, query.Action, ActionGroup.PlanningAndAnalysis, ActionRisk.Mutating,
                AutonomyLevel: Core.Documents.Policy.AutonomyDial.Min,
                EffectiveMinAutonomy: Core.Documents.Policy.AutonomyDial.AlwaysHuman,
                ActionAssignmentSource.PlatformCeiling,
                Enforced: true, Enabled: true, AllowedRoles: null, Reason: "always-human"));
        }
    }

    private sealed class ThrowingGate : IAutonomyGate
    {
        public Task<AutonomyDecision> EvaluateAsync(AutonomyQuery query, CancellationToken ct = default)
            => throw new InvalidOperationException("control plane unreachable");
    }

    /// <summary>
    /// A gate that throws <see cref="OperationCanceledException"/> for a reason
    /// that has NOTHING to do with the caller: an internal linked-CTS deadline, an
    /// EF cancellation on a pooled command, a Polly timeout. The request's own
    /// token is never cancelled.
    /// </summary>
    private sealed class GateSideCancellationGate : IAutonomyGate
    {
        public Task<AutonomyDecision> EvaluateAsync(AutonomyQuery query, CancellationToken ct = default)
            => throw new OperationCanceledException(
                "internal gate deadline", new CancellationTokenSource(0).Token);
    }

    private sealed class FixedPrincipal : IGovernancePrincipalResolver
    {
        public Task<GovernancePrincipal> ResolveAsync(
            ClaimsPrincipal? caller = null, CancellationToken ct = default)
            => Task.FromResult(GovernancePrincipal.ForUser(Guid.NewGuid()));
    }

    private static LlmCallRequest Request(string? action = "code-generate") => new()
    {
        Role = "developer",
        Prompt = "do the thing",
        CorrelationId = "run-1",
        Action = action,
    };

    private static Task<IResult> Call(
        IAutonomyGate? gate, RecordingAgent agent, LlmCallRequest? request = null,
        CancellationToken ct = default) =>
        LlmCallEndpoints.CallLlm(
            request ?? Request(),
            new FixedTenant(null),
            agent,
            new PassthroughMapper(),
            NullLoggerFactory.Instance,
            ct,
            gate,
            new FixedPrincipal());

    // ====================================================================
    // AC3 arm (a)
    // ====================================================================

    [Test]
    public async Task LlmCallSeam_NeverBlocks_EvenUnderEnforce()
    {
        var gate = new AlwaysHumanGate();
        var agent = new RecordingAgent();

        var result = await Call(gate, agent);

        agent.Runs.Should().Be(1,
            "SEAM A MUST NEVER BLOCK. The gate said Denied, enforced, at AlwaysHuman — the "
            + "hostile answer an admin produces by pinning the action to a human at any scope — "
            + "and the dispatch STILL proceeded. If this ever fails, a workflow with no human "
            + "route (44 of 45) will suspend with nobody able to resume it.");
        result.Should().NotBeNull();
    }

    [Test]
    public async Task LlmCallSeam_NeverBlocks_OnRequiresHumanEither()
    {
        // The other blocking outcome. RequiresHuman is the one that LOOKS
        // survivable ("a person will pick it up") and is in fact the worse of the
        // two here: there is no person on this path.
        var gate = new AlwaysHumanGate { Outcome = AutonomyOutcome.RequiresHuman };
        var agent = new RecordingAgent();

        await Call(gate, agent);

        agent.Runs.Should().Be(1);
    }

    [Test]
    public async Task TheSeam_actuallyEvaluates_soTheNeverBlocksTestIsNotVacuous()
    {
        // THE ANTI-NO-OP HALF. "It never blocks" is trivially satisfiable by a
        // seam that never runs. This asserts the observation really happens, on
        // the agent-action plane, carrying the request's role and correlation.
        var gate = new AlwaysHumanGate();

        await Call(gate, new RecordingAgent());

        gate.Queries.Should().ContainSingle();
        gate.Queries[0].Action.Should().Be(
            new ActionKey(ActionNamespace.AgentAction, "code-generate"),
            "AC3 evaluates ActionKey(AgentAction, request.Action) — the interesting question at "
            + "this seam is WHICH agent step is running; effect:llm.call is what the ROUTE is "
            + "bound to, for the drift harnesses");
        gate.Queries[0].Role.Should().Be("developer");
        gate.Queries[0].CorrelationId.Should().Be("run-1");
    }

    [Test]
    public async Task NoActionOnTheRequest_meansNoEvaluation()
    {
        // AC3: "when LlmCallRequest.Action is non-null". A null action has no
        // agent-action key to evaluate, and inventing one would either fabricate a
        // catalogued action or spam `uncatalogued` allows into the audit stream.
        var gate = new AlwaysHumanGate();
        var agent = new RecordingAgent();

        await Call(gate, agent, Request(action: null));

        gate.Queries.Should().BeEmpty();
        agent.Runs.Should().Be(1);
    }

    [Test]
    public async Task AnObservationFailure_doesNotFailTheCall()
    {
        // An observe-only seam that can 500 the request it is observing is a
        // blocking seam with extra steps — the exact outcome epic D2 forbids.
        var agent = new RecordingAgent();

        var act = async () => await Call(new ThrowingGate(), agent);

        await act.Should().NotThrowAsync();
        agent.Runs.Should().Be(1);
    }

    [Test]
    public async Task NoGateRegistered_isNotAnError()
    {
        var agent = new RecordingAgent();
        await Call(gate: null, agent);
        agent.Runs.Should().Be(1);
    }

    // ====================================================================
    // 2026-08-01 review finding F7 — the observing seam must not be able to
    // fail a call it is not permitted to block, by ANY exception type
    // ====================================================================

    [Test]
    public async Task AGateSideCancellation_onAnUncancelledRequest_doesNotFailTheCall()
    {
        // F7. `AnObservationFailure_doesNotFailTheCall` above only covers exceptions
        // that are not OperationCanceledException — the observer had a
        // `catch (OperationCanceledException) { throw; }` ahead of its swallow-all,
        // so an OCE raised INSIDE the gate escaped and failed the LLM call. That is
        // control flow, on the one seam whose whole contract is that it has none.
        //
        // The request's token is CancellationToken.None: nothing the caller did
        // caused this. The realistic sources are an internal linked-CTS deadline in
        // the gate, an EF cancellation on a pooled command, or a Polly timeout —
        // all NEW failure surface on a route that made no gate call before Story
        // 43-9.
        var agent = new RecordingAgent();

        var act = async () => await Call(new GateSideCancellationGate(), agent, ct: CancellationToken.None);

        await act.Should().NotThrowAsync<OperationCanceledException>(
            "an OperationCanceledException the CALLER did not cause is just another observation "
            + "failure; letting it through makes Seam A able to fail a call it may not block");
        agent.Runs.Should().Be(1,
            "and the dispatch must still have happened — Seam A never blocks, in any version");
    }

    [Test]
    public async Task AGenuineCallerCancellation_stillPropagates()
    {
        // THE ANTI-OVERSHOOT HALF. The fix must narrow the rethrow to a real caller
        // abort, not delete it: when the client HAS aborted, there is no point
        // running the provider, and swallowing the cancellation would turn an
        // aborted request into a billed model call.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var agent = new RecordingAgent();

        var act = async () => await Call(new GateSideCancellationGate(), agent, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        agent.Runs.Should().Be(0, "an aborted request must not reach the provider");
    }
}
