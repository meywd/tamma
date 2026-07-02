namespace Tamma.Core.Enums;

/// <summary>
/// Story 34-4 — the direction of a plan change, classified from the recurring
/// price of the new plan version vs the prior one. Emitted as a tag on
/// <c>TENANT.PLAN.CHANGED</c> (lower-cased) so Billing / analytics can tell an
/// upgrade from a downgrade without re-deriving it. A re-assignment to the same
/// <c>(PlanId, PlanVersion)</c> — or an equal-priced move — is
/// <see cref="Lateral"/>.
/// </summary>
public enum PlanChangeDirection
{
    /// <summary>New plan costs more than the prior (or the first-ever paid assignment).</summary>
    Upgrade,

    /// <summary>New plan costs less than the prior.</summary>
    Downgrade,

    /// <summary>Same price (incl. the idempotent same-version re-assign no-op).</summary>
    Lateral,
}
