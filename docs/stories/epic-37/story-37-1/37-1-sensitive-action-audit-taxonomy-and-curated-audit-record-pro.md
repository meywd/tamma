# Story 37-1: Sensitive-Action Audit Taxonomy & Curated Audit-Record Projection

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **compliance officer / platform operator** (and, in single-user mode, the sole user auditing their own instance),
I want a **canonical catalog of compliance-relevant sensitive actions** and a **curated, queryable audit-record read-model** materialized from the existing DCB event stream,
So that I can answer "who did what, when, to which target, with what outcome" for every sensitive action — across tenant and platform scope — without scanning the raw event store, and so that the curated trail is the stable substrate the rest of Epic 37 (tamper-evidence, query/export, retention, GDSR) builds on.

## Priority

P0 — Foundation for the entire Epic 37 audit/compliance product layer. Story 37-2 (tamper-evidence / hash-chain) and 37-10 (audit query/search/export API) both consume the `audit_records` read-model this story produces. Without a curated, normalized projection there is no audit product — only the raw, unindexed DCB stream.

## Architectural Context (READ FIRST)

This story builds **ON TOP OF** the Epic 4 DCB event store. It does **NOT** rebuild, duplicate, or replace the event store.

- The **single DCB stream is the source of truth and the audit substrate** (per CLAUDE.md "DCB Pattern" and Epic 4). Raw immutable events stay authoritative. The curated `audit_records` table is a **read-optimized product layer** with a back-reference (`source_event_id` + `source_sequence_number`) to the originating raw event.
- Target codebase is the C# app `apps/tamma-elsa/`. **`packages/api` is DELETED — never target it.**
  - `Tamma.Core` — enums, redaction, pure catalog constants (`src/Tamma.Core/Redaction/CredentialRedactor.cs` exists).
  - `Tamma.Data` — entities, EF `DbContext`s, repositories, migrations.
    - `src/Tamma.Data/Entities/DomainEvent.cs` — the DCB event row (tenant `domain_events`; also a CP DbSet). Has `Id`, `Type`, `TenantId`, `IssueNumber`, `Tags` (JSONB), `Metadata`, `Data`, `CreatedAt`, and `SequenceNumber` (BIGSERIAL total-order cursor).
    - `src/Tamma.Data/Entities/PlatformEvent.cs` — control-plane cross-tenant event store (`Id`, `Type`, `TenantId?`, `UserId?`, `Tags`, `Metadata`, `Data`, `CreatedAt`, `SequenceNumber`).
    - `src/Tamma.Data/TenantDbContext.cs` — per-tenant schema context (`DomainEvents` DbSet lives here → **tenant audit records go in the tenant schema**).
    - `src/Tamma.Data/ControlPlaneDbContext.cs` — control-plane context (`DomainEvents` + `PlatformEvents` DbSets → **platform audit records go in the control plane**).
    - `src/Tamma.Data/Repositories/EventRepository.cs` / `IEventRepository.cs` — append + query DCB events (do NOT modify the write path; only read).
  - `Tamma.Api` — endpoints, services, DI extensions, hosted/background services.
- **Per-mode ownership (mirror Epic 27 / prompt-store, per CLAUDE.md "Operating Modes"):** single-user mode keys curated rows by `user_id` (tenant_id NULL); SaaS mode keys by `tenant_id` (user_id NULL). Exactly-one XOR, mirroring `prompt_overrides`. Mode comes from `ITammaModeProvider` (`src/Tamma.Api/Services/PromptStore/TammaMode.cs`).

### Real cursor-projection precedent to mirror

`AlertRuleEvaluator` (`src/Tamma.Api/Services/Alerts/Rules/AlertRuleEvaluator.cs`) is a background poller that already does exactly the cursor-tracked scan this story needs: it reads new `DomainEvent` + `PlatformEvent` rows by `SequenceNumber`, persists progress in the `AlertEvaluatorCursor` entity (`LastDomainSequenceNumber` / `LastPlatformSequenceNumber`), and resumes on restart. The `AuditProjector` MUST follow this established pattern (its own cursor entity, its own `EvaluatorId`-style id) rather than inventing a new mechanism.

## Acceptance Criteria

1. **Catalog exists and is centralized.** A `SensitiveActionCatalog` static class is added under `src/Tamma.Core/Audit/SensitiveActionCatalog.cs`, mirroring the shape of the existing `SecretAuditEventTypes` (`src/Tamma.Api/Services/Secrets/ISecretAccessAuditor.cs`). It enumerates **≥30 action codes** as named constants, each mapped to (a) a `category`, (b) a `severity`, and (c) a SOC2 control id, via a single immutable lookup (e.g. `IReadOnlyDictionary<string, SensitiveActionDescriptor>`).

2. **Catalog categories are complete.** Action codes are grouped under the categories: `CONFIG` (prompt/persona/convention/agent-config/sanitization-rule edits), `RBAC` (role/membership/invite changes), `SECRET` (secret read/write/reveal/rotate/revoke), `BYOK` (provider-key / provider-chain changes), `BILLING` (plan/subscription/budget changes), `EXPORT` (data export / DSAR), `AUTH` (login success/failure, logout, password reset, token refresh), `IMPERSONATION` (start/end), `TENANT` (provision/deprovision/move/lifecycle), `AGENT` (agent dispatch / autonomous code action), and `PERSONA` (persona/system-prompt changes). Each category has ≥1 code; the catalog defines the `AuditCategory` and `AuditSeverity` enums (severity ∈ `info | notice | warning | critical`) in `Tamma.Core`.

3. **Catalog reuses existing emitters without re-emitting.** The catalog **maps existing event types** already emitted elsewhere — `SECRET.*` (Epic 29, `SecretAuditEventTypes`), `IMPERSONATION.STARTED` / `IMPERSONATION.ENDED` (28-R2, `AdminImpersonationsEndpoints.cs`), `USER.ROLE_CHANGED.SUCCESS` (`AdminEndpoints.cs`), `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` (`OrgEndpoints.cs`) — **without adding new emit call-sites for them**. The projection observes the already-flowing events; it does not change who emits or when.

4. **`AuditRecord` entity + tables (tenant AND platform).** An `AuditRecord` entity is added (`src/Tamma.Data/Entities/AuditRecord.cs`) and registered as a DbSet in **both** `TenantDbContext` (per-tenant schema, tenant-scope audit) and `ControlPlaneDbContext` (platform-scope audit). Columns: `id`, `action_code`, `category`, `severity`, `actor_user_id`, `actor_email_snapshot`, `target_type`, `target_id`, `outcome` (`success | failure | denied`), `ip_address`, `user_agent`, `occurred_at`, `source_event_id`, `source_sequence_number`, `payload_json` (redacted), plus the per-mode ownership column(s): `tenant_id` (SaaS) / `user_id` (single-user). Entity config goes in `TammaModelConfiguration.cs` (the single configuration source).

5. **Per-mode ownership XOR + uniqueness.** A CHECK constraint enforces exactly-one of (`user_id`, `tenant_id`) non-null (mirroring `prompt_overrides` `principal_xor`). A UNIQUE constraint on `source_event_id` guarantees idempotency (one curated row per raw event). The tenant-context global query filter (same defence-in-depth used by `EventRepository.ListByTenantAsync`) applies to the tenant `AuditRecord` set.

6. **EF migration.** An additive EF migration is added under `src/Tamma.Data/Migrations/Tenant/` (tenant `audit_records`) and `src/Tamma.Data/Migrations/ControlPlane/` (platform `audit_records`). `dotnet ef migrations has-pending-model-changes` reports **none** after; migrations apply and roll back cleanly.

7. **`IAuditProjector` reads by cursor and inserts exactly one row per catalog match.** An `IAuditProjector` / `AuditProjector` (`src/Tamma.Data/Audit/AuditProjector.cs`) reads new `DomainEvent` (tenant scope) / `PlatformEvent` (platform scope) rows by `SequenceNumber` cursor and inserts **exactly one** `audit_record` per **catalog-matched** event. Non-catalog event types are **skipped** (no row). The projector resolves `category`/`severity`/`outcome`/`actor`/`target` by joining the raw event's `Type` + `Tags` + `Data` against `SensitiveActionCatalog`.

8. **Idempotent / replayable from cursor.** Re-running the projector from any cursor position **never double-inserts** (enforced by the `source_event_id` unique index + insert-if-absent). The projection is fully rebuildable: deleting `audit_records` and resetting the cursor to 0 reconstructs an identical curated trail from the raw DCB stream. `source_sequence_number` preserves the DCB total-order cursor so replay order is deterministic.

9. **Eventual, non-blocking projection.** The projector runs as a **cursor-tracked background pass** (a `BackgroundService` mirroring `AlertRuleEvaluator` + its cursor entity, or hung off the existing `TaskQueueProcessor` / Elsa schedule). The per-request hot path is **never blocked** by projection — audit-record materialization is eventual. A **lag metric** (`tamma.audit.projection_lag` = max raw `SequenceNumber` − last projected `SequenceNumber`) is exposed via the existing OTel meter pattern.

10. **Redaction before persistence.** `payload_json` is passed through `Tamma.Core` redaction (`CredentialRedactor.Clean` / equivalent) before the row is persisted, so no secret plaintext, API key, token, or password ever lands in `audit_records`. A dedicated test feeds a `SECRET.WRITE`-shaped event with a fake secret in `Data` and asserts the persisted `payload_json` contains the `[REDACTED]` placeholder and never the plaintext.

11. **Tenant vs platform scope routing.** Catalog-matched **tenant-scoped** events (those with a `TenantId`, in SaaS mode) materialize into the **tenant schema** `audit_records` keyed by `tenant_id`. **Platform-scoped** events (`TenantId` null — orchestrator/platform/lifecycle, e.g. `IMPERSONATION.*` against the platform) materialize into the **control-plane** `audit_records`. In **single-user mode**, all curated rows are keyed by `user_id` (tenant_id NULL) and the routing collapses to the single-user store.

12. **Tamper-evidence hook for 37-2.** `AuditRecord` carries the fields 37-2 needs to build its hash chain without a schema change: `source_sequence_number` (deterministic order), plus a nullable `record_hash` and `prev_record_hash` column reserved for 37-2 (populated by 37-2, NOT this story — this story only adds the columns and leaves them null). The projector inserts rows in strict `source_sequence_number` order so 37-2 can chain them deterministically.

13. **Unit tests (xUnit, test-first).** Tests cover: (a) **catalog completeness** — every existing emitter event type asserted (`SECRET.*`, `IMPERSONATION.*`, `USER.ROLE_CHANGED.SUCCESS`, `TENANT.MEMBER_ROLE_CHANGED.SUCCESS`) is present in the catalog and ≥30 codes / all 11 categories populated; (b) **projector idempotency** — running twice over the same range yields exactly one row per event; (c) **per-mode key selection** — SaaS event → `tenant_id` set / `user_id` null; single-user → `user_id` set / `tenant_id` null; (d) **redaction enforcement** (AC10); (e) **non-catalog skip** — a `WORKFLOW.STEP_COMPLETED` event produces zero rows.

14. **Cross-scope isolation test.** A tenant-A event never materializes into tenant-B's schema, and a tenant-scoped event never lands in the control-plane `audit_records` (and vice-versa). The tenant global query filter rejects a cross-tenant read of `audit_records`.

15. **No write-path change to the DCB store.** A test asserts the projector only reads `DomainEvent` / `PlatformEvent` (via `IEventRepository` read methods or a read-only query) and never appends, mutates, or deletes raw events — the event store remains the immutable source of truth.

## Technical Design

### Component layout

```
apps/tamma-elsa/src/
  Tamma.Core/
    Audit/
      SensitiveActionCatalog.cs        # NEW — ≥30 codes, category+severity+SOC2 control, immutable lookup
      AuditCategory.cs                 # NEW — enum: CONFIG|RBAC|SECRET|BYOK|BILLING|EXPORT|AUTH|IMPERSONATION|TENANT|AGENT|PERSONA
      AuditSeverity.cs                 # NEW — enum: info|notice|warning|critical
      SensitiveActionDescriptor.cs     # NEW — record: ActionCode, Category, Severity, Soc2ControlId, TargetTypeHint
    Redaction/
      CredentialRedactor.cs            # EXISTING — reused for payload_json redaction
  Tamma.Data/
    Entities/
      AuditRecord.cs                   # NEW — curated row (see schema below)
      AuditProjectorCursor.cs          # NEW — cursor entity (mirror AlertEvaluatorCursor)
      DomainEvent.cs                   # EXISTING — read source (tenant)
      PlatformEvent.cs                 # EXISTING — read source (platform)
    Audit/
      IAuditProjector.cs               # NEW
      AuditProjector.cs                # NEW — cursor-tracked, idempotent, redacting projection
    Repositories/
      IAuditRecordRepository.cs        # NEW
      AuditRecordRepository.cs         # NEW — insert-if-absent + cursor read; reuses tenant/CP context
      IEventRepository.cs              # EXISTING — read only
    TenantDbContext.cs                 # MODIFY — add AuditRecords + AuditProjectorCursor DbSets
    ControlPlaneDbContext.cs           # MODIFY — add AuditRecords + AuditProjectorCursor DbSets
    TammaModelConfiguration.cs         # MODIFY — entity config (CHECK constraints, unique index, filter)
    Migrations/Tenant/                 # NEW migration — tenant audit_records
    Migrations/ControlPlane/           # NEW migration — platform audit_records
  Tamma.Api/
    Services/Audit/
      AuditProjectorBackgroundService.cs   # NEW — BackgroundService host (mirror AlertRuleEvaluator)
      AuditProjectorOptions.cs             # NEW — RunOnStartup gate, poll interval
      AuditProjectionMetrics.cs            # NEW — projection_lag OTel gauge
    Extensions/
      AuditServiceCollectionExtensions.cs  # NEW — DI wiring; called from Program.cs
    Program.cs                              # MODIFY — register projector + background service
```

> Note: this story produces the **catalog + projection + tables** only. It deliberately ships **no read/query/export endpoint** (that is Story 37-10) and **no hash chaining** (that is Story 37-2). It ships the `record_hash` / `prev_record_hash` columns and the deterministic-order guarantee that 37-2 needs.

### Taxonomy enum + descriptor

```csharp
// src/Tamma.Core/Audit/AuditCategory.cs
namespace Tamma.Core.Audit;

public enum AuditCategory
{
    Config, Rbac, Secret, Byok, Billing, Export, Auth, Impersonation, Tenant, Agent, Persona
}

// src/Tamma.Core/Audit/AuditSeverity.cs
public enum AuditSeverity { Info, Notice, Warning, Critical }

// src/Tamma.Core/Audit/SensitiveActionDescriptor.cs
public sealed record SensitiveActionDescriptor(
    string ActionCode,        // canonical DCB event type, e.g. "SECRET.REVEAL"
    AuditCategory Category,
    AuditSeverity Severity,
    string Soc2ControlId,     // e.g. "CC6.1"
    string TargetTypeHint);   // e.g. "secret", "user", "tenant"
```

```csharp
// src/Tamma.Core/Audit/SensitiveActionCatalog.cs  (shape mirrors SecretAuditEventTypes)
namespace Tamma.Core.Audit;

public static class SensitiveActionCatalog
{
    // ── SECRET (maps existing Epic 29 SecretAuditEventTypes — NOT re-emitted here) ──
    public const string SecretRead        = "SECRET.READ";
    public const string SecretWrite       = "SECRET.WRITE";
    public const string SecretReveal      = "SECRET.REVEAL";
    public const string SecretRotateStarted = "SECRET.ROTATE.STARTED";
    public const string SecretRotateSuccess = "SECRET.ROTATE.SUCCESS";
    public const string SecretVersionRevoked = "SECRET.VERSION.REVOKED";

    // ── RBAC (maps existing AdminEndpoints / OrgEndpoints emitters) ──
    public const string UserRoleChanged   = "USER.ROLE_CHANGED.SUCCESS";
    public const string TenantMemberRoleChanged = "TENANT.MEMBER_ROLE_CHANGED.SUCCESS";
    public const string TenantMemberAdded = "TENANT.MEMBER_ADDED.SUCCESS";
    public const string TenantMemberRemoved = "TENANT.MEMBER_REMOVED.SUCCESS";
    public const string UserInvited       = "USER.INVITED.SUCCESS";

    // ── IMPERSONATION (maps existing 28-R2 emitters) ──
    public const string ImpersonationStarted = "IMPERSONATION.STARTED";
    public const string ImpersonationEnded   = "IMPERSONATION.ENDED";

    // ── CONFIG / PERSONA ──
    public const string PromptOverrideChanged   = "PROMPT.OVERRIDE.CHANGED";
    public const string ConventionChanged       = "CONVENTION.CHANGED";
    public const string AgentConfigChanged      = "AGENT_CONFIG.CHANGED";
    public const string SanitizationRuleChanged = "SANITIZATION_RULE.CHANGED";
    public const string PersonaChanged          = "PERSONA.CHANGED";
    public const string SystemPromptChanged     = "SYSTEM_PROMPT.CHANGED";

    // ── BYOK ──
    public const string ProviderKeyChanged   = "PROVIDER_KEY.CHANGED";
    public const string ProviderChainChanged = "PROVIDER_CHAIN.CHANGED";

    // ── BILLING ──
    public const string PlanChanged         = "BILLING.PLAN.CHANGED";
    public const string SubscriptionChanged = "BILLING.SUBSCRIPTION.CHANGED";
    public const string BudgetChanged       = "BUDGET.CONFIG.CHANGED";

    // ── EXPORT ──
    public const string DataExported = "DATA.EXPORTED.SUCCESS";
    public const string DsarRequested = "GDPR.DSAR.REQUESTED";

    // ── AUTH ──
    public const string LoginSuccess  = "AUTH.LOGIN.SUCCESS";
    public const string LoginFailure  = "AUTH.LOGIN.FAILURE";
    public const string Logout        = "AUTH.LOGOUT.SUCCESS";
    public const string PasswordReset = "AUTH.PASSWORD_RESET.SUCCESS";
    public const string TokenRefreshed = "AUTH.TOKEN.REFRESHED";

    // ── TENANT lifecycle ──
    public const string TenantProvisioned   = "TENANT.PROVISIONED.SUCCESS";
    public const string TenantDeprovisioned = "TENANT.DEPROVISIONED.SUCCESS";
    public const string TenantMoved         = "TENANT.MOVED.SUCCESS";

    // ── AGENT ──
    public const string AgentDispatched = "AGENT.DISPATCH.SUCCESS";
    public const string AgentCodeApplied = "CODE.GENERATED.SUCCESS";

    /// <summary>Immutable code → descriptor lookup. The single source of truth
    /// for "is this event a sensitive action, and how is it classified".</summary>
    public static readonly IReadOnlyDictionary<string, SensitiveActionDescriptor> ByCode;

    /// <summary>True if the projector should materialize an audit_record for this raw event type.</summary>
    public static bool IsSensitive(string eventType) => ByCode.ContainsKey(eventType);

    static SensitiveActionCatalog()
    {
        ByCode = new Dictionary<string, SensitiveActionDescriptor>
        {
            [SecretReveal] = new(SecretReveal, AuditCategory.Secret, AuditSeverity.Critical, "CC6.1", "secret"),
            [UserRoleChanged] = new(UserRoleChanged, AuditCategory.Rbac, AuditSeverity.Warning, "CC6.3", "user"),
            // … one entry per const above …
        };
    }
}
```

> **Catalog-completeness test** (AC13a) reflects over the existing emitter constants (e.g. `SecretAuditEventTypes`) and asserts every one is a key in `SensitiveActionCatalog.ByCode`, so a future code that drops/renames an emitted type without updating the catalog fails CI.

### AuditRecord entity

```csharp
// src/Tamma.Data/Entities/AuditRecord.cs
namespace Tamma.Data.Entities;

public class AuditRecord
{
    public Guid Id { get; set; }                       // UUID v7
    public string ActionCode { get; set; } = null!;    // canonical event type
    public string Category { get; set; } = null!;      // AuditCategory (string)
    public string Severity { get; set; } = null!;      // AuditSeverity (string)

    // Who
    public Guid? ActorUserId { get; set; }
    public string? ActorEmailSnapshot { get; set; }    // point-in-time email (actor row may later change)

    // What / target
    public string? TargetType { get; set; }            // "secret" | "user" | "tenant" | ...
    public string? TargetId { get; set; }
    public string Outcome { get; set; } = "success";   // success | failure | denied

    // Context
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime OccurredAt { get; set; }

    // Back-reference to the raw DCB event (source of truth)
    public Guid SourceEventId { get; set; }            // UNIQUE — idempotency key
    public long SourceSequenceNumber { get; set; }     // DCB total-order cursor (deterministic replay)

    public string PayloadJson { get; set; } = "{}";    // redacted projection of raw Data/Tags

    // Per-mode ownership — exactly one is non-null (XOR CHECK)
    public Guid? TenantId { get; set; }                // SaaS
    public Guid? UserId { get; set; }                  // single-user

    // Reserved for Story 37-2 tamper-evidence (this story leaves null)
    public string? RecordHash { get; set; }
    public string? PrevRecordHash { get; set; }
}
```

### Projection cursor (mirror `AlertEvaluatorCursor`)

```csharp
// src/Tamma.Data/Entities/AuditProjectorCursor.cs
public class AuditProjectorCursor
{
    public string ProjectorId { get; set; } = "default";
    public long LastDomainSequenceNumber { get; set; }     // tenant DomainEvents cursor
    public long LastPlatformSequenceNumber { get; set; }   // CP PlatformEvents cursor
    public DateTime UpdatedAt { get; set; }
}
```

### Projector loop (sketch)

```csharp
// src/Tamma.Data/Audit/AuditProjector.cs
public async Task<int> ProjectBatchAsync(CancellationToken ct)
{
    var cursor = await LoadCursorAsync(ct);                       // resume — mirror AlertRuleEvaluator
    var rawBatch = await ReadNewEventsAsync(cursor, batchSize: 500, ct);  // ORDER BY SequenceNumber
    var inserted = 0;

    foreach (var raw in rawBatch)                                // strict SequenceNumber order (37-2 needs this)
    {
        if (!SensitiveActionCatalog.ByCode.TryGetValue(raw.Type, out var desc))
            continue;                                            // AC7 non-catalog skip

        var record = BuildAuditRecord(raw, desc);                // map actor/target/outcome from Tags+Data
        record.PayloadJson = CredentialRedactor.Clean(           // AC10 redact BEFORE persist
            ProjectPayload(raw));
        AssignOwnership(record, raw, _mode.Current);             // AC11 tenant_id vs user_id

        // insert-if-absent on UNIQUE(source_event_id) — AC8 idempotent
        if (await _repo.InsertIfAbsentAsync(record, ct)) inserted++;
    }

    await SaveCursorAsync(MaxSeq(rawBatch), ct);
    _metrics.RecordLag(MaxRawSeq - cursor.LastDomainSequenceNumber);   // AC9 lag gauge
    return inserted;
}
```

### Background host

`AuditProjectorBackgroundService : BackgroundService` mirrors `AlertRuleEvaluator`: a poll loop (default interval configurable via `AuditProjectorOptions`), a `RunOnStartup` gate (so unrelated tests can disable it, mirroring `AlertRuleEvaluatorOptions` / `NotificationDispatcherOptions`), and crash-isolation per tick (one bad batch logs + continues; it does not kill the host). Tenant-scoped projection fans out per active tenant context (consistent with the Story 28-1 per-tenant evaluator direction); platform projection reads the CP `PlatformEvents`.

### EF model configuration (in `TammaModelConfiguration.cs`)

```csharp
// audit_records — applied to BOTH tenant + CP model builds
b.Entity<AuditRecord>(e =>
{
    e.ToTable("audit_records", t =>
    {
        t.HasCheckConstraint("ck_audit_records_principal_xor",
            "(user_id IS NOT NULL AND tenant_id IS NULL) OR (user_id IS NULL AND tenant_id IS NOT NULL)");
    });
    e.HasIndex(x => x.SourceEventId).IsUnique();                 // idempotency
    e.HasIndex(x => new { x.TenantId, x.OccurredAt });           // tenant query path (37-10)
    e.HasIndex(x => x.SourceSequenceNumber);                     // replay order / 37-2 chain
    // tenant context global query filter (same defence-in-depth as DomainEvent)
});
```

## Dependencies

- **Prerequisite — Epic 4 (DCB event store):** Story 4-1 (event schema, `DomainEvent` shape + `SequenceNumber`), Story 4-7 (event query API / read patterns reused for the projector's cursor read). The projector READS this store; it never writes to it.
- **Prerequisite — Epic 28 (schema-per-tenant):** `ControlPlaneDbContext`, `TenantDbContext`, the tenant-context global query filter, and the cursor/background-pass infrastructure (`AlertRuleEvaluator`, `AlertEvaluatorCursor`, `TaskQueueProcessor`).
- **Prerequisite — Epic 27 (per-mode ownership):** the single-user `user_id` vs SaaS `tenant_id` XOR pattern (`prompt_overrides`), and `ITammaModeProvider` (`TammaMode.cs`).
- **Consumed by — Story 37-2 (tamper-evidence / hash chain):** consumes the `audit_records` deterministic-order rows + the reserved `record_hash` / `prev_record_hash` columns. This story is the **tamper-evidence hook**.
- **Consumed by — Story 37-10 (audit query/search/export API):** queries the curated `audit_records` read-model instead of scanning the raw stream.

## Testing Strategy

Tests are **xUnit**, live under `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/` and `tests/Tamma.Data.Tests/Audit/`, and are written **test-first (TDD, Red→Green→Refactor)**. Docker-bound suites run via `sg docker -c "dotnet test ..."` (see `.dev` / project memory on the stale session docker group).

1. **Catalog completeness (pure, no DB):** reflect over `SecretAuditEventTypes` + the impersonation/role-change constants; assert each is in `SensitiveActionCatalog.ByCode`; assert ≥30 codes and all 11 `AuditCategory` values populated; assert every descriptor has a non-empty SOC2 control id.
2. **Projector idempotency:** seed N raw events (mix of catalog + non-catalog); run projector twice over the full range; assert exactly one `audit_record` per catalog-matched event and zero for non-catalog; assert a from-scratch replay (truncate + cursor 0) reproduces an identical set.
3. **Per-mode key selection:** with `ITammaModeProvider` = SaaS, a tenant event → `tenant_id` set / `user_id` null; with single-user, → `user_id` set / `tenant_id` null; XOR CHECK rejects a hand-crafted both-set row.
4. **Redaction enforcement:** feed a `SECRET.WRITE` event whose `Data` carries a fake `tamma_sk_…` / `Bearer …` / `password=…`; assert persisted `payload_json` contains `[REDACTED]` and never the plaintext.
5. **Non-catalog skip:** a `WORKFLOW.STEP_COMPLETED` raw event produces zero `audit_records`.
6. **Cross-scope isolation (DB):** tenant-A event never lands in tenant-B schema; tenant-scoped event never lands in CP `audit_records`; the tenant global query filter rejects a cross-tenant read.
7. **No-write-path assertion:** a spy/fake `IEventRepository` asserts the projector calls only read methods (no `AppendAsync` / mutate / delete) against `DomainEvent` / `PlatformEvent`.
8. **Lag metric:** with M un-projected raw events, `tamma.audit.projection_lag` reports M; after a full pass it reports 0.
9. **Background-service gating:** `RunOnStartup=false` keeps the loop idle for unrelated tests; crash-isolation — one throwing batch logs and the loop survives.
10. **Migration discipline:** `dotnet ef migrations has-pending-model-changes` → none; migration applies + rolls back on both tenant and CP.

## Estimated Effort

5–6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Audit/SensitiveActionCatalog.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Audit/AuditCategory.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Audit/AuditSeverity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Audit/SensitiveActionDescriptor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AuditRecord.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AuditProjectorCursor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Audit/IAuditProjector.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Audit/AuditProjector.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IAuditRecordRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/AuditRecordRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (add `AuditRecords` + `AuditProjectorCursors` DbSets) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `AuditRecords` + `AuditProjectorCursors` DbSets) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config, CHECK, unique index, query filter) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/*` | Create (tenant `audit_records` migration) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*` | Create (platform `audit_records` migration) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditProjectorBackgroundService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditProjectorOptions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditProjectionMetrics.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/AuditServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (wire projector + background service) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/SensitiveActionCatalogTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditProjectorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditRecordScopeIsolationTests.cs` | Create |

> Paths under `apps/tamma-elsa/src/Tamma.Core/Audit/`, `src/Tamma.Data/Audit/`, `src/Tamma.Api/Services/Audit/`, and the `tests/.../Audit/` files are **NEW** (no existing audit package today). `DomainEvent.cs`, `PlatformEvent.cs`, `EventRepository.cs`, `TenantDbContext.cs`, `ControlPlaneDbContext.cs`, `TammaModelConfiguration.cs`, `CredentialRedactor.cs`, `TammaMode.cs`, `AlertRuleEvaluator.cs`, `AlertEvaluatorCursor.cs` are **EXISTING** (verified).

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions
3. Read `AlertRuleEvaluator.cs` + `AlertEvaluatorCursor` end-to-end — the projector is a near-clone of its cursor-tracked scan; do not invent a new cursor mechanism
4. Read `SecretAuditEventTypes` (`ISecretAccessAuditor.cs`) — the catalog mirrors its const-class shape and must remap (not re-emit) its codes
5. Planned the TDD approach (Red→Green→Refactor)

### Build ON the DCB stream — do not rebuild it

The single biggest failure mode is treating this as "a new event store." It is not. Raw `DomainEvent` / `PlatformEvent` rows stay the immutable source of truth. `audit_records` is a derived, rebuildable read-model with a `source_event_id` back-reference. If `audit_records` is ever wrong or corrupted, the fix is "truncate + reset cursor + re-project," never "patch the row." AC8 + AC15 pin this.

### Why a curated projection at all (vs. querying raw events)

The raw stream is generic (`Type` + opaque JSONB `Tags`/`Data`). Compliance queries ("every secret reveal by user X last quarter, with outcome") need normalized, indexed columns (actor, target, outcome, occurred_at, category, severity) and a stable per-mode ownership key. The projection is the product layer that turns "we have all the events" into "we can answer auditor questions in one indexed query" — which 37-10 then exposes and 37-2 then makes tamper-evident.

### Scope-derivation subtlety

Tenant-scoped vs platform-scoped routing (AC11) is driven by the raw event's `TenantId` AND the process mode. In SaaS: `TenantId` non-null → tenant schema keyed by `tenant_id`; `TenantId` null → CP keyed by `tenant_id` null (platform row). In single-user: every curated row is keyed by `user_id`, `tenant_id` null — there is no tenant dimension. Get this wrong and a platform-internal action (e.g. impersonation against the platform) could surface in a tenant's audit view. The isolation test (AC14) and per-mode test (AC13c) must pin the matrix.

### Redaction is non-negotiable and happens before persistence

`payload_json` is the one field that can leak. Redact with `CredentialRedactor.Clean` (handles bearer tokens, `tamma_sk_` prefixes, key=value assignments, URL basic-auth) BEFORE the row is inserted — never "redact on read." AC10's test is the gate.

### Reserved 37-2 columns

`record_hash` / `prev_record_hash` are added now (nullable, left null) so 37-2 lands without a schema migration on the hot table, and so the projector's strict `source_sequence_number` insertion order gives 37-2 a deterministic chain to hash. Do not populate them in this story.

### No migration anxiety

`audit_records` is additive on both tenant and CP. Run `dotnet ef migrations has-pending-model-changes` after authoring entity config; it must report none. Mirror all entity config in `TammaModelConfiguration.cs` (the single source), not in `OnModelCreating` overrides scattered across contexts.

## Logging Requirements

- **INFO**: projection batch completed (`projectorId`, `domainCursor`, `platformCursor`, `eventsScanned`, `recordsInserted`, `recordsSkipped`, `batchDurationMs`); background service started/stopped.
- **DEBUG**: per-event classification decision (`eventType`, `matched`, `category`, `severity`); cursor loaded/saved; lag gauge recorded.
- **WARN**: projection batch tick threw and was crash-isolated (`error`, `lastCursor`); projection lag exceeds threshold (`lag`, `thresholdSeconds`); a catalog-matched event had no resolvable actor/target (`eventType`, `sourceEventId`) — projected with nulls, not dropped.
- **ERROR**: cursor persistence failed; audit-record insert failed for a non-uniqueness reason; redactor threw (must never silently persist un-redacted payload — fail the row, keep the cursor un-advanced for that event).
- **Structured context**: include `{ projectorId, sourceEventId, sourceSequenceNumber, actionCode, category, tenantId|userId }` where applicable.
- **Credential safety**: NEVER log raw `Data` / `Tags` / `payload_json` before redaction; NEVER log secret plaintext, tokens, or passwords. Redact via `CredentialRedactor.Clean` before any payload appears in a log line.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
