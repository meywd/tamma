using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Security;
using Tamma.Core;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Activities.LlmCall.Credentials;

/// <summary>
/// Story 32-3 — config-backed <see cref="IProviderCredentialResolver"/> for the
/// standalone Elsa workflow host (<c>Tamma.ElsaServer</c>).
///
/// <para><b>Why this exists.</b> <see cref="CallLlmInlineActivity"/> executes in
/// the <c>Tamma.ElsaServer</c> process, which deliberately does NOT reference
/// <c>Tamma.Api</c> (see <c>Tamma.ElsaServer/Program.cs</c> agent-dispatch note).
/// The cabinet-backed <c>DefaultProviderCredentialResolver</c> and its Epic 29
/// secret-cabinet dependencies (<c>SecretsDbContext</c>,
/// <c>ISecretStoreBackend</c>, <c>IRuntimeSecretResolver</c>, ...) all live in
/// <c>Tamma.Api</c> and are not reachable here. Without a resolver registered in
/// this host the activity bound a <c>null</c> resolver and sent an empty
/// <c>ApiKey</c> — a hard regression to no-auth. This resolver restores the
/// platform-key path (AC12 backward-compat) while keeping the
/// <see cref="IProviderCredentialResolver"/> seam intact, so the key read lives
/// in a resolver (not back in the activity, which AC2 forbids).</para>
///
/// <para><b>Resolution order</b> for <c>(tenantId, provider)</c>:</para>
/// <list type="number">
///   <item><description>Platform key from configuration — gated by
///     <see cref="IPlatformFallbackPolicy"/>. Read from
///     <c>LlmProviders:&lt;provider&gt;:ApiKey</c> first, then the legacy
///     per-provider slot (e.g. <c>Anthropic:ApiKey</c>) for back-compat.
///     Returns <see cref="CredentialSource.Platform"/>.</description></item>
///   <item><description>Otherwise fail-closed: emit
///     <c>AGENT.CREDENTIAL.DENIED</c> (when an event sink is available) and
///     throw <c>TammaError("PROVIDER_CREDENTIAL_UNAVAILABLE")</c> — NEVER a
///     silent empty/wrong key (AC6).</description></item>
/// </list>
///
/// <para><b>No BYOK layer.</b> Tenant BYOK keys live in the Epic 29 cabinet,
/// which this host cannot reach. A tenant id is accepted (so the seam is
/// identical) but only the platform leg applies — this is the single-user /
/// standalone-engine behaviour. SaaS BYOK resolution is owned by the
/// API-hosted <c>DefaultProviderCredentialResolver</c>.</para>
///
/// <para><b>Credential safety (AC5):</b> the plaintext key lives only on the
/// returned <see cref="ProviderCredential.ApiKey"/>; only the tag-safe
/// projection (<see cref="ProviderCredential.ToTag"/>) ever reaches an event or
/// a log line.</para>
/// </summary>
public sealed class ConfigPlatformProviderCredentialResolver : IProviderCredentialResolver
{
    private readonly IConfiguration _configuration;
    private readonly IPlatformFallbackPolicy _fallbackPolicy;
    private readonly ProviderAllowlist _allowlist;
    private readonly ILogger<ConfigPlatformProviderCredentialResolver> _logger;
    private readonly IEventRepository? _events;

    public ConfigPlatformProviderCredentialResolver(
        IConfiguration configuration,
        IPlatformFallbackPolicy fallbackPolicy,
        ProviderAllowlist allowlist,
        ILogger<ConfigPlatformProviderCredentialResolver> logger,
        IEventRepository? events = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(fallbackPolicy);
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(logger);

        _configuration = configuration;
        _fallbackPolicy = fallbackPolicy;
        _allowlist = allowlist;
        _logger = logger;
        _events = events;
    }

    /// <inheritdoc />
    public async Task<ProviderCredential> ResolveAsync(
        Guid? tenantId, string providerName, CancellationToken ct = default)
    {
        var provider = Normalize(providerName);

        // Platform key — gated by the policy (single-user always allowed).
        if (_fallbackPolicy.IsPlatformFallbackAllowed(tenantId, provider))
        {
            var platformKey = ReadPlatformKey(provider);
            if (!string.IsNullOrWhiteSpace(platformKey))
            {
                var cred = new ProviderCredential(
                    platformKey,
                    CredentialSource.Platform,
                    $"platform:{ProviderCabinetNames.Platform(provider)}",
                    VersionNumber: null);
                _logger.LogDebug(
                    "Platform key resolved from config for provider {Provider} (tenant {TenantId}).",
                    provider, tenantId);
                await EmitResolvedAsync(tenantId, provider, cred, ct).ConfigureAwait(false);
                return cred;
            }

            _logger.LogWarning(
                "Platform fallback allowed for provider {Provider} (tenant {TenantId}) " +
                "but no platform key is configured (LlmProviders:{Provider}:ApiKey).",
                provider, tenantId, provider);
        }

        // Fail-closed — never a wrong/empty key.
        var reason = tenantId is null
            ? "platform_key_unset"
            : "no_byok_in_engine_host_and_platform_key_unavailable";
        await EmitDeniedAsync(tenantId, provider, reason, ct).ConfigureAwait(false);

        _logger.LogError(
            "Provider credential unavailable in engine host: provider={Provider}, " +
            "tenant={TenantId}, reason={Reason}.",
            provider, tenantId, reason);

        throw new TammaError(
            "PROVIDER_CREDENTIAL_UNAVAILABLE",
            $"No usable credential for provider '{provider}' " +
            "(platform key unset in the workflow engine host).",
            new Dictionary<string, object?>
            {
                ["tenantId"] = tenantId,
                ["provider"] = provider,
                ["reason"] = reason,
            },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <inheritdoc />
    public void Invalidate(Guid? tenantId, string providerName)
    {
        // Config-backed resolver holds no cache — nothing to invalidate.
    }

    /// <summary>
    /// Read the platform key for <paramref name="provider"/> from configuration.
    /// Primary slot: <c>LlmProviders:&lt;provider&gt;:ApiKey</c> (the existing
    /// engine appsettings shape). Falls back to the legacy per-provider slot
    /// (<c>Anthropic:ApiKey</c> / <c>OpenAI:ApiKey</c> / <c>OpenRouter:ApiKey</c>)
    /// that pre-32-3 <c>LoadProviderConfig</c> used to read — so a deployment
    /// that supplied platform keys the old way still authenticates (AC12).
    /// </summary>
    private string? ReadPlatformKey(string provider)
    {
        var fromSection = _configuration[$"LlmProviders:{provider}:ApiKey"];
        if (!string.IsNullOrWhiteSpace(fromSection))
        {
            return fromSection;
        }

        // Legacy per-provider slots (the exact keys 32-3 stopped reading in the
        // activity). Only the providers that had a legacy slot are mapped.
        var legacyKey = provider switch
        {
            "anthropic" => "Anthropic:ApiKey",
            "openai" => "OpenAI:ApiKey",
            "openrouter" => "OpenRouter:ApiKey",
            _ => null,
        };
        if (legacyKey is not null)
        {
            var legacy = _configuration[legacyKey];
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                return legacy;
            }
        }

        return null;
    }

    private string Normalize(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new TammaError(
                "PROVIDER_CREDENTIAL_UNAVAILABLE",
                "Provider name must be non-empty.",
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        var provider = providerName.Trim().ToLowerInvariant();
        if (!_allowlist.IsAllowed(provider))
        {
            throw new TammaError(
                "PROVIDER_CREDENTIAL_UNAVAILABLE",
                $"Provider '{provider}' is not in the allowlist.",
                new Dictionary<string, object?> { ["provider"] = provider },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
        return provider;
    }

    private async Task EmitResolvedAsync(
        Guid? tenantId, string provider, ProviderCredential cred, CancellationToken ct)
    {
        if (_events is null)
        {
            return; // engine host may run without a durable event sink
        }

        var tag = cred.ToTag(); // tag-safe — NEVER the ApiKey
        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "AGENT.CREDENTIAL_RESOLVED.SUCCESS",
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = tenantId?.ToString(),
                    provider,
                    source = tag.Source,
                    secretRef = tag.SecretRef,
                    host = "elsa-engine",
                }),
                Metadata = JsonSerializer.Serialize(new
                {
                    workflowVersion = "1.0.0",
                    eventSource = "system",
                }),
                Data = JsonSerializer.Serialize(new { version = tag.Version }),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit-emit failure must not block a valid resolution.
            _logger.LogWarning(ex,
                "Failed to emit AGENT.CREDENTIAL_RESOLVED.SUCCESS for provider {Provider}.",
                provider);
        }
    }

    private async Task EmitDeniedAsync(
        Guid? tenantId, string provider, string reason, CancellationToken ct)
    {
        if (_events is null)
        {
            return;
        }

        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "AGENT.CREDENTIAL.DENIED",
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = tenantId?.ToString(),
                    provider,
                    reason,
                    host = "elsa-engine",
                }),
                Metadata = JsonSerializer.Serialize(new
                {
                    workflowVersion = "1.0.0",
                    eventSource = "system",
                }),
                Data = "{}",
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit AGENT.CREDENTIAL.DENIED for provider {Provider}.",
                provider);
        }
    }
}
