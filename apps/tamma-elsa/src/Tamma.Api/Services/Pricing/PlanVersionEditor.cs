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
public sealed class PlanVersionEditor : IPlanVersionEditor
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

    /// <inheritdoc />
    public async Task<Plan> CreateInitialVersionAsync(
        string slug,
        PlanDraftSpec draft,
        PlanEditorPrincipal principal,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(principal);

        ValidateDraft(draft);
        RequireDisplayName(draft);

        // A brand-new plan may not collide with ANY existing version of the slug
        // — the caller must version an existing slug via VersionPlanAsync.
        var exists = await _db.Plans.AsNoTracking().AnyAsync(p => p.Slug == slug, ct);
        if (exists)
        {
            throw new TammaError(
                "PLAN.SLUG.EXISTS",
                $"Plan slug '{slug}' already exists — create a new version via PUT instead.",
                new Dictionary<string, object?> { ["slug"] = slug },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        var plan = await InsertNewV1PlanAsync(slug, draft, isCustom: false, ct);

        _logger.LogInformation(
            "Plan created (initial version): {Slug} v1 ({PlanId}) by {ActorUserId}",
            slug, plan.Id, principal.UserId);

        await _publisher.AppendAndPublishAsync(
            BuildPlanEvent(
                PlanCatalogEventTypes.CatalogUpdated,
                principal,
                new Dictionary<string, string?>
                {
                    ["action"] = "created",
                    ["slug"] = slug,
                    ["version"] = "1",
                    ["planId"] = plan.Id.ToString("D"),
                }),
            ct);

        return plan;
    }

    /// <inheritdoc />
    public async Task<Plan> VersionPlanAsync(
        string slug,
        PlanDraftSpec draft,
        PlanEditorPrincipal principal,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(principal);

        ValidateDraft(draft);

        // Reuse ALL of the supersede/deprecate versioning logic (and its
        // PLAN.VERSION.CREATED / PLAN.DEPRECATED events) — do NOT duplicate it.
        var newPlan = await CreateNewVersionAsync(slug, draft, principal, ct);

        // Add the admin-surface catalog event on top (AC9).
        await _publisher.AppendAndPublishAsync(
            BuildPlanEvent(
                PlanCatalogEventTypes.CatalogUpdated,
                principal,
                new Dictionary<string, string?>
                {
                    ["action"] = "versioned",
                    ["slug"] = slug,
                    ["version"] = newPlan.Version.ToString(),
                    ["planId"] = newPlan.Id.ToString("D"),
                    ["supersedesPlanId"] = newPlan.SupersedesPlanId?.ToString("D"),
                }),
            ct);

        return newPlan;
    }

    /// <inheritdoc />
    public async Task<Plan> CreateCustomVersionAsync(
        Guid tenantId,
        PlanDraftSpec draft,
        PlanEditorPrincipal principal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(principal);

        if (tenantId == Guid.Empty)
        {
            throw new TammaError(
                "PLAN.CUSTOM.TENANT_REQUIRED",
                "A custom plan must be bound to a non-empty tenant id.",
                new Dictionary<string, object?> { ["tenantId"] = tenantId.ToString("D") },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        ValidateDraft(draft);
        RequireDisplayName(draft);

        var slug = CustomPlanSlug.New(tenantId);
        var plan = await InsertNewV1PlanAsync(slug, draft, isCustom: true, ct);

        _logger.LogInformation(
            "Custom plan minted: {Slug} v1 ({PlanId}) bound to tenant {TenantId} by {ActorUserId}",
            slug, plan.Id, tenantId, principal.UserId);

        await _publisher.AppendAndPublishAsync(
            BuildPlanEvent(
                PlanCatalogEventTypes.CustomCreated,
                principal,
                new Dictionary<string, string?>
                {
                    ["slug"] = slug,
                    ["version"] = "1",
                    ["planId"] = plan.Id.ToString("D"),
                    ["tenantId"] = tenantId.ToString("D"),
                }),
            ct);

        return plan;
    }

    /// <inheritdoc />
    public async Task<PlanDeprecationResult> DeprecateVersionAsync(
        string slug,
        int version,
        bool force,
        PlanEditorPrincipal principal,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(principal);

        var plan = await _db.Plans
            .FirstOrDefaultAsync(p => p.Slug == slug && p.Version == version, ct);

        if (plan is null)
        {
            throw new TammaError(
                "PLAN.VERSION.NOT_FOUND",
                $"Plan '{slug}' v{version} does not exist.",
                new Dictionary<string, object?> { ["slug"] = slug, ["version"] = version },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }

        // Count tenants whose assignment PINS this exact version (the version-
        // pinned PlanId shadow column — Story 34-1/28). TODO(34-4): once tracked
        // assignment lands, switch this to the assignment table.
        var affected = await _db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.DeletedAt == null && EF.Property<Guid?>(t, "PlanId") == plan.Id)
            .CountAsync(ct);

        _logger.LogDebug(
            "Deprecate {Slug} v{Version}: status={Status}, affectedTenants={Affected}, force={Force}",
            slug, version, plan.Status, affected, force);

        // Already deprecated — idempotent success (no write, immutability holds).
        if (plan.Status == "deprecated")
        {
            return new PlanDeprecationResult(Deprecated: true, AffectedTenantCount: affected);
        }

        // Blocked: active assignments and no force. Re-pricing existing tenants is
        // never a silent side effect of catalog deprecation (immutability rule).
        if (affected > 0 && !force)
        {
            _logger.LogWarning(
                "Deprecate blocked: {Slug} v{Version} has {Affected} assigned tenant(s); pass force=true to override",
                slug, version, affected);
            return new PlanDeprecationResult(Deprecated: false, AffectedTenantCount: affected);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        plan.Status = "deprecated";
        plan.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Plan deprecated: {Slug} v{Version} ({PlanId}); {Affected} tenant(s) remain pinned; by {ActorUserId}",
            slug, version, plan.Id, affected, principal.UserId);

        await _publisher.AppendAndPublishAsync(
            BuildPlanEvent(
                PlanCatalogEventTypes.CatalogUpdated,
                principal,
                new Dictionary<string, string?>
                {
                    ["action"] = "deprecated",
                    ["slug"] = slug,
                    ["version"] = version.ToString(),
                    ["planId"] = plan.Id.ToString("D"),
                    ["affectedTenantCount"] = affected.ToString(),
                    ["force"] = force ? "true" : "false",
                }),
            ct);

        return new PlanDeprecationResult(Deprecated: true, AffectedTenantCount: affected);
    }

    /// <summary>
    /// Build + insert a fresh v1 <c>active</c> plan (with children) from
    /// <paramref name="draft"/>. Shared by the initial-create and custom-mint
    /// paths. The immutability interceptor permits a freshly-Added active plan
    /// and its children.
    /// </summary>
    private async Task<Plan> InsertNewV1PlanAsync(
        string slug, PlanDraftSpec draft, bool isCustom, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var newPlanId = Guid.NewGuid();

        var plan = new Plan
        {
            Id = newPlanId,
            Slug = slug,
            DisplayName = draft.DisplayName!,
            Version = 1,
            Status = "active",
            IsCustom = isCustom,
            BillingInterval = draft.BillingInterval ?? "monthly",
            SupersedesPlanId = null,
            MonthlyPriceUsd = draft.MonthlyPriceUsd ?? 0m,
            Quotas = "{}",
            IsActive = true,
            PlacementPolicy = draft.PlacementPolicy ?? "shared",
            CreatedAt = now,
            UpdatedAt = now,
            Features = (draft.Features ?? [])
                .Select(f => new PlanFeature
                {
                    Id = Guid.NewGuid(),
                    PlanId = newPlanId,
                    FeatureKey = f.FeatureKey,
                    BoolValue = f.BoolValue,
                    StringValue = f.StringValue,
                }).ToList(),
            Entitlements = (draft.Entitlements ?? [])
                .Select(e => new PlanEntitlement
                {
                    Id = Guid.NewGuid(),
                    PlanId = newPlanId,
                    MetricKey = e.MetricKey,
                    LimitValue = e.LimitValue,
                    Period = e.Period,
                    OverageMode = e.OverageMode,
                }).ToList(),
            Prices = (draft.Prices ?? [])
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

        _db.Plans.Add(plan);
        await _db.SaveChangesAsync(ct);
        return plan;
    }

    private static readonly string[] s_validPricingModes = { "platform_provided", "byok" };
    private static readonly string[] s_validBillingIntervals = { "monthly", "annual" };
    private static readonly string[] s_validPeriods = { "monthly", "total" };
    private static readonly string[] s_validOverageModes = { "block", "allow", "meter" };

    private static void RequireDisplayName(PlanDraftSpec draft)
    {
        if (string.IsNullOrWhiteSpace(draft.DisplayName))
        {
            throw new TammaError(
                "PLAN.DISPLAY_NAME.REQUIRED",
                "A new plan requires a non-empty display name.",
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }
    }

    /// <summary>
    /// Story 34-2 (AC8) — validate the closed-enum string fields BEFORE any write
    /// and fail loud with a stable code the endpoint maps to 422/400. The metric
    /// key is already a typed <c>EntitlementMetricKey</c> on the draft (the DTO
    /// mapping parsed + validated it), so it is not re-checked here.
    /// </summary>
    private static void ValidateDraft(PlanDraftSpec draft)
    {
        if (draft.BillingInterval is not null
            && !s_validBillingIntervals.Contains(draft.BillingInterval))
        {
            throw new TammaError(
                "PLAN.BILLING_INTERVAL.INVALID",
                $"Invalid billing interval '{draft.BillingInterval}' (expected: monthly | annual).",
                new Dictionary<string, object?> { ["billingInterval"] = draft.BillingInterval },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        if (draft.Prices is not null)
        {
            foreach (var p in draft.Prices)
            {
                if (!s_validPricingModes.Contains(p.PricingMode))
                {
                    throw new TammaError(
                        "PLAN.PRICING_MODE.INVALID",
                        $"Invalid pricing mode '{p.PricingMode}' (expected: platform_provided | byok).",
                        new Dictionary<string, object?> { ["pricingMode"] = p.PricingMode },
                        retryable: false,
                        severity: TammaErrorSeverity.High);
                }
            }
        }

        if (draft.Entitlements is not null)
        {
            foreach (var e in draft.Entitlements)
            {
                if (!s_validPeriods.Contains(e.Period))
                {
                    throw new TammaError(
                        "PLAN.ENTITLEMENT_PERIOD.INVALID",
                        $"Invalid entitlement period '{e.Period}' (expected: monthly | total) for metric '{e.MetricKey}'.",
                        new Dictionary<string, object?> { ["period"] = e.Period, ["metricKey"] = e.MetricKey.ToString() },
                        retryable: false,
                        severity: TammaErrorSeverity.High);
                }

                if (!s_validOverageModes.Contains(e.OverageMode))
                {
                    throw new TammaError(
                        "PLAN.OVERAGE_MODE.INVALID",
                        $"Invalid overage mode '{e.OverageMode}' (expected: block | allow | meter) for metric '{e.MetricKey}'.",
                        new Dictionary<string, object?> { ["overageMode"] = e.OverageMode, ["metricKey"] = e.MetricKey.ToString() },
                        retryable: false,
                        severity: TammaErrorSeverity.High);
                }
            }
        }
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
