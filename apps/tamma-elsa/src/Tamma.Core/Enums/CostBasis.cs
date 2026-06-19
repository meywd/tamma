namespace Tamma.Core.Enums;

/// <summary>
/// Story 36-1 — discriminates whether the LLM/agent cost on an analytics
/// fact row was paid against the tenant's own provider key (BYOK — Tamma
/// never bills it) or against a Tamma-platform key (Tamma fronts the cost and
/// may bill the tenant via the row's <c>PlatformBilledUsd</c> measure).
///
/// <para>Persisted as lowercase text (<c>byok</c> / <c>platform</c>) by the
/// analytics EF model config (mirrors the
/// <see cref="MentorshipState"/>/<c>SessionStatus</c>
/// <c>HasConversion&lt;string&gt;()</c> enum-to-text precedent, but
/// lower-cased so the discriminator is uniform in ad-hoc SQL). The ordinals
/// are part of the contract — do not renumber.</para>
/// </summary>
public enum CostBasis
{
    /// <summary>Cost paid against the tenant's own provider key — Tamma never bills it.</summary>
    Byok = 0,

    /// <summary>Cost fronted by a Tamma-platform key — billable to the tenant via PlatformBilledUsd.</summary>
    Platform = 1,
}
