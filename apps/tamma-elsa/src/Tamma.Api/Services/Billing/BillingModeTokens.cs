using Tamma.Core.Enums;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-2 — the canonical <c>billing_mode</c> wire tokens. These are the
/// EXACT tokens <see cref="MetricBillingModeExtensions.ToToken"/> emits, Story
/// 32-3's <c>CredentialSource.ToTag()</c> emits, and the analytics
/// <c>ResolveCostBasis</c> reader keys off — so a value stamped on a usage
/// event / diagnostic resolves identically on every reader (AC11).
/// </summary>
public static class BillingModeTokens
{
    public const string Byok = MetricBillingModeExtensions.ByokToken;         // "byok"
    public const string Platform = MetricBillingModeExtensions.PlatformToken; // "platform"

    /// <summary>True iff <paramref name="token"/> is exactly <c>byok</c> or <c>platform</c>.</summary>
    public static bool IsValid(string? token) =>
        token is Byok or Platform;
}
