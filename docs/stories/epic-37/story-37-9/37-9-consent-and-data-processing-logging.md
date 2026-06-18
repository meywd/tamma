# Story 37-9: Consent & Data-Processing Logging

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **tenant owner (SaaS) or self-hosting user (single-user)**,
I want every consent and data-processing decision — terms/DPA acceptance, BYOK
vs platform-provided data handling, telemetry opt-in/out, and AI-training-data
usage — recorded as immutable, versioned, who/what/when history that feeds the
audit trail,
So that I can prove (for SOC2 / GDPR Records of Processing Activities) exactly
when consent was granted or withdrawn, and the platform can gate behaviour on
the current effective consent.

## Priority

P2 — GDPR/SOC2 control evidence. Part of the Epic 37 compliance product layer
(consent logging is an explicit deliverable of the epic theme: "GDPR controls
(DSAR export, right-to-erasure, consent logging)").

## Context & Background

Tamma processes user repository content, prompts, and LLM payloads on behalf of
tenants. GDPR Art. 6/7 requires a demonstrable record of the legal basis and
consent for each processing purpose; Art. 30 requires a Record of Processing
Activities (ROPA). SOC2 privacy criteria require the same evidence trail. Today
Tamma has **no consent capture and no ROPA** — there is no way for a tenant to
prove when they accepted the DPA or opted out of telemetry.

This story adds an **append-only consent log** (`consent_records`) plus a small
**ROPA registry** (`processing_activities`), both per-mode owned (mirroring the
`prompt_overrides` / `Convention` dual-scoping precedent), with every change
emitting a sensitive `CONSENT.*` audit event that flows into the curated audit
trail from Story 37-1 and the hash-chain from Story 37-2.

**Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md):**

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns a **consent record**? | The sole user — `user_id`-keyed, `tenant_id` NULL (same XOR as `prompt_overrides`). | The tenant — `tenant_id`-keyed, `user_id` NULL. `tenant_owner`/`tenant_admin` grant/withdraw on the org's behalf. |
| Who can **grant/withdraw**? | The user (no RBAC). | `tenant_owner` / `tenant_admin` only; `member` gets read-only (403 on write), mirroring prompt-store RBAC. |
| Who can **read** consent state + history? | The user. | Any tenant member (own tenant only); cross-tenant rejected. |
| Where is the **ROPA registry** owned? | System-defined activities (shipped defaults) + the sole user's tenant view. | System-defined activities (platform-owner via `OwnerAccess`) surfaced read-only to every tenant member; tenant-scoped notes optional. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

## Dependency note on the audit substrate

The epic sequences this story **after 37-1 (curated audit trail) and 37-2
(tamper-evident hash-chain)**. Those stories deliver the `audit_records` table
and the `IAuditTrail` write seam (NEW — not yet in the repo as of this draft).
Until 37-1/37-2 land, the only audit substrate present is the DCB `DomainEvent`
store via `IEventRepository.AppendAsync` (verified at
`apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs`). This story
writes `CONSENT.GRANTED` / `CONSENT.WITHDRAWN` through the 37-1 audit seam (and,
because the audit trail itself is built on the DCB stream, the same events also
land as `DomainEvent` rows that the `AlertRuleEvaluator` already polls). **If
37-1/37-2 are not yet merged when this story is implemented, fall back to
appending directly via `IEventRepository.AppendAsync` and tag the events
`sensitive: "true"` so the 37-1 ingest backfills them into `audit_records`.**

## Acceptance Criteria

1. **AC1 — `consent_records` entity (append-only, per-mode owned).** A new
   `consent_records` table stores `consent_type`, `version`, `granted` (bool),
   `actor_user_id`, `occurred_at`, `ip_address`, `user_agent`, and
   `source_event_id` (back-reference to the emitted `CONSENT.*` audit event).
   Exactly one of `user_id` / `tenant_id` is non-null (principal XOR CHECK,
   mirroring `ck_prompt_overrides_principal_xor`). The table is **append-only**:
   no UPDATE/DELETE path exists in the service or repository; a withdrawal is a
   NEW row with `granted = false`, never an in-place edit of the grant row.

2. **AC2 — record grant/withdraw + read effective state & history.** Endpoints:
   `POST /api/v1/orgs/{tenantId}/consent` (SaaS, `tenant_owner`/`tenant_admin`)
   and a self-service single-user variant record a grant or withdrawal;
   `GET /api/v1/orgs/{tenantId}/consent` returns the **current effective
   consent per type** plus `?history=true` for the full append-only timeline.
   Endpoint shape is identical across modes — auth middleware decides which
   principal key (`user_id` vs `tenant_id`) is written, exactly as the
   prompt-store API does.

3. **AC3 — consent changes emit hash-chained sensitive audit events.** Every
   grant emits `CONSENT.GRANTED` and every withdrawal emits `CONSENT.WITHDRAWN`
   (pattern `AGGREGATE.ACTION.STATUS`), flagged sensitive, written through the
   Story 37-1 audit trail and hash-chained via Story 37-2. Tags include
   `consentType`, `version`, `scope` (`user`/`tenant`), `tenantId`, `actorUserId`,
   `mode`. The resulting `audit_records` (or DCB) row id is stored back on the
   `consent_records.source_event_id` column.

4. **AC4 — canonical, versioned consent types.** A `ConsentType` constants set
   ships at minimum: `terms_of_service`, `data_processing_agreement`,
   `byok_data_handling`, `telemetry_opt_in`, `ai_training_data_usage`. Each type
   carries a current **active version** (from `ConsentTypeCatalog` in code, the
   `SystemPrompts` precedent for shipped defaults). A TOS/DPA version bump
   requires re-consent — a grant at v1 does not satisfy a v2 requirement.

5. **AC5 — effective-consent resolution + staleness flag.** Resolution returns
   the latest record per `(scope, consent_type)` ordered by `occurred_at` then
   the `BIGSERIAL` tiebreak (mirroring `DomainEvent.SequenceNumber` ordering),
   and flags `stale = true` when `catalog.activeVersion > consentedVersion`
   (re-consent required) and `granted = false` when the latest record is a
   withdrawal. A type with no record returns `granted = false, stale = false,
   consentedVersion = null`.

6. **AC6 — Records of Processing Activities (ROPA) registry.** A
   `processing_activities` table (system-scoped defaults seeded insert-missing-
   only, the `ConventionSeedSpecs` precedent) records each processing purpose:
   `activity_key`, `name`, `purpose`, `lawful_basis`, `data_categories`,
   `retention`, `recipients`, `is_active`. `GET /api/v1/orgs/{tenantId}/processing-activities`
   returns the registry for tenant members (read-only); platform-owner CRUD lives
   at `GET/POST/PATCH /api/v1/admin/processing-activities` (`OwnerAccess`).

7. **AC7 — RBAC per mode; cross-tenant rejected.** In SaaS, `member` users can
   read their own tenant's consent + ROPA but receive **403** on
   `POST .../consent`; `tenant_owner`/`tenant_admin` may write. Any request whose
   path `{tenantId}` differs from the caller's membership is rejected by the
   existing `RequireTenantMembershipFilter` (cross-tenant 403/404). In
   single-user mode, the sole user has full read+write with no role check.

8. **AC8 — consent gating helper.** An `IConsentGate.RequireAsync(scopeKey,
   consentType)` helper resolves effective consent and throws a
   `TammaError("CONSENT.REQUIRED.MISSING", ..., severity High)` when consent is
   absent, withdrawn, or stale — for callers that must enforce a gate (e.g. an
   AI-training-data-dependent code path). The gate **reads only**; it never
   auto-grants. Gating is wired at one demonstrative call site (telemetry emit
   or AI-training-data usage) and exposed for future call sites; it does NOT
   silently degrade — a missing-consent gate is a hard error, consistent with
   the project's no-empty-fallback rule.

9. **AC9 — append-only enforcement is structural, not advisory.** There is no
   service/repository method that updates or deletes a `consent_records` row;
   a test asserts the repository surface exposes only append + query. (DB-level
   immutability hardening — e.g. a revoke trigger — is out of scope; the
   service surface is the enforcement boundary, matching how the DCB event
   store is append-only by API not by DB trigger today.)

10. **AC10 — tests cover the compliance-critical paths.** Unit + integration
    tests cover: append-only enforcement (AC9); grant→withdraw→re-grant history
    correctness and ordering; version-bump staleness detection (AC5); `CONSENT.*`
    emission with correct tags + `source_event_id` round-trip (AC3); per-mode
    RBAC matrix incl. SaaS `member` 403 and cross-tenant rejection (AC7); ROPA
    read/seed + admin CRUD (AC6); `IConsentGate` throws on missing/withdrawn/
    stale and passes on a fresh grant (AC8).

11. **AC11 — full suite stays green; migration applies + rolls back cleanly.**
    The additive EF migration (new tables, no baseline CHECK edits) reports no
    pending model changes after `dotnet ef migrations add`; entity config lives
    only in `TammaModelConfiguration.cs` (the established single source).

## Technical Design

### Substrate verified in-repo (do NOT invent new substrate)

| Concern | Real anchor (verified) |
|---|---|
| Subject identity | `apps/tamma-elsa/src/Tamma.Data/Entities/User.cs` (`Id`, `TenantId`, `Role`, `PlatformRole`) |
| Audit event row | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` (`Type`, `TenantId`, `Tags`, `Data`, `SequenceNumber`) |
| Audit write seam | `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` (`AppendAsync`) |
| Dual-scoping precedent | `apps/tamma-elsa/src/Tamma.Data/Entities/PromptOverride.cs` + `TammaModelConfiguration.cs` ~714 (`ck_prompt_overrides_principal_xor`, `NULLS NOT DISTINCT` unique index) |
| Mode | `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` (`ITammaModeProvider`) |
| Tenant RBAC | `apps/tamma-elsa/src/Tamma.Api/Authorization/TenantRoleHierarchy.cs` + `RequireTenantMembershipFilter` (path-tenant gate) |
| Org endpoints (target) | `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (`ListTenantAudit` precedent) |
| Platform-owner gate | `OwnerAccess` policy (admin endpoints under `/api/v1/admin/*`) |
| Seed-defaults precedent | `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionSeedSpecs.cs` (insert-missing-only) |
| 37-1/37-2 audit trail | `audit_records` table + `IAuditTrail` / hash-chain — **NEW (delivered by deps)** |

> Note: `packages/api` (TypeScript) is DELETED. All targets are the C#
> `apps/tamma-elsa` solution. Do not reference or create TS-side code.

### `ConsentRecord` entity (NEW)

`apps/tamma-elsa/src/Tamma.Data/Entities/ConsentRecord.cs`:

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Append-only consent / data-processing decision. One row per grant or
/// withdrawal — there is NO in-place edit. Per-mode owned (XOR on
/// user_id / tenant_id), mirroring <see cref="PromptOverride"/>.
/// </summary>
public class ConsentRecord
{
    public Guid Id { get; set; }

    /// <summary>Single-user mode: the sole user. NULL in SaaS.</summary>
    public Guid? UserId { get; set; }

    /// <summary>SaaS mode: the owning tenant. NULL in single-user.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>One of <see cref="Tamma.Api.Services.Compliance.ConsentType"/>.</summary>
    public string ConsentType { get; set; } = null!;

    /// <summary>Version of the TOS/DPA/policy this decision applies to.</summary>
    public string Version { get; set; } = null!;

    /// <summary>true = granted, false = withdrawn.</summary>
    public bool Granted { get; set; }

    /// <summary>The user who performed the action (audit who).</summary>
    public Guid ActorUserId { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>FK to the CONSENT.* audit/DCB row this change emitted.</summary>
    public Guid? SourceEventId { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// Monotonic insertion-order tiebreak (BIGSERIAL), mirroring
    /// <see cref="DomainEvent.SequenceNumber"/> — disambiguates two records
    /// that share an <see cref="OccurredAt"/> millisecond when resolving the
    /// latest decision per (scope, type).
    /// </summary>
    public long SequenceNumber { get; set; }
}
```

Model config (in `TammaModelConfiguration.cs`, mirroring the PromptOverride block):

- `principal_xor` CHECK: exactly one of `UserId` / `TenantId` non-null.
- `granted_type` CHECK on `ConsentType` IN the canonical set (or leave open and
  validate in service — match the project's existing CHECK-vs-service split).
- Index on `(UserId, TenantId, ConsentType, SequenceNumber DESC)` for the
  latest-per-type resolution query. **No unique index** — multiple rows per
  `(scope, type)` are the whole point (history).
- `SequenceNumber` as `BIGSERIAL` identity (same as `DomainEvent`).

### `ProcessingActivity` entity (NEW — ROPA)

`apps/tamma-elsa/src/Tamma.Data/Entities/ProcessingActivity.cs`:

```csharp
public class ProcessingActivity
{
    public Guid Id { get; set; }
    public string ActivityKey { get; set; } = null!;   // e.g. "llm_payload_processing"
    public string Name { get; set; } = null!;
    public string Purpose { get; set; } = null!;
    public string LawfulBasis { get; set; } = null!;   // GDPR Art.6 basis
    public string[] DataCategories { get; set; } = [];
    public string Retention { get; set; } = null!;
    public string[] Recipients { get; set; } = [];     // sub-processors
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

System-scoped (no tenant key) — these are platform-level processing facts,
seeded insert-missing-only via a `ProcessingActivitySeedSpecs` mirroring
`ConventionSeedSpecs`. Unique on `ActivityKey`.

### `ConsentType` catalog (NEW — code-shipped defaults)

`apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ConsentTypeCatalog.cs`:

```csharp
public static class ConsentType
{
    public const string TermsOfService        = "terms_of_service";
    public const string DataProcessingAgreement = "data_processing_agreement";
    public const string ByokDataHandling      = "byok_data_handling";
    public const string TelemetryOptIn        = "telemetry_opt_in";
    public const string AiTrainingDataUsage   = "ai_training_data_usage";
}

public static class ConsentTypeCatalog
{
    // Active version per type — a bump here forces re-consent (AC4/AC5).
    public static readonly IReadOnlyDictionary<string, string> ActiveVersions =
        new Dictionary<string, string>
        {
            [ConsentType.TermsOfService]          = "2026-06-01",
            [ConsentType.DataProcessingAgreement] = "2026-06-01",
            [ConsentType.ByokDataHandling]        = "1.0",
            [ConsentType.TelemetryOptIn]          = "1.0",
            [ConsentType.AiTrainingDataUsage]     = "1.0",
        };
}
```

### `ConsentService` (NEW)

`apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ConsentService.cs`:

- `RecordAsync(scope, consentType, granted, actorUserId, ip, ua)` →
  validates `consentType` against the catalog, resolves `version` from
  `ConsentTypeCatalog.ActiveVersions`, emits `CONSENT.GRANTED`/`CONSENT.WITHDRAWN`
  via the 37-1 audit trail (fallback `IEventRepository.AppendAsync`), inserts a
  new `ConsentRecord` with `SourceEventId` set, returns the new record.
  **Append-only — no update path.**
- `GetEffectiveAsync(scope)` → latest record per `(scope, type)` +
  `stale`/`granted` flags (AC5). Returns one row per known consent type
  (absent → `granted=false`).
- `GetHistoryAsync(scope, consentType?)` → full append-only timeline, newest
  first by `(OccurredAt, SequenceNumber)`.

Scope is resolved from `ITammaModeProvider` + caller identity exactly like the
prompt store: single-user → `user_id`; SaaS → `tenant_id`.

### `IConsentGate` (NEW)

`apps/tamma-elsa/src/Tamma.Api/Services/Compliance/IConsentGate.cs`:

```csharp
public interface IConsentGate
{
    /// <summary>Throws TammaError("CONSENT.REQUIRED.MISSING", High) when the
    /// effective consent for <paramref name="consentType"/> is absent,
    /// withdrawn, or stale. Read-only; never grants.</summary>
    Task RequireAsync(ConsentScope scope, string consentType, CancellationToken ct = default);
}
```

Wired at one demonstrative site (telemetry emit OR AI-training-data path) to
prove the gate; not retro-fitted across the codebase in this story.

### Endpoints

In `OrgEndpoints.cs` (the spec target), mirroring `ListTenantAudit`:

```
POST /api/v1/orgs/{tenantId}/consent      → ConsentService.RecordAsync (admin+ via RoleAtLeast; member → 403)
GET  /api/v1/orgs/{tenantId}/consent      → effective state (any member); ?history=true → timeline
GET  /api/v1/orgs/{tenantId}/processing-activities  → ROPA read (any member)
```

Single-user self-service variant: `POST /api/v1/consent` + `GET /api/v1/consent`
(no path-tenant; principal = the user). Route registration in `Program.cs`
alongside the existing `orgs.MapGet("/{tenantId:guid}/audit", ...)` block (line
~1550), each org route attaching `RequireTenantMembershipFilter`.

Platform-owner ROPA CRUD under `/api/v1/admin/processing-activities`
(`OwnerAccess`), mirroring existing admin endpoints.

### Migration

Additive EF migration under
`apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` via
`dotnet ef migrations add AddConsentAndProcessingActivities`. New tables only —
no baseline CHECK edits — so it is a normal additive migration. Verify
`has-pending-model-changes` reports none afterwards.

## Dependencies

- **Prerequisite — Story 37-1** (curated audit trail): provides the
  `audit_records` table + `IAuditTrail` write seam that `CONSENT.*` events flow
  into. Fallback to `IEventRepository.AppendAsync` if not yet merged.
- **Prerequisite — Story 37-2** (tamper-evident hash-chain): hash-chains the
  `CONSENT.*` audit rows so consent history is tamper-evident.
- **Prerequisite — Epic 18 (auth)**: `User`, `TenantMembership`,
  `TenantRoleHierarchy`, `RequireTenantMembershipFilter`, `OwnerAccess`/
  `PlatformOwnerAccess` policies — the RBAC substrate this story's per-mode
  gating relies on.
- **Related — `ITammaModeProvider`** (`TammaMode.cs`): single source for
  per-mode scope derivation.
- **Related — Epic 37 GDPR DSAR/erasure stories**: consent history is part of
  the DSAR export payload and must be preserved (audit) on erasure.

## Testing Strategy

Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/` (new folder;
xUnit/NUnit per existing convention). Docker-bound suites run via
`sg docker -c "dotnet test ..."`.

1. **`ConsentServiceTests`** — grant inserts one row + emits `CONSENT.GRANTED`
   with correct tags and round-trips `SourceEventId`; withdraw inserts a second
   row (`granted=false`) + `CONSENT.WITHDRAWN`, never edits the grant row;
   grant→withdraw→re-grant produces 3 rows in correct order; effective state
   returns the latest; staleness flips when the catalog version is bumped above
   the consented version.
2. **`ConsentAppendOnlyTests`** — repository/service surface exposes only
   append + query (no update/delete); reflection or interface assertion (AC9).
3. **`ConsentEndpointsTests`** — per-mode RBAC matrix: single-user user
   read+write; SaaS `tenant_owner`/`tenant_admin` write, `member` read-only
   (403 on POST); cross-tenant path rejected by the membership filter; history
   query returns ordered timeline.
4. **`ProcessingActivityTests`** — seed is insert-missing-only (re-run adds
   nothing, never reverts edits); tenant read returns active activities;
   platform-owner CRUD gated by `OwnerAccess` (non-owner 403).
5. **`ConsentGateTests`** — `RequireAsync` throws `CONSENT.REQUIRED.MISSING` on
   absent/withdrawn/stale; passes on a fresh active-version grant; never grants.
6. **Migration test** — apply + rollback clean; no pending model changes.

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/ConsentRecord.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/ProcessingActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `DbSet<ConsentRecord>`, `DbSet<ProcessingActivity>`) |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (ignore/scope the new sets per the established dual-context pattern) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config: XOR CHECK, indexes, BIGSERIAL — single source) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*_AddConsentAndProcessingActivities.cs` | Create (EF migration) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IConsentRepository.cs` | Create (append + query only) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/ConsentRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ConsentType.cs` (+ `ConsentTypeCatalog`) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ConsentService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/IConsentGate.cs` (+ `ConsentGate.cs`) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ConsentEventTypes.cs` (`CONSENT.GRANTED`, `CONSENT.WITHDRAWN`) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ProcessingActivitySeedSpecs.cs` (+ seeder) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | Modify (add consent + ROPA handlers) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminProcessingActivityEndpoints.cs` | Create (platform-owner CRUD) |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs` (or new `ConsentDtos.cs`) | Modify/Create (request/response shapes) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/ComplianceServiceCollectionExtensions.cs` | Create (DI wiring) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (route mapping + DI registration) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ConsentServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ConsentAppendOnlyTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ConsentEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ProcessingActivityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ConsentGateTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions
3. Confirmed the state of Stories 37-1 and 37-2 (audit trail + hash-chain). If
   `audit_records` / `IAuditTrail` are merged, write `CONSENT.*` through them; if
   not, append via `IEventRepository.AppendAsync` and tag `sensitive: "true"`.
4. Planned a TDD approach (Red-Green-Refactor).

### Append-only is the load-bearing invariant

Consent history's evidentiary value depends on immutability. Enforce it at the
**service + repository surface** (no update/delete method exists) — the same way
the DCB event store is append-only by API. A withdrawal is always a new row.
A test (`ConsentAppendOnlyTests`) must pin this so a future "convenience" edit
method can't slip in. DB-level immutability (revoke triggers) is a deliberate
non-goal here; the API boundary is the enforcement line, matching project
precedent.

### Per-mode scope derivation — reuse, don't reinvent

Derive scope (`user_id` vs `tenant_id`) from `ITammaModeProvider` + caller
identity exactly as `PromptStoreService` does. Do NOT add a per-user override
layer in SaaS — consent is owned by the tenant (tenant_owner/tenant_admin),
mirroring the prompt-store "no per-user layer in SaaS" decision in CLAUDE.md.

### Staleness, not deletion, drives re-consent

A TOS/DPA version bump must NOT invalidate or delete the old grant — it stays in
history. Staleness is computed at read time: `activeVersion > consentedVersion`.
This keeps the audit trail intact while still forcing a fresh decision.

### No empty/plain fallback

Consistent with the project's resolution rule
(`feedback_resolution_no_empty_fallback`): `IConsentGate.RequireAsync` is a hard
error on missing/withdrawn/stale consent. It never auto-grants and never
silently passes.

### Migration discipline

`consent_records` and `processing_activities` are additive new tables, so a
normal `dotnet ef migrations add` applies — NOT a baseline CHECK edit. Still run
`has-pending-model-changes` afterwards (must report none) and put all entity
config in `TammaModelConfiguration.cs` only.

## Logging Requirements

- **INFO**: consent recorded (`consentType`, `granted`, `scope`, `version`,
  `actorUserId`, `mode`); ROPA seeded (`added` count); effective-consent endpoint
  queried.
- **DEBUG**: effective-consent resolution result per type (`granted`, `stale`,
  `consentedVersion`, `activeVersion`); consent-gate check outcome.
- **WARN**: consent-gate denial (`consentType`, `scope`, reason: absent/
  withdrawn/stale); attempted write by a `member` role (403).
- **ERROR**: audit-event emission failed (consent NOT recorded — the row insert
  and the event emission must be atomic; a failed emit aborts the record); DB
  write failure.
- **Structured context**: `{ scope, tenantId, userId, consentType, version,
  granted, mode, sourceEventId }` where applicable.
- **Credential / PII safety**: NEVER log full IP/user-agent at INFO+ beyond what
  the consent row already persists for evidence; never log auth tokens.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
