# Story 37-11: SOC2-Aligned Control Mapping & Evidence Pack

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **platform owner preparing for a SOC2 Type II audit** (and as a **tenant under their own attestation**),
I want a declarative control catalog that maps SOC2 Trust Services Criteria to the Tamma features that satisfy them (auth/RBAC, secret cabinet encryption, audit logging, access reviews, backups, change management), plus an on-demand, signed evidence pack that bundles the supporting audit excerpts, chain-verification results, retention/legal-hold state, and config attestations for a chosen period,
So that the raw audit machinery built in Epic 37 becomes a usable compliance posture report — each control shows its status (satisfied / gap), its supporting evidence, and any findings — that an auditor can be handed directly.

## Priority

P2 — turns the audit substrate into auditor-facing compliance product; required for Tamma's own SOC2 and for tenant attestations.

## Context & Boundary

This story is the **compliance product layer** that sits on top of the Epic 37 audit substrate. It does **not** create new audit events, retention machinery, or hash-chain logic — it **reads** them and **maps** them to a control framework.

- **Target codebase**: C# `apps/tamma-elsa`. Control-catalog domain logic lives in `Tamma.Core`; evidence-pack collection + endpoints live in `Tamma.Api`; persistence/read-models live in `Tamma.Data`.
- **`packages/api` is DELETED** — it is never a target. All compliance code is C#.
- **Per-mode** (mandatory two-scoping-model answer, per CLAUDE.md "Operating Modes"): a control's *status* and its *evidence* are scoped differently in single-user vs SaaS — see [Per-Mode Ownership](#per-mode-ownership) below. The endpoint shape is identical between modes; auth middleware decides scope.
- **Scope split**: platform-level (Tamma's own SOC2 — `PlatformOwnerAccess`) is the primary surface; per-tenant (a tenant under their own attestation — `tenant_owner`/`tenant_admin`) is the secondary surface and sees **only tenant-scoped evidence**, never platform internals.

### Dependency status (verified 2026-06-17, repo @ main)

The dependency stories supply the read-models and machinery this story consumes. Several are **not yet built** — this story declares the seams it binds to and marks them NEW where the artifact does not exist on disk today.

| Dep | Supplies | On disk today? |
|---|---|---|
| 37-1 | Curated audit read-model (`AuditRecord` entity + query API) | **NEW** — no `AuditRecord` entity exists in `Tamma.Data/Entities/`; this story reads it via the 37-1 query service |
| 37-2 | Hash-chain verification result over the audit read-model | **NEW** — no hash-chain verifier exists yet |
| 37-3 | Audit query/search API | **NEW** |
| 37-4 | Signed/encrypted/expiring export machinery (`AuditExportService`) | **NEW** — no `AuditExportService` exists; evidence pack reuses it |
| 37-5 | Retention policy snapshot | **NEW** |
| 37-6 | Legal-hold state | **NEW** |
| 37-10 | Audit dashboard surfaces (this story adds a control-status tab) | **NEW** |

**Real, verified controls this story maps to** (these exist on disk and are the *evidence sources* the catalog points at):

| Control area | Real artifact (verified) |
|---|---|
| Auth / RBAC | `src/Tamma.Api/Program.cs` policies (`OwnerAccess`, `PlatformOwnerAccess`, `MemberAccess`); `src/Tamma.Api/Auth/Permissions.cs`; `src/Tamma.Api/Authorization/TenantRoleHierarchy.cs`, `RequireTenantMembershipFilter.cs` |
| Encryption / secret cabinet (Epic 29) | `src/Tamma.Api/Services/Secrets/` (KEK provider, `KekRotationCoordinator`, `SecretRow`/`SecretVersionRow`); AES-GCM at rest via `TenantSecretProtector` |
| Audit logging | `src/Tamma.Data/Entities/DomainEvent.cs` (+ `SequenceNumber`); `IEventRepository.AppendAsync`; `ISecretAccessAuditor` (`SECRET.READ/WRITE/ROTATE.*/REVEAL/VERSION.REVOKED`) |
| Access / change monitoring | `src/Tamma.Api/Services/Alerts/` (`IAlertSink.RaiseAsync`, `AlertRuleEvaluator`); `AdminImpersonation` entity + `AdminImpersonationService` |
| Change management | `src/Tamma.Data/Entities/WorkflowDefinition.cs`, `WorkflowInstance.cs`; `KekRotation` entity |
| Backups (Epic 28) | `src/Tamma.Api/Services/Provisioning/TenantMoveService.cs`; tenant-database pool placement; `Cranl` backup capability flags |

## Acceptance Criteria

1. **Declarative ControlCatalog.** A code-resident, immutable `ControlCatalog` (`src/Tamma.Core/Compliance/ControlCatalog.cs`) maps each in-scope SOC2 criterion (at minimum CC6.x logical access, CC7.x monitoring/incident, CC8.x change management; plus CC1/CC2 governance and A1 availability where Tamma has supporting evidence) to a `ControlDefinition` carrying: the **required catalog action codes** (real audit event types, e.g. `SECRET.REVEAL`, `SECRET.ROTATE.SUCCESS`, `ALERT.RAISED`), a **retention floor** (minimum days the supporting evidence must be retained), a **chain-verification requirement** (bool: does this control require hash-chain integrity over its evidence), the **RBAC policy** that enforces the control (e.g. `PlatformOwnerAccess`), and an **ISO 27001 Annex A cross-reference** where the criteria overlap (informational).

2. **No orphan references.** Every action code referenced by the catalog resolves to a real, emitted event type. A test enumerates the catalog and asserts each referenced action code is a known/emitted type (cross-checked against the audit catalog from 37-1/37-3 and the constants in `ISecretAccessAuditor`, alert event types, etc.) — the build fails if a control points at a non-existent code.

3. **Platform control-status endpoint.** `GET /api/admin/compliance/controls` (**`PlatformOwnerAccess`**) returns every control with: identity (criterion id, title, category, ISO xref), `status` (`satisfied` | `gap`), `lastEvidenceAt` (most recent supporting audit record timestamp), `supportingRecordCount`, retention/chain requirements, and the enforcing RBAC policy.

4. **Tenant control-status endpoint.** `GET /api/v1/orgs/{tenantId}/compliance/controls` (`tenant_owner`/`tenant_admin`; `member` → read-only list or 403 on any future mutation) returns the same shape but evaluated against **tenant-scoped evidence only** — platform-internal controls (e.g. KEK rotation, platform backups, seed/config gaps) are **never** included in the tenant view.

5. **Gap detection with actionable findings.** A `ControlEvaluator` flags a control as `gap` when (a) it has **no supporting evidence in the lookback window** (e.g. an access-review control with no access-review/impersonation event in N days, default configurable `Compliance:EvidenceLookbackDays`), or (b) the effective retention for its evidence is **below the control's floor**, or (c) chain verification is required but the latest 37-2 verification **failed**. Each gap carries a human-readable `finding` string and a `remediationHint`.

6. **Evidence-pack generation (async, signed).** `POST /api/admin/compliance/evidence-pack` (`PlatformOwnerAccess`) and `POST /api/v1/orgs/{tenantId}/compliance/evidence-pack` (tenant scope) accept `{ periodStart, periodEnd, criteria?: string[] }`, return **`202 Accepted`** with a job/artifact id, and run the long collection on the existing platform task-queue thread (mirrors the provisioning/KEK-rotation `Results.Accepted` pattern). The generated bundle contains: the **control → evidence map** (each control with its supporting audit excerpts pulled via the 37-1/37-3 query API), the **hash-chain verification result** (37-2), the **retention policy snapshot** (37-5), the **legal-hold state** (37-6), and a **coverage summary** (counts of satisfied vs gap controls, per-category rollup).

7. **Evidence pack reuses export machinery & is encrypted/expiring.** Bundle production goes through the 37-4 `AuditExportService` (NEW) so the artifact is **signed**, **encrypted at rest**, and **expiring** exactly like other audit exports — no bespoke crypto in this story. `GET .../compliance/evidence-pack/{id}` returns generation status (`pending → collecting → signing → ready`/`failed`) and, when ready, a short-lived download reference.

8. **Evidence pack is itself signed AND audited.** Generation emits `COMPLIANCE.EVIDENCE_PACK.GENERATED` (sensitive, audited) via `IEventRepository.AppendAsync` with tags `{ scope, tenantId?, periodStart, periodEnd, criteriaCount, coverageSatisfied, coverageGap, mode }`; a download emits `COMPLIANCE.EVIDENCE_PACK.DOWNLOADED`. The signature over the bundle is verifiable independently of Tamma (signature + the public verification material are included in the pack manifest), so an auditor can confirm the pack was not tampered with after generation.

9. **Per-mode scoping is honored.** In **single-user** mode the sole user (their JWT) sees system + their own evidence as one feed (`tenantId` null). In **SaaS** mode platform controls are `PlatformOwnerAccess`-only and never leak to tenants; tenant controls are `tenantId`-scoped via the existing `/api/v1/orgs/{tenantId}/*` path-tenant gate. Mode is read from `ITammaModeProvider` (`src/Tamma.Api/Services/PromptStore/TammaMode.cs`).

10. **Control-status report/dashboard data shape.** The platform endpoint additionally exposes a `GET /api/admin/compliance/controls/summary` rollup (counts by category and status, oldest-gap age, last evidence-pack timestamp) suitable for a dashboard widget on the 37-10 audit dashboard (UI itself is a thin add in 37-10; this story ships the data + DTO).

11. **RBAC enforced end-to-end.** Platform endpoints reject non-`platform_admin` callers (403); tenant endpoints reject cross-tenant access (404 — never reveal existence) and reject `member`-role mutations (403). A test matrix covers platform-owner / tenant_owner / tenant_admin / member / cross-tenant.

12. **Evidence-pack artifact lifecycle is audited & bounded.** Expired/failed packs are not downloadable (410/404); the artifact id is opaque (no PII / criterion leakage in the id); the manifest never embeds secret material (the secret cabinet is *attested to*, never *exported*).

13. **Config attestations are point-in-time and signed into the pack.** The pack includes a `ConfigAttestation` section: a redacted snapshot of compliance-relevant configuration (encryption-at-rest enabled, KEK rotation cadence, retention policy floors, alert channels configured count, mode) — **values that prove a control's posture, never the secrets themselves** (no API keys, no connection strings). The attestation is collected at generation time and covered by the pack signature.

14. **Tests.** (a) Catalog maps to real catalog codes — no orphan references (AC2); (b) gap detection fires on missing/aged evidence, sub-floor retention, and failed chain verification; (c) evidence-pack signature round-trips (generate → verify signature → tamper → verify fails); (d) RBAC per mode (AC11 matrix); (e) tenant view never contains platform-scope controls; (f) `COMPLIANCE.EVIDENCE_PACK.GENERATED` event emitted exactly once per successful generation with correct tags.

## Technical Design

### Component layout

```
src/Tamma.Core/Compliance/                         # NEW directory — pure, dependency-light
  ControlCatalog.cs            # immutable list of ControlDefinition (the framework map)
  ControlDefinition.cs         # record: CriterionId, Title, Category, IsoXref,
                               #   RequiredActionCodes[], RetentionFloorDays,
                               #   RequiresChainVerification, RbacPolicy, Scope (Platform|Tenant)
  TrustServiceCriteria.cs      # SOC2 TSC enum/constants (CC1..CC9, A1, C1, PI1, P1..)
  ControlStatus.cs             # record: status, lastEvidenceAt, supportingRecordCount,
                               #   finding?, remediationHint?
  ControlScope.cs              # Platform | Tenant

src/Tamma.Api/Services/Compliance/                 # NEW directory
  IControlEvaluator.cs
  ControlEvaluator.cs          # reads 37-1/37-3 audit read-model + 37-5 retention +
                               #   37-2 chain result → produces ControlStatus per control
  IEvidencePackService.cs
  EvidencePackService.cs       # orchestrates collection → 37-4 AuditExportService (sign/encrypt)
  EvidencePackJob.cs           # status record (pending/collecting/signing/ready/failed)
  ConfigAttestationCollector.cs# redacted point-in-time config snapshot (AC13)
  ComplianceEventTypes.cs      # COMPLIANCE.EVIDENCE_PACK.GENERATED / .DOWNLOADED
  EvidencePackTaskHandler.cs   # IPlatformTaskHandler — runs the async collection (AC6)

src/Tamma.Api/Endpoints/
  ComplianceEndpoints.cs       # NEW — admin section + tenant section (mirror AlertEndpoints.cs)

src/Tamma.Data/Entities/
  EvidencePackArtifact.cs      # NEW — persisted pack metadata (id, scope, tenant_id, period,
                               #   status, coverage counts, expires_at, signature_ref)
```

### ControlDefinition (catalog row)

```csharp
// src/Tamma.Core/Compliance/ControlDefinition.cs  (NEW)
public sealed record ControlDefinition(
    string CriterionId,            // "CC6.1"
    string Title,                  // "Logical access controls restrict ..."
    string Category,               // "CC6 — Logical & Physical Access"
    string? IsoXref,               // "A.9.2.x" (informational overlap)
    IReadOnlyList<string> RequiredActionCodes, // real event types that evidence this control
    int RetentionFloorDays,        // evidence must be retained >= this
    bool RequiresChainVerification,
    string RbacPolicy,             // "PlatformOwnerAccess"
    ControlScope Scope);           // Platform | Tenant
```

### Catalog excerpt (illustrative — real action codes only)

```csharp
// src/Tamma.Core/Compliance/ControlCatalog.cs  (NEW)
public static class ControlCatalog
{
    public static readonly IReadOnlyList<ControlDefinition> All = new[]
    {
        new ControlDefinition(
            CriterionId: "CC6.1",
            Title: "The entity implements logical access security controls (RBAC, least privilege).",
            Category: "CC6 — Logical and Physical Access",
            IsoXref: "A.9.2",
            RequiredActionCodes: new[] { "USER.LOGIN.SUCCESS", "ADMIN.IMPERSONATION.STARTED" },
            RetentionFloorDays: 365,
            RequiresChainVerification: true,
            RbacPolicy: "PlatformOwnerAccess",
            Scope: ControlScope.Platform),

        new ControlDefinition(
            CriterionId: "CC6.7",
            Title: "Data at rest is encrypted (secret cabinet, KEK-wrapped secrets).",
            Category: "CC6 — Logical and Physical Access",
            IsoXref: "A.10.1",
            RequiredActionCodes: new[] { "SECRET.ROTATE.SUCCESS", "SECRET.VERSION.REVOKED" },
            RetentionFloorDays: 365,
            RequiresChainVerification: true,
            RbacPolicy: "PlatformOwnerAccess",
            Scope: ControlScope.Platform),

        new ControlDefinition(
            CriterionId: "CC7.2",
            Title: "Security events are monitored and alerted on.",
            Category: "CC7 — System Operations",
            IsoXref: "A.12.4",
            RequiredActionCodes: new[] { "ALERT.RAISED" },
            RetentionFloorDays: 180,
            RequiresChainVerification: false,
            RbacPolicy: "PlatformOwnerAccess",
            Scope: ControlScope.Platform),

        new ControlDefinition(
            CriterionId: "CC8.1",
            Title: "Changes are authorized, tested, and tracked (workflow defs, KEK rotation).",
            Category: "CC8 — Change Management",
            IsoXref: "A.12.1.2",
            RequiredActionCodes: new[] { "SECRET.ROTATE.STARTED", "SECRET.ROTATE.SUCCESS" },
            RetentionFloorDays: 365,
            RequiresChainVerification: true,
            RbacPolicy: "PlatformOwnerAccess",
            Scope: ControlScope.Platform),
        // ... tenant-scope controls (e.g. tenant access reviews, tenant data export logging)
        //     carry Scope = ControlScope.Tenant.
    };
}
```

> **AC2 enforcement:** a test enumerates `ControlCatalog.All.SelectMany(c => c.RequiredActionCodes)` and asserts each is present in the audit-catalog known-codes set (from 37-1/37-3) ∪ `ISecretAccessAuditor` constants ∪ alert/known event types. Any orphan fails the build. (Where a 37-1 dependency code is not yet emitted on disk, the test asserts against the **declared** catalog from 37-1 — the seam — and is marked pending until 37-1 lands; see Dev Notes.)

### ControlEvaluator

```csharp
// src/Tamma.Api/Services/Compliance/ControlEvaluator.cs  (NEW)
public sealed class ControlEvaluator : IControlEvaluator
{
    // deps (all NEW seams from dep stories + real existing services):
    //   IAuditQuery auditQuery            // 37-1/37-3 read-model query (NEW)
    //   IChainVerifier chainVerifier      // 37-2 (NEW)
    //   IRetentionPolicyReader retention  // 37-5 (NEW)
    //   ITammaModeProvider mode           // existing — TammaMode.cs
    //   IOptions<ComplianceOptions> opts  // EvidenceLookbackDays etc.

    public async Task<ControlStatus> EvaluateAsync(
        ControlDefinition control, ComplianceScopeKey scope, CancellationToken ct)
    {
        // 1. count + latest supporting record in lookback window via auditQuery
        // 2. effective retention for the control's evidence via retention reader
        // 3. chain verification result (only if control.RequiresChainVerification)
        // gap when: no evidence in window, OR retention < floor, OR chain failed.
    }
}
```

### Evidence pack — async generation through 37-4 export

```csharp
// POST .../compliance/evidence-pack -> 202 + { artifactId }
// 1. ComplianceEndpoints validates period + (optional) criteria filter, RBAC, scope.
// 2. Persists EvidencePackArtifact { status = pending }, enqueues a PlatformQueuedTask.
// 3. EvidencePackTaskHandler (IPlatformTaskHandler) runs on the existing
//    PlatformTaskWorker thread:
//       collecting -> build control->evidence map (auditQuery excerpts),
//                     chain result (37-2), retention snapshot (37-5),
//                     legal-hold state (37-6), config attestation (AC13),
//                     coverage summary.
//       signing    -> hand the assembled bundle to AuditExportService (37-4) which
//                     signs + encrypts + sets expiry. Store signature_ref + expires_at.
//       ready      -> append COMPLIANCE.EVIDENCE_PACK.GENERATED (sensitive, audited).
// 4. GET .../evidence-pack/{id} reports status; when ready returns short-lived ref.
//    Download appends COMPLIANCE.EVIDENCE_PACK.DOWNLOADED.
```

The handler **never** invents crypto — `AuditExportService` (37-4) owns sign/encrypt/expiry so the evidence pack inherits the same tamper-evidence guarantees and expiry policy as every other audit export.

### Endpoints (mirror `AlertEndpoints.cs` admin + tenant sections)

```
# Platform (PlatformOwnerAccess)
GET  /api/admin/compliance/controls                  -> ControlStatus[] (platform scope)
GET  /api/admin/compliance/controls/summary          -> coverage rollup (AC10)
POST /api/admin/compliance/evidence-pack             -> 202 { artifactId }
GET  /api/admin/compliance/evidence-pack/{id}        -> status / download ref
GET  /api/admin/compliance/evidence-packs            -> paged list (50/500)

# Tenant (tenant_owner / tenant_admin; member read-only)
GET  /api/v1/orgs/{tenantId}/compliance/controls     -> ControlStatus[] (tenant scope ONLY)
POST /api/v1/orgs/{tenantId}/compliance/evidence-pack-> 202 { artifactId }
GET  /api/v1/orgs/{tenantId}/compliance/evidence-pack/{id}
```

Tenant routes attach to the existing `/api/v1/orgs/{tenantId}` group (path-tenant gate, `Program.cs` ~1505–1512) and filter the catalog to `Scope == Tenant`. The admin group mirrors the `AlertEndpoints` admin section (paging defaults 50/500).

### Per-Mode Ownership

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who sees **platform-scope** controls/evidence (KEK rotation, backups, seed config)? | The sole user — it's their instance; one feed, `tenantId` null. | Platform owner ONLY (`PlatformOwnerAccess`). Never exposed to tenants — would leak platform internals. |
| Who sees **tenant-scope** controls (tenant access reviews, tenant data-export logging)? | The sole user (`tenantId` null; same XOR as the rest of the schema). | `tenant_owner`/`tenant_admin` via `/api/v1/orgs/{tenantId}/...`; `member` read-only. |
| Who can generate an evidence pack? | The user. | Platform-scope pack: platform owner. Tenant-scope pack: tenant_owner/tenant_admin (or platform owner). |
| Where is `COMPLIANCE.EVIDENCE_PACK.GENERATED` tagged? | `tenantId` null. | Platform pack → `tenantId` null (admin feed); tenant pack → `tenantId` set (tenant feed). |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Persistence

```csharp
// src/Tamma.Data/Entities/EvidencePackArtifact.cs  (NEW)
public class EvidencePackArtifact
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = "platform"; // platform | tenant
    public Guid? TenantId { get; set; }             // set for tenant scope; null otherwise
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string Status { get; set; } = "pending"; // pending|collecting|signing|ready|failed
    public int CoverageSatisfied { get; set; }
    public int CoverageGap { get; set; }
    public string? SignatureRef { get; set; }       // reference into 37-4 export store
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }         // mirrors 37-4 export expiry
}
```

Additive EF migration under `src/Tamma.Data/Migrations/ControlPlane/`; entity config in `TammaModelConfiguration.cs` (single source per repo convention). Verify `has-pending-model-changes` reports none after the migration. Per CLAUDE.md "no migration anxiety" — additive, no data backfill.

## Dependencies

- **Prerequisite 37-1** (audit read-model): `ControlEvaluator` reads supporting records via the 37-1 query service; AC2 catalog cross-check uses the 37-1 known-codes set. **NEW** on disk.
- **Prerequisite 37-2** (hash chain): chain-verification requirement on controls consumes 37-2's verifier result. **NEW**.
- **Prerequisite 37-3** (query/search API): evidence excerpts in the pack are pulled via 37-3. **NEW**.
- **Prerequisite 37-4** (export machinery): evidence pack is produced through `AuditExportService` for sign/encrypt/expiry. **NEW**.
- **Prerequisite 37-5** (retention): retention policy snapshot in the pack + sub-floor gap detection. **NEW**.
- **Prerequisite 37-6** (legal hold): legal-hold state section of the pack. **NEW**.
- **Related 37-10** (audit dashboard): this story ships the control-status data + DTO for a thin dashboard widget. **NEW**.
- **Reuses (real, on disk)**: Epic 29 secret cabinet (`Services/Secrets/`) and Epic 28 backup/move (`TenantMoveService`) as *evidence sources*; `IAlertSink`/`AlertRuleEvaluator`; `IEventRepository`; `ITammaModeProvider`; `PlatformTaskWorker`/`IPlatformTaskHandler` for async generation; `TenantRoleHierarchy`/`RequireTenantMembershipFilter` for RBAC; auth policies in `Program.cs`.

## Testing Strategy

Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/` (xUnit) and `tests/Tamma.Core.Tests/Compliance/`. Docker-bound suites run via `sg docker -c "dotnet test ..."`.

1. **Catalog integrity (Core, no DB)**: `ControlCatalog.All` has unique criterion ids; every `RequiredActionCodes` entry resolves to a known/emitted event type (AC2 — no orphans); every control carries a valid `RbacPolicy` and `Scope`.
2. **Gap detection (Api)**: with a stubbed `IAuditQuery`/`IRetentionPolicyReader`/`IChainVerifier` — control with no evidence in window → `gap` + finding; evidence present but retention < floor → `gap`; chain required + verification failed → `gap`; everything satisfied → `satisfied` with `lastEvidenceAt` + count.
3. **Evidence-pack signature round-trip**: generate (handler) → bundle signed by 37-4 stub → verify signature passes → mutate a byte → verify fails. Asserts the pack is tamper-evident.
4. **Async lifecycle**: `POST` returns 202 + artifact id with status `pending`; running the task handler transitions `collecting → signing → ready`; `GET` reflects each state; failure path sets `failed` and is not downloadable.
5. **RBAC per mode (AC11 matrix)**: platform endpoint — platform_admin 200, non-admin 403; tenant endpoint — tenant_owner/tenant_admin 200, member read-only/403 on generate, cross-tenant 404; single-user mode — sole user sees platform + tenant controls in one list.
6. **Tenant-view isolation**: `GET /api/v1/orgs/{id}/compliance/controls` never returns a `Scope == Platform` control; never another tenant's evidence.
7. **Event emission**: successful generation appends exactly one `COMPLIANCE.EVIDENCE_PACK.GENERATED` with correct tags (scope, tenantId?, period, coverage counts, mode); download appends `COMPLIANCE.EVIDENCE_PACK.DOWNLOADED`.
8. **Config attestation redaction (AC13)**: attestation section contains posture booleans/counts and **no** secret values (assert no connection string / API-key shaped strings appear).

## Estimated Effort

4–5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Compliance/ControlCatalog.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Compliance/ControlDefinition.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Compliance/TrustServiceCriteria.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Compliance/ControlStatus.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Compliance/ControlScope.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/IControlEvaluator.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ControlEvaluator.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/IEvidencePackService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/EvidencePackService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/EvidencePackJob.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ConfigAttestationCollector.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/EvidencePackTaskHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ComplianceEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/ComplianceEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/ComplianceServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/EvidencePackArtifact.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*_AddEvidencePackArtifact.cs` | Create (EF gen) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (DbSet) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map endpoints + register services) |
| `apps/tamma-elsa/tests/Tamma.Core.Tests/Compliance/ControlCatalogTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ControlEvaluatorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/EvidencePackServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ComplianceEndpointsTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (esp. any audit/compliance spikes from Epic 37).
3. Confirmed the **dependency seams**: 37-1 (`IAuditQuery`/known-codes), 37-2 (`IChainVerifier`), 37-4 (`AuditExportService`), 37-5 (`IRetentionPolicyReader`), 37-6 (legal-hold reader). These are **NEW** — if a dependency interface is not yet on disk, define the consuming seam as an interface in this story and bind to the real implementation when the dependency lands; do not duplicate the dependency's logic.
4. Planned TDD (Red-Green-Refactor); the catalog-integrity test (AC2) is the first red test.

### Dependency-not-yet-landed handling

This story's primary content — the **ControlCatalog** and **per-mode RBAC/endpoints** — is buildable today against the real existing controls (auth, secret cabinet, alerts, audit events). The evidence-*collection* paths depend on 37-1/37-3/37-4/37-5/37-6 read-models. Code against narrow consumer interfaces (`IAuditQuery`, `IChainVerifier`, `IRetentionPolicyReader`, `ILegalHoldReader`, `AuditExportService`); where the dependency is unmerged, ship the interface + a test double and gate the live wiring behind the dependency's DI registration. **Never** target `packages/api` (deleted) and **never** re-implement a dependency's machinery here.

### Why a code-resident catalog (not a DB table)

The control framework is **policy-as-code**: it ships with Tamma, is reviewed in PRs, and must be version-controlled for the audit trail (a catalog change is itself a change-management event). Mirrors the precedent of `SystemPrompts.cs` (system defaults in code) and `BuiltInAlertRules.cs` (built-in rules in code, seeded idempotently). Only the *generated artifacts* (`EvidencePackArtifact`) and the *evidence* (DCB events) live in the DB.

### Config attestation must never become a secret exfiltration path

AC13 is deliberately narrow: the attestation proves **posture** (encryption-at-rest = true, KEK rotation cadence = 90d, retention floor = 365d, mode = SaaS, alert channels configured = 3) — never the secret material itself. The `ConfigAttestationCollector` reads only non-sensitive config and explicitly excludes anything from the secret cabinet, connection strings, or API keys. The redaction test (Testing #8) is load-bearing.

### Async generation reuses the platform task queue

Evidence-pack collection (audit excerpts across a period + chain verify + retention + legal hold) is long-running, so it follows the established 202-Accepted + `PlatformTaskWorker`/`IPlatformTaskHandler` pattern (same as tenant provisioning and KEK rotation — see `AdminEndpoints.cs` ~442 / `KekRotationEndpoints.cs` ~54). Do not block the request thread.

### Event-store topology (Story 28-1 / Epic 30 forward-compat)

`COMPLIANCE.EVIDENCE_PACK.*` events append to the CP `DomainEvents` store via `IEventRepository` (what `AlertRuleEvaluator` polls). Platform-scope evidence-pack events must stay **CP-resident**; tenant-scope events follow the per-tenant fan-out when Epic 30 lands — keep the recorder explicit about scope so the migration only touches tenant routing.

## Logging Requirements

- **INFO**: evidence-pack requested (scope, tenantId?, period, criteriaCount); pack ready (artifactId, coverageSatisfied/Gap, durationMs); control-status endpoint queried (scope, controlCount, gapCount); pack downloaded (artifactId).
- **DEBUG**: per-control evaluation result (criterionId, status, supportingRecordCount, lastEvidenceAt); task-queue state transition (collecting/signing/ready).
- **WARN**: control flagged `gap` with finding (criterionId, reason — no-evidence / sub-floor-retention / chain-failed); evidence-pack generation retried.
- **ERROR**: evidence-pack generation failed (artifactId, stage, error); 37-4 export sign/encrypt failure; chain-verifier (37-2) unavailable.
- **Structured context**: include `{ scope, tenantId, criterionId, artifactId, periodStart, periodEnd, mode }` where applicable.
- **Credential safety**: NEVER log secret cabinet material, connection strings, API keys, or signature private material. The config attestation log line logs **counts/booleans only**, never values.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
