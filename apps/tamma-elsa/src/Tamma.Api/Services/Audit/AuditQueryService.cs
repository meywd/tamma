using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Dtos.Audit;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-3 — the audit query orchestrator. Parses are already done by
/// <see cref="AuditQueryFilter"/>; this service builds the EF query (structured
/// filters AND-combined, parameterized <c>ILIKE</c> search, keyset seek on
/// <c>source_sequence_number</c>), projects <see cref="AuditRecordResponse"/>,
/// computes a capped-estimate <c>total</c>, and appends the best-effort
/// <c>AUDIT.QUERIED</c> meta-audit event (AC10).
///
/// <para><b>Scope routing mirrors the Story 37-1 projector:</b> SaaS
/// tenant-scoped rows live in the tenant's own schema (reached via
/// <see cref="ITenantDbContextFactory"/>, filtered by <c>tenant_id</c>);
/// single-user rows + platform rows live in the control plane (single-user keyed
/// by <c>user_id</c>, platform keyed by both-null). Physical placement is the
/// first isolation wall; the explicit predicate is the second.</para>
/// </summary>
public sealed class AuditQueryService(
    ITenantDbContextFactory tenantDbFactory,
    ControlPlaneDbContext controlPlane,
    IEventRepository events,
    IPlatformEventRepository platformEvents,
    ITenantContext tenantContext,
    TimeProvider timeProvider,
    ILogger<AuditQueryService> logger)
    : IAuditQueryService
{
    private const string TenantScope = "tenant";
    private const string PlatformScope = "platform";

    public async Task<AuditQueryResponse> QueryTenantAsync(
        Guid tenantId, Guid? callerUserId, AuditQueryFilter filter, TammaMode mode, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        AuditQueryResponse response;
        if (mode == TammaMode.SaaS)
        {
            // Pin the ambient tenant context so any tenant-context-aware seam
            // engages, then read the tenant's OWN schema. The explicit
            // WHERE tenant_id == tid is the second wall behind the per-tenant
            // physical connection.
            tenantContext.SetTenantId(tenantId);
            await using var db = await tenantDbFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);
            response = await RunAsync(db, filter, r => r.TenantId == tenantId, ct).ConfigureAwait(false);
        }
        else
        {
            // single-user — the sole user owns every row; their rows live in the
            // control plane keyed by user_id.
            response = await RunAsync(
                controlPlane, filter, r => r.UserId == callerUserId, ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Audit query served: scope={Scope} tenantId={TenantId} actorUserId={ActorUserId} "
                + "filterKeys={FilterKeys} resultCount={ResultCount} total={Total} "
                + "mode={Mode} durationMs={DurationMs}",
            TenantScope, tenantId, callerUserId, filter.AppliedFilterKeys(),
            response.Records.Count, response.Total, mode, sw.ElapsedMilliseconds);

        await EmitMetaAuditAsync(TenantScope, tenantId, callerUserId, filter, response.Records.Count, mode, ct)
            .ConfigureAwait(false);
        return response;
    }

    public async Task<AuditQueryResponse> QueryPlatformAsync(
        Guid? callerUserId, AuditQueryFilter filter, TammaMode mode, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Platform-scope rows have no tenant owner. In single-user mode the sole
        // user is the operator and their rows are also tenant_id-null, so the
        // predicate is identical: read the CP rows with no tenant.
        var response = await RunAsync(
            controlPlane, filter, r => r.TenantId == null, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Audit query served: scope={Scope} tenantId={TenantId} actorUserId={ActorUserId} "
                + "filterKeys={FilterKeys} resultCount={ResultCount} total={Total} "
                + "mode={Mode} durationMs={DurationMs}",
            PlatformScope, (Guid?)null, callerUserId, filter.AppliedFilterKeys(),
            response.Records.Count, response.Total, mode, sw.ElapsedMilliseconds);

        await EmitMetaAuditAsync(PlatformScope, null, callerUserId, filter, response.Records.Count, mode, ct)
            .ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// The load-bearing query: scope predicate + structured filters + search +
    /// keyset seek, projected + paged, with a capped-estimate total.
    /// </summary>
    private static async Task<AuditQueryResponse> RunAsync(
        DbContext db,
        AuditQueryFilter f,
        System.Linq.Expressions.Expression<Func<AuditRecord, bool>> scope,
        CancellationToken ct)
    {
        // Search roots the query in a parameterized ILIKE over the text columns
        // AND the payload (jsonb cast to text). Parameterization makes an
        // injection-shaped q a literal (inert). No search ⇒ the plain DbSet.
        var filtered = SearchRoot(db, f)
            .Where(scope);
        filtered = ApplyStructuredFilters(filtered, f);

        // Capped exact count — bounds the cost so paging is never gated on a full
        // COUNT(*) over millions of rows (AC9). Take(cap) then Count → the planner
        // stops at the cap.
        var total = await filtered.Take(AuditQueryResponse.CountCap).CountAsync(ct).ConfigureAwait(false);
        var totalIsCapped = total >= AuditQueryResponse.CountCap;

        // Keyset seek (AC5): cursor < last-seen sequence, most-recent first. The
        // sequence is unique + monotonic so it is its own deterministic tiebreak.
        var pageQuery = filtered;
        if (f.Cursor is not null)
        {
            var cursor = f.Cursor.Value;
            pageQuery = pageQuery.Where(r => r.SourceSequenceNumber < cursor);
        }

        var page = await pageQuery
            .OrderByDescending(r => r.SourceSequenceNumber)
            .Take(f.Limit + 1) // +1 sentinel to compute nextCursor without a second query
            .Select(r => new AuditRecordResponse(
                r.Id,
                r.Category,
                r.ActionCode,
                r.ActorUserId,
                r.ActorEmailSnapshot,
                r.TargetType,
                r.TargetId,
                r.Severity,
                r.Outcome,
                r.IpAddress,
                r.OccurredAt,
                r.PayloadJson,
                r.SourceSequenceNumber))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = page.Count > f.Limit;
        var rows = hasMore ? page.Take(f.Limit).ToList() : page;
        var nextCursor = hasMore
            ? AuditQueryFilter.EncodeCursor(rows[^1].SourceSequenceNumber)
            : null;

        return new AuditQueryResponse(rows, nextCursor, total, totalIsCapped);
    }

    /// <summary>
    /// When <c>q</c> is present, root the query in a parameterized <c>ILIKE</c>
    /// over the searchable text columns + the payload (jsonb <c>::text</c> cast).
    /// Mirrors <c>EventRepository</c>'s raw-SQL jsonb search pattern. The
    /// interpolated <c>{term}</c> is a parameter — an injection-shaped term is a
    /// literal (inert). Unqualified <c>audit_records</c> resolves to the tenant
    /// schema via the per-tenant connection's search_path (or public on the CP).
    /// </summary>
    private static IQueryable<AuditRecord> SearchRoot(DbContext db, AuditQueryFilter f)
    {
        var set = db.Set<AuditRecord>();
        if (string.IsNullOrWhiteSpace(f.Search))
        {
            return set.AsNoTracking();
        }

        var term = "%" + EscapeLike(f.Search) + "%";
        return set.FromSqlInterpolated($@"
                SELECT * FROM audit_records
                WHERE ""ActorEmailSnapshot"" ILIKE {term}
                   OR ""TargetId"" ILIKE {term}
                   OR ""ActionCode"" ILIKE {term}
                   OR ""TargetType"" ILIKE {term}
                   OR ""PayloadJson""::text ILIKE {term}")
            .AsNoTracking();
    }

    private static IQueryable<AuditRecord> ApplyStructuredFilters(
        IQueryable<AuditRecord> q, AuditQueryFilter f)
    {
        if (f.Category is not null) q = q.Where(r => r.Category == f.Category);
        if (f.Action is not null) q = q.Where(r => r.ActionCode == f.Action);
        if (f.ActorUserId is not null) q = q.Where(r => r.ActorUserId == f.ActorUserId);
        if (f.TargetType is not null) q = q.Where(r => r.TargetType == f.TargetType);
        if (f.TargetId is not null) q = q.Where(r => r.TargetId == f.TargetId);
        if (f.Severity is not null) q = q.Where(r => r.Severity == f.Severity);
        if (f.Outcome is not null) q = q.Where(r => r.Outcome == f.Outcome);
        if (f.IpAddress is not null) q = q.Where(r => r.IpAddress == f.IpAddress);
        if (f.From is not null) q = q.Where(r => r.OccurredAt >= f.From);
        if (f.To is not null) q = q.Where(r => r.OccurredAt < f.To);
        return q;
    }

    /// <summary>Escape LIKE/ILIKE wildcards so a wildcard-shaped term can't widen
    /// the match. Postgres' default escape char is backslash.</summary>
    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// AC10 — append the <c>AUDIT.QUERIED</c> meta-audit event best-effort. A
    /// tenant read (SaaS) routes to the tenant store; a single-user or platform
    /// read routes to the control plane. A failure here is logged WARN and NEVER
    /// fails the user's read (the data is already materialized).
    /// </summary>
    private async Task EmitMetaAuditAsync(
        string scope, Guid? tenantId, Guid? actorUserId,
        AuditQueryFilter filter, int resultCount, TammaMode mode, CancellationToken ct)
    {
        try
        {
            var tags = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["tenantId"] = tenantId?.ToString(),
                ["actorUserId"] = actorUserId?.ToString(),
                ["scope"] = scope,
                ["mode"] = mode == TammaMode.SaaS ? "saas" : "single-user",
            });
            var data = JsonSerializer.Serialize(new
            {
                filters = filter.ToAuditableShape(),
                resultCount,
            });
            var metadata = JsonSerializer.Serialize(new
            {
                workflowVersion = "1.0.0",
                eventSource = "system",
            });
            var now = timeProvider.GetUtcNow().UtcDateTime;

            if (scope == TenantScope && mode == TammaMode.SaaS && tenantId is Guid tid)
            {
                await events.AppendAsync(new DomainEvent
                {
                    Id = Guid.NewGuid(),
                    Type = AuditQueryEventTypes.Queried,
                    TenantId = tid,
                    Tags = tags,
                    Data = data,
                    Metadata = metadata,
                    CreatedAt = now,
                }).ConfigureAwait(false);
            }
            else
            {
                // single-user tenant reads + all platform reads live in the CP
                // platform stream (tenant_id null).
                await platformEvents.AppendAsync(new PlatformEvent
                {
                    Id = Guid.NewGuid(),
                    Type = AuditQueryEventTypes.Queried,
                    TenantId = null,
                    Tags = tags,
                    Data = data,
                    Metadata = metadata,
                    CreatedAt = now,
                }, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "AUDIT.QUERIED meta-audit append failed (best-effort; the audit read "
                    + "was still served): scope={Scope} tenantId={TenantId}",
                scope, tenantId);
        }
    }
}
