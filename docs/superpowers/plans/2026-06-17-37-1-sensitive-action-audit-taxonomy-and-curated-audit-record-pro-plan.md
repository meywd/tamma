# Plan — Story 37-1: Sensitive-Action Audit Taxonomy & Curated Audit-Record Projection

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development for every
> phase below — this is a test-first (TDD) project; write the failing test before the implementation.
> Use superpowers:executing-plans (or subagent-driven-development) to work the phases in order.
> Steps use checkbox (`- [ ]`) syntax for tracking.
>
> Story file: `docs/stories/epic-37/story-37-1/37-1-sensitive-action-audit-taxonomy-and-curated-audit-record-pro.md`
> MANDATORY first read: `docs/guides/BEFORE_YOU_CODE.md` + `.dev/{spikes,bugs,findings,decisions}/`.

**Goal:** Define the canonical catalog of compliance-relevant sensitive actions and build a curated,
queryable `audit_records` read-model materialized FROM the existing Epic 4 DCB event stream — a
read-optimized product layer ON TOP OF the event store (not a replacement). Tenant-scope rows in
the per-tenant schema, platform-scope rows in the control plane, per-mode ownership (`user_id` in
single-user, `tenant_id` in SaaS), redacted payloads, idempotent + replayable from a sequence
cursor, with the tamper-evidence hook (deterministic order + reserved hash columns) that Story 37-2
will chain.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests are xUnit under `apps/tamma-elsa/tests/Tamma.Api.Tests/` (+ `Tamma.Data.Tests`); docker-bound
suites run via `sg docker -c "dotnet test ..."` (session docker group is stale — see project
memory `reference_dotnet_test_docker`).

---

## Non-goals (YAGNI guard)

- **NO new event store.** Raw `DomainEvent` / `PlatformEvent` rows remain the immutable source of
  truth. We READ them and project into `audit_records`. We never append/mutate/delete raw events
  (story AC15). If `audit_records` is wrong, the fix is truncate-and-replay, never patch.
- **NO re-emitting existing events.** `SECRET.*`, `IMPERSONATION.*`, `USER.ROLE_CHANGED.SUCCESS`,
  `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` are already emitted today; the catalog MAPS them. We add no
  new emit call-sites for already-emitted types in this story.
- **NO query/search/export API.** That is Story 37-10. This story ships only catalog + projection +
  tables.
- **NO hash chaining / tamper-evidence computation.** That is Story 37-2. We ship the deterministic
  insertion order + the reserved nullable `record_hash` / `prev_record_hash` columns, left null.
- **NO per-request hot-path coupling.** Projection is eventual (background cursor pass), never on
  the request path (story AC9). A broken projector must not slow or fail a live action.
- **NO change to resolution/emit semantics anywhere.** Detection is read-only over events already
  flowing.
- **NO targeting `packages/api`.** It is DELETED. Target `apps/tamma-elsa` only.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### The DCB substrate (READ source — do not modify the write path)

| Artifact | Verified path | Notes |
|---|---|---|
| `DomainEvent` (tenant + CP DbSet) | `src/Tamma.Data/Entities/DomainEvent.cs` | `Id, Type, TenantId?, IssueNumber?, Tags(JSONB), Metadata, Data, CreatedAt, SequenceNumber(BIGSERIAL)`. `SequenceNumber` is the total-order cursor — the doc-comment explicitly names `AlertRuleEvaluator` as the precedent consumer. |
| `PlatformEvent` (CP) | `src/Tamma.Data/Entities/PlatformEvent.cs` | Same shape + `UserId?`. Cross-tenant / platform-only events (`TENANT.*`, `ORCHESTRATOR.TICK.*`, pre-resolution installs). `TenantId` null = platform-only. |
| `IEventRepository` / `EventRepository` | `src/Tamma.Data/Repositories/{IEventRepository,EventRepository}.cs` | `AppendAsync`, `QueryAsync`, `QueryWithPaginationAsync`, `ListByTenantAsync` (tenant audit, prefix match, global-query-filter defence-in-depth). Use the READ methods only. |
| `TenantDbContext` | `src/Tamma.Data/TenantDbContext.cs` | Per-tenant schema. `DomainEvents` DbSet lives HERE → tenant `audit_records` go here. |
| `ControlPlaneDbContext` | `src/Tamma.Data/ControlPlaneDbContext.cs` | CP context. Has both `DomainEvents` AND `PlatformEvents` DbSets → platform `audit_records` go here. |
| `TammaModelConfiguration` | `src/Tamma.Data/TammaModelConfiguration.cs` | Single source for entity config across both contexts. New `AuditRecord` config goes here. |
| Migrations | `src/Tamma.Data/Migrations/{Tenant,ControlPlane}/` | Both have separate migration trees — `audit_records` needs one in each. |

### The cursor-projection precedent to clone (do NOT invent a new mechanism)

- `src/Tamma.Api/Services/Alerts/Rules/AlertRuleEvaluator.cs` — background poller that reads new
  `DomainEvent` + `PlatformEvent` rows BY `SequenceNumber`, persists progress, resumes on restart,
  crash-isolates per tick. Cursor crash-safety + dual-stream (`cp.DomainEvents` + `cp.PlatformEvents`)
  fetch + `LoadCursorAsync`/`SaveCursorAsync` are all there — the `AuditProjector` is structurally a
  near-clone with a different "what to do per matched event" body.
- `AlertEvaluatorCursor` (`src/Tamma.Data/Entities/AlertEvaluatorCursor.cs`) — `EvaluatorId`,
  `LastDomainSequenceNumber`, `LastPlatformSequenceNumber`, `UpdatedAt`. Copy this shape into
  `AuditProjectorCursor`.
- Background-service options precedent: `AlertRuleEvaluatorOptions` / `NotificationDispatcherOptions`
  have a `RunOnStartup` gate (so unrelated tests can disable the loop) — mirror for `AuditProjectorOptions`.
- Other `BackgroundService` hosts to match the registration style: `TaskQueueProcessor`,
  `NotificationDispatcher`, `RevealTokenSweeper`, `PlatformTaskWorker`.

### Existing emitters the catalog must MAP (verified — not re-emitted)

| Family | Verified source | Constants/codes |
|---|---|---|
| SECRET | `src/Tamma.Api/Services/Secrets/ISecretAccessAuditor.cs` → `SecretAuditEventTypes` | `SECRET.READ/WRITE/REVEAL/ROTATE.{STARTED,SUCCESS,FAILED}/VERSION.REVOKED/MIGRATED.*` |
| IMPERSONATION | `src/Tamma.Api/Endpoints/Admin/AdminImpersonationsEndpoints.cs` | `IMPERSONATION.STARTED`, `IMPERSONATION.ENDED` (platform events) |
| RBAC (platform role) | `src/Tamma.Api/Endpoints/AdminEndpoints.cs` (~215) | `USER.ROLE_CHANGED.SUCCESS` |
| RBAC (tenant role) | `src/Tamma.Api/Endpoints/OrgEndpoints.cs` (~227) | `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` (+ member add/remove, invite emitters in same file) |

> The catalog-completeness test reflects over `SecretAuditEventTypes` and asserts each is present in
> `SensitiveActionCatalog.ByCode`, so renaming/dropping an emitter without updating the catalog fails CI.

### Redaction + mode seams (reuse)

- `src/Tamma.Core/Redaction/CredentialRedactor.cs` — `public static string Clean(string?)`,
  `Placeholder = "[REDACTED]"`; regexes for bearer tokens, `key=value` assignments, `tamma_sk_`
  prefix, URL basic-auth, control chars. Use for `payload_json` before persist.
- `src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (SingleUser | SaaS),
  process-stable. Drives per-mode ownership key selection.
- Per-mode XOR precedent: `prompt_overrides` `principal_xor` CHECK + `UNIQUE NULLS NOT DISTINCT`
  (CLAUDE.md "Prompt Store Architecture" / "Storage").

### Auth/policy seams (for later stories, noted now)

- `OwnerAccess` / `PlatformOwnerAccess` policies registered in `src/Tamma.Api/Program.cs` (~971/986);
  tenant scoping via `RequireTenantMembershipFilter` (`src/Tamma.Api/Authorization/`). Not needed in
  37-1 (no endpoint), but 37-10 will use these.

### Per-mode ownership — the mandatory two-scoping-model answer (per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Who owns a curated **tenant-scoped** action record? | The sole user — keyed `user_id`, `tenant_id` NULL (no tenant dimension exists). | The tenant — keyed `tenant_id`, `user_id` NULL; lands in the tenant schema. |
| Where does a **platform-scoped** event (TenantId null) materialize? | Single-user store, keyed `user_id`. | Control-plane `audit_records`, `tenant_id` null (platform row, never in a tenant's view). |
| XOR invariant | `user_id` set / `tenant_id` null. | `tenant_id` set / `user_id` null. CHECK rejects both/neither. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`). | same. |

---

## Architecture (detection-free; pure projection)

**Raw DCB events → cursor scan → catalog match → classify + redact + key by mode → insert-if-absent
into `audit_records`.** Mirrors `AlertRuleEvaluator` end-to-end for the cursor/background mechanics.

1. **`SensitiveActionCatalog`** (`Tamma.Core/Audit/`) — pure const-class + immutable `ByCode` lookup
   (mirrors `SecretAuditEventTypes`). The single source of "is this sensitive + how is it classified"
   (category, severity, SOC2 control, target-type hint).
2. **`AuditRecord`** entity + `audit_records` table in BOTH `TenantDbContext` (tenant schema) and
   `ControlPlaneDbContext` (platform). XOR CHECK on (`user_id`,`tenant_id`), UNIQUE on
   `source_event_id` (idempotency), `source_sequence_number` index (replay order / 37-2 chain),
   reserved nullable `record_hash`/`prev_record_hash`.
3. **`AuditProjectorCursor`** entity (clone of `AlertEvaluatorCursor`) — `LastDomainSequenceNumber`
   (tenant) + `LastPlatformSequenceNumber` (platform).
4. **`AuditProjector`** — cursor-tracked, idempotent, redacting projection. Skips non-catalog events,
   inserts exactly one row per matched event in strict `SequenceNumber` order.
5. **`AuditProjectorBackgroundService`** — `BackgroundService` host (poll interval + `RunOnStartup`
   gate + crash-isolation per tick) + `AuditProjectionMetrics` (`tamma.audit.projection_lag` gauge).
6. **DI:** `AuditServiceCollectionExtensions.AddTammaAudit(...)` wired in `Program.cs`.

---

## Phased TDD tasks

### Phase 0 — Read & confirm (no code)

- [ ] Read `BEFORE_YOU_CODE.md` + scan `.dev/{spikes,bugs,findings,decisions}/` for prior audit/event
  projection work.
- [ ] Read `AlertRuleEvaluator.cs` + `AlertEvaluatorCursor.cs` fully — confirm the cursor read +
  dual-stream fetch + crash-isolation contract the projector will clone.
- [ ] Read `SecretAuditEventTypes` (`ISecretAccessAuditor.cs`) — confirm const-class shape to mirror.
- [ ] Confirm `dotnet build` is green at HEAD before starting (baseline).

### Phase 1 — Taxonomy catalog (`Tamma.Core`, pure, no DB) — story AC1–AC3, AC13a

- [ ] **RED:** `tests/Tamma.Api.Tests/Audit/SensitiveActionCatalogTests.cs` — assert ≥30 codes, all
  11 `AuditCategory` values populated, every descriptor has a non-empty SOC2 control id, and every
  constant in `SecretAuditEventTypes` (+ `IMPERSONATION.STARTED/ENDED`, `USER.ROLE_CHANGED.SUCCESS`,
  `TENANT.MEMBER_ROLE_CHANGED.SUCCESS`) is a key in `SensitiveActionCatalog.ByCode`.
- [ ] **GREEN:** add `AuditCategory.cs`, `AuditSeverity.cs`, `SensitiveActionDescriptor.cs`,
  `SensitiveActionCatalog.cs` under `src/Tamma.Core/Audit/`. Populate `ByCode`.
- [ ] **REFACTOR:** ensure `IsSensitive(eventType)` + `ByCode` are the only lookup path.

### Phase 2 — `AuditRecord` + `AuditProjectorCursor` entities, EF config, migrations — AC4–AC6, AC12

- [ ] **RED:** `tests/Tamma.Data.Tests/Audit/AuditRecordModelTests.cs` — XOR CHECK rejects both/neither
  set; UNIQUE `source_event_id` rejects a duplicate; tenant global query filter scopes reads;
  `has-pending-model-changes` → none (model-config completeness test).
- [ ] **GREEN:** add `AuditRecord.cs` + `AuditProjectorCursor.cs` entities; register DbSets in both
  contexts; add entity config (CHECK, unique index, `SourceSequenceNumber` index, query filter,
  reserved hash columns) in `TammaModelConfiguration.cs`.
- [ ] `dotnet ef migrations add AddAuditRecords` for BOTH Tenant + ControlPlane trees; verify
  `has-pending-model-changes` reports none; apply + roll back cleanly (docker-bound → `sg docker -c`).

### Phase 3 — `AuditRecordRepository` (insert-if-absent + cursor) — AC8 plumbing

- [ ] **RED:** `tests/Tamma.Data.Tests/Audit/AuditRecordRepositoryTests.cs` — `InsertIfAbsentAsync`
  returns true on first insert, false (no second row) on duplicate `source_event_id`; cursor
  load/save round-trips.
- [ ] **GREEN:** `IAuditRecordRepository` + `AuditRecordRepository` over the appropriate context
  (tenant vs CP selected by caller/mode). Insert-if-absent via unique-index catch (mirror
  `prompt_overrides` upsert handling).

### Phase 4 — `AuditProjector` (classify + redact + key by mode + skip) — AC7, AC8, AC10, AC11, AC15

- [ ] **RED:** `tests/Tamma.Api.Tests/Audit/AuditProjectorTests.cs`:
  - idempotency — run twice over a mixed batch → one row per catalog match, zero for non-catalog;
    truncate + cursor 0 → identical replay.
  - non-catalog skip — `WORKFLOW.STEP_COMPLETED` → zero rows.
  - per-mode key — SaaS tenant event → `tenant_id` set; single-user → `user_id` set.
  - redaction — `SECRET.WRITE` with fake `tamma_sk_`/`Bearer`/`password=` in `Data` → `payload_json`
    has `[REDACTED]`, never plaintext.
  - no-write-path — spy `IEventRepository` asserts only read methods called (no append/mutate/delete).
- [ ] **GREEN:** `IAuditProjector` + `AuditProjector` — `ProjectBatchAsync`: load cursor → read new
  events ordered by `SequenceNumber` → for each, `SensitiveActionCatalog.ByCode.TryGetValue` (skip
  miss) → `BuildAuditRecord` (map actor/target/outcome from `Tags`+`Data`) → `CredentialRedactor.Clean`
  payload → `AssignOwnership` by `_mode.Current` → `InsertIfAbsentAsync` → save cursor. Strict
  `SequenceNumber` order (37-2 needs it).
- [ ] **REFACTOR:** extract `BuildAuditRecord` / `AssignOwnership` / `ProjectPayload` as testable units.

### Phase 5 — Background host + lag metric + scope isolation — AC9, AC14

- [ ] **RED:** `tests/Tamma.Api.Tests/Audit/AuditRecordScopeIsolationTests.cs` — tenant-A event never
  in tenant-B schema; tenant-scoped event never in CP `audit_records` (and vice-versa); tenant global
  filter rejects cross-tenant read. Plus a lag-metric test (M un-projected → gauge M; after pass → 0)
  and a `RunOnStartup=false` gating test.
- [ ] **GREEN:** `AuditProjectorBackgroundService` (poll loop + `RunOnStartup` gate + per-tick
  crash-isolation, cloning `AlertRuleEvaluator`); `AuditProjectorOptions`; `AuditProjectionMetrics`
  (OTel gauge `tamma.audit.projection_lag`). Tenant fan-out per active tenant context; platform reads
  CP `PlatformEvents`.

### Phase 6 — DI wiring + full suite green — AC6, AC13, AC15

- [ ] **GREEN:** `AuditServiceCollectionExtensions.AddTammaAudit(...)`; wire in `Program.cs` (mirror
  `AlertServiceCollectionExtensions`). Register repo, projector, background service, metrics.
- [ ] Run the full xUnit suite (`sg docker -c "dotnet test ..."`) — green; `has-pending-model-changes`
  → none; `dotnet build` warning-clean for new files.

---

## Sequencing

Phase 0 → 1 → 2 → 3 → 4 → 5 → 6, strictly in order (each phase's tests depend on the prior phase's
types). Phase 1 (catalog) is the only phase with zero DB dependency and can be reviewed/merged on its
own if the wave is split. Phases 2–3 are pure data layer; 4–5 are the projection engine; 6 is wiring.

---

## Risks

- **"Rebuild the event store" temptation.** Highest-impact failure mode. Mitigation: the projector is
  read-only over `DomainEvent`/`PlatformEvent`; AC15's no-write-path test is load-bearing; `audit_records`
  is rebuildable (truncate + cursor 0). Keep the back-reference (`source_event_id`/`source_sequence_number`)
  on every row.
- **Scope-derivation bug (tenant vs platform vs single-user).** A platform-internal event leaking into
  a tenant's audit view is a confidentiality breach. Mitigation: AC11/AC13c/AC14 pin the matrix; route
  on raw `TenantId` AND `_mode.Current`; tenant global query filter as defence-in-depth (same as
  `EventRepository.ListByTenantAsync`).
- **Un-redacted payload leak.** `payload_json` is the only field that can carry secrets. Mitigation:
  redact BEFORE persist (never on read); AC10 test; ERROR-and-skip (don't advance cursor) if the
  redactor throws — never persist an un-redacted payload.
- **Idempotency under concurrency / replay.** Mitigation: UNIQUE `source_event_id` + insert-if-absent
  (catch unique violation → treat as already-projected). Cursor crash mid-batch may re-scan — the
  unique index makes re-scan a no-op (same guarantee `AlertRuleEvaluator` relies on for its sink-side
  dedup).
- **Catalog drift from emitters.** A future rename of an emitted type silently drops it from the audit
  trail. Mitigation: catalog-completeness reflection test (AC13a) over `SecretAuditEventTypes` fails CI.
- **Event-store topology shift (Story 28-1 / Epic 30).** Tenant events are moving per-tenant. Mitigation:
  read tenant events via the tenant context and platform events via CP explicitly (don't assume one
  table); the projector's tenant fan-out mirrors the direction `AlertRuleEvaluator`'s doc-comment
  describes for its own migration.
- **Migration discipline.** `audit_records` is additive on both trees, but a missed `TammaModelConfiguration`
  entry leaves `has-pending-model-changes` dirty. Mitigation: config in `TammaModelConfiguration.cs`
  only; verify pending-changes → none in Phase 2 and again in Phase 6.
- **Hot-path coupling regression.** If projection ever runs inline, a live action could block on it.
  Mitigation: projection is ONLY in the background service; no projector call from any request handler
  (AC9). No DI of `IAuditProjector` into endpoint code.

## Acceptance criteria (plan-level — maps to story ACs)

- [ ] `SensitiveActionCatalog` ships ≥30 codes across all 11 categories, each with severity + SOC2
  control; catalog-completeness test green over existing emitters (story AC1–3, AC13a).
- [ ] `audit_records` exists in BOTH tenant schema and control plane with the full column set + XOR
  CHECK + unique `source_event_id` + reserved hash columns; migrations apply/rollback; `has-pending-model-changes`
  → none (story AC4–6, AC12).
- [ ] `AuditProjector` materializes exactly one redacted row per catalog-matched event, skips
  non-catalog, keys by mode, routes tenant vs platform correctly, is idempotent/replayable, and never
  writes to the raw event store (story AC7–8, AC10–11, AC15).
- [ ] Background projection is eventual + non-blocking with a working lag gauge and `RunOnStartup`
  gate (story AC9).
- [ ] Cross-scope isolation holds (no cross-tenant / no tenant↔platform bleed) (story AC14).
- [ ] Full xUnit suite green via `sg docker -c "dotnet test ..."`; `dotnet build` clean.
