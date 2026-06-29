# Epic 30 — Pluggable Provisioning: Finish the Seam, Tighten the Model

> **For agentic workers:** This is an ARCHITECTURE + PHASE-DECOMPOSITION plan in the style of
> `2026-06-09-unified-schema-per-tenant.md`. Each **Phase** below becomes its own detailed
> `superpowers:writing-plans` task-plan at execution time, implemented via
> `superpowers:subagent-driven-development`. Tasks inside phases use checkbox (`- [ ]`) syntax.
> The project is test-first: every task lists the tests to write BEFORE implementation.

**Status**: PHASE A DONE (2026-06-29); Phases B–E PLANNED.

**Goal:** Finish Epic 30's pluggable-provisioning seam on top of the now-merged unified
schema-per-tenant model (PR #343, 98cfb1c2): retire the [Obsolete] V1 `ITenantProvisioner` surface
("Wave C"), reconcile the V2 provider contract with the unified model (providers mint
`tenant_databases` pool rows — they do NOT own tenant DB routing), tighten the two transitional
CHECK constraints (parent-plan deviations 6–7), make Story 28-1's `MigrateTenantElsaAsync` real (or
formally re-scope it with a recorded decision), and run the Story 28-13 OpenBao trigger review.

**Architecture:** One provisioning contract — `ITenantInfrastructureProvider` (V2, already in code)
— with backends registered in `TenantProviderRegistry` (`null`, `cranl` today; `hetzner`,
`cloudflare`, `byo` reserved). Per locked decision 3 / deviation 22 of the parent plan, a backend's
job is to **mint hosting infrastructure and register it as a `tenant_databases` pool row**;
placement, schema lifecycle, and per-tenant connection strings stay owned by the unified model
(`TenantPlacementService` + `TenantMoveService` + AES-GCM Search-Path envelopes). `ProviderKey` on
the tenant is a backend LABEL only. The V2 endpoint directory narrows to engine-URL routing for
dedicated-compute tenants; database routing is always the unified
`EncryptedConnectionString` path.

**Tech Stack:** .NET 9 / EF Core 9 / Npgsql / Testcontainers; platform task queue
(`PlatformTaskWorker` + `IPlatformTaskHandler`); existing AES-GCM `TenantSecretProtector`.

**Parent docs:**
- `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (deviations 6–7, 22; decision 3)
- `docs/stories/epic-30/README.md` (story map — written 2026-04-20, partially stale, see findings)
- `docs/stories/epic-28/story-28-13/28-13-openbao-kms-backend.md` (trigger checklist)

---

## 1. Current-state findings (verified 2026-06-10 on main @ 98cfb1c2 — do not re-derive)

### 1.1 Two provisioning surfaces coexist; production still rides V1

- **V1 (Obsolete, "Removed in Wave C")** —
  `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/`:
  - `ITenantProvisioner.cs:27` — `[Obsolete("Use ITenantInfrastructureProvider (V2) instead. Removed in Wave C.")]`
  - `NullTenantProvisioner.cs` — fakes "shared infra Ready" immediately (no-Cranl deployments).
  - `CranlTenantProvisioner.cs` + `CranlProvisioningWorkflow.cs` (435 lines) +
    `TenantProvisioningTaskHandler.cs` — the live Cranl walk.
  - Wired in `Tamma.Api/Extensions/ProvisioningServiceCollectionExtensions.cs:76` (Cranl) and
    `:93` (Null), both under `#pragma warning disable CS0618`.
  - **Production callers**: `Tamma.Api/Endpoints/AdminEndpoints.cs:428-482`
    (`ProvisionTenant` / `GetTenantProvisioning` / `DeprovisionTenant`) inject `ITenantProvisioner`.
- **V2 (Stories 30-1, 30-2, 30-3, 30-8 — code landed in wave-b; commits 85ce1de8, 8a60c675,
  8a9a58c0/20af4dd9, 441cad39/bebedba6, fix 9bf0341b)** —
  `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/`:
  - `ITenantInfrastructureProvider.cs` — `ProviderKey`, `GetCapabilities()`, `ProvisionAsync`,
    `GetStatusAsync`, `DeprovisionAsync`, `ResolveEndpointsAsync`; idempotency contract in XML doc.
    Reserved keys: `null`, `cranl`, `hetzner`, `cloudflare`, `byo`.
  - `TenantProviderRegistry.cs` (singleton over `IEnumerable<ITenantInfrastructureProvider>`),
    `NullTenantProvider.cs` (throws `NotSupportedException` on provision/deprovision/resolve —
    deliberately NOT the V1 fake-Ready semantics; see `NullTenantProvider.cs:23-25`),
    `Cranl/CranlTenantProviderV2.cs` (360 lines, scoped, adapted via
    `ScopedTenantInfrastructureProviderAdapter`).
  - `ProvisionTenantV2Workflow.cs` (30 KB) — 8-step compensating saga (ResolveProvider → Preflight
    → ReserveResources → ExecuteProvision → PersistEndpoints → RegisterSecrets(placeholder) →
    InitialProbe → Activate), resumable via `tenants.provisioning_state`; plain orchestrator class,
    not Elsa (30-1 ADR §5 — Tamma.Activities does not reference Tamma.Api).
  - `ProvisionTenantV2Dispatcher.cs` + `ProvisionTenantV2TaskHandler.cs` — platform-queue dispatch.
    **The dispatcher has NO production caller** — only DI registration
    (`ProvisioningServiceCollectionExtensions.cs:130`). Admin endpoints never reach V2.
  - `V2TenantEndpointDirectory.cs` — bound as the `ITenantEndpointDirectory` the LRU resolver
    consults BEFORE the legacy decrypt path (`Program.cs:274` `AddTenantProvisioningV2()`;
    `Tamma.Data/Pooling/LruPooledTenantConnectionResolver.cs:112-126`, fallback flow at `:948-996`).
    Resolution: `tenants.ProviderKey` NULL → `NotApplicable` → legacy `EncryptedConnectionString`.
  - `SqlTenantProviderKeyLookup.cs` — tolerates a missing `provider_key` column (pre-30-3 deploys).
- **Shared vocabulary**: `ProvisioningModels.cs:22` `enum ProvisioningState`
  (`none → pending → database_provisioning → database_ready → app_provisioning → app_deploying →
  ready | failed | deprovisioning | deprovisioned`) is used by BOTH surfaces
  (`V2/ProvisioningStatusSnapshot.cs` doc: "deliberately does not mint a new state-machine
  vocabulary"). Wave C must keep this enum when deleting the rest of `ProvisioningModels.cs`'s V1
  types. Persisted on `tenants.ProvisioningState` (`Tamma.Data/Entities/Tenant.cs:51`).

### 1.2 The V2/unified-model tension (the real architectural work)

`CranlTenantProviderV2.ResolveEndpointsAsync` (`V2/Cranl/CranlTenantProviderV2.cs:240-340`)
decrypts `tenants.CranlDatabaseUrlEncrypted` (`Tenant.cs:37`) and returns it as the tenant's
`DatabaseUrl`. The LRU resolver would build an `NpgsqlDataSource` on that **raw Cranl DB URL — no
`Search Path=t_<hex>`, no per-tenant role** — bypassing the unified isolation model entirely.
Neither `CranlProvisioningWorkflow` (V1) nor `CranlTenantProviderV2` registers the minted hosting
database into `tenant_databases` (grep: zero references). Today this path is dormant only because
nothing sets `tenants.ProviderKey` in production (no V2 caller). Parent-plan decision 3 +
deviation 22 define the fix: **the provider mints a pool row; the unified model places the tenant
schema onto it.**

### 1.3 The two transitional CHECKs (parent deviations 6–7) — exact current definitions

Authoritative in `Tamma.Data/TammaModelConfiguration.cs`, hand-mirrored in
`Migrations/ControlPlane/20260609205701_InitialControlPlane.cs` (`:457`, `:564`), its
`.Designer.cs`, and `ControlPlaneDbContextModelSnapshot.cs` (`:487`, `:1406`) — the Phase-0 "C1
procedure": edit all four, then `dotnet ef migrations has-pending-model-changes` must report none.

- **Deviation 6** — `TammaModelConfiguration.cs:359-361`:
  ```sql
  CONSTRAINT ck_api_keys_scope CHECK ("Scope" IN ('platform','user','installation','service','tenant'))
  ```
  Spec target on CP is `('platform','user')`. Live writers of the extra scopes:
  `Endpoints/OrgApiKeysEndpoints.cs:53,72` (`Scope = "tenant"` → CP),
  `Services/GitHub/InstallationRouterService.cs:33,286` (`"installation"`), plus `"service"`
  writers (`CiSecretsRotationHandler`, `ApiKeyRotationService` area). The tenant-side `api_keys`
  table already exists with a `Scope = 'tenant'` CHECK
  (`tests/Tamma.Api.Tests/Epic28/TenantDbContextModelTests.cs:90`).
- **Deviation 7** — `TammaModelConfiguration.cs:277-282`:
  ```sql
  CONSTRAINT ck_tenants_connection_string_present CHECK (
    "Status" IS NULL
    OR "Status" IN ('pending_verification','provisioning','failed','deleted','deleting','delete_requested')
    OR "EncryptedConnectionString" IS NOT NULL)
  ```
  Spec-exact target (parent plan §1): `"Status" = 'pending_verification' OR
  "EncryptedConnectionString" IS NOT NULL`. The exemptions exist because (a) V1 Cranl flows minted
  mid-provisioning — now false: unified creation mints at tenant creation (Phase 2/3, 2026-06-10);
  (b) delete flows null the envelope and force-delete enters deleting/delete_requested from
  never-minted `failed` rows (comment block `TammaModelConfiguration.cs:258-269`).

### 1.4 Story 28-1's stub and the two Epic-30-gated ignored tests

- `Tamma.Data/Pooling/EfTenantDbMigrator.cs:104-115` — `MigrateTenantElsaAsync` logs
  `tenant.lifecycle.migrate_elsa skipped reason=elsa_db_not_split` and returns
  `Task.CompletedTask`. Interface: `Tamma.Data/Abstractions/ITenantDbMigrator.cs:42`.
  **No production caller exists** — only the interface + impl pair. Story doc
  (`docs/stories/epic-28/story-28-1/28-1-ef-migration-scripts.md`): "MOSTLY DONE … per-tenant Elsa
  DB migrate/run: INTENTIONAL no-op stub, NOT runtime-verified, deferred to the Epic 30
  db-per-tenant runtime cutover."
- `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs:229-254` —
  `Tenants_Cranl_Columns_Are_Ignored_On_NewContext` `[Ignore]`: "re-enable when Epic 30 lands the
  alternative routing column" (asserts `CranlProjectId`/`CranlDatabaseId`/`CranlAppId`/
  `CranlAppUrl`/`CranlDatabaseUrlEncrypted`/`CranlRegion`/`ProvisioningState`/`ProvisioningDetail`/
  `ProvisioningUpdatedAt` leave the Tenant model).
- `tests/Tamma.Api.Tests/Epic28/TenantDbContextModelTests.cs:59-87` —
  `Tenant_Resident_Entities_Have_No_TenantId_Column` `[Ignore]`: rationale references the
  now-deleted `StubTenantConnectionResolver` (stale premise — the unified resolver is
  unconditional since Phase 3); the `TenantId` columns themselves may still exist.

### 1.5 Story 28-13 (OpenBao) and the key seam

- `Tamma.Api/Services/Provisioning/TenantSecretProtector.cs` — AES-GCM, key from
  `Cranl:EncryptionKey` (base64 32 bytes), HKDF-from-ApiKey fallback strictly dev
  (`FromConfiguration`, R2-H11). Doc comment `:13,:36`: migrate to an `IKeyProtector` abstraction
  when 28-13 lands. **`IKeyProtector` does not exist anywhere in the codebase.**
- `docs/stories/epic-28/story-28-13/28-13-openbao-kms-backend.md` § "Trigger Conditions": first
  paying tenant with breach-notification clause / auditor finding / 10 paying tenants /
  threat-model change / OpenBao LF graduation. Story stays DEFERRED until one fires; the firing
  trigger must be recorded in the un-deferring commit.

### 1.6 Stale docs + blocked stories

- `docs/stories/epic-30/README.md` and ALL ten `30-*-impl-plan.md` headers say
  "**Status**: Planned (2026-04-20)" although 30-1/30-2/30-3/30-8 code shipped in wave-b.
- Epic 29 is "planning (briefs only)" and `ISecretStore` does not exist in code → Story 30-4
  (Hetzner, depends on 29-7) is hard-blocked; 30-5/30-6/30-7/30-10 are large greenfield items with
  no code. They are NOT in this plan's scope (see Non-goals).
- Full-suite baseline at main: **4575 passed** (wave-b merge). Tests run via
  `sg docker -c "dotnet test ..."` in `apps/tamma-elsa` (docker group note in memory).

---

## 2. Non-goals / explicitly out of scope

- **New backends**: Hetzner Cloud (30-4, blocked on Epic 29), Cloudflare Workers+D1 (30-5),
  BYO (30-6). The registry + capability matrix make these pure-additive later.
- **Onboarding UI** (30-7) and **cost/quota dashboard** (30-10).
- OpenBao **implementation** (28-13) — this plan only runs the trigger review and records the
  outcome; implementation happens only if a trigger fired, as its own plan.
- Billing, region failover, online tenant moves (parent plan §7).
- Per-user/per-tenant pluggable providers — providers stay platform-operator-wired (30-1 ADR).

---

## 3. Phase decomposition (each becomes its own task-plan)

### Phase A — Wave C: retire the V1 provisioner surface ✅ DONE (2026-06-29)

**Execution plan:** `docs/superpowers/plans/2026-06-29-epic-30-phase-a-v1-to-v2-cutover.md`
**Commits:** `c25cd980` → `ca4a3879` → `7678e794` → `d69c42bb` → `c9f2c353`
(branch `feat/epic-30-phase-a-v1v2-cutover`, off `origin/main f118e58d`)

- [x] **Task A1 — Null-deployment semantics decision + dispatcher gap.** `c25cd980`:
  `ProvisionTenantV2Dispatcher.DispatchAsync` null-provider branch short-circuits to
  `ProvisioningState.Ready` / detail `shared_infrastructure_no_backend_configured`. No enqueue.
  Matches V1 `NullTenantProvisioner` semantics under the unified model (schema minted at creation).
- [x] **Task A2 — Port admin endpoints to V2 + add deprovision path.** `ca4a3879` + `7678e794`:
  `ProvisionTenantV2TaskPayload` gains `Operation` discriminator (`Provision`/`Deprovision`);
  `DispatchDeprovisionAsync` added to the dispatcher (null → `Deprovisioned` no-enqueue; real →
  `Deprovisioning` + enqueue); `DeprovisionAsync` added to `ProvisionTenantV2Workflow`;
  handler branches on `payload.Operation`. `AdminEndpoints.cs:428-482` re-pointed to
  `ProvisionTenantV2Dispatcher` + `TenantProviderRegistry`; DTO / status codes / route
  templates unchanged; `ProvisioningAdminEndpointsTests` green on V2.
- [x] **Task A3 — Delete V1.** `d69c42bb`: `ITenantProvisioner`, `NullTenantProvisioner`,
  `CranlTenantProvisioner`, `TenantProvisioningTaskHandler`, V1 DI registrations and CS0618
  pragmas removed; V1 test files deleted; `ProvisioningModels.cs` V1 records cleaned up;
  `grep -rn "ITenantProvisioner\b" src tests` → 0 hits.

  **Deviation from this plan's original A3 scope:** `CranlProvisioningWorkflow.cs` was **kept**
  (not deleted). The plan originally called for deleting it along with the rest of the V1 surface.
  Code review found that deleting the engine would orphan the V2 Cranl path — the V2
  `CranlTenantProviderV2` delegates to the workflow, and there was no other implementation of the
  Cranl REST-walk. Instead (`c9f2c353`): `CranlProvisioningWorkflow` was retained and two new
  `IPlatformTaskHandler`s were wired — `CranlProvisionPlatformTaskHandler` (task type
  `provisioning.tenant`) and `CranlDeprovisionPlatformTaskHandler` (task type
  `provisioning.tenant.deprovision`) — so a Cranl-configured deployment completes end-to-end
  (project→db→app→Ready, and app→db→project teardown) rather than timing out to Failed. This
  makes the Cranl V2 path **functional in Phase A** rather than deferred to Phase B.

### Phase B — Reconcile V2 with the unified model: providers mint pool rows

This is decision 3 / deviation 22 made real. A `DatabaseOnly` provision = "mint a hosting DB,
register it in `tenant_databases`, move the tenant's schema onto it." Database routing NEVER flows
through provider-resolved raw URLs.

- [ ] **Task B1 — Narrow `V2TenantEndpointDirectory` to engine routing.** The directory stops
  returning `DatabaseUrl` (always `NotApplicable` for DB resolution; the LRU resolver's unified
  `EncryptedConnectionString` path is the only DB route — matching CLAUDE.md "Routing (current
  state)"). `TenantEndpoints.EngineUrl` remains for dedicated-compute engine dispatch.
  Tests first: directory tests assert ProviderKey-set tenants still resolve DB connections via the
  unified envelope (real-PG test through `LruPooledTenantConnectionResolver`), and engine-URL
  resolution still works for a fake dedicated-compute provider.
- [ ] **Task B2 — `CranlTenantProviderV2` registers the minted DB as a pool row.** On
  `database_ready`: parse the Cranl `DATABASE_URL`, create a `tenant_databases` row (Label
  `cranl-<tenant-slug>`, `PlacementClass='dedicated'`, encrypted admin conn via
  `ITenantConnectionStringProtector`, `Status='active'`), record provenance in
  `tenants.ProviderKey='cranl'` + `tenants.ProviderResourceIds` (already stamped at
  `CranlTenantProviderV2.cs:152`), then drive the existing `TenantMoveService` (Phase-4 machinery:
  draining → pg_dump -n t_<hex> → restore → re-point envelope → evict → drop source) to move the
  tenant onto it. **Analysis sub-task (blocking)**: verify the Cranl-minted credential can
  `CREATE SCHEMA`/`CREATE ROLE` on its DB; if not (no CREATEROLE), fall back to the documented
  shared-role-per-DB placement for that row and record the decision. Tests first: provider unit
  tests with a fake Cranl client asserting pool-row creation + move dispatch; env-gated e2e
  (`CRANL_API_KEY_TEST`) for the real walk.
- [ ] **Task B3 — Drop the legacy Cranl tenant columns + re-enable ignored test #1.** With B1/B2,
  `tenants.CranlDatabaseUrlEncrypted`/`CranlProjectId`/`CranlDatabaseId`/`CranlAppId`/`CranlAppUrl`/
  `CranlRegion` lose their last readers (resource ids live in `provider_resource_ids` JSONB;
  `CranlAppUrl` moves to `TenantEndpoints`/resource ids for engine routing). Keep
  `ProvisioningState`/`ProvisioningDetail`/`ProvisioningUpdatedAt` (still the saga's resume state)
  — **amend the ignored test's expectations accordingly and record the deviation** (the 2026-04
  test predates the 30-2 plain-orchestrator design). Un-`[Ignore]`
  `ControlPlaneDbContextModelTests.Tenants_Cranl_Columns_Are_Ignored_On_NewContext` (re-scoped),
  collapse-edit the CP baseline per the C1 four-artifact procedure, prove
  `has-pending-model-changes` clean + baseline applies on throwaway Postgres.

### Phase C — Tighten the transitional CHECKs (parent deviations 6–7)

- [ ] **Task C1 — `ck_tenants_connection_string_present` → near-spec form.** Mint-at-creation is
  live, so `provisioning` no longer needs the exemption. Delete flows DO null the envelope, so the
  honest target (analysis task first — enumerate every writer of `Status` +
  `EncryptedConnectionString = null`: `MarkTenantDeletingActivity`,
  `AdminTenantsEndpoints.ForceDeleteTenant`, purge path) is expected to be:
  ```sql
  CONSTRAINT ck_tenants_connection_string_present CHECK (
    "Status" IS NULL
    OR "Status" IN ('pending_verification','deleting','deleted')
    OR "EncryptedConnectionString" IS NOT NULL)
  ```
  with `failed`/`delete_requested`/`provisioning` dropped from the exemption list (a failed or
  delete-requested tenant under the unified model HAS an envelope — it was minted at creation). If
  the writer audit finds a flow that legitimately nulls earlier, keep the minimal exemption and
  record it as a deviation. Tests first: real-PG tests asserting (a) `active` + NULL envelope →
  23514, (b) `failed`/`delete_requested` + NULL envelope → 23514 (new), (c) the
  force-delete-from-failed path still completes (it now carries its envelope into
  deleting/deleted). Four-artifact C1 edit procedure + `has-pending-model-changes` + throwaway-PG
  apply.
- [ ] **Task C2 — `ck_api_keys_scope` tightening + tenant-key relocation.** Two sub-decisions,
  analysis first:
  1. `'tenant'`-scoped keys move out of CP: `OrgApiKeysEndpoints.cs:53,72` writes into the
     tenant store's `api_keys` (table + `Scope='tenant'` CHECK already exist);
     `ApiKeyAuthHandler` routes lookup by key-prefix → CP first, then active-tenant store (or a
     prefix discriminator — analysis decides; auth hot path, benchmark before/after).
  2. `'installation'`/`'service'` are platform-plane keys (GitHub App installation router, service
     automation) — they have no tenant store to move to. Proposed: amend the spec enumeration
     rather than relocate, landing:
  ```sql
  CONSTRAINT ck_api_keys_scope CHECK ("Scope" IN ('platform','user','installation','service'))
  ```
  i.e. `'tenant'` removed from CP. If the analysis instead justifies full spec
  `('platform','user')`, fold installation/service into `platform` ownership semantics — decision
  recorded either way in the execution record + parent-plan deviation note. Tests first: org
  api-key CRUD round-trip against the tenant store, CP insert with `Scope='tenant'` → 23514,
  auth-handler resolution for both stores, four-artifact CHECK edit + PG apply.

### Phase D — Story 28-1 closeout: per-tenant Elsa runner

- [ ] **Task D1 — Decide the Elsa topology under the unified model (blocking decision).** The
  stub predates schema-per-tenant. Options: (a) per-tenant Elsa schema `t_<hex>_elsa` in the
  tenant's pool DB, migrated by a real `MigrateTenantElsaAsync` (Elsa's EF migrations against a
  Search-Path-scoped connection); (b) Elsa stays global except for dedicated-compute tenants,
  where the per-tenant engine (Cranl App) owns Elsa tables in the tenant's hosting DB — the stub
  becomes real only on the `DedicatedCompute` topology path; (c) formally close 28-1's residual as
  "superseded by unified model" and delete the stub + interface member. Record in
  `.dev/decisions/`. Current evidence: the stub has zero callers — (b) or (c) are the likely
  outcomes; do NOT implement (a) without a consumer.
- [ ] **Task D2 — Implement the decision.** If (b): wire `MigrateTenantElsaAsync` into the
  dedicated-compute provision saga (between `database_ready` and `app_provisioning`), embed the
  Elsa migration-assembly hash + fail-fast on drift (28-5 plan §9 open question), env-gated
  runtime verification e2e (real Cranl creds: engine boots, Elsa tables present, workflow executes
  in the tenant engine). If (c): delete `ITenantDbMigrator.MigrateTenantElsaAsync` +
  `EfTenantDbMigrator.cs:104-115`, update Story 28-1 doc to DONE-with-decision. Either way:
  re-evaluate ignored test #2
  (`TenantDbContextModelTests.Tenant_Resident_Entities_Have_No_TenantId_Column:59`) — its stale
  StubTenantConnectionResolver premise is gone; either drop the `TenantId` columns from
  tenant-resident tables (schema isolation makes them redundant; baseline regen) and re-enable, or
  re-scope the `[Ignore]` text to a true remaining blocker. Tests first per the chosen branch.

### Phase E — Story 28-13 trigger review + docs closeout

- [ ] **Task E1 — OpenBao trigger review.** Walk the five §"Trigger Conditions" checkboxes against
  2026-06-11 reality (paying tenants: zero → triggers 1/3 not fired; check OpenBao LF graduation
  status via WebSearch; auditor/threat-model: none). Record the dated outcome (expected: "still
  deferred — no trigger fired") in the story doc + `.dev/decisions/`. **No code** unless a trigger
  fired; if one fired, that spawns its own plan (incl. the `IKeyProtector` seam extraction from
  `TenantSecretProtector`).
- [ ] **Task E2 — Documentation truth-up.** Update `docs/stories/epic-30/README.md` + the ten
  impl-plan Status headers (30-1/2/3/8 → DONE-in-wave-b with commit refs; 30-9 → partially
  delivered by Phase A deprovision dispatch; 30-4/5/6/7/10 → deferred with blockers named);
  CLAUDE.md "Multi-tenant provisioning" section (V1 `NullTenantProvisioner` reference → V2 seam,
  `cranl_database_url_encrypted` sentence → pool-row registration); parent plan deviations section
  gains the Phase C final CHECK forms; memory file updates; execution record per phase.

---

## 4. Risks

| Risk | Mitigation |
|---|---|
| Admin API contract drift during the V1→V2 endpoint swap breaks the dashboard | Shared `ProvisioningState` enum + unchanged `TenantProvisioningResponse`; endpoint tests pinned BEFORE the swap (Task A2 is test-first against the existing contract). |
| B2's Cranl-minted credential lacks CREATEROLE → role-per-tenant impossible on that DB | Blocking analysis sub-task in B2; documented fallback (shared role per dedicated DB — still single-tenant DB, isolation preserved by topology) recorded as a deviation. |
| Tightened conn-string CHECK trips a delete/purge flow not found in the writer audit (repeat of the original 23514 bug) | C1 writer audit enumerates ALL `Status` writers via grep before choosing the form; real-PG tests exercise force-delete-from-failed end-to-end; exemptions only removed with a passing test proving the flow. |
| Auth hot-path regression from dual-store api-key lookup (C2) | Prefix-discriminated routing (single store hit per lookup) preferred; micro-benchmark in the C2 task-plan; CP lookup order preserved for platform keys. |
| Four-artifact CHECK mirroring drifts (baseline vs snapshot vs designer vs model) | C1 procedure is mandatory per task; `has-pending-model-changes` + throwaway-Postgres baseline apply are phase gates (same as Phases 0/4 of the parent plan). |
| Cranl e2e unverifiable in CI (no live Cranl) | All Cranl-real paths env-gated (`CRANL_API_KEY_TEST`); fake-client unit tests carry the contract; runtime verification recorded as done/blocked explicitly in the execution record — never silently skipped. |
| Deleting V1 loses behavior only its tests covered | Task A3 mandates a test-diff (CranlProvisioningWorkflowTests vs ProvisionTenantV2WorkflowTests) and porting gaps BEFORE deletion. |

---

## 5. Acceptance criteria

1. **One provisioning surface.** `ITenantProvisioner`, `NullTenantProvisioner`,
   `CranlTenantProvisioner`, `CranlProvisioningWorkflow`, `TenantProvisioningTaskHandler` are
   deleted; `grep -rn "ITenantProvisioner\b" apps/tamma-elsa/src apps/tamma-elsa/tests` → 0 hits;
   admin provision/status/deprovision endpoints ride `ProvisionTenantV2Dispatcher` /
   `TenantProviderRegistry`; no-backend deployments still mark tenants Ready immediately.
2. **Providers mint pool rows; unified model owns routing.** A Cranl `DatabaseOnly` provision ends
   with a `tenant_databases` row + the tenant's schema moved onto it + the tenant resolving through
   its own AES-GCM Search-Path envelope; `V2TenantEndpointDirectory` never supplies a raw
   `DatabaseUrl` to the LRU resolver; legacy `Cranl*` tenant columns are gone from the model and
   baseline.
3. **CHECKs tightened** (four artifacts each, `has-pending-model-changes` clean, baseline applies
   on throwaway Postgres). Current transitional forms being replaced:
   - `ck_api_keys_scope`: `"Scope" IN ('platform','user','installation','service','tenant')`
     → final form per Task C2 decision (proposed:
     `"Scope" IN ('platform','user','installation','service')`, `'tenant'` keys relocated to the
     tenant store), decision recorded.
   - `ck_tenants_connection_string_present`:
     `"Status" IS NULL OR "Status" IN ('pending_verification','provisioning','failed','deleted','deleting','delete_requested') OR "EncryptedConnectionString" IS NOT NULL`
     → final form per Task C1 audit (proposed exemptions only
     `pending_verification`,`deleting`,`deleted`), with real-PG tests proving `active`/`failed`/
     `delete_requested` + NULL envelope are rejected and the force-delete path still works.
4. **28-1 closed.** `MigrateTenantElsaAsync` is either a real, runtime-verified (env-gated e2e)
   step of the dedicated-compute saga, or deleted with a recorded supersession decision; Story
   28-1 doc no longer says "MOSTLY DONE"; both Epic-30-gated `[Ignore]` tests are re-enabled or
   re-scoped with accurate, current rationale (no stale StubTenantConnectionResolver references).
5. **28-13 reviewed.** Dated trigger-review outcome recorded in the story doc +
   `.dev/decisions/`; if a trigger fired, a follow-up plan exists.
6. **Suite + docs.** Full suite ≥ baseline 4575 green (plus new tests); Epic 30 README/impl-plan
   statuses, CLAUDE.md provisioning/routing sections, and the parent plan's deviation list reflect
   the end state; per-phase execution records written.

---

## Self-review notes

- Deliberately narrower than the 2026-04-20 story map: 30-4/5/6/7/10 stay deferred (Epic 29
  blocker + no code), so this plan finishes the *seam* — the thing the unified-tenancy work left
  as "loose threads" — rather than shipping four backends. New backends become pure-additive
  `AddTenantProvider*` registrations afterward.
- Highest-blast-radius items: Phase C2 (auth hot path) and Phase B1 (resolver routing). Both gate
  behind real-PG integration tests and land after Phase A proves the V2 path under the existing
  test contract.
- Open question routed to Task D1 on purpose: the per-tenant Elsa story only makes sense for
  dedicated compute, and the stub has zero callers today — implementing before a consumer exists
  would violate the "don't ship the wrong default" rule.
