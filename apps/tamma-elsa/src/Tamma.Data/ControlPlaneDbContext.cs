using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Tamma.Core;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Data.Entities;

namespace Tamma.Data;

/// <summary>
/// Control-plane <see cref="DbContext"/> that owns the tenant-agnostic
/// tables: users, tenants (registry), tenant memberships, invites, API keys
/// (CP-scoped), GitHub installations, webhook deliveries, and auth tokens.
///
/// <para>Epic 28 isolation model: control-plane data never carries a
/// <c>TenantId</c> filter — the rows themselves are either global (users,
/// plans) or organisational (tenants, memberships, invites). Per-tenant
/// business data (workflows, prompts, events, queued tasks, budgets,
/// diagnostics, etc.) lives on <see cref="TenantDbContext"/>, constructed
/// via <see cref="ITenantDbContextFactory"/>.</para>
///
/// <para>Supersedes the Epic 17 dual-context split (<c>TammaDbContext</c>
/// + <c>TammaAppDbContext</c> + RLS on shared DB). The db-per-tenant
/// architecture replaces RLS entirely — each tenant eventually gets its
/// own physical database with no tenant column and no row-level filter.</para>
/// </summary>
public class ControlPlaneDbContext : DbContext
{
    public ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options)
        : base(options)
    {
    }

    // ── Story 34-1 (AC6) — plan-immutability SaveChanges interceptor ──
    //
    // EnforcePlanImmutability scans the ChangeTracker before EVERY save and
    // throws PLAN.VERSION.IMMUTABLE for any mutation of an active/deprecated
    // Plan row or its child PlanFeature/PlanEntitlement/PlanPrice rows. The
    // application-level guard in PlanVersionEditor is no longer the only line
    // of defence: a raw db.Plans.First(active).MonthlyPriceUsd = 999; SaveChanges
    // now fails LOUD here. The single controlled active→deprecated flip the
    // version editor performs is allowed; children of a freshly-Added active
    // plan (the new version's rows) are allowed.

    // Story 34-1 follow-up — leak-proof immutability-guard suppression.
    //
    // The context is POOLED in production (AddPooledDbContextFactory /
    // AddTenantConnectionPool). EF's pool reset only clears state it knows about
    // (the ChangeTracker, the connection, IResettableService services) — it does
    // NOT clear a custom bool field on the derived context. A plain settable
    // `SuppressPlanImmutabilityGuard = true` left dangling (e.g. by a caller that
    // forgot a finally, or an exception that skipped the reset) would silently
    // ride the pooled instance into the NEXT lease and disable the guard for an
    // unrelated caller. So the suppression is exposed ONLY as a disposable scope:
    // the caller writes `using var _ = ctx.SuppressPlanImmutabilityGuard();` and
    // the scope's Dispose ALWAYS restores the prior value — even on exception,
    // because `using` is a try/finally — so a caller physically cannot leave it
    // set and it can never ride a pooled instance into the next lease. There is
    // no public setter at all: the only way to flip the flag is to open (and
    // therefore, via `using`, automatically close) a scope.
    private bool _suppressPlanImmutabilityGuard;

    /// <summary>
    /// Story 34-1 — escape hatch for the trusted <c>PlansSeeder</c> system-
    /// defaults populate path ONLY. The seeder does insert-missing-only backfill
    /// of children onto an already-active v1 plan (Story 28-1 shipped the bare
    /// plan rows; 34-1 backfills the typed children), which the immutability
    /// interceptor would otherwise (correctly) reject.
    ///
    /// <para>Returns a disposable scope that suppresses the guard for its
    /// lifetime and restores the prior value on <see cref="IDisposable.Dispose"/>
    /// — guaranteed, even on exception, via <c>using</c>. There is intentionally
    /// no public setter: a caller physically cannot leave the flag stuck on,
    /// which keeps the guard leak-proof across pooled-context reuse. No
    /// user-facing or request-handling code should ever open this scope.</para>
    /// </summary>
    public IDisposable SuppressPlanImmutabilityGuard()
        => new PlanImmutabilityGuardSuppressionScope(this);

    private sealed class PlanImmutabilityGuardSuppressionScope : IDisposable
    {
        private readonly ControlPlaneDbContext _ctx;
        private readonly bool _previous;
        private bool _disposed;

        public PlanImmutabilityGuardSuppressionScope(ControlPlaneDbContext ctx)
        {
            _ctx = ctx;
            _previous = ctx._suppressPlanImmutabilityGuard;
            ctx._suppressPlanImmutabilityGuard = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ctx._suppressPlanImmutabilityGuard = _previous;
        }
    }

    /// <summary>
    /// Story 35-1 follow-up — resolve the
    /// <c>PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning</c>
    /// advisory cleanly. Apply this from EVERY <see cref="DbContextOptions{TContext}"/>
    /// builder seam that constructs a <see cref="ControlPlaneDbContext"/>: the DI
    /// factory (<c>AddTammaData</c>), the pooled factory (<c>AddTenantConnectionPool</c>),
    /// and the design-time factory used by <c>ef migrations</c>.
    ///
    /// <para><b>What fires it.</b> <c>Tenant</c> and <c>User</c> carry a
    /// soft-delete query filter (<c>DeletedAt == null</c>). Their REQUIRED
    /// dependents — <c>BillingCustomer</c> (Story 35-1), plus the pre-existing
    /// <c>TenantMembership</c>, <c>UserInvite</c>, <c>RefreshToken</c>,
    /// <c>PasswordResetToken</c> — have a non-nullable FK (so EF marks the nav
    /// <c>IsRequired()</c>) but NO soft-delete column of their own, so they carry
    /// no matching filter. EF warns that an <c>Include</c> of the filtered
    /// principal could yield a null required navigation. That is an ACCEPTED
    /// pattern here: the dependents are hard-deleted on cascade when the tenant/
    /// user is HARD-deleted; while a principal is merely SOFT-deleted the
    /// dependent legitimately still exists and an <c>Include</c> returning null is
    /// the correct, intended behaviour.</para>
    ///
    /// <para><b>Why suppress, and why on the OPTIONS builder (not
    /// <c>OnModelCreating</c>/<c>TammaModelConfiguration</c>, and NOT
    /// <c>OnConfiguring</c>).</b> The two model-level alternatives both fail the
    /// Wave-1 constraints: (1) adding a matching query filter is impossible —
    /// these dependents have no <c>DeletedAt</c> column; (2) marking the
    /// navigation <c>.IsRequired(false)</c> would make the FK column nullable, i.e.
    /// a SCHEMA change + a new migration, which the cascade-preservation
    /// constraint forbids. So the only constraint-satisfying resolution is to
    /// ignore this one advisory. <c>ConfigureWarnings</c> is a
    /// <see cref="DbContextOptionsBuilder"/> API — a <c>ModelBuilder</c> has no
    /// warning surface, so it CANNOT live in <c>TammaModelConfiguration</c>. It
    /// also CANNOT live in an <c>OnConfiguring</c> override: this context is
    /// registered through <c>AddPooledDbContextFactory</c> in production, and EF
    /// throws <c>"'OnConfiguring' cannot be used to modify DbContextOptions when
    /// DbContext pooling is enabled"</c>. The options-builder seam is the only
    /// place that is both pooling-safe AND applies uniformly to all five
    /// relationships. Schema, cascade behaviour, and query semantics are
    /// untouched; <c>has-pending-model-changes</c> stays clean.</para>
    /// </summary>
    public static void ConfigureControlPlaneWarnings(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId
                .PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (!_suppressPlanImmutabilityGuard)
        {
            // Compute the untracked-owning-plan id set ONCE, then resolve its
            // statuses synchronously and pass both through to the enforcer.
            var untrackedIds = CollectUntrackedOwningPlanIds();
            var untrackedStatus = untrackedIds.Count == 0
                ? new Dictionary<Guid, string>()
                : Plans.AsNoTracking()
                    .Where(p => untrackedIds.Contains(p.Id))
                    .Select(p => new { p.Id, p.Status })
                    .ToDictionary(x => x.Id, x => x.Status);

            EnforcePlanImmutability(untrackedStatus);
            EnforceProviderPriceImmutability();
        }
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        if (!_suppressPlanImmutabilityGuard)
        {
            // Compute the untracked-owning-plan id set ONCE here (the async path
            // previously computed it both in the pre-pass AND again inside
            // EnforcePlanImmutability). Resolve statuses via the async DB lookup,
            // then pass the SAME id set + status map into the synchronous enforcer
            // so it never recomputes or blocks on the DB.
            var untrackedIds = CollectUntrackedOwningPlanIds();
            var untrackedStatus = await ResolveUntrackedPlanStatusAsync(
                untrackedIds, cancellationToken);

            EnforcePlanImmutability(untrackedStatus);
            EnforceProviderPriceImmutability();
        }
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Async resolution of the owning-plan statuses for a pre-computed set of
    /// untracked plan ids — used by <see cref="SaveChangesAsync(bool, CancellationToken)"/>
    /// so the synchronous <see cref="EnforcePlanImmutability"/> body never blocks
    /// on the DB. Empty input ⇒ no query.
    /// </summary>
    private async Task<Dictionary<Guid, string>> ResolveUntrackedPlanStatusAsync(
        HashSet<Guid> untrackedIds, CancellationToken ct)
    {
        if (untrackedIds.Count == 0) return new Dictionary<Guid, string>();

        var rows = await Plans.AsNoTracking()
            .Where(p => untrackedIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Status })
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Id, x => x.Status);
    }

    /// <summary>
    /// Owning-plan ids of changed child rows that are NOT represented by a
    /// tracked <see cref="Plan"/> entry — these need a DB status lookup.
    /// </summary>
    private HashSet<Guid> CollectUntrackedOwningPlanIds()
    {
        var trackedPlanIds = new HashSet<Guid>(
            ChangeTracker.Entries<Plan>().Select(e => e.Entity.Id));

        var needed = new HashSet<Guid>();
        foreach (var planId in EnumerateChangedChildOwningPlanIds())
        {
            if (!trackedPlanIds.Contains(planId)) needed.Add(planId);
        }
        return needed;
    }

    private IEnumerable<Guid> EnumerateChangedChildOwningPlanIds()
    {
        foreach (var e in ChangeTracker.Entries<PlanFeature>())
            if (e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                yield return e.Entity.PlanId;
        foreach (var e in ChangeTracker.Entries<PlanEntitlement>())
            if (e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                yield return e.Entity.PlanId;
        foreach (var e in ChangeTracker.Entries<PlanPrice>())
            if (e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                yield return e.Entity.PlanId;
    }

    /// <summary>
    /// Story 34-1 AC6 — throws <c>PLAN.VERSION.IMMUTABLE</c> (High) when the
    /// pending change set mutates an immutable plan version or its children.
    /// </summary>
    /// <param name="untrackedStatus">
    /// DB status of the owning plans of changed child rows that are NOT
    /// represented by a tracked <see cref="Plan"/> entry. The caller computes the
    /// id set ONCE (<see cref="CollectUntrackedOwningPlanIds"/>) and resolves the
    /// statuses (synchronously for <see cref="SaveChanges"/>, via an async DB
    /// lookup for <see cref="SaveChangesAsync(bool, CancellationToken)"/>).
    /// </param>
    private void EnforcePlanImmutability(
        IReadOnlyDictionary<Guid, string> untrackedStatus)
    {
        // 1. Direct Plan mutations.
        foreach (var entry in ChangeTracker.Entries<Plan>())
        {
            if (entry.State != EntityState.Modified) continue;

            var originalStatus = entry.OriginalValues.GetValue<string>(nameof(Plan.Status));
            if (originalStatus is not ("active" or "deprecated")) continue;

            // The ONLY permitted change to an immutable row is the controlled
            // active→deprecated flip the version editor performs: Status flips
            // active→deprecated and nothing else changes except UpdatedAt.
            if (IsControlledDeprecationFlip(entry, originalStatus))
            {
                continue;
            }

            ThrowImmutable(entry.Entity.Slug, entry.Entity.Version,
                originalStatus, entry.Entity.Id, "plan");
        }

        // 2. Child mutations (insert / update / delete). The untracked-owning-plan
        // ids + their DB statuses were computed ONCE by the caller and passed in
        // as untrackedStatus (the async path used to recompute the id set here as
        // well — see Fix #4).
        var trackedPlanStatus = ChangeTracker.Entries<Plan>()
            .ToDictionary(e => e.Entity.Id, e => (e.State, e.Entity.Status));

        EnforceChildren<PlanFeature>(e => e.PlanId, trackedPlanStatus, untrackedStatus);
        EnforceChildren<PlanEntitlement>(e => e.PlanId, trackedPlanStatus, untrackedStatus);
        EnforceChildren<PlanPrice>(e => e.PlanId, trackedPlanStatus, untrackedStatus);
    }

    private void EnforceChildren<TChild>(
        Func<TChild, Guid> planIdSelector,
        IReadOnlyDictionary<Guid, (EntityState State, string Status)> trackedPlanStatus,
        IReadOnlyDictionary<Guid, string> untrackedStatus)
        where TChild : class
    {
        foreach (var entry in ChangeTracker.Entries<TChild>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var planId = planIdSelector(entry.Entity);

            // The new version's children attach to a freshly-Added active plan
            // — those are part of creating a new immutable version, so allow.
            if (trackedPlanStatus.TryGetValue(planId, out var tracked))
            {
                if (tracked.State == EntityState.Added) continue;
                if (tracked.Status is "active" or "deprecated")
                {
                    ThrowImmutable("(child)", null, tracked.Status, planId, typeof(TChild).Name);
                }
                continue;
            }

            // Owning plan not tracked — use the DB status. A child of an
            // already-persisted active/deprecated plan is immutable.
            if (untrackedStatus.TryGetValue(planId, out var dbStatus)
                && dbStatus is "active" or "deprecated")
            {
                ThrowImmutable("(child)", null, dbStatus, planId, typeof(TChild).Name);
            }
        }
    }

    /// <summary>
    /// True iff this Modified Plan entry is the controlled active→deprecated
    /// flip the version editor performs: Status changes from <c>active</c> to
    /// <c>deprecated</c> and no other property changes except <c>UpdatedAt</c>.
    /// </summary>
    private static bool IsControlledDeprecationFlip(
        EntityEntry<Plan> entry, string originalStatus)
    {
        if (originalStatus != "active") return false;
        if (entry.Entity.Status != "deprecated") return false;

        foreach (var prop in entry.Properties)
        {
            if (!prop.IsModified) continue;
            var name = prop.Metadata.Name;
            if (name is nameof(Plan.Status) or nameof(Plan.UpdatedAt)) continue;
            // Any other modified column means this is NOT a pure flip.
            return false;
        }
        return true;
    }

    private static void ThrowImmutable(
        string slug, int? version, string status, Guid planId, string entityKind)
    {
        var versionLabel = version is null ? string.Empty : $" v{version}";
        throw new TammaError(
            "PLAN.VERSION.IMMUTABLE",
            $"Plan '{slug}'{versionLabel} is {status} and immutable — its {entityKind} rows "
            + "cannot be inserted, updated, or deleted in place. Create a new version "
            + "via PlanVersionEditor instead.",
            new Dictionary<string, object?>
            {
                ["slug"] = slug,
                ["version"] = version,
                ["status"] = status,
                ["planId"] = planId.ToString("D"),
                ["entityKind"] = entityKind,
            },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>
    /// Story 34-11 (I1) — the AUTHORITATIVE provider-cost immutability guard,
    /// mirroring <see cref="EnforcePlanImmutability"/>. Throws
    /// <c>PROVIDER.PRICE.IMMUTABLE</c> for ANY content mutation of an
    /// already-<c>superseded</c> <see cref="ProviderModelPrice"/> row — a raw EF
    /// <c>UPDATE</c> that bypasses the admin endpoint must STILL fail loud, not
    /// succeed silently. The ONLY permitted change to an immutable row is the
    /// controlled <c>active → superseded</c> flip the <c>VersionPrice</c> path
    /// performs (Status + UpdatedAt only); anything else is rejected.
    /// <para>No DB lookup needed — <see cref="ProviderModelPrice"/> has no child
    /// rows and the original status is available from the change-tracker.</para>
    /// </summary>
    private void EnforceProviderPriceImmutability()
    {
        foreach (var entry in ChangeTracker.Entries<ProviderModelPrice>())
        {
            if (entry.State != EntityState.Modified) continue;

            var originalStatus = entry.OriginalValues.GetValue<string>(nameof(ProviderModelPrice.Status));

            // An already-superseded row is fully immutable: any modification throws.
            if (originalStatus == "superseded")
            {
                ThrowPriceImmutable(entry.Entity, originalStatus);
                continue;
            }

            // An active row may ONLY undergo the controlled active→superseded flip
            // (Status + UpdatedAt). Any other field change — including smuggling a
            // rate edit through the supersede path — is rejected.
            if (originalStatus == "active")
            {
                if (IsControlledSupersedeFlip(entry)) continue;
                ThrowPriceImmutable(entry.Entity, originalStatus);
            }
        }
    }

    /// <summary>
    /// True iff this Modified <see cref="ProviderModelPrice"/> entry is the
    /// controlled <c>active → superseded</c> flip: Status changes from
    /// <c>active</c> to <c>superseded</c> and no other property changes except
    /// <c>UpdatedAt</c> (mirrors <see cref="IsControlledDeprecationFlip"/>).
    /// </summary>
    private static bool IsControlledSupersedeFlip(EntityEntry<ProviderModelPrice> entry)
    {
        if (entry.Entity.Status != "superseded") return false;

        foreach (var prop in entry.Properties)
        {
            if (!prop.IsModified) continue;
            var name = prop.Metadata.Name;
            if (name is nameof(ProviderModelPrice.Status) or nameof(ProviderModelPrice.UpdatedAt))
                continue;
            // Any other modified column means this is NOT a pure flip.
            return false;
        }
        return true;
    }

    private static void ThrowPriceImmutable(ProviderModelPrice price, string status)
    {
        throw new TammaError(
            "PROVIDER.PRICE.IMMUTABLE",
            $"Provider cost price {price.Id:D} ({price.ProviderKey}/{price.Model}) is "
            + $"{status} and immutable — version a new price instead of editing it in place.",
            new Dictionary<string, object?>
            {
                ["priceId"] = price.Id.ToString("D"),
                ["providerKey"] = price.ProviderKey,
                ["model"] = price.Model,
                ["status"] = status,
            },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    // ── Control-plane entities (Doc 01 §1.2 — exactly 14 tables) ──
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<UserInvite> UserInvites => Set<UserInvite>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<GitHubInstallation> GitHubInstallations => Set<GitHubInstallation>();
    public DbSet<GitHubInstallationRepo> GitHubInstallationRepos => Set<GitHubInstallationRepo>();
    public DbSet<GitHubWebhookDelivery> GitHubWebhookDeliveries => Set<GitHubWebhookDelivery>();

    /// <summary>
    /// Story 31-2 — generalised per-(tenant, platform_kind) installation
    /// registry. The <see cref="GitHubInstallation"/> table stays for
    /// 31-3 dual-read; new platform bindings (Gitea/Forgejo/GitLab/etc.)
    /// land here.
    /// </summary>
    public DbSet<TenantPlatformInstallation> TenantPlatformInstallations =>
        Set<TenantPlatformInstallation>();

    /// <summary>
    /// Story 31-7 — cross-platform webhook delivery idempotency journal.
    /// Generalises <see cref="GitHubWebhookDeliveries"/>; the older
    /// table stays for the deprecation window but new deliveries land
    /// in this table for every <c>PlatformKind</c>.
    /// </summary>
    public DbSet<PlatformWebhookDelivery> PlatformWebhookDeliveries =>
        Set<PlatformWebhookDelivery>();

    /// <summary>
    /// Unified-tenancy Phase 0 — operator DB pool registry. One row per
    /// Postgres database available for tenant-schema placement.
    /// </summary>
    public DbSet<TenantDatabase> TenantDatabases => Set<TenantDatabase>();

    // ── Control-plane platform tables (Story 28-6 + 28-10) ──
    //
    // These three tables (platform_events, platform_queued_tasks,
    // platform_email_outbox) own cross-tenant / pre-tenant-resolution work.
    // They never live on a TenantDbContext — they are the control plane's
    // durable scratchpad for lifecycle events, installation-routing tasks,
    // and system-scope mail that must flow before or after a tenant DB
    // exists. The Plans table owns the subscription-plan catalogue.
    public DbSet<Plan> Plans => Set<Plan>();

    /// <summary>
    /// Story 34-1 — typed feature flags per plan version (replaces the opaque
    /// <c>Plan.Quotas</c> JSON for capability flags). CP-resident.
    /// </summary>
    public DbSet<PlanFeature> PlanFeatures => Set<PlanFeature>();

    /// <summary>
    /// Story 34-1 — typed quota entitlements per plan version. CP-resident.
    /// </summary>
    public DbSet<PlanEntitlement> PlanEntitlements => Set<PlanEntitlement>();

    /// <summary>
    /// Story 34-1 — recurring + per-seat + metered pricing per plan version,
    /// split by BYOK vs platform-provided pricing mode. CP-resident.
    /// </summary>
    public DbSet<PlanPrice> PlanPrices => Set<PlanPrice>();

    /// <summary>
    /// Story 34-4 — audited, version-pinned per-tenant plan assignments. The
    /// SOURCE OF TRUTH for "what plan version is this tenant on right now"
    /// (replaces the loose <c>Tenant.Plan</c> string + the Epic-28 <c>PlanId</c>
    /// shadow column, which are kept in lockstep as a cache). CP-resident. At
    /// most one <c>active</c> row per tenant (partial unique index).
    /// </summary>
    public DbSet<TenantPlanAssignment> TenantPlanAssignments => Set<TenantPlanAssignment>();

    public DbSet<PlatformEvent> PlatformEvents => Set<PlatformEvent>();
    public DbSet<PlatformQueuedTask> PlatformQueuedTasks => Set<PlatformQueuedTask>();
    public DbSet<PlatformEmailOutboxMessage> PlatformEmailOutbox => Set<PlatformEmailOutboxMessage>();

    /// <summary>
    /// Story 38-3 — control-plane Slack notification outbox. The fire-and-forget
    /// analogue of <see cref="PlatformEmailOutbox"/>; the engine writes intent via
    /// <c>POST /api/v1/notifications/slack</c> and <c>OutboxSlackSender</c> (the
    /// sole webhook-credential holder) drains it. CP-resident so it delivers
    /// regardless of tenant-DB routing (same rationale as the email outbox).
    /// </summary>
    public DbSet<SlackOutboxMessage> SlackOutbox => Set<SlackOutboxMessage>();

    /// <summary>
    /// Story 28-10 fact table — one row per <c>(Hour, TenantId)</c> tuple,
    /// populated hourly by <c>HourlyAnalyticsRollupWorkflow</c>. Platform-wide
    /// rows carry <c>TenantId = null</c>. See
    /// <see cref="Entities.PlatformAnalyticsHourly"/> for the column catalogue.
    /// </summary>
    public DbSet<PlatformAnalyticsHourly> PlatformAnalyticsHourly => Set<PlatformAnalyticsHourly>();

    /// <summary>
    /// Story 28-7 deferred-item routing table: O(1) prefix → tenant+apiKey
    /// lookups for <c>ApiKeyAuthHandler</c>. See
    /// <see cref="PlatformApiKeyIndex"/> for row semantics.
    /// </summary>
    public DbSet<PlatformApiKeyIndex> PlatformApiKeyIndex => Set<PlatformApiKeyIndex>();

    /// <summary>
    /// R2-H14: durable record of in-flight + completed KEK rotations.
    /// The <c>StagedSecondaryProtected</c> column lets a coordinator
    /// recover from a process crash mid-rotation without losing the
    /// new KEK material.
    /// </summary>
    public DbSet<KekRotation> KekRotations => Set<KekRotation>();

    /// <summary>
    /// Story 28-R2 follow-up B — SOC2 / ISO 27001 audit table for
    /// platform-admin impersonation sessions. One row per session
    /// (start = INSERT; end = UPDATE setting <c>EndedAt</c> /
    /// <c>EndedReason</c>). The active subset is indexed via a
    /// partial index on <c>EndedAt IS NULL</c> for cheap
    /// incident-response queries. See
    /// <see cref="Entities.AdminImpersonation"/> for column semantics.
    /// </summary>
    public DbSet<AdminImpersonation> AdminImpersonations =>
        Set<AdminImpersonation>();

    /// <summary>
    /// Story 28-R2 / PF-S9 — single-row sentinel that pins which user
    /// owns the bootstrap superadmin promotion. <c>CHECK (Id = 1)</c>
    /// + unique primary key force the schema to admit at most one row,
    /// closing the previous TOCTOU race where concurrent
    /// first-user-ever registrations both observed
    /// <c>existingUserCount == 0</c> and both received
    /// <c>platform_admin</c>. See <see cref="Entities.PlatformBootstrap"/>.
    /// </summary>
    public DbSet<PlatformBootstrap> PlatformBootstraps =>
        Set<PlatformBootstrap>();

    // ── Story 5.6 + 1.5-37 (Wave C.1) — alert system ──
    //
    // The three alert tables are CP-resident: alert rules + channels
    // fan out across tenants, and platform-scoped alerts (TenantId=null)
    // must live somewhere every tenant dashboard can't read directly.
    // Tenant-scoped alerts still carry TenantId and are routed into the
    // tenant-facing UI (Wave C.3) via explicit TenantId filters.

    /// <summary>
    /// Story 5.6 — raised alerts. One row per lifecycle (active →
    /// acknowledged → resolved).
    /// </summary>
    public DbSet<Alert> Alerts => Set<Alert>();

    /// <summary>
    /// Story 1.5-37 — configured delivery targets. Credentials live
    /// in the secret store via <c>CredentialsSecretId</c>, never in
    /// the <c>Config</c> column.
    /// </summary>
    public DbSet<AlertChannel> AlertChannels => Set<AlertChannel>();

    /// <summary>
    /// Story 1.5-37 — audit log of every delivery attempt.
    /// <c>NotificationDispatcher</c> drains <c>pending</c>/<c>failed</c>
    /// rows; <c>dropped_rate_limit</c> rows are audited-only.
    /// </summary>
    public DbSet<AlertDeliveryAttempt> AlertDeliveryAttempts =>
        Set<AlertDeliveryAttempt>();

    // ── Story 5.6 (Wave C.2) — alert rule engine ──
    //
    // Rules + evaluator cursor both live on the control plane: rules
    // fan out across tenants, and the cursor is the single control-
    // plane-wide progress marker for the <c>AlertRuleEvaluator</c>.

    /// <summary>Story 5.6 (Wave C.2) — alert rules.</summary>
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();

    /// <summary>Story 5.6 (Wave C.2) — evaluator progress cursor.</summary>
    public DbSet<AlertEvaluatorCursor> AlertEvaluatorCursors =>
        Set<AlertEvaluatorCursor>();

    // ── Story 32-1 — first-class agent entities (Epic 32 foundation) ──
    //
    // Agent definitions (public/system + private/tenant-or-user-owned) are
    // CP-resident: visibility/identity is a control-plane concern and public
    // agents are shared cross-tenant. ALL performance/action data stays
    // tenant-scoped in later Epic 32 stories — these tables are definition-only.

    /// <summary>
    /// Story 32-1 — first-class agent identities (replaces the anonymous
    /// role-keyed <see cref="AgentConfig"/> blob as the canonical Epic 32
    /// join key). CP-resident; see <see cref="Entities.Agent"/>.
    /// </summary>
    public DbSet<Agent> Agents => Set<Agent>();

    /// <summary>
    /// Story 32-1 — immutable, monotonically-versioned saved-config snapshots
    /// per agent. Insert-only; see <see cref="Entities.AgentVersion"/>.
    /// </summary>
    public DbSet<AgentVersion> AgentVersions => Set<AgentVersion>();

    /// <summary>
    /// Story 32-2 — role→agent selections. On the CP context this holds the
    /// single-user (user-keyed) rows; the SAME table also lives in every tenant
    /// schema for SaaS (tenant-keyed) rows. See
    /// <see cref="Entities.AgentRoleSelection"/>.
    /// </summary>
    public DbSet<AgentRoleSelection> AgentRoleSelections => Set<AgentRoleSelection>();

    /// <summary>
    /// Story 32-16 — per-tenant agent/persona ENABLEMENT (catalog membership:
    /// which PUBLIC personas a principal exposes). CP-resident in BOTH modes
    /// (SaaS rows keyed by <c>TenantId</c>, single-user rows by <c>UserId</c>) —
    /// it gates the CP-resident public agent catalog and is not a
    /// <c>t_&lt;hex&gt;</c> tenant-private row. See
    /// <see cref="Entities.TenantAgentEnablement"/>.
    /// </summary>
    public DbSet<TenantAgentEnablement> TenantAgentEnablements => Set<TenantAgentEnablement>();

    // ── Story 35-1 — billing foundation (Epic 35) ──
    //
    // The tenant→Stripe customer mapping + the slug→Stripe-ids catalog are
    // CP-resident: billing is a cross-cutting platform concern, the catalog is
    // platform-global, and the customer binding is keyed by tenant (not
    // tenant-resident business data). SaaS only — single-user never writes here.

    /// <summary>
    /// Story 35-1 — one row per tenant binding it to its Stripe customer.
    /// Unique <c>TenantId</c>; see <see cref="Entities.BillingCustomer"/>.
    /// </summary>
    public DbSet<BillingCustomer> BillingCustomers => Set<BillingCustomer>();

    /// <summary>
    /// Story 35-1 — slug→Stripe Product/Price/Meter id catalog. Unique
    /// <c>PlanSlug</c>; platform-global. See <see cref="Entities.BillingPlanPrice"/>.
    /// </summary>
    public DbSet<BillingPlanPrice> BillingPlanPrices => Set<BillingPlanPrice>();

    /// <summary>
    /// Story 35-5 — Stripe webhook dedup + audit journal. Unique
    /// <c>StripeEventId</c> makes at-least-once redelivery a no-op ack. See
    /// <see cref="Entities.BillingWebhookEvent"/>.
    /// </summary>
    public DbSet<BillingWebhookEvent> BillingWebhookEvents => Set<BillingWebhookEvent>();

    /// <summary>
    /// Story 35-4 — the control-plane mirror of a tenant's Stripe subscription.
    /// At most one non-terminal row per tenant (partial-unique on
    /// <c>TenantId</c>). Story 35-6 reads <c>PlanSlug</c> + <c>Seats</c> as the
    /// single quota source. See <see cref="Entities.BillingSubscription"/>.
    /// </summary>
    public DbSet<BillingSubscription> BillingSubscriptions => Set<BillingSubscription>();

    /// <summary>
    /// Story 37-1 — platform-scope curated audit trail. Tenant-scope rows live
    /// in the per-tenant schema's <c>audit_records</c>; these are the
    /// platform/lifecycle rows (impersonation, tenant provision/move, etc.).
    /// </summary>
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    /// <summary>
    /// Story 37-1 — the audit projector's resume cursor (mirrors
    /// <see cref="AlertEvaluatorCursor"/>). CP-resident; one row per projector.
    /// </summary>
    public DbSet<AuditProjectorCursor> AuditProjectorCursors => Set<AuditProjectorCursor>();

    /// <summary>
    /// Story 37-2 — signed chain checkpoints for BOTH platform and tenant
    /// scopes. Always CP-resident (a tenant cannot rewrite an anchor stored
    /// outside its own schema); <c>tenant_id</c> discriminates the scope.
    /// </summary>
    public DbSet<AuditChainCheckpoint> AuditChainCheckpoints => Set<AuditChainCheckpoint>();

    /// <summary>
    /// Story 34-11 — the provider COST identity (platform-global, NOT
    /// tenant-scoped). Promotes the frozen <c>ProviderPricingService</c> rate
    /// sheet to a first-class, admin-editable entity behind the unchanged
    /// <c>IProviderPricingService</c> seam.
    /// </summary>
    public DbSet<Provider> Providers => Set<Provider>();

    /// <summary>
    /// Story 34-11 — per-model versioned COST rows (USD-per-1M). Immutable +
    /// EffectiveFrom-windowed: an edit supersedes rather than mutates so a usage
    /// event prices under the rate active at its OccurredAt (reproducible).
    /// </summary>
    public DbSet<ProviderModelPrice> ProviderModelPrices => Set<ProviderModelPrice>();

    /// <summary>
    /// Story 34-5 — versioned platform margin policies (global/plan/provider
    /// scope) applied by the cost->price engine. Platform-global (no TenantId);
    /// immutable + EffectiveFrom-windowed like <see cref="ProviderModelPrices"/>.
    /// </summary>
    public DbSet<MarginPolicy> MarginPolicies => Set<MarginPolicy>();

    /// <summary>
    /// Story 34-3 — the authoritative per-<c>(tenant, provider)</c> billing-mode
    /// owner (BYOK vs platform-provided). CP-resident (keyed by tenant). Single
    /// source of truth the pricing-mode resolver + the 35-2 billing-mode tagger
    /// read; the 32-3 runtime credential resolver reports what actually resolved
    /// and is reconciled against this declared intent.
    /// </summary>
    public DbSet<TenantProviderBilling> TenantProviderBillings => Set<TenantProviderBilling>();

    // Story 28-1 PR D: the 11 + 4 mentorship tenant-resident entities
    // (AgentConfig, PromptOverride, ProviderHealth, ProviderDiagnostic,
    // SanitizationRule, WorkflowDefinition, WorkflowInstance, DomainEvent,
    // QueuedTask, EmailOutboxMessage, BudgetConfig + MentorshipSession,
    // MentorshipEvent, JuniorDeveloper, Story) have left the control plane
    // entirely and now live exclusively on TenantDbContext. They are NOT
    // in the CP model graph — the matching DbSet shim properties below are
    // retained ONLY so legacy test fixtures and any not-yet-migrated
    // consumers still compile. Any actual query / SaveChanges through these
    // shim DbSets throws at runtime ("The entity type X cannot be used
    // since it has been excluded from the model.") which is exactly the
    // failure shape we want — it forces the call site to migrate to
    // ITenantDbContextFactory.

    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<AgentConfig> AgentConfigs => Set<AgentConfig>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<PromptOverride> PromptOverrides => Set<PromptOverride>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<ProviderHealth> ProviderHealths => Set<ProviderHealth>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<ProviderDiagnostic> ProviderDiagnostics => Set<ProviderDiagnostic>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<SanitizationRule> SanitizationRules => Set<SanitizationRule>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<Entities.DomainEvent> DomainEvents => Set<Entities.DomainEvent>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<QueuedTask> QueuedTasks => Set<QueuedTask>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<BudgetConfig> BudgetConfigs => Set<BudgetConfig>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<MentorshipSession> MentorshipSessions => Set<MentorshipSession>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<MentorshipEvent> MentorshipEvents => Set<MentorshipEvent>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<JuniorDeveloper> JuniorDevelopers => Set<JuniorDeveloper>();
    /// <summary>Compile-time shim — entity ignored on CP per Story 28-1 PR D.</summary>
    public DbSet<Story> Stories => Set<Story>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        TammaModelConfiguration.ConfigureControlPlaneEntities(
            modelBuilder, includeTenantShadowColumns: true);
        // Story 28-1 PR D: explicitly ignore the moved POCOs so EF's
        // convention-based discovery doesn't re-pick them up via navigation
        // properties on Tenant. The CP model contract is now exactly the
        // CP-resident table list (Doc 01 §1.2 + alerts + KEK + impersonation
        // + bootstrap + analytics + apikey index). The DbSet shim properties
        // above remain only as a compile-time bridge for not-yet-migrated
        // call sites; runtime queries throw with a clear "entity excluded
        // from model" diagnostic.
        TammaModelConfiguration.IgnoreLegacyAndMentorshipEntities(modelBuilder);

        ConfigurePlatformAnalyticsHourly(modelBuilder);
        ConfigurePlatformApiKeyIndex(modelBuilder);
        ConfigureAlerts(modelBuilder);
        ConfigureAlertChannels(modelBuilder);
        ConfigureAlertDeliveryAttempts(modelBuilder);
        ConfigureAlertRules(modelBuilder);
        ConfigureAlertEvaluatorCursor(modelBuilder);
        ConfigureKekRotations(modelBuilder);
        ConfigurePlatformBootstrap(modelBuilder);
        ConfigureTenantPlatformInstallations(modelBuilder);
        ConfigurePlatformWebhookDeliveries(modelBuilder);
        ConfigureTenantDatabases(modelBuilder);

        // Story 32-1 — first-class agent entities. CP-resident, configured in
        // the shared single source so the model graph + migration stay aligned.
        TammaModelConfiguration.ConfigureAgentEntities(modelBuilder);

        // Story 32-2 — agent_role_selections. Dual-resident (CP for single-user
        // user-keyed rows; tenant schema for SaaS tenant-keyed rows — same SAME
        // shape on both, mirroring audit_records). fixedTenantId: null = the CP
        // build.
        TammaModelConfiguration.ConfigureAgentRoleSelections(modelBuilder, fixedTenantId: null);

        // Story 32-16 — per-tenant agent/persona enablement (catalog membership).
        // CP-resident in BOTH modes (gates the CP public agent catalog; keyed by
        // tenant id / user id, not per t_<hex>) — configured ONLY here, never on
        // the tenant context.
        TammaModelConfiguration.ConfigureTenantAgentEnablements(modelBuilder);

        // Story 35-1 — billing customer mapping + Stripe catalog. CP-resident,
        // configured in the shared single source so the model graph + migration
        // stay aligned (same convention as the agent entities above).
        TammaModelConfiguration.ConfigureBillingEntities(modelBuilder);

        // Story 37-1 — platform-scope curated audit trail + the projector cursor.
        // audit_records carries the SAME shape as the tenant-schema table; the
        // CP build hosts platform-scope rows (impersonation, tenant lifecycle).
        // The cursor is CP-resident (mirrors alert_evaluator_cursor) — the single
        // projector resumes both streams from one row.
        TammaModelConfiguration.ConfigureAuditEntities(modelBuilder, fixedTenantId: null);
        TammaModelConfiguration.ConfigureAuditProjectorCursor(modelBuilder);
        // Story 37-2 — signed chain checkpoints (CP-resident for every scope).
        TammaModelConfiguration.ConfigureAuditChainCheckpoint(modelBuilder);

        // Story 34-11 — provider COST price-book. CP-resident, platform-global
        // (no TenantId): cost is the provider's published rate, identical for
        // every tenant. Mirrors the ConfigurePlans versioning pattern.
        ConfigureProviders(modelBuilder);
        ConfigureProviderModelPrices(modelBuilder);

        // Story 34-5 — versioned platform margin policies (sell-side). Same
        // CP-resident, platform-global, immutable-versioned shape as the 34-11
        // cost rows above.
        ConfigureMarginPolicies(modelBuilder);

        // Story 34-4 — per-tenant, version-pinned plan assignments. CP-resident
        // (keyed by tenant, alongside the plans catalog). Partial unique index
        // enforces at most one active assignment per tenant; FKs to tenants
        // (Cascade) + plans (Restrict).
        TammaModelConfiguration.ConfigureTenantPlanAssignments(modelBuilder);

        // Story 34-3 — the authoritative per-(tenant, provider) billing-mode
        // owner. CP-resident (keyed by tenant, alongside the billing tables).
        // One active row per (tenant, provider) via a partial unique index;
        // CHECKs pin mode/status + the byok↔secret XOR. FK to tenants (Cascade).
        TammaModelConfiguration.ConfigureTenantProviderBilling(modelBuilder);
    }

    /// <summary>
    /// Story 34-11 — <c>providers</c> table. Platform-global cost identity. The
    /// unique <c>Key</c> is the canonical provider handle; CHECKs pin
    /// <c>AuthModel</c>/<c>Status</c> to their closed enums. No tenant column —
    /// cost is mode- and tenant-independent (design §4.4).
    /// </summary>
    private static void ConfigureProviders(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Provider>(entity =>
        {
            entity.ToTable("providers", t =>
            {
                t.HasCheckConstraint(
                    "ck_providers_auth_model",
                    "\"AuthModel\" IN ('api-key','cli-token')");
                t.HasCheckConstraint(
                    "ck_providers_status",
                    "\"Status\" IN ('active','retired')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Key).IsRequired().HasMaxLength(50);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.AuthModel)
                .IsRequired().HasMaxLength(20).HasDefaultValue("api-key");
            entity.Property(e => e.Status)
                .IsRequired().HasMaxLength(20).HasDefaultValue("active");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Canonical provider key is the natural key the cost resolver
            // (and the FK from provider_model_prices) joins on.
            entity.HasIndex(e => e.Key)
                .HasDatabaseName("UX_providers_Key").IsUnique();
        });
    }

    /// <summary>
    /// Story 34-11 — <c>provider_model_prices</c> table. The immutability
    /// invariant lives in SQL: a partial unique index
    /// <c>UX_provider_model_prices_OneActivePerModel</c> on
    /// <c>(ProviderKey, Model) WHERE "Status" = 'active'</c> guarantees exactly
    /// one active row per model (mirrors <c>UX_plans_OneActivePerSlug</c>). The
    /// window index supports the EffectiveFrom-windowed resolution. FK
    /// <c>ProviderKey → providers.Key</c> with RESTRICT (a referenced cost
    /// identity must never be hard-deleted out from under its price rows).
    /// </summary>
    private static void ConfigureProviderModelPrices(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderModelPrice>(entity =>
        {
            entity.ToTable("provider_model_prices", t =>
            {
                t.HasCheckConstraint(
                    "ck_provider_model_prices_status",
                    "\"Status\" IN ('active','superseded')");
                t.HasCheckConstraint(
                    "ck_provider_model_prices_source",
                    "\"Source\" IN ('seed','admin')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ProviderKey).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Model).IsRequired().HasMaxLength(200);
            entity.Property(e => e.InputUsdPer1M).HasColumnType("decimal(20,8)");
            entity.Property(e => e.OutputUsdPer1M).HasColumnType("decimal(20,8)");
            entity.Property(e => e.CacheReadUsdPer1M).HasColumnType("decimal(20,8)");
            entity.Property(e => e.CacheWriteUsdPer1M).HasColumnType("decimal(20,8)");
            entity.Property(e => e.Status)
                .IsRequired().HasMaxLength(20).HasDefaultValue("active");
            entity.Property(e => e.Source)
                .IsRequired().HasMaxLength(20).HasDefaultValue("seed");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Exactly one active price per (ProviderKey, Model) — the
            // immutability invariant in SQL (mirrors UX_plans_OneActivePerSlug).
            entity.HasIndex(e => new { e.ProviderKey, e.Model })
                .HasDatabaseName("UX_provider_model_prices_OneActivePerModel")
                .HasFilter("\"Status\" = 'active'").IsUnique();

            // Resolution-window lookup (provider+model, ordered by EffectiveFrom).
            entity.HasIndex(e => new { e.ProviderKey, e.Model, e.EffectiveFrom })
                .HasDatabaseName("IX_provider_model_prices_Window");

            entity.HasOne<Provider>()
                .WithMany(p => p.Prices)
                .HasForeignKey(e => e.ProviderKey)
                .HasPrincipalKey(p => p.Key)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// Story 34-5 — <c>margin_policies</c> table. Three CHECKs pin the closed
    /// enums (<c>Scope</c>, <c>Status</c>) and the "at least one knob" invariant
    /// (<c>ck_margin_policies_has_knob</c> — never an all-null policy row). The
    /// partial unique index <c>UX_margin_policies_OneActivePerScopeRef</c> on
    /// <c>(Scope, RefKey) WHERE "Status" = 'active'</c> with
    /// <c>NULLS NOT DISTINCT</c> guarantees exactly one active policy per
    /// <c>(Scope, RefKey)</c> — including the single global row whose
    /// <c>RefKey</c> is NULL (mirrors <c>UX_provider_model_prices_OneActivePerModel</c>).
    /// The window index supports the EffectiveFrom-windowed resolution.
    /// </summary>
    private static void ConfigureMarginPolicies(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MarginPolicy>(entity =>
        {
            entity.ToTable("margin_policies", t =>
            {
                t.HasCheckConstraint(
                    "ck_margin_policies_scope",
                    "\"Scope\" IN ('global','plan','provider')");
                t.HasCheckConstraint(
                    "ck_margin_policies_status",
                    "\"Status\" IN ('active','superseded')");
                // At least one knob must be set — never an all-null policy row.
                t.HasCheckConstraint(
                    "ck_margin_policies_has_knob",
                    "\"MarkupMultiplier\" IS NOT NULL OR \"FixedUsdPer1M\" IS NOT NULL");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Scope).IsRequired().HasMaxLength(20);
            entity.Property(e => e.RefKey).HasMaxLength(200);
            entity.Property(e => e.MarkupMultiplier).HasColumnType("decimal(20,8)");
            entity.Property(e => e.FixedUsdPer1M).HasColumnType("decimal(20,8)");
            entity.Property(e => e.Status)
                .IsRequired().HasMaxLength(20).HasDefaultValue("active");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Exactly one active policy per (Scope, RefKey); NULLS NOT DISTINCT so
            // the single global row (RefKey NULL) is unique too.
            entity.HasIndex(e => new { e.Scope, e.RefKey })
                .HasDatabaseName("UX_margin_policies_OneActivePerScopeRef")
                .HasFilter("\"Status\" = 'active'")
                .IsUnique()
                .AreNullsDistinct(false);

            // Resolution-window lookup (scope+refKey, ordered by EffectiveFrom).
            entity.HasIndex(e => new { e.Scope, e.RefKey, e.EffectiveFrom })
                .HasDatabaseName("IX_margin_policies_Window");
        });
    }

    /// <summary>
    /// Story 31-7 — <c>platform_webhook_deliveries</c> table. CHECK
    /// constraint pins <c>PlatformKind</c> to the same closed enum the
    /// installations table uses; the unique
    /// <c>(PlatformKind, DeliveryId)</c> index is the natural key the
    /// receiver hashes against to drop duplicate deliveries.
    /// </summary>
    private static void ConfigurePlatformWebhookDeliveries(
        ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformWebhookDelivery>(entity =>
        {
            entity.ToTable("platform_webhook_deliveries", t =>
            {
                t.HasCheckConstraint(
                    "CK_platform_webhook_deliveries_PlatformKind",
                    "\"PlatformKind\" IN ('github','gitea','forgejo','gitlab','bitbucket','azure_devops')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.ReceivedAt).HasDefaultValueSql("now()");

            // Natural key — receiver checks this composite before
            // dispatching to drop a re-delivery of the same logical
            // event from a platform that retried on transient errors.
            entity.HasIndex(e => new { e.PlatformKind, e.DeliveryId })
                .HasDatabaseName("UX_platform_webhook_deliveries_Kind_DeliveryId")
                .IsUnique();

            // Pruner support — drop rows older than N days for cleanup.
            entity.HasIndex(e => e.ReceivedAt)
                .HasDatabaseName("IX_platform_webhook_deliveries_ReceivedAt");
        });
    }

    /// <summary>
    /// Story 31-2 — <c>tenant_platform_installations</c> table.
    /// CHECK constraints pin <c>PlatformKind</c> + <c>Status</c> to
    /// closed enums; the partial unique index on
    /// <c>(TenantId, PlatformKind, InstallationExternalId)</c> is what
    /// the resolver hashes against, with a separate partial unique on
    /// <c>(TenantId, PlatformKind)</c> filtered by
    /// <c>IsPrimary = true</c> guaranteeing a unique primary per tenant
    /// kind for the no-explicit-kind resolution path.
    /// </summary>
    private static void ConfigureTenantPlatformInstallations(
        ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantPlatformInstallation>(entity =>
        {
            entity.ToTable("tenant_platform_installations", t =>
            {
                t.HasCheckConstraint(
                    "CK_tenant_platform_installations_PlatformKind",
                    "\"PlatformKind\" IN ('github','gitea','forgejo','gitlab','bitbucket','azure_devops')");
                t.HasCheckConstraint(
                    "CK_tenant_platform_installations_Status",
                    "\"Status\" IN ('connected','suspended','disconnected')");
                t.HasCheckConstraint(
                    "CK_tenant_platform_installations_CredentialSecretScope",
                    "\"CredentialSecretScope\" IN ('platform','tenant')");
                t.HasCheckConstraint(
                    "CK_tenant_platform_installations_WebhookSecretScope",
                    "\"WebhookSecretScope\" IS NULL OR \"WebhookSecretScope\" IN ('platform','tenant')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.MetadataJson)
                .HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.IsPrimary).HasDefaultValue(true);
            entity.Property(e => e.Status).HasDefaultValue("connected");
            entity.Property(e => e.CredentialSecretScope).HasDefaultValue("tenant");

            // Webhook resolve path — 31-7 only has the external id from the
            // payload, not the row id. Composite (PlatformKind, ExternalId)
            // narrows to the right driver instance even when two platforms
            // mint colliding ids.
            entity.HasIndex(e => new { e.PlatformKind, e.InstallationExternalId })
                .HasDatabaseName("IX_tenant_platform_installations_PlatformKind_ExternalId")
                .HasFilter("\"InstallationExternalId\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            // Idempotency / dedupe: the same external installation can't
            // be registered twice against the same tenant + kind. The
            // filter excludes soft-deleted rows so a tenant can re-add
            // a previously-disconnected installation cleanly.
            entity.HasIndex(e => new
            {
                e.TenantId,
                e.PlatformKind,
                e.InstallationExternalId,
            })
                .HasDatabaseName("UX_tenant_platform_installations_TenantId_Kind_ExternalId")
                .HasFilter("\"InstallationExternalId\" IS NOT NULL AND \"DeletedAt\" IS NULL")
                .IsUnique();

            // At most one primary per (TenantId, PlatformKind) — keeps
            // the no-explicit-kind resolver path deterministic. Partial
            // index so non-primary rows don't collide.
            entity.HasIndex(e => new { e.TenantId, e.PlatformKind })
                .HasDatabaseName("UX_tenant_platform_installations_PrimaryPerKind")
                .HasFilter("\"IsPrimary\" = TRUE AND \"DeletedAt\" IS NULL")
                .IsUnique();
        });
    }

    /// <summary>
    /// Story 28-R2 / PF-S9 — single-row <c>platform_bootstrap</c>
    /// sentinel. Schema-level guard against bootstrap-superadmin
    /// race; <see cref="Entities.PlatformBootstrap"/> for semantics.
    /// </summary>
    private static void ConfigurePlatformBootstrap(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformBootstrap>(entity =>
        {
            entity.ToTable("platform_bootstrap", t =>
            {
                // Mathematical impossibility of more than one row: PK
                // forces uniqueness, CHECK forces the value to be 1.
                // Two concurrent inserts → exactly one wins, the loser
                // gets a UNIQUE violation that the application catches
                // and falls back to a normal "user" platform role.
                t.HasCheckConstraint(
                    "ck_platform_bootstrap_singleton",
                    "\"Id\" = 1");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.ClaimedAt)
                .HasDefaultValueSql("now()");

            // FK to users — RESTRICT so the bootstrap admin can't be
            // soft-removed without explicitly clearing the sentinel.
            // (Soft-delete is a hard-delete-equivalent for users;
            // hard-delete on users is forbidden by app code, so this
            // FK is effectively a tripwire.)
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// R2-H14 — <c>kek_rotations</c> table. One row per rotation; the
    /// active row is identified by a partial unique index on
    /// <c>Status IN ('pending', 'running')</c>. The
    /// <c>StagedSecondaryProtected</c> column is encrypted by the OLD
    /// primary KEK at write time so the row is always readable after a
    /// restart.
    /// </summary>
    private static void ConfigureKekRotations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KekRotation>(entity =>
        {
            entity.ToTable("kek_rotations", t =>
            {
                t.HasCheckConstraint(
                    "CK_kek_rotations_status",
                    "\"Status\" IN ('pending','running','completed','failed','cancelled')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Status).IsRequired().HasMaxLength(32);
            entity.Property(e => e.VersionOld).IsRequired();
            entity.Property(e => e.VersionNew).IsRequired();
            entity.Property(e => e.StagedSecondaryProtected).HasColumnType("bytea");
            entity.Property(e => e.FailureReason).HasMaxLength(2000);
            entity.Property(e => e.StartedAt);

            // Hot read: status transitions on the in-flight row. Partial
            // index keeps the index tight (most rows are completed/failed).
            entity.HasIndex(e => e.Status)
                .HasDatabaseName("IX_kek_rotations_Status")
                .HasFilter("\"Status\" IN ('pending','running')");

            // Reverse-chronological list for the operator dashboard.
            entity.HasIndex(e => e.StartedAt)
                .HasDatabaseName("IX_kek_rotations_StartedAt")
                .IsDescending(true);
        });
    }

    /// <summary>
    /// Unified-tenancy Phase 0 — <c>tenant_databases</c> registry (the admin DB
    /// pool). CHECKs pin the two closed enums; <c>Label</c> is the operator key.
    /// </summary>
    private static void ConfigureTenantDatabases(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantDatabase>(entity =>
        {
            entity.ToTable("tenant_databases", t =>
            {
                t.HasCheckConstraint(
                    "ck_tenant_databases_placement_class",
                    "\"PlacementClass\" IN ('shared','dedicated')");
                t.HasCheckConstraint(
                    "ck_tenant_databases_status",
                    "\"Status\" IN ('active','draining','full','retired')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Label).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Host).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Port).HasDefaultValue(5432);
            entity.Property(e => e.AdminConnectionStringEncrypted)
                .IsRequired().HasColumnType("bytea");
            entity.Property(e => e.PlacementClass)
                .IsRequired().HasMaxLength(20).HasDefaultValue("shared");
            entity.Property(e => e.TierEligibility)
                .HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.TenantCount).HasDefaultValue(0);
            entity.Property(e => e.Status)
                .IsRequired().HasMaxLength(20).HasDefaultValue("active");
            entity.Property(e => e.KekVersion).HasDefaultValue((short)1);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.Label).IsUnique();
            entity.HasIndex(e => e.Status);
        });
    }

    /// <summary>
    /// Story 5.6 (Wave C.2) — <c>alert_rules</c> table. CHECK
    /// constraint pins severity to its enum; unique index on the
    /// human-readable <c>Name</c>; partial unique index on
    /// <c>BuiltInKey</c> so seeder upserts are idempotent without
    /// colliding on admin-created rules (which carry NULL
    /// <c>BuiltInKey</c>).
    /// </summary>
    private static void ConfigureAlertRules(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertRule>(entity =>
        {
            entity.ToTable("alert_rules", t =>
            {
                t.HasCheckConstraint(
                    "CK_alert_rules_severity",
                    "\"Severity\" IN ('critical','warning','info')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Predicate)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.ThrottleSeconds).HasDefaultValue(0);
            entity.Property(e => e.ChannelIds)
                .HasColumnType("uuid[]")
                .HasDefaultValueSql("ARRAY[]::uuid[]");
            entity.Property(e => e.IsBuiltIn).HasDefaultValue(false);
            entity.Property(e => e.BuiltInKey).HasMaxLength(64);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            // Hot read: the evaluator fetches by (EventType, IsEnabled).
            entity.HasIndex(e => new { e.EventType, e.IsEnabled })
                .HasDatabaseName("IX_alert_rules_EventType_IsEnabled");

            // Idempotent seeder upserts key off BuiltInKey. Partial
            // unique keeps admin-created NULL-key rules from colliding.
            entity.HasIndex(e => e.BuiltInKey)
                .HasDatabaseName("UX_alert_rules_BuiltInKey")
                .HasFilter("\"BuiltInKey\" IS NOT NULL")
                .IsUnique();

            // Human-readable uniqueness — admin UI rejects a duplicate
            // name at write time via the 409 handler; the DB-level
            // index is defence in depth.
            entity.HasIndex(e => e.Name)
                .HasDatabaseName("UX_alert_rules_Name")
                .IsUnique();
        });
    }

    /// <summary>
    /// Story 5.6 (Wave C.2) — evaluator cursor. Composite key on
    /// <see cref="AlertEvaluatorCursor.EvaluatorId"/> so each logical
    /// evaluator owns one row; multi-process deployments share the
    /// single <c>"default"</c> evaluator id today.
    /// </summary>
    private static void ConfigureAlertEvaluatorCursor(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertEvaluatorCursor>(entity =>
        {
            entity.ToTable("alert_evaluator_cursor");
            entity.HasKey(e => e.EvaluatorId);
            entity.Property(e => e.EvaluatorId)
                .IsRequired()
                .HasMaxLength(64);
            // Per-stream BIGINT cursors. Default 0 means "fetch from
            // the start" — a freshly-inserted row resumes at the head
            // of each stream.
            entity.Property(e => e.LastDomainSequenceNumber)
                .HasDefaultValue(0L);
            entity.Property(e => e.LastPlatformSequenceNumber)
                .HasDefaultValue(0L);
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });
    }

    /// <summary>
    /// Story 5.6 (Wave C.1) — <c>alerts</c> table configuration.
    /// CHECK constraints pin severity + status to their enums so a
    /// buggy write path can't stash an unknown value and confuse the
    /// admin feed. Indexes cover the two hot reads: the admin feed
    /// (by status + recency) and the tenant feed (tenant-filtered +
    /// recency).
    /// </summary>
    private static void ConfigureAlerts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Alert>(entity =>
        {
            entity.ToTable("alerts", t =>
            {
                t.HasCheckConstraint(
                    "CK_alerts_severity",
                    "\"Severity\" IN ('critical','warning','info')");
                t.HasCheckConstraint(
                    "CK_alerts_status",
                    "\"Status\" IN ('active','acknowledged','resolved')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(512);
            entity.Property(e => e.Description).IsRequired();
            entity.Property(e => e.CorrelationId).HasMaxLength(255);
            entity.Property(e => e.Metadata)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .HasDefaultValue("active");
            entity.Property(e => e.Resolution).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // Admin feed — "show me the last 50 active alerts" hits
            // this index. Descending on CreatedAt avoids an in-memory
            // reverse sort.
            entity.HasIndex(e => new { e.Status, e.CreatedAt })
                .HasDatabaseName("IX_alerts_Status_CreatedAt")
                .IsDescending(false, true);

            // Tenant feed — partial index keeps the tenant-scoped
            // rows tight; platform-wide alerts (TenantId=null) don't
            // waste space here because they're served by the admin
            // feed index above.
            entity.HasIndex(e => new { e.TenantId, e.CreatedAt })
                .HasDatabaseName("IX_alerts_TenantId_CreatedAt")
                .HasFilter("\"TenantId\" IS NOT NULL")
                .IsDescending(false, true);

            // Severity-first dashboards (e.g. "all criticals this
            // week") get their own ordering.
            entity.HasIndex(e => new { e.Severity, e.CreatedAt })
                .HasDatabaseName("IX_alerts_Severity_CreatedAt")
                .IsDescending(false, true);

            // CorrelationId lookup — "show me every alert tied to
            // this workflow retry storm". Partial so the null
            // majority doesn't bloat the index.
            entity.HasIndex(e => e.CorrelationId)
                .HasDatabaseName("IX_alerts_CorrelationId")
                .HasFilter("\"CorrelationId\" IS NOT NULL");
        });
    }

    /// <summary>
    /// Story 1.5-37 (Wave C.1) — <c>alert_channels</c> table.
    /// </summary>
    private static void ConfigureAlertChannels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertChannel>(entity =>
        {
            entity.ToTable("alert_channels", t =>
            {
                t.HasCheckConstraint(
                    "CK_alert_channels_channel_type",
                    "channel_type IN ('email','slack','pagerduty','webhook')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
            entity.Property(e => e.ChannelType)
                .IsRequired()
                .HasMaxLength(32)
                .HasColumnName("channel_type");
            entity.Property(e => e.IsEnabled)
                .HasDefaultValue(true);
            entity.Property(e => e.Config)
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => new { e.TenantId, e.IsEnabled })
                .HasDatabaseName("IX_alert_channels_TenantId_IsEnabled");

            entity.HasIndex(e => new { e.ChannelType, e.IsEnabled })
                .HasDatabaseName("IX_alert_channels_ChannelType_IsEnabled");
        });
    }

    /// <summary>
    /// Story 1.5-37 (Wave C.1) — <c>alert_delivery_attempts</c>
    /// table. The partial index on <c>(Status, CreatedAt)</c>
    /// covers the dispatcher poll query
    /// (<c>WHERE status IN ('pending','failed')</c>) — Postgres can
    /// scan the index top-down with no table hits until it has the
    /// batch size.
    /// </summary>
    private static void ConfigureAlertDeliveryAttempts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlertDeliveryAttempt>(entity =>
        {
            entity.ToTable("alert_delivery_attempts", t =>
            {
                t.HasCheckConstraint(
                    "CK_alert_delivery_attempts_status",
                    "\"Status\" IN ('pending','success','failed','dropped_rate_limit')");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.AttemptNumber).IsRequired();
            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(32)
                .HasDefaultValue("pending");
            entity.Property(e => e.Error).HasMaxLength(2000);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            entity.HasIndex(e => e.AlertId)
                .HasDatabaseName("IX_alert_delivery_attempts_AlertId");

            // Dispatcher hot path — filter on status keeps the
            // successful attempts (the vast majority after steady
            // state) out of the index.
            entity.HasIndex(e => new { e.Status, e.CreatedAt })
                .HasDatabaseName("IX_alert_delivery_attempts_Status_CreatedAt")
                .HasFilter("\"Status\" IN ('pending','failed')")
                .IsDescending(false, true);

            // FK cascade from alerts keeps delivery-attempt rows
            // honest when an alert is hard-deleted (tenant purge).
            entity.HasOne<Alert>()
                .WithMany()
                .HasForeignKey(e => e.AlertId)
                .OnDelete(DeleteBehavior.Cascade);

            // Preserve delivery history even when a channel is
            // soft-deleted — the admin can still audit who received
            // what. Enforced by RESTRICT; soft-delete is the
            // contracted deprovisioning path.
            entity.HasOne<AlertChannel>()
                .WithMany()
                .HasForeignKey(e => e.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurePlatformAnalyticsHourly(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformAnalyticsHourly>(entity =>
        {
            entity.ToTable("platform_analytics_hourly");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.Hour).HasColumnType("timestamp with time zone");
            entity.Property(e => e.TenantId);
            entity.Property(e => e.WorkflowsStarted).HasDefaultValue(0L);
            entity.Property(e => e.WorkflowsCompleted).HasDefaultValue(0L);
            entity.Property(e => e.WorkflowsFailed).HasDefaultValue(0L);
            entity.Property(e => e.AgentDispatches).HasDefaultValue(0L);
            entity.Property(e => e.TokensIn).HasDefaultValue(0L);
            entity.Property(e => e.TokensOut).HasDefaultValue(0L);
            entity.Property(e => e.CostUsd).HasPrecision(20, 4).HasDefaultValue(0m);
            entity.Property(e => e.ActiveTenantsAtHourEnd).HasDefaultValue(0);
            entity.Property(e => e.ComputedAt).HasDefaultValueSql("now()");

            // Per-tenant time-series lookup — "show me tenant X's last
            // 30d of workflows" on the admin dashboard. The filter keeps
            // the index tight (one platform-wide row per hour is routed
            // through UX_*_PlatformWide below instead). Descending on the
            // Hour leg matches "most-recent-first" scans.
            entity.HasIndex(e => new { e.TenantId, e.Hour })
                .HasDatabaseName("IX_platform_analytics_hourly_TenantId_Hour")
                .HasFilter("\"TenantId\" IS NOT NULL")
                .IsDescending(false, true);

            // Idempotency key — the rollup fan-out writes at most one row
            // per (Hour, TenantId). Two partial unique indexes cover both
            // the tenant-scoped path and the NULL-TenantId platform-wide
            // slot without needing a COALESCE expression that some older
            // Postgres servers reject on unique indexes. Combined, they
            // also serve descending "last N hours" scans because Postgres
            // can use a partial index for an `ORDER BY Hour DESC` with a
            // matching predicate.
            entity.HasIndex(e => new { e.Hour, e.TenantId })
                .HasDatabaseName("UX_platform_analytics_hourly_Hour_TenantId")
                .HasFilter("\"TenantId\" IS NOT NULL")
                .IsDescending(true, false)
                .IsUnique();

            entity.HasIndex(e => e.Hour)
                .HasDatabaseName("UX_platform_analytics_hourly_Hour_PlatformWide")
                .HasFilter("\"TenantId\" IS NULL")
                .IsDescending(true)
                .IsUnique();
        });
    }

    /// <summary>
    /// Story 28-7 deferred-item routing table: maps a key prefix to its
    /// tenant + ApiKey for O(1) bearer-token authentication. The
    /// <see cref="PlatformApiKeyIndex.HashedSuffix"/> is SHA-256 of the
    /// remainder so the table never stores plaintext material.
    /// </summary>
    private static void ConfigurePlatformApiKeyIndex(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformApiKeyIndex>(entity =>
        {
            entity.ToTable("platform_api_key_index");
            entity.HasKey(e => e.KeyPrefix);
            entity.Property(e => e.KeyPrefix).IsRequired().HasMaxLength(16);
            entity.Property(e => e.HashedSuffix).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ApiKeyId).IsRequired();
            entity.Property(e => e.Scope).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

            // Look-ups during auth:
            // 1. Fast path — by composite (KeyPrefix, HashedSuffix); PK gives
            //    us KeyPrefix already, suffix filter is a secondary equality.
            //    A dedicated index keeps SELECT ... WHERE KeyPrefix = ? AND
            //    HashedSuffix = ? off a full table scan on large CPs.
            entity.HasIndex(e => new { e.KeyPrefix, e.HashedSuffix });
            // 2. Reverse lookup by ApiKeyId for revoke cascades.
            entity.HasIndex(e => e.ApiKeyId);
            // 3. Tenant filter for bulk-revoke on tenant delete (cascade).
            entity.HasIndex(e => e.TenantId).HasFilter("\"TenantId\" IS NOT NULL");
        });
    }
}
