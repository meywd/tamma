using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.Providers;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 34-5 — admin view/version of platform MARGIN policies under
/// <c>/api/admin/pricing/margins</c>. Every route MUST be gated behind the
/// <c>PlatformOwnerAccess</c> policy at the wiring site (NOT <c>OwnerAccess</c>,
/// which admits every personal-tenant owner): margin policies are platform-GLOBAL
/// in both single-user and SaaS modes (no per-tenant margin rows), so this is
/// platform-scoped admin work.
///
/// <para>Write is immutable-versioned (mirrors <c>AdminProviderPricingEndpoints</c>):
/// a <c>PUT</c> supersedes the prior <c>active</c> row for the same
/// <c>(scope, refKey)</c> and inserts a new one — so a historical usage event
/// stays priced under the policy that was active at its timestamp. Mutations emit
/// <c>PRICING.MARGIN.UPDATED</c> to the control-plane <c>platform_events</c> store
/// via <see cref="IPlatformEventPublisher"/>.</para>
/// </summary>
public static class AdminPricingEndpoints
{
    private const string WorkflowVersion = "1.0.0";

    // ── GET /api/admin/pricing/margins ──
    public static async Task<IResult> ListMargins(
        ControlPlaneDbContext db,
        CancellationToken ct)
    {
        var policies = await db.MarginPolicies
            .AsNoTracking()
            .OrderBy(p => p.Scope)
            .ThenBy(p => p.RefKey)
            .ThenByDescending(p => p.EffectiveFrom)
            .Select(p => new MarginPolicyDto(
                p.Id, p.Scope, p.RefKey, p.MarkupMultiplier, p.FixedUsdPer1M,
                p.EffectiveFrom, p.Status, p.CreatedAt, p.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(new { policies });
    }

    // ── PUT /api/admin/pricing/margins ──
    public static async Task<IResult> VersionMargin(
        VersionMarginRequest body,
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        ClaimsPrincipal principal,
        TimeProvider time,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Scope))
        {
            return Results.BadRequest(new { error = "scope_required" });
        }
        if (body.Scope is not ("global" or "plan" or "provider"))
        {
            return Results.BadRequest(new { error = "invalid_scope", scope = body.Scope });
        }

        // RefKey discipline: NULL only for global; required for plan/provider.
        var refKey = NormalizeRefKey(body.Scope, body.RefKey);
        if (body.Scope == "global" && !string.IsNullOrWhiteSpace(body.RefKey))
        {
            return Results.BadRequest(new { error = "global_refkey_must_be_null" });
        }
        if (body.Scope != "global" && string.IsNullOrWhiteSpace(refKey))
        {
            return Results.BadRequest(new { error = "refkey_required", scope = body.Scope });
        }

        // At least one knob non-null (mirrors ck_margin_policies_has_knob).
        if (body.MarkupMultiplier is null && body.FixedUsdPer1M is null)
        {
            return Results.BadRequest(new { error = "at_least_one_knob_required" });
        }
        // A malformed/typo'd negative markup must not poison the sell price.
        if (body.MarkupMultiplier is < 0m)
        {
            return Results.BadRequest(new { error = "negative_markup_multiplier" });
        }
        // A markup is by definition >= 1: sell = cost * multiplier, so a supplied
        // multiplier in [0,1) would price AT/BELOW the platform's own wholesale
        // cost (a fat-fingered 0.13 = ~87% revenue loss) while still passing
        // ck_margin_policies_has_knob. Only guard when the multiplier is actually
        // supplied — a null multiplier with a FixedUsdPer1M knob is a legitimate
        // "cost + fixed per-token fee" policy and must stay allowed.
        if (body.MarkupMultiplier is not null && body.MarkupMultiplier < 1m)
        {
            return Results.BadRequest(new { error = "markup_multiplier_below_one" });
        }
        if (body.FixedUsdPer1M is < 0m)
        {
            return Results.BadRequest(new { error = "negative_fixed_usd_per_1m" });
        }

        var now = time.GetUtcNow().UtcDateTime;
        var effectiveFrom = (body.EffectiveFrom ?? now).ToUniversalTime();
        var newId = Guid.NewGuid();

        // The prior active row for (scope, refKey) — flipped to superseded.
        MarginPolicy? prior = await db.MarginPolicies
            .FirstOrDefaultAsync(p =>
                p.Scope == body.Scope
                && p.RefKey == refKey
                && p.Status == "active", ct);

        var ownsTx = db.Database.CurrentTransaction is null && db.Database.IsRelational();
        var tx = ownsTx ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            if (prior is not null)
            {
                // Flip-then-insert in two saves inside one tx (deterministic
                // ordering, same rationale as PlanVersionEditor): the partial
                // unique index rejects a second active row, so the insert must
                // follow the supersede.
                prior.Status = "superseded";
                prior.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }

            db.MarginPolicies.Add(new MarginPolicy
            {
                Id = newId,
                Scope = body.Scope,
                RefKey = refKey,
                MarkupMultiplier = body.MarkupMultiplier,
                FixedUsdPer1M = body.FixedUsdPer1M,
                EffectiveFrom = effectiveFrom,
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }
        catch (DbUpdateException dbEx) when (IsUniqueViolation(dbEx))
        {
            // A concurrent PUT for the same (scope, refKey) won the race and
            // inserted its active row first; our insert then hit the partial
            // unique index (Postgres 23505). This is a benign lost-write, not a
            // server fault — surface 409 so the caller retries (pre-fix it leaked
            // as a 500). The one-active-per-scope invariant is preserved.
            if (tx is not null) await tx.RollbackAsync(ct);
            return Results.Conflict(new
            {
                error = "margin_policy_conflict",
                scope = body.Scope,
                refKey,
                message = "A concurrent update superseded this margin policy; retry the request.",
            });
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

        await publisher.AppendAndPublishAsync(
            BuildEvent(PricingEventTypes.MarginUpdated, principal, new Dictionary<string, string?>
            {
                ["scope"] = body.Scope,
                ["refKey"] = refKey,
                ["effectiveFrom"] = effectiveFrom.ToString("O"),
                ["supersededPolicyId"] = prior?.Id.ToString("D"),
            }),
            ct);

        return Results.Ok(new
        {
            policy = new MarginPolicyDto(
                newId, body.Scope, refKey, body.MarkupMultiplier, body.FixedUsdPer1M,
                effectiveFrom, "active", now, now),
            supersededPolicyId = prior?.Id,
        });
    }

    /// <summary>
    /// Detects the Postgres 23505 unique-violation raised by the partial unique
    /// index (one active row per <c>(scope, refKey)</c>) when two concurrent PUTs
    /// race the supersede-then-insert. EF wraps the Npgsql exception in a
    /// <see cref="DbUpdateException"/>. Same shape as
    /// <c>PromptEndpoints.IsUniqueViolation</c>.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException dbEx)
        => dbEx.InnerException is Npgsql.PostgresException pgEx
           && string.Equals(pgEx.SqlState, "23505", StringComparison.Ordinal);

    /// <summary>Canonicalize a provider-scope refKey; trim others; global -> null.</summary>
    private static string? NormalizeRefKey(string scope, string? refKey)
    {
        if (scope == "global") return null;
        if (string.IsNullOrWhiteSpace(refKey)) return null;
        return scope == "provider"
            ? ProviderRateLookup.Canonicalize(refKey)
            : refKey.Trim();
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

/// <summary>Wire projection of a <see cref="MarginPolicy"/> row.</summary>
public sealed record MarginPolicyDto(
    Guid Id,
    string Scope,
    string? RefKey,
    decimal? MarkupMultiplier,
    decimal? FixedUsdPer1M,
    DateTime EffectiveFrom,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Body for <c>PUT /api/admin/pricing/margins</c>.</summary>
public sealed record VersionMarginRequest(
    string Scope,
    string? RefKey = null,
    decimal? MarkupMultiplier = null,
    decimal? FixedUsdPer1M = null,
    DateTime? EffectiveFrom = null);
