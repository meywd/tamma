using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;

namespace Tamma.Api.Services.Integrations;

/// <summary>
/// Cabinet-backed <see cref="IJiraCredentialResolver"/> — the JIRA sibling of
/// git BYOK's <c>GitTokenResolver</c>. Reuses the proven tenant-scoped cabinet
/// read seam (<see cref="ITenantProviderKeyReader"/> → a <c>Scope=tenant</c> row
/// → active-version plaintext), which honours the "<c>ISecretStore</c> never
/// surfaces plaintext" boundary by reading through the backend directly.
///
/// <para>Resolution order (tenant→system→fail-loud):</para>
/// <list type="number">
///   <item>tenant BYOK bundle from <c>integration/jira/config</c> (only when a
///     tenant is present).</item>
///   <item>process <c>Jira:*</c> config — ONLY in single-user mode.</item>
///   <item>otherwise null ⇒ the mediation fails loud
///     (<c>JIRA_CREDENTIAL_UNAVAILABLE</c>).</item>
/// </list>
///
/// <para>A short-TTL in-process cache (keyed by tenant) mirrors the provider
/// resolver; the write endpoints call <see cref="Invalidate"/> after a mutation.
/// The plaintext token lives only on the returned credential — never logged.</para>
/// </summary>
public sealed class JiraCredentialResolver : IJiraCredentialResolver
{
    /// <summary>In-process cache TTL for a resolved tenant bundle.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(60);

    private readonly ITenantProviderKeyReader _cabinet;
    private readonly IConfiguration _configuration;
    private readonly ITammaModeProvider _mode;
    private readonly ILogger<JiraCredentialResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    public JiraCredentialResolver(
        ITenantProviderKeyReader cabinet,
        IConfiguration configuration,
        ITammaModeProvider mode,
        ILogger<JiraCredentialResolver> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? cacheTtl = null)
    {
        _cabinet = cabinet ?? throw new ArgumentNullException(nameof(cabinet));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
    }

    /// <inheritdoc />
    public async Task<JiraCredentialResolution?> ResolveAsync(
        Guid? tenantId, CancellationToken ct = default)
    {
        // ── tenant tier (BYOK) ──
        if (tenantId is { } tid)
        {
            var now = _timeProvider.GetUtcNow();
            if (_cache.TryGetValue(tid, out var cached) && cached.ExpiresAt > now)
            {
                return new JiraCredentialResolution(cached.Credential, IntegrationCredentialSource.Tenant);
            }

            var byok = await TryReadTenantAsync(tid, ct).ConfigureAwait(false);
            if (byok is not null)
            {
                _cache[tid] = new CacheEntry(byok, now.Add(_cacheTtl));
                return new JiraCredentialResolution(byok, IntegrationCredentialSource.Tenant);
            }
        }

        // ── system tier (single-user config) ──
        if (_mode.Mode == TammaMode.SingleUser)
        {
            var system = TryReadSystemConfig();
            if (system is not null)
            {
                return new JiraCredentialResolution(system, IntegrationCredentialSource.System);
            }
        }

        // ── fail-loud tier ──
        _logger.LogWarning(
            "JIRA credential unresolvable for tenant {TenantId} (mode {Mode}): no tenant BYOK bundle" +
            " and no single-user Jira:* config — failing loud (JIRA_CREDENTIAL_UNAVAILABLE).",
            tenantId, _mode.Mode);
        return null;
    }

    /// <inheritdoc />
    public void Invalidate(Guid? tenantId)
    {
        if (tenantId is { } tid)
        {
            _cache.TryRemove(tid, out _);
        }
    }

    private async Task<JiraCredential?> TryReadTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var row = await _cabinet
            .TryReadAsync(tenantId, IntegrationCabinetNames.JiraConfig, ct)
            .ConfigureAwait(false);
        // A malformed/partial stored bundle deserializes to null → treated as
        // absent (never a partial credential).
        return row is null ? null : JiraCredentialCodec.TryDeserialize(row.Plaintext);
    }

    private JiraCredential? TryReadSystemConfig()
    {
        var baseUrl = _configuration["Jira:BaseUrl"];
        var email = _configuration["Jira:Email"];
        var apiToken = _configuration["Jira:ApiToken"];
        if (string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(apiToken))
        {
            return null;
        }
        return new JiraCredential(baseUrl.Trim(), email.Trim(), apiToken);
    }

    private readonly record struct CacheEntry(JiraCredential Credential, DateTimeOffset ExpiresAt);
}
