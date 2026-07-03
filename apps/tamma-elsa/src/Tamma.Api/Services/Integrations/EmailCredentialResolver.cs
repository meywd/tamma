using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;

namespace Tamma.Api.Services.Integrations;

/// <summary>
/// Cabinet-backed <see cref="IEmailCredentialResolver"/> — the email sibling of
/// git BYOK's <c>GitTokenResolver</c>. Reuses the tenant-scoped cabinet read seam
/// (<see cref="ITenantProviderKeyReader"/>) and the single-user <c>Email:*</c>
/// config as the system tier.
///
/// <para>Resolution order (tenant→system→fail-loud):</para>
/// <list type="number">
///   <item>tenant BYOK bundle from <c>integration/email/config</c> (only when a
///     tenant is present).</item>
///   <item>process <c>Email:*</c> config — ONLY in single-user mode.</item>
///   <item>otherwise null ⇒ the mediation fails loud
///     (<c>EMAIL_CREDENTIAL_UNAVAILABLE</c>).</item>
/// </list>
/// </summary>
public sealed class EmailCredentialResolver : IEmailCredentialResolver
{
    /// <summary>
    /// In-process cache TTL for a resolved tenant bundle. NOTE (same as provider
    /// BYOK): a positive entry lives up to this TTL and <see cref="Invalidate"/> only
    /// evicts the LOCAL process cache — in a multi-replica deployment a credential
    /// revoked/rotated on one replica may keep resolving on another for up to this
    /// window before its own entry expires. 60s bounds that revocation lag; we do NOT
    /// over-engineer distributed invalidation here.
    /// </summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(60);

    private readonly ITenantProviderKeyReader _cabinet;
    private readonly IConfiguration _configuration;
    private readonly ITammaModeProvider _mode;
    private readonly ILogger<EmailCredentialResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheTtl;

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    public EmailCredentialResolver(
        ITenantProviderKeyReader cabinet,
        IConfiguration configuration,
        ITammaModeProvider mode,
        ILogger<EmailCredentialResolver> logger,
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
    public async Task<EmailCredentialResolution?> ResolveAsync(
        Guid? tenantId, CancellationToken ct = default)
    {
        // ── tenant tier (BYOK) ──
        if (tenantId is { } tid)
        {
            var now = _timeProvider.GetUtcNow();
            if (_cache.TryGetValue(tid, out var cached) && cached.ExpiresAt > now)
            {
                return new EmailCredentialResolution(cached.Credential, IntegrationCredentialSource.Tenant);
            }

            var byok = await TryReadTenantAsync(tid, ct).ConfigureAwait(false);
            if (byok is not null)
            {
                _cache[tid] = new CacheEntry(byok, now.Add(_cacheTtl));
                return new EmailCredentialResolution(byok, IntegrationCredentialSource.Tenant);
            }
        }

        // ── system tier (single-user config) ──
        if (_mode.Mode == TammaMode.SingleUser)
        {
            var system = TryReadSystemConfig();
            if (system is not null)
            {
                return new EmailCredentialResolution(system, IntegrationCredentialSource.System);
            }
        }

        // ── fail-loud tier ──
        _logger.LogWarning(
            "Email credential unresolvable for tenant {TenantId} (mode {Mode}): no tenant BYOK bundle" +
            " and no single-user Email:* config — failing loud (EMAIL_CREDENTIAL_UNAVAILABLE).",
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

    private async Task<EmailCredential?> TryReadTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var row = await _cabinet
            .TryReadAsync(tenantId, IntegrationCabinetNames.EmailConfig, ct)
            .ConfigureAwait(false);
        return row is null ? null : EmailCredentialCodec.TryDeserialize(row.Plaintext);
    }

    private EmailCredential? TryReadSystemConfig()
    {
        var from = _configuration["Email:From"];
        if (string.IsNullOrWhiteSpace(from))
        {
            // The single-user transports (SmtpEmailService / ResendEmailService)
            // both REQUIRE a from address; without it there is nothing to resolve.
            return null;
        }

        var transport = (_configuration["Email:Provider"] ?? EmailCredential.TransportSmtp)
            .Trim().ToLowerInvariant();

        var credential = transport switch
        {
            EmailCredential.TransportResend => new EmailCredential(
                EmailCredential.TransportResend, from.Trim(),
                ResendApiKey: _configuration["Email:Resend:ApiKey"]),
            _ => new EmailCredential(
                EmailCredential.TransportSmtp, from.Trim(),
                SmtpHost: _configuration["Email:Smtp:Host"],
                SmtpPort: _configuration.GetValue<int?>("Email:Smtp:Port"),
                SmtpUsername: _configuration["Email:Smtp:Username"],
                SmtpPassword: _configuration["Email:Smtp:Password"],
                SmtpUseStartTls: _configuration.GetValue<bool?>("Email:Smtp:UseStartTls")),
        };

        // In single-user mode the outbox transport reads the same Email:* config
        // this bundle mirrors, so a from-only config (SMTP host supplied later at
        // the sender) is still a legitimate "present" system credential: enqueue
        // works without the host. We therefore treat from-present as sufficient
        // for the SMTP tier, and require the api key only for the resend tier.
        if (transport == EmailCredential.TransportResend
            && string.IsNullOrWhiteSpace(credential.ResendApiKey))
        {
            return null;
        }

        return credential;
    }

    private readonly record struct CacheEntry(EmailCredential Credential, DateTimeOffset ExpiresAt);
}
