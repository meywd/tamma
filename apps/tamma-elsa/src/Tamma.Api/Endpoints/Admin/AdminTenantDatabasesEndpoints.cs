using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tamma.Api.Dtos.Admin;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Unified-tenancy Phase 4 — platform-admin CRUD over the
/// <c>tenant_databases</c> registry (the operator's DB pool). Mirrors
/// <see cref="AdminTenantsEndpoints"/>: static minimal-API handlers behind
/// the <c>PlatformOwnerAccess</c> policy, registered in Program.cs under
/// <c>/api/admin/tenant-databases</c>.
///
/// <para>SECURITY: the admin connection string is plaintext INBOUND only
/// (POST/PATCH body). It is probed live (<c>SELECT 1</c> on a fresh
/// <see cref="NpgsqlConnection"/> — unreachable rows are rejected with 422
/// + the Npgsql error), Host/Port are parsed from it (no separate body
/// fields, so no mismatch is possible), then it is AES-GCM-encrypted via
/// <see cref="ITenantConnectionStringProtector"/> with the current KEK
/// version stamped. Neither the plaintext nor the envelope is EVER
/// serialised into any response (see <c>AdminTenantDatabaseDtos</c>).</para>
///
/// <para>Validation matrix: label unique → 409; placement/status enum →
/// 400; conn string unparsable → 400; unreachable → 422; delete with
/// TenantCount &gt; 0 OR any tenants.DatabaseId referencing the row
/// (defensive count — bookkeeping could drift) → 409. Delete is a hard
/// delete (zero-data project). Capacity stays advisory (Phase 2 note) —
/// CRUD validates shape, not global invariants.</para>
/// </summary>
public static class AdminTenantDatabasesEndpoints
{
    private static readonly HashSet<string> AllowedPlacementClasses =
        new(StringComparer.Ordinal) { "shared", "dedicated" };

    private static readonly HashSet<string> AllowedStatuses =
        new(StringComparer.Ordinal) { "active", "draining", "full", "retired" };

    /// <summary>Reachability-probe connect timeout (seconds) — keeps a dead
    /// host from pinning the request for Npgsql's 15s default.</summary>
    private const int ProbeTimeoutSeconds = 5;

    // ── GET /api/admin/tenant-databases ──

    public static async Task<IResult> ListDatabases(
        ControlPlaneDbContext db,
        CancellationToken ct = default)
    {
        var items = await db.TenantDatabases
            .AsNoTracking()
            .OrderBy(d => d.Label)
            .Select(d => ToListItem(d))
            .ToListAsync(ct);

        return Results.Ok(new AdminTenantDatabaseListResponse(items, items.Count));
    }

    // ── GET /api/admin/tenant-databases/{databaseId} ──

    /// <summary>Row + the tenants placed on it (tenant→DB view, pool side).</summary>
    public static async Task<IResult> GetDatabaseDetail(
        Guid databaseId,
        ControlPlaneDbContext db,
        CancellationToken ct = default)
    {
        var row = await db.TenantDatabases
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == databaseId, ct);
        if (row is null)
            return Results.NotFound(new { error = "tenant_database_not_found" });

        // Shadow-column join: tenants.DatabaseId / tenants.SchemaName are
        // EF shadow properties (TammaModelConfiguration). Soft-deleted
        // rows stay visible here for audit, mirroring AdminTenantsEndpoints.
        var tenants = await db.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.DeletedAt == null
                && EF.Property<Guid?>(t, "DatabaseId") == databaseId)
            .OrderBy(t => t.Slug)
            .Select(t => new AdminTenantDatabaseTenantItem(
                t.Id,
                t.Slug,
                EF.Property<string?>(t, "SchemaName"),
                EF.Property<string?>(t, "Status")))
            .ToListAsync(ct);

        return Results.Ok(new AdminTenantDatabaseDetailResponse(ToListItem(row), tenants));
    }

    // ── POST /api/admin/tenant-databases ──

    public static async Task<IResult> CreateDatabase(
        CreateTenantDatabaseRequest req,
        ControlPlaneDbContext db,
        ITenantConnectionStringProtector protector,
        [FromServices] TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Label))
            return Results.BadRequest(new { error = "label_required" });
        if (string.IsNullOrWhiteSpace(req.AdminConnectionString))
            return Results.BadRequest(new { error = "admin_connection_string_required" });

        var placementClass = string.IsNullOrWhiteSpace(req.PlacementClass)
            ? "shared" : req.PlacementClass;
        if (!AllowedPlacementClasses.Contains(placementClass))
            return Results.BadRequest(new
            {
                error = "invalid_placement_class",
                message = $"placementClass must be one of: {string.Join(", ", AllowedPlacementClasses)}",
            });

        if (req.TenantCapacity is < 1)
            return Results.BadRequest(new { error = "invalid_tenant_capacity" });

        var label = req.Label.Trim();
        if (await db.TenantDatabases.AnyAsync(d => d.Label == label, ct))
            return DuplicateLabel(label);

        // Host/Port come FROM the connection string — by design there are
        // no Host/Port body fields to disagree with.
        if (!TryParse(req.AdminConnectionString, out var builder, out var parseError))
            return Results.BadRequest(new
            {
                error = "invalid_connection_string",
                message = parseError,
            });

        // Live reachability probe: a pool row that cannot serve a
        // SELECT 1 would brick every lifecycle step routed at it.
        var probeError = await ProbeAsync(req.AdminConnectionString, ct);
        if (probeError is not null)
            return Unreachable(probeError);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var row = new TenantDatabase
        {
            Id = Guid.NewGuid(),
            Label = label,
            Host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host,
            Port = builder.Port,
            AdminConnectionStringEncrypted = protector.Encrypt(req.AdminConnectionString),
            PlacementClass = placementClass,
            TierEligibility = req.TierEligibility ?? [],
            TenantCapacity = req.TenantCapacity,
            TenantCount = 0,
            Status = "active",
            KekVersion = (short)protector.CurrentKekVersion,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TenantDatabases.Add(row);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/admin/tenant-databases/{row.Id}", ToListItem(row));
    }

    // ── PATCH /api/admin/tenant-databases/{databaseId} ──

    public static async Task<IResult> UpdateDatabase(
        Guid databaseId,
        UpdateTenantDatabaseRequest req,
        ControlPlaneDbContext db,
        ITenantConnectionStringProtector protector,
        ITenantDatabasePool pool,
        [FromServices] TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        var row = await db.TenantDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId, ct);
        if (row is null)
            return Results.NotFound(new { error = "tenant_database_not_found" });

        if (req.Status is not null && !AllowedStatuses.Contains(req.Status))
            return Results.BadRequest(new
            {
                error = "invalid_status",
                message = $"status must be one of: {string.Join(", ", AllowedStatuses)}",
            });

        if (req.TenantCapacity is < 1)
            return Results.BadRequest(new { error = "invalid_tenant_capacity" });

        string? newLabel = null;
        if (req.Label is not null)
        {
            newLabel = req.Label.Trim();
            if (newLabel.Length == 0)
                return Results.BadRequest(new { error = "label_required" });
            if (newLabel != row.Label
                && await db.TenantDatabases.AnyAsync(
                    d => d.Label == newLabel && d.Id != databaseId, ct))
                return DuplicateLabel(newLabel);
        }

        var rotated = false;
        if (req.AdminConnectionString is not null)
        {
            if (string.IsNullOrWhiteSpace(req.AdminConnectionString))
                return Results.BadRequest(new { error = "admin_connection_string_required" });
            if (!TryParse(req.AdminConnectionString, out var builder, out var parseError))
                return Results.BadRequest(new
                {
                    error = "invalid_connection_string",
                    message = parseError,
                });

            // Rotation gets the same reachability gate as create — an
            // unreachable rotated string would brick the row just the same.
            var probeError = await ProbeAsync(req.AdminConnectionString, ct);
            if (probeError is not null)
                return Unreachable(probeError);

            row.Host = string.IsNullOrWhiteSpace(builder.Host) ? "localhost" : builder.Host;
            row.Port = builder.Port;
            row.AdminConnectionStringEncrypted = protector.Encrypt(req.AdminConnectionString);
            row.KekVersion = (short)protector.CurrentKekVersion;
            rotated = true;
        }

        if (newLabel is not null) row.Label = newLabel;
        if (req.TierEligibility is not null) row.TierEligibility = req.TierEligibility;
        if (req.TenantCapacity is not null) row.TenantCapacity = req.TenantCapacity;
        if (req.Status is not null) row.Status = req.Status;
        row.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);

        // Only AFTER the new envelope is durably persisted: drop the pool's
        // cached decrypt so the next lifecycle step re-reads the row.
        if (rotated)
            pool.EvictAdminConnection(databaseId);

        return Results.Ok(ToListItem(row));
    }

    // ── DELETE /api/admin/tenant-databases/{databaseId} ──

    public static async Task<IResult> DeleteDatabase(
        Guid databaseId,
        ControlPlaneDbContext db,
        ITenantDatabasePool pool,
        CancellationToken ct = default)
    {
        var row = await db.TenantDatabases
            .FirstOrDefaultAsync(d => d.Id == databaseId, ct);
        if (row is null)
            return Results.NotFound(new { error = "tenant_database_not_found" });

        // Defensive double-check: the bookkept counter AND a live count of
        // tenants whose DatabaseId shadow column references this row — the
        // two could drift, and either being non-zero means the row still
        // hosts schemas. The FK is Restrict, so this 409 is the friendly
        // face of the same invariant.
        var referencing = await db.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(t => EF.Property<Guid?>(t, "DatabaseId") == databaseId, ct);
        if (row.TenantCount != 0 || referencing != 0)
            return Results.Json(
                new
                {
                    error = "tenant_database_in_use",
                    message = $"tenant_databases row '{row.Label}' still hosts tenants "
                        + $"(TenantCount={row.TenantCount}, referencing={referencing}) — "
                        + "move or delete them first.",
                    tenantCount = row.TenantCount,
                    referencingTenants = referencing,
                },
                statusCode: StatusCodes.Status409Conflict);

        db.TenantDatabases.Remove(row);
        await db.SaveChangesAsync(ct);
        // Hygiene: a deleted row must not linger in the decrypt cache.
        pool.EvictAdminConnection(databaseId);

        return Results.NoContent();
    }

    // ── helpers ──

    private static AdminTenantDatabaseListItem ToListItem(TenantDatabase d) =>
        new(
            d.Id,
            d.Label,
            d.Host,
            d.Port,
            d.PlacementClass,
            d.TierEligibility,
            d.TenantCapacity,
            d.TenantCount,
            d.Status,
            d.KekVersion,
            d.CreatedAt,
            d.UpdatedAt);

    private static bool TryParse(
        string connectionString,
        out NpgsqlConnectionStringBuilder builder,
        out string? error)
    {
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            builder = new NpgsqlConnectionStringBuilder();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Live <c>SELECT 1</c> on a fresh <see cref="NpgsqlConnection"/>
    /// (short connect timeout). Returns null when reachable, otherwise the
    /// Npgsql/socket error message for the 422 body.
    /// </summary>
    private static async Task<string?> ProbeAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            var probe = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Timeout = ProbeTimeoutSeconds,
            };
            await using var conn = new NpgsqlConnection(probe.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = ProbeTimeoutSeconds;
            await cmd.ExecuteScalarAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // NpgsqlException, SocketException, TimeoutException, auth
            // failures (PostgresException 28P01), ... — all mean "this row
            // cannot serve provisioning DDL".
            return ex.Message;
        }
    }

    private static IResult DuplicateLabel(string label) =>
        Results.Json(
            new
            {
                error = "duplicate_label",
                message = $"tenant_databases already has a row labelled '{label}'.",
            },
            statusCode: StatusCodes.Status409Conflict);

    private static IResult Unreachable(string npgsqlError) =>
        Results.Json(
            new
            {
                error = "database_unreachable",
                message = "SELECT 1 reachability probe failed for the supplied admin "
                    + "connection string.",
                detail = npgsqlError,
            },
            statusCode: StatusCodes.Status422UnprocessableEntity);
}
