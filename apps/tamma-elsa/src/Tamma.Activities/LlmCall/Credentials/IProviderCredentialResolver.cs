namespace Tamma.Activities.LlmCall.Credentials;

/// <summary>
/// Where a resolved provider API key came from. Consumed by Epics 34/35
/// (pricing/billing) and 32-9/32-10 (usage/benchmarking) as the
/// <c>credentialSource</c> tag — never the key itself.
/// </summary>
public enum CredentialSource
{
    /// <summary>The tenant's own bring-your-own-key, read from the Epic 29 cabinet.</summary>
    Byok,

    /// <summary>The platform-provided key, read through the cabinet runtime path.</summary>
    Platform,
}

/// <summary>
/// Resolved provider credential. <see cref="ApiKey"/> is plaintext for the
/// immediate outbound HTTP call ONLY — it MUST NEVER be serialized into a
/// <c>DomainEvent</c>, a <c>ProviderAttemptDiagnostic</c>, an exception
/// message, or any log line (Story 32-3 AC5, redaction test is the gate).
/// Only <see cref="ToTag"/> — the tag-safe projection — ever reaches an
/// event / diagnostic / log.
/// </summary>
/// <param name="ApiKey">Plaintext key. Request-scoped; dropped after the
/// HTTP header is set.</param>
/// <param name="Source">BYOK vs platform.</param>
/// <param name="SecretRefStorageKey">Stable storage-key for the secret
/// (e.g. <c>tenant:&lt;guid&gt;:provider/anthropic/api-key</c> or
/// <c>platform:anthropic/api-key</c>). Safe to log / tag.</param>
/// <param name="VersionNumber">Active cabinet version number for BYOK; null
/// for the platform leg.</param>
public sealed record ProviderCredential(
    string ApiKey,
    CredentialSource Source,
    string? SecretRefStorageKey,
    int? VersionNumber)
{
    /// <summary>
    /// Tag-safe projection. NEVER includes <see cref="ApiKey"/>. This is the
    /// only thing handed to diagnostics / DCB events / logs.
    /// </summary>
    public ProviderCredentialTag ToTag() => new(
        Source.ToString().ToLowerInvariant(),
        SecretRefStorageKey,
        VersionNumber);
}

/// <summary>
/// Tag-safe projection of a <see cref="ProviderCredential"/> — explicitly
/// free of any plaintext. Serialized into diagnostics / DCB-event tags.
/// </summary>
/// <param name="Source">"byok" | "platform".</param>
/// <param name="SecretRef">Storage-key for the secret (never the value).</param>
/// <param name="Version">Active version number (BYOK) or null (platform).</param>
public sealed record ProviderCredentialTag(
    string Source,
    string? SecretRef,
    int? Version);

/// <summary>
/// Resolves the API key for an LLM call from the per-tenant BYOK cabinet
/// (Epic 29), falling back to the platform-provided key. The single seam that
/// owns ALL "where does the provider key come from at execution time?" logic
/// (Story 32-3 — canonical ownership).
///
/// <para>The interface lives in <c>Tamma.Activities</c> so
/// <see cref="CallLlmInlineActivity"/> can depend on it without referencing
/// <c>Tamma.Api</c> (the cabinet-backed implementation,
/// <c>DefaultProviderCredentialResolver</c>, lives in <c>Tamma.Api</c> where
/// the Epic 29 services are reachable and is wired in the API-hosted process).
/// When the resolver is absent (e.g. the standalone Elsa engine with no
/// cabinet) the activity falls back to the existing platform/config path —
/// the single-user behaviour AC6 prescribes.</para>
/// </summary>
public interface IProviderCredentialResolver
{
    /// <summary>
    /// Resolve the API key for <c>(tenantId, providerName)</c>.
    /// <paramref name="tenantId"/> == null ⇒ single-user / platform scope.
    /// Order: tenant BYOK cabinet key → platform-provided key (gated by the
    /// fallback policy). Fail-closed in SaaS when neither is available/allowed:
    /// emits <c>AGENT.CREDENTIAL.DENIED</c> and throws
    /// <c>TammaError("PROVIDER_CREDENTIAL_UNAVAILABLE", retryable:false,
    /// severity:High)</c> — NEVER a silent wrong/empty key.
    /// </summary>
    Task<ProviderCredential> ResolveAsync(
        Guid? tenantId, string providerName, CancellationToken ct = default);

    /// <summary>
    /// Invalidate the cached BYOK entry for <c>(tenantId, provider)</c>.
    /// Called on register / rotate / remove (AC7) and on a matching
    /// <c>SECRET.ROTATE.ACTIVATED</c> (AC9).
    /// </summary>
    void Invalidate(Guid? tenantId, string providerName);
}
