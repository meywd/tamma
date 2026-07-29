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
        // Current-or-previous (44-0 AC8 / WorkItemKeyHistory.Matches in SQL):
        // the unique Key index serves the common case; the previous-keys
        // containment rides the text[] column.
        return await db.WorkItems.FirstOrDefaultAsync(
            w => w.Key == key || w.PreviousKeys.Contains(key));
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

    public async Task<WorkItemEntity?> UpdateAsync(WorkItemEntity item)
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
        await db.SaveChangesAsync();
        return existing;
    }

    public async Task<WorkItemEntity?> SetStatusAsync(Guid id, string statusWire)
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
        await db.SaveChangesAsync();
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
        await db.SaveChangesAsync();
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
        await db.SaveChangesAsync();
        return existing;
    }

    public async Task<WorkItemEntity?> RekeyAsync(Guid id, string newKey)
    {
        // Strict, non-normalizing parse — a bad key is rejected, never coerced.
        var parsed = WorkItemRef.Parse(newKey);

        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var existing = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (existing is null)
            return null;

        if (string.Equals(existing.Key, parsed.ToWire(), StringComparison.Ordinal))
            return existing; // no-op re-key: nothing to record

        // The single implementation of the history rule (44-0 AC8):
        // idempotent, order-preserving, oldest first. A project MOVE never
        // reaches this method — the key is frozen on a move.
        var outgoing = WorkItemRef.Parse(existing.Key);
        existing.PreviousKeys = WorkItemKeyHistory.Record(existing.PreviousKeys, outgoing).ToList();
        existing.Key = parsed.ToWire();
        existing.UpdatedAt = DateTime.UtcNow;
        existing.Version += 1;
        await db.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var tid = RequireTenantId();
        await using var db = await tenantDbFactory.CreateAsync(tid);
        var row = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (row is null)
            return false;
        db.WorkItems.Remove(row);
        // Children RESTRICT the parent FK (the 44-2 409); relation edges
        // CASCADE away with the item.
        await db.SaveChangesAsync();
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

        var existing = await db.WorkItemRelations.FirstOrDefaultAsync(r =>
            r.SourceId == canonicalSource && r.TargetId == canonicalTarget && r.Kind == wire);
        if (existing is not null)
            return existing; // idempotent — the canonical row already exists

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
        await db.SaveChangesAsync();
        return relation;
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
