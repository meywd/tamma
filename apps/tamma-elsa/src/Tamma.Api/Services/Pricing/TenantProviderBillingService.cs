using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Api.Services.Billing;
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
///   <item><b>Disable</b> — flip the owner row to <c>platform</c> FIRST (the resolver
///     stops returning <c>byok</c>), THEN retire the cabinet secret. If the retire
///     throws, the mode is already <c>platform</c> (safe).</item>
/// </list>
///
/// <para>Every mutation invalidates Story 32-3's credential cache
/// (<see cref="IProviderCredentialResolver.Invalidate"/>) and emits a
/// <c>PRICING.BYOK.*</c> DCB event. The provider key is ALWAYS canonicalized
/// (<see cref="BillingProviderKey.Canonicalize"/>) before storage / lookup so the owner
/// row matches the resolver's canonicalized read.</para>
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
        var canonical = Canonicalize(provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("A BYOK api key is required.", nameof(apiKey));
        }

        var now = _time.GetUtcNow().UtcDateTime;

        // 1) Cabinet key FIRST — a bad key throws here, before any mode change (no
        //    partial write). The cabinet stores it under the canonical slug 32-3 reads.
        var secret = await _cabinet
            .WriteAsync(tenantId, canonical, apiKey, actorUserId ?? Guid.Empty, ct)
            .ConfigureAwait(false);
        var secretName = ProviderCabinetName(canonical);

        // 2) Upsert the ONE active owner row (AC12 — no duplicate active row).
        var existing = await FindActiveRowAsync(tenantId, canonical, tracking: true, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            _db.TenantProviderBillings.Add(new TenantProviderBilling
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ProviderKey = canonical,
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
        _credentialResolver.Invalidate(tenantId, canonical);

        _logger.LogInformation(
            "BYOK enabled: tenantId={TenantId} provider={Provider} mode=byok secretVersion={Version}",
            tenantId, canonical, secret.ActiveVersionNumber);

        await EmitAsync(PricingEventTypes.ByokEnabled, tenantId, canonical, ModeByok, now, ct)
            .ConfigureAwait(false);

        return new ByokModeResult(canonical, ModeByok, KeySet: true);
    }

    /// <inheritdoc />
    public async Task<ByokModeResult> DisableByokAsync(
        Guid tenantId, string provider, Guid? actorUserId, CancellationToken ct = default)
    {
        var canonical = Canonicalize(provider);
        var now = _time.GetUtcNow().UtcDateTime;

        // 1) Flip the owner row to platform FIRST (secret ref tombstoned; row kept for
        //    audit). Idempotent — a missing / already-platform row is a no-op.
        var existing = await FindActiveRowAsync(tenantId, canonical, tracking: true, ct)
            .ConfigureAwait(false);
        if (existing is not null && existing.Mode != ModePlatform)
        {
            existing.Mode = ModePlatform;
            existing.SecretName = null;
            existing.UpdatedAt = now;
            existing.UpdatedBy = actorUserId;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // 2) Retire the cabinet secret (best-effort; idempotent).
        await _cabinet.RemoveAsync(tenantId, canonical, ct).ConfigureAwait(false);

        // 3) Invalidate 32-3's cached credential so the next LLM call falls back to
        //    the platform leg.
        _credentialResolver.Invalidate(tenantId, canonical);

        _logger.LogInformation(
            "BYOK disabled: tenantId={TenantId} provider={Provider} mode=platform",
            tenantId, canonical);

        await EmitAsync(PricingEventTypes.ByokDisabled, tenantId, canonical, ModePlatform, now, ct)
            .ConfigureAwait(false);

        return new ByokModeResult(canonical, ModePlatform, KeySet: false);
    }

    /// <inheritdoc />
    public async Task<ByokModeResult> GetModeAsync(
        Guid tenantId, string provider, CancellationToken ct = default)
    {
        var canonical = Canonicalize(provider);
        var row = await FindActiveRowAsync(tenantId, canonical, tracking: false, ct)
            .ConfigureAwait(false);
        return Project(canonical, row?.Mode, row?.SecretName);
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
        Guid tenantId, string canonical, bool tracking, CancellationToken ct)
    {
        IQueryable<TenantProviderBilling> q = tracking
            ? _db.TenantProviderBillings
            : _db.TenantProviderBillings.AsNoTracking();
        return q.FirstOrDefaultAsync(
            r => r.TenantId == tenantId
                 && r.Status == StatusActive
                 && r.ProviderKey.ToLower() == canonical,
            ct);
    }

    private static ByokModeResult Project(string canonical, string? mode, string? secretName)
    {
        var isByok = string.Equals(mode, ModeByok, StringComparison.Ordinal);
        return new ByokModeResult(
            canonical,
            isByok ? ModeByok : ModePlatform,
            KeySet: isByok && !string.IsNullOrEmpty(secretName));
    }

    private static string Canonicalize(string? provider)
    {
        var canonical = BillingProviderKey.Canonicalize(provider);
        if (canonical.Length == 0)
        {
            throw new ArgumentException("A provider key is required.", nameof(provider));
        }
        return canonical;
    }

    private static string ProviderCabinetName(string canonical) =>
        ProviderCabinetNames.Byok(canonical);

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
