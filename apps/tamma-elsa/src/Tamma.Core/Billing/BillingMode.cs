namespace Tamma.Core.Billing;

/// <summary>
/// Story 35-1 — how a tenant's AI usage is billed. Lives in
/// <c>Tamma.Core</c> so both the data layer (text column on
/// <c>billing_customers</c>) and the API layer (the billing provider) reference
/// a single enum.
///
/// <para>This story only stores and defaults the flag. The metering /
/// token-markup-suppression behaviour that reads it is Story 35-3's scope — a
/// BYOK tenant must not be charged the platform token markup, but that logic is
/// NOT implemented here.</para>
/// </summary>
public enum BillingMode
{
    /// <summary>
    /// Default — Tamma supplies the AI provider credentials and bills the
    /// tenant for metered token usage (with the platform markup).
    /// </summary>
    PlatformProvided,

    /// <summary>
    /// Bring-your-own-key — the tenant supplies their own AI provider
    /// credentials. Recorded so Story 35-3 metering can suppress the token
    /// markup. (No suppression logic in this story.)
    /// </summary>
    Byok,
}
