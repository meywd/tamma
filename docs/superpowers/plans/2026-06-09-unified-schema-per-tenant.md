# Unified Schema-per-Tenant Tenancy Model — Architecture & Implementation Plan

> **For agentic workers:** This is an ARCHITECTURE + PHASE-DECOMPOSITION plan. It is intentionally
> not a single bite-sized TDD script — the change spans multiple subsystems. Each **Phase** below
> becomes its own detailed `superpowers:writing-plans` task-plan at execution time, implemented via
> `superpowers:subagent-driven-development`. Do NOT start coding from this doc until the "Decisions
> needed" section is resolved.

**Goal:** Replace the two-mode tenancy model (RLS-shared-tables *vs* optional own-DB) with ONE model:
every tenant has a uniquely-named schema and an (encrypted) connection string; the database behind
that connection string may host one tenant or many; placement is a SaaS-admin concern driven by
subscription tier. Tenant behavior is identical regardless of placement.

**Architecture:** Control-plane data stays global (one CP database). Tenant-resident data lives in a
per-tenant schema (`t_<hex>`) inside an assigned database, addressed by a connection string carrying
`Search Path=<schema>`. A `tenant_databases` registry (the admin's DB pool) records which databases
exist and which tenants live where; `plans.placement_policy` maps subscription tier → shared-pool vs
dedicated DB. Isolation is by schema + per-tenant role. Phase-3 RLS on tenant tables is removed.

**Tech Stack:** PostgreSQL 17 (schemas + per-tenant roles + `search_path`), EF Core 9 (per-schema
migrations history), Npgsql (`Search Path` connection-string key), existing AES-GCM KEK envelope for
connection-string encryption, existing `LruPooledTenantConnectionResolver`.

**Why now:** The server has **zero business data** (verified 2026-06-09: 0 users / 0 tenants / 0
events; the only rows are shipped workflow defs + migration history + seed alert rules). Stores are
fully replaceable, so we set the correct model from zero instead of carrying the split. No data
migration is required — schema is recreated from the EF model; the server gets one `volume-reset` at
next deploy.

---

## 1. Target model (the invariants)

1. **One tenant type.** No `shared-infra` vs `per-tenant` distinction in code or behavior.
2. **Every tenant has:** a unique `schema_name`, a `database_id` (which DB hosts that schema), and an
   encrypted `connection_string` (Host/Port/Database/Username/Password + `Search Path=<schema>`).
3. **The DB behind a tenant** may hold 1..N tenant schemas. "Dedicated" = the tenant is the only
   schema in its DB; "shared" = it co-resides with others. This is *placement*, invisible to the app.
4. **Isolation** = schema + per-tenant role (`role_<hex>` with `USAGE`/`CREATE` on only its own
   schema, default `search_path` = its schema). NOT RLS on co-mingled tables.
5. **Placement is admin-managed and tier-driven** (`plans.placement_policy`). Admin can list DBs, see
   tenant→DB mapping, and **move** a tenant to another DB.
6. **Control plane is global** (one CP DB): `users`, `tenants`, `tenant_memberships`,
   `platform_events`, `plans`, `tenant_databases`, etc. Cross-tenant admin queries keep working.

This makes the spec's data-integrity CHECK *correct and uniform* (no `ProviderKey` exemption):
`CHECK (Status = 'pending_verification' OR EncryptedConnectionString IS NOT NULL)` — because every
active tenant genuinely has a connection string.

---

## 2. Data model changes (control plane, global DB)

### 2.1 New table `tenant_databases` (the admin DB pool / registry)

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `Label` | text | operator-facing name, e.g. `shared-eu-1`, `dedicated-acme` |
| `Host`, `Port` | text/int | |
| `AdminConnectionStringEncrypted` | bytea | provisioner-role conn for creating schemas/roles (AES-GCM/KEK) |
| `PlacementClass` | text CHECK in (`shared`,`dedicated`) | |
| `TierEligibility` | text[] | which plan tiers may land here (e.g. `{free,team}`) |
| `TenantCapacity` | int null | max schemas (null = unbounded); for shared pools |
| `TenantCount` | int | maintained on placement/move |
| `Status` | text CHECK in (`active`,`draining`,`full`,`retired`) | |
| `KekVersion` | smallint NOT NULL DEFAULT 1 | for the admin conn envelope |
| `CreatedAt`,`UpdatedAt` | timestamptz | |

### 2.2 `tenants` table — columns to add / fix to spec

- Add `SchemaName text` — unique, `t_<guid32>`; the tenant's schema in its DB.
- Add `DatabaseId uuid` FK → `tenant_databases(Id)` (Restrict).
- `EncryptedConnectionString bytea` — keep; now **required once provisioned** (see CHECK).
- `KekVersion smallint NOT NULL DEFAULT 1` — **fix to spec** (currently `integer NULL`).
- `Status` — `CHECK ("Status" IS NULL OR "Status" IN ('pending_verification','provisioning','active','delete_requested','deleting','deleted','failed','suspended'))`.
- `CHECK ("Status" = 'pending_verification' OR "EncryptedConnectionString" IS NOT NULL)` — uniform.
- Remove the `ProviderKey`-as-mode flag from tenancy logic (keep column only if Epic 30 still needs
  it as a backend label; otherwise drop — see Decisions).

### 2.3 `plans` table — placement policy

- Add `PlacementPolicy text NOT NULL DEFAULT 'shared'` CHECK in (`shared`,`dedicated`).
- Seed: `free`,`team` → `shared`; `enterprise` → `dedicated` (confirm map in Decisions).

### 2.4 `api_keys` (CP) — fix to spec

- `Scope` CHECK in (`platform`,`user`) on the CP table; tenant-DB `api_keys.Scope` CHECK = `tenant`.

---

## 3. Subsystem responsibilities & file map

| Subsystem | Key files | Responsibility |
|---|---|---|
| Naming | `Tamma.Data/Pooling/TenantNaming.cs` | add `SchemaName(guid)` → `t_<hex>`; keep `RoleName`; `DatabaseName` becomes "the assigned DB's name", not derived from tenant id |
| CP model | `Tamma.Data/TammaModelConfiguration.cs`, `ControlPlaneDbContext.cs`, new `Entities/TenantDatabase.cs` | registry table, tenant columns, CHECKs, plan policy |
| Placement | new `Tamma.Api/Services/Provisioning/ITenantPlacementService.cs` | pick/assign a DB for a (tenant, plan-tier); capacity + tier eligibility |
| Tenant schema lifecycle | `Tamma.Activities/TenantLifecycle/*` (`CreateTenantDatabaseActivity` → becomes Create*Schema*; new `CreateTenantSchemaActivity`, role grants) | create role+schema in the assigned DB, grant schema-scoped, set search_path |
| Migrations into schema | `Tamma.Data/Pooling/EfTenantDbMigrator.cs`, `TenantDbContextFactory.cs` | apply `TenantDbContext` migrations into `t_<hex>` (search_path + `__TenantMigrationsHistory` in-schema) |
| Resolver | `Tamma.Data/Pooling/LruPooledTenantConnectionResolver.cs`, remove `StubTenantConnectionResolver` central path | every tenant → decrypt conn string → pooled `NpgsqlDataSource` with search_path |
| Conn-string mint/encrypt | `Tamma.Api/Services/Provisioning/TenantSecretProtector*` | build `…;Search Path=t_<hex>`, encrypt, store on tenant row |
| Admin | `Tamma.Api/Endpoints/Admin/*` (new `TenantDatabasesEndpoints`, `MoveTenantEndpoint`) | DB-pool CRUD; tenant→DB view; move-tenant op |
| Move tenant | new `Tamma.Activities/TenantLifecycle/MoveTenantWorkflow` + activities | `pg_dump --schema` → restore into target → re-point conn string → evict pool → drop source schema/role |
| RLS removal | Phase-3 RLS SQL + `Tamma:RequireTenantIsolation` guard, `docker/init-db.sql` | drop RLS-on-tenant-tables; isolation now schema+role |
| Backup (28-5) | `BackupTenantDatabaseActivity` | becomes schema-scoped: `pg_dump --schema=t_<hex>` instead of whole-DB |
| Migration collapse | all 4 owned contexts' `Migrations/` dirs | regenerate single baseline per context reflecting the above |

---

## 4. Phase decomposition (each becomes its own task-plan)

- **Phase 0 — DONE 2026-06-09.** CP schema to target + collapse CP baseline. Add `tenant_databases`, tenant columns
  (`SchemaName`, `DatabaseId`), CHECKs (Status, uniform connection-string, api_keys Scope), KekVersion
  → smallint; `plans.PlacementPolicy` + seed. Reconcile the runtime/design-time history-table-name
  mismatch. Delete CP migrations (`Migrations/` + `Migrations/ControlPlane/`) and regenerate one
  `InitialControlPlane`. Validate apply on a throwaway Postgres. **Schema-only — no behavior change.**
- **Phase 1 — DONE 2026-06-09.** Tenant schema + per-schema migrations. `TenantNaming.SchemaName`; `EfTenantDbMigrator`
  + `TenantDbContextFactory` apply into `t_<hex>` via `Search Path` + in-schema history table. Collapse
  the tenant baseline.
- **Phase 2 — Unified resolver.** Resolver always uses the stored connection string + search_path;
  remove `StubTenantConnectionResolver` central fallback and the "ControlPlane string ⇒ stub" branch.
- **Phase 3 — Unified creation path.** `ITenantPlacementService` (tier → DB); `CreateTenantSchemaActivity`
  (role+schema+grants); mint+encrypt connection string for **every** tenant, including the personal
  tenant at registration. Remove "shared-infra-only until provisioning."
- **Phase 4 — Admin placement + move.** `tenant_databases` CRUD endpoints (OwnerAccess); tenant→DB
  view; `MoveTenantWorkflow` (dump-schema → restore → re-point → evict → drop source).
- **Phase 5 — Remove Phase-3 RLS + cleanup.** Drop RLS policies on tenant tables; retire the
  `ProviderKey` mode flag (per Decisions); adapt the 28-5 backup to `--schema`.

Phases 0–2 are internal/safe (no live data). Phase 3 changes the registration/login path — gate behind
tests. Phases 4–5 are additive + cleanup.

---

## 5. Per-tenant credentials & isolation (recommended)

**Role-per-tenant** (`role_<hex>`): `GRANT USAGE, CREATE ON SCHEMA t_<hex> TO role_<hex>`,
`ALTER ROLE role_<hex> SET search_path = t_<hex>`, and crucially **no grant on `public` or other
tenant schemas**, so a tenant connection cannot read another schema even by changing `search_path`.
This generalizes the existing db-per-tenant role model to schema-per-tenant and gives real isolation
inside a shared DB. (Alternative — one shared role per DB — is simpler but weaker; see Decisions.)

---

## 6. Decisions

**LOCKED (2026-06-09):**

1. **Role model: role-per-tenant.** Each tenant connects as `role_<hex>` with `USAGE`/`CREATE` on only
   its own schema + default `search_path = t_<hex>`, no grant on `public`/sibling schemas. Strong
   isolation inside a shared DB. (See §5.)
2. **Tier → placement: `free`+`team` → shared pool, `enterprise` → dedicated DB.** `plans.PlacementPolicy`
   seed: free=`shared`, team=`shared`, enterprise=`dedicated`.

**Resolved with defaults (revisit if needed; do NOT block Phase 0):**

3. **`ProviderKey`/Epic 30 — default:** keep `ProviderKey` only as a *backend label* (which provider
   minted the hosting DB into `tenant_databases`), NOT as a tenancy-mode flag. Epic 30's
   `ITenantInfrastructureProvider` becomes the thing that *provisions a database row into the
   registry*; per-tenant placement/schema lifecycle is owned by this model. Final drop-or-keep of the
   column decided in Phase 5.
4. **Move-tenant downtime — default:** brief per-tenant **read-only window** during a move (mark
   `draining` → dump-schema → restore → re-point → evict → drop source). Online/zero-downtime move is
   explicitly out of scope (§7).
5. **Scope vs Epic 28 — default:** treat as an **extension/re-scope of Epic 28** (reuses 28's resolver,
   KEK envelope, lifecycle-workflow + activity base). Tracking-doc update only; not a blocker. Confirm
   epic numbering when we wire story docs.

**Phase 0/1 implementation deviations (2026-06-09, recorded from the task-plans):**

6. **api_keys Scope CHECK is transitional**: `('platform','user','installation','service','tenant')`
   on CP — live code writes service/installation/tenant scopes to CP today. Tighten to the spec's
   `('platform','user')` when tenant-scoped keys physically move out (Phase 2+).
7. **Conn-string CHECK exempts `provisioning`,`failed`,`deleted`,`deleting`,`delete_requested`**
   (besides NULL/`pending_verification`) — presence is enforced only for `active`/`suspended`.
   Today's flows hold NULL conn strings in those states (mint happens mid-provisioning; failure can
   precede mint; delete nulls the envelope), and force-delete enters deleting/delete_requested from
   `failed` (or legacy NULL-status) rows that never got minted — without the exemption the designed
   cleanup path hits 23514. Spec-exact form lands with Phase 3's mint-at-creation.
8. **RLS + tamma_app role ported verbatim** into the collapsed `InitialControlPlane` baseline
   (34 raw-SQL objects) so Phase 0 stays behavior-neutral; removal stays Phase 5.
9. **uuid-ossp dependency eliminated** (mentorship defaults → `gen_random_uuid`) — an extension
   function would not resolve under a per-tenant `search_path`; both baselines now apply on bare
   Postgres with zero extensions.
10. **Tenant baseline carries zero raw SQL** — the NULLS NOT DISTINCT unique indexes are
    model-level in EF 9.

---

## 7. Non-goals / explicitly out of scope

- Cross-region DB placement, read replicas, connection-pooler (pgbouncer) topology.
- Online (zero-downtime) tenant moves (unless Decision 4 says otherwise).
- Any data migration tooling — there is no data; recreate from the model.

---

## Self-review notes

- Spec coverage: the original "schema to spec + collapse migrations" ask is absorbed into Phase 0/1
  (CHECKs, KekVersion, baselines). The new "one model + tier-driven placement" requirement is the
  spine (Phases 0–4). RLS removal is Phase 5.
- Open risk: Phase 3 touches auth/registration — the highest-blast-radius change; it must land with
  the unified resolver (Phase 2) already green, behind the full suite + a Postgres integration run.
- Validation: every phase that regenerates migrations must prove the baseline applies to a throwaway
  Postgres before commit; CI's "Integration Tests (Postgres)" is the gate; server gets one
  `volume-reset` at deploy.
