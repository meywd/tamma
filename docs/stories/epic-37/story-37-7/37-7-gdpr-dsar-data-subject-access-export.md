# Story 37-7: GDPR DSAR — Data Subject Access Export

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **tenant owner/admin (SaaS), a platform owner, or a self-hosted user**,
I want to run a Data Subject Access Request (DSAR) that gathers **all** personal data Tamma
holds about a given subject — across the control plane and the relevant tenant schema(s) — and
produces a portable, machine-readable + human-readable export bundle,
So that Tamma can satisfy GDPR Art. 15 (right of access) / Art. 20 (data portability) and
CCPA right-to-know obligations with a complete, audited, RBAC-gated, secret-safe artifact.

## Priority

P1 — required for GDPR/CCPA compliance posture and the Epic 37 data-governance product layer.

## Context & Boundaries

A DSAR identifies a **data subject** — a `User`/member referenced by `userId` or `email` — and
collects every record that is *about* that person:

- **Control plane** (`ControlPlaneDbContext`): `User`, `TenantMembership`, `UserInvite`,
  `RefreshToken` (metadata only), `ApiKey` (metadata only), `AdminImpersonation` (where the subject
  is `TargetUserId` or `ImpersonatorUserId`), platform audit records (`PlatformEvent`, plus
  `DomainEvent` rows where the subject is actor/target), and `SecretRow` ownership (metadata only —
  "secret X exists", never the value).
- **Per-tenant schema** (`TenantDbContext` via `ITenantDbContextFactory.CreateAsync(tenantId)`):
  the subject's authored `PromptOverride` (`CreatedBy`/`UserId`), `Convention` (`CreatedBy`),
  `AgentConfig` (`CreatedBy`), `SanitizationRule`, `Alert` ownership, and tenant audit records
  (`DomainEvent` rows where the subject is actor/target).

The export runs as an **async job** (the existing `PlatformQueuedTask` / `TaskQueueProcessor`
pattern — same machinery 37-4 uses for audit export) and is **itself** an audited, RBAC-gated,
secret-redacting action. The collector is **data-map-driven**: a declarative `SubjectDataMap`
enumerates every PII source so adding a new PII-bearing entity is a single registration, not a
scatter of edits across services.

> **Architecture note.** This story targets the C# engine in `apps/tamma-elsa`
> (`Tamma.Api` + `Tamma.Data`). The legacy TypeScript `packages/api` is deleted and is NOT a
> target. New collector/data-map types live under
> `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/` (NEW directory).

## Acceptance Criteria

1. **Three initiation surfaces, per mode.**
   `POST /api/v1/orgs/{tenantId}/dsar` (gated `OwnerAccess` — tenant_owner/tenant_admin) and
   `POST /api/admin/dsar` (gated `PlatformOwnerAccess`) accept a subject reference
   (`{ userId }` or `{ email }`) and return **202 Accepted** with `{ jobId, status: "pending" }`.
   In **single-user mode** a self-service `POST /api/v1/dsar/self` lets the sole user export their
   own data with no admin role required (subject is pinned to the caller; a supplied
   `userId`/`email` that is not the caller is rejected 403).

2. **Identity resolution + verification.** The subject reference resolves to exactly one `User`
   (case-insensitive email match per the `LOWER(email)` index). An ambiguous/unknown reference
   returns 404. The requested subject **must belong to the requesting tenant** in SaaS mode
   (a member of `{tenantId}` via `TenantMembership`); a cross-tenant subject returns 404 (not 403,
   to avoid confirming the subject exists elsewhere). The DSAR request records the verified
   requester identity and the (separately verified) subject identity in the job record.

3. **Declarative data map.** A `SubjectDataMap` declares every personal-data source as a
   `SubjectDataSource` entry: `{ Category, Scope (control-plane|tenant), EntityType, PiiFields,
   SubjectKeySelector, Collect(...) }`. The map is the single registry of "where personal data
   about a subject lives" and is the only file edited when a new PII-bearing entity is added.

4. **Data-map completeness guard.** A test asserts that **every** entity flagged as PII-bearing
   (entities decorated with a `[ContainsPersonalData]` marker attribute, or listed in a
   `KnownPiiEntities` allow-list) appears in the `SubjectDataMap`. A PII entity missing from the
   map fails the build/test — the collector cannot silently miss a data category.

5. **DsarCollector compiles the bundle.** `DsarCollector` enumerates the `SubjectDataMap`,
   queries the control plane once and each in-scope tenant schema once (via
   `ITenantDbContextFactory`), and produces a structured result: one section per `Category`, each
   row annotated with **provenance** (`{ source, scope, entityType, tenantId? }`).

6. **Machine-readable + human-readable export.** The bundle is emitted as (a) `export.json` — a
   structured JSON document, one top-level key per category, with a top-level `manifest`
   (`{ subjectId, subjectEmail, generatedAt, requestedBy, mode, sourceCount, categories[],
   schemaVersion }`); and (b) `export.html` (or `export.md`) — a human-readable rendering of the
   same data for a non-technical data subject. Both are packaged together (zip) under the manifest.

7. **Secret-value exclusion (load-bearing).** Credential/secret **values** are NEVER included.
   `SecretRow`, `ApiKey`, `RefreshToken`, password hashes, and verification-token hashes contribute
   **metadata only** — e.g. `{ name, purpose, scope, createdAt, exists: true }` — never the
   protected value, hash, or ciphertext. A test asserts no secret value / hash / token plaintext
   appears anywhere in a generated bundle.

8. **Async job lifecycle.** `POST` enqueues a `PlatformQueuedTask` (type `compliance.dsar.export`)
   handled by a `DsarExportTaskHandler : IPlatformTaskHandler` on the `TaskQueueProcessor`. State
   transitions are observable via `GET /api/v1/orgs/{tenantId}/dsar/{jobId}` /
   `GET /api/admin/dsar/{jobId}`: `pending → collecting → packaging → ready` (or `failed` with a
   non-sensitive reason). The handler is crash-isolated and idempotent on retry.

9. **Artifact at rest, encrypted, time-limited download (37-4 parity).** The packaged bundle is
   stored encrypted at rest and downloadable via a single-use, time-limited token
   (`GET /api/.../dsar/{jobId}/download?token=...`), mirroring the 37-4 artifact + reveal-token
   pattern (`SecretRevealTokenRow`-style: `TokenHash`, `ExpiresAt`, single-consume, expiry sweep).
   The artifact and token expire on a configurable TTL; expired downloads return 410 Gone.

10. **Audited (37-1).** DSAR generation emits DCB events `GDPR.DSAR.REQUESTED` and
    `GDPR.DSAR.COMPLETED` (and `GDPR.DSAR.FAILED` on failure), flagged **sensitive** so 37-1's
    curated audit trail captures them. Tags: `subjectId`, `requestedBy`, `tenantId` (SaaS),
    `mode`, `jobId`. The download is audited too (`GDPR.DSAR.DOWNLOADED`). The **subject's data
    values are NOT in the event payload** — only references/counts.

11. **RBAC matrix.** SaaS `member` users cannot run a DSAR for others (403). A tenant
    owner/admin can DSAR only subjects belonging to their tenant (cross-tenant → 404). Platform
    owner can DSAR any subject. Single-user mode: self-only (the sole user). All three are enforced
    by the existing policy stack (`OwnerAccess`, `PlatformOwnerAccess`) plus an in-handler subject
    membership check.

12. **Multi-tenant subject coverage.** When a subject belongs to multiple tenants and the caller
    is the **platform owner**, the export covers the control plane + every tenant the subject is a
    member of. When the caller is a **tenant owner/admin**, the export is scoped to **their tenant
    only** (no other-tenant data leaks into a tenant-initiated DSAR).

13. **Tests.** Collector coverage vs `SubjectDataMap`; PII-entity completeness guard;
    secret-value exclusion; cross-tenant subject rejection (404); identity resolution
    (email/userId, ambiguous → 404); RBAC matrix (member 403, tenant scoping, platform-owner
    multi-tenant, single-user self-only); async job lifecycle (pending→ready, failure path,
    idempotent retry); `GDPR.DSAR.*` emission; download token single-use + expiry (410).

## Technical Design

### Directory / file layout (NEW unless noted)

```
apps/tamma-elsa/src/Tamma.Api/Services/Compliance/
  ISubjectDataMap.cs                # contract: enumerate SubjectDataSource entries
  SubjectDataMap.cs                 # the declarative registry of PII sources
  SubjectDataSource.cs             # record: Category, Scope, EntityType, PiiFields, Collect(...)
  ContainsPersonalDataAttribute.cs # marker on PII-bearing entities (or KnownPiiEntities list)
  IDsarCollector.cs                 # contract
  DsarCollector.cs                  # enumerates map, queries CP + tenant schemas, builds sections
  DsarBundle.cs                     # in-memory model: manifest + sections (provenance-tagged)
  DsarBundlePackager.cs            # json + html render → encrypted zip artifact
  DsarExportTaskHandler.cs         # IPlatformTaskHandler ("compliance.dsar.export")
  IDsarJobStore.cs / DsarJobStore.cs   # job lifecycle persistence (status, artifact ref, token)
  DsarRedactionPolicy.cs           # central secret/hash/value exclusion rules
apps/tamma-elsa/src/Tamma.Api/Endpoints/
  ComplianceDsarEndpoints.cs        # NEW — admin + org + self-service maps; or fold into Org/Admin
  OrgEndpoints.cs                   # MODIFY — add org DSAR routes (or delegate to new file)
  AdminEndpoints.cs                 # MODIFY — add admin DSAR routes
apps/tamma-elsa/src/Tamma.Data/Entities/
  DsarJob.cs                        # NEW — job record (CP-resident)
apps/tamma-elsa/src/Tamma.Data/
  ControlPlaneDbContext.cs          # MODIFY — DbSet<DsarJob>; CP collectors read here
  TammaModelConfiguration.cs        # MODIFY — DsarJob mapping (indices, CHECK on status)
  TenantDbContext.cs                # READ-ONLY in collector — no schema change
  Migrations/ControlPlane/          # NEW additive migration (DsarJob table)
apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/
  SubjectDataMapCompletenessTests.cs
  DsarCollectorTests.cs
  DsarRedactionTests.cs
  ComplianceDsarEndpointsTests.cs
  DsarExportTaskHandlerTests.cs
```

### `SubjectDataSource` (the registry primitive)

```csharp
public sealed record SubjectDataSource(
    string Category,                 // "account", "memberships", "authored-prompts", ...
    DsarScope Scope,                 // ControlPlane | Tenant
    Type EntityType,                 // typeof(User), typeof(PromptOverride), ...
    IReadOnlyList<string> PiiFields, // documented PII columns (for the completeness audit)
    // Returns provenance-tagged, already-redacted rows for one subject.
    Func<DsarQueryContext, CancellationToken, Task<IReadOnlyList<DsarRecord>>> Collect);

public enum DsarScope { ControlPlane, Tenant }

public sealed record DsarQueryContext(
    Guid SubjectUserId,
    string SubjectEmail,
    ControlPlaneDbContext Cp,
    TenantDbContext? Tenant,         // non-null only for Scope == Tenant
    Guid? TenantId);
```

Each entry's `Collect` delegate runs the narrow query for that one entity keyed by the subject and
maps to provenance-tagged `DsarRecord`s, applying `DsarRedactionPolicy` (AC7). The map is the only
place that "knows" a category exists; the collector is generic over it.

### Data map coverage (initial registration)

| Category | Scope | Entity | Subject key | Redaction |
|---|---|---|---|---|
| account | ControlPlane | `User` | `Id` / `Email` | drop `PasswordHash`, `*TokenHash` |
| memberships | ControlPlane | `TenantMembership` | `UserId` | full (no secrets) |
| invites | ControlPlane | `UserInvite` | invited email | full |
| refresh-sessions | ControlPlane | `RefreshToken` | `UserId` | **metadata only** (no token) |
| api-keys | ControlPlane | `ApiKey` | owner | **metadata only** (no key/hash) |
| secrets-owned | ControlPlane/Tenant | `SecretRow` | `OwnerUserId` | **metadata only** (`exists:true`) |
| impersonation | ControlPlane | `AdminImpersonation` | `TargetUserId`/`ImpersonatorUserId` | full |
| platform-audit | ControlPlane | `PlatformEvent` + `DomainEvent` | actor/target tag | redact secret tags |
| authored-prompts | Tenant | `PromptOverride` | `CreatedBy`/`UserId` | full |
| authored-conventions | Tenant | `Convention` | `CreatedBy` | full |
| agent-configs | Tenant | `AgentConfig` | `CreatedBy` | full |
| sanitization-rules | Tenant | `SanitizationRule` | owner | full |
| alerts | Tenant | `Alert` | owner | full |
| tenant-audit | Tenant | `DomainEvent` | actor/target tag | redact secret tags |

### `DsarCollector` flow

```csharp
public async Task<DsarBundle> CollectAsync(DsarRequest req, CancellationToken ct)
{
    // 1. resolve + verify subject (404 if unknown/ambiguous; membership check upstream)
    // 2. partition map: ControlPlane sources vs Tenant sources
    // 3. one CP context: run every ControlPlane source's Collect
    // 4. for each in-scope tenant (single in tenant-initiated; N for platform-owner multi-tenant):
    //      using ctx = await tenantFactory.CreateAsync(tenantId, ct);
    //      run every Tenant source's Collect
    // 5. assemble DsarBundle { manifest, sections[] }, provenance-tagged
}
```

In-scope tenants: tenant-initiated DSAR ⇒ exactly `{tenantId}`; platform-owner DSAR ⇒ every tenant
the subject is a member of (AC12).

### Packaging + artifact (37-4 parity)

`DsarBundlePackager` renders `export.json` (manifest + sections) and `export.html` (human-readable),
zips them, encrypts the zip at rest (reuse the 37-4 artifact encryption seam), and registers a
single-use download token (`SecretRevealTokenRow`-style: `TokenHash`, `ExpiresAt`, `Status`,
single-consume) on the `DsarJob`. Download endpoint validates the token, streams once, marks
consumed; expired/consumed ⇒ 410.

### Async job: `DsarExportTaskHandler`

```csharp
public sealed class DsarExportTaskHandler : IPlatformTaskHandler
{
    public string TaskType => "compliance.dsar.export";
    public async Task HandleAsync(PlatformQueuedTask task, CancellationToken ct)
    {
        // payload: { jobId, subjectUserId, requestedBy, tenantScope, mode }
        // collecting -> bundle = collector.CollectAsync(...)
        // packaging  -> artifact = packager.PackAsync(bundle)
        // ready      -> jobStore.MarkReady(jobId, artifactRef, downloadToken)
        // emit GDPR.DSAR.COMPLETED; on throw -> GDPR.DSAR.FAILED + retry semantics
    }
}
```

Throwing a normal exception → retryable (worker re-enqueues); `PlatformTaskTerminalException` for
permanently-bad payloads → dead-letter. Handler is idempotent: re-running a `ready` job is a no-op.

### Endpoints (per-mode shape)

```
POST /api/v1/orgs/{tenantId}/dsar              OwnerAccess        body {userId|email}      -> 202 {jobId}
GET  /api/v1/orgs/{tenantId}/dsar/{jobId}      OwnerAccess        -> status
GET  /api/v1/orgs/{tenantId}/dsar/{jobId}/download?token=  OwnerAccess -> artifact / 410
POST /api/admin/dsar                            PlatformOwnerAccess body {userId|email}     -> 202 {jobId}
GET  /api/admin/dsar/{jobId}                    PlatformOwnerAccess -> status
GET  /api/admin/dsar/{jobId}/download?token=    PlatformOwnerAccess -> artifact / 410
POST /api/v1/dsar/self                          AuthenticatedAny (single-user) -> 202 {jobId}  # subject pinned to caller
GET  /api/v1/dsar/self/{jobId}                  AuthenticatedAny (single-user) -> status
```

Tenant-route subject scoping rides the existing `RequireTenantMembershipFilter` for the caller plus
an in-handler check that the **subject** is a member of `{tenantId}`. Self-service is gated by
`ITammaModeProvider` returning `SingleUser`.

### Events (DCB)

| Type | When | Tags | Payload (NO subject values) |
|---|---|---|---|
| `GDPR.DSAR.REQUESTED` | on enqueue | subjectId, requestedBy, tenantId?, mode, jobId | `{ subjectRefType }` |
| `GDPR.DSAR.COMPLETED` | on ready | + sectionCount | `{ categories, recordCount, artifactBytes }` |
| `GDPR.DSAR.FAILED` | on failure | + reasonCode | `{ reasonCode }` (non-sensitive) |
| `GDPR.DSAR.DOWNLOADED` | on consume | + downloadedBy | `{}` |

Emitted via `IEventRepository.AppendAsync` (mirroring `OrgEndpoints.EmitTenantEvent`). System-scope
(admin/single-user) events use `TenantId = null`.

### `DsarJob` entity (CP-resident, NEW)

```csharp
public class DsarJob {
  public Guid Id { get; set; }
  public Guid SubjectUserId { get; set; }
  public string SubjectEmail { get; set; } = null!;
  public Guid RequestedByUserId { get; set; }
  public Guid? TenantId { get; set; }          // null in admin/single-user system-scope
  public string Mode { get; set; } = null!;     // "single-user" | "saas"
  public string Status { get; set; } = "pending"; // CHECK: pending|collecting|packaging|ready|failed
  public string? ArtifactRef { get; set; }      // encrypted-at-rest blob reference
  public byte[]? DownloadTokenHash { get; set; }
  public DateTime? DownloadExpiresAt { get; set; }
  public DateTime? DownloadConsumedAt { get; set; }
  public string? FailureReasonCode { get; set; }
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }
}
```

## Dependencies

- **37-1 (curated audit trail)** — DSAR events are sensitive, audited actions. `GDPR.DSAR.*` must
  flow into 37-1's curated trail. *(Sibling story dir exists; not yet authored — treat the
  sensitive-event taxonomy as the integration contract.)*
- **37-4 (async export + artifact pattern)** — reuse the `PlatformQueuedTask`/`TaskQueueProcessor`
  async-export flow, encrypted-at-rest artifact, and single-use time-limited download token. *(NEW
  sibling — if it lands first, consume its packager/token seam directly; if this lands first,
  factor the artifact+token helper so 37-4 reuses it.)*
- **Epic 28 (tenancy)** — control-plane + per-tenant schema access. Uses `ControlPlaneDbContext`
  for CP sources and `ITenantDbContextFactory.CreateAsync(tenantId)` for tenant sources
  (`LruPooledTenantConnectionResolver` is unconditionally wired per the tenancy plan).
- **Existing infra** — `IEventRepository`, `IPlatformTaskHandler`/`PlatformQueuedTask`,
  `OwnerAccess`/`PlatformOwnerAccess` policies, `RequireTenantMembershipFilter`,
  `ITammaModeProvider`, `SecretRevealTokenRow`-style token pattern.

## Testing Strategy

1. **Completeness guard (`SubjectDataMapCompletenessTests`)** — reflect over entities marked
   `[ContainsPersonalData]` (or the `KnownPiiEntities` list); assert each appears as a
   `SubjectDataSource` in `SubjectDataMap`. A new PII entity without a map entry fails the build.
2. **Collector coverage (`DsarCollectorTests`)** — seed a subject with rows in every category
   across CP + two tenant schemas; assert the bundle has one section per category, correct row
   counts, provenance tags, and that platform-owner multi-tenant covers both tenants while
   tenant-initiated covers only one (AC12).
3. **Redaction (`DsarRedactionTests`)** — generate a bundle for a subject who owns secrets,
   API keys, refresh tokens, and a password; assert NO secret value, ciphertext, key, hash, or
   token plaintext appears anywhere in `export.json`/`export.html`; assert metadata-only rows carry
   `exists: true` (AC7).
4. **Endpoints / RBAC (`ComplianceDsarEndpointsTests`)** — member 403; tenant owner/admin scoped
   to own tenant; cross-tenant subject 404; unknown/ambiguous subject 404; email vs userId
   resolution; platform owner any subject; single-user self-only (non-self ref 403); 202 + jobId
   shape.
5. **Job lifecycle (`DsarExportTaskHandlerTests`)** — pending→collecting→packaging→ready;
   failure path → failed + `GDPR.DSAR.FAILED`; idempotent retry (ready job re-run is a no-op);
   terminal vs retryable exception routing.
6. **Events** — `GDPR.DSAR.REQUESTED` on enqueue, `COMPLETED` on ready, `DOWNLOADED` on consume;
   payloads contain no subject data values.
7. **Download token** — single-use (second consume 410), expiry (410 after TTL), tamper (401).
8. **DB suites** run via `sg docker -c "dotnet test ..."` (Postgres-bound), per the project
   .NET-test convention.

## Estimated Effort

5-6 days.

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/SubjectDataMap.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ISubjectDataMap.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/SubjectDataSource.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ContainsPersonalDataAttribute.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/IDsarCollector.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/DsarCollector.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/DsarBundle.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/DsarBundlePackager.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/DsarRedactionPolicy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/DsarExportTaskHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/IDsarJobStore.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/DsarJobStore.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/ComplianceDsarEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/ComplianceServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/DsarJob.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `DbSet<DsarJob>`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (DsarJob mapping + CHECK) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*_AddDsarJob.cs` | Create (additive) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI + route + task-handler registration) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` | Modify (admin DSAR routes) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | Modify (org DSAR routes) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/SubjectDataMapCompletenessTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/DsarCollectorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/DsarRedactionTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ComplianceDsarEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/DsarExportTaskHandlerTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions
3. Read sibling stories 37-1 (audit taxonomy) and 37-4 (async export + artifact) once authored —
   reuse their artifact/token seam rather than re-inventing it
4. Reviewed the tenancy access seams (`ITenantDbContextFactory`, `LruPooledTenantConnectionResolver`)
5. Planned a TDD approach (Red-Green-Refactor) — completeness guard and redaction tests first

### Architecture target

- **Target `apps/tamma-elsa` (C#) only.** `packages/api` (TypeScript) is deleted — never a target.
- CP personal data: `ControlPlaneDbContext`. Tenant personal data:
  `ITenantDbContextFactory.CreateAsync(tenantId)` — never raw SQL across schemas.
- Async work uses the `PlatformQueuedTask` + `TaskQueueProcessor` + `IPlatformTaskHandler` machinery
  (same as 37-4 and the Cranl provisioning flow), not an ad-hoc thread.

### Secret safety (the highest-risk requirement)

`DsarRedactionPolicy` is the single chokepoint for value exclusion — every `Collect` delegate runs
its rows through it. Default-deny: a field is included only if explicitly listed in the source's
non-secret projection. `SecretRow`/`ApiKey`/`RefreshToken`/`PasswordHash`/`*TokenHash` columns map
to `{ exists: true, ... }` metadata. The `DsarRedactionTests` scan the full serialized bundle for
known secret material and MUST fail if any leaks.

### Identity verification subtlety

Resolving by `email` uses the same case-insensitive semantics as the `LOWER(email)` partial unique
index; resolving by `userId` is exact. Reject ambiguous matches with 404 (do not partial-match).
The requester's identity comes from the verified JWT/claims; the subject is verified independently
(existence + tenant membership) — never trust the request body's claim that the subject "belongs"
to the tenant.

### Mode awareness

`ITammaModeProvider` gates which surfaces are live: SaaS exposes org + admin routes; single-user
exposes the self-service route. A single deployment is one mode for the process lifetime — do not
branch per-request on mode beyond reading the provider.

## Logging Requirements

- **INFO**: DSAR requested (jobId, subjectId, requestedBy, mode), collection started/finished
  (sectionCount, recordCount), artifact packaged (bytes), download served (jobId), job completed.
- **DEBUG**: per-source row counts during collection, tenant-schema context opened/closed,
  flush cycle of the packager.
- **WARN**: subject resolved to multiple users (rejected), download token expired/consumed,
  retryable handler failure (re-enqueue).
- **ERROR**: collection failed (reasonCode, no subject values), artifact encryption/storage
  failure, task moved to dead-letter.
- **Structured context**: `{ jobId, subjectId, requestedBy, tenantId, mode, sectionCount,
  recordCount }` where applicable.
- **Credential safety**: NEVER log subject personal data values, secret values/hashes, download
  tokens, or artifact contents. Log references and counts only.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
