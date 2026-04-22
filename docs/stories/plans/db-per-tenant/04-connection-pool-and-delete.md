# 04 — Runtime Connection Management & Tenant Deletion

> **Scope.** This document covers the two load-bearing operational concerns
> of the database-per-tenant architecture:
>
> 1. How the API resolves, caches, and recycles per-tenant Postgres
>    connections at runtime.
> 2. How a tenant is hard-deleted, including active-session handling,
>    backups, and disaster recovery.
>
> Everything else in the db-per-tenant plan (control-plane split, Elsa
> topology, provisioning workflow) **assumes these two flows work**.
>
> **Design document only.** No code. Pseudocode is illustrative.
>
> **Related documents (sibling agents):**
> - `01-control-plane-split.md` — control-plane schema (tenants table shape)
> - `02-elsa-topology.md` — global vs per-tenant Elsa instances
> - `03-provisioning-workflow.md` — tenant create flow (owns CREATE DATABASE)
>
> This document owns **DROP DATABASE** and **runtime connection pools**.

---

## 1. Scale targets and Postgres limits

### 1.1 Growth curve

| Horizon     | Tenants | Postgres instances | Avg active tenants/instance |
| ----------- | ------- | ------------------ | --------------------------- |
| Launch      | 100     | 1                  | ~30 (long tail is idle)     |
| 6 months    | 1,000   | 1–2                | ~300                        |
| 12 months   | 10,000  | 5–10 (sharded)     | ~1,500                      |
| 24 months   | 50,000  | 20+ (sharded)      | ~1,500                      |

"Active" = tenants whose users touched the API in the last 5 minutes
(our idle-pool lifetime). The long tail — tenants who log in once a
week — does not hold open connection pools.

### 1.2 Bottlenecks per Postgres instance

Postgres limits we design against, ordered by **when they bite**:

| Limit                   | Default | Safe ceiling | Hits us at...                                  |
| ----------------------- | ------- | ------------ | ---------------------------------------------- |
| `max_connections`       | 100     | ~800–1,000\* | **~200 active tenants** (at 5 conn/tenant)     |
| 63-byte identifier cap  | 63      | 63           | never (our names are 45 chars, see §5)         |
| `shared_buffers` memory | 128MB   | 25% of RAM   | **~5,000 DBs** on an 8GB box                   |
| Per-DB backend memory   | —       | —            | **~2,000 DBs** on an 8GB box (~4 MB / idle DB) |
| WAL / checkpointing     | —       | —            | load-dependent; shows up at ~10k write-active DBs |
| Autovacuum worker slots | 3       | 10           | **~500 heavily-written DBs**                   |
| `pg_stat_*` table scans | —       | —            | **~3,000 DBs** — admin queries get slow        |
| Backup (pg_dump-all)    | —       | —            | **~1,000 DBs** — wall-clock exceeds RPO window |

\* Safe ceiling assumes PgBouncer transaction-mode pooling is **not** in
front (we're holding `NpgsqlDataSource` pools in-process; see §1.3).
Without PgBouncer, raising `max_connections` past ~1,000 hits CPU scheduling
overhead and `per_conn_memory × max_connections` becomes significant
(`work_mem`, `temp_buffers`).

### 1.3 When each ceiling forces action

**Ceiling 1: `max_connections` — hits first, at ~200 active tenants.**

- Action: **cap the in-process pool (§2.4)** — don't let each tenant hold
  5 connections. Default is `MaxPoolSize=5`, but with 200 active tenants
  the in-process cache already evicts (`K = 200`).
- Kick-in trigger: `pg_stat_activity` backend count > 70% of
  `max_connections` for 5 minutes → alert; raise `max_connections` or
  reduce `MaxCachedTenants`.

**Ceiling 2: per-DB memory — hits at ~1,000–2,000 DBs per instance.**

- Action: **shard to additional Postgres instances.** `tenants` table
  gains `PostgresInstanceId` column. Resolver looks up the instance first,
  then builds the connection string.
- Kick-in trigger: instance RAM > 70% sustained, or `pg_database` count
  > 1,000 on a single cluster.

**Ceiling 3: admin-query pain (vacuum, pg_dump, pg_stat_activity scans).**

- Action: same as Ceiling 2 — shard. Also consider scheduling
  maintenance windows per-shard.
- Kick-in trigger: backup wall-clock > RPO window, or autovacuum
  lagging (pg_stat_user_tables shows old `last_autovacuum`).

**Ceiling 4: shared_buffers — rarely binding.**

- Postgres reuses shared_buffers across all DBs in the cluster, so per-DB
  memory cost is dominated by per-process state (Ceiling 2), not shared
  buffers. Listed for completeness.

### 1.4 Key design decision

**We shard Postgres clusters, not pool sizes.** That is: when we outgrow
one cluster, we add a second cluster (with its own `max_connections`,
autovacuum, backups) rather than cramming 10k DBs into one box.

**Recommendation: target ≤ 500 tenants per Postgres instance in prod.**
That keeps us comfortably below every ceiling and leaves headroom for
traffic spikes.

---

## 2. Connection resolver contract

### 2.1 Public interface

```csharp
public interface ITenantConnectionResolver
{
    /// <summary>
    /// Resolves a ready-to-use NpgsqlDataSource for the given tenant.
    /// Pools are cached in-process; idle pools are evicted after
    /// TenantConnectionOptions.IdleLifetime.
    /// </summary>
    /// <exception cref="TenantNotFoundException">
    /// Tenant does not exist in the control plane.
    /// </exception>
    /// <exception cref="TenantNotActiveException">
    /// Tenant Status is Deleting (grace expired) or Deleted or Suspended.
    /// </exception>
    NpgsqlDataSource DataSourceFor(Guid tenantId);

    /// <summary>
    /// Same as DataSourceFor but for the tenant's Elsa database.
    /// </summary>
    NpgsqlDataSource ElsaDataSourceFor(Guid tenantId);

    /// <summary>
    /// Force-close the pool for a tenant. Used during tenant deletion
    /// so the subsequent DROP DATABASE succeeds.
    /// Removes both the tenant DB pool and the tenant Elsa DB pool.
    /// </summary>
    ValueTask EvictAsync(Guid tenantId);

    /// <summary>
    /// Diagnostic hook. Returns current cache size, hit rate,
    /// per-tenant last-access times. Used by /api/admin/diagnostics.
    /// </summary>
    ResolverStats GetStats();
}
```

### 2.2 Implementation shape

```
TenantConnectionResolver
├── ConcurrentDictionary<Guid, Lazy<NpgsqlDataSource>> _pools
├── MemoryCache _lru                 — entry key = tenantId, size budget = K
├── IMemoryCache.PostEvictionCallback — on eviction, Dispose() the data source
├── IOptionsMonitor<TenantConnectionOptions>
├── ITenantRecordLookup               — reads tenants row from control plane
├── IConnectionStringDecryptor        — §4
└── ILogger<TenantConnectionResolver>
```

**Lookup flow (read path):**

1. `_pools.TryGetValue(tenantId, ...)` — hot path, no lock, no await.
2. Hit: `_lru.Set(tenantId, marker, slidingExpiration=IdleLifetime)` to
   refresh LRU; return `.Value` of the `Lazy<>`.
3. Miss: acquire `SemaphoreSlim` keyed per-tenant (avoids thundering
   herd on cold start), then:
   a. Lookup `tenants` row via `ITenantRecordLookup`.
   b. Validate `Status ∈ {Active}` → throw `TenantNotActiveException`
      otherwise.
   c. Decrypt connection string (§4).
   d. Build `NpgsqlDataSourceBuilder` with per-tenant settings (§2.4).
   e. Build DataSource, insert into `_pools` and `_lru`.
   f. Release semaphore.

**The `Lazy<>` wrapper** serialises concurrent misses for the same tenant
so only one thread pays the lookup-and-build cost.

### 2.3 Eviction (reactive + proactive)

Three eviction paths:

| Trigger                      | Path                                           |
| ---------------------------- | ---------------------------------------------- |
| Idle > `IdleLifetime` (5m)   | `MemoryCache` sliding expiration → callback    |
| `MaxCachedTenants` exceeded  | LRU eviction via `MemoryCache` size budget     |
| Tenant deletion (§6, §8)     | Explicit `EvictAsync(tenantId)` + signal (§2.5) |

The `PostEvictionCallback` is the **single point** where `NpgsqlDataSource.DisposeAsync()`
runs. Anywhere we remove from `_pools` without going through the
callback would leak sockets.

### 2.4 Npgsql configuration per tenant pool

| Setting                  | Value       | Rationale                                      |
| ------------------------ | ----------- | ---------------------------------------------- |
| `MinPoolSize`            | 0           | Idle tenants hold zero backends.               |
| `MaxPoolSize`            | 5           | 5 concurrent requests / tenant typical; with   |
|                          |             | 200 cached tenants → peak 1,000 connections.   |
| `ConnectionIdleLifetime` | 60 s        | Npgsql returns idle connections to the server. |
| `ConnectionLifetime`     | 0 (none)    | Let server close stale conns; re-auth on reuse. |
| `Timeout` (connect)      | 5 s         | Fail fast; tenant DB is on same VPC.           |
| `CommandTimeout`         | 30 s        | Most requests finish in < 1s; 30s = runaway.   |
| `ApplicationName`        | `tamma-api;tenant=<guid>` | Shows up in `pg_stat_activity`; essential for forensics and delete (§6). |
| `KeepAlive`              | 30 s        | Detect stale server-side sockets.              |

### 2.5 Configuration schema

```yaml
# appsettings.json → TenantConnection section
TenantConnection:
  MaxCachedTenants: 200           # K — see §1 and §2.3
  IdleLifetime: 00:05:00          # 5 minutes sliding
  MaxPoolSizePerTenant: 5
  MinPoolSizePerTenant: 0
  CommandTimeout: 00:00:30
  ConnectTimeout: 00:00:05
  DecryptorKeyEnvVar: TAMMA_TENANT_KEK
  # Deletion signal channel: in-process event bus that resolver listens on
  EvictionSignalTopic: "tenant.deleted"
```

### 2.6 Observability

Emit these metrics (Prometheus / OpenTelemetry):

```
tamma_tenant_pool_hits_total{tenant_id="..."}
tamma_tenant_pool_misses_total{tenant_id="..."}
tamma_tenant_pool_evictions_total{reason="idle|lru|explicit"}
tamma_tenant_pool_size                       # current cached tenants
tamma_tenant_pool_resolve_seconds_bucket    # p50, p95, p99 for lookup
tamma_tenant_pool_active_connections{tenant_id="..."} # from Npgsql stats
```

Log (structured):

```
INFO  tenant.pool.created       tenant_id=... duration_ms=12
WARN  tenant.pool.evicted       tenant_id=... reason=lru idle_seconds=...
ERROR tenant.pool.build_failed  tenant_id=... error=...
```

---

## 3. DbContext registration

### 3.1 Four DbContexts, three lifetimes

| DbContext              | Lifetime | Connection source                                  |
| ---------------------- | -------- | -------------------------------------------------- |
| `ControlPlaneDbContext` | Scoped   | Static `ConnectionStrings:ControlPlane`            |
| `GlobalElsaDbContext`  | Scoped   | Static `ConnectionStrings:GlobalElsa`              |
| `TenantDbContext`      | Scoped   | `ITenantConnectionResolver.DataSourceFor(tenantId)` |
| `TenantElsaDbContext`  | Scoped   | `ITenantConnectionResolver.ElsaDataSourceFor(tid)` |

**Naming mapping to sibling docs:** `ControlPlaneDbContext` replaces
the current single `TammaDbContext` for non-tenant data (users, tenants,
memberships, platform events). The per-tenant mentorship/workflow data
migrates into `TenantDbContext`.

### 3.2 Registration pseudocode

```csharp
// ── Control plane: static connection string ──────────────────────
services.AddDbContext<ControlPlaneDbContext>((sp, options) =>
{
    var cs = sp.GetRequiredService<IConfiguration>()
        .GetConnectionString("ControlPlane")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:ControlPlane is required");
    options.UseNpgsql(cs, npgsql => npgsql.MigrationsHistoryTable(
        "__ef_migrations_history", "control_plane"));
});

// ── Global Elsa: static connection string ────────────────────────
services.AddDbContext<GlobalElsaDbContext>((sp, options) =>
{
    var cs = sp.GetRequiredService<IConfiguration>()
        .GetConnectionString("GlobalElsa")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:GlobalElsa is required");
    options.UseNpgsql(cs);
});

// ── Resolver registration ────────────────────────────────────────
services.AddSingleton<ITenantConnectionResolver, TenantConnectionResolver>();
services.Configure<TenantConnectionOptions>(
    configuration.GetSection("TenantConnection"));

// ── Tenant DbContext: scoped, per-request resolution ─────────────
services.AddDbContext<TenantDbContext>((sp, options) =>
{
    var tenantCtx = sp.GetRequiredService<ITenantContext>();
    if (!tenantCtx.IsResolved)
    {
        // Fail-fast in DI: caller must be on a tenant-scoped route.
        throw new TenantNotResolvedException(
            "TenantDbContext requested without a tenant context. " +
            "Is this a pre-tenant endpoint (login/register/webhook)?");
    }

    var resolver = sp.GetRequiredService<ITenantConnectionResolver>();
    var dataSource = resolver.DataSourceFor(tenantCtx.TenantId);
    options.UseNpgsql(dataSource);
});

services.AddDbContext<TenantElsaDbContext>((sp, options) =>
{
    var tenantCtx = sp.GetRequiredService<ITenantContext>();
    if (!tenantCtx.IsResolved)
        throw new TenantNotResolvedException(...);

    var resolver = sp.GetRequiredService<ITenantConnectionResolver>();
    options.UseNpgsql(resolver.ElsaDataSourceFor(tenantCtx.TenantId));
});
```

### 3.3 Fail-fast for pre-tenant endpoints

**Problem:** routes like `POST /api/auth/login`, `POST /api/tenants`,
`POST /webhooks/github` execute **before** a tenant context exists. If
a handler on those routes accidentally takes `TenantDbContext` via DI, we
must fail at DI resolution time, not with a mysterious `null` later.

**Mechanism:**

- `ITenantContext.IsResolved` is `false` on pre-tenant requests.
- The `AddDbContext<TenantDbContext>` factory **throws**
  `TenantNotResolvedException` inside DI resolution.
- Developers see the exception stack trace pointing at their handler.

**Enforcement in CI:**

- Unit test: resolve every controller via `IServiceProvider`, assert
  that controllers in the pre-tenant allowlist (`AuthController`,
  `TenantsController.Create`, `WebhooksController`) don't transitively
  depend on `TenantDbContext`.

### 3.4 Super-user (global/platform admin) DbContext access

Per user directive: "super user is super, global."

A **platform admin** is a user with `users.IsPlatformAdmin = true`
(in control plane). Platform admins can:

- Read any tenant's data for support (via explicit "impersonate" flow).
- Trigger tenant deletion (§6).
- Access cross-tenant reports.

**Access pattern:**

- Platform admins get `ControlPlaneDbContext` always.
- When they need to read a specific tenant's data, they set
  `ITenantContext.TenantId` explicitly via an **impersonation
  middleware** (behind `[Authorize(Policy = "PlatformAdmin")]`).
- Every impersonation emits a `PLATFORM_ADMIN.IMPERSONATED.SUCCESS`
  event to `platform_events` with the admin's user_id and target
  tenant_id.
- Impersonation has a **15-minute TTL**; after that the resolver
  re-authorises.

**Critical:** platform admins do **not** bypass connection pooling —
they go through the same `ITenantConnectionResolver`. They simply can
resolve **any** tenant, not just their own.

---

## 4. Connection-string encryption

User directive: "conn string you figure out."

### 4.1 Approach comparison

#### Approach A — Stored ciphertext in control plane (RECOMMENDED)

Every `tenants` row carries `EncryptedConnectionString bytea`, produced
by AES-256-GCM using a Key Encryption Key (KEK) from the environment:

```
tenants.EncryptedConnectionString  = AES-GCM(plaintext, KEK, nonce) || nonce || tag
```

- **KEK env var:** `TAMMA_TENANT_KEK` — 32-byte base64. Loaded at
  startup, kept in a `ProtectedMemory` region.
- **Plaintext content:**
  ```
  Host=<pg-host>;Port=<port>;Database=tamma_tenant_<guid32>;
    Username=tamma_tenant_<guid32>;Password=<32-byte-random-base64>
  ```
  The password is generated at provisioning and never written anywhere
  else — the only canonical copy is this ciphertext.
- **Key rotation (two-key overlap):**
  1. Deploy new `TAMMA_TENANT_KEK_NEXT` alongside existing `TAMMA_TENANT_KEK`.
  2. Decryptor tries both (current first, then next) on read — either key
     works.
  3. Background job re-encrypts every row with the new key and rewrites
     the ciphertext in-place.
  4. Once job completes, swap: `TAMMA_TENANT_KEK_NEXT` → `TAMMA_TENANT_KEK`,
     remove old KEK from env.
  - Rotation is **O(tenants)** wall-clock (background, non-blocking).
  - Each row write is a small transactional update on control plane.

**Pros:** self-hosted, no external dependencies, fast
(AES-GCM is hardware-accelerated: ~1 GB/s on modern x86), works today.

**Cons:** the KEK is on the API host's env; a host compromise compromises
all tenants. Mitigation: KEK is never logged, Docker secrets (not env),
rotate on any suspected compromise.

#### Approach B — Deterministic derivation

```
password = HMAC-SHA256(master_secret, "tenant:" || tenant_id)
username = "tamma_tenant_" + hex32(tenant_id)
```

- No ciphertext storage — control plane only stores `tenant_id`.
- Connection string is reconstructed on demand from the tenant_id.

**Pros:** no storage, no decrypt-at-read, rotation-free for usernames.

**Cons:**
- Rotating `master_secret` requires **rewriting the Postgres role
  password for every tenant** simultaneously (or running two secrets in
  overlap with role pre-provisioning — ugly).
- Loses operational flexibility: you can't rotate a single tenant's
  credentials after a leak.
- No way to provision tenants on "external" Postgres instances (where
  the password was set by the DBA, not by us).
- No way to store per-tenant connection parameters (PgBouncer port,
  read-replica host) without re-introducing storage.

**Verdict:** attractive-looking but operationally painful.

#### Approach C — KMS-backed

- Ciphertext stored in control plane, KEK in AWS KMS / HashiCorp Vault.
- Every decrypt is a KMS API call (or we cache the DEK for N minutes).

**Pros:** KEK never leaves the KMS boundary. HSM-backed in enterprise
tiers. Audit trail of every decrypt.

**Cons:**
- KMS latency (10–50 ms) on cold-path pool builds — mitigated by
  in-process pool cache (decrypt happens once per 5-min window per
  tenant, not per request).
- Adds an external dependency; KMS outage = API outage for cold tenants.
- Pricing scales with decrypt volume.

### 4.2 Recommendation

**Phase 1 (launch → 1,000 tenants): Approach A.** Self-hosted, no
external dependencies, matches Tamma's self-maintenance ethos. KEK rotation
is a well-understood operation.

**Phase 2 (→ multi-region, enterprise tier): Approach C.** When Tamma
offers on-prem/BYO-KMS or runs across regions, move the KEK to a KMS
behind an abstraction — the `IConnectionStringDecryptor` interface is
the seam.

```csharp
public interface IConnectionStringDecryptor
{
    string Decrypt(byte[] ciphertext);   // interface seam for A → C
}
```

**Approach B is rejected.**

### 4.3 Storage schema (control plane)

```sql
-- owned by sibling agent 1 (control-plane split); shown here for reference.
ALTER TABLE tenants ADD COLUMN
  encrypted_connection_string bytea NOT NULL;

ALTER TABLE tenants ADD COLUMN
  encrypted_elsa_connection_string bytea NOT NULL;

-- Key version for rotation support: nullable during first deploy,
-- required after backfill.
ALTER TABLE tenants ADD COLUMN
  kek_version smallint NOT NULL DEFAULT 1;
```

---

## 5. Naming scheme

User directive: "naming make sure no collision."

### 5.1 Canonical DB / role names

For a tenant with `tenants.Id = <uuid>`:

| Resource          | Name                                   | Length |
| ----------------- | -------------------------------------- | ------ |
| Tenant DB         | `tamma_tenant_<guid32hex>`             | 45     |
| Tenant Elsa DB    | `tamma_tenant_<guid32hex>_elsa`        | 50     |
| Tenant role       | `tamma_tenant_<guid32hex>`             | 45     |
| Backup archive    | `<tenant-id-hyphens>/<iso-ts>.sql.gz`  | n/a    |

Where `<guid32hex>` is the **hyphen-stripped** 32-character hex
representation of the UUID (e.g. `abc123def4567890abc123def4567890`).

**Length check:**
- `tamma_tenant_` prefix = 13 chars
- 32-char guid hex = 32 chars
- `_elsa` suffix = 5 chars
- Total worst case: 13 + 32 + 5 = **50 chars ≤ 63** ✓

### 5.2 Why hex32, not base36 or base64?

- **Hex:** `[0-9a-f]`, case-insensitive (Postgres folds to lowercase),
  ASCII-safe, deterministic from UUID.
- **Base36:** would fit in 25 chars, but Postgres identifiers are
  case-insensitive unless quoted, so we'd lose 10 bits of entropy.
- **Base64:** has `+`, `/`, `=` — requires quoting, ugly in `psql`.

Hex is the least-surprising choice and leaves plenty of headroom.

### 5.3 Collision analysis

UUIDs are 122 bits of randomness (v4) or 74 bits + timestamp (v7).
Collision probability at 10k tenants is ~10⁻²⁸. At 10M tenants (our
ceiling), still ~10⁻²². **Not a concern.**

What *is* a concern:

- **Same prefix used by existing databases in the cluster.**
  Mitigation: provisioning (agent 3) does a `SELECT 1 FROM pg_database
  WHERE datname = ?` pre-flight check and retries with a fresh UUID on
  conflict. (Only possible if humans manually created a DB with the
  same name — which they shouldn't.)
- **Reserved names (pg_*, postgres, template0, template1).**
  Mitigation: our prefix `tamma_tenant_` trivially avoids all reserved
  namespaces.

### 5.4 User-visible slugs (separate concern)

Slugs like `my-startup` appear in URLs (`app.tamma.dev/my-startup/...`)
and are **entirely decoupled** from DB names.

- `tenants.Slug` is a column, unique, regex-constrained
  (`^[a-z0-9][a-z0-9-]{1,62}$`).
- Slug collision detection happens at provisioning time, not at DB-name
  time.
- A tenant renaming their slug (`my-startup` → `mystartup-inc`) does
  **not** rename the underlying DB — the mapping is via `tenants.Id`.

### 5.5 Multi-instance (sharded) naming

When we shard (§1.3), the tuple `(PostgresInstanceId, tenant_id)`
identifies a tenant. DB names remain the same
(`tamma_tenant_<guid32>`) — uniqueness is per-cluster, and the instance
is identified by `tenants.PostgresInstanceId` which the resolver reads
before building the connection string.

---

## 6. Delete flow — hard erasure

User directive: "easier for delete requests, make sure all is isolated."

### 6.1 Entry point

```
DELETE /api/admin/tenants/{id}
  AUTH: [Authorize(Policy = "PlatformAdmin")]
        OR [Authorize(Policy = "TenantOwner")] with body {"confirm": "<slug>"}
  BODY: { "confirm": "<tenant-slug>", "reason": "user-requested" }
  → 202 Accepted
    {
      "deletionId": "<uuid>",
      "status": "pending",
      "graceExpiresAt": "2026-04-17T14:23:45.000Z",
      "expectedCompletionAt": "2026-04-17T14:28:45.000Z",
      "cancelUrl": "/api/admin/tenants/<id>/cancel"
    }
```

The request is idempotent on `(tenantId, deletionId)`: retries with the
same `deletionId` return the same 202. A concurrent delete attempt on a
tenant in `Status = deleting` returns **409 Conflict** with the existing
deletionId.

### 6.2 State machine (tenant Status field)

```
active  ──delete request──▶  deleting  ──cancel──▶  active
                                 │
                                 │  grace window
                                 │  elapsed
                                 ▼
                             dropping  ──step fails,
                                 │      retryable──▶ dropping
                                 │
                                 ▼
                             deleted  (terminal)
```

### 6.3 Full flow

```
1. API handler receives DELETE /api/admin/tenants/{id}
2. Transaction on ControlPlaneDbContext:
   a. Lock tenant row (SELECT ... FOR UPDATE)
   b. Assert Status IN ('active', 'deleting')
      — if 'deleted': return 410 Gone
      — if 'suspended': allow (suspended → deleting is legal)
   c. UPDATE tenants
        SET Status = 'deleting',
            DeleteRequestedAt = now(),
            DeleteRequestedBy = <user_id>,
            DeleteReason = <body.reason>,
            DeletionId = gen_random_uuid()
      WHERE Id = <id>
   d. INSERT INTO platform_events (
        type='TENANT.DELETE_REQUESTED', tenant_id, user_id,
        data={deletionId, graceSeconds}
      )
   e. Commit
3. Publish message to RabbitMQ queue "tamma.tenant.deletions"
   with routing-key "tenant.delete.requested" (delivery=persistent,
   delay=GraceSeconds via RabbitMQ delayed-message plugin).
4. Return 202 Accepted with deletionId.

5. [Grace window: 5 min default]
   During this window:
     - Tenant API requests continue to resolve normally (§8.1).
     - Cancellation endpoint accepted:
       DELETE /api/admin/tenants/{id}/cancel
       → UPDATE tenants SET Status='active' WHERE Status='deleting'
       → publish "tenant.delete.cancelled" — consumed by worker,
         which acks the delayed message without processing.

6. [Grace expires]
   Worker (DeleteTenantWorkflow on global Elsa) picks up the message.
   Re-reads tenant row; if Status != 'deleting', drop (was cancelled).
   Else:

   Step A: UPDATE tenants SET Status = 'dropping'
   Step B: Resolver.EvictAsync(id)
           — removes in-process pool for tenant DB + tenant Elsa DB.
           — publishes "tenant.deleted" signal on in-process bus.
   Step C: Optional: backup (§9) if Email:DeletionBackup=true.
   Step D: Terminate backends on tenant DBs:
           SELECT pg_terminate_backend(pid)
             FROM pg_stat_activity
            WHERE datname IN ('tamma_tenant_<g>', 'tamma_tenant_<g>_elsa');
   Step E: ALTER DATABASE tamma_tenant_<g> CONNECTION LIMIT 0;
           ALTER DATABASE tamma_tenant_<g>_elsa CONNECTION LIMIT 0;
           — prevents new connections during the drop window.
   Step F: DROP DATABASE tamma_tenant_<g>;
   Step G: DROP DATABASE tamma_tenant_<g>_elsa;
   Step H: DROP ROLE tamma_tenant_<g>;
   Step I: Transaction on ControlPlaneDbContext:
           DELETE FROM tenant_memberships WHERE tenant_id = <id>;
           DELETE FROM user_invites WHERE tenant_id = <id>;
           UPDATE tenants
              SET Status='deleted',
                  DeletedAt=now(),
                  EncryptedConnectionString=NULL,
                  EncryptedElsaConnectionString=NULL;
           INSERT INTO platform_events (
             type='TENANT.DELETED.SUCCESS', tenant_id,
             data={backupPath, durationMs, stepsCompleted}
           );
   Step J: Ack RabbitMQ message.

7. Any step failure → workflow retries from Elsa's durable state
   (see §10 disaster recovery).
```

### 6.4 "Keep tenants row for audit" — GDPR analysis

After deletion:

- `tenants` row retained with: `Id`, `Slug`, `Name`, `OwnerUserId`,
  `Status='deleted'`, `CreatedAt`, `DeletedAt`.
- `EncryptedConnectionString` is **NULL** (nothing to decrypt even if
  KEK leaks).
- Business data is gone (DB dropped).
- Owner user_id and slug are identifiers of the tenant, **not** personal
  data of the owner (the owner's email/name lives in `users`).

**GDPR defensibility:**

- Retention basis: **legitimate interest** — platform audit trail, fraud
  detection, legal holds.
- Retention period: 7 years (aligns with typical tax/compliance law);
  configurable per-deployment.
- Data subject rights: the owner can separately request their own user
  row's erasure (§7); the tenant row's `OwnerUserId` then becomes a
  dangling reference — tolerable since we can't forensic-trace deleted
  tenants to specific humans.

**Alternative (also acceptable):** hard-delete the `tenants` row entirely
and keep the audit trail in `platform_events` (which references
`tenant_id` as a UUID with no join). Choose based on legal-team
preference; implementation difference is minor.

**Decision:** **soft-delete the tenants row, TTL = 7 years.** Matches
compliance expectations and makes post-mortems easier ("what was slug
`acme-co` 18 months ago?").

### 6.5 Why RabbitMQ + Elsa for the workflow?

- **RabbitMQ delayed-message plugin** gives us the grace window for free
  (message not delivered until `now + graceSeconds`).
- **Elsa workflow** gives durable state across API restarts, step-level
  retries, and built-in visibility in Elsa Studio.
- Alternative: inline timer + Hangfire — acceptable, but Elsa is already
  in the stack.

### 6.6 Idempotency per step

Every step must be safely re-runnable:

| Step | Idempotent? | Notes                                                       |
| ---- | ----------- | ----------------------------------------------------------- |
| B    | Yes         | Evicting an already-evicted pool is a no-op.                |
| C    | Yes         | Backup to timestamped path; re-run overwrites same path.    |
| D    | Yes         | Terminate-if-exists; no-op if no backends.                  |
| E    | Yes         | `ALTER DATABASE ... CONNECTION LIMIT 0` is idempotent.      |
| F    | Yes (with `IF EXISTS`) | `DROP DATABASE IF EXISTS`                       |
| G    | Yes         | Same                                                        |
| H    | Yes (with `IF EXISTS`) | `DROP ROLE IF EXISTS`                           |
| I    | Yes         | UPDATE with `WHERE Status='dropping'` — no-op if re-run.    |

---

## 7. User deletion (distinct from tenant deletion)

### 7.1 Leave tenant (straightforward)

```
DELETE /api/tenants/<tenant_id>/memberships/<user_id>
  AUTH: the user themselves, or a tenant admin
  → DELETE FROM tenant_memberships
     WHERE user_id = <user_id> AND tenant_id = <tenant_id>
  → emit USER.LEFT_TENANT event
  → 204 No Content
```

Post-condition: the user can no longer access the tenant, but their
`user_id` **may still appear** in the tenant's `domain_events` JSONB
tags (authorship trail: "who created this event"). This is
**GDPR-defensible under legitimate interest** (security audit trail),
but we should document it in the privacy policy.

### 7.2 Full identity erasure ("right to be forgotten")

Manual, ops-coordinated, not a first-class feature in v1. Runbook:

```
1. User submits erasure request (email or in-app).
2. Ops verifies identity (email round-trip, MFA, etc.).
3. Ops runs erasure workflow (manual CLI, not a public API):
   a. For each tenant_id the user was a member of:
      - Acquire elevated access via impersonation (§3.4).
      - UPDATE domain_events
          SET data = jsonb_set(data, '{user_id}', '"anon:deleted"'),
              tags = tags - 'user_id' || jsonb_build_object(
                'user_id_redacted_at', now())
        WHERE data->>'user_id' = <user_id>;
      - Same treatment for any per-tenant table that references
        user_id (authored_by, assigned_to, etc.) — these are
        discovered via schema inspection; anonymisation rather than
        delete to preserve referential integrity.
   b. DELETE FROM tenant_memberships WHERE user_id = <user_id>;
      DELETE FROM user_invites WHERE email = <user_email>;
   c. DELETE FROM users WHERE id = <user_id>;
   d. INSERT INTO platform_events (
        type='USER.ERASED.SUCCESS', user_id=<hashed>,
        data={tenantsTouched, runbookVersion, operatorId}
      );
4. Ops confirms completion to user within 30 days (GDPR deadline).
```

**Why manual in v1:**

- The anonymisation step is schema-dependent — we need to know which
  JSONB paths and columns reference user_id per tenant DB.
- Running it automated across 10k tenant DBs requires a fan-out job
  with careful backoff; premature optimisation.
- GDPR allows 30 days; manual ops is fine.

**v2 plan:** promote to a first-class workflow once the domain schema
stabilises and we can enumerate reference paths declaratively.

---

## 8. Handling active sessions during delete

### 8.1 Middleware behaviour by Status

Route the tenant-context middleware (currently
`TenantContextMiddleware`) to read `tenants.Status` and
`tenants.DeleteRequestedAt` alongside the rest of the tenant row:

| Status     | DeleteRequestedAt | Grace expired? | API behaviour                              |
| ---------- | ----------------- | -------------- | ------------------------------------------ |
| active     | NULL              | —              | **Normal** — pass through.                 |
| suspended  | NULL              | —              | **402 Payment Required** — billing block.  |
| deleting   | set               | NO             | **Normal** — allow last-minute cancel.     |
| deleting   | set               | YES            | **503 `error=tenant_deleting`** — RA-H=0*  |
| dropping   | set               | —              | **503 `error=tenant_deleting`** — RA-H=0   |
| deleted    | set               | —              | **410 Gone `error=tenant_deleted`**        |

\* `Retry-After: 0` because the tenant won't come back — clients should
not retry.

### 8.2 In-flight requests at grace expiry

A request that **started during grace** (thus acquired a pool) but is
**still executing at grace expiry** is allowed to finish. The pool is
not evicted until Step B of the delete workflow, which only runs
**after** `grace + buffer` where `buffer = max CommandTimeout = 30s`.

Sequence:

```
t=0:        tenant admin fires DELETE (grace = 300s)
t=0:        middleware starts returning 503 for new requests* after t=300
t=1..300:   existing requests finish, normal behaviour
t=300:      middleware blocks new requests — 503
t=330:      grace + buffer → delete workflow starts, Step B evicts pool
t=335:      all DB connections gone; DROP DATABASE runs
```

\* **Correction:** middleware blocks after `t=grace`, not at `t=0`. During
grace, everything is normal so cancellation works cleanly.

### 8.3 User-experience copy

- **503 `tenant_deleting`:** "This workspace is being deleted and is no
  longer accepting requests. Contact support@tamma.dev if this is
  unexpected."
- **410 `tenant_deleted`:** "This workspace no longer exists."

### 8.4 WebSocket / SSE clients

Long-lived SSE streams (`/api/v1/events/stream`) need explicit handling:

- When the resolver publishes the `tenant.deleted` signal (§2.5, Step
  B), the SSE handler subscribing to that signal **terminates open
  streams** with a final event `{"type": "TENANT_DELETED"}` and closes
  the connection. Clients reconnect and hit the 503/410 path in §8.1.

---

## 9. Backup-before-delete safeguard

### 9.1 Design

Before Step D (pg_terminate_backend):

```
pg_dump --format=custom --compress=9 \
        --host=<pg-host> --port=<port> \
        --dbname=tamma_tenant_<g> \
        --file=/backup/<tenant-id>/<iso-ts>.dump
```

Backup is taken **from the superuser connection**, not the tenant role
(the tenant role might have been suspended at this point).

### 9.2 Storage

- Path: `BACKUP_PATH/<tenant-id-hyphens>/<iso-ts>.dump` +
  `<iso-ts>.metadata.json` (tenant name, slug, deleter, reason,
  deletion_id).
- Retention: 30 days default, configurable via `Backup:RetentionDays`.
- Auto-purge: nightly cron removes backups older than retention.
- Encryption at rest: filesystem-level (LUKS / EBS encryption) — we do
  not double-encrypt the dump file.

### 9.3 Configuration

```yaml
Backup:
  DeletionBackup: true              # default true in prod, false in dev
  Path: /var/tamma/backup
  RetentionDays: 30
  IncludeElsaDb: false              # tenant Elsa DB is workflow state —
                                    # usually not worth backing up; flag
                                    # for legal-hold cases.
```

### 9.4 Restore

Restore is **manual only** and involves:

1. Create a new tenant (new `tenant_id`, new slug).
2. `pg_restore --dbname=tamma_tenant_<new_g> <backup-file>`
3. Manually update the `tenants.OwnerUserId` if a different owner.

The restore is a support/legal tool, not a user-facing "undelete."

---

## 10. Disaster recovery

### 10.1 Failure scenarios

| Failure                                          | Symptoms                                    | Recovery                                |
| ------------------------------------------------ | ------------------------------------------- | --------------------------------------- |
| API crashes mid-DELETE request                   | `Status=deleting` but workflow never started | Elsa picks up queued msg on restart.    |
| Postgres crashes between Step F and G            | Main DB dropped, Elsa DB orphaned           | Workflow resumes Step G (idempotent).   |
| Postgres crashes between Step G and H            | Both DBs dropped, role orphaned             | Workflow resumes Step H (idempotent).   |
| Elsa crashes mid-workflow                        | Durable state = last-committed step         | Elsa engine resumes on restart.         |
| Partial pool eviction (Step B) — resolver crash  | Pool still cached, workflow hung            | Resolver restarts empty; workflow retries Step B. |
| Control plane unreachable (API ↔ control-plane)  | Resolver can't look up tenant               | Cached pools continue serving; cold miss = 503. |

### 10.2 Startup recovery check

On API startup, run this scan (non-blocking, fires a warning):

```sql
SELECT id, slug, delete_requested_at, status
  FROM tenants
 WHERE status IN ('deleting', 'dropping')
   AND delete_requested_at < now() - interval '15 minutes';
```

For each row: log `WARN  tenant.delete.stuck tenant_id=... status=...`
and emit metric `tamma_tenant_delete_stuck_total`. Ops is paged on
non-zero metric. Manual inspection + workflow re-drive from Elsa UI.

### 10.3 Ghost-resource cleanup script

A nightly maintenance job reconciles control plane with Postgres:

```
For each tenant in tenants WHERE status = 'deleted':
  Assert NOT EXISTS in pg_database WHERE datname = 'tamma_tenant_<g>*'
  Assert NOT EXISTS in pg_roles WHERE rolname = 'tamma_tenant_<g>'
  → If assertion fails, emit WARN and manual-fix runbook link.

For each DB in pg_database matching 'tamma_tenant_*':
  Extract guid from name.
  Assert EXISTS in tenants WHERE Id = guid AND status IN ('active', 'suspended', 'deleting', 'dropping').
  → If assertion fails (orphan DB), log ERROR and manual-fix runbook.
```

This catches hand-edits, bug-introduced leaks, and partial-delete
survivors.

### 10.4 Recovery metrics

```
tamma_tenant_delete_stuck_total       # tenants in deleting > 15 min
tamma_tenant_delete_duration_seconds  # p50, p95, p99 of successful deletes
tamma_tenant_delete_retries_total     # step retries
tamma_tenant_orphan_dbs_total         # nightly reconcile output
tamma_tenant_orphan_roles_total
```

Alerts:

- `tamma_tenant_delete_stuck_total > 0` → page immediately.
- `tamma_tenant_orphan_dbs_total > 0` for 2 consecutive reconciles → page.
- `tamma_tenant_delete_duration_seconds{quantile="0.95"} > 120` → warn.

---

## 11. Appendix: Key decisions summary

| # | Decision | Value / rationale |
| - | -------- | ----------------- |
| 1 | **Pool caching** | `ConcurrentDictionary<Guid, Lazy<NpgsqlDataSource>>` + `MemoryCache` LRU |
| 2 | **MaxCachedTenants (K)** | 200 at launch; tune to 500 before sharding |
| 3 | **IdleLifetime** | 5 minutes sliding |
| 4 | **MaxPoolSize per tenant** | 5 connections |
| 5 | **Postgres tenants per cluster** | target ≤ 500; hard cap ~800 via `max_connections` |
| 6 | **Sharding trigger** | `pg_database` count > 1,000 OR instance RAM > 70% |
| 7 | **Connection string storage** | AES-256-GCM ciphertext in `tenants.EncryptedConnectionString`, KEK in `TAMMA_TENANT_KEK` env |
| 8 | **Key rotation** | Two-key overlap + background re-encrypt |
| 9 | **DB name format** | `tamma_tenant_<guid32hex>` (45 chars; Elsa variant +5) |
| 10 | **Slug separate from DB name** | Yes — `tenants.Slug` is user-visible only |
| 11 | **Grace window** | 5 minutes (configurable); message delayed via RabbitMQ |
| 12 | **Delete steps idempotent** | Yes — every step re-runnable |
| 13 | **Audit trail** | `tenants` row soft-deleted, 7-year retention; data gone |
| 14 | **User erasure** | Manual runbook in v1; first-class workflow in v2 |
| 15 | **DbContext for pre-tenant routes** | `ControlPlaneDbContext` only; `TenantDbContext` throws in DI |
| 16 | **Platform admin** | Flag on `users`, goes through resolver with impersonation event |
| 17 | **Backup before delete** | `pg_dump` custom format, 30-day retention, flag-gated |
| 18 | **Disaster recovery** | Elsa durable state + startup scan + nightly reconcile |

---

## 12. What this doc does not cover

- **Control-plane table shape** — owned by agent 1 (`01-control-plane-split.md`).
- **Global vs per-tenant Elsa split** — owned by agent 2
  (`02-elsa-topology.md`).
- **Provisioning (CREATE DATABASE, migrations on new tenant DB)** —
  owned by agent 3 (`03-provisioning-workflow.md`). This doc only owns
  the **drop** side.
- **Row-level security as an additional defence** — not part of
  db-per-tenant proper; if sibling docs enable it, the resolver doesn't
  care (it's per-DB anyway).
- **Cross-region replication / read replicas** — future concern; would
  extend the resolver to return primary / replica pairs.
- **Backup restoration UX** — out of scope; restore is an ops/legal tool.

---

*End of document.*
