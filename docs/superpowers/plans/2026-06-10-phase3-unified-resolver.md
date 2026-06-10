# Phase 3 — Unified Resolver: System Store + Stub Removal

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tenant data access goes through ONE path — `LruPooledTenantConnectionResolver` resolving
each tenant's stored encrypted connection string (`Search Path=t_<hex>`) — with
`StubTenantConnectionResolver` deleted; platform-level "system default" rows (TenantId NULL) get an
explicit home: the **system store** (the central DB's public-schema tenant tables, exactly where
they live today), accessed through a dedicated seam instead of riding the stub.

**Architecture:** A new `ISystemStoreDbContextFactory` returns a `TenantDbContext` bound to the
central admin/app connection (no `Search Path`) — the system store. Every service that reads/writes
TenantId-NULL system rows (conventions, sanitization_rules, agent_configs, budget_configs,
provider_health) splits its data path: tenant rows via `ITenantDbContextFactory` (now always
resolver-backed), system rows via the system store. `ConventionStoreSeeder` seeds the system store
directly (no more `GetDataSourceAsync(Guid.Empty)`). The LRU resolver registers UNCONDITIONALLY
(its only hard deps — `IDbContextFactory<ControlPlaneDbContext>`, `IConnectionStringDecryptor`,
options — exist in every topology; "CP" in dev/self-host is simply the central DB).
`TenantDbContextFactory` keeps only the resolver ctor. The
`GuardTenantIsolationInProduction`/`Tamma:RequireTenantIsolation` knob is deleted (it guarded
exactly the stub fallback that no longer exists). Single-user personal-tenant provisioning becomes
hard-fail. Dev gets a checked-in Development-only KEK so provisioning works out of the box.

**Tech Stack:** .NET 9 / EF Core 9 / Npgsql 9, existing Phase 2 provisioning pipeline, Testcontainers.

**Parent doc:** `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (§4 Phase 3 —
re-ordered: was "Phase 2 — unified resolver").

---

## Environment facts (verified 2026-06-10 — do not re-derive)

- Repo: `/home/meywd/tamma/apps/tamma-elsa`, branch `feat/wave-b`. Build `dotnet build Tamma.sln`;
  docker/tests via `sg docker -c "..."`. Full-suite baseline: 4464 passed / 11 skipped.
- **System-default rows** (TenantId NULL in tenant-resident tables): `conventions` (read:
  `ConventionStore.GetAsync`/`ResolveAsync` via `IConventionRepository` →
  `ITenantDbContextFactory`; write: `ConventionStoreSeeder` + admin CRUD
  `UpsertSystemDefaultAsync`/`DeleteSystemDefaultAsync`), `sanitization_rules`, `agent_configs`
  (one system row), `budget_configs`, `provider_health` (per-provider platform rows). PromptStore
  system defaults are IN-CODE (`SystemPrompts`) — NOT affected.
- `ConventionStoreSeeder.SeedAsync(ct)` overload calls `_resolver.GetDataSourceAsync(Guid.Empty)` —
  stub-dependent; the `SeedAsync(TenantDbContext, ct)` overload is the test seam.
- `TenantDbContextFactory` DI (`Tamma.Data/DependencyInjection.cs:86-90`): shared-conn-string mode.
  Stub registered at `:102-106` (TryAddSingleton). `AddTenantConnectionPool`
  (`TenantConnectionPoolServiceCollectionExtensions.cs`) requires a CP conn string and is gated in
  `Program.cs:268-293` on `ConnectionStrings:ControlPlane` presence.
- `GuardTenantIsolationInProduction` (`DependencyInjection.cs:216-240`) + call site
  (`Program.cs:246-251`) + `Tamma__RequireTenantIsolation=false` in
  `docker-compose.prod.yml:211` — all stub-era; delete together.
- Resolver consumers and their failure handling: TenantContextMiddleware (handles
  NotFound/NotProvisioned), ApiKeyAuthHandler (VERIFY handling), PoolWarmupService (catch-all),
  KekRotationCoordinator (iterates envelope-bearing tenants only), ConventionStoreSeeder
  (Guid.Empty — replaced by T1), TenantStatusInvalidationListener (EvictAsync — idempotent).
- `EnsurePersonalTenantMiddleware` soft-fail try/catch at ~lines 165-183 (Phase 2 comment says
  Phase 3 makes it hard).
- KEK: `TenantSecretProtector.FromConfiguration` — `Cranl:EncryptionKey` (prod hard-fail), dev
  fallback HKDF from `Cranl:ApiKey`; NEITHER set in appsettings.Development.json today. Prod VPS
  now receives `CRANL_ENCRYPTION_KEY` via deploy secrets (commit 225af712); the bootstrap seeder is
  boot-safe (warn+skip) when the protector is unavailable.
- Test fixtures all ride the stub/shared mode today: ApiTestFixture (2 containers: CP + shared
  tenant store), TenancySetUpFixture (1 container + RLS roles), Diagnostics/Providers/
  ProviderSession fixtures (ApiTestFixture pattern), ConventionStore tests (direct TenantDbContext).
- Phase 2 gave: `TenantProvisioningService.ProvisionAsync` (placement→role→schema→mint→migrate→
  encrypt→active), `TenantDatabasesSeeder` (central pool row at startup), personal tenants
  provision at first login (soft-fail).

## Phase 3 boundaries (YAGNI guard)

- NO admin CRUD for tenant_databases / move-tenant (Phase 4). NO RLS removal / ProviderKey retirement
  (Phase 5). NO data migration anywhere (zero data).
- The system store keeps living in the central DB public schema — moving system rows to CP-native
  tables is explicitly NOT this phase (record as a Phase 5+ consideration only if friction appears).

---

### Task 1: `ISystemStoreDbContextFactory` + ConventionStore split + seeder off the resolver (TDD)

**Files:**
- Create: `apps/tamma-elsa/src/Tamma.Data/Abstractions/ISystemStoreDbContextFactory.cs`
- Create: `apps/tamma-elsa/src/Tamma.Data/SystemStoreDbContextFactory.cs`
- Modify: `Tamma.Data/DependencyInjection.cs` (register it with the same conn-string chain the
  shared TenantDbContextFactory uses today)
- Modify: `src/Tamma.Api/Services/Conventions/ConventionStoreSeeder.cs` (resolver overload →
  system store), `ConventionRepository`/`ConventionStore` (system-default reads/writes → system
  store; tenant-override reads stay on `ITenantDbContextFactory`)
- Tests: extend the existing ConventionStore test files — they already pass a direct
  `TenantDbContext`; add coverage that system-default resolution works when the TENANT context has
  no system rows (i.e., the two legs genuinely hit different stores).

Contract (complete):

```csharp
namespace Tamma.Data.Abstractions;

/// <summary>
/// Unified-tenancy Phase 3 — the SYSTEM STORE: platform-level default rows
/// (TenantId NULL — conventions, sanitization rules, agent/budget config,
/// provider health) live in the central database's public-schema tenant
/// tables, owned by platform admins. Tenant-scoped rows live in each
/// tenant's t_&lt;hex&gt; schema and are reached via
/// <see cref="ITenantDbContextFactory"/>. This seam replaces the
/// transitional "stub resolver routes Guid.Empty to the shared DB" trick.
/// </summary>
public interface ISystemStoreDbContextFactory
{
    ValueTask<TenantDbContext> CreateAsync(CancellationToken cancellationToken = default);
}
```

Implementation: holds the central connection string (same `appConnectionString ??
adminConnectionString` chain `AddTammaData` uses today), builds options exactly like
`TenantDbContextFactory`'s shared mode did (history table pinned, no Search Path), returns
`new TenantDbContext(options)` (no tenant id — read how the seeder's overload constructs it today
and mirror). Split rule for ConventionStore: `GetSystemDefaultAsync`/`UpsertSystemDefaultAsync`/
`DeleteSystemDefaultAsync`/`ListSystemDefaultsAsync` (grep the repository for TenantId-NULL
queries) go through the system store; tenant-override methods stay on the tenant factory. The
SeedAsync(ct) overload drops its `ITenantConnectionResolver` dependency entirely.

Verify: build; `sg docker -c "dotnet test tests/Tamma.Api.Tests/Tamma.Api.Tests.csproj --filter
'FullyQualifiedName~Convention' -v minimal"` green; commit
`feat(tenancy-p3): system store seam + ConventionStore system/tenant split`.

---

### Task 2: Split the remaining system-row services

**Files:** discover by grep — for EACH of `sanitization_rules`, `agent_configs`, `budget_configs`,
`provider_health`: find every query filtering `TenantId == null` (or falling back to a NULL-tenant
row) in `src/` (`grep -rn "TenantId == null\|TenantId is null\|TenantId IS NULL" src/ --include=*.cs`
plus reading the owning service of each entity). Route those reads/writes through
`ISystemStoreDbContextFactory`; tenant-scoped legs stay put. The fallback SEMANTICS (tenant →
system → error/default) must not change — assert by running each service's existing test filter
after the change. If an entity turns out to have NO live system-row usage (dead column), record
that in the report instead of inventing a split.

Verify: build + the touched services' test filters green; commit
`feat(tenancy-p3): system-row reads route through the system store`.

---

### Task 3: Resolver unification — delete the stub

**Files:**
- Modify: `Tamma.Data/DependencyInjection.cs` — `TenantDbContextFactory` registered in RESOLVER
  mode; DELETE the stub registration; DELETE `GuardTenantIsolationInProduction`.
- Delete: `Tamma.Data/StubTenantConnectionResolver.cs` (grep references first — tests using it get
  fixed in T5; if src/ references exist beyond DI, STOP and report).
- Modify: `TenantConnectionPoolServiceCollectionExtensions.AddTenantConnectionPool` — drop the
  CP-string requirement (the pooled CP DbContext factory it wires should fall back to the central
  connection string when no dedicated CP string exists — read what it does with
  `controlPlaneConnectionString` and adapt); `Program.cs:268-293` — register UNCONDITIONALLY,
  delete the else-branch log + the guard call at :246-251.
- Modify: `docker-compose.prod.yml` — remove `Tamma__RequireTenantIsolation` block (comment + line);
  grep `RequireTenantIsolation` repo-wide and purge (code, tests, docs notes inline).
- Modify: `TenantDbContextFactory` — delete the shared-conn-string ctor and `_connectionString`
  field (resolver-only); fix the XML doc.
- Keep `appConnectionString` plumbing in AddTammaData only as far as the system store (T1) needs it.

Expected fallout at this commit: tests riding the stub fail — that is T5's job; this task's gate is
`dotnet build Tamma.sln` (0 errors) + the resolver/pool unit tests
(`--filter 'FullyQualifiedName~TenantConnectionPool|FullyQualifiedName~Resolver'`) green. Commit
`feat(tenancy-p3)!: LRU resolver is the only tenant connection path; stub removed`.

---

### Task 4: Hard-fail personal provisioning + dev KEK

**Files:**
- `EnsurePersonalTenantMiddleware` — remove the try/catch (let `ProvisionAsync` failures propagate;
  update the Phase 2 comment), update its tests (throwing fake now FAILS the request — invert that
  test's assertion).
- `src/Tamma.Api/appsettings.Development.json` — add a Development-only KEK with a loud comment:

```jsonc
// Dev-only AES-GCM KEK (base64 32 bytes) so tenant provisioning works out
// of the box. NEVER reuse outside local development — production deploys
// supply TAMMA_CRANL_ENCRYPTION_KEY via deploy secrets.
"Cranl": { "EncryptionKey": "<generate once with: openssl rand -base64 32>" }
```

(JSON has no comments — put the warning in an adjacent `"_comment_EncryptionKey"` property.
Generate a real value and commit it; it protects nothing but local dev data.)

Verify: build + middleware/auth test filters; commit
`feat(tenancy-p3): personal-tenant provisioning is mandatory; dev KEK ships in Development config`.

---

### Task 5: Test-fixture migration (the long tail) + full suite

The big one. Mechanism per fixture:
1. Fixture env gets a KEK (`Cranl__EncryptionKey` env var, explicit base64 — fixtures already set
   env vars; mirror).
2. The tenant-data container/DB keeps its role as the SYSTEM STORE (tenant migrations still applied
   to it for public-schema system rows — unchanged).
3. Every test tenant whose TENANT data is touched must be provisioned: after creating the tenant
   row, call `ITenantProvisioningService.ProvisionAsync(tenantId)` (resolve from the test server's
   services) — the central pool row comes from `TenantDatabasesSeeder` (runs at API startup
   already). Single-user-mode fixtures get this FREE via the middleware. SaaS-mode tests creating
   orgs must provision explicitly (no workflow subscriber exists — pre-existing gap, do NOT build
   one now; call the service in the test/fixture).
4. Tests that constructed `TenantDbContextFactory("conn-string")` directly switch to the resolver
   ctor with a real or fake resolver; tests that used `StubTenantConnectionResolver` get a minimal
   local fake (`tests/.../TestDoubles/` already has Recording/Noop resolvers — extend/reuse).

Procedure: run the FULL suite, bucket failures by fixture, fix bucket-by-bucket (fixtures first,
then stragglers), re-run until green. Budget: this is expected to be the phase's largest task —
report per-bucket counts as you go. Gate: full suite 0 failures (count may shift slightly with
inverted/added tests). Commit per bucket or as one
`test(tenancy-p3): fixtures provision tenants through the unified resolver path`.

---

### Task 6: Docs + execution record

- Parent plan: Phase 3 → `**Phase 3 (re-ordered: was Phase 2) — DONE <date>.**`; deviation list
  append: `13. **System store** — platform-default rows stay in the central DB public schema behind
  ISystemStoreDbContextFactory (not moved to CP tables). 14. **RequireTenantIsolation knob deleted**
  — it guarded the stub fallback that no longer exists. 15. **Dev KEK ships in
  appsettings.Development.json** (dev-only, documented).`
- `wiki/Architecture.md` + `wiki/Multi-Tenant-Provisioning.md`: resolver section (one path, stub
  gone), system-store description, config-reference updates (`docs/deployment/configuration-reference.md`
  — grep RequireTenantIsolation/ControlPlane guidance and update).
- Execution record appended to THIS plan (commit range, suite counts, fixture-bucket notes).
- Commit `docs(tenancy-p3): mark Phase 3 complete (unified resolver + system store)`.

---

## Self-review notes

- **Spec coverage** (parent Phase 3): resolver always uses stored conn string ✓ (T3+T5); stub +
  central fallback removed ✓ (T3); "ControlPlane string ⇒ stub" branch gone ✓ (T3). System-default
  question — the thing that actually blocked stub removal beyond conn strings — solved by the
  system-store seam ✓ (T1-T2).
- **Failure-mode honesty:** unprovisioned tenant + tenant-data access now throws
  `TenantConnectionStringMissingException` end-to-end; TenantContextMiddleware already maps
  not-provisioned to a 5xx/Retry-After UX. T5 proves the suite survives that reality.
- **Type consistency:** `ISystemStoreDbContextFactory.CreateAsync()` mirrors
  `ITenantDbContextFactory.CreateAsync(tenantId)` shape minus the tenant id.
- **Known risks:** (1) T5's long tail is unbounded until run — that's why it's isolated with a
  bucket procedure. (2) `AddTenantConnectionPool`'s pooled CP factory behavior without a dedicated
  CP string needs reading before edit (T3 instructs). (3) ApiKeyAuthHandler's exception handling
  for missing envelopes — T5 will surface it via failing tests if inadequate; fix there.

---

## Execution record (2026-06-10) — PHASE COMPLETE

**Commit range:** `27f99619..a58f983d` on `feat/wave-b`, plus the closing docs commit
(`docs(tenancy-p3): mark Phase 3 complete ...`).

| Commit | Task | What |
|---|---|---|
| `27f99619` | T1 | `ISystemStoreDbContextFactory` seam + ConventionStore system/tenant split; seeder off the resolver (`GetDataSourceAsync(Guid.Empty)` gone) |
| — | T2 | **Resolved as no-op** (see below) — no commit |
| `750bf004` | T3 | `feat!`: LRU resolver is the only tenant connection path; stub + `GuardTenantIsolationInProduction`/`Tamma:RequireTenantIsolation` deleted; `TenantDbContextFactory` resolver-only; pool registers unconditionally |
| `ed287b1d` | T4 | Personal-tenant provisioning mandatory (soft-fail try/catch removed); dev KEK in `appsettings.Development.json` |
| `f1107558` | T5 | Diagnostics bucket provisions tenants through the unified path |
| `e54aec20` | T5 | Providers bucket (pinned test tenant provisioned) |
| `683b7426` | T5 | Orgs bucket (org tenants provisioned) |
| `c9ef45d5` | T5 | Dashboard/Engine/TaskQueue buckets |
| `9d72951d` | T5 | SaaS/Epic9/Agents/Alerts buckets |
| `d9dc85c6` | T5 | ConventionStore harness drops the relocated stub resolver |
| `a58f983d` | T5 | src fix: per-tenant migration connection no longer strands an idle pool |

**T2 finding — resolved as no-op.** The grep + service read showed the system-row (TenantId NULL)
tiers for `sanitization_rules`, `agent_configs`, `budget_configs`, and `provider_health` are
**dead**: their platform defaults are in-code per Story 28-1, and no live read/write path queries a
NULL-tenant row for them. Only **conventions** has a real system-default tier, and T1 already moved
it onto the system store. No split was invented for the dead tiers (per the plan's instruction).

**T5 failure inventory:** the post-T3/T4 full suite opened at **91 failures**; bucket-by-bucket
fixture migration (KEK env var + explicit `ProvisionAsync` per test tenant + resolver-ctor/fake
swaps) drove it to **0**.

**Production bugs exposed by the unified path** (fixed in `f1107558..a58f983d` — the test
migration was the detector, the fixes are src/):

1. **EF internal-service-provider explosion at >20 tenant data sources** — each per-tenant
   `NpgsqlDataSource` produced a distinct EF internal service provider; `TenantDbContextFactory`
   now lends a pooled connection instead.
2. **CreateOrg emitted into the not-yet-provisioned tenant store** — org creation now calls
   `ProvisionAsync` synchronously before the first tenant-store write; orgs and personal tenants
   share the same first-class path.
3. **DeleteOrg emitted after the soft-delete made the store unreachable** — lifecycle event now
   emits before the delete (emit-before-delete).
4. **Per-tenant migration connections stranded one idle pool each** — the one-shot migration
   connection now sets `Pooling=false`.

**Known residuals (deliberate):** failed CreateOrg leaves a recoverable half-tenant (idempotent
re-provision exists, operator-driven — no self-service org re-provision endpoint yet);
tenant-terminal lifecycle events (DELETED/PURGED) are written to the tenant's own store and are
unreachable post-delete — move to the CP store when convenient.

**Final gate:** full suite **4461 passed / 0 failed** (count shifted from the 4464 baseline with
inverted/removed stub-era tests). Docs updated: parent plan Phase 3 marked DONE + deviations 13–17;
`wiki/Architecture.md` (single resolver path, §6.3 system store, hard-fail provisioning);
`wiki/Multi-Tenant-Provisioning.md` (synchronous org provisioning, stub removal);
`docs/deployment/configuration-reference.md` (`Cranl:EncryptionKey` row; RequireTenantIsolation
removal note verified).
