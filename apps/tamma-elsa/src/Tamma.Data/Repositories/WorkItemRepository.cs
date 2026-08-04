using Microsoft.EntityFrameworkCore;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using Tamma.Core.Tracking;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// Tenant-scoped <c>work_items</c> + <c>work_item_relations</c> repository
/// (Story 44-1). Same seam shape as <see cref="AcceptanceRulesRepository"/>
/// (<see cref="ITenantDbContextFactory"/> + ambient <see cref="ITenantContext"/>).
///
/// <para>Storage-layer invariants owned here: gap-free key minting under the
/// project row lock (AC5); the frozen key + <c>PreviousKeys</c> history
/// (44-0 AC8 — <c>WorkItemKeyHistory</c> is the single implementation);
/// canonical relation-edge form (44-0 AC14 —
/// <c>WorkItemRelationKindExtensions.Canonicalize</c> is the single
/// implementation, called, never reimplemented); vocabulary validation at the
/// write boundary so junk fails as a typed <c>TammaError</c> rather than a DB
/// CHECK violation. Structural hierarchy validation (cycles, depth, the Epic
/// kind rule) is Story 44-3's service, NOT this class.</para>
/// </summary>
public class WorkItemRepository(
    ITenantDbContextFactory tenantDbFactory,
    ITenantContext tenantContext) : IWorkItemRepository
{
    private Guid RequireTenantId() => tenantContext.TenantId
        ?? throw new InvalidOperationException(
            "WorkItemRepository requires an ambient tenant id. Tracker tables are "
            + "tenant-schema resident (Epic 44 D5).");

    public async Task<WorkItemEntity?> GetAsync(Guid id)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
    }

    public async Task<WorkItemEntity?> GetByKeyAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        // Current-or-previous (44-0 AC8 / WorkItemKeyHistory.Matches in SQL),
        // resolved DETERMINISTICALLY: the current-Key match (unique index)
        // always wins; only when no row currently holds the key do we fall
        // back to the previous-keys containment on the text[] column. A single
        // OR query with FirstOrDefault is nondeterministic when one row's
        // current key is another row's previous key (a rekey freed the key and
        // a later rekey re-took it).
        var current = await db.WorkItems.FirstOrDefaultAsync(w => w.Key == key);
        if (current is not null)
            return current;
        // Among previous-keys holders the Id order is a stable tie-break (a
        // key can sit in two rows' histories after chained rekeys).
        return await db.WorkItems
            .Where(w => w.PreviousKeys.Contains(key))
            .OrderBy(w => w.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<List<WorkItemEntity>> ListAsync(WorkItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);

        var items = db.WorkItems.AsQueryable();
        if (query.ProjectId is { } projectId)
            items = items.Where(w => w.ProjectId == projectId);
        if (query.Statuses is { Count: > 0 } statuses)
            items = items.Where(w => statuses.Contains(w.Status));
        if (query.Kinds is { Count: > 0 } kinds)
            items = items.Where(w => kinds.Contains(w.Kind));
        if (query.AssigneeUserId is { } assignee)
            items = items.Where(w => w.AssigneeUserId == assignee);
        if (query.IterationId is { } iteration)
            items = items.Where(w => w.IterationId == iteration);
        if (query.TopLevelOnly)
            items = items.Where(w => w.ParentId == null);
        else if (query.ParentId is { } parent)
            items = items.Where(w => w.ParentId == parent);
        if (query.ExternalLinked is bool externalLinked)
        {
            items = externalLinked
                ? items.Where(w => w.ExternalRefJson != null)
                : items.Where(w => w.ExternalRefJson == null);
        }
        if (query.HasEstimate is bool hasEstimate)
        {
            items = hasEstimate
                ? items.Where(w => w.Estimate != null)
                : items.Where(w => w.Estimate == null);
        }
        if (!string.IsNullOrWhiteSpace(query.TitleContains))
            items = items.Where(w => EF.Functions.ILike(
                w.Title, $"%{EscapeLike(query.TitleContains)}%"));

        // Keyset cursor over the SQL order itself — (Rank, Key), both
        // DB-side comparisons, so paging can never disagree with the
        // COLLATE "C" ordering (AC4). No OFFSET: stable under insertion.
        if (query.AfterRank is not null && query.AfterKey is not null)
        {
            items = items.Where(w =>
                string.Compare(w.Rank, query.AfterRank) > 0
                || (w.Rank == query.AfterRank && string.Compare(w.Key, query.AfterKey) > 0));
        }

        var limit = query.Limit is > 0 and <= 500 ? query.Limit : 100;
        return await items
            .OrderBy(w => w.Rank)
            .ThenBy(w => w.Key)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<WorkItemEntity> CreateAsync(WorkItemEntity item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateVocabulary(item);

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        await using var tx = await db.Database.BeginTransactionAsync();

        // ── The mint (AC5 / plan D6) ──
        // Lock the project row for the duration of the create so two
        // concurrent creates serialize on the counter: gap-free, monotone,
        // no duplicate (ProjectId, Number) possible.
        var project = await db.Projects
            .FromSqlInterpolated(
                $"""SELECT * FROM projects WHERE "Id" = {item.ProjectId} FOR UPDATE""")
            .AsTracking()
            .SingleOrDefaultAsync()
            ?? throw new TammaError(
                "TRACKER.PROJECT_NOT_FOUND",
                $"Project '{item.ProjectId}' does not exist in this tenant.",
                new Dictionary<string, object?> { ["projectId"] = item.ProjectId },
                retryable: false,
                severity: TammaErrorSeverity.High);

        var number = project.NextNumber;
        project.NextNumber = number + 1;

        item.Number = number;
        // The frozen key (44-0 AC8): minted exactly once, via WorkItemRef —
        // never a hand-rolled string. WorkItemRef validates the prefix.
        item.Key = new WorkItemRef(project.Key, number).ToWire();
        if (item.Id == Guid.Empty)
            item.Id = UuidV7.NewGuid();
        item.PreviousKeys ??= [];

        // Ranks: append at the end of each axis when the caller didn't place
        // the item. Append(currentMax) — never a fixed sentinel (44-0 AC9).
        // The ORDER BY ... DESC LIMIT 1 maxima are ordinal because both
        // columns are COLLATE "C".
        if (string.IsNullOrEmpty(item.Rank))
        {
            var maxRank = await db.WorkItems
                .Where(w => w.ProjectId == item.ProjectId)
                .OrderByDescending(w => w.Rank)
                .Select(w => w.Rank)
                .FirstOrDefaultAsync();
            item.Rank = Rank.Append(maxRank);
        }
        else
        {
            ValidateRank(item.Rank, nameof(item.Rank));
        }

        if (string.IsNullOrEmpty(item.SiblingRank))
        {
            var maxSibling = await db.WorkItems
                .Where(w => w.ProjectId == item.ProjectId && w.ParentId == item.ParentId)
                .OrderByDescending(w => w.SiblingRank)
                .Select(w => w.SiblingRank)
                .FirstOrDefaultAsync();
            item.SiblingRank = Rank.Append(maxSibling);
        }
        else
        {
            ValidateRank(item.SiblingRank, nameof(item.SiblingRank));
        }

        var now = DateTime.UtcNow;
        item.CreatedAt = now;
        item.UpdatedAt = now;
        item.ClosedAt = WorkItemStatusExtensions.Parse(item.Status).IsTerminal() ? now : null;
        item.Version = 1;

        db.WorkItems.Add(item);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return item;
    }

    public async Task<WorkItemEntity?> UpdateAsync(WorkItemEntity item, int? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateVocabulary(item);

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var existing = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == item.Id);
        if (existing is null)
            return null;

        // Frozen/owned-elsewhere columns deliberately NOT copied: Key, Number,
        // PreviousKeys (RekeyAsync), Status/ClosedAt (SetStatusAsync),
        // Rank/SiblingRank (SetRanksAsync), ParentId (SetParentAsync),
        // ProjectId (a move seam is 44-2's, and it does NOT re-mint the key).
        existing.Kind = item.Kind;
        existing.Priority = item.Priority;
        existing.IssueType = item.IssueType;
        existing.Title = item.Title;
        existing.Description = item.Description;
        existing.IterationId = item.IterationId;
        existing.AssigneeUserId = item.AssigneeUserId;
        existing.Estimate = item.Estimate;
        existing.ExternalRefJson = item.ExternalRefJson;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version += 1;
        PinExpectedVersion(db, existing, expectedVersion);
        await SaveGuardingVersionAsync(db, existing.Id);
        return existing;
    }

    public async Task<WorkItemEntity?> SetStatusAsync(Guid id, string statusWire, int? expectedVersion = null)
    {
        // Parse first — fail-loud on junk (TRACKER.UNKNOWN_STATUS), and the
        // terminal rule stays DERIVED from Category() (44-0 AC3), never a
        // set literal here.
        var status = WorkItemStatusExtensions.Parse(statusWire);

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var existing = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (existing is null)
            return null;

        existing.Status = status.ToWire();
        existing.ClosedAt = status.IsTerminal() ? DateTime.UtcNow : null;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version += 1;
        PinExpectedVersion(db, existing, expectedVersion);
        await SaveGuardingVersionAsync(db, existing.Id);
        return existing;
    }

    public async Task<WorkItemEntity?> SetRanksAsync(Guid id, string? rank, string? siblingRank)
    {
        if (rank is null && siblingRank is null)
            throw new ArgumentException("At least one of rank / siblingRank must be supplied.");
        if (rank is not null)
            ValidateRank(rank, nameof(rank));
        if (siblingRank is not null)
            ValidateRank(siblingRank, nameof(siblingRank));

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var existing = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (existing is null)
            return null;

        if (rank is not null)
            existing.Rank = rank;
        if (siblingRank is not null)
            existing.SiblingRank = siblingRank;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version += 1;
        await SaveGuardingVersionAsync(db, existing.Id);
        return existing;
    }

    public async Task<WorkItemEntity?> SetParentAsync(Guid id, Guid? parentId, string? siblingRank = null)
    {
        if (parentId == id)
        {
            throw new TammaError(
                "TRACKER.SELF_RELATION",
                "A work item cannot be its own parent.",
                new Dictionary<string, object?> { ["workItemId"] = id },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
        if (siblingRank is not null)
            ValidateRank(siblingRank, nameof(siblingRank));

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var existing = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (existing is null)
            return null;

        existing.ParentId = parentId;
        if (siblingRank is not null)
        {
            existing.SiblingRank = siblingRank;
        }
        else
        {
            // Append among the new siblings — same edge convention as create.
            var maxSibling = await db.WorkItems
                .Where(w => w.ProjectId == existing.ProjectId
                    && w.ParentId == parentId && w.Id != id)
                .OrderByDescending(w => w.SiblingRank)
                .Select(w => w.SiblingRank)
                .FirstOrDefaultAsync();
            existing.SiblingRank = Rank.Append(maxSibling);
        }
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version += 1;
        await SaveGuardingVersionAsync(db, existing.Id);
        return existing;
    }

    public async Task<WorkItemEntity?> RekeyAsync(Guid id, string newKey)
    {
        // Strict, non-normalizing parse — a bad key is rejected, never coerced.
        var parsed = WorkItemRef.Parse(newKey);
        var target = parsed.ToWire();

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        await using var tx = await db.Database.BeginTransactionAsync();

        var existing = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (existing is null)
            return null;

        if (string.Equals(existing.Key, target, StringComparison.Ordinal))
            return existing; // no-op re-key: nothing to record

        // Collision guard: the target must not be another row's CURRENT key.
        // Pre-checked here for a typed error; the UX_work_items_key catch
        // below closes the check-then-write race window.
        if (await db.WorkItems.AnyAsync(w => w.Key == target && w.Id != id))
            throw KeyConflict(target, id);

        // Mint-space guard: when the target prefix is an EXISTING project's
        // key, decide whose number space this key lives in.
        //  - A DIFFERENT project's prefix is rejected: a cross-project rekey
        //    is not a rename, it's a move, and a move never re-mints the key
        //    (44-0 AC8) — out of scope for this seam by contract.
        //  - The item's OWN project: a target number at/above NextNumber
        //    would wedge minting forever (when the counter reaches it,
        //    CreateAsync hits UX_work_items_key, rolls back, and NextNumber
        //    never advances) — so advance the counter past it, under the same
        //    FOR UPDATE row-lock discipline as CreateAsync.
        // A prefix that matches NO project (the operator prefix-rename,
        // TAM → TAMMA) has no counter to guard and passes through.
        var targetProject = await db.Projects
            .FromSqlInterpolated(
                $"""SELECT * FROM projects WHERE "Key" = {parsed.ProjectKey} FOR UPDATE""")
            .AsTracking()
            .SingleOrDefaultAsync();
        if (targetProject is not null && targetProject.Id != existing.ProjectId)
        {
            throw new TammaError(
                "TRACKER.CROSS_PROJECT_REKEY",
                $"Cannot rekey to '{target}': prefix '{parsed.ProjectKey}' belongs to a "
                + "different project. A rekey is a rename within the item's own key space; "
                + "moving an item between projects keeps its frozen key (44-0 AC8).",
                new Dictionary<string, object?>
                {
                    ["workItemId"] = id,
                    ["targetKey"] = target,
                    ["targetProjectId"] = targetProject.Id,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
        if (targetProject is not null && parsed.Number >= targetProject.NextNumber)
            targetProject.NextNumber = parsed.Number + 1;

        // The single implementation of the history rule (44-0 AC8):
        // idempotent, order-preserving, oldest first. A project MOVE never
        // reaches this method — the key is frozen on a move.
        var outgoing = WorkItemRef.Parse(existing.Key);
        existing.PreviousKeys = WorkItemKeyHistory.Record(existing.PreviousKeys, outgoing).ToList();
        existing.Key = target;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version += 1;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // The Version token (44-1 review, 2026-07-29): an interleaved
            // writer bumped Version after our read, so our UPDATE matched no
            // row. Losing silently here would drop the loser's PreviousKeys
            // recording — surface the typed, retryable conflict instead.
            throw ConcurrencyConflict(id, ex);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent writer took the key between the pre-check and the
            // save — same fact, typed the same way.
            throw KeyConflict(target, id);
        }
        await tx.CommitAsync();
        return existing;
    }

    private static TammaError KeyConflict(string target, Guid workItemId) => new(
        "TRACKER.KEY_CONFLICT",
        $"Cannot rekey to '{target}': another work item already holds that key.",
        new Dictionary<string, object?>
        {
            ["workItemId"] = workItemId,
            ["targetKey"] = target,
        },
        retryable: false,
        severity: TammaErrorSeverity.High);

    /// <summary>
    /// Save guarding the <c>Version</c> optimistic-concurrency token (44-1
    /// review, 2026-07-29): the caller has already bumped <c>Version</c>; EF
    /// adds <c>WHERE "Version" = &lt;as-read&gt;</c>, so an interleaved writer
    /// surfaces as a typed, RETRYABLE <c>TRACKER.CONCURRENCY_CONFLICT</c> —
    /// never a silent last-write-wins that could lose <c>PreviousKeys</c>
    /// history or a status/rank/parent write.
    /// </summary>
    /// <summary>
    /// Make the caller's <c>If-Match</c> precondition ATOMIC with the write
    /// (44-2 adversarial review, 2026-07-29). The EF token alone closes only the
    /// window INSIDE this repository: the service reads, checks
    /// <c>RequireVersion</c>, and then the repository RE-READS in a fresh
    /// context, so <c>W2.read(v1) → W1 completes(v2) → W2.repo-read(v2) →
    /// W2 writes v3</c> passed the service check and never tripped the token.
    /// Pinning the token's ORIGINAL value to the version the caller asserted
    /// puts <c>WHERE "Version" = @expected</c> in the UPDATE/DELETE itself, so
    /// the stale writer matches no row and loses with the typed conflict.
    /// </summary>
    private static void PinExpectedVersion(TenantDbContext db, WorkItemEntity row, int? expectedVersion)
    {
        if (expectedVersion is int expected)
            db.Entry(row).Property(w => w.Version).OriginalValue = expected;
    }

    private static async Task SaveGuardingVersionAsync(TenantDbContext db, Guid workItemId)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw ConcurrencyConflict(workItemId, ex);
        }
    }

    private static TammaError ConcurrencyConflict(Guid workItemId, DbUpdateConcurrencyException ex) => new(
        "TRACKER.CONCURRENCY_CONFLICT",
        $"Work item '{workItemId}' was modified by another writer while this update "
        + "was in flight (optimistic-concurrency Version mismatch). Re-read the item "
        + "and retry the operation against its current state.",
        new Dictionary<string, object?>
        {
            ["workItemId"] = workItemId,
            ["conflictEntries"] = ex.Entries.Count,
        },
        retryable: true,
        severity: TammaErrorSeverity.Medium);

    public async Task<bool> DeleteAsync(Guid id, int? expectedVersion = null)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (row is null)
            return false;
        db.WorkItems.Remove(row);
        PinExpectedVersion(db, row, expectedVersion);
        // Children RESTRICT the parent FK — 44-2 maps SqlState 23503 to the
        // documented 409; relation edges CASCADE away with the item.
        await SaveGuardingVersionAsync(db, id);
        return true;
    }

    // ── Relations (44-0 AC14; canonical form — plan D8) ──

    public async Task<WorkItemRelation> AddRelationAsync(
        Guid sourceId, Guid targetId, WorkItemRelationKind kind, Guid? createdByUserId = null)
    {
        // THE direction convention, exactly once, from Tamma.Core.Tracking:
        // rejects self-relations (TRACKER.SELF_RELATION) and orders symmetric
        // endpoints lower-id-first so a mirror duplicate cannot be stored.
        var (canonicalSource, canonicalTarget) = kind.Canonicalize(sourceId, targetId);
        var wire = kind.ToWire();

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);

        // Insert-first, no check-then-insert window: two concurrent adds of
        // the same canonical edge race the unique index, and the loser's
        // 23505 IS the "already exists" fact — caught below and answered with
        // the stored row, so the documented idempotent contract holds under
        // concurrency, not just in sequence.
        var relation = new WorkItemRelation
        {
            Id = UuidV7.NewGuid(),
            SourceId = canonicalSource,
            TargetId = canonicalTarget,
            Kind = wire,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTime.UtcNow,
        };
        db.WorkItemRelations.Add(relation);
        try
        {
            await db.SaveChangesAsync();
            return relation;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The canonical row already exists (concurrent writer or an
            // earlier add) — UX_work_item_relations_source_target_kind.
            // Detach the failed insert and hand back the winner. A row that
            // vanished between violation and re-read (concurrent remove) means
            // the edge does not exist NOW — rethrow so the caller retries.
            db.Entry(relation).State = EntityState.Detached;
            var winner = await db.WorkItemRelations.FirstOrDefaultAsync(r =>
                r.SourceId == canonicalSource && r.TargetId == canonicalTarget && r.Kind == wire);
            if (winner is null)
                throw;
            return winner;
        }
    }

    public async Task<bool> RemoveRelationAsync(Guid sourceId, Guid targetId, WorkItemRelationKind kind)
    {
        var (canonicalSource, canonicalTarget) = kind.Canonicalize(sourceId, targetId);
        var wire = kind.ToWire();

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var row = await db.WorkItemRelations.FirstOrDefaultAsync(r =>
            r.SourceId == canonicalSource && r.TargetId == canonicalTarget && r.Kind == wire);
        if (row is null)
            return false;
        db.WorkItemRelations.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<WorkItemRelation>> ListRelationsAsync(Guid workItemId)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        return await db.WorkItemRelations
            .Where(r => r.SourceId == workItemId || r.TargetId == workItemId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Postgres unique-violation (SqlState 23505) — ONLY that; any other
    /// <see cref="DbUpdateException"/> (FK 23503, CHECK 23514, …) propagates.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    // ── Validation helpers (wire boundary — fail loud, never coerce) ──

    private static void ValidateVocabulary(WorkItemEntity item)
    {
        _ = WorkItemKindExtensions.Parse(item.Kind);       // TRACKER.UNKNOWN_KIND
        _ = WorkItemStatusExtensions.Parse(item.Status);   // TRACKER.UNKNOWN_STATUS

        if (item.Priority is not null)
        {
            // Alias-aware (critical→urgent, medium→normal via TriageVocabulary),
            // then canonicalized to the wire the CHECK constraint knows.
            if (!TrackerPriority.TryParsePriority(item.Priority, out var priority))
            {
                throw new TammaError(
                    "TRACKER.UNKNOWN_PRIORITY",
                    $"Unknown work item priority: '{item.Priority}'. Valid: "
                    + string.Join(", ", TrackerPriority.AcceptedPriorityWires) + ".",
                    new Dictionary<string, object?> { ["input"] = item.Priority },
                    retryable: false,
                    severity: TammaErrorSeverity.High);
            }
            item.Priority = priority.ToWire();
        }

        if (item.IssueType is not null)
        {
            if (!TrackerPriority.TryParseType(item.IssueType, out var type))
            {
                throw new TammaError(
                    "TRACKER.UNKNOWN_ISSUE_TYPE",
                    $"Unknown work item type: '{item.IssueType}'. Valid: "
                    + string.Join(", ", TrackerPriority.AcceptedTypeWires) + ".",
                    new Dictionary<string, object?> { ["input"] = item.IssueType },
                    retryable: false,
                    severity: TammaErrorSeverity.High);
            }
            item.IssueType = type.ToWire();
        }

        if (string.IsNullOrWhiteSpace(item.Title))
        {
            throw new TammaError(
                "TRACKER.INVALID_TITLE",
                "A work item requires a non-empty title.",
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
    }

    private static void ValidateRank(string candidate, string paramName)
    {
        if (!Rank.IsValid(candidate))
        {
            throw new TammaError(
                "TRACKER.INVALID_RANK",
                $"'{candidate}' is not a canonical rank ({paramName}): non-empty base-62 "
                + "over 0-9A-Za-z with no trailing '0'. Mint ranks via Rank.Between/"
                + "Append/Prepend — never hand-build them.",
                new Dictionary<string, object?> { [paramName] = candidate },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
    }

    private static string EscapeLike(string input) =>
        input.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
