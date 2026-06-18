using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-1 — the ONLY write path into the plan price-book. Plans are
/// immutable, versioned rows: editing never mutates an <c>active</c>/
/// <c>deprecated</c> row in place. <see cref="CreateNewVersionAsync"/> inserts a
/// new <c>Version = prior + 1</c> row (<see cref="Plan.SupersedesPlanId"/>
/// pointing at the prior, <c>Status = active</c>), flips the prior to
/// <c>deprecated</c>, all in ONE transaction, then emits
/// <c>PLAN.VERSION.CREATED</c> + <c>PLAN.DEPRECATED</c> to <c>platform_events</c>.
///
/// <para>The partial unique index <c>UX_plans_OneActivePerSlug</c> is the
/// load-bearing backstop — the DB rejects a second <c>active</c> row per slug
/// regardless of app logic, so a flip-then-insert race can never leave two
/// active versions. The AUTHORITATIVE immutability enforcement is the
/// <c>SaveChanges</c> interceptor on <c>ControlPlaneDbContext</c>, which throws
/// <c>PLAN.VERSION.IMMUTABLE</c> for ANY mutation of an active/deprecated plan
/// row OR its child feature/entitlement/price rows — including raw EF mutations
/// that never go through this editor. <see cref="EnsureMutableOrThrow"/> is an
/// optional pre-flight convenience that surfaces the same rejection early
/// (before a write is attempted); it does NOT replace the interceptor.</para>
///
/// <para>This story ships the editor as a tested SERVICE METHOD; wiring it
/// behind admin create/deprecate ENDPOINTS is Story 34-2. The catalog is
/// platform-GLOBAL in both modes — there is no per-tenant override layer; in
/// SaaS the endpoint (34-2) gates this on <c>PlatformOwnerAccess</c> (the
/// price book is platform-scoped admin work, not a per-tenant owner gate).</para>
/// </summary>
public sealed class PlanVersionEditor
{
    private const string WorkflowVersion = "1.0.0";

    private readonly ControlPlaneDbContext _db;
    private readonly IPlatformEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger<PlanVersionEditor> _logger;

    public PlanVersionEditor(
        ControlPlaneDbContext db,
        IPlatformEventPublisher publisher,
        TimeProvider time,
        ILogger<PlanVersionEditor> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Optional pre-flight immutability check. A <c>Plan</c> whose status is
    /// <c>active</c> or <c>deprecated</c> may NOT be mutated in place — only a
    /// <c>draft</c> row is writable (plus the controlled active→deprecated flip
    /// performed inside <see cref="CreateNewVersionAsync"/>). Throws
    /// <c>PLAN.VERSION.IMMUTABLE</c> (severity High) otherwise.
    ///
    /// <para>This is a convenience that lets a caller reject early, with a
    /// friendly message, BEFORE attempting a write. It is NOT the authoritative
    /// guard: the <c>ControlPlaneDbContext</c> <c>SaveChanges</c> interceptor
    /// enforces the same invariant (incl. child rows) for every save path,
    /// even raw EF mutations that bypass this editor entirely.</para>
    /// </summary>
    public void EnsureMutableOrThrow(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Status is "active" or "deprecated")
        {
            _logger.LogWarning(
                "Rejected mutation of immutable plan {Slug} v{Version} (status={Status})",
                plan.Slug, plan.Version, plan.Status);

            throw new TammaError(
                "PLAN.VERSION.IMMUTABLE",
                $"Plan '{plan.Slug}' v{plan.Version} is {plan.Status} and immutable. " +
                "Create a new version instead of editing it.",
                new Dictionary<string, object?>
                {
                    ["slug"] = plan.Slug,
                    ["version"] = plan.Version,
                    ["status"] = plan.Status,
                    ["planId"] = plan.Id.ToString("D"),
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
    }

    /// <summary>
    /// Supersede the current <c>active</c> version of <paramref name="slug"/>
    /// with a new immutable version built from <paramref name="draft"/>. Returns
    /// the newly-created active <see cref="Plan"/> (with children attached).
    /// Throws <c>PLAN.VERSION.NO_ACTIVE</c> if the slug has no active version.
    /// </summary>
    public async Task<Plan> CreateNewVersionAsync(
        string slug,
        PlanDraftSpec draft,
        PlanEditorPrincipal principal,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(principal);

        var prior = await _db.Plans
            .Include(p => p.Features)
            .Include(p => p.Entitlements)
            .Include(p => p.Prices)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.Status == "active", ct);

        if (prior is null)
        {
            throw new TammaError(
                "PLAN.VERSION.NO_ACTIVE",
                $"No active version exists for plan slug '{slug}'.",
                new Dictionary<string, object?> { ["slug"] = slug },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var newPlanId = Guid.NewGuid();
        var newVersion = prior.Version + 1;

        var newPlan = new Plan
        {
            Id = newPlanId,
            Slug = slug,
            DisplayName = draft.DisplayName ?? prior.DisplayName,
            Version = newVersion,
            Status = "active",
            IsCustom = draft.IsCustom ?? prior.IsCustom,
            BillingInterval = draft.BillingInterval ?? prior.BillingInterval,
            SupersedesPlanId = prior.Id,
            MonthlyPriceUsd = draft.MonthlyPriceUsd ?? prior.MonthlyPriceUsd,
            Quotas = prior.Quotas,
            IsActive = prior.IsActive,
            PlacementPolicy = draft.PlacementPolicy ?? prior.PlacementPolicy,
            CreatedAt = now,
            UpdatedAt = now,
            Features = (draft.Features ?? ToFeatureDrafts(prior.Features))
                .Select(f => new PlanFeature
                {
                    Id = Guid.NewGuid(),
                    PlanId = newPlanId,
                    FeatureKey = f.FeatureKey,
                    BoolValue = f.BoolValue,
                    StringValue = f.StringValue,
                }).ToList(),
            Entitlements = (draft.Entitlements ?? ToEntitlementDrafts(prior.Entitlements))
                .Select(e => new PlanEntitlement
                {
                    Id = Guid.NewGuid(),
                    PlanId = newPlanId,
                    MetricKey = e.MetricKey,
                    LimitValue = e.LimitValue,
                    Period = e.Period,
                    OverageMode = e.OverageMode,
                }).ToList(),
            Prices = (draft.Prices ?? ToPriceDrafts(prior.Prices))
                .Select(p => new PlanPrice
                {
                    Id = Guid.NewGuid(),
                    PlanId = newPlanId,
                    PricingMode = p.PricingMode,
                    RecurringUsd = p.RecurringUsd,
                    SeatUsd = p.SeatUsd,
                    MeteredComponent = p.MeteredComponent,
                }).ToList(),
        };

        _logger.LogDebug(
            "Plan version-create transaction begin: {Slug} v{Prior} → v{New}",
            slug, prior.Version, newVersion);

        // Flip-then-insert in one transaction with DETERMINISTIC ordering:
        // deprecate the prior (SaveChanges), THEN add the new active row
        // (SaveChanges), then commit. EF's single-SaveChanges UPDATE-before-
        // INSERT topological sort keys off DECLARED index columns and does NOT
        // reliably account for FILTERED indexes (the determining Status column
        // lives in the UX_plans_OneActivePerSlug WHERE predicate, not the index
        // key) — see dotnet/efcore#7193. If EF emitted INSERT-before-UPDATE the
        // new active row would collide with the still-active prior and the
        // create would spuriously fail. Two explicit SaveChanges inside ONE
        // transaction make the ordering unconditional; the partial unique index
        // remains the final DB-level arbiter of "one active per slug".
        //
        // Compatible with the ControlPlaneDbContext immutability interceptor:
        // the first save is the controlled active→deprecated flip (allowed);
        // the second adds a freshly-Added active plan + its children (allowed).
        var ownsTx = _db.Database.CurrentTransaction is null
            && _db.Database.IsRelational();
        var tx = ownsTx ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            prior.Status = "deprecated";
            prior.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            _db.Plans.Add(newPlan);
            await _db.SaveChangesAsync(ct);

            if (tx is not null)
            {
                await tx.CommitAsync(ct);
            }
        }
        catch
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(ct);
            }

            _logger.LogError(
                "Plan version-create transaction rolled back: {Slug} v{Prior} → v{New}",
                slug, prior.Version, newVersion);
            throw;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync();
            }
        }

        _logger.LogInformation(
            "Plan version created: {Slug} v{Version} ({PlanId}); v{PriorVersion} deprecated",
            slug, newVersion, newPlanId, prior.Version);

        // Events only AFTER the real state transition committed.
        await _publisher.AppendAndPublishAsync(
            BuildPlanEvent(
                PlanCatalogEventTypes.VersionCreated,
                principal,
                new Dictionary<string, string?>
                {
                    ["slug"] = slug,
                    ["version"] = newVersion.ToString(),
                    ["planId"] = newPlanId.ToString("D"),
                    ["supersedesPlanId"] = prior.Id.ToString("D"),
                }),
            ct);

        await _publisher.AppendAndPublishAsync(
            BuildPlanEvent(
                PlanCatalogEventTypes.Deprecated,
                principal,
                new Dictionary<string, string?>
                {
                    ["slug"] = slug,
                    ["version"] = prior.Version.ToString(),
                    ["planId"] = prior.Id.ToString("D"),
                    ["supersededByPlanId"] = newPlanId.ToString("D"),
                }),
            ct);

        return newPlan;
    }

    private PlatformEvent BuildPlanEvent(
        string type,
        PlanEditorPrincipal principal,
        IReadOnlyDictionary<string, string?> extraTags)
    {
        var tags = new Dictionary<string, string?>
        {
            ["source"] = "admin",
        };
        foreach (var (k, v) in extraTags)
        {
            if (v is not null) tags[k] = v;
        }
        if (!string.IsNullOrEmpty(principal.UserId)) tags["actorUserId"] = principal.UserId;
        if (!string.IsNullOrEmpty(principal.Email)) tags["actorEmail"] = principal.Email;

        // Defence-in-depth: the canonical record also carries the tag data so a
        // future tag refactor can't drop the audit breadcrumb.
        var data = new Dictionary<string, object?>(
            tags.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));

        return new PlatformEvent
        {
            Type = type,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""",
            Data = JsonSerializer.Serialize(data),
        };
    }

    private static IReadOnlyList<PlanFeatureDraft> ToFeatureDrafts(IEnumerable<PlanFeature> src) =>
        src.Select(f => new PlanFeatureDraft(f.FeatureKey, f.BoolValue, f.StringValue)).ToList();

    private static IReadOnlyList<PlanEntitlementDraft> ToEntitlementDrafts(IEnumerable<PlanEntitlement> src) =>
        src.Select(e => new PlanEntitlementDraft(e.MetricKey, e.LimitValue, e.Period, e.OverageMode)).ToList();

    private static IReadOnlyList<PlanPriceDraft> ToPriceDrafts(IEnumerable<PlanPrice> src) =>
        src.Select(p => new PlanPriceDraft(p.PricingMode, p.RecurringUsd, p.SeatUsd, p.MeteredComponent)).ToList();
}

/// <summary>
/// Story 34-1 — the new-version content. Header fields are optional overrides
/// (null ⇒ copy from the prior version); child collections are full
/// replacements when non-null (null ⇒ copy the prior version's children).
/// </summary>
public sealed record PlanDraftSpec(
    string? DisplayName = null,
    bool? IsCustom = null,
    string? BillingInterval = null,
    decimal? MonthlyPriceUsd = null,
    string? PlacementPolicy = null,
    IReadOnlyList<PlanFeatureDraft>? Features = null,
    IReadOnlyList<PlanEntitlementDraft>? Entitlements = null,
    IReadOnlyList<PlanPriceDraft>? Prices = null);

/// <summary>A feature row to write into a new version.</summary>
public sealed record PlanFeatureDraft(string FeatureKey, bool? BoolValue, string? StringValue);

/// <summary>An entitlement row to write into a new version.</summary>
public sealed record PlanEntitlementDraft(
    EntitlementMetricKey MetricKey, long? LimitValue, string Period, string OverageMode);

/// <summary>A price row to write into a new version.</summary>
public sealed record PlanPriceDraft(
    string PricingMode, decimal RecurringUsd, decimal SeatUsd, string MeteredComponent);

/// <summary>
/// Minimal actor identity stamped onto the DCB events. The endpoint layer
/// (Story 34-2) derives this from the <c>ClaimsPrincipal</c>; tests pass it
/// directly. Mode-agnostic — the catalog is platform-owned in both modes.
/// </summary>
public sealed record PlanEditorPrincipal(string? UserId, string? Email);
