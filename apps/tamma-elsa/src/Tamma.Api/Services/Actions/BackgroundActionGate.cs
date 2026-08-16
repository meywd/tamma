using Microsoft.Extensions.DependencyInjection;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Repositories;
using Tamma.Core.Actions;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-9 <b>Seam D</b> (AC9, D8) — the per-tick gate for background actors.
/// One call per tick per actor: <c>true</c> means "run this tick", <c>false</c>
/// means "skip it, an admin has switched this actor off".
///
/// <para><b>IT CAN ONLY DENY, and that is structural rather than a policy
/// choice.</b> A hosted service has no <c>ActivityExecutionContext</c>, no
/// bookmark and nobody watching, so it cannot suspend for a person. Every
/// <c>automation:*</c> descriptor is therefore <c>EscalatableToHuman = false</c>
/// by construction (the <c>Automation(...)</c> factory hard-codes it), which
/// makes the pure evaluator collapse a below-threshold resolution to
/// <see cref="AutonomyOutcome.Denied"/> rather than
/// <see cref="AutonomyOutcome.RequiresHuman"/>; and the admin API rejects a
/// mid-range <c>MinAutonomy</c> on such a target with <c>ACTION_POLICY.INVALID</c>
/// instead of silently treating it as Deny. The dial for a sweeper is two-state:
/// <c>Min</c> (run) or <c>AlwaysHuman</c> (off).</para>
///
/// <para><b>IT MUST NEVER TAKE DOWN THE HOST.</b>
/// <c>BackgroundServiceExceptionBehavior</c> defaults to <c>StopHost</c>, so an
/// unhandled governance failure inside a tick would kill the process. Every
/// exception is caught INSIDE this helper, emitted as
/// <c>ACTION.GATE.EVALUATION_FAILED</c>, and answered <c>true</c> — fail-OPEN on
/// an evaluation ERROR, deny only on a DECISION. Fail-closed on an error would
/// stop every sweeper on the platform during a control-plane blip, which is a
/// worse failure than a few ungated ticks; the honest residual (a permanently
/// broken gate silently means "all sweepers ungated") is mitigated by alerting on
/// <c>EVALUATION_FAILED</c> volume, not by flipping the direction.</para>
///
/// <para><b>TWO THINGS ARE NOT "AN EVALUATION ERROR", and both were being read as
/// one</b> (adversarial review, 2026-08-01):</para>
/// <list type="bullet">
///   <item><b>F2 — a DECISION whose audit row failed.</b>
///   <see cref="AutonomyGateDecisionUnrecordedException"/> means the gate decided
///   and only the record of it is missing. It is caught before the catch-all and
///   the decision is re-applied, so a gated-off actor stays off. The two failures
///   are correlated — 43-5's fail-closed degradation fires when the control plane
///   is unreadable and <c>domain_events</c> lives in the same Postgres — so
///   without this one blip both produced the denial and cancelled it.</item>
///   <item><b>F9 — a HANG.</b> A catch covers a throw, not a gate that never
///   returns; an unbounded evaluation stalls the sweeper's loop indefinitely,
///   which is neither open nor closed. Every evaluation runs under
///   <see cref="AutonomyGateDeadline"/>, and a timeout resolves into the fail-open
///   arm above.</item>
/// </list>
///
/// <para><b>SCOPE PER TICK — getting this wrong is a startup crash.</b>
/// <see cref="IAutonomyGate"/> and <see cref="IGovernancePrincipalResolver"/> are
/// registered SCOPED (they read the scoped <c>ITenantContext</c>,
/// <c>IEventRepository</c> and <c>IAcceptanceRulesResolver</c>); an
/// <c>IHostedService</c> is a SINGLETON. So this helper is a singleton that holds
/// only <see cref="IServiceScopeFactory"/> and creates a scope per call. A caller
/// must never be tempted to inject <see cref="IAutonomyGate"/> into a hosted
/// service directly.</para>
/// </summary>
public interface IBackgroundActionGate
{
    /// <summary>
    /// May <paramref name="actor"/> run this tick?
    /// </summary>
    /// <param name="actor">The catalogued background actor.</param>
    /// <param name="tenantId">
    /// The tenant a tenant-scoped sweep is acting for, or null for a
    /// cross-tenant/platform sweep (which resolves against the platform scope and
    /// the shipped defaults).
    /// </param>
    /// <returns><c>true</c> to run — including on every evaluation error.</returns>
    Task<bool> MayRunAsync(BackgroundActor actor, Guid? tenantId = null, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class BackgroundActionGate : IBackgroundActionGate
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BackgroundActionGate>? _logger;

    public BackgroundActionGate(
        IServiceScopeFactory scopes, ILogger<BackgroundActionGate>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> MayRunAsync(
        BackgroundActor actor, Guid? tenantId = null, CancellationToken ct = default)
    {
        // Cancellation is NOT swallowed: a host shutting down mid-tick is not a
        // governance failure and must not be logged as one. Everything else is.
        ct.ThrowIfCancellationRequested();

        var key = new ActionKey(ActionNamespace.Automation, actor.ToWire());

        // F9 — a catch covers a THROW, not a HANG. A gate that never returns
        // would otherwise stall this sweeper's loop forever.
        using var deadline = AutonomyGateDeadline.CreateLinkedSource(ct);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var gate = scope.ServiceProvider.GetService<IAutonomyGate>();
            if (gate is null)
            {
                // No governance stack in this host (the engine registers none).
                // Not an error and not a denial — there is no policy to apply.
                return true;
            }

            // 2026-08-14 (engine-driven E2E): a background tick has no HTTP
            // scope, so EnsurePersonalTenantMiddleware never ran and the
            // ambient tenant was NULL. In single-user mode every tenant-
            // resident read the gate performs (acceptance_rules_overrides)
            // then threw "requires an ambient tenant id", the gate CORRECTLY
            // read that as unreadable policy and denied, and so EVERY
            // background actor in a single-user deployment was gated off
            // permanently (outbox sender, task-queue processor, ...) while
            // logging an error per tick. Bind the sole user's existing
            // personal tenant for this scope — the same seam the middleware
            // uses. Read-only on purpose: minting a tenant belongs to a user
            // request, so a pre-setup deployment still behaves as before.
            if (tenantId is null)
            {
                await TryBindSingleUserTenantAsync(scope.ServiceProvider, deadline.Token)
                    .ConfigureAwait(false);
                tenantId = scope.ServiceProvider.GetService<ITenantContext>()?.TenantId;
            }

            var principal = tenantId is Guid tid
                ? GovernancePrincipal.ForTenant(tid)
                : GovernancePrincipal.Platform;

            var decision = await gate.EvaluateAsync(
                new AutonomyQuery(
                    key, principal,
                    Role: null,
                    Operation: "background-tick",
                    Target: actor.ToWire(),
                    CorrelationId: null,
                    // F4 — this seam DOES block (it skips the tick), so it is
                    // entitled to spend a grant. It never actually consults the
                    // ledger, because it has no correlation to key one to and
                    // because an automation target is never escalatable, but the
                    // flag states the seam's capability rather than leaving a
                    // future correlation-carrying caller to discover it.
                    SeamCanBlock: true,
                    // Story 43-13 AC6 — the DECLARATION, not a bypass: this is
                    // the ONE in-process site that may say Machinery (it has no
                    // wire spelling). The dial comparison is skipped for the
                    // tick, but enabled=false still denies and the fail-closed
                    // unreadable-policy posture still bites — Seam D keeps its
                    // Amendment-4 job of denying a background job that would
                    // execute an un-gated upstream LLM decision.
                    Caller: CallerKind.Machinery),
                deadline.Token).ConfigureAwait(false);

            return Apply(decision, actor, tenantId);
        }
        catch (AutonomyGateDecisionUnrecordedException unrecorded)
        {
            // F2 — NOT an evaluation error: the gate decided, only the record
            // failed. Answering "run the tick" here would let one Postgres blip
            // both produce a fail-closed denial and cancel it.
            _logger?.LogError(unrecorded,
                "Autonomy gate DECIDED {Outcome} for background actor {Actor} (tenant={TenantId}) "
                + "but the audit row could not be written. The decision STANDS.",
                unrecorded.Decision.Outcome, actor.ToWire(), tenantId);
            return Apply(unrecorded.Decision, actor, tenantId);
        }
        catch (OperationCanceledException) when (!AutonomyGateDeadline.IsDeadline(ct)) { throw; }
        catch (Exception ex)
        {
            // FAIL OPEN, and never out of this method: an escaping exception in a
            // BackgroundService stops the host by default.
            _logger?.LogError(ex,
                "Autonomy evaluation FAILED for background actor {Actor} (tenant={TenantId}); "
                + "the tick PROCEEDS ungated. Deny on a decision, never on an error — "
                + "fail-closed here would stop every sweeper on a control-plane blip.",
                actor.ToWire(), tenantId);

            await TryEmitEvaluationFailedAsync(key, ex).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// Single-user only: bind the sole user's EXISTING personal tenant onto this
    /// scope so the gate's tenant-resident policy reads have a home. Silent and
    /// best-effort by design — every failure mode (SaaS, pre-setup, no
    /// membership, missing seam) simply leaves the scope tenant-less, which is
    /// exactly the behaviour that shipped before this bind existed.
    /// </summary>
    private static async Task TryBindSingleUserTenantAsync(
        IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var mode = services.GetService<ITammaModeProvider>();
            if (mode is null || mode.Mode != TammaMode.SingleUser) return;

            var tenantContext = services.GetService<ITenantContext>();
            if (tenantContext is null || tenantContext.TenantId is not null) return;

            var soleUsers = services.GetService<ISoleUserProvider>();
            var memberships = services.GetService<ITenantMembershipRepository>();
            if (soleUsers is null || memberships is null) return;

            var userId = await soleUsers.GetSoleUserIdAsync(ct).ConfigureAwait(false);
            var owned = await memberships.GetUserTenantsAsync(userId).ConfigureAwait(false);
            if (owned.Count == 0) return;

            tenantContext.SetTenantId(owned.OrderByDescending(m => m.JoinedAt).First().TenantId);
        }
        catch
        {
            // Pre-setup deployment (no owner configured / no users row), or any
            // other resolution failure: leave the scope tenant-less. The gate's
            // own fail-closed posture then applies unchanged.
        }
    }

    /// <summary>
    /// The tick answer for one decision — the SAME predicate whether the decision
    /// arrived normally or attached to an unrecorded-decision failure (F2), so the
    /// two paths cannot drift.
    /// </summary>
    private bool Apply(AutonomyDecision decision, BackgroundActor actor, Guid? tenantId)
    {
        if (!decision.Enforced || decision.Outcome == AutonomyOutcome.Automated)
        {
            return true;
        }

        // The tick is SKIPPED. The audit row is written by the gate on the
        // non-swallowing path (an enforced denial is never swallowed) — or, when
        // that append itself failed, the F2 log above is the loud record — so this
        // log is operator ergonomics, not the record.
        _logger?.LogInformation(
            "Background actor {Actor} is gated OFF by autonomy policy "
            + "(outcome={Outcome}, effectiveMinAutonomy={EffectiveMin}, source={Source}, "
            + "tenant={TenantId}); this tick is skipped.",
            actor.ToWire(), decision.Outcome, decision.EffectiveMinAutonomy,
            decision.Source, tenantId);
        return false;
    }

    /// <summary>
    /// Best-effort <c>ACTION.GATE.EVALUATION_FAILED</c>. Its own failure is
    /// swallowed: this method exists because the host must not die, and an audit
    /// append that throws while reporting an audit-able failure would defeat the
    /// entire point.
    /// </summary>
    private async Task TryEmitEvaluationFailedAsync(ActionKey key, Exception cause)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var events = scope.ServiceProvider.GetService<ActionGateEventsService>();
            if (events is null) return;
            await events.EmitEvaluationFailedAsync(
                key.ToWire(), cause.Message, tenantId: null, userId: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Could not emit ACTION.GATE.EVALUATION_FAILED for {ActionKey}.", key.ToWire());
        }
    }
}

/// <summary>
/// Seam D's CALL-SITE shape (Story 43-9 AC9). Hosted services resolve the gate
/// from the provider or scope factory they already hold rather than taking a new
/// constructor parameter.
///
/// <para><b>Why not a constructor parameter, given Seam B made its gate REQUIRED
/// in the constructor for exactly the opposite reason?</b> Because the two
/// hazards are different. Seam B's runner has ten OPTIONAL-NULLABLE collaborators,
/// so a nullable gate there would be absent precisely whenever an unrelated
/// optional dependency was absent. A hosted service's <see cref="IServiceProvider"/>
/// / <see cref="IServiceScopeFactory"/> is NEVER absent in a running host — it is
/// how the service does all its work — so resolving through it cannot produce the
/// "silently ungated in production" failure that made Seam B's parameter
/// required. What it buys is that no construction site moves, which matters here
/// because these classes are constructed directly by suites this story does not
/// own.</para>
///
/// <para>A host with no governance stack registered (the Elsa engine) answers
/// <c>true</c>: there is no policy there to apply, and Seam D deliberately does
/// not invent one.</para>
/// </summary>
public static class BackgroundActionGateAccessor
{
    /// <summary>One tick gate for a service holding an <see cref="IServiceProvider"/>.</summary>
    public static async Task<bool> MayRunTickAsync(
        IServiceProvider services,
        BackgroundActor actor,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var gate = services.GetService<IBackgroundActionGate>();
        return gate is null
            || await gate.MayRunAsync(actor, tenantId, ct).ConfigureAwait(false);
    }

    /// <summary>One tick gate for a service holding an <see cref="IServiceScopeFactory"/>.</summary>
    public static async Task<bool> MayRunTickAsync(
        IServiceScopeFactory scopes,
        BackgroundActor actor,
        Guid? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        await using var scope = scopes.CreateAsyncScope();
        return await MayRunTickAsync(scope.ServiceProvider, actor, tenantId, ct)
            .ConfigureAwait(false);
    }
}
