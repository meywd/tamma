using Microsoft.EntityFrameworkCore;
using Tamma.Data.Entities;

namespace Tamma.Data.Repositories;

/// <summary>
/// EF Core / Npgsql backed implementation of <see cref="IDiagnosticsRepository"/>.
/// </summary>
/// <remarks>
/// Read operations defer to EF's global query filter for tenant isolation
/// whenever the explicit <c>tenantId</c> parameter is <c>null</c>. The
/// aggregation query uses <c>date_trunc</c>/<c>to_timestamp</c> via raw SQL
/// for efficient server-side time bucketing, which requires a PostgreSQL
/// backend (the rest of the codebase already assumes this).
/// </remarks>
public class DiagnosticsRepository(TammaDbContext db) : IDiagnosticsRepository
{
    /// <inheritdoc />
    public async Task<Guid> InsertAsync(ProviderDiagnostic diagnostic)
    {
        // Preserve any caller-provided CreatedAt (tests need stable timestamps).
        if (diagnostic.CreatedAt == default)
            diagnostic.CreatedAt = DateTime.UtcNow;

        db.ProviderDiagnostics.Add(diagnostic);
        await db.SaveChangesAsync();
        return diagnostic.Id;
    }

    /// <inheritdoc />
    public Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(
        string? providerKey, DateTime? from, DateTime? to, int limit, int offset)
        => QueryAsync(providerKey, from, to, limit, offset,
            tenantId: null, success: null, model: null);

    /// <inheritdoc />
    public async Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(
        string? providerKey,
        DateTime? from,
        DateTime? to,
        int limit,
        int offset,
        Guid? tenantId,
        bool? success,
        string? model)
    {
        var query = db.ProviderDiagnostics.AsQueryable();

        if (!string.IsNullOrEmpty(providerKey))
            query = query.Where(d => d.ProviderKey == providerKey);
        if (tenantId.HasValue)
            query = query.Where(d => d.TenantId == tenantId.Value);
        if (from.HasValue)
        {
            var fromUtc = DateTime.SpecifyKind(from.Value, DateTimeKind.Utc);
            query = query.Where(d => d.CreatedAt >= fromUtc);
        }
        if (to.HasValue)
        {
            var toUtc = DateTime.SpecifyKind(to.Value, DateTimeKind.Utc);
            query = query.Where(d => d.CreatedAt <= toUtc);
        }
        if (success.HasValue)
            query = query.Where(d => d.Success == success.Value);
        if (!string.IsNullOrEmpty(model))
            query = query.Where(d => d.Model == model);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
        return (items, total);
    }

    /// <inheritdoc />
    public async Task<decimal> GetCostSumAsync(Guid? tenantId, DateTime from, DateTime to)
    {
        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);

        var query = db.ProviderDiagnostics
            .Where(d => d.CreatedAt >= fromUtc && d.CreatedAt < toUtc);

        if (tenantId.HasValue)
            query = query.Where(d => d.TenantId == tenantId.Value);

        // EF translates Sum over empty to null → coalesce to 0m.
        var sum = await query
            .Select(d => (decimal?)d.Cost)
            .SumAsync();
        return sum ?? 0m;
    }

    /// <inheritdoc />
    public async Task<List<DiagnosticsBucketRow>> AggregateAsync(
        DateTime from,
        DateTime to,
        TimeSpan bucket,
        Guid? tenantId)
    {
        if (bucket <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(bucket), "Bucket must be positive.");

        var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc);
        var bucketSeconds = (long)bucket.TotalSeconds;

        // Server-side bucketing via Postgres epoch arithmetic. The bucket
        // index is floor((epoch(created_at) - epoch(from)) / bucket_seconds);
        // multiplying back and adding to @from yields the bucket-start
        // timestamp. We return via ADO.NET so global query filters don't
        // matter — tenant isolation is handled by the @tenantId parameter.
        var sql = @"
            SELECT
              to_timestamp(
                EXTRACT(EPOCH FROM @p_from) +
                FLOOR((EXTRACT(EPOCH FROM ""CreatedAt"") - EXTRACT(EPOCH FROM @p_from)) / @p_bucket)::bigint * @p_bucket
              ) AT TIME ZONE 'UTC' AS bucket_start,
              COUNT(*)::bigint AS total_calls,
              COUNT(*) FILTER (WHERE ""Success"") ::bigint AS success_count,
              COALESCE(SUM(""Cost""), 0)::numeric AS total_cost,
              COALESCE(AVG(""RequestDurationMs""), 0)::double precision AS avg_latency_ms
            FROM ""provider_diagnostics""
            WHERE ""CreatedAt"" >= @p_from
              AND ""CreatedAt"" < @p_to
              AND (@p_tenant IS NULL OR ""TenantId"" = @p_tenant)
            GROUP BY bucket_start
            ORDER BY bucket_start;
        ";

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var pFrom = (Npgsql.NpgsqlParameter)cmd.CreateParameter();
        pFrom.ParameterName = "p_from";
        pFrom.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
        pFrom.Value = fromUtc;
        cmd.Parameters.Add(pFrom);

        var pTo = (Npgsql.NpgsqlParameter)cmd.CreateParameter();
        pTo.ParameterName = "p_to";
        pTo.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
        pTo.Value = toUtc;
        cmd.Parameters.Add(pTo);

        var pBucket = (Npgsql.NpgsqlParameter)cmd.CreateParameter();
        pBucket.ParameterName = "p_bucket";
        pBucket.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint;
        pBucket.Value = bucketSeconds;
        cmd.Parameters.Add(pBucket);

        var pTenant = (Npgsql.NpgsqlParameter)cmd.CreateParameter();
        pTenant.ParameterName = "p_tenant";
        pTenant.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
        pTenant.Value = (object?)tenantId ?? DBNull.Value;
        cmd.Parameters.Add(pTenant);

        var results = new List<DiagnosticsBucketRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var raw = reader.GetDateTime(0);
            var bucketStart = DateTime.SpecifyKind(raw, DateTimeKind.Utc);
            results.Add(new DiagnosticsBucketRow(
                BucketStart: bucketStart,
                TotalCalls: reader.GetInt64(1),
                SuccessCount: reader.GetInt64(2),
                TotalCost: reader.GetDecimal(3),
                AvgLatencyMs: reader.GetDouble(4)));
        }
        return results;
    }
}
