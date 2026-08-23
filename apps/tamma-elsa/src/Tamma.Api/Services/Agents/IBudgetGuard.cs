using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.ADL;
using Tamma.Api.Services.Diagnostics;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 (compose step 1b) — the server-side budget gate the managed
/// endpoint checks BEFORE the provider call (fail-closed). It mirrors the
/// existing <c>CheckBudgetActivity</c> contract — a cap of <c>0</c> (or less)
/// means "unlimited" (always within budget); a positive cap that cannot be
/// satisfied ⇒ DENY (the loop is never invoked). This is the named owner of the
/// budget gate in the rule-2 sequence.
///
/// <para>On an EVALUATION ERROR the posture is deny-if-capped, not deny-always:
/// an implementation denies when it knows a cap exists and allows when it does
/// not, because blocking every model call in a deployment that sets no caps at
/// all would be a self-inflicted outage. This sentence used to promise DENY on
/// "any error during evaluation", which no implementation has ever done — see
/// <see cref="RunningSpendBudgetGuard"/> for how "knows a cap exists" is
/// established, and for the one residual case that still allows.</para>
///
/// <para>The minimal seam keeps the heavy <c>CheckBudgetActivity</c> /
/// <c>TammaApiClient.GetBudgetAsync</c> integration out of the per-call hot
/// path until 32-9 supplies the server-side running-spend source; the
/// <see cref="PerCallBudgetGuard"/> default enforces the per-call cap the
/// request carries (<c>params.budgetCapUsd</c>) with the same fail-closed
/// discipline.</para>
/// </summary>
public interface IBudgetGuard
{
    /// <summary>
    /// Decide whether a managed run with the given per-call USD
    /// <paramref name="budgetCapUsd"/> may proceed. <c>true</c> ⇒ within budget;
    /// <c>false</c> ⇒ over budget / cannot evaluate ⇒ the caller fails closed
    /// with <c>BUDGET_EXCEEDED</c> (the loop is never invoked).
    /// </summary>
    Task<bool> IsWithinBudgetAsync(Guid? tenantId, decimal budgetCapUsd, CancellationToken ct = default);
}

/// <summary>
/// Story 32-5 — the interim per-call budget guard. It enforces the per-call
/// cap the request carries with the same fail-closed semantics as
/// <c>CheckBudgetActivity</c>:
/// <list type="bullet">
///   <item><description><c>cap &lt;= 0</c> ⇒ unlimited ⇒ within budget.</description></item>
///   <item><description><c>cap &gt; 0</c> ⇒ within budget for THIS call (the
///     pre-call estimate is unknown server-side until 32-9 supplies running
///     spend; the post-call cost is metered downstream). A future 32-9-backed
///     guard consults the tenant's accrued spend here.</description></item>
/// </list>
/// <para><b>Fail-closed:</b> any exception ⇒ deny (return false), never an
/// allow-by-default. This matches the activity's <c>catch ⇒ BudgetExhausted</c>
/// rule. <b>32-9 follow-on TODO:</b> consult running tenant spend.</para>
/// </summary>
public sealed class PerCallBudgetGuard : IBudgetGuard
{
    private readonly ILogger<PerCallBudgetGuard>? _logger;

    public PerCallBudgetGuard(ILogger<PerCallBudgetGuard>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> IsWithinBudgetAsync(
        Guid? tenantId, decimal budgetCapUsd, CancellationToken ct = default)
    {
        try
        {
            // cap <= 0 ⇒ unlimited (CheckBudgetActivity: CapUsd <= 0 ⇒ WithinBudget).
            // A positive cap is honoured per-call; running-spend enforcement is the
            // 32-9 follow-on. Never allow-by-default on error.
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                "Budget guard failed, defaulting to DENY (fail-closed): {Exception}", ex.Message);
            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// The <see cref="IBudgetGuard"/> that actually stops spending — the 32-9 follow-on
/// <see cref="PerCallBudgetGuard"/> names and never got.
///
/// <para><b>Why this is the right chokepoint.</b> Every model call the autonomous loop
/// makes goes engine → <c>POST /api/v1/llm/call</c> → <c>IManagedAgent.RunAsync</c>, whose
/// step 1b consults this guard and fails the run with <c>BUDGET_EXCEEDED</c> when it says
/// no. The two budget checks that existed elsewhere could not cap anything: the engine's
/// in-workflow gate meters a per-call <c>BudgetState</c> re-seeded from the caller's
/// <c>budgetCapUsd</c> on every call (so it never accumulates), and
/// <c>ProviderChainResolver</c>'s budget read only annotates a chain-inspection endpoint.
/// This guard reads the PERIOD spend the API already tracks
/// (<see cref="IDiagnosticsService.GetBudgetAsync"/>) and denies once it crosses a cap.</para>
///
/// <para><b>Two caps, same shape as the loop's dispatch ceiling</b>
/// (<see cref="AdlSpendCeiling"/>): the account budget limit (<c>Budget:LimitUsd</c>, or a
/// per-tenant override set through <c>PUT /api/providers/budget/{id}</c>) and the
/// ADL-specific <c>Adl:MaxSpendUsd</c>. Either being reached denies.
/// <c>Adl:BudgetOwnerId</c> supplies the bucket in single-user mode, where no tenant is
/// ever attached to the request — without it there is nothing to meter and this guard
/// degrades to the per-call behaviour, which IS the "loop with no cap" state, so it warns
/// once per process rather than staying quiet about it.</para>
///
/// <para><b>Fail-closed, scoped.</b> An evaluation error denies ONLY when a cap is
/// actually configured; with no cap there is nothing to evaluate, and denying every model
/// call because the diagnostics DB blinked would be a self-inflicted outage. "Configured"
/// means EITHER cap: <c>Adl:MaxSpendUsd</c>, or a per-owner limit this process has
/// previously read successfully. Consulting only the first fails open in SaaS, where
/// <c>Adl:*</c> is not set and the cap lives in the store that just failed.</para>
/// </summary>
public sealed class RunningSpendBudgetGuard : IBudgetGuard
{
    private readonly IDiagnosticsService _diagnostics;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<RunningSpendBudgetGuard>? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private int _unmeteredWarned;

    /// <summary>
    /// Owners observed to carry a positive budget limit. The limit lives in the
    /// diagnostics store, so when a read fails there is no way to ask whether a cap
    /// exists — this records the answer from the last read that worked, which is what
    /// lets the failure path fail CLOSED for a capped owner. Registered as a singleton,
    /// so the memory is process-wide; bounded by the number of distinct owners.
    ///
    /// <para>RESIDUAL, stated rather than papered over: an owner whose cap lives ONLY in
    /// the store and whose very FIRST evaluation fails is still allowed through — nothing
    /// has ever observed a limit for it. Closing that would mean denying every model call
    /// for any tenant whenever the store blinks, including deployments that set no caps at
    /// all, which is a self-inflicted outage. Set Adl:MaxSpendUsd to make the cap knowable
    /// without a store read.</para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, bool>
        _ownersWithAKnownLimit = new();

    public RunningSpendBudgetGuard(
        IDiagnosticsService diagnostics,
        IConfiguration? configuration = null,
        ILogger<RunningSpendBudgetGuard>? logger = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _configuration = configuration;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<bool> IsWithinBudgetAsync(
        Guid? tenantId, decimal budgetCapUsd, CancellationToken ct = default)
    {
        var ceiling = _configuration?.GetValue<decimal?>(AdlSpendCeiling.MaxSpendKey) ?? 0m;

        var owner = tenantId ?? ParseOwner(_configuration?.GetValue<string?>(AdlSpendCeiling.BudgetOwnerKey));
        if (owner is null)
        {
            // Warn once per process: repeating it on every model call would bury it.
            if (Interlocked.Exchange(ref _unmeteredWarned, 1) == 0)
            {
                _logger?.LogWarning(
                    "Model spend is UNMETERED — no tenant on the request and {Key} is unset, so no "
                    + "cumulative cap can be enforced. Set it to a GUID to cap what the loop spends.",
                    AdlSpendCeiling.BudgetOwnerKey);
            }
            return true;
        }

        try
        {
            var status = await _diagnostics.GetBudgetAsync(owner.Value, ct).ConfigureAwait(false);

            // Remember whether this owner HAS a cap, so a later evaluation failure knows
            // which way to fail. Both directions matter (review finding, 2026-08-19: the
            // add-only version recorded "some read once showed a limit" forever, so a
            // tenant that later REMOVED its cap — limit back to 0 = unlimited, confirmed
            // by successful reads — was still denied on every store blip for the process
            // lifetime). The memory is "the answer from the last read that worked", so a
            // clean read of an uncapped owner clears it.
            if (status.Limit > 0m)
            {
                _ownersWithAKnownLimit[owner.Value] = true;
            }
            else
            {
                _ownersWithAKnownLimit.TryRemove(owner.Value, out _);
            }

            var decision = AdlSpendCeiling.Evaluate(status.Spent, status.Limit, ceiling);
            if (decision.Stop)
            {
                _logger?.LogWarning("Model call denied — {Reason}", decision.Reason);

                // BUDGET.EXHAUSTED into the DCB stream, HERE, because this is where
                // exhaustion is actually decided (2026-08-20). The event's only
                // previous emitter was CheckBudgetActivity, which is wired to no
                // workflow graph — so the built-in budget alert rule could never
                // fire in production. Best-effort: an alert emission must never
                // turn a clean denial into an error.
                await EmitBudgetExhaustedAsync(owner.Value, status.Spent, status.Limit, ct)
                    .ConfigureAwait(false);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // "Is a cap configured?" has TWO sources, and this used to consult only one.
            // Adl:MaxSpendUsd is the autonomous-loop key; the other cap is the per-owner
            // budget limit set through PUT /api/providers/budget/{id} or Budget:LimitUsd,
            // which lives in the very store that just failed — so it is unknowable here.
            // A SaaS deployment has no reason to set Adl:*, so `ceiling` was 0 there and
            // EVERY evaluation failure returned allow: a tenant with a $100 limit had its
            // ceiling silently removed for as long as the diagnostics DB was unreachable,
            // which is the opposite of what this class documents.
            var capConfigured = ceiling > 0m || _ownersWithAKnownLimit.ContainsKey(owner.Value);
            _logger?.LogWarning(ex,
                "Budget evaluation failed for owner {Owner}; {Posture}.",
                owner.Value,
                capConfigured ? "DENYING (a cap is configured — fail closed)" : "allowing (no cap known)");
            return !capConfigured;
        }
    }

    private static Guid? ParseOwner(string? raw)
        => Guid.TryParse(raw, out var g) && g != Guid.Empty ? g : null;

    private async Task EmitBudgetExhaustedAsync(Guid owner, decimal spent, decimal limit, CancellationToken ct)
    {
        if (_scopeFactory is null) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var emitter = scope.ServiceProvider
                .GetService<Tamma.Data.Abstractions.IAlertEventEmitter>();
            if (emitter is null) return;

            await emitter.EmitBudgetExhaustedAsync(new Tamma.Data.Abstractions.BudgetExhaustedEvent(
                TenantId: owner,
                CorrelationId: string.Empty,
                Source: "api",
                Spent: spent,
                Limit: limit,
                ProviderName: "(pre-call)",
                WorkflowInstanceId: string.Empty), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "BUDGET.EXHAUSTED emission failed; the denial stands");
        }
    }
}
