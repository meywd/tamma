# Story 37-11 — SOC2-Aligned Control Mapping & Evidence Pack

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Read `docs/guides/BEFORE_YOU_CODE.md` first.

**Goal:** Turn the Epic 37 audit substrate into an auditor-facing compliance product. Ship a
code-resident **ControlCatalog** that maps SOC2 Trust Services Criteria (CC6 access, CC7 monitoring,
CC8 change management, CC1/CC2 governance, A1 availability — plus ISO 27001 Annex A overlaps) to the
real Tamma controls that satisfy them (auth/RBAC, secret cabinet encryption, audit logging, access
reviews, backups, change management), evaluate each control to **satisfied / gap** against the audit
read-model, and generate an **on-demand, signed, encrypted, expiring evidence pack** (control→evidence
map + chain verification + retention snapshot + legal-hold state + config attestations + coverage
summary) — platform-level for Tamma's own SOC2 and per-tenant for tenants under their own attestation.

**Story file:** `docs/stories/epic-37/story-37-11/37-11-soc2-aligned-control-mapping-and-evidence-pack.md`
(Status: drafted; 14 ACs).

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` — control-plane API + Elsa engine.
Catalog domain logic in `Tamma.Core/Compliance/`; evidence collection + endpoints in
`Tamma.Api/Services/Compliance/` + `Endpoints/ComplianceEndpoints.cs`; persistence in `Tamma.Data`.
Tests in `apps/tamma-elsa/tests/Tamma.Core.Tests/Compliance/` and `tests/Tamma.Api.Tests/Compliance/`
(xUnit; docker-bound suites run via `sg docker -c "dotnet test ..."`).

**`packages/api` is DELETED — never a target.** All code is C#.

---

## Non-goals (YAGNI guard)

- **NO new audit events, retention machinery, hash-chain, export crypto, or legal-hold logic.** This
  story READS the Epic 37 substrate and MAPS it. It owns exactly two new event types
  (`COMPLIANCE.EVIDENCE_PACK.GENERATED` / `.DOWNLOADED`) and the evidence-pack artifact metadata —
  nothing else in the audit pipeline.
- **NO bespoke signing/encryption.** The evidence pack is produced through 37-4 `AuditExportService`,
  inheriting its sign/encrypt/expiry guarantees. If a byte of crypto is written in this story, it is
  wrong.
- **NO DB-resident control framework.** The catalog is policy-as-code (mirrors `SystemPrompts.cs` /
  `BuiltInAlertRules.cs`) — version-controlled, PR-reviewed; only generated artifacts + evidence live
  in the DB.
- **NO secret exfiltration via attestations.** Config attestations prove POSTURE (booleans/counts/
  cadence), never secret values, connection strings, or API keys.
- **NO per-user override layer in SaaS.** Compliance posture is tenant- or platform-owned; members get
  read-only views (mirrors prompt-store RBAC).
- **NO heavy dashboard UI.** This story ships the control-status data + DTOs; the visual tab is a thin
  add in 37-10.
- **NO re-implementation of unmerged dependencies.** Where 37-1/37-3/37-4/37-5/37-6 are not yet on
  disk, code against narrow consumer interfaces + test doubles and bind to the real impl when it lands.

---

## Current-state findings (verified 2026-06-17, repo @ main)

### What exists on disk (real evidence sources the catalog maps to)

| Control area | Verified artifact |
|---|---|
| Auth / RBAC | `src/Tamma.Api/Program.cs` policies `OwnerAccess` (~971), `PlatformOwnerAccess` (~986), `MemberAccess` (~991); `src/Tamma.Api/Auth/Permissions.cs`; `src/Tamma.Api/Authorization/TenantRoleHierarchy.cs`, `RequireTenantMembershipFilter.cs` (`TenantRole` item key, `IsAtLeast(role, Admin)`). |
| Encryption / secret cabinet (Epic 29) | `src/Tamma.Api/Services/Secrets/` — `KekProvider`, `KekRotationCoordinator`, `KekRotationMetrics`, `SecretRow`/`SecretVersionRow` entities; AES-GCM at rest via `Services/Provisioning/TenantSecretProtector.cs`. |
| Audit logging | `src/Tamma.Data/Entities/DomainEvent.cs` (Type/TenantId/Tags/Metadata/Data/CreatedAt/`SequenceNumber`); `IEventRepository.AppendAsync(DomainEvent)`; `ISecretAccessAuditor` codes (`SECRET.READ/WRITE/ROTATE.STARTED/ROTATE.SUCCESS/ROTATE.FAILED/REVEAL/VERSION.REVOKED/MIGRATED.*`). |
| Monitoring / alerting | `src/Tamma.Api/Services/Alerts/IAlertSink.RaiseAsync(AlertPayload)`; `AlertRuleEvaluator` (polls `DomainEvents`); `ALERT.RAISED` event. |
| Access review / change | `AdminImpersonation` entity + `AdminImpersonationService`; `WorkflowDefinition`/`WorkflowInstance` entities; `KekRotation` entity. |
| Backups (Epic 28) | `src/Tamma.Api/Services/Provisioning/TenantMoveService.cs`; tenant-database pool placement; Cranl backup capability flags. |
| Async job pattern | `Services/PlatformTasks/PlatformTaskWorker.cs` + `IPlatformTaskHandler`; `Results.Accepted` 202 precedent in `AdminEndpoints.cs` (~442), `KekRotationEndpoints.cs` (~54). |
| Mode seam | `src/Tamma.Api/Services/PromptStore/TammaMode.cs` (`ITammaModeProvider`, SingleUser \| SaaS). |
| Per-mode endpoint precedent | `src/Tamma.Api/Endpoints/AlertEndpoints.cs` (admin section under `/api/v1/admin/...` + tenant section under `/api/v1/orgs/{tenantId}/...`; paging 50/500; `TenantRoleHierarchy.IsAtLeast` mutation gate). |
| Org route group | `Program.cs` ~1505–1513: `/api/v1/orgs` group with path-tenant gate. |

### What is NEW (does not exist on disk — dependency seams)

- **No `AuditRecord` / audit read-model entity** in `Tamma.Data/Entities/` → 37-1/37-3 (`IAuditQuery`).
- **No hash-chain verifier** → 37-2 (`IChainVerifier`).
- **No `AuditExportService`** → 37-4 (sign/encrypt/expiring export).
- **No retention-policy reader** → 37-5 (`IRetentionPolicyReader`).
- **No legal-hold reader / entity** → 37-6 (`ILegalHoldReader`).
- **No `Compliance` directory** in `Tamma.Core` or `Tamma.Api/Services` → this story creates it.
- **No `EvidencePackArtifact` entity** → this story creates it.

**Implication:** the catalog + per-mode RBAC + endpoints are buildable today against real controls;
the evidence-*collection* paths bind to consumer interfaces + test doubles until the deps merge. The
catalog-integrity test (AC2) cross-checks against the real on-disk event codes (`ISecretAccessAuditor`,
`ALERT.RAISED`, auth events) plus the 37-1 declared known-codes set (pending until 37-1 lands).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Who sees **platform-scope** controls/evidence (KEK, backups, seed config)? | The sole user — one feed, `tenantId` null. | Platform owner ONLY (`PlatformOwnerAccess`). Never leaked to tenants. |
| Who sees **tenant-scope** controls? | The sole user (`tenantId` null). | `tenant_owner`/`tenant_admin` via `/api/v1/orgs/{id}/...`; `member` read-only. |
| Who generates a pack? | The user. | Platform pack: platform owner. Tenant pack: tenant_owner/admin (or platform owner). |
| Pack-generated event tag | `tenantId` null. | Platform → null (admin feed); tenant → set (tenant feed). |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`). | same |

---

## Architecture

**Catalog (policy-as-code) → evaluate against substrate → status report → async signed evidence pack.**

1. **`ControlCatalog`** (`Tamma.Core/Compliance/`, NEW) — immutable `ControlDefinition[]` mapping each
   SOC2 criterion to `{ RequiredActionCodes[], RetentionFloorDays, RequiresChainVerification,
   RbacPolicy, Scope (Platform|Tenant), IsoXref }`. Reviewed in PRs; a catalog change is itself a
   change-management artifact.
2. **`ControlEvaluator`** (`Tamma.Api/Services/Compliance/`, NEW) — for a `(control, scopeKey)`,
   reads supporting records via `IAuditQuery` (37-1/37-3), effective retention via
   `IRetentionPolicyReader` (37-5), and chain result via `IChainVerifier` (37-2) → produces
   `ControlStatus { status, lastEvidenceAt, supportingRecordCount, finding?, remediationHint? }`.
   **Gap** when: no evidence in lookback window, OR retention < floor, OR chain required + failed.
3. **`EvidencePackService` + `EvidencePackTaskHandler`** (NEW) — `POST` returns `202` + artifact id,
   persists `EvidencePackArtifact{status=pending}`, enqueues a `PlatformQueuedTask`; the handler runs
   on `PlatformTaskWorker`: `collecting` (control→evidence map via `IAuditQuery` excerpts + chain
   result + retention snapshot + legal-hold state + config attestation + coverage summary) → `signing`
   (hand bundle to 37-4 `AuditExportService` for sign/encrypt/expiry) → `ready` (append
   `COMPLIANCE.EVIDENCE_PACK.GENERATED`, sensitive/audited).
4. **`ConfigAttestationCollector`** (NEW) — point-in-time redacted posture snapshot (encryption-at-rest
   on, KEK cadence, retention floors, alert-channel count, mode); booleans/counts only, never secrets.
5. **`ComplianceEndpoints`** (NEW) — admin section (`PlatformOwnerAccess`) + tenant section
   (`/api/v1/orgs/{tenantId}` group), mirroring `AlertEndpoints.cs`. Tenant view filters catalog to
   `Scope == Tenant` and tenant-scoped evidence ONLY.
6. **`EvidencePackArtifact`** (NEW entity) — pack metadata (scope, tenant_id, period, status, coverage
   counts, signature_ref, expires_at); additive EF migration; entity config in
   `TammaModelConfiguration.cs` only.

---

## Task breakdown

### T1 — ControlCatalog + control domain types (Core, no DB)

**Scope:** Code-resident catalog and value types. No evaluation logic yet.

**Files (NEW):** `Tamma.Core/Compliance/ControlDefinition.cs`, `ControlScope.cs`, `ControlStatus.cs`,
`TrustServiceCriteria.cs`, `ControlCatalog.cs`.

**Tests first:** `tests/Tamma.Core.Tests/Compliance/ControlCatalogTests.cs` — unique criterion ids;
every control carries a non-empty `RequiredActionCodes`, a valid `RbacPolicy`, and a `Scope`; **AC2
no-orphan check**: every referenced action code resolves to a known/emitted event type (cross-check
against `ISecretAccessAuditor` constants ∪ `ALERT.RAISED` ∪ auth events ∪ the 37-1 declared
known-codes seam — mark the 37-1 portion pending until 37-1 lands); platform controls carry
`Scope == Platform`, tenant controls `Scope == Tenant`.

**Acceptance:**
- [ ] Catalog covers CC6.x, CC7.x, CC8.x at minimum, each with real action codes + retention floor +
      chain flag + RBAC policy + ISO xref.
- [ ] No orphan action-code references (AC2) for codes that exist on disk today.
- [ ] `Tamma.Core` builds; catalog test green.

### T2 — Consumer-interface seams for dependencies (Api, no live wiring)

**Scope:** Define the narrow interfaces this story consumes from unmerged deps, plus test doubles, so
T3+ is buildable independent of 37-1/37-2/37-4/37-5/37-6 merge order.

**Files (NEW):** `Tamma.Api/Services/Compliance/IAuditQuery.cs` (or import 37-1's if present — verify
first), `IChainVerifier.cs`, `IRetentionPolicyReader.cs`, `ILegalHoldReader.cs`,
`IComplianceEvidenceExporter.cs` (thin facade over 37-4 `AuditExportService`); test doubles under
`tests/Tamma.Api.Tests/Compliance/TestDoubles/`.

> **Verify-first:** if any dependency interface already exists on disk (a dep merged ahead of this
> story), import and bind to it instead of redeclaring. Do NOT duplicate dependency logic.

**Tests first:** seam contracts only (a test double satisfies each interface; no behavior yet).

**Acceptance:**
- [ ] Each consumer seam is a small interface this story owns the *consumption* of, not the *impl*.
- [ ] Test doubles compile and are usable by T3/T4 tests.

### T3 — ControlEvaluator + gap detection (Api)

**Scope:** Evaluate a control against the substrate via the T2 seams; per-mode scope key from
`ITammaModeProvider`.

**Files (NEW):** `Tamma.Api/Services/Compliance/IControlEvaluator.cs`, `ControlEvaluator.cs`,
`ComplianceOptions.cs` (`EvidenceLookbackDays`, defaults).

**Tests first:** `tests/Tamma.Api.Tests/Compliance/ControlEvaluatorTests.cs` — no evidence in window →
`gap` + finding + remediationHint; evidence present, retention < floor → `gap`; chain required + verify
failed → `gap`; chain not required → chain ignored; all satisfied → `satisfied` with `lastEvidenceAt` +
`supportingRecordCount`; mode matrix (single-user vs SaaS scope key).

**Acceptance:**
- [ ] Three gap reasons each produce a distinct, human-readable finding.
- [ ] Evaluator reads mode from `ITammaModeProvider`; never queries cross-tenant evidence for a tenant
      scope key.
- [ ] Suite green.

### T4 — Evidence pack: artifact entity, service, async task handler, attestation (Api + Data)

**Scope:** Persist pack metadata; orchestrate async collection → 37-4 sign/encrypt/expiry; emit
audited events; redacted config attestation.

**Files (NEW):** `Tamma.Data/Entities/EvidencePackArtifact.cs`; DbSet in `ControlPlaneDbContext.cs` +
config in `TammaModelConfiguration.cs`; additive migration under `Migrations/ControlPlane/`.
`Tamma.Api/Services/Compliance/IEvidencePackService.cs`, `EvidencePackService.cs`, `EvidencePackJob.cs`,
`EvidencePackTaskHandler.cs` (`IPlatformTaskHandler`), `ConfigAttestationCollector.cs`,
`ComplianceEventTypes.cs` (`COMPLIANCE.EVIDENCE_PACK.GENERATED` / `.DOWNLOADED`).

**Tests first:** `tests/Tamma.Api.Tests/Compliance/EvidencePackServiceTests.cs` — request persists
`pending` + enqueues task; handler transitions `collecting → signing → ready`; bundle goes through the
37-4 exporter stub (sign/encrypt); signature round-trip (verify passes; tamper → verify fails);
`GENERATED` event appended exactly once with correct tags (scope, tenantId?, period, coverage, mode);
download appends `DOWNLOADED`; failure path → `failed`, not downloadable; **attestation redaction**:
no connection-string / API-key-shaped values present (AC13).

**Acceptance:**
- [ ] Pack produced ONLY via 37-4 exporter — zero bespoke crypto in this story.
- [ ] `EvidencePackArtifact` migration applies; `has-pending-model-changes` reports none.
- [ ] Attestation contains posture booleans/counts only.

### T5 — ComplianceEndpoints (admin + tenant) with per-mode RBAC (Api)

**Scope:** Read + generation endpoints; mirror `AlertEndpoints.cs` admin/tenant split; map in
`Program.cs`; DI extension.

**Files (NEW):** `Tamma.Api/Endpoints/ComplianceEndpoints.cs`,
`Tamma.Api/Extensions/ComplianceServiceCollectionExtensions.cs`; **modify** `Program.cs` (map routes,
register services, register `EvidencePackTaskHandler` with the platform task worker).

```
GET  /api/admin/compliance/controls                  (PlatformOwnerAccess)
GET  /api/admin/compliance/controls/summary          (PlatformOwnerAccess)
POST /api/admin/compliance/evidence-pack             (PlatformOwnerAccess) -> 202
GET  /api/admin/compliance/evidence-pack/{id}        (PlatformOwnerAccess)
GET  /api/admin/compliance/evidence-packs            (PlatformOwnerAccess; paged 50/500)
GET  /api/v1/orgs/{tenantId}/compliance/controls     (tenant member; Scope==Tenant ONLY)
POST /api/v1/orgs/{tenantId}/compliance/evidence-pack(tenant_owner/admin; member 403) -> 202
GET  /api/v1/orgs/{tenantId}/compliance/evidence-pack/{id}
```

**Tests first:** `tests/Tamma.Api.Tests/Compliance/ComplianceEndpointsTests.cs` — RBAC matrix
(platform_admin / tenant_owner / tenant_admin / member / cross-tenant 404); tenant `controls` never
returns a `Scope==Platform` control nor another tenant's evidence; `POST` returns 202 + id, subsequent
`GET` reflects status; single-user mode: sole user sees platform + tenant controls in one list; summary
rollup counts by category/status.

**Acceptance:**
- [ ] Endpoint shape identical between modes; auth middleware decides scope (prompt-store/alert
      precedent).
- [ ] Cross-tenant → 404 (never reveal existence); member mutation → 403.
- [ ] Tenant view isolation holds (no platform-scope leakage).

### T6 (thin) — control-status DTO for the 37-10 dashboard widget

**Scope:** Expose the `controls/summary` rollup DTO in a shape the 37-10 audit dashboard can render as a
compliance-posture widget. No new UI in this story (37-10 owns the tab); ship the data contract + a
contract test.

**Files:** finalize the summary DTO in `ComplianceEndpoints.cs` / a `ComplianceSummaryDto.cs`; document
the contract in the story.

**Tests first:** summary endpoint returns category rollup, satisfied/gap counts, oldest-gap age, last
evidence-pack timestamp.

**Acceptance:**
- [ ] `GET /api/admin/compliance/controls/summary` returns a stable, dashboard-ready rollup.

---

## Task order & dependencies

T1 → T2 → T3 → T4 → T5 → T6.
T1 (catalog) is standalone and the first red test. T2 unblocks T3/T4 from dependency merge order. T5
needs T3 (status) + T4 (pack). T6 is a thin add on T5.

**External dep merge order is independent** of this plan thanks to T2 seams; live wiring of
37-1/37-2/37-4/37-5/37-6 is a one-line DI swap when each lands.

## Risks

- **Dependency not yet merged (37-1/37-2/37-4/37-5/37-6):** mitigated by T2 consumer-interface seams +
  test doubles. Risk: redeclaring an interface a dep already ships — mitigation: T2 starts by grepping
  for an existing impl and binding to it.
- **Catalog drift / orphan codes:** as new audit events land or are renamed, the catalog can point at
  a dead code. Mitigation: the AC2 no-orphan test runs in CI and fails the build on drift.
- **Secret exfiltration via attestation (AC13):** highest-severity risk — an evidence pack handed to an
  external auditor must never contain secret material. Mitigation: `ConfigAttestationCollector` reads
  only non-sensitive config + a redaction test asserting no secret-shaped strings; the secret cabinet
  is *attested to*, never *exported*.
- **Bespoke crypto creep:** signing/encryption belongs to 37-4. Mitigation: T4 routes the bundle
  through the 37-4 exporter facade only; review rejects any in-story crypto.
- **Tenant-view leakage:** a platform-scope control surfacing in the tenant endpoint pages tenants for
  platform internals. Mitigation: T5 filters strictly to `Scope == Tenant`; isolation test is
  load-bearing.
- **Event-store topology shift (Story 28-1 / Epic 30):** `COMPLIANCE.EVIDENCE_PACK.*` append to CP
  `DomainEvents` today (what `AlertRuleEvaluator` polls). Platform-scope events must stay CP-resident;
  tenant-scope follow per-tenant fan-out later. Mitigation: recorder is explicit about scope so the
  Epic 30 migration only touches tenant routing.
- **Migration discipline:** `EvidencePackArtifact` is additive — verify `has-pending-model-changes`
  reports none after the migration; mirror entity config in `TammaModelConfiguration.cs` only (single
  source per repo convention).
