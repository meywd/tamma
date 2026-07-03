using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Pricing;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-2 — the SaaS billing-mode tagger. Reads Story 34-3's authoritative
/// <c>TenantProviderBilling</c> mode (via
/// <see cref="ITenantProviderBillingResolver"/>) and reconciles it against Story
/// 32-3's resolved credential source. On disagreement the 32-3 source wins for
/// the stamped tag (it is the credential actually used on the wire), a WARN is
/// logged and a <c>BILLING.MODE.MISMATCH</c> DCB event is appended.
///
/// <para>Holds NO key plaintext and writes NO mode — it only computes the
/// <c>byok</c>/<c>platform</c> token.</para>
/// </summary>
public sealed class BillingModeTagger : IBillingModeTagger
{
    private readonly ITenantProviderBillingResolver _owner;
    private readonly IEventRepository _events;
    private readonly ILogger<BillingModeTagger> _logger;

    public BillingModeTagger(
        ITenantProviderBillingResolver owner,
        IEventRepository events,
        ILogger<BillingModeTagger> logger)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> ResolveTagAsync(
        Guid? tenantId,
        string providerKey,
        string? credentialSource = null,
        CancellationToken ct = default)
    {
        // Fix 2 — canonicalize the provider to the family key the owner row is stored
        // under BEFORE resolution + logging, so the owner lookup and every reader agree
        // on one key (the vendor handle "anthropic-claude" resolves the "anthropic" row).
        providerKey = BillingProviderKey.Canonicalize(providerKey);

        // 1) The DECLARED mode from the 34-3 owner (default: platform).
        var declared = await _owner.ResolveModeAsync(tenantId, providerKey, ct).ConfigureAwait(false);
        var token = declared.ToToken();

        // 2) Reconcile with 32-3's RUNTIME credential source when present.
        var source = Normalize(credentialSource);
        if (source is not null)
        {
            if (!BillingModeTokens.IsValid(source))
            {
                // 32-3 handed us a token outside the closed domain — fail loud
                // (never silently tag), AC11.
                _logger.LogError(
                    "billing_mode reconcile: 32-3 credentialSource '{Source}' is neither "
                    + "'byok' nor 'platform' (tenant {TenantId} provider {Provider}).",
                    source, tenantId, providerKey);
                throw new TammaError(
                    "BILLING_MODE_INVALID_SOURCE",
                    $"Credential source '{source}' is not a valid billing mode.",
                    new Dictionary<string, object?>
                    {
                        ["tenantId"] = tenantId,
                        ["provider"] = providerKey,
                        ["source"] = source,
                    },
                    retryable: false,
                    severity: TammaErrorSeverity.High);
            }

            if (!string.Equals(source, token, StringComparison.Ordinal))
            {
                // 34-3 mode ≠ 32-3 source. 32-3 wins (it is the wire credential).
                _logger.LogWarning(
                    "billing_mode mismatch: declared(34-3)={Declared} source(32-3)={Source} "
                    + "for tenant {TenantId} provider {Provider} — 32-3 source wins.",
                    token, source, tenantId, providerKey);
                await EmitMismatchAsync(tenantId, providerKey, token, source, ct).ConfigureAwait(false);
                token = source;
            }
        }

        // 3) Final validation — the stamped token MUST be exactly byok|platform.
        if (!BillingModeTokens.IsValid(token))
        {
            _logger.LogError(
                "billing_mode resolved to an out-of-domain token '{Token}' "
                + "(tenant {TenantId} provider {Provider}).",
                token, tenantId, providerKey);
            throw new TammaError(
                "BILLING_MODE_INVALID_TOKEN",
                $"Resolved billing_mode '{token}' is not a valid token.",
                new Dictionary<string, object?>
                {
                    ["tenantId"] = tenantId,
                    ["provider"] = providerKey,
                    ["token"] = token,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        _logger.LogInformation(
            "billing_mode resolved: tenantId={TenantId} provider={Provider} billingMode={BillingMode}",
            tenantId, providerKey, token);
        return token;
    }

    private async Task EmitMismatchAsync(
        Guid? tenantId, string provider, string mode34, string source32, CancellationToken ct)
    {
        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = BillingModeEvents.BillingModeMismatch,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = tenantId?.ToString(),
                    provider,
                    mode34,
                    source32,
                }),
                Metadata = JsonSerializer.Serialize(new
                {
                    workflowVersion = "1.0.0",
                    eventSource = "system",
                }),
                Data = JsonSerializer.Serialize(new { observedAt = DateTime.UtcNow }),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The mismatch audit event is best-effort — never fail the call over
            // an audit-append error, but log it loudly.
            _logger.LogError(
                ex, "Failed to append BILLING.MODE.MISMATCH for tenant {TenantId} provider {Provider}.",
                tenantId, provider);
        }
    }

    private static string? Normalize(string? credentialSource)
    {
        if (string.IsNullOrWhiteSpace(credentialSource))
        {
            return null;
        }
        return credentialSource.Trim().ToLowerInvariant();
    }
}
