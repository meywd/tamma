# Story 37-8: GDPR Right-to-Erasure with Crypto-Shredding & Audit Preservation

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **data-protection officer (platform owner) or a tenant owner acting for a data subject**,
I want a verified right-to-erasure request to irreversibly destroy or anonymize a subject's
personal data across the control-plane and that subject's tenant schema — using crypto-shredding
(destroying the subject's envelope-encryption key version) for any encrypted PII — while the
tamper-evident audit chain stays intact and active legal holds block the deletion,
So that Tamma satisfies GDPR Art. 17 (right to erasure) without breaking the SOC2 / Art. 30
audit evidence the platform is required to retain.

## Priority

P1 — Required GDPR control; gates SOC2 Type II evidence for the audit/compliance product layer.

## Context & Boundary

This story builds on the Epic 37 audit substrate and the Epic 29 secret cabinet. Three Epic 37
dependencies are **not yet authored** at the time of drafting and are referenced as NEW seams to
be produced by their own stories; this story consumes their contracts:

- **37-2 (audit hash chain)** — NEW. Provides the tamper-evident per-row hash chain over curated
  audit records and a `VerifyChainAsync` check. Erasure must NOT break it: audit rows are
  anonymized/tombstoned and re-anchored, never deleted.
- **37-6 (legal hold)** — NEW. Provides an `ILegalHoldService` that answers "is subject X (or any
  of records R) under an active hold?". Erasure is blocked for held records.
- **37-7 (DSAR export)** — NEW. Provides the `SubjectDataMap` — the authoritative catalogue of
  every place a subject's PII lives (control-plane columns, tenant-schema columns, encrypted
  secret refs) plus a per-field **erasure policy** (`deletable` vs `must-retain-anonymized`).
  Erasure walks this same map; DSAR (read) and erasure (destroy) share one inventory.

What already exists and is reused unchanged (verified against `apps/tamma-elsa` @ main):

- **Crypto-shred substrate**: `SecretVersionRow` (`apps/tamma-elsa/src/Tamma.Data/Entities/SecretVersionRow.cs`)
  already carries a per-version AES-256-GCM envelope, a per-version `KekId`, and a `revoked`
  status. `ISecretStoreBackend.DeleteVersionAsync` (`.../Services/Secrets/ISecretStoreBackend.cs`)
  already **scrubs the ciphertext (zeroes the bytes) while keeping the row for audit history** —
  this is exactly crypto-shred: destroy the key/ciphertext, keep the tombstone.
- **Tenant-purge machinery**: `TenantLifecycleEvents`
  (`.../Tamma.Activities/TenantLifecycle/TenantLifecycleEvents.cs`) already models the
  `TENANT.DELETE.*` / `TENANT.DELETED.SUCCESS` lifecycle. Subject erasure shares its event-builder
  shape and reuses the secret-scrub primitive that tenant purge uses.
- **Append-only event store**: `DomainEvent` (`.../Tamma.Data/Entities/DomainEvent.cs`) is
  immutable with a `BIGSERIAL SequenceNumber`. Erasure NEVER mutates a `DomainEvent`; PII inside
  events is handled by crypto-shred + the anonymized re-anchor on the curated audit projection.

**Out of scope (YAGNI):** no UI in this story (admin/tenant dashboard surfaces are 37-12);
no automated subject-discovery crawler beyond the 37-7 `SubjectDataMap`; no third-party
sub-processor erasure fan-out (documented as a manual runbook step); no `packages/api` work —
that package is deleted, all work targets the C# `apps/tamma-elsa`.

## Acceptance Criteria

1. **Erasure request endpoints exist, per-mode.**
   `POST /api/v1/orgs/{tenantId}/erasure` (SaaS/single-user tenant scope) and
   `POST /api/v1/admin/tenants/{tenantId}/erasure` (platform scope) accept a body
   `{ subjectRef, reason }` where `subjectRef` identifies the data subject (user id and/or email)
   and `reason` is a free-text justification. Both return `202 Accepted` with an
   `erasureRequestId`; the long-running execution runs off the request thread (see AC11).

2. **`ErasureExecutor` walks the `SubjectDataMap` and applies each field's policy.**
   For every entry in the 37-7 `SubjectDataMap` for the subject, the executor either
   **hard-deletes** the field/row (policy `deletable`) or **anonymizes** it in place
   (policy `must-retain-anonymized`, e.g. replacing `Email`/`DisplayName`/`AvatarUrl`/`GitHubLogin`
   on `User` with a stable pseudonymous tombstone). The applied action per field is recorded.

3. **Envelope-encrypted PII is crypto-shredded, not merely deleted.**
   For any `SubjectDataMap` entry of kind `encrypted-secret`, the executor revokes/destroys the
   subject-scoped key version through the Epic 29 cabinet
   (`ISecretStore.RetireVersionAsync` / `ISecretStoreBackend.DeleteVersionAsync`), which scrubs the
   ciphertext and flips `SecretVersionRow.Status` to `revoked` while keeping the row. The result
   records **which `(SecretId, VersionNumber, KekId)` tuples were destroyed**.

4. **The append-only event store is never mutated.**
   No `DomainEvent` row is updated or deleted. PII embedded in events is rendered unrecoverable by
   the crypto-shred of AC3 (events store encrypted refs / ciphertext, not plaintext) and by the
   curated-audit anonymization of AC5. A test asserts zero `UPDATE`/`DELETE` against `domain_events`.

5. **Audit records are anonymized + re-anchored, not deleted; the 37-2 chain still verifies.**
   The subject's actor/target identity fields on curated audit records are replaced with a stable
   pseudonymous tombstone (e.g. `erased-subject:{hashedSubjectId}`); the affected rows are re-hashed
   and the 37-2 chain is **re-anchored** (an explicit, audited re-anchor — not a silent in-place
   edit). `ILegalHoldService`-independent `VerifyChainAsync` passes after erasure.

6. **Legal hold (37-6) blocks erasure for held records and returns a partial result.**
   Before touching any record the executor consults `ILegalHoldService`; records under an active
   hold are skipped, the response/result lists each held-back item with the hold id + reason, and a
   `GDPR.ERASURE.BLOCKED_BY_HOLD` event is emitted. The non-held remainder still erases.

7. **Erasure itself is audited with per-category counts; the request is permanently retained.**
   Erasure emits `GDPR.ERASURE.REQUESTED` (on accept), then exactly one terminal event —
   `GDPR.ERASURE.COMPLETED` (nothing held back) or `GDPR.ERASURE.PARTIAL` (something held back) —
   carrying per-category counts (`deleted`, `anonymized`, `crypto_shredded`, `held`). These are
   sensitive/audited events. The request id, the acting principal, the reason, and the destroyed
   key-version tuples are themselves **permanently retained** (never erased), as the lawful basis +
   evidence of the erasure.

8. **RBAC is enforced per-mode.**
   SaaS tenant endpoint: `tenant_owner` only (tenant_admin/member → 403). Single-user mode: the
   sole user. Platform endpoint: `PlatformOwnerAccess` policy (`platform_admin` claim). Cross-tenant
   callers 404 via the path-tenant membership filter. The subject must be in scope for the chosen
   tenant; an out-of-scope subject → 404 (do not leak existence).

9. **Crypto-shred renders ciphertext permanently unrecoverable (verification).**
   After erasure, reading any crypto-shredded secret version returns null ciphertext
   (`GetVersionPlaintextAsync` → null) and `Status = revoked`; there is no key path to recover the
   plaintext. A verification step (`VerifyErasureAsync`) re-walks the `SubjectDataMap` and asserts:
   no plaintext PII remains in deletable/anonymizable fields, and every `encrypted-secret` entry is
   scrubbed.

10. **Erasure is idempotent.**
    Re-running erasure for the same subject is a safe no-op for already-erased fields/secrets
    (DeleteVersionAsync is already idempotent; anonymized rows stay anonymized; hard-deleted rows
    stay gone) and emits a terminal event with zero-or-unchanged counts — no double-counting, no
    error.

11. **Execution is asynchronous via the platform task queue with an SLA.**
    The endpoint enqueues a platform-scope task (`IPlatformQueuedTaskRepository`) processed by the
    existing `TaskQueueProcessor`; subject erasure must reach a terminal state within the configured
    SLA (default 30 days per GDPR Art. 12(3), configurable via `Gdpr:ErasureSlaDays`), and the
    target operational completion is recorded. A `GET /api/v1/orgs/{tenantId}/erasure/{id}` (and
    admin variant) reports state: `requested → in_progress → completed | partial | failed`.

12. **Tenant-purge reconciliation.**
    Subject erasure and the existing `TENANT.DELETED.SUCCESS` purge share the secret-scrub primitive
    and the per-mode scoping; a documented note + a test confirm a full tenant purge crypto-shreds
    every subject's keys (erasure is the subset, tenant-purge the superset) with no duplicated logic.

13. **Recorder/executor never partially corrupts the chain on failure.**
    If any step throws mid-walk, already-applied deletions/shreds stand (they are individually
    durable + idempotent), the task is marked failed with the last-completed cursor, a retry resumes
    from that cursor, and the 37-2 chain still verifies (re-anchor is the last step, applied
    atomically per affected row).

## Technical Design

### Component overview

```
POST /api/v1/orgs/{tenantId}/erasure            ─┐
POST /api/v1/admin/tenants/{tenantId}/erasure   ─┤  OrgEndpoints / AdminTenantsEndpoints
                                                  │   (RBAC + 202 + enqueue)
                                                  ▼
                              IPlatformQueuedTaskRepository ("GDPR_ERASURE" task)
                                                  ▼  TaskQueueProcessor (existing thread)
                                                  ▼
                                          ErasureExecutor
                       ┌──────────────────────────┼──────────────────────────┐
                       ▼                          ▼                            ▼
              SubjectDataMap (37-7)      ILegalHoldService (37-6)      ISecretStore /
              (what + per-field           (block held records)         ISecretStoreBackend
               erasure policy)                                          (crypto-shred:
                       │                                                 DeleteVersionAsync)
                       ▼                                                       │
              TenantDbContext / ControlPlaneDbContext                         ▼
              (hard-delete / anonymize columns)                       AuditChainAnonymizer (37-2)
                       │                                              (anonymize + re-anchor)
                       └──────────────────────► IEventRepository (GDPR.ERASURE.* events)
```

### Crypto-shred design (the core)

PII falls into three storage classes; each has a destruction strategy that **preserves the
append-only event store and the audit chain**:

| Storage class | Where | Erasure strategy | Audit effect |
|---|---|---|---|
| Plaintext column, `deletable` | e.g. `users.password_hash`, refresh tokens | hard `DELETE` / set NULL | none — not audit data |
| Plaintext column, `must-retain-anonymized` | `users.email`, `display_name`, `avatar_url`, `github_login` | overwrite with stable tombstone `erased:{hashedSubjectId}` | row kept; FK integrity preserved |
| Envelope-encrypted (Epic 29) | `secret_versions.Ciphertext` for subject-scoped secrets | **crypto-shred**: `DeleteVersionAsync` scrubs bytes, `Status=revoked`, KEK link severed | row kept; ciphertext unrecoverable |
| Audit identity fields (37-2) | curated audit rows' actor/target | anonymize to tombstone, **re-hash + re-anchor chain** | chain re-anchored, `VerifyChainAsync` passes |
| `domain_events` (append-only) | DCB stream | **never touched**; PII lives only as encrypted refs/ciphertext rendered dead by the shred above | immutable, intact |

Why crypto-shred for encrypted PII rather than deleting event rows: `DomainEvent` is immutable
(append-only with a `BIGSERIAL` total-order cursor used by `AlertRuleEvaluator`). Destroying the
per-subject key version makes the ciphertext permanently undecryptable while leaving the
tamper-evident structure — bytes and sequence — fully intact. This is the GDPR-recognised
"crypto-erasure" technique (EDPB Guidelines 05/2021; ISO/IEC 27040 §3.7 cryptographic erase).

### Key management for per-subject shred

The Epic 29 cabinet already keys versions individually (`SecretVersionRow.KekId` per version,
ciphertext per version, `revoked` terminal status). For erasure we treat any
**subject-scoped secret** (a `SecretRow` whose `SubjectDataMap` entry attributes it to the subject)
as the unit of shredding: revoke every live version of that secret via `RetireVersionAsync` /
`DeleteVersionAsync`. The destroyed `(SecretId, VersionNumber, KekId)` tuples are recorded in the
terminal `GDPR.ERASURE.COMPLETED`/`PARTIAL` event `data` (retained as evidence). No new key
hierarchy is introduced — per-version envelopes are the existing per-subject granularity.

> Note: a coarser KEK-per-subject scheme is explicitly NOT introduced here — it would require an
> Epic 29 schema change and the existing per-version scrub already gives irreversibility. If a
> future story adds subject-scoped DEKs, `ErasureExecutor` swaps the shred call without changing its
> contract.

### Scope resolution (per-mode, mandatory two-model answer)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who may request erasure? | the sole user (their instance) | `tenant_owner` only (admin/member 403); platform owner via admin endpoint |
| What is the subject scope? | the user's own data (their tenant = themselves) | a data subject within the named tenant |
| Where does the subject's data live? | control-plane (single instance) | control-plane (user identity) + the tenant's own schema (`TenantDbContext`) |
| Who owns the audit-of-erasure record? | the sole user's feed (`TenantId` null) | tenant feed (`TenantId` set) + platform feed for the admin-initiated path |
| Mode source | `ITammaModeProvider` (process-stable) | same |

### Key types (NEW unless marked)

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ErasureExecutor.cs (NEW)
public sealed class ErasureExecutor
{
    public Task<ErasureResult> ExecuteAsync(ErasureRequest request, CancellationToken ct = default);
    public Task<ErasureVerification> VerifyErasureAsync(SubjectRef subject, CancellationToken ct = default);
}

public sealed record ErasureRequest(
    Guid ErasureRequestId, Guid TenantId, SubjectRef Subject, string Reason,
    Guid ActorUserId, ErasureScope Scope /* TenantSelfService | PlatformAdmin */);

public sealed record ErasureResult(
    Guid ErasureRequestId,
    int Deleted, int Anonymized, int CryptoShredded, int Held,
    IReadOnlyList<ShreddedKeyVersion> DestroyedKeyVersions,   // (SecretId, Version, KekId)
    IReadOnlyList<HeldBackItem> HeldBack,                     // (mapEntryId, holdId, reason)
    ErasureStatus Status /* Completed | Partial | Failed */);
```

`SubjectDataMap` + `SubjectRef` + per-field `ErasurePolicy` come from **37-7 (NEW)**;
`ILegalHoldService` from **37-6 (NEW)**; `AuditChainAnonymizer` + `VerifyChainAsync` from
**37-2 (NEW)**. This story defines `ErasureExecutor`, the endpoints, the task payload, the event
types, and the secret-shred integration; it depends on those three contracts and stubs/mocks them
in tests until their stories land.

### Event types (`AGGREGATE.ACTION.STATUS`)

`GDPR.ERASURE.REQUESTED`, `GDPR.ERASURE.COMPLETED`, `GDPR.ERASURE.PARTIAL`,
`GDPR.ERASURE.BLOCKED_BY_HOLD`, `GDPR.ERASURE.FAILED`. Appended via `IEventRepository.AppendAsync`
(control-plane store for the platform path; tenant store for tenant-scope counts) following the
`TenantLifecycleEvents.BuildEvent` shape (tags: `tenantId`, `subjectHash`, `erasureRequestId`,
`mode`, `actorUserId`; metadata: `eventSource=system`). Tags carry a **hashed** subject id, never
the plaintext email/id.

### Async execution & SLA

Endpoint validates RBAC + scope, generates `erasureRequestId`, appends `GDPR.ERASURE.REQUESTED`,
enqueues a `GDPR_ERASURE` platform task (`IPlatformQueuedTaskRepository`), returns `202`. The
existing `TaskQueueProcessor` picks it up and invokes `ErasureExecutor.ExecuteAsync`. Status is
read back from the queued-task row + terminal event. SLA default 30 days
(`Gdpr:ErasureSlaDays`); the executor records a target-completion timestamp and a WARN log if the
task is still non-terminal past SLA.

### Endpoint wiring

```
POST /api/v1/orgs/{tenantId}/erasure              → OrgEndpoints.RequestErasure (tenant_owner)
GET  /api/v1/orgs/{tenantId}/erasure/{id}         → OrgEndpoints.GetErasureStatus
POST /api/v1/admin/tenants/{tenantId}/erasure     → AdminTenantsEndpoints.RequestErasure (PlatformOwnerAccess)
GET  /api/v1/admin/tenants/{tenantId}/erasure/{id}→ AdminTenantsEndpoints.GetErasureStatus
```

Tenant routes sit behind `RequireTenantMembershipFilter` (cross-tenant 404) + a `tenant_owner`
role check (mirrors `ReprovisionOrg`'s `RoleAtLeast` gate, tightened to owner). Admin routes use
`.RequireAuthorization("PlatformOwnerAccess")` (the same policy `AdminTenantsEndpoints` uses today).

## Dependencies

- **Prerequisite (NEW): Story 37-2** — audit hash chain + `VerifyChainAsync` + re-anchor seam
  (`AuditChainAnonymizer`). Erasure re-anchors the chain after anonymizing audit identity fields.
- **Prerequisite (NEW): Story 37-6** — `ILegalHoldService`; blocks erasure of held records.
- **Prerequisite (NEW): Story 37-7** — `SubjectDataMap` + `SubjectRef` + per-field `ErasurePolicy`;
  erasure walks the same inventory DSAR exports.
- **Prerequisite: Epic 29** — secret cabinet (`ISecretStore`, `ISecretStoreBackend`,
  `SecretVersionRow`, `DeleteVersionAsync` scrub). EXISTS — verified.
- **Reuses (EXISTS):** `DomainEvent` (immutable), `IEventRepository.AppendAsync`,
  `TenantLifecycleEvents` (purge machinery + event shape), `IPlatformQueuedTaskRepository` +
  `TaskQueueProcessor` (async), `ITammaModeProvider` (mode), `PlatformOwnerAccess` policy +
  `RequireTenantMembershipFilter` (RBAC), `User` entity (PII fields).
- **Related:** Story 37-12 (compliance dashboard) surfaces erasure status — out of scope here.

## Testing Strategy

Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/` (xUnit; docker-bound suites run
via `sg docker -c "dotnet test ..."`). Mock `SubjectDataMap`, `ILegalHoldService`,
`AuditChainAnonymizer` until 37-2/6/7 land.

1. **Policy application** — a map with both `deletable` and `must-retain-anonymized` fields: deletes
   the former, anonymizes the latter to a stable tombstone; counts correct.
2. **Crypto-shred unrecoverability** — an `encrypted-secret` entry: after erasure
   `GetVersionPlaintextAsync` → null, `Status = revoked`; destroyed `(SecretId, Version, KekId)`
   recorded in the terminal event; no key path recovers plaintext.
3. **Chain intact after erasure** — seed a 37-2 chain over audit rows including the subject; run
   erasure; `VerifyChainAsync` passes (re-anchored), and an asserted-tamper (manual byte edit)
   still fails verification (proves the chain is real, not bypassed).
4. **Append-only store untouched** — assert zero `UPDATE`/`DELETE` statements hit `domain_events`
   during erasure (interceptor/spy).
5. **Legal-hold blocks** — held records skipped, `GDPR.ERASURE.BLOCKED_BY_HOLD` emitted, partial
   result lists held items with hold id + reason, remainder erased, terminal = `PARTIAL`.
6. **Partial-result reporting** — counts per category (`deleted`/`anonymized`/`crypto_shredded`/`held`)
   match the map; `held > 0` ⇒ `GDPR.ERASURE.PARTIAL`, else `COMPLETED`.
7. **Event emission** — `REQUESTED` on accept, exactly one terminal event, subject id hashed in
   tags, request/actor/reason retained.
8. **Idempotent re-run** — second run is a no-op (no double counts, no throw); already-shredded
   versions stay revoked.
9. **RBAC matrix** — tenant_owner ✓, tenant_admin 403, member 403, cross-tenant 404, platform admin
   ✓ on admin route, non-platform 403; out-of-scope subject 404.
10. **SLA / status** — status endpoint reports `requested→in_progress→completed|partial|failed`;
    WARN logged when non-terminal past `Gdpr:ErasureSlaDays`.
11. **Resume after mid-walk failure** — inject a throw mid-map; task marked failed with cursor;
    retry resumes; chain verifies.
12. **Tenant-purge reconciliation** — a full purge shreds every subject's keys via the same
    primitive; erasure (single subject) is the subset.

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ErasureExecutor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ErasureRequest.cs` (records: request/result/held/shredded-key) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ErasureEventTypes.cs` (`GDPR.ERASURE.*`) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/IAuditChainAnonymizer.cs` (consumes 37-2; stub until then) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/GdprErasureTaskHandler.cs` (TaskQueueProcessor handler for `GDPR_ERASURE`) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Compliance/ErasureOptions.cs` (`Gdpr:ErasureSlaDays`) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/ComplianceServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | Modify (add `RequestErasure` + `GetErasureStatus`, tenant routes) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` | Modify (add platform erasure routes) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map routes; register compliance services + task handler) |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Compliance/ErasureDtos.cs` (request/status DTOs) | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ErasureExecutorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ErasureEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Compliance/ErasureChainPreservationTests.cs` | Create |

> The SubjectDataMap / SubjectRef / ErasurePolicy (37-7), ILegalHoldService (37-6), and
> AuditChainAnonymizer impl (37-2) are owned by their respective stories. This story references
> their contracts and mocks them in tests; it does not create the production implementations.

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes/bugs/findings/decisions (esp. Epic 28 KEK decision
   `project_epic28_kek_decision.md` and Epic 29 secret-cabinet notes)
3. Confirmed 37-2/37-6/37-7 contracts are stable enough to mock; if their shapes shift, update the
   stub interfaces in `Services/Compliance/`
4. Planned a TDD approach (Red-Green-Refactor)

### Crypto-shred is destroy-the-key, not delete-the-row

`DomainEvent` is append-only and load-bearing for the alert evaluator's `SequenceNumber` cursor.
Never `UPDATE`/`DELETE` it. The `SecretVersionRow` scrub (`DeleteVersionAsync`) already zeroes
ciphertext and keeps the row — lean on it; do not invent a parallel deletion path. The audit chain
(37-2) is anonymized + **re-anchored** (re-hashed and re-signed as an explicit step), never silently
mutated — a silent edit would itself look like tampering to `VerifyChainAsync`.

### Never log PII; hash the subject everywhere

Subject id/email appears in tags only as a salted hash (`subjectHash`). Logs, events, and the
held-back report use the hash. The plaintext reason + acting principal ARE retained (lawful-basis
evidence) but the subject's own PII is the thing being destroyed — do not echo it back.

### Idempotency + resume

Every destructive primitive is individually idempotent (DeleteVersionAsync no-op on revoked;
anonymize-if-not-already-tombstoned; hard-delete-if-exists). Persist a per-map cursor on the queued
task so a crash resumes rather than restarts. Apply the chain re-anchor last and per-affected-row
atomically so a partial run never leaves an unverifiable chain.

### Reconcile with TENANT.PURGED

A full tenant purge is "erase every subject in the tenant." Share the secret-scrub primitive and
the per-mode scoping with `TenantLifecycleEvents` so purge and subject-erasure can't drift. Subject
erasure does NOT delete the tenant; tenant purge does (and additionally drops the schema).

## Logging Requirements

- **INFO**: Erasure requested (erasureRequestId, tenantId, mode, hashed subject), erasure completed
  (counts per category), task picked up / terminal.
- **DEBUG**: Per-map-entry action (entry kind, policy, action taken), per-secret shred
  (SecretId, version), chain re-anchor span (rows touched).
- **WARN**: Legal-hold blocked items (count, hold ids), erasure still non-terminal past SLA, retry
  resuming from cursor.
- **ERROR**: Executor step failure (step, cursor), chain verification failed post-erasure (must
  page — this is a compliance-integrity incident), secret-store unreachable.
- **Structured context**: `{ erasureRequestId, tenantId, mode, subjectHash, deleted, anonymized,
  cryptoShredded, held }` where applicable.
- **Credential / PII safety**: NEVER log the subject's email/id/PII or any secret plaintext or KEK
  bytes; subject identity is always the salted hash.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
