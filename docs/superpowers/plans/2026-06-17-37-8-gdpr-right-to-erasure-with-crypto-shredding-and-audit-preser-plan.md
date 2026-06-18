# Story 37-8 — GDPR Right-to-Erasure with Crypto-Shredding & Audit Preservation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Read [BEFORE_YOU_CODE.md](../../guides/BEFORE_YOU_CODE.md) first.

**Goal:** On a verified right-to-erasure request, irreversibly remove or anonymize a data
subject's personal data across the control-plane and that subject's tenant schema, using
**crypto-shredding** (destroying the subject's envelope-encryption key version in the Epic 29
cabinet) for envelope-encrypted PII, while **preserving the tamper-evident audit chain** (37-2 —
audit identity fields are anonymized + re-anchored, never deleted) and **respecting active legal
holds** (37-6). Reconcile with the existing `TENANT.PURGED` lifecycle so subject erasure and tenant
purge share machinery.

**Story file:** `docs/stories/epic-37/story-37-8/37-8-gdpr-right-to-erasure-with-crypto-shredding-and-audit-preser.md`

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`; build needs no wrapper). **`packages/api` is deleted — never a
target.**

---

## Non-goals (YAGNI guard)

- **NO mutation of the append-only event store.** `DomainEvent`
  (`src/Tamma.Data/Entities/DomainEvent.cs`) stays immutable — its `BIGSERIAL SequenceNumber` is the
  `AlertRuleEvaluator` cursor. PII in events is killed by crypto-shred + curated-audit anonymization,
  never by row edits/deletes.
- **NO new key hierarchy.** The Epic 29 per-version envelope (`SecretVersionRow.KekId` + per-version
  ciphertext + `revoked` status) is the per-subject shred granularity. A subject-scoped DEK scheme
  is a future Epic 29 story, not this one.
- **NO UI.** Admin/tenant dashboard surfaces for erasure status are Story 37-12.
- **NO subject-discovery crawler.** Erasure walks the 37-7 `SubjectDataMap`; finding subjects is
  37-7's job.
- **NO sub-processor erasure fan-out.** Third-party processor erasure is a documented manual runbook
  step, not code here.
- **NO change to resolution/auth semantics** beyond adding the erasure routes + their RBAC gates.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Crypto-shred substrate already exists (Epic 29) — reuse, don't reinvent

| Seam | File | What it gives erasure |
|---|---|---|
| `SecretVersionRow` | `src/Tamma.Data/Entities/SecretVersionRow.cs` | Per-version AES-256-GCM envelope, **per-version `KekId`**, `Status` incl. `revoked`, nullable `Ciphertext` ("Null when the version row has been scrubbed ... the row is retained so audit queries still see that a version existed"). |
| `ISecretStoreBackend.DeleteVersionAsync` | `src/Tamma.Api/Services/Secrets/ISecretStoreBackend.cs` | "Scrub the ciphertext for a version (the version row is kept for audit history; only the bytes are zeroed). **Idempotent**." — this is crypto-shred. |
| `ISecretStore.RetireVersionAsync` | `src/Tamma.Api/Services/Secrets/ISecretStore.cs` | "Force-revoke a specific version: scrub the ciphertext, flip status to Revoked." |
| `GetVersionPlaintextAsync` | `ISecretStoreBackend` | "Returns null when the version row exists but its ciphertext has been scrubbed" — the verification probe for AC9. |

### Append-only + immutability constraint

- `DomainEvent` (`src/Tamma.Data/Entities/DomainEvent.cs`): no update path; `SequenceNumber`
  `BIGSERIAL` total-order cursor consumed by `AlertRuleEvaluator`. **Hard constraint: erasure must
  not UPDATE/DELETE this table** (AC4).

### Tenant-purge machinery to reconcile with

- `src/Tamma.Activities/TenantLifecycle/TenantLifecycleEvents.cs` — `TENANT.DELETE.*` /
  `TENANT.DELETED.SUCCESS` constants + `BuildEvent(...)` helper (the canonical
  `AGGREGATE.ACTION.STATUS` event shape: `tenantId` tag, `eventSource=system` metadata). Subject
  erasure mirrors this shape for `GDPR.ERASURE.*` and shares the secret-scrub primitive a full purge
  uses.

### PII surface (the things erasure destroys)

- `src/Tamma.Data/Entities/User.cs` — `Email` (NOT NULL, case-insensitive unique index on
  `LOWER(email) WHERE deleted_at IS NULL`), `DisplayName`, `AvatarUrl`, `GitHubLogin`,
  `PasswordHash`, `Settings` (JSON), soft-delete `DeletedAt`. Anonymizable fields ⇒ stable tombstone;
  `PasswordHash`/tokens ⇒ hard delete.

### Async + RBAC + events plumbing (all exist)

- **Async:** `IPlatformQueuedTaskRepository` (`src/Tamma.Data/Repositories/IPlatformQueuedTaskRepository.cs`)
  for platform-scope tasks; processed by `src/Tamma.Api/Services/TaskQueue/TaskQueueProcessor.cs`.
  (`ITaskQueue` is tenant-scoped-only by Story 28-1 — use the platform repo for `GDPR_ERASURE`.)
- **Events:** `IEventRepository.AppendAsync(DomainEvent)`
  (`src/Tamma.Data/Repositories/IEventRepository.cs`).
- **RBAC:** `Program.cs` ~971 `OwnerAccess` (tenant-role, `users:manage`), ~986
  `PlatformOwnerAccess` (`platform_admin` claim). Tenant-route precedent:
  `OrgEndpoints.ReprovisionOrg` (~859) — `RequireTenantMembershipFilter` (cross-tenant 404) +
  `RoleAtLeast(httpContext, TenantRoleHierarchy.Admin)`; tighten to owner for erasure.
- **Mode:** `ITammaModeProvider` (`src/Tamma.Api/Services/PromptStore/TammaMode.cs`).

### NEW dependencies — not yet authored at draft time (mock until they land)

| Dep | Provides | Status |
|---|---|---|
| 37-2 | audit hash chain, `VerifyChainAsync`, re-anchor seam (`IAuditChainAnonymizer`) | **NEW** — empty `docs/stories/epic-37/story-37-2/` |
| 37-6 | `ILegalHoldService` — is subject/record under active hold? | **NEW** — empty `docs/stories/epic-37/story-37-6/` |
| 37-7 | `SubjectDataMap` + `SubjectRef` + per-field `ErasurePolicy` | **NEW** — empty `docs/stories/epic-37/story-37-7/` |

This story defines local stub interfaces for these in `Services/Compliance/`, mocks them in tests,
and consumes the real implementations once their stories ship.

---

## Architecture

**request → enqueue → executor walks map → per-class destroy → audit-of-erasure**, reusing the
secret cabinet for irreversibility and the platform task queue for async:

1. **Endpoint** (`OrgEndpoints` tenant + `AdminTenantsEndpoints` platform): RBAC + scope check →
   generate `erasureRequestId` → append `GDPR.ERASURE.REQUESTED` → enqueue `GDPR_ERASURE`
   platform task → `202 Accepted`.
2. **`GdprErasureTaskHandler`** (on `TaskQueueProcessor`): deserialize payload → call
   `ErasureExecutor.ExecuteAsync` → mark task terminal.
3. **`ErasureExecutor`**: walk the 37-7 `SubjectDataMap`; per entry consult 37-6
   `ILegalHoldService` (skip held); apply per-policy action —
   - plaintext `deletable` ⇒ hard delete / NULL,
   - plaintext `must-retain-anonymized` ⇒ tombstone overwrite,
   - `encrypted-secret` ⇒ **crypto-shred** via `ISecretStore.RetireVersionAsync` /
     `DeleteVersionAsync` (record `(SecretId, Version, KekId)`),
   - audit identity field ⇒ 37-2 `IAuditChainAnonymizer.AnonymizeAndReanchorAsync`.
   Persist a per-map cursor on the task for resume. Apply chain re-anchor last, per-row atomic.
4. **Terminal event**: `GDPR.ERASURE.COMPLETED` or `...PARTIAL` (if any held) with per-category
   counts + destroyed key-version tuples; `GDPR.ERASURE.BLOCKED_BY_HOLD` for held items;
   `GDPR.ERASURE.FAILED` on unrecoverable error. Request/actor/reason/tuples retained as evidence.
5. **Verify**: `VerifyErasureAsync` re-walks the map — no plaintext PII remains, every
   `encrypted-secret` scrubbed (`GetVersionPlaintextAsync` → null, `Status=revoked`).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who may request? | the sole user (their instance) | `tenant_owner` only via tenant route (admin/member 403); `platform_admin` via admin route |
| Subject scope | the user themselves | a data subject inside the named tenant |
| Data location | control-plane (single instance) | control-plane (identity) + tenant schema (`TenantDbContext`) |
| Audit-of-erasure feed | sole user's feed (`TenantId` null) | tenant feed (`TenantId` set) + platform feed for admin path |
| Mode source | `ITammaModeProvider` | same |

---

## Task breakdown

### E8-1: Compliance contracts + event types + DTOs + DI (core, no behaviour yet)

**Scope:** Define the local seams and wire DI. No destruction logic yet.

**Files:**
- New: `src/Tamma.Api/Services/Compliance/ErasureRequest.cs` (records: `ErasureRequest`,
  `ErasureResult`, `ShreddedKeyVersion(SecretId, Version, KekId)`, `HeldBackItem(MapEntryId, HoldId,
  Reason)`, `ErasureStatus`, `ErasureScope`).
- New: `src/Tamma.Api/Services/Compliance/ErasureEventTypes.cs`
  (`GDPR.ERASURE.REQUESTED|COMPLETED|PARTIAL|BLOCKED_BY_HOLD|FAILED`).
- New stub seams (consumed from 37-2/6/7; mocked in tests):
  `Services/Compliance/IAuditChainAnonymizer.cs` (37-2),
  `Services/Compliance/ISubjectDataMapProvider.cs` + `SubjectRef`/`SubjectDataMapEntry`/`ErasurePolicy`
  (37-7), `Services/Compliance/ILegalHoldService.cs` (37-6). Mark each clearly "OWNED BY 37-x; this
  is a transitional contract to be replaced when that story lands."
- New: `src/Tamma.Api/Services/Compliance/ErasureOptions.cs` (`Gdpr:ErasureSlaDays`, default 30).
- New: `src/Tamma.Api/Dtos/Compliance/ErasureDtos.cs` (request body `{ subjectRef, reason }`,
  status response).
- New: `src/Tamma.Api/Extensions/ComplianceServiceCollectionExtensions.cs`; wire in `Program.cs`
  (mirror an existing `*ServiceCollectionExtensions` pattern).

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/ErasureContractsTests.cs` — record shapes /
event-type constants stable; options bind from config; DI resolves `ErasureExecutor` (once E8-2
exists, this becomes the resolution smoke test).

**Acceptance:**
- [ ] Event-type constants + records compile and are referenced nowhere-else-by-string.
- [ ] `Gdpr:ErasureSlaDays` binds (default 30).
- [ ] Full suite stays green.

### E8-2: `ErasureExecutor` — map walk, per-policy destroy, crypto-shred, counts

**Scope:** The core. Walk `ISubjectDataMapProvider`, apply per-entry policy, crypto-shred encrypted
entries, accumulate per-category counts + destroyed key tuples. No endpoints/queue yet; drive
directly in tests.

**Files:**
- New: `src/Tamma.Api/Services/Compliance/ErasureExecutor.cs` — `ExecuteAsync(ErasureRequest)` +
  `VerifyErasureAsync(SubjectRef)`. Inject `ISubjectDataMapProvider`, `ILegalHoldService`,
  `IAuditChainAnonymizer`, `ISecretStore`, `ControlPlaneDbContext`/`TenantDbContext` accessors,
  `IEventRepository`, `ITammaModeProvider`, `ILogger`.
- Destruction primitives: hard-delete/NULL (deletable), tombstone overwrite
  (`erased:{hashedSubjectId}`, must-retain-anonymized), crypto-shred via
  `ISecretStore.RetireVersionAsync` for each live version of each subject-scoped secret (record
  `(SecretId, Version, KekId)`), audit anonymize via `IAuditChainAnonymizer`.

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/ErasureExecutorTests.cs` —
- policy application: deletes deletable, anonymizes must-retain to stable tombstone, counts correct;
- crypto-shred: post-run `GetVersionPlaintextAsync` → null, `Status=revoked`, tuple recorded
  (story AC2/AC3/AC9);
- idempotent re-run: no double-count, no throw, shredded stays revoked (AC10);
- append-only untouched: spy/interceptor asserts zero `UPDATE`/`DELETE` on `domain_events` (AC4);
- mid-walk throw: partial work durable, cursor recorded, resume completes (AC13).

**Acceptance:**
- [ ] Each policy class destroyed by its correct strategy; counts match the map.
- [ ] Crypto-shredded ciphertext unrecoverable; destroyed tuples recorded.
- [ ] Idempotent; `domain_events` never mutated.

### E8-3: Legal-hold gating + partial results + GDPR.ERASURE.* emission

**Scope:** Wrap the walk with 37-6 hold checks and emit the lifecycle events.

**Files:** modify `ErasureExecutor.cs` — pre-check `ILegalHoldService` per entry; skip held, collect
`HeldBackItem`s; emit `GDPR.ERASURE.BLOCKED_BY_HOLD` (per batch of held), and exactly one terminal
`COMPLETED`/`PARTIAL` (held>0 ⇒ PARTIAL) carrying per-category counts + tuples via
`IEventRepository.AppendAsync` using the `TenantLifecycleEvents.BuildEvent`-style shape; subject id
**hashed** in tags. `REQUESTED` is emitted by the endpoint (E8-4) but assert its tags here too.

**Tests (first):** extend `ErasureExecutorTests` —
- held records skipped + listed with hold id/reason; remainder erased; terminal = PARTIAL;
  `BLOCKED_BY_HOLD` emitted (AC6);
- no-held ⇒ exactly one `COMPLETED`, counts correct (AC7);
- subject id hashed in every event tag; reason/actor/tuples present in terminal `data` (AC7);
- failure ⇒ `GDPR.ERASURE.FAILED` with cursor.

**Acceptance:**
- [ ] Legal hold blocks held records; partial result accurate; remainder still erased.
- [ ] Exactly one terminal event; per-category counts correct; subject hashed everywhere.

### E8-4: Endpoints (tenant + platform) + RBAC + async enqueue + status

**Scope:** HTTP surface and async wiring. Validate → `REQUESTED` event → enqueue `GDPR_ERASURE`
platform task → 202; plus status read-back.

**Files:**
- Modify `src/Tamma.Api/Endpoints/OrgEndpoints.cs` — `RequestErasure` (tenant_owner; mirror
  `ReprovisionOrg`'s membership filter + role gate, tightened to owner) +
  `GetErasureStatus` (`GET /api/v1/orgs/{tenantId}/erasure/{id}`).
- Modify `src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` — platform `RequestErasure` +
  `GetErasureStatus` under `.RequireAuthorization("PlatformOwnerAccess")`.
- New: `src/Tamma.Api/Services/Compliance/GdprErasureTaskHandler.cs` — registered with
  `TaskQueueProcessor`; deserialize payload, call `ErasureExecutor.ExecuteAsync`, mark terminal,
  WARN if past `Gdpr:ErasureSlaDays`.
- Modify `Program.cs` — map the four routes; register handler + services via the E8-1 extension.
- Enqueue via `IPlatformQueuedTaskRepository` (platform-scope; NOT `ITaskQueue`).

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/ErasureEndpointsTests.cs` —
- RBAC matrix: tenant_owner ✓, tenant_admin 403, member 403, cross-tenant 404, platform_admin ✓ on
  admin route / non-platform 403, out-of-scope subject 404 (AC8);
- accept ⇒ 202 + `erasureRequestId` + `REQUESTED` event + a `GDPR_ERASURE` task enqueued (AC1/AC11);
- status endpoint reports `requested→in_progress→completed|partial|failed` (AC11);
- handler invokes executor and marks task terminal; SLA WARN past threshold.

**Acceptance:**
- [ ] Endpoint shape consistent; auth middleware decides scope (prompt-store/org precedent).
- [ ] 202 + async execution via existing `TaskQueueProcessor`; status reflects progress.

### E8-5: Chain preservation + verification + tenant-purge reconciliation

**Scope:** Prove the load-bearing compliance invariants and reconcile with `TENANT.PURGED`.

**Files:** modify `ErasureExecutor.cs` (call `IAuditChainAnonymizer.AnonymizeAndReanchorAsync`
**last**, per affected row atomically; expose `VerifyErasureAsync`); doc note in the story's
Dev Notes already covers the purge-share — add a thin shared helper if the secret-scrub call needs
factoring so purge and erasure call one method.

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/ErasureChainPreservationTests.cs` —
- seed a 37-2 chain incl. the subject; erase; `VerifyChainAsync` passes (re-anchored) (AC5);
- positive-control: a manual byte edit to a chained row still FAILS verification (chain is real,
  not bypassed);
- `VerifyErasureAsync` re-walk: no plaintext PII remains, every encrypted entry scrubbed (AC9);
- tenant-purge reconciliation: a full purge crypto-shreds every subject's keys via the same
  primitive; single-subject erasure is the subset (AC12);
- crash mid-walk ⇒ resume ⇒ chain still verifies (AC13).

**Acceptance:**
- [ ] Chain verifies after erasure; tamper still detected.
- [ ] Verification confirms PII unrecoverable; purge ↔ erasure share the shred primitive.

---

## Task order & dependencies

E8-1 → E8-2 → E8-3 → E8-4 → E8-5. E8-1 is the only hard prerequisite for the rest. E8-5's chain
tests depend on the 37-2 anonymizer contract from E8-1 (mocked) and the real impl once 37-2 ships.

## Risks

- **Story 37-2/6/7 not yet authored.** Their contracts are mocked behind transitional interfaces in
  `Services/Compliance/`. Risk: their real shapes differ. Mitigation: keep the stub interfaces
  minimal (only the methods erasure calls) and clearly marked "OWNED BY 37-x"; a follow-up swaps the
  mock for the real DI registration with no `ErasureExecutor` change.
- **Accidentally breaking the audit chain.** A silent in-place edit of a chained audit row looks
  like tampering to `VerifyChainAsync`. Mitigation: anonymization MUST go through the 37-2
  re-anchor seam (re-hash + re-sign as an explicit step), applied last and per-row atomic; the
  positive-control tamper test guards against bypass.
- **Mutating the append-only event store.** Easy to reach for "just delete the PII events."
  Mitigation: AC4 interceptor test fails the build if any `UPDATE`/`DELETE` hits `domain_events`;
  PII is killed only by crypto-shred + curated-audit anonymization.
- **Irreversibility correctness.** Crypto-shred must leave NO key path. Mitigation: lean on the
  existing `DeleteVersionAsync` scrub (already idempotent, already keeps the tombstone row) and
  `VerifyErasureAsync` re-probe; do not invent a parallel deletion path.
- **PII leakage in logs/events.** Mitigation: subject identity is always a salted hash in tags +
  logs; only reason/actor/key-tuples (evidence) are retained in clear.
- **Tenant-purge drift.** Erasure and purge could grow divergent shred logic. Mitigation: factor the
  secret-scrub into one shared helper both call (E8-5).
- **Async failure leaving a half-erased subject.** Mitigation: per-map cursor on the queued task,
  idempotent primitives, resume-from-cursor; chain re-anchor applied last so a partial run is still
  verifiable.
- **SLA breach invisibility.** Mitigation: `Gdpr:ErasureSlaDays` (default 30) + WARN log + status
  endpoint surface `failed`/non-terminal.
