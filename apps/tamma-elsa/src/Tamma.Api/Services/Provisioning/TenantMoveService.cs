using System.Globalization;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Grace delay between the drain/evict and <c>pg_dump</c>, in seconds
    /// (default 2). In-flight-write window: requests that cleared the
    /// read-only middleware BEFORE the status flipped to 'draining' may
    /// still be executing writes on already-leased connections — the evict
    /// only prevents NEW leases, it does not cancel commands in flight. The
    /// grace lets those last writes land before the dump snapshots the
    /// schema. Set 0 in unit tests.
    /// </summary>
    public int DrainGraceSeconds { get; set; } = 2;
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
///     step 5 — so a non-zero exit is acceptable ONLY when stderr's
///     "errors ignored on restore: N" summary reports no more than the
///     expected count (exactly the pre-created schema error); a higher N,
///     or a non-zero exit with no parseable summary, aborts.
///     <b>verify</b>: <c>__TenantMigrationsHistory</c> row count in the
///     target schema must equal the source's (fast pre-gate), then EVERY
///     base table under the schema is compared source-vs-target by
///     <c>count(*)</c> — any mismatch aborts (source intact, tenant still
///     'draining').</description></item>
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
/// re-point and completes the tail — assert the schema actually exists on
/// the target, sweep stale copies of the schema off every other pool row
/// (skipping rows that alias the target's physical database), re-run the
/// step-8 verify probe, then activate.</para>
///
/// <para><b>Concurrency:</b> the whole of <see cref="MoveAsync"/> runs
/// under a per-tenant Postgres advisory lock on a dedicated control-plane
/// session (<c>pg_try_advisory_lock(hashtextextended(tenantId, 0))</c>) —
/// a second concurrent move for the same tenant is rejected up front. The
/// lock is released in a finally and dies with the session regardless.</para>
///
/// <para><b>Aliasing guard:</b> two tenant_databases rows that point at
/// the SAME physical (Host, Port, Database) would make a "move" between
/// them dump-and-drop the live schema — validation rejects that before
/// any destructive step, and the resume sweep refuses to drop the schema
/// off any row aliasing the target's physical database. The admin CRUD
/// rejects registering such duplicate rows in the first place.</para>
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
        // ── Step 0: per-tenant advisory lock for the WHOLE move ──────────
        // Two concurrent MoveAsync calls for one tenant would interleave
        // destructive steps (drop/restore/repoint) unpredictably — the
        // second caller is rejected up front. Held on a dedicated CP
        // session; released in the finally (and by session death anyway).
        await using var moveLock = await AcquireMoveLockAsync(tenantId, ct);

        // ── Step 1: validate ─────────────────────────────────────────────
        var plan = await ValidateAsync(tenantId, targetDatabaseId, ct);
        var schema = plan.SchemaName;
        var quotedSchema = TenantNaming.Quote(schema);
        var roleName = TenantNaming.RoleName(tenantId);

        if (plan.IsResume)
        {
            // The step-7 re-point already committed in a prior run — the
            // tenant points at the TARGET. Complete the tail: assert the
            // schema is really on the target, sweep stale schema copies off
            // every other pool row, re-verify the resolver round-trip, then
            // activate.
            _logger.LogInformation(
                "tenant.move.resume tenantId={TenantId} targetDatabaseId={TargetDatabaseId} "
                + "schema={Schema}", tenantId, targetDatabaseId, schema);
            if (!await _pool.SchemaExistsOnAsync(plan.Target.Id, schema, ct))
            {
                // Sweeping now would destroy the only remaining copy of the
                // tenant's data (on whichever row still holds it).
                throw new InvalidOperationException(
                    $"Tenant move resume aborted for '{tenantId}': the tenant points at "
                    + $"target row '{plan.Target.Id}' but schema '{schema}' does not exist "
                    + "there — refusing to sweep other pool rows (that would drop the only "
                    + "remaining copy of the data). Investigate the committed re-point "
                    + "before retrying.");
            }
            await SweepStaleSchemasAsync(tenantId, plan.Target, schema, roleName, ct);
            await VerifyTargetRoundTripAsync(tenantId, ct);
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

        // Owner-only (0700 on Unix) private tmp directory — the dump holds
        // a full copy of the tenant's data and must not be readable by
        // other local users. The directory (and the dump inside) is
        // deleted in the finally regardless of outcome.
        var dumpDir = Directory.CreateTempSubdirectory("tamma-move-");
        var dumpFile = Path.Combine(dumpDir.FullName, $"{schema}.dump");
        try
        {
            // ── Step 2: drain (read-only window opens) ───────────────────
            await SetStatusAsync(tenantId, "draining", ct);
            await _resolver.EvictAsync(tenantId, ct);
            _logger.LogInformation(
                "tenant.move.drain tenantId={TenantId} (writes now 503; reads keep flowing)",
                tenantId);

            // In-flight-write window: requests that cleared the read-only
            // middleware BEFORE the status flip may still be writing on
            // already-leased connections — the evict prevents NEW leases
            // but does not cancel commands in flight. A short grace lets
            // those last writes land before pg_dump snapshots the schema.
            if (_options.DrainGraceSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(_options.DrainGraceSeconds), ct);

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

            // ── Step 6: restore into the target + verify ─────────────────
            var targetInfo = await _pool.GetConnectionInfoAsync(target.Id, ct);
            var restoreResult = await RunPgToolAsync(
                _options.PgRestorePath,
                PgToolArguments.ForPgRestore(targetInfo, dumpFile, roleName),
                targetInfo.Password,
                tenantId, step: "restore",
                // pg_restore exits 1 when it hit ignorable errors — the
                // pre-created schema's "already exists" is guaranteed to
                // trip that. EnsureRestoreSucceeded parses stderr's
                // "errors ignored on restore: N" summary and aborts when N
                // exceeds that one expected error (or when a non-zero exit
                // carries no parseable summary).
                failureIsFatal: false, ct);
            EnsureRestoreSucceeded(restoreResult, tenantId);
            _logger.LogInformation(
                "tenant.move.restore tenantId={TenantId} schema={Schema} "
                + "targetDatabaseId={TargetDatabaseId}", tenantId, schema, target.Id);

            // Fast pre-gate (one table), then the full per-table row-count
            // comparison. NOTE: a failed restore CAN produce a matching
            // history count (e.g. data-load errors after the history table
            // restored) — the per-table comparison is the authoritative
            // gate, the stderr summary the early-warning.
            await VerifyHistoryAsync(tenantId, source.Id, target.Id, quotedSchema, ct);
            await VerifyRowCountsAsync(
                tenantId, source.Id, target.Id, schema, quotedSchema, ct);

            // ── Step 7: re-point envelope + bookkeeping (ONE SaveChanges) ─
            await RepointAsync(
                tenantId, source.Id, target.Id, targetPlacement, sameCluster,
                freshPassword, ct);

            // ── Step 8: evict + verify through the production factory ────
            await VerifyTargetRoundTripAsync(tenantId, ct);

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
            // Tmp hygiene regardless of outcome — the dump holds a full
            // copy of the tenant's data; the whole 0700 directory goes.
            try
            {
                dumpDir.Delete(recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "tenant.move.tmp_cleanup_failed tenantId={TenantId} dir={Dir}",
                    tenantId, dumpDir.FullName);
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

        // Same-physical-database aliasing guard: two pool rows can point at
        // ONE physical (Host, Port, Database) — a "move" between them would
        // dump the schema, drop it "on the target" (= the live copy), and
        // then drop "the source" (= the restored copy), losing everything.
        // Reject BEFORE any destructive step.
        if (string.Equals(source.Host, target.Host, StringComparison.OrdinalIgnoreCase)
            && source.Port == target.Port)
        {
            var sourceDbName = await _pool.GetDatabaseNameAsync(source.Id, ct);
            var targetDbName = await _pool.GetDatabaseNameAsync(target.Id, ct);
            if (string.Equals(sourceDbName, targetDbName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Tenant move rejected for '{tenantId}': source row '{source.Id}' and "
                    + $"target row '{targetDatabaseId}' alias the same physical database "
                    + $"({source.Host}:{source.Port}/{sourceDbName}) — a move between them "
                    + "would drop the live schema. Remove the duplicate tenant_databases "
                    + "row instead.");
            }
        }

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
    private async Task<ProcessRunResult> RunPgToolAsync(
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

        return result;
    }

    /// <summary>
    /// pg_restore's "errors ignored on restore: N" stderr summary — emitted
    /// whenever the tool finished but skipped over errors (exit code 1).
    /// </summary>
    private static readonly Regex IgnoredErrorsSummary = new(
        @"errors ignored on restore:\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Exactly ONE ignorable pg_restore error is expected: the step-5
    /// pre-created schema's "schema t_&lt;hex&gt; already exists" (observed
    /// count in the live end-to-end move: 1). Anything beyond that means
    /// real objects or data failed to restore.
    /// </summary>
    private const int ExpectedIgnorableRestoreErrors = 1;

    /// <summary>
    /// Restore-verification hardening: exit 0 → clean; exit != 0 with the
    /// stderr summary reporting at most <see cref="ExpectedIgnorableRestoreErrors"/>
    /// ignored errors → acceptable; a higher count, or no parseable summary,
    /// aborts the move (source schema intact, tenant stays 'draining').
    /// </summary>
    private void EnsureRestoreSucceeded(ProcessRunResult result, Guid tenantId)
    {
        if (result.ExitCode == 0)
            return;

        var match = IgnoredErrorsSummary.Match(result.StdErr ?? string.Empty);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"pg_restore exited {result.ExitCode} during the tenant move for "
                + $"'{tenantId}' and stderr carries no 'errors ignored on restore' summary "
                + "— treating as a hard failure. Source schema is intact; tenant remains "
                + "'draining'. See logs for stderr.");
        }

        var ignored = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (ignored > ExpectedIgnorableRestoreErrors)
        {
            throw new InvalidOperationException(
                $"pg_restore ignored {ignored} errors during the tenant move for "
                + $"'{tenantId}' — only {ExpectedIgnorableRestoreErrors} is expected (the "
                + "step-5 pre-created schema's \"already exists\"). The restore is not "
                + "trustworthy. Source schema is intact; tenant remains 'draining'. See "
                + "logs for stderr.");
        }

        _logger.LogInformation(
            "tenant.move.restore ignorable_errors tenantId={TenantId} ignored={Ignored} "
            + "expected={Expected}", tenantId, ignored, ExpectedIgnorableRestoreErrors);
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

    /// <summary>
    /// Per-table row-count comparison (restore-verification hardening):
    /// every base table under the schema on the SOURCE row must hold the
    /// same <c>count(*)</c> on the TARGET. The tenant is draining
    /// (mutating verbs 503) and evicted, so source counts are stable for
    /// the comparison window. Any mismatch aborts the move with the
    /// offending tables listed — source intact, tenant stays 'draining'.
    /// </summary>
    private async Task VerifyRowCountsAsync(
        Guid tenantId, Guid sourceDatabaseId, Guid targetDatabaseId,
        string schema, string quotedSchema, CancellationToken ct)
    {
        // schema is the generated t_<hex> (TenantNaming) — safe to inline
        // inside a single-quoted literal.
        var tablesSql =
            "SELECT string_agg(table_name, ',' ORDER BY table_name) "
            + "FROM information_schema.tables "
            + $"WHERE table_schema = '{schema}' AND table_type = 'BASE TABLE';";
        var rawTables = Convert.ToString(
            await _pool.ExecuteScalarOnAsync(sourceDatabaseId, tablesSql, ct));
        if (string.IsNullOrWhiteSpace(rawTables))
        {
            throw new InvalidOperationException(
                $"Tenant move aborted for '{tenantId}': information_schema lists no base "
                + $"tables under schema '{schema}' on the source row — cannot verify the "
                + "restore. Source schema is intact; tenant remains 'draining'.");
        }

        var tables = rawTables.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var mismatches = new List<string>();
        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            var countSql =
                $"SELECT count(*) FROM {quotedSchema}.{TenantNaming.Quote(table)};";
            var sourceCount = Convert.ToInt64(
                await _pool.ExecuteScalarOnAsync(sourceDatabaseId, countSql, ct) ?? -1L);
            var targetCount = Convert.ToInt64(
                await _pool.ExecuteScalarOnAsync(targetDatabaseId, countSql, ct) ?? -1L);
            if (sourceCount < 0 || sourceCount != targetCount)
                mismatches.Add($"{table} (source={sourceCount}, target={targetCount})");
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"Tenant move aborted for '{tenantId}': per-table row counts differ after "
                + $"the restore — {string.Join("; ", mismatches)}. Source schema is intact; "
                + "tenant remains 'draining'. Re-run the move or reset the status to "
                + "'active'.");
        }
        _logger.LogInformation(
            "tenant.move.verify_rows tenantId={TenantId} tables={Tables} (all counts match)",
            tenantId, tables.Length);
    }

    /// <summary>
    /// Step-8 verify probe (also re-run by the resume tail): evict the
    /// tenant's pooled connections, then open a real TenantDbContext
    /// through the production factory — a trivial query proves the
    /// re-pointed envelope decrypts, connects, and lands in the restored
    /// schema (mirrors the provisioning verify).
    /// </summary>
    private async Task VerifyTargetRoundTripAsync(Guid tenantId, CancellationToken ct)
    {
        await _resolver.EvictAsync(tenantId, ct);
        await using (var tenantCtx = await _tenantDbFactory.CreateAsync(tenantId, ct))
        {
            _ = await tenantCtx.AgentConfigs.AsNoTracking()
                .FirstOrDefaultAsync(ct);
        }
        _logger.LogInformation(
            "tenant.move.verify_target tenantId={TenantId} (resolver round-trip ok)",
            tenantId);
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
        var targetDbName = await _pool.GetDatabaseNameAsync(target.Id, ct);
        List<TenantDatabase> rows;
        await using (var db = await _cpFactory.CreateDbContextAsync(ct))
        {
            rows = await db.TenantDatabases.Where(d => d.Id != target.Id).ToListAsync(ct);
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            // Aliasing guard: a row whose (Host, Port, Database) equals the
            // target's points at the SAME physical database the tenant now
            // lives on — "sweeping" it would drop the live schema.
            if (string.Equals(row.Host, target.Host, StringComparison.OrdinalIgnoreCase)
                && row.Port == target.Port
                && string.Equals(
                    await _pool.GetDatabaseNameAsync(row.Id, ct), targetDbName,
                    StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "tenant.move.sweep_skip tenantId={TenantId} databaseId={DatabaseId} "
                    + "aliases the target's physical database ({Host}:{Port}/{Db}) — not "
                    + "swept; remove the duplicate tenant_databases row.",
                    tenantId, row.Id, row.Host, row.Port, targetDbName);
                continue;
            }

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

    // ── concurrency: per-tenant advisory lock ──────────────────────────────

    /// <summary>
    /// Take <c>pg_try_advisory_lock(hashtextextended(tenantId, 0))</c> on a
    /// DEDICATED control-plane session held for the whole move (the session
    /// lives inside the returned handle; disposing it unlocks — and the
    /// lock dies with the session regardless). Not acquired → a move for
    /// this tenant is already running somewhere → throw. Returns null when
    /// the control plane is non-relational (EF InMemory unit suites — no
    /// session to lock on; production CP is always Postgres).
    /// </summary>
    private async Task<MoveAdvisoryLock?> AcquireMoveLockAsync(
        Guid tenantId, CancellationToken ct)
    {
        var db = await _cpFactory.CreateDbContextAsync(ct);
        try
        {
            if (!db.Database.IsRelational())
            {
                _logger.LogDebug(
                    "tenant.move.lock skipped_non_relational tenantId={TenantId}", tenantId);
                await db.DisposeAsync();
                return null;
            }

            await db.Database.OpenConnectionAsync(ct);
            var conn = db.Database.GetDbConnection();
            bool acquired;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT pg_try_advisory_lock(hashtextextended(@tid, 0));";
                var p = cmd.CreateParameter();
                p.ParameterName = "tid";
                p.Value = tenantId.ToString("D");
                cmd.Parameters.Add(p);
                acquired = await cmd.ExecuteScalarAsync(ct) is true;
            }

            if (!acquired)
            {
                throw new InvalidOperationException(
                    $"A move for tenant '{tenantId}' is already in progress (the per-tenant "
                    + "control-plane advisory lock is held by another session) — wait for "
                    + "it to finish or fail before retrying.");
            }
            _logger.LogInformation(
                "tenant.move.lock acquired tenantId={TenantId}", tenantId);
            return new MoveAdvisoryLock(db, tenantId, _logger);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Holds the dedicated CP session carrying the per-tenant advisory
    /// lock. Dispose unlocks explicitly (best-effort — the session-scoped
    /// lock is released by Postgres when the connection closes anyway) and
    /// disposes the session.
    /// </summary>
    private sealed class MoveAdvisoryLock : IAsyncDisposable
    {
        private readonly ControlPlaneDbContext _db;
        private readonly Guid _tenantId;
        private readonly ILogger _logger;

        public MoveAdvisoryLock(ControlPlaneDbContext db, Guid tenantId, ILogger logger)
        {
            _db = db;
            _tenantId = tenantId;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                var conn = _db.Database.GetDbConnection();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT pg_advisory_unlock(hashtextextended(@tid, 0));";
                var p = cmd.CreateParameter();
                p.ParameterName = "tid";
                p.Value = _tenantId.ToString("D");
                cmd.Parameters.Add(p);
                await cmd.ExecuteScalarAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "tenant.move.lock_release_failed tenantId={TenantId} (the lock "
                    + "dies with the session regardless)", _tenantId);
            }
            finally
            {
                await _db.DisposeAsync();
            }
        }
    }
}
