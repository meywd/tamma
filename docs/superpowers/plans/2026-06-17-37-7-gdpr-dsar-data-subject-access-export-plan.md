# Story 37-7 — GDPR DSAR (Data Subject Access Export) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes tests
> before implementation. DB-bound xUnit suites run via `sg docker -c "dotnet test ..."`.

**Goal:** Implement the GDPR Art. 15 / Art. 20 (and CCPA right-to-know) **Data Subject Access
Request** flow: given a subject (a `User` by `userId` or `email`), gather **all** personal data
Tamma holds about them across the control plane and the relevant tenant schema(s), and produce a
portable, machine-readable + human-readable export bundle. The job runs async on the existing
`TaskQueueProcessor`, is RBAC-gated and identity-verified, is itself audited (`GDPR.DSAR.*`), and
**never** emits secret values.

**Story file:** `docs/stories/epic-37/story-37-7/37-7-gdpr-dsar-data-subject-access-export.md`

**Seed/spec:** `/tmp/pab_stories/37-7.json` (P1, Epic 37 "Audit, Compliance & Data Governance",
est 5-6 days). boundaryNote: *(none)*.

---

## Non-goals (YAGNI guard)

- **NO right-to-erasure / deletion.** This story is read/export only. Erasure is a separate Epic 37
  story — DSAR must not delete or anonymize anything.
- **NO new async-job framework.** Reuse `PlatformQueuedTask` + `TaskQueueProcessor` +
  `IPlatformTaskHandler` (the 37-4 / Cranl-provisioning machinery). No bespoke threads/timers.
- **NO new artifact-storage / token primitive if 37-4 ships first.** Reuse its encrypted-at-rest
  artifact + single-use time-limited download token (`SecretRevealTokenRow`-style). If this story
  lands first, factor a shared helper so 37-4 consumes it.
- **NO change to resolution/secret semantics.** Secret values are excluded by default-deny
  redaction; nothing in DSAR reveals a protected value, hash, or ciphertext.
- **NO `packages/api` (TypeScript) work.** That package is deleted. All work is in `apps/tamma-elsa`
  (`Tamma.Api` + `Tamma.Data`).
- **NO per-request mode branching beyond `ITammaModeProvider`.** A deployment is one mode for the
  process lifetime.
- **NO dashboard UI in this story.** Endpoints only; a DSAR admin/tenant UI is a follow-up.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Where the personal data lives

| Scope | DbContext | Subject-bearing entities (verified) |
|---|---|---|
| Control plane | `Tamma.Data/ControlPlaneDbContext.cs` | `User` (`Email`, `PasswordHash`, `*TokenHash`, `Settings`), `TenantMembership` (`UserId`), `UserInvite`, `RefreshToken`, `ApiKey`, `AdminImpersonation` (`TargetUserId`/`ImpersonatorUserId`), `PlatformEvent`, `DomainEvent`, `SecretRow` (`OwnerUserId`) |
| Per-tenant | `Tamma.Data/TenantDbContext.cs` via `ITenantDbContextFactory.CreateAsync(tenantId)` | `PromptOverride` (`CreatedBy`/`UserId`), `Convention` (`CreatedBy`), `AgentConfig` (`CreatedBy`), `SanitizationRule`, `Alert`, `DomainEvent` |

- `User` (`Tamma.Data/Entities/User.cs`): `Email` NOT NULL, case-insensitive unique via
  `LOWER(email) WHERE deleted_at IS NULL`. Has `PasswordHash`, `EmailVerificationTokenHash`,
  `Settings` (JSON) — all PII / secret-adjacent.
- `TenantMembership` (`Entities/TenantMembership.cs`): `{ TenantId, UserId, Role }` — the subject's
  tenant set (drives multi-tenant coverage in AC12).
- `AdminImpersonation` (`Entities/AdminImpersonation.cs`): subject as `TargetUserId` or
  `ImpersonatorUserId`.
- `SecretRow` (`Entities/SecretRow.cs`): `OwnerUserId` — include metadata only.
- `PromptOverride`/`Convention`/`AgentConfig` all carry `CreatedBy` (Guid?) — authored-by key.

### Reusable infrastructure (verified present)

- **Event write side:** `IEventRepository.AppendAsync(DomainEvent)`
  (`Tamma.Data/Repositories/EventRepository.cs`); `DomainEvent` = `{ Id, Type, TenantId?, Tags,
  Metadata, Data, ... }`. Emission pattern: `OrgEndpoints.EmitTenantEvent` (line ~1036).
- **Async jobs:** `PlatformQueuedTask` (`Entities/PlatformQueuedTask.cs`) +
  `Services/TaskQueue/TaskQueueProcessor.cs` + `Services/PlatformTasks/IPlatformTaskHandler.cs`
  (`TaskType` routing, retry/terminal/dead-letter semantics, cancellation-aware) registered via
  `services.AddPlatformTaskHandler<T>()`.
- **RBAC policies** (`Program.cs` ~966-996): `OwnerAccess` (tenant owner — tenant-scoped),
  `PlatformOwnerAccess` (platform admin — keys off `User.PlatformRole == "platform_admin"`),
  `MemberAccess`, `AuthenticatedAny`. Tenant membership enforced by `RequireTenantMembershipFilter`.
- **Tenant access:** `ITenantDbContextFactory.CreateAsync(tenantId, ct)`
  (`Tamma.Data/TenantDbContextFactory.cs`) → `TenantDbContext` scoped to the tenant's schema via
  `LruPooledTenantConnectionResolver` (unconditionally wired per the tenancy plan).
- **Mode:** `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`) — process-stable
  SingleUser | SaaS.
- **Time-limited single-use token precedent:** `SecretRevealTokenRow`
  (`Entities/SecretRevealTokenRow.cs`): `TokenHash` (unique index), `ExpiresAt`, `Status`,
  `ConsumedAt` — exactly the download-token shape DSAR needs.

### Gaps (NEW, must be built)

- `Services/Compliance/` directory does **not** exist.
- No `DsarJob` entity / table.
- Sibling stories **37-1** and **37-4** dirs exist but are **empty** (not yet authored). Treat their
  contracts (sensitive-event taxonomy; async-export artifact + token) as integration points; if they
  land first, consume their seams; otherwise build the artifact/token helper here so they reuse it.

---

## Architecture

**Resolve subject → collect (data-map-driven) → package (json + html, encrypted) → audited async
job → time-limited download.**

```
POST /dsar  ──202──►  DsarJob(pending) + enqueue PlatformQueuedTask("compliance.dsar.export")
                                   │
                       TaskQueueProcessor picks it up
                                   ▼
            DsarExportTaskHandler.HandleAsync
              ├─ collecting: DsarCollector.CollectAsync
              │     ├─ ControlPlaneDbContext: run every ControlPlane SubjectDataSource
              │     └─ for each in-scope tenant: TenantDbContext via factory → Tenant sources
              ├─ packaging:  DsarBundlePackager  (export.json + export.html → encrypted zip)
              └─ ready:      DsarJobStore.MarkReady(artifactRef, downloadTokenHash, expiresAt)
                                   │
              GDPR.DSAR.REQUESTED / COMPLETED / FAILED  (IEventRepository, sensitive=true)
                                   ▼
GET /dsar/{jobId}/download?token=  ──single-use, TTL──►  artifact  (else 410)
```

**Data-map-driven collection (AC3/AC4):** `SubjectDataMap` is the single registry; each
`SubjectDataSource` declares `{ Category, Scope, EntityType, PiiFields, Collect }`. The collector is
generic over the map. A `[ContainsPersonalData]` marker (or `KnownPiiEntities` list) + a completeness
test guarantees no PII entity is silently omitted.

**Secret exclusion (AC7):** `DsarRedactionPolicy` is the single chokepoint, default-deny — a field
ships only if explicitly listed in the source's non-secret projection. Secrets/keys/hashes/tokens →
`{ exists: true, ...metadata }`.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who can **initiate** a DSAR? | The sole user, for **themselves only** (`/api/v1/dsar/self`). A non-self subject ref → 403. | Tenant owner/admin for subjects **in their tenant** (`/api/v1/orgs/{tenantId}/dsar`, `OwnerAccess`); platform owner for **any** subject (`/api/admin/dsar`, `PlatformOwnerAccess`). `member` → 403. |
| Whose data is in scope? | The sole user's CP + their tenant. | Tenant-initiated: CP + **that one tenant only**. Platform-owner: CP + **every** tenant the subject is a member of. |
| What `TenantId` on the `DsarJob`/events? | null (system-scope = the user's feed). | Tenant-initiated: `{tenantId}`. Admin: null (platform-scope). |
| Identity verification | caller == subject (claims). | subject existence + `TenantMembership` in `{tenantId}` (tenant route) verified independently of the request body. |
| Mode source | `ITammaModeProvider` | same |

---

## Phase breakdown

### DSAR-1: `DsarJob` entity + job store + DCB event types (core, no collection yet)

**Scope:** Persist the job lifecycle; emit `GDPR.DSAR.*`; no collection/packaging yet.

**Files:**
- New: `Tamma.Data/Entities/DsarJob.cs` (fields per story Technical Design); `DbSet<DsarJob>` in
  `ControlPlaneDbContext.cs`; mapping + CHECK on `status`
  (`pending|collecting|packaging|ready|failed`) + indices in `TammaModelConfiguration.cs`; additive
  EF migration under `Migrations/ControlPlane/` (verify `has-pending-model-changes` reports none
  after).
- New: `Services/Compliance/IDsarJobStore.cs`, `DsarJobStore.cs` (create/transition/mark-ready/
  mark-failed/register-download-token/consume-token).
- New: `Services/Compliance/DsarEventTypes.cs`
  (`GDPR.DSAR.REQUESTED/COMPLETED/FAILED/DOWNLOADED`).
- New: `Extensions/ComplianceServiceCollectionExtensions.cs`; wire in `Program.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Compliance/DsarJobStoreTests.cs` — create→pending;
transitions enforce CHECK; mark-ready stamps artifact ref + token hash + expiry; mark-failed stamps
reasonCode; token consume single-use; events appended on request/complete/fail; migration
applies/rolls back.

**Acceptance criteria:**
- [ ] `DsarJob` table created additively; `has-pending-model-changes` → none.
- [ ] Status transitions reject invalid states (CHECK).
- [ ] `GDPR.DSAR.REQUESTED/COMPLETED/FAILED` appended via `IEventRepository` with no subject values
      in payload.
- [ ] Full suite stays green.

### DSAR-2: `SubjectDataMap` + completeness guard + redaction policy

**Scope:** The declarative registry of PII sources, the marker attribute, the build-failing
completeness test, and the default-deny redaction chokepoint. No DB queries in collectors yet
(stub `Collect` delegates wired to the map shape, real queries in DSAR-3).

**Files:**
- New: `Services/Compliance/SubjectDataSource.cs`, `ISubjectDataMap.cs`, `SubjectDataMap.cs`,
  `ContainsPersonalDataAttribute.cs` (+ `KnownPiiEntities` fallback list),
  `DsarRedactionPolicy.cs`, `DsarRecord.cs` / `DsarBundle.cs` (model with provenance).
- Annotate PII entities with `[ContainsPersonalData]` (or populate `KnownPiiEntities`): `User`,
  `TenantMembership`, `UserInvite`, `RefreshToken`, `ApiKey`, `AdminImpersonation`, `SecretRow`,
  `PromptOverride`, `Convention`, `AgentConfig`, `SanitizationRule`, `Alert`, `DomainEvent`,
  `PlatformEvent`.

**Tests (first):** `Compliance/SubjectDataMapCompletenessTests.cs` — reflect over
`[ContainsPersonalData]` entities; assert each is a `SubjectDataSource` in the map (a missing one
fails the build). `Compliance/DsarRedactionTests.cs` — feed rows containing secret/hash/token
material; assert default-deny strips them and emits `{ exists: true }` metadata.

**Acceptance criteria:**
- [ ] Every PII-marked entity is registered in `SubjectDataMap` (guard test red→green by adding a
      missing registration).
- [ ] Redaction is default-deny: a field appears only if explicitly projected; no secret value /
      hash / token / ciphertext survives.

### DSAR-3: `DsarCollector` — CP + per-tenant collection

**Scope:** Real `Collect` delegates; the collector partitions the map (CP vs Tenant), queries
`ControlPlaneDbContext` once and each in-scope tenant via `ITenantDbContextFactory`, assembles a
provenance-tagged `DsarBundle`. Implements subject resolution (email/userId → exactly one user;
ambiguous/unknown → not-found signal) and in-scope-tenant computation (tenant-initiated = one;
platform-owner = all of subject's memberships).

**Files:**
- New: `Services/Compliance/IDsarCollector.cs`, `DsarCollector.cs`.
- Flesh out each `SubjectDataSource.Collect` (the 14 categories in the story's data-map table),
  each routed through `DsarRedactionPolicy`.

**Tests (first):** `Compliance/DsarCollectorTests.cs` — seed a subject across CP + two tenant
schemas; assert one section per category, correct counts, provenance tags; platform-owner covers
both tenants, tenant-initiated covers only one (AC12); subject resolution by email (case-insensitive)
and userId; ambiguous → not-found.

**Acceptance criteria:**
- [ ] Bundle has one provenance-tagged section per `SubjectDataMap` category present.
- [ ] Multi-tenant coverage matches caller scope (AC12).
- [ ] Subject resolution exact for userId, case-insensitive for email, not-found on ambiguity.

### DSAR-4: `DsarBundlePackager` + `DsarExportTaskHandler` (async job)

**Scope:** Render `export.json` (manifest + sections) + `export.html` (human-readable), zip,
encrypt at rest, register a single-use TTL download token on the `DsarJob`. Wire the
`IPlatformTaskHandler` that drives collecting→packaging→ready and emits `GDPR.DSAR.COMPLETED` /
`FAILED`, with idempotent retry.

**Files:**
- New: `Services/Compliance/DsarBundlePackager.cs` (reuse 37-4 artifact-encryption + token seam if
  present; else implement and factor for reuse), `DsarExportTaskHandler.cs`
  (`TaskType = "compliance.dsar.export"`).
- Register handler via `AddPlatformTaskHandler<DsarExportTaskHandler>()` in the compliance extension.

**Tests (first):** `Compliance/DsarExportTaskHandlerTests.cs` — pending→collecting→packaging→ready;
failure → failed + `GDPR.DSAR.FAILED`; idempotent retry (ready job re-run no-op); terminal vs
retryable exception routing; manifest fields present; both `export.json` + `export.html` in the zip;
end-to-end `DsarRedactionTests` against the **packaged** bundle (no secret material anywhere).

**Acceptance criteria:**
- [ ] Job reaches `ready` with an encrypted artifact + single-use TTL token.
- [ ] Bundle contains machine-readable JSON + human-readable HTML + manifest.
- [ ] Retry is idempotent; bad payload → terminal → dead-letter.
- [ ] No secret value/hash/token in the packaged artifact.

### DSAR-5: Endpoints + RBAC + identity verification + download

**Scope:** The three initiation surfaces, status, and time-limited download, with per-mode RBAC and
independent subject verification.

```
POST /api/v1/orgs/{tenantId}/dsar                 OwnerAccess          -> 202 {jobId}
GET  /api/v1/orgs/{tenantId}/dsar/{jobId}         OwnerAccess          -> status
GET  /api/v1/orgs/{tenantId}/dsar/{jobId}/download?token=   OwnerAccess -> artifact | 410
POST /api/admin/dsar                               PlatformOwnerAccess  -> 202 {jobId}
GET  /api/admin/dsar/{jobId}                       PlatformOwnerAccess  -> status
GET  /api/admin/dsar/{jobId}/download?token=       PlatformOwnerAccess  -> artifact | 410
POST /api/v1/dsar/self                             AuthenticatedAny (SingleUser only) -> 202 {jobId}
GET  /api/v1/dsar/self/{jobId}                     AuthenticatedAny (SingleUser only) -> status
```

**Files:**
- New: `Endpoints/ComplianceDsarEndpoints.cs` (mirror `AlertEndpoints.cs`/`OrgEndpoints.cs`
  structure: admin + org + self sections); map in `Program.cs`. (Optionally thin shims in
  `OrgEndpoints.cs`/`AdminEndpoints.cs` that delegate.)
- In-handler subject verification: tenant route asserts subject ∈ `{tenantId}` via
  `TenantMembership` (cross-tenant → 404); admin route allows any; self route pins subject to caller
  (non-self ref → 403). Self route gated on `ITammaModeProvider == SingleUser`.

**Tests (first):** `Compliance/ComplianceDsarEndpointsTests.cs` — full RBAC matrix (member 403;
tenant owner/admin scoped; cross-tenant subject 404; unknown/ambiguous 404; platform-owner any;
single-user self-only with non-self 403); 202 + jobId shape; status endpoint reflects lifecycle;
download single-use (second 410), expiry (410), tamper (401); `GDPR.DSAR.REQUESTED` on enqueue,
`GDPR.DSAR.DOWNLOADED` on consume.

**Acceptance criteria:**
- [ ] Endpoint shape consistent; auth stack + in-handler subject check enforce the RBAC matrix.
- [ ] Tenant-initiated DSAR never returns other-tenant data.
- [ ] Download is single-use and TTL-bounded; expired/consumed → 410, tampered → 401.

---

## Phase order & dependencies

DSAR-1 → DSAR-2 → DSAR-3 → DSAR-4 → DSAR-5.
DSAR-1 (job store + events) and DSAR-2 (map + redaction) are independent and can be parallelized;
DSAR-3 needs DSAR-2; DSAR-4 needs DSAR-1+DSAR-3; DSAR-5 needs DSAR-4. External deps 37-1
(audit taxonomy) and 37-4 (artifact/token seam) are integration points — if unbuilt, build the
seam in DSAR-4 and factor it for reuse.

## Risks

- **Secret leak (highest):** any new PII source could accidentally serialize a secret. Mitigation:
  `DsarRedactionPolicy` default-deny + a redaction test that scans the **packaged** artifact, not
  just the in-memory model. Treat a redaction-test failure as a release blocker.
- **Cross-tenant leak:** a tenant-initiated DSAR must never touch another tenant's schema. Mitigation:
  in-scope-tenant computation is `{tenantId}` for tenant routes, enforced + tested (AC12); the
  collector only opens tenant contexts in the computed set.
- **Subject-existence oracle:** returning 403 vs 404 can confirm a subject exists in another tenant.
  Use 404 for cross-tenant/unknown subjects (do not distinguish), 403 only for role/self failures.
- **Data-map drift:** a future PII entity added without a map entry would silently miss a category.
  Mitigation: `[ContainsPersonalData]` marker + build-failing completeness guard (AC4).
- **Async-job topology / 37-4 ordering:** if 37-4's artifact+token seam isn't merged, DSAR-4 must
  build a self-contained encrypted-artifact + single-use-token helper and factor it so 37-4 reuses
  it — avoid two divergent token implementations.
- **Migration discipline:** `DsarJob` is additive; still verify `has-pending-model-changes` → none
  and mirror entity config in `TammaModelConfiguration.cs` (the single source). DB suites run via
  `sg docker -c "dotnet test ..."`.
- **Event-store topology shift (28-1/Epic 30):** system-scope (`TenantId = null`)
  `GDPR.DSAR.*` events must stay CP-resident via `IEventRepository`; only tenant-scope routing moves
  later.
