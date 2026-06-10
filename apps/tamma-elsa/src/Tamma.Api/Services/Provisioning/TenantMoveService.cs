using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.TenantLifecycle;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
// The legacy Cranl resolver (Tamma.Api.Services.Provisioning.
// ITenantConnectionResolver) shadows the pool-cache abstraction inside
// this namespace — alias the one the move engine actually evicts.
using IPoolResolver = Tamma.Data.Abstractions.ITenantConnectionResolver;

namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// Options for the Phase 4 tenant move engine (section <c>TenantMove</c>).
/// </summary>
public sealed class TenantMoveOptions
{
    public const string SectionName = "TenantMove";

    /// <summary>Path to the <c>pg_dump</c> binary (PATH-resolved by default).</summary>
    public string PgDumpPath { get; set; } = "pg_dump";

    /// <summary>Path to the <c>pg_restore</c> binary (PATH-resolved by default).</summary>
    public string PgRestorePath { get; set; } = "pg_restore";

    /// <summary>Hard timeout for each tool run, in seconds (default 30 minutes).</summary>
    public int TimeoutSeconds { get; set; } = 30 * 60;
}

/// <summary>
/// Unified-tenancy Phase 4 — <see cref="ITenantMoveService"/>: moves a
/// tenant's <c>t_&lt;hex&gt;</c> schema to another <c>tenant_databases</c>
/// pool row with a brief per-tenant read-only window (parent plan
/// decision 4). Step order (each step idempotent or safely re-runnable;
/// step transitions log with a <c>tenant.move.&lt;step&gt;</c> prefix):
/// <list type="number">
///   <item><description><b>validate</b> — tenant exists, not deleted,
///     Status 'active' (or 'draining' when resuming an interrupted move),
///     has placement; target row exists, is 'active', differs from the
///     source, and passes the SAME tier-eligibility/capacity predicate
///     placement uses (<see cref="TenantPlacementService.EligibleFor"/>).</description></item>
///   <item><description><b>drain</b> — Status → 'draining' + pool evict.
///     In-flight requests finish; new mutating verbs 503 at the
///     middleware; reads keep flowing (the LRU resolver treats
///     'draining' as connection-yielding).</description></item>
///   <item><description><b>dump</b> — <c>pg_dump -F c -n t_&lt;hex&gt;</c>
///     from the SOURCE row to a tmp file (password via PGPASSWORD only,
///     mirroring <see cref="BackupTenantDatabaseActivity"/>).</description></item>
///   <item><description><b>role</b> — same-cluster (source Host:Port ==
///     target Host:Port): skip, roles are cluster-wide. Cross-cluster:
///     <see cref="ITenantProvisioningService.CreateRoleAsync"/> on the
///     target (fresh password); a pre-existing target role whose password
///     is unrecoverable aborts with the DROP OWNED BY runbook.</description></item>
///   <item><description><b>schema</b> — drop any leftover target schema
///     from a previously failed attempt (the tenant's live data is still
///     on the source), then
///     <see cref="ITenantProvisioningService.CreateSchemaAsync"/> on the
///     target (CREATE SCHEMA AUTHORIZATION + GRANT CONNECT + per-DB
///     search_path default).</description></item>
///   <item><description><b>restore</b> — <c>pg_restore --no-owner --role
///     &lt;tenant role&gt;</c> into the target row's database. The admin
///     user must be allowed to <c>SET ROLE</c> to the tenant role
///     (superuser or member). pg_restore exits 1 for ignorable errors —
///     notably "schema already exists" for the schema we pre-created in
///     step 5 — so a non-zero exit is logged but NOT fatal by itself;
///     the history verification below is the authoritative gate.
///     <b>verify</b>: <c>__TenantMigrationsHistory</c> row count in the
///     target schema must equal the source's — mismatch aborts (source
///     intact, tenant still 'draining').</description></item>
///   <item><description><b>repoint</b> — mint the new connection string
///     (same-cluster: decrypt the current envelope and swap only
///     <c>Database</c>; cross-cluster: build fresh with the new
///     password), encrypt + persist envelope + KekVersion, flip
///     tenants.DatabaseId, and shift TenantCount source−1/target+1 in
///     ONE SaveChanges.</description></item>
///   <item><description><b>verify_target</b> — evict the pool again and
///     open a real TenantDbContext through the production factory; a
///     trivial query proves the re-pointed envelope resolves.</description></item>
///   <item><description><b>drop_source</b> — <c>DROP SCHEMA IF EXISTS
///     ... CASCADE</c> on the source row; cross-cluster additionally
///     DROP OWNED BY + DROP ROLE on the SOURCE cluster (the role owns
///     nothing else there); same-cluster keeps the role (it owns the
///     target schema).</description></item>
///   <item><description><b>activate</b> — Status → 'active'. The tmp
///     dump file is deleted in a finally regardless of outcome.</description></item>
/// </list>
///
/// <para><b>Failure windows:</b> any failure in steps 2-6 leaves the
/// tenant 'draining' with the source schema intact — the operator
/// re-runs <see cref="MoveAsync"/> (steps are idempotent; note a
/// cross-cluster retry whose previous attempt already created the target
/// role aborts with the DROP OWNED BY runbook, because that role's
/// password is unrecoverable) or PATCHes the status back to 'active' to
/// cancel the move. A failure AFTER step 7 committed leaves the tenant
/// pointing at the TARGET (still 'draining'): re-running
/// <see cref="MoveAsync"/> with the same target detects the committed
/// re-point and completes the tail — sweep stale copies of the schema
/// off every other pool row, then activate.</para>
/// </summary>
public sealed class TenantMoveService : ITenantMoveService
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _cpFactory;
    private readonly ITenantDatabasePool _pool;
    private readonly ITenantProvisioningService _provisioning;
    private readonly IProcessRunner _processRunner;
    private readonly IConnectionStringDecryptor _decryptor;
    private readonly ITenantConnectionStringProtector _protector;
    private readonly IPoolResolver _resolver;
    private readonly ITenantDbContextFactory _tenantDbFactory;
    private readonly TenantMoveOptions _options;
    private readonly ILogger<TenantMoveService> _logger;

    public TenantMoveService(
        IDbContextFactory<ControlPlaneDbContext> cpFactory,
        ITenantDatabasePool pool,
        ITenantProvisioningService provisioning,
        IProcessRunner processRunner,
        IConnectionStringDecryptor decryptor,
        ITenantConnectionStringProtector protector,
        IPoolResolver resolver,
        ITenantDbContextFactory tenantDbFactory,
        Microsoft.Extensions.Options.IOptions<TenantMoveOptions> options,
        ILogger<TenantMoveService> logger)
    {
        _cpFactory = cpFactory;
        _pool = pool;
        _provisioning = provisioning;
        _processRunner = processRunner;
        _decryptor = decryptor;
        _protector = protector;
        _resolver = resolver;
        _tenantDbFactory = tenantDbFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task MoveAsync(
        Guid tenantId, Guid targetDatabaseId, CancellationToken ct = default)
    {
        // ── Step 1: validate ─────────────────────────────────────────────
        var plan = await ValidateAsync(tenantId, targetDatabaseId, ct);
        var schema = plan.SchemaName;
        var quotedSchema = TenantNaming.Quote(schema);
        var roleName = TenantNaming.RoleName(tenantId);

        if (plan.IsResume)
        {
            // The step-7 re-point already committed in a prior run — the
            // tenant points at the TARGET. Complete the tail: sweep stale
            // schema copies off every other pool row, then activate.
            _logger.LogInformation(
                "tenant.move.resume tenantId={TenantId} targetDatabaseId={TargetDatabaseId} "
                + "schema={Schema}", tenantId, targetDatabaseId, schema);
            await SweepStaleSchemasAsync(tenantId, plan.Target, schema, roleName, ct);
            await SetStatusAsync(tenantId, "active", ct);
            _logger.LogInformation(
                "tenant.move.activate tenantId={TenantId} (resume tail completed)", tenantId);
            return;
        }

        var source = plan.Source!;
        var target = plan.Target;
        var sameCluster =
            string.Equals(source.Host, target.Host, StringComparison.OrdinalIgnoreCase)
            && source.Port == target.Port;
        _logger.LogInformation(
            "tenant.move.validate tenantId={TenantId} sourceDatabaseId={SourceDatabaseId} "
            + "targetDatabaseId={TargetDatabaseId} schema={Schema} sameCluster={SameCluster}",
            tenantId, source.Id, target.Id, schema, sameCluster);

        var dumpFile = Path.Combine(
            Path.GetTempPath(), $"tamma-move-{schema}-{Guid.NewGuid():N}.dump");
        try
        {
            // ── Step 2: drain (read-only window opens) ───────────────────
            await SetStatusAsync(tenantId, "draining", ct);
            await _resolver.EvictAsync(tenantId, ct);
            _logger.LogInformation(
                "tenant.move.drain tenantId={TenantId} (writes now 503; reads keep flowing)",
                tenantId);

            // ── Step 3: dump the schema from the SOURCE row ──────────────
            var sourceInfo = await _pool.GetConnectionInfoAsync(source.Id, ct);
            await RunPgToolAsync(
                _options.PgDumpPath,
                PgToolArguments.ForPgDump(sourceInfo, dumpFile, schema),
                sourceInfo.Password,
                tenantId, step: "dump", failureIsFatal: true, ct);
            _logger.LogInformation(
                "tenant.move.dump tenantId={TenantId} schema={Schema} file={File}",
                tenantId, schema, dumpFile);

            // ── Step 4: role on the target cluster ───────────────────────
            var targetPlacement = new TenantPlacement(target.Id, schema);
            string? freshPassword = null;
            if (sameCluster)
            {
                _logger.LogInformation(
                    "tenant.move.role tenantId={TenantId} same_cluster_skip (roles are "
                    + "cluster-wide; credentials carry over)", tenantId);
            }
            else
            {
                freshPassword = await _provisioning.CreateRoleAsync(tenantId, targetPlacement, ct);
                if (freshPassword is null)
                {
                    throw new InvalidOperationException(
                        $"Tenant role '{roleName}' already exists on target pool row "
                        + $"{target.Id} and its password is unrecoverable (likely a prior "
                        + "failed cross-cluster move attempt). Operator runbook: connect to "
                        + $"the target cluster, run 'DROP OWNED BY {roleName}' then "
                        + $"'DROP ROLE {roleName}', and re-run the move.");
                }
                _logger.LogInformation(
                    "tenant.move.role tenantId={TenantId} created_on_target "
                    + "targetDatabaseId={TargetDatabaseId}", tenantId, target.Id);
            }

            // ── Step 5: schema on the target ─────────────────────────────
            // Defensive pre-drop: a leftover schema from a previously
            // failed attempt may hold partial objects that would corrupt
            // the restore (COPY into half-restored tables). The tenant's
            // live data is on the SOURCE — anything under this name on the
            // target is garbage by definition at this point.
            await _pool.ExecuteOnAsync(
                target.Id, $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE;", ct);
            await _provisioning.CreateSchemaAsync(tenantId, targetPlacement, ct);
            _logger.LogInformation(
                "tenant.move.schema tenantId={TenantId} schema={Schema} "
                + "targetDatabaseId={TargetDatabaseId}", tenantId, schema, target.Id);

            // ── Step 6: restore into the target + verify history ─────────
            var targetInfo = await _pool.GetConnectionInfoAsync(target.Id, ct);
            await RunPgToolAsync(
                _options.PgRestorePath,
                PgToolArguments.ForPgRestore(targetInfo, dumpFile, roleName),
                targetInfo.Password,
                tenantId, step: "restore",
                // pg_restore exits 1 when it hit ignorable errors — the
                // pre-created schema's "already exists" is guaranteed to
                // trip that. The history verification below is the
                // authoritative success gate; a genuinely failed restore
                // cannot produce a matching history count.
                failureIsFatal: false, ct);
            _logger.LogInformation(
                "tenant.move.restore tenantId={TenantId} schema={Schema} "
                + "targetDatabaseId={TargetDatabaseId}", tenantId, schema, target.Id);

            await VerifyHistoryAsync(tenantId, source.Id, target.Id, quotedSchema, ct);

            // ── Step 7: re-point envelope + bookkeeping (ONE SaveChanges) ─
            await RepointAsync(
                tenantId, source.Id, target.Id, targetPlacement, sameCluster,
                freshPassword, ct);

            // ── Step 8: evict + verify through the production factory ────
            await _resolver.EvictAsync(tenantId, ct);
            await using (var tenantCtx = await _tenantDbFactory.CreateAsync(tenantId, ct))
            {
                // Trivial query against a tenant-schema table proves the
                // re-pointed envelope decrypts, connects, and lands in the
                // restored schema (mirrors the provisioning verify).
                _ = await tenantCtx.AgentConfigs.AsNoTracking()
                    .FirstOrDefaultAsync(ct);
            }
            _logger.LogInformation(
                "tenant.move.verify_target tenantId={TenantId} (resolver round-trip ok)",
                tenantId);

            // ── Step 9: drop the source schema (and role, cross-cluster) ─
            await _pool.ExecuteOnAsync(
                source.Id, $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE;", ct);
            if (!sameCluster)
            {
                await DropSourceRoleAsync(source.Id, roleName, ct);
            }
            _logger.LogInformation(
                "tenant.move.drop_source tenantId={TenantId} sourceDatabaseId={SourceDatabaseId} "
                + "droppedRole={DroppedRole}", tenantId, source.Id, !sameCluster);

            // ── Step 10: activate (read-only window closes) ──────────────
            await SetStatusAsync(tenantId, "active", ct);
            _logger.LogInformation(
                "tenant.move.activate tenantId={TenantId} targetDatabaseId={TargetDatabaseId}",
                tenantId, target.Id);
        }
        finally
        {
            // Tmp-file hygiene regardless of outcome — the dump may hold a
            // full copy of the tenant's data.
            try
            {
                if (File.Exists(dumpFile)) File.Delete(dumpFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "tenant.move.tmp_cleanup_failed tenantId={TenantId} file={File}",
                    tenantId, dumpFile);
            }
        }
    }

    // ── step helpers ──────────────────────────────────────────────────────

    private sealed record MovePlan(
        TenantDatabase? Source,
        TenantDatabase Target,
        string SchemaName,
        bool IsResume);

    private async Task<MovePlan> ValidateAsync(
        Guid tenantId, Guid targetDatabaseId, CancellationToken ct)
    {
        await using var db = await _cpFactory.CreateDbContextAsync(ct);

        var tenant = await db.Tenants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException(
                $"Tenant '{tenantId}' not found — cannot move.");
        if (tenant.DeletedAt is not null)
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' is soft-deleted — move is not allowed.");

        var entry = db.Entry(tenant);
        var status = entry.Property<string?>("Status").CurrentValue;
        // 'active' starts a fresh move; 'draining' resumes an interrupted
        // one (failure policy: steps 2-6 leave the tenant draining and the
        // operator re-runs MoveAsync). Anything else is a lifecycle state
        // the move must not touch.
        if (status is not null
            && !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "draining", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' has status '{status}' — only 'active' tenants can be "
                + "moved ('draining' resumes an interrupted move).");
        }

        var schemaName = entry.Property<string?>("SchemaName").CurrentValue;
        var sourceDatabaseId = entry.Property<Guid?>("DatabaseId").CurrentValue;
        if (schemaName is null || sourceDatabaseId is null)
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' has no placement (DatabaseId/SchemaName) — provision it "
                + "before moving.");

        var target = await db.TenantDatabases
                .FirstOrDefaultAsync(d => d.Id == targetDatabaseId, ct)
            ?? throw new InvalidOperationException(
                $"Target tenant_databases row '{targetDatabaseId}' does not exist.");

        // Resume detection: the step-7 re-point already committed — the
        // tenant points at the requested target and is still draining.
        if (sourceDatabaseId.Value == targetDatabaseId)
        {
            if (string.Equals(status, "draining", StringComparison.OrdinalIgnoreCase))
                return new MovePlan(null, target, schemaName, IsResume: true);
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' is already placed on database '{targetDatabaseId}' — "
                + "target must differ from the source.");
        }

        if (!string.Equals(target.Status, "active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Target tenant_databases row '{targetDatabaseId}' has status "
                + $"'{target.Status}' — only 'active' rows accept moves.");

        var source = await db.TenantDatabases
                .FirstOrDefaultAsync(d => d.Id == sourceDatabaseId.Value, ct)
            ?? throw new InvalidOperationException(
                $"Source tenant_databases row '{sourceDatabaseId}' does not exist — the "
                + "tenant's placement is corrupt.");

        // Tier eligibility/capacity — the SAME predicate placement uses
        // (TenantPlacementService.EligibleFor), evaluated in-memory on the
        // loaded target row.
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Slug == tenant.Plan, ct)
            ?? throw new InvalidOperationException(
                $"No plans row for slug '{tenant.Plan}' (tenant '{tenantId}') — move "
                + "requires plans.PlacementPolicy; seed or repair the plans table.");
        if (!TenantPlacementService.EligibleFor(plan.Slug, plan.PlacementPolicy)
                .Compile()(target))
        {
            throw new InvalidOperationException(
                $"Target tenant_databases row '{targetDatabaseId}' is not eligible for tier "
                + $"'{plan.Slug}' (placement policy '{plan.PlacementPolicy}'): need an active "
                + $"row with PlacementClass '{plan.PlacementPolicy}', tier eligibility, and "
                + "capacity headroom"
                + (plan.PlacementPolicy == "dedicated"
                    ? " — dedicated rows host exactly one tenant" : string.Empty)
                + ".");
        }

        return new MovePlan(source, target, schemaName, IsResume: false);
    }

    private async Task SetStatusAsync(Guid tenantId, string status, CancellationToken ct)
    {
        await using var db = await _cpFactory.CreateDbContextAsync(ct);
        var tenant = await db.Tenants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new InvalidOperationException(
                $"Tenant '{tenantId}' disappeared mid-move — cannot set status '{status}'.");
        db.Entry(tenant).Property<string?>("Status").CurrentValue = status;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Run pg_dump/pg_restore through the <see cref="IProcessRunner"/>
    /// seam. Password travels via PGPASSWORD ONLY (argv is world-readable
    /// through /proc). Stderr is logged truncated but never embedded in
    /// thrown messages (it can echo connection details).
    /// </summary>
    private async Task RunPgToolAsync(
        string fileName,
        List<string> arguments,
        string password,
        Guid tenantId,
        string step,
        bool failureIsFatal,
        CancellationToken ct)
    {
        var result = await _processRunner.RunAsync(
            new ProcessRunRequest(
                FileName: fileName,
                Arguments: arguments,
                WorkingDirectory: Path.GetTempPath(),
                EnvironmentOverrides: new Dictionary<string, string>
                {
                    ["PGPASSWORD"] = password,
                },
                TimeoutSeconds: _options.TimeoutSeconds),
            ct).ConfigureAwait(false);

        if (result.TimedOut)
            throw new InvalidOperationException(
                $"{fileName} timed out after {_options.TimeoutSeconds}s during tenant move "
                + $"step '{step}' (tenant {tenantId}).");

        if (result.ExitCode != 0)
        {
            _logger.LogWarning(
                "tenant.move.{Step} tool_nonzero_exit tenantId={TenantId} tool={Tool} "
                + "exit={Exit} fatal={Fatal} stderr={StdErr}",
                step, tenantId, fileName, result.ExitCode, failureIsFatal,
                Truncate(result.StdErr));
            if (failureIsFatal)
                throw new InvalidOperationException(
                    $"{fileName} failed (exit {result.ExitCode}) during tenant move step "
                    + $"'{step}' (tenant {tenantId}). See logs for stderr.");
        }
    }

    private async Task VerifyHistoryAsync(
        Guid tenantId, Guid sourceDatabaseId, Guid targetDatabaseId,
        string quotedSchema, CancellationToken ct)
    {
        var sql = $"SELECT count(*) FROM {quotedSchema}.\"__TenantMigrationsHistory\";";
        var sourceCount = Convert.ToInt64(
            await _pool.ExecuteScalarOnAsync(sourceDatabaseId, sql, ct) ?? -1L);
        var targetCount = Convert.ToInt64(
            await _pool.ExecuteScalarOnAsync(targetDatabaseId, sql, ct) ?? -1L);

        if (sourceCount < 0 || sourceCount != targetCount)
        {
            // Source intact; tenant stays 'draining' — operator retries
            // MoveAsync or PATCHes the status back to 'active'.
            throw new InvalidOperationException(
                $"Tenant move aborted for '{tenantId}': __TenantMigrationsHistory row count "
                + $"on the target ({targetCount}) does not match the source ({sourceCount}) — "
                + "the restore is incomplete. Source schema is intact; tenant remains "
                + "'draining'. Re-run the move or reset the status to 'active'.");
        }
        _logger.LogInformation(
            "tenant.move.verify tenantId={TenantId} historyRows={Rows}",
            tenantId, sourceCount);
    }

    private async Task RepointAsync(
        Guid tenantId,
        Guid sourceDatabaseId,
        Guid targetDatabaseId,
        TenantPlacement targetPlacement,
        bool sameCluster,
        string? freshPassword,
        CancellationToken ct)
    {
        string newConnectionString;
        if (sameCluster)
        {
            // Keep role + password + Search Path; swap only the Database.
            await using var readDb = await _cpFactory.CreateDbContextAsync(ct);
            var row = await readDb.Tenants.IgnoreQueryFilters()
                .Where(t => t.Id == tenantId)
                .Select(t => new
                {
                    Envelope = EF.Property<byte[]?>(t, "EncryptedConnectionString"),
                    KekVersion = (int?)EF.Property<short>(t, "KekVersion"),
                })
                .FirstAsync(ct);
            if (row.Envelope is null || row.Envelope.Length == 0)
                throw new InvalidOperationException(
                    $"Tenant '{tenantId}' has no stored connection-string envelope — cannot "
                    + "re-point a same-cluster move (the credentials live only in the envelope).");
            var current = _decryptor.Decrypt(row.Envelope, row.KekVersion);
            var builder = new NpgsqlConnectionStringBuilder(current)
            {
                Database = await _pool.GetDatabaseNameAsync(targetDatabaseId, ct),
            };
            newConnectionString = builder.ConnectionString;
        }
        else
        {
            newConnectionString = await _provisioning.BuildConnectionStringAsync(
                tenantId, targetPlacement, freshPassword!, ct);
        }

        // ONE SaveChanges: envelope, placement stamp, and both pool-row
        // counters move together or not at all.
        await using var db = await _cpFactory.CreateDbContextAsync(ct);
        var tenant = await db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == tenantId, ct);
        var entry = db.Entry(tenant);
        entry.Property("EncryptedConnectionString").CurrentValue =
            _protector.Encrypt(newConnectionString);
        entry.Property("KekVersion").CurrentValue = (short)_protector.CurrentKekVersion;
        entry.Property<Guid?>("DatabaseId").CurrentValue = targetDatabaseId;
        var now = DateTime.UtcNow;
        tenant.UpdatedAt = now;

        var source = await db.TenantDatabases.FirstAsync(d => d.Id == sourceDatabaseId, ct);
        var target = await db.TenantDatabases.FirstAsync(d => d.Id == targetDatabaseId, ct);
        source.TenantCount = Math.Max(0, source.TenantCount - 1);
        source.UpdatedAt = now;
        target.TenantCount += 1;
        target.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "tenant.move.repoint tenantId={TenantId} databaseId={TargetDatabaseId} "
            + "sameCluster={SameCluster} kek={Kek}",
            tenantId, targetDatabaseId, sameCluster, _protector.CurrentKekVersion);
    }

    /// <summary>
    /// Resume tail (re-run after a committed step 7): the original source
    /// row id is no longer recorded anywhere, but the schema name is
    /// globally unique per tenant (<c>t_&lt;hex&gt;</c>) — any pool row
    /// other than the target that still holds it is the stale source copy.
    /// </summary>
    private async Task SweepStaleSchemasAsync(
        Guid tenantId, TenantDatabase target, string schema, string roleName,
        CancellationToken ct)
    {
        var quotedSchema = TenantNaming.Quote(schema);
        List<TenantDatabase> rows;
        await using (var db = await _cpFactory.CreateDbContextAsync(ct))
        {
            rows = await db.TenantDatabases.Where(d => d.Id != target.Id).ToListAsync(ct);
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!await _pool.SchemaExistsOnAsync(row.Id, schema, ct))
                continue;

            await _pool.ExecuteOnAsync(
                row.Id, $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE;", ct);
            var sameCluster =
                string.Equals(row.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                && row.Port == target.Port;
            if (!sameCluster)
                await DropSourceRoleAsync(row.Id, roleName, ct);
            _logger.LogInformation(
                "tenant.move.drop_source tenantId={TenantId} sourceDatabaseId={SourceDatabaseId} "
                + "droppedRole={DroppedRole} (resume sweep)", tenantId, row.Id, !sameCluster);
        }
    }

    private async Task DropSourceRoleAsync(
        Guid sourceDatabaseId, string roleName, CancellationToken ct)
    {
        // Cross-cluster only: the role has no objects left on the source
        // cluster after the schema drop. DROP OWNED BY clears residual
        // grants (GRANT CONNECT, per-DB search_path default) before the
        // role itself goes.
        if (!await _pool.RoleExistsOnAsync(sourceDatabaseId, roleName, ct))
            return;
        var quotedRole = TenantNaming.Quote(roleName);
        await _pool.ExecuteOnAsync(sourceDatabaseId, $"DROP OWNED BY {quotedRole};", ct);
        await _pool.ExecuteOnAsync(sourceDatabaseId, $"DROP ROLE IF EXISTS {quotedRole};", ct);
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500] + "…";
    }
}
