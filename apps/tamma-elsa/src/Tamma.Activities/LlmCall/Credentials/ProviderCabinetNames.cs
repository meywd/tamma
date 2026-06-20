namespace Tamma.Activities.LlmCall.Credentials;

/// <summary>
/// Canonical cabinet names for provider API keys, kept as one source of truth
/// so the BYOK management API (Story 32-3 AC7) and the resolver (AC1/AC2)
/// cannot drift apart on the slug.
///
/// <para><b>BYOK</b> (tenant-scoped) keys live under
/// <c>provider/&lt;name&gt;/api-key</c>. <b>Platform</b> keys reuse the
/// Story 29-9 stopgap cabinet names (<c>&lt;name&gt;/api-key</c>) so the
/// platform leg goes through the one existing platform-key source of truth
/// (<c>IRuntimeSecretResolver</c>) — see AC2.</para>
/// </summary>
public static class ProviderCabinetNames
{
    /// <summary>
    /// Tenant-scoped BYOK cabinet name for <paramref name="provider"/>, e.g.
    /// <c>provider/anthropic/api-key</c>. <paramref name="provider"/> is
    /// expected to already be normalized (lower-invariant, allowlist-checked).
    /// </summary>
    public static string Byok(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return $"provider/{provider.Trim().ToLowerInvariant()}/api-key";
    }

    /// <summary>
    /// Platform cabinet name for <paramref name="provider"/>, e.g.
    /// <c>anthropic/api-key</c>. Matches the Story 29-9
    /// <c>StopgapSecretMap.Platform*ApiKey</c> constants where they exist
    /// (anthropic); providers with no stopgap entry (openai, openrouter) simply
    /// have no platform cabinet row yet → the platform leg returns null and the
    /// resolver fails closed / loud, which is correct.
    /// </summary>
    public static string Platform(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return $"{provider.Trim().ToLowerInvariant()}/api-key";
    }

    private const string ByokPrefix = "provider/";
    private const string ByokSuffix = "/api-key";

    /// <summary>
    /// Reverse of <see cref="Byok"/>: extract the provider name from a
    /// <c>provider/&lt;name&gt;/api-key</c> cabinet name, or null when the name
    /// is not a BYOK provider-key slug.
    /// </summary>
    public static string? TryParse(string? cabinetName)
    {
        if (string.IsNullOrWhiteSpace(cabinetName)
            || !cabinetName.StartsWith(ByokPrefix, StringComparison.Ordinal)
            || !cabinetName.EndsWith(ByokSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var inner = cabinetName.Substring(
            ByokPrefix.Length,
            cabinetName.Length - ByokPrefix.Length - ByokSuffix.Length);

        return string.IsNullOrWhiteSpace(inner) ? null : inner;
    }
}
