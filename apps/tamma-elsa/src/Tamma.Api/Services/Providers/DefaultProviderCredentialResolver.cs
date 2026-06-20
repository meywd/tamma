using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Core;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Story 32-3 — the canonical BYOK→platform provider-credential resolver.
///
/// <para>Resolution order for <c>(tenantId, provider)</c>:</para>
/// <list type="number">
///   <item><description>Tenant BYOK cabinet key
///     (<see cref="ITenantProviderKeyReader"/>) — only when a tenant is
///     present. In-process cached by <c>(tenantId, provider)</c> for a short
///     TTL (AC9).</description></item>
///   <item><description>Platform-provided key via
///     <see cref="IRuntimeSecretResolver"/> (the one platform-key source of
///     truth, AC2), gated by <see cref="IPlatformFallbackPolicy"/>.</description></item>
///   <item><description>Otherwise fail-closed: emit
///     <c>AGENT.CREDENTIAL.DENIED</c> and throw
///     <c>TammaError("PROVIDER_CREDENTIAL_UNAVAILABLE")</c> — never a silent
///     wrong/empty key (AC6).</description></item>
/// </list>
///
/// <para><b>Credential safety (AC5):</b> the plaintext key lives only on the
/// returned <see cref="ProviderCredential.ApiKey"/> and is never serialized
/// into a <c>DomainEvent</c>, log line, or exception. Only the tag-safe
/// projection (<see cref="ProviderCredential.ToTag"/>) reaches events / logs.</para>
/// </summary>
public sealed class DefaultProviderCredentialResolver : IProviderCredentialResolver
{
    /// <summary>Default cache TTL — 60s, matching <see cref="RuntimeSecretResolver.DefaultCacheTtl"/>.</summary>
    public static readonly TimeSpan DefaultCacheTtl = RuntimeSecretResolver.DefaultCacheTtl;

    private readonly ITenantProviderKeyReader _byokReader;
    private readonly IRuntimeSecretResolver? _platformKeys;
    private readonly IPlatformFallbackPolicy _fallbackPolicy;
    private readonly IEventRepository _events;
    private readonly ITammaModeProvider _mode;
    private readonly ProviderAllowlist _allowlist;
    private readonly ILogger<DefaultProviderCredentialResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;

    private readonly ConcurrentDictionary<(Guid TenantId, string Provider), CacheEntry> _cache = new();

    public DefaultProviderCredentialResolver(
        ITenantProviderKeyReader byokReader,
        IRuntimeSecretResolver? platformKeys,
        IPlatformFallbackPolicy fallbackPolicy,
        IEventRepository events,
        ITammaModeProvider mode,
        ProviderAllowlist allowlist,
        ILogger<DefaultProviderCredentialResolver> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(byokReader);
        ArgumentNullException.ThrowIfNull(fallbackPolicy);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(logger);

        _byokReader = byokReader;
        _platformKeys = platformKeys;
        _fallbackPolicy = fallbackPolicy;
        _events = events;
        _mode = mode;
        _allowlist = allowlist;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
    }

    /// <inheritdoc />
    public async Task<ProviderCredential> ResolveAsync(
        Guid? tenantId, string providerName, CancellationToken ct = default)
    {
        var provider = Normalize(providerName);

        // 1) BYOK — only when a tenant is present (SaaS). Single-user
        //    (tenantId == null) has no separate BYOK layer.
        if (tenantId is { } tid)
        {
            var key = (tid, provider);
            var now = _timeProvider.GetUtcNow();
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            {
                _logger.LogDebug(
                    "BYOK cache hit for tenant {TenantId} provider {Provider}.",
                    tid, provider);
                return await EmitResolvedAsync(tenantId, provider, cached.Credential, ct)
                    .ConfigureAwait(false);
            }

            var byok = await _byokReader
                .TryReadAsync(tid, ProviderCabinetNames.Byok(provider), ct)
                .ConfigureAwait(false);
            if (byok is not null)
            {
                var cred = new ProviderCredential(
                    byok.Plaintext,
                    CredentialSource.Byok,
                    $"tenant:{tid}:{ProviderCabinetNames.Byok(provider)}",
                    byok.VersionNumber);
                _cache[key] = new CacheEntry(cred, now.Add(_cacheTtl));
                _logger.LogDebug(
                    "BYOK resolved for tenant {TenantId} provider {Provider} (version {Version}).",
                    tid, provider, byok.VersionNumber);
                return await EmitResolvedAsync(tenantId, provider, cred, ct).ConfigureAwait(false);
            }

            _logger.LogDebug(
                "No BYOK key for tenant {TenantId} provider {Provider}; evaluating platform fallback.",
                tid, provider);
        }

        // 2) Platform fallback — gated by the policy.
        if (_fallbackPolicy.IsPlatformFallbackAllowed(tenantId, provider))
        {
            var platformKey = await TryReadPlatformKeyAsync(provider, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(platformKey))
            {
                var cred = new ProviderCredential(
                    platformKey,
                    CredentialSource.Platform,
                    $"platform:{ProviderCabinetNames.Platform(provider)}",
                    VersionNumber: null);
                _logger.LogDebug(
                    "Platform key resolved for provider {Provider} (tenant {TenantId}).",
                    provider, tenantId);
                return await EmitResolvedAsync(tenantId, provider, cred, ct).ConfigureAwait(false);
            }

            _logger.LogWarning(
                "Platform fallback allowed for provider {Provider} (tenant {TenantId}) " +
                "but the platform key is unset.", provider, tenantId);
        }

        // 3) Fail-closed — never a wrong/empty key.
        var reason = tenantId is null
            ? "platform_key_unset"
            : "no_byok_and_platform_fallback_unavailable";
        await EmitDeniedAsync(tenantId, provider, reason, ct).ConfigureAwait(false);

        _logger.LogError(
            "Provider credential unavailable: provider={Provider}, tenant={TenantId}, " +
            "reason={Reason}, mode={Mode}.",
            provider, tenantId, reason, _mode.Mode);

        throw new TammaError(
            "PROVIDER_CREDENTIAL_UNAVAILABLE",
            $"No usable credential for provider '{provider}'" +
            (tenantId is null
                ? " (platform key unset)."
                : " (no tenant BYOK key and platform fallback unavailable)."),
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
        if (tenantId is not { } tid)
        {
            return; // single-user / platform has no per-tenant BYOK cache entry
        }
        var provider = Normalize(providerName);
        _cache.TryRemove((tid, provider), out _);
        _logger.LogInformation(
            "BYOK cache invalidated for tenant {TenantId} provider {Provider}.",
            tid, provider);
    }

    private async Task<string?> TryReadPlatformKeyAsync(string provider, CancellationToken ct)
    {
        if (_platformKeys is null)
        {
            return null;
        }

        try
        {
            return await _platformKeys
                .GetAsync(ProviderCabinetNames.Platform(provider), ct)
                .ConfigureAwait(false);
        }
        catch (MissingSecretException)
        {
            // Story 29-10 fail-fast mode: no cabinet row. Treat as unset for the
            // platform leg — the resolver's own fail-closed path handles it.
            return null;
        }
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

    private async Task<ProviderCredential> EmitResolvedAsync(
        Guid? tenantId, string provider, ProviderCredential cred, CancellationToken ct)
    {
        var tag = cred.ToTag(); // tag-safe — NEVER the ApiKey
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
                mode = _mode.Mode.ToString(),
            }),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = "1.0.0",
                eventSource = "system",
            }),
            // Data carries only the version — NEVER the key.
            Data = JsonSerializer.Serialize(new { version = tag.Version }),
            CreatedAt = DateTime.UtcNow,
        }).ConfigureAwait(false);

        return cred;
    }

    private async Task EmitDeniedAsync(
        Guid? tenantId, string provider, string reason, CancellationToken ct)
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
                mode = _mode.Mode.ToString(),
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

    private readonly record struct CacheEntry(ProviderCredential Credential, DateTimeOffset ExpiresAt);
}
