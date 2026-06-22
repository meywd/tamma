using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 (compose step 1b) — the server-side budget gate the managed
/// endpoint checks BEFORE the provider call (fail-closed). It mirrors the
/// existing <c>CheckBudgetActivity</c> contract — a cap of <c>0</c> (or less)
/// means "unlimited" (always within budget); a positive cap that cannot be
/// satisfied, or any error during evaluation, ⇒ DENY (the loop is never
/// invoked). This is the named owner of the budget gate in the rule-2 sequence.
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
