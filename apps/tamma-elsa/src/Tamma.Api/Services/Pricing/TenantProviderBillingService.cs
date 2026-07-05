using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Services.Providers;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-3 — default <see cref="ITenantProviderBillingService"/>. Owns the WRITE
/// path that populates the authoritative <c>TenantProviderBilling</c> owner rows the
/// read-side <see cref="TenantProviderBillingResolver"/> consumes.
///
/// <para>Ordering is fail-loud and partial-write-safe:</para>
/// <list type="bullet">
///   <item><b>Enable</b> — write the cabinet key FIRST (a bad key throws before any
///     mode change), THEN upsert the owner row to <c>byok</c>. If the row write throws,
///     the key is orphaned but the mode stays <c>platform</c> (safe) — never a
///     <c>byok</c> row with no key.</item>
///   <item><b>Disable</b> — flip the owner row to <c>platform</c> FIRST, THEN retire
///     the cabinet secret. Row-first is deliberate: a key-first ordering could leave a
///     <c>byok</c> row with no cabinet key (a SecretName-XOR violation) — strictly
///     worse. Note this ordering flips the BILLING mode immediately but NOT the runtime
///     credential: Story 32-3's credential resolver keys off cabinet PRESENCE, so it
///     keeps resolving the tenant key until the secret is actually retired. A retire
///     failure therefore surfaces a RETRIABLE error (after the invalidate + DISABLED
///     event still run) rather than a mid-way 500 that silently leaves the key live.</item>
/// </list>
///
/// <para>Every mutation invalidates Story 32-3's credential cache
/// (<see cref="IProviderCredentialResolver.Invalidate"/>) and emits a
/// <c>PRICING.BYOK.*</c> DCB event. The provider key is ALWAYS the RAW provider
/// IDENTITY (<see cref="ProviderIdentity.Normalize"/> — <c>Trim().ToLowerInvariant()</c>,
/// NO alias-family reduction) for BOTH the owner-row <c>ProviderKey</c> and the cabinet
/// slug, so the write slug is byte-identical to what the credential resolver reads for
/// that same handle (<c>github-copilot</c> ≠ <c>openai</c>, <c>gemini</c> ≠
/// <c>google</c>). The rate-card family alias is NOT applied here.</para>
/// </summary>
public sealed class TenantProviderBillingService : ITenantProviderBillingService
{
    private const string ModePlatform = MetricBillingModeExtensions.PlatformToken;
    private const string ModeByok = MetricBillingModeExtensions.ByokToken;
    private const string StatusActive = "active";

    private readonly ControlPlaneDbContext _db;
    private readonly IProviderByokSecretCabinet _cabinet;
    private readonly IEventRepository _events;
    private readonly IProviderCredentialResolver _credentialResolver;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantProviderBillingService> _logger;

    public TenantProviderBillingService(
        ControlPlaneDbContext db,
        IProviderByokSecretCabinet cabinet,
        IEventRepository events,
        IProviderCredentialResolver credentialResolver,
        TimeProvider time,
        ILogger<TenantProviderBillingService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _cabinet = cabinet ?? throw new ArgumentNullException(nameof(cabinet));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _credentialResolver = credentialResolver ?? throw new ArgumentNullException(nameof(credentialResolver));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ByokModeResult> EnableByokAsync(
        Guid tenantId, string provider, string apiKey, Guid? actorUserId, CancellationToken ct = default)
    {
        var normalized = NormalizeProvider(provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("A BYOK api key is required.", nameof(apiKey));
        }

        var now = _time.GetUtcNow().UtcDateTime;

        // 1) Cabinet key FIRST — a bad key throws here, before any mode change (no
        //    partial write). The cabinet stores it under the RAW-identity slug 32-3
        //    reads for this same handle (provider/<handle>/api-key).
        var secret = await _cabinet
            .WriteAsync(tenantId, normalized, apiKey, actorUserId ?? Guid.Empty, ct)
            .ConfigureAwait(false);
        var secretName = ProviderCabinetName(normalized);

        // 2) Upsert the ONE active owner row (AC12 — no duplicate active row). A
        //    concurrent first-time enable races the check-then-insert window; the loser
        //    hits ux_tpb_active_provider (23505) as a DbUpdateException, mapped to 409 at
        //    the endpoint (never a 500).
        var existing = await FindActiveRowAsync(tenantId, normalized, tracking: true, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            _db.TenantProviderBillings.Add(new TenantProviderBilling
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProviderKey = normalized,
                Mode = ModeByok,
                SecretName = secretName,
                Status = StatusActive,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = actorUserId,
                UpdatedBy = actorUserId,
            });
        }
        else
        {
            existing.Mode = ModeByok;
            existing.SecretName = secretName;
            existing.UpdatedAt = now;
            existing.UpdatedBy = actorUserId;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // 3) Invalidate 32-3's cached credential so the next LLM call re-resolves BYOK.
        _credentialResolver.Invalidate(tenantId, normalized);

        _logger.LogInformation(
            "BYOK enabled: tenantId={TenantId} provider={Provider} mode=byok secretVersion={Version}",
            tenantId, normalized, secret.ActiveVersionNumber);

        await EmitAsync(PricingEventTypes.ByokEnabled, tenantId, normalized, ModeByok, now, ct)
            .ConfigureAwait(false);

        return new ByokModeResult(normalized, ModeByok, KeySet: true);
    }

    /// <inheritdoc />
    public async Task<ByokModeResult> DisableByokAsync(
        Guid tenantId, string provider, Guid? actorUserId, CancellationToken ct = default)
    {
        var normalized = NormalizeProvider(provider);
        var now = _time.GetUtcNow().UtcDateTime;

        // 1) Flip the owner row to platform FIRST (secret ref tombstoned; row kept for
        //    audit). Idempotent — a missing / already-platform row is a no-op. Row-first
        //    (not key-first) is deliberate: a key-first order could leave a byok row with
        //    no cabinet key (a SecretName-XOR violation). This flips the BILLING mode; the
        //    32-3 credential resolver still resolves the tenant key until step 2 actually
        //    retires the secret (it reads cabinet PRESENCE, not this row).
        var existing = await FindActiveRowAsync(tenantId, normalized, tracking: true, ct)
            .ConfigureAwait(false);
        if (existing is not null && existing.Mode != ModePlatform)
        {
            existing.Mode = ModePlatform;
            existing.SecretName = null;
            existing.UpdatedAt = now;
            existing.UpdatedBy = actorUserId;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // 2) Retire the cabinet secret (idempotent). A failure here MUST NOT skip the
        //    cache-invalidate / DISABLED event (the billing mode already flipped), and it
        //    MUST NOT report clean success either — the credential resolver reads cabinet
        //    PRESENCE, so a still-present key keeps resolving byok. Log it, run steps 3/4
        //    anyway, then surface a RETRIABLE error so the caller re-runs the idempotent
        //    disable to actually retire the key (rather than a mid-way 500 that leaves the
        //    key live AND skips the event).
        Exception? retireFailure = null;
        try
        {
            await _cabinet.RemoveAsync(tenantId, normalized, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            retireFailure = ex;
            _logger.LogError(
                ex,
                "BYOK disable: cabinet retire FAILED for tenantId={TenantId} provider={Provider}; "
                + "owner row is already platform but the key is still present — surfacing a "
                + "retriable error so the disable is re-run.",
                tenantId, normalized);
        }

        // 3) Invalidate 32-3's cached credential so the next LLM call re-resolves.
        _credentialResolver.Invalidate(tenantId, normalized);

        _logger.LogInformation(
            "BYOK disabled: tenantId={TenantId} provider={Provider} mode=platform",
            tenantId, normalized);

        // 4) Emit the DISABLED event ALWAYS — the mode change is authoritative even when
        //    the cabinet retire needs a retry.
        await EmitAsync(PricingEventTypes.ByokDisabled, tenantId, normalized, ModePlatform, now, ct)
            .ConfigureAwait(false);

        if (retireFailure is not null)
        {
            throw new TammaError(
                "BYOK.DISABLE.CABINET_RETIRE_FAILED",
                $"BYOK owner row for provider '{normalized}' was flipped to platform, but retiring "
                + "the cabinet key failed; retry the disable to remove the key.",
                new Dictionary<string, object?>
                {
                    ["tenantId"] = tenantId,
                    ["provider"] = normalized,
                },
                retryable: true,
                severity: TammaErrorSeverity.Medium);
        }

        return new ByokModeResult(normalized, ModePlatform, KeySet: false);
    }

    /// <inheritdoc />
    public async Task<ByokModeResult> GetModeAsync(
        Guid tenantId, string provider, CancellationToken ct = default)
    {
        var normalized = NormalizeProvider(provider);
        var row = await FindActiveRowAsync(tenantId, normalized, tracking: false, ct)
            .ConfigureAwait(false);
        return Project(normalized, row?.Mode, row?.SecretName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ByokModeResult>> ListModesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        var rows = await _db.TenantProviderBillings
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Status == StatusActive)
            .Select(r => new { r.ProviderKey, r.Mode, r.SecretName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r => Project(r.ProviderKey.ToLowerInvariant(), r.Mode, r.SecretName))
            .ToList();
    }

    private Task<TenantProviderBilling?> FindActiveRowAsync(
        Guid tenantId, string normalized, bool tracking, CancellationToken ct)
    {
        IQueryable<TenantProviderBilling> q = tracking
            ? _db.TenantProviderBillings
            : _db.TenantProviderBillings.AsNoTracking();
        return q.FirstOrDefaultAsync(
            r => r.TenantId == tenantId
                 && r.Status == StatusActive
                 && r.ProviderKey.ToLower() == normalized,
            ct);
    }

    private static ByokModeResult Project(string normalized, string? mode, string? secretName)
    {
        var isByok = string.Equals(mode, ModeByok, StringComparison.Ordinal);
        return new ByokModeResult(
            normalized,
            isByok ? ModeByok : ModePlatform,
            KeySet: isByok && !string.IsNullOrEmpty(secretName));
    }

    // The RAW provider IDENTITY (Trim + lower, NO alias-family reduction) — the exact
    // string the credential resolver reads for this handle. See ProviderIdentity.
    private static string NormalizeProvider(string? provider)
    {
        var normalized = ProviderIdentity.Normalize(provider);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A provider key is required.", nameof(provider));
        }
        return normalized;
    }

    private static string ProviderCabinetName(string normalized) =>
        ProviderCabinetNames.Byok(normalized);

    private async Task EmitAsync(
        string type, Guid tenantId, string provider, string mode, DateTime now, CancellationToken ct)
    {
        var tenantTag = tenantId.ToString();
        await _events.AppendAsync(new DomainEvent
        {
            Id = Guid.NewGuid(),
            Type = type,
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(new
            {
                tenantId = tenantTag,
                provider,
                mode,
            }),
            Metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = "1.0.0",
                eventSource = "system",
            }),
            Data = JsonSerializer.Serialize(new
            {
                provider,
                mode,
            }),
            CreatedAt = now,
        }).ConfigureAwait(false);
    }
}
