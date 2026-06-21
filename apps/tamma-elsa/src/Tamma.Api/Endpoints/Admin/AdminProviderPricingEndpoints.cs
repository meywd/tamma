using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Providers;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 34-11 — admin CRUD for the provider COST price-book under
/// <c>/api/admin/providers*</c>. Every route MUST be gated behind the
/// <c>PlatformOwnerAccess</c> policy at the wiring site (NOT <c>OwnerAccess</c>,
/// which admits every personal-tenant owner): the cost book is platform-GLOBAL
/// in both single-user and SaaS modes (no per-tenant override layer), so it is
/// platform-scoped admin work.
///
/// <para>The write paths are immutable-versioned (mirrors
/// <c>PlanVersionEditor</c>): a price <c>PUT</c> supersedes the prior
/// <c>active</c> row and inserts a new one; a mutation of a <c>superseded</c>
/// row throws <c>PROVIDER.PRICE.IMMUTABLE</c>. Mutations emit DCB events to the
/// control-plane <c>platform_events</c> store via
/// <see cref="IPlatformEventPublisher"/>.</para>
/// </summary>
public static class AdminProviderPricingEndpoints
{
    private const string WorkflowVersion = "1.0.0";

    // ── GET /api/admin/providers ──
    public static async Task<IResult> ListProviders(
        ControlPlaneDbContext db,
        CancellationToken ct)
    {
        var providers = await db.Providers
            .AsNoTracking()
            .OrderBy(p => p.Key)
            .Select(p => new
            {
                p.Id,
                p.Key,
                p.DisplayName,
                p.AuthModel,
                p.Status,
                p.CreatedAt,
                p.UpdatedAt,
            })
            .ToListAsync(ct);

        return Results.Ok(new { providers });
    }

    // ── GET /api/admin/providers/{key}/prices ──
    public static async Task<IResult> ListPrices(
        string key,
        ControlPlaneDbContext db,
        CancellationToken ct)
    {
        var canonical = ProviderRateLookup.Canonicalize(key);

        var providerExists = await db.Providers
            .AsNoTracking()
            .AnyAsync(p => p.Key == canonical, ct);
        if (!providerExists)
        {
            return Results.NotFound(new { error = "provider_not_found", key = canonical });
        }

        var prices = await db.ProviderModelPrices
            .AsNoTracking()
            .Where(p => p.ProviderKey == canonical)
            .OrderBy(p => p.Model).ThenByDescending(p => p.EffectiveFrom)
            .ToListAsync(ct);

        return Results.Ok(new { key = canonical, prices });
    }

    // ── POST /api/admin/providers ──
    public static async Task<IResult> RegisterProvider(
        RegisterProviderRequest body,
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        ClaimsPrincipal principal,
        TimeProvider time,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Key))
        {
            return Results.BadRequest(new { error = "key_required" });
        }
        if (body.AuthModel is not ("api-key" or "cli-token"))
        {
            return Results.BadRequest(new { error = "invalid_auth_model", authModel = body.AuthModel });
        }

        var canonical = ProviderRateLookup.Canonicalize(body.Key);
        var exists = await db.Providers.AnyAsync(p => p.Key == canonical, ct);
        if (exists)
        {
            return Results.Conflict(new { error = "provider_exists", key = canonical });
        }

        var now = time.GetUtcNow().UtcDateTime;
        var provider = new Provider
        {
            Id = Guid.NewGuid(),
            Key = canonical,
            DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? canonical : body.DisplayName,
            AuthModel = body.AuthModel,
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);

        await publisher.AppendAndPublishAsync(
            BuildEvent(ProviderPricingEventTypes.Registered, principal, new Dictionary<string, string?>
            {
                ["providerKey"] = canonical,
                ["authModel"] = provider.AuthModel,
            }),
            ct);

        return Results.Created($"/api/admin/providers/{canonical}", new { provider.Id, provider.Key });
    }

    // ── PATCH /api/admin/providers/{key} ──
    public static async Task<IResult> UpdateProvider(
        string key,
        UpdateProviderRequest body,
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        ClaimsPrincipal principal,
        TimeProvider time,
        CancellationToken ct)
    {
        var canonical = ProviderRateLookup.Canonicalize(key);
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Key == canonical, ct);
        if (provider is null)
        {
            return Results.NotFound(new { error = "provider_not_found", key = canonical });
        }

        if (body?.AuthModel is { } am && am is not ("api-key" or "cli-token"))
        {
            return Results.BadRequest(new { error = "invalid_auth_model", authModel = am });
        }
        if (body?.Status is { } st && st is not ("active" or "retired"))
        {
            return Results.BadRequest(new { error = "invalid_status", status = st });
        }

        if (!string.IsNullOrWhiteSpace(body?.DisplayName)) provider.DisplayName = body!.DisplayName!;
        if (!string.IsNullOrWhiteSpace(body?.AuthModel)) provider.AuthModel = body!.AuthModel!;
        if (!string.IsNullOrWhiteSpace(body?.Status)) provider.Status = body!.Status!;
        provider.UpdatedAt = time.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);

        await publisher.AppendAndPublishAsync(
            BuildEvent(ProviderPricingEventTypes.StatusChanged, principal, new Dictionary<string, string?>
            {
                ["providerKey"] = canonical,
                ["status"] = provider.Status,
                ["authModel"] = provider.AuthModel,
            }),
            ct);

        return Results.Ok(new { provider.Key, provider.DisplayName, provider.AuthModel, provider.Status });
    }

    // ── PUT /api/admin/providers/{key}/prices ──
    public static async Task<IResult> VersionPrice(
        string key,
        VersionPriceRequest body,
        ControlPlaneDbContext db,
        IProviderCostResolver resolver,
        IPlatformEventPublisher publisher,
        ClaimsPrincipal principal,
        TimeProvider time,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Model))
        {
            return Results.BadRequest(new { error = "model_required" });
        }

        var canonical = ProviderRateLookup.Canonicalize(key);

        var providerExists = await db.Providers.AnyAsync(p => p.Key == canonical, ct);
        if (!providerExists)
        {
            return Results.NotFound(new { error = "provider_not_found", key = canonical });
        }

        var now = time.GetUtcNow().UtcDateTime;
        var effectiveFrom = (body.EffectiveFrom ?? now).ToUniversalTime();

        // The prior active row for (canonical, model) — flipped to superseded.
        var prior = await db.ProviderModelPrices
            .FirstOrDefaultAsync(p =>
                p.ProviderKey == canonical
                && p.Model == body.Model
                && p.Status == "active", ct);

        var newId = Guid.NewGuid();

        var ownsTx = db.Database.CurrentTransaction is null && db.Database.IsRelational();
        var tx = ownsTx ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            if (prior is not null)
            {
                // Flip-then-insert in two saves inside one tx (deterministic
                // ordering, same rationale as PlanVersionEditor): the partial
                // unique index rejects a second active row, so insert must
                // follow the supersede.
                prior.Status = "superseded";
                prior.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }

            db.ProviderModelPrices.Add(new ProviderModelPrice
            {
                Id = newId,
                ProviderKey = canonical,
                Model = body.Model,
                InputUsdPer1M = body.InputUsdPer1M,
                OutputUsdPer1M = body.OutputUsdPer1M,
                CacheReadUsdPer1M = body.CacheReadUsdPer1M,
                CacheWriteUsdPer1M = body.CacheWriteUsdPer1M,
                EffectiveFrom = effectiveFrom,
                Status = "active",
                Source = "admin",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }

        resolver.Invalidate();

        await publisher.AppendAndPublishAsync(
            BuildEvent(ProviderPricingEventTypes.PriceVersioned, principal, new Dictionary<string, string?>
            {
                ["providerKey"] = canonical,
                ["model"] = body.Model,
                ["effectiveFrom"] = effectiveFrom.ToString("O"),
                ["supersededPriceId"] = prior?.Id.ToString("D"),
            }),
            ct);

        return Results.Ok(new
        {
            id = newId,
            providerKey = canonical,
            model = body.Model,
            effectiveFrom,
            supersededPriceId = prior?.Id,
        });
    }

    /// <summary>
    /// Story 34-11 — the authoritative immutability guard for a direct mutation
    /// of an already-<c>superseded</c> cost row. The admin write path never
    /// mutates superseded rows (it supersedes + inserts), but any code path that
    /// tries must throw <c>PROVIDER.PRICE.IMMUTABLE</c>.
    /// </summary>
    public static void EnsureMutableOrThrow(ProviderModelPrice price)
    {
        ArgumentNullException.ThrowIfNull(price);

        if (price.Status == "superseded")
        {
            throw new TammaError(
                "PROVIDER.PRICE.IMMUTABLE",
                $"Provider cost price {price.Id:D} ({price.ProviderKey}/{price.Model}) is "
                + "superseded and immutable. Version a new price instead of editing it.",
                new Dictionary<string, object?>
                {
                    ["priceId"] = price.Id.ToString("D"),
                    ["providerKey"] = price.ProviderKey,
                    ["model"] = price.Model,
                    ["status"] = price.Status,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
    }

    private static PlatformEvent BuildEvent(
        string type,
        ClaimsPrincipal? principal,
        IReadOnlyDictionary<string, string?> extraTags)
    {
        var tags = new Dictionary<string, string?> { ["source"] = "admin" };
        foreach (var (k, v) in extraTags)
        {
            if (v is not null) tags[k] = v;
        }

        var userId = principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal?.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal?.FindFirst(ClaimTypes.Email)?.Value
            ?? principal?.FindFirst("email")?.Value;
        if (!string.IsNullOrEmpty(userId)) tags["actorUserId"] = userId;
        if (!string.IsNullOrEmpty(email)) tags["actorEmail"] = email;

        var data = new Dictionary<string, object?>(
            tags.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));

        return new PlatformEvent
        {
            Type = type,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = $"{{\"workflowVersion\":\"{WorkflowVersion}\",\"eventSource\":\"system\"}}",
            Data = JsonSerializer.Serialize(data),
        };
    }
}

/// <summary>Body for <c>POST /api/admin/providers</c>.</summary>
public sealed record RegisterProviderRequest(
    string Key,
    string? DisplayName = null,
    string AuthModel = "api-key");

/// <summary>Body for <c>PATCH /api/admin/providers/{key}</c>.</summary>
public sealed record UpdateProviderRequest(
    string? DisplayName = null,
    string? AuthModel = null,
    string? Status = null);

/// <summary>Body for <c>PUT /api/admin/providers/{key}/prices</c>.</summary>
public sealed record VersionPriceRequest(
    string Model,
    decimal InputUsdPer1M,
    decimal OutputUsdPer1M,
    decimal? CacheReadUsdPer1M = null,
    decimal? CacheWriteUsdPer1M = null,
    DateTime? EffectiveFrom = null);
