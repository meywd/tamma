namespace Tamma.Activities.ADL;

/// <summary>
/// The loop-level SPEND CEILING decision — pure, so the fail-closed rules are unit
/// testable without an Elsa runtime or an HTTP client.
///
/// <para><b>Two independent caps.</b> The per-tenant budget already tracked by the API
/// (<c>BudgetStatus.Limit</c>, from <c>Budget:LimitUsd</c> / <c>PUT /api/providers/budget/{id}</c>)
/// and an ADL-specific ceiling (<c>Adl:MaxSpendUsd</c>, or the orchestrator config's
/// <c>limits.dailyBudgetUsd</c>). Either being exceeded stops dispatch. The second exists
/// because the tenant limit defaults to 0 ("unlimited") in a single-user deployment, which
/// is exactly the deployment the autonomous loop runs in — so without it the loop has no
/// cap at all.</para>
///
/// <para><b>Fail-closed, but only where a cap was asked for.</b> When no cap is configured
/// there is nothing to enforce and an unreadable budget is not an error. When a cap IS
/// configured and the spend cannot be read, dispatch stops for that tick — cheap, because
/// the orchestrator re-dispatches itself regardless of this edge, so a transient API blip
/// costs one cycle rather than the loop (the invariant lane A protects).</para>
/// </summary>
public static class AdlSpendCeiling
{
    /// <summary>Config key for the ADL-specific ceiling (USD, per budget period). 0 = off.</summary>
    public const string MaxSpendKey = "Adl:MaxSpendUsd";

    /// <summary>
    /// Config key naming the budget bucket to meter against when the workflow carries no
    /// tenant (single-user mode). Must be a GUID — the budget API keys on one.
    /// </summary>
    public const string BudgetOwnerKey = "Adl:BudgetOwnerId";

    /// <summary>The outcome of a ceiling evaluation.</summary>
    /// <param name="Stop">True when dispatch must take the Stop edge.</param>
    /// <param name="Reason">Operator-facing reason; empty when <paramref name="Stop"/> is false.</param>
    public readonly record struct Decision(bool Stop, string Reason);

    /// <summary>
    /// Decide from an observed spend. <paramref name="tenantLimitUsd"/> is
    /// <c>BudgetStatus.Limit</c> (0 = unlimited); <paramref name="adlCeilingUsd"/> is the
    /// ADL-specific cap (0 = off).
    /// </summary>
    public static Decision Evaluate(decimal spentUsd, decimal tenantLimitUsd, decimal adlCeilingUsd)
    {
        if (adlCeilingUsd > 0m && spentUsd >= adlCeilingUsd)
        {
            return new Decision(true,
                $"spend ceiling reached (${spentUsd:F2} of ${adlCeilingUsd:F2} {MaxSpendKey})");
        }

        if (tenantLimitUsd > 0m && spentUsd >= tenantLimitUsd)
        {
            return new Decision(true,
                $"budget exhausted (${spentUsd:F2} of ${tenantLimitUsd:F2} budget limit)");
        }

        return new Decision(false, string.Empty);
    }

    /// <summary>
    /// Decide when the spend could NOT be read. Stops only if a cap was configured —
    /// see the fail-closed note on the type.
    /// </summary>
    public static Decision EvaluateUnknown(bool ceilingConfigured, string detail)
        => ceilingConfigured
            ? new Decision(true, $"spend unknown while a ceiling is configured — {detail}")
            : new Decision(false, string.Empty);
}
