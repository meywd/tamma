# Story 37-2: Tamper-Evident Hash-Chain over Audit Records

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **compliance / security operator** (platform owner) and a **tenant administrator**,
I want every curated audit record to carry a cryptographic hash that links it to the prior record in its scope, plus a verification routine and signed periodic checkpoints,
So that any insertion, deletion, reordering, or in-place mutation of the audit trail — even by an attacker with direct database write access — is detected and localized, satisfying SOC2 / ISO27001 integrity-of-audit-evidence controls.

## Priority

P0 - Tamper-evidence is the integrity backbone of the entire audit/compliance product layer (Epic 37). Without it, the curated trail from 37-1 is merely a copy that an insider could silently rewrite.

## Context & Boundary

This story sits on the **curated `audit_records` projection** delivered by Story **37-1**, NOT on the raw DCB `domain_events` event store (`Tamma.Data/Entities/DomainEvent.cs`). The raw event store is already monotonic and append-mostly (BIGSERIAL `SequenceNumber`); this story adds an *independent* cryptographic integrity layer over the curated read-model so the human-facing audit trail can be proven unmodified.

Two independent chains exist, mirroring the two event planes already in the codebase (per `AlertEventEmitter`'s tenant-vs-platform split):

- **Per-tenant chains** — one hash-chain per `tenant_id`, persisted in the tenant's own store (`TenantDbContext`).
- **Platform chain** — a single chain for platform-scoped audit records (`tenant_id` NULL), persisted in the control-plane store (`ControlPlaneDbContext`).

A record is only ever a member of exactly one chain (its scope). Chains never cross-link.

> **NEW vs EXISTING**: `audit_records` and its projector (`AuditProjector`) are introduced by **37-1** and are treated here as EXISTING dependencies. Every component this story adds (chain columns, `audit_chain_checkpoints`, `AuditChainVerifier`, the checkpoint workflow, the verify endpoints) is **NEW**. The spec's cited paths `Tamma.Core/Audit/AuditChainVerifier.cs`, `Tamma.Data/Audit/AuditProjector.cs`, and `Tamma.Data/Entities/AuditChainCheckpoint.cs` do not exist yet — `Tamma.Core` and the Elsa `Workflows` folder DO exist as target locations (verified). `packages/api` is deleted and is never a target.

## Acceptance Criteria

1. **Chain columns on `audit_records`.** The `audit_records` entity (from 37-1) gains `record_hash` (`bytea`/`char(64)` hex, SHA-256) and `prev_hash` columns, plus a per-scope monotonic `chain_sequence` (`bigint`). The first record in each scope chains its `prev_hash` to a fixed **genesis constant** (`AuditChainGenesis.Hash`, a documented well-known 32-byte value). Additive EF migration on both `TenantDbContext` (per-tenant chain) and `ControlPlaneDbContext` (platform chain); `dotnet ef migrations has-pending-model-changes` reports none afterward.

2. **Hash computed over a canonical serialization.** `record_hash = SHA-256( prev_hash ‖ canonical(record) )` where `canonical(record)` is a deterministic, field-ordered, culture-invariant serialization of the record's stable fields (id, scope, tenant_id, actor, action, target, occurred_at in UTC ISO-8601 ms, payload). A dedicated `AuditRecordCanonicalizer` produces byte-stable output independent of JSON key ordering or platform locale; canonicalization is unit-tested for stability across two runs and across machines (no `Dictionary` enumeration-order dependence).

3. **Chaining enforced at insert time inside `AuditProjector`.** When `AuditProjector` (37-1) appends a record, it reads the current chain head for that scope, sets `prev_hash` = head's `record_hash` (or genesis), computes `record_hash`, assigns the next `chain_sequence`, and persists atomically. Per-scope insert is **serialized** (Postgres `pg_advisory_xact_lock` keyed on the scope, mirroring `PostgresAdvisoryLeaderLock`) so concurrent appends to the same chain remain strictly monotonic with no forked/duplicate sequence.

4. **`AuditChainVerifier.VerifyAsync(scope, from, to)`** recomputes each record's hash from its canonical form and validates `prev_hash` linkage record-to-record across the requested range. It returns either `Ok` or the **first broken link** as a structured result: `{ recordId, chainSequence, reason }` where `reason ∈ { mutated, missing (gap in chain_sequence), reordered, prev_hash_mismatch }`. Verification is O(n) over the range and streams records rather than loading the whole chain into memory.

5. **Signed checkpoints (`audit_chain_checkpoints`).** A new entity/table `audit_chain_checkpoints` (`scope`, `tenant_id` nullable, `head_sequence`, `head_hash`, `signed_at`, `signature`, `key_version`) records a signed anchor of the chain head. The signature is an HMAC-SHA256 (or AES-GCM envelope) over `canonical(scope ‖ head_sequence ‖ head_hash ‖ signed_at)` using a signing key drawn from the **Epic 29 secret cabinet** via `ISecretStore` / `TenantSecretProtector` — **never** a plaintext env key. `key_version` records which key version signed it so signing-key rotation does not invalidate historical checkpoints.

6. **Checkpoint written on a schedule and on demand.** A `BackgroundService` scheduler (`AuditChainCheckpointScheduler`, modeled on `HourlyAnalyticsRollupScheduler`) dispatches an Elsa workflow (`AuditChainCheckpointWorkflow`) hourly (configurable cadence, `RunOnStartup` gate, multi-pod-safe via `pg_try_advisory_lock`) that writes one checkpoint per active scope. An on-demand path (admin endpoint, below) writes a checkpoint immediately.

7. **Cross-checkpoint verification.** `VerifyAsync` over a range spanning a checkpoint confirms the persisted checkpoint `head_hash` matches the recomputed chain head at `head_sequence` AND that the checkpoint `signature` validates against the cabinet key for its `key_version`. A record mutated/reordered between two checkpoints is detected and the broken link is localized to the exact `chain_sequence`; a checkpoint whose signature fails validation is reported distinctly from a chain-body break.

8. **Verification endpoints (per-mode RBAC).** `GET /api/v1/orgs/{tenantId}/audit/verify` (tenant member; `tenant_admin`+ required, mirroring `OrgEndpoints.ListTenantAudit`) verifies that tenant's chain and never reads another tenant's records or the platform chain. `GET /api/admin/audit/verify` (`PlatformOwnerAccess`) verifies the platform chain and, with a `tenantId` query param, any tenant's chain. Both return `{ status, firstBrokenLink?, lastCheckpoint }`. Optional `from`/`to` query params bound the range (defaults: from last checkpoint, to head).

9. **DCB events emitted.** `AUDIT.CHAIN.VERIFIED` (on a clean verify) and `AUDIT.CHAIN.TAMPER_DETECTED` (on any break) are appended via `IEventRepository` (tenant-scope → tenant `domain_events`; platform-scope → `platform_events`), following the `AlertEventEmitter` plane-routing precedent. `AUDIT.CHAIN.CHECKPOINTED` is emitted per checkpoint write. Tags include `scope`, `tenantId`, `chainSequence`, `reason`. No record payload contents are copied into event data.

10. **Tamper detection raises a critical alert.** A `AUDIT.CHAIN.TAMPER_DETECTED` event raises a `critical` alert through the existing `IAlertSink.RaiseAsync` / `AlertEventEmitter` path (`Tamma.Api/Services/Alerts`), `TenantId` set for tenant-scope tampering and null (platform feed) for platform-scope. A built-in alert rule (`audit-chain-tamper`, severity `critical`) on `AUDIT.CHAIN.TAMPER_DETECTED` is seeded idempotently by `BuiltInAlertRuleSeeder` so detection fans out with no manual rule setup.

11. **Append-only enforcement (defense-in-depth).** A Postgres trigger (or rule) on `audit_records` rejects `UPDATE` and `DELETE` on the chain columns and core record fields, so accidental ORM writes can't silently rewrite history; the hash-chain is the *cryptographic* guarantee and the trigger is the *belt-and-suspenders* DB guarantee. The trigger is documented as a best-effort barrier (a superuser/`ALTER TABLE DISABLE TRIGGER` still bypasses it — which is exactly why the cryptographic chain exists).

12. **Performance acceptable.** Per-record chaining adds bounded overhead to the 37-1 projection: hashing is O(record size); the per-scope advisory lock is held only for the head read + insert. A verification of 100k records completes within a documented budget (target < 10s on the reference VPS) by streaming + batch reads. A perf test asserts append throughput regression from chaining stays under a documented threshold.

13. **Per-mode ownership answered.** single-user mode: the sole user owns and verifies their (single) chain — system/platform-scope and the user's records surface in one verify view; the verify endpoint requires only an authenticated user. SaaS mode: tenant chains are owned by `tenant_owner`/`tenant_admin` (`member` → 403 on verify, mirroring prompt-store RBAC); the platform chain is owned by the platform owner (`PlatformOwnerAccess`) and is never exposed to tenants. Mode is read from `ITammaModeProvider`.

14. **Unit + integration tests** (see Testing Strategy): clean chain verifies OK; mutate one record → `mutated` detected at its sequence; delete a record → `missing` gap detected; reorder two records → `reordered`/`prev_hash_mismatch` detected; tampered checkpoint signature → detected and reported distinctly; concurrent inserts on one scope keep `chain_sequence` strictly monotonic with no fork; cross-tenant isolation (verifying tenant A never touches tenant B's chain); per-mode RBAC matrix.

## Technical Design

### Component layout

| Component | Project / namespace | New/Existing |
|---|---|---|
| `audit_records` (+ `record_hash`, `prev_hash`, `chain_sequence`) | `Tamma.Data` (entity from 37-1) | Existing entity, NEW columns |
| `AuditProjector` (chaining at insert) | `Tamma.Data/Audit` (from 37-1) | Existing, NEW chaining logic |
| `AuditChainGenesis` (genesis constant) | `Tamma.Core/Audit` | NEW |
| `AuditRecordCanonicalizer` | `Tamma.Core/Audit` | NEW |
| `AuditChainHasher` (SHA-256 compose) | `Tamma.Core/Audit` | NEW |
| `IAuditChainVerifier` + `AuditChainVerifier` | `Tamma.Core/Audit` (logic) reading via `Tamma.Data` repo | NEW |
| `AuditChainCheckpoint` (entity) | `Tamma.Data/Entities` | NEW |
| `IAuditChainSigner` + `SecretCabinetAuditChainSigner` | `Tamma.Api/Services/Audit` | NEW (uses `ISecretStore`/`TenantSecretProtector`) |
| `AuditChainCheckpointWorkflow` + `AuditChainCheckpointScheduler` | `Tamma.ElsaServer/Workflows` | NEW (mirror `HourlyAnalyticsRollup*`) |
| Verify endpoints | `Tamma.Api/Endpoints/OrgEndpoints.cs`, `AdminEndpoints.cs` | Existing files, NEW handlers |
| `AUDIT.CHAIN.*` event emission | via `IEventRepository` / `AlertEventEmitter` pattern | NEW event types, existing seam |
| `audit-chain-tamper` built-in alert rule | `Tamma.Api/Services/Alerts/Rules/BuiltInAlertRules.cs` | Existing file, NEW spec |
| Append-only trigger | EF migration raw SQL | NEW |

> Note: the spec lists `Tamma.Api/Services/Secrets/ISecretStore.cs` as a primary component — it is **existing** (verified) and is consumed (not modified) by the new `SecretCabinetAuditChainSigner`. The Epic 29 cabinet already emits access-audit events per read, so signing-key reads are themselves audited.

### Hash-chain schema (additive)

```sql
-- on audit_records (37-1 table), both tenant + control-plane stores
ALTER TABLE audit_records
  ADD COLUMN chain_sequence BIGINT NOT NULL,        -- per-scope monotonic
  ADD COLUMN prev_hash      BYTEA  NOT NULL,         -- 32 bytes (genesis for first)
  ADD COLUMN record_hash    BYTEA  NOT NULL;         -- 32 bytes SHA-256

-- one chain per scope: tenant chain is the whole tenant store table;
-- platform chain is the whole control-plane store table. Uniqueness of
-- chain_sequence per scope is enforced per-store.
CREATE UNIQUE INDEX uq_audit_records_chain_seq ON audit_records (chain_sequence);

CREATE TABLE audit_chain_checkpoints (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  scope         TEXT NOT NULL,                       -- 'tenant' | 'platform'
  tenant_id     UUID NULL,                           -- set for tenant scope; null for platform
  head_sequence BIGINT NOT NULL,
  head_hash     BYTEA  NOT NULL,                     -- 32 bytes
  signed_at     TIMESTAMPTZ NOT NULL,
  signature     BYTEA  NOT NULL,
  key_version   INTEGER NOT NULL,                    -- which cabinet key version signed
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT scope_tenant_consistency CHECK (
    (scope = 'platform' AND tenant_id IS NULL)
    OR (scope = 'tenant' AND tenant_id IS NOT NULL)
  )
);
CREATE INDEX ix_audit_chain_checkpoints_scope_seq
  ON audit_chain_checkpoints (scope, tenant_id, head_sequence DESC);
```

### Compute (insert-time chaining)

`AuditProjector` (37-1) gains a chaining step. Pseudocode (real impl uses the EF context + `pg_advisory_xact_lock` inside the projection transaction):

```csharp
// inside AuditProjector.AppendAsync, within the projection transaction
await db.AcquireScopeAdvisoryLockAsync(scopeKey, ct);   // serialize this chain
var head = await db.AuditRecords
    .OrderByDescending(r => r.ChainSequence)
    .Select(r => new { r.ChainSequence, r.RecordHash })
    .FirstOrDefaultAsync(ct);

record.ChainSequence = (head?.ChainSequence ?? 0) + 1;
record.PrevHash      = head?.RecordHash ?? AuditChainGenesis.Hash;
var canonical        = AuditRecordCanonicalizer.ToBytes(record);   // deterministic
record.RecordHash    = AuditChainHasher.Compose(record.PrevHash, canonical); // SHA-256(prev ‖ canon)

db.AuditRecords.Add(record);
// committed atomically with the rest of the 37-1 projection
```

`AuditChainHasher.Compose(prev, canon)`:

```csharp
using var sha = SHA256.Create();
sha.TransformBlock(prev, 0, prev.Length, null, 0);
sha.TransformFinalBlock(canon, 0, canon.Length);
return sha.Hash!;   // 32 bytes
```

### Verify

```csharp
public async Task<ChainVerificationResult> VerifyAsync(
    AuditChainScope scope, long? from, long? to, CancellationToken ct)
{
    long? expectedSeq = null;
    byte[]? expectedPrev = (from is null or 1) ? AuditChainGenesis.Hash : null;
    // when starting mid-chain, anchor expectedPrev to the record at (from-1)
    await foreach (var r in repo.StreamRecordsAsync(scope, from, to, ct))
    {
        if (expectedSeq is not null && r.ChainSequence != expectedSeq)
            return ChainVerificationResult.Missing(r.RecordId, expectedSeq.Value); // gap / reorder
        if (expectedPrev is not null && !r.PrevHash.SequenceEqual(expectedPrev))
            return ChainVerificationResult.PrevHashMismatch(r.RecordId, r.ChainSequence);
        var recomputed = AuditChainHasher.Compose(r.PrevHash, AuditRecordCanonicalizer.ToBytes(r));
        if (!recomputed.SequenceEqual(r.RecordHash))
            return ChainVerificationResult.Mutated(r.RecordId, r.ChainSequence);
        expectedPrev = r.RecordHash;
        expectedSeq  = r.ChainSequence + 1;
    }
    // cross-checkpoint confirmation
    var cp = await checkpoints.GetLastCoveringAsync(scope, to, ct);
    if (cp is not null)
    {
        if (!await signer.VerifyAsync(cp, ct))
            return ChainVerificationResult.CheckpointSignatureInvalid(cp.Id, cp.HeadSequence);
        if (cp.HeadSequence == lastSeenSeq && !cp.HeadHash.SequenceEqual(lastSeenHash))
            return ChainVerificationResult.CheckpointHeadMismatch(cp.Id, cp.HeadSequence);
    }
    return ChainVerificationResult.Ok(lastSeenSeq, lastCheckpoint: cp);
}
```

### Signing (Epic 29 cabinet)

`SecretCabinetAuditChainSigner` resolves a signing key from `ISecretStore` (a dedicated cabinet secret, e.g. `SecretRef("platform", null, "audit-chain-signing-key")`), and HMAC-SHA256-signs the canonical checkpoint preimage. The plaintext key is delivered only through the cabinet's out-of-band path (per `ISecretStore`'s plaintext rule); the signer caches the active version in-process with a short TTL and records `key_version` on every checkpoint so rotation (Epic 28/29) doesn't strand old anchors. `TenantSecretProtector`'s AES-GCM is the at-rest envelope the cabinet already uses; this story reuses that protector, it does not invent new key handling.

### Checkpoint scheduling

`AuditChainCheckpointScheduler : BackgroundService` (copy `HourlyAnalyticsRollupScheduler`'s structure verbatim: `Enabled`/`FireAtMinute`/`PollInterval` options, `pg_try_advisory_lock` leader election, `_lastFired` hour-key dedup, WARN-and-continue failure isolation) dispatches `AuditChainCheckpointWorkflow`, which enumerates active scopes (platform + each tenant with new records since its last checkpoint) and writes one signed checkpoint per scope. On-demand checkpointing reuses the same per-scope write routine called directly from the admin endpoint.

### Endpoints

```
GET /api/v1/orgs/{tenantId}/audit/verify          (tenant_admin+; tenant chain only)
    ?from=<seq>&to=<seq>
    → { status: "ok"|"tampered", firstBrokenLink?: {recordId, chainSequence, reason}, lastCheckpoint }

GET /api/admin/audit/verify                        (PlatformOwnerAccess; platform chain, or ?tenantId=<id>)
    ?tenantId=<id>&from=<seq>&to=<seq>
    → same shape

POST /api/admin/audit/checkpoint                   (PlatformOwnerAccess; on-demand, 202)
    ?tenantId=<id>   (omit for platform scope)
```

Tenant handler mirrors `OrgEndpoints.ListTenantAudit`: reads role from `RequireTenantMembershipFilter.TenantRoleItemKey`, requires `TenantRoleHierarchy.Admin`, sets ambient `ITenantContext.SetTenantId(tenantId)` for defense-in-depth global query filtering. Admin handlers gate on the `PlatformOwnerAccess` policy (verified to exist in `Program.cs`).

## Dependencies

- **Prerequisite (hard)**: Story **37-1** — provides `audit_records` entity, `AuditProjector`, and the curated-projection transaction this story hooks chaining into. 37-1 is currently **not yet written** (`docs/stories/epic-37/story-37-1/` is empty); 37-2 cannot start until 37-1's projection schema is settled.
- **Prerequisite**: **Epic 4** (DCB event substrate) — `DomainEvent`, `IEventRepository`, `PlatformEvent` plane routing used for `AUDIT.CHAIN.*` events (verified present).
- **Prerequisite**: **Epic 29** (secret cabinet) — `ISecretStore` / `TenantSecretProtector` for the checkpoint signing key (verified present).
- **Prerequisite**: **Epic 5** (alerts) — `IAlertSink` / `AlertEventEmitter` / `BuiltInAlertRuleSeeder` for the critical tamper alert (verified present).
- **Related**: Epic 28 KEK rotation — checkpoint `key_version` must coexist with the cabinet's key-rotation lifecycle.
- **Related**: Elsa workflow + scheduler infra (`HourlyAnalyticsRollupScheduler` as the copy-from template; verified present).

## Testing Strategy

1. **Canonicalization tests** (`Tamma.Core.Tests/Audit/`): identical record → identical bytes across two serializations and across `Dictionary` insertion orders; differing payloads → differing bytes; UTC/locale invariance (run under a non-invariant culture).
2. **Hasher tests**: `Compose(prev, canon)` is 32 bytes, deterministic, and a single bit flip in either input changes the output (avalanche sanity).
3. **Projector chaining tests** (`Tamma.Data.Tests` / `Tamma.Api.Tests`, docker-bound via `sg docker -c "dotnet test ..."`): sequential appends produce `chain_sequence` 1..N with `prev_hash[i] == record_hash[i-1]`; first record's `prev_hash == AuditChainGenesis.Hash`.
4. **Concurrency test**: N parallel appends to one scope → exactly N rows, `chain_sequence` strictly monotonic 1..N, no duplicate/fork (advisory-lock serialization holds).
5. **Verifier tamper matrix** (the AC14 core): clean → `Ok`; in-place mutate one record's payload → `mutated` at that sequence; delete a middle record → `missing` gap; swap two records' sequences → `reordered`/`prev_hash_mismatch`; clip the chain tail → detected at the truncation point.
6. **Checkpoint tests**: checkpoint signature validates against cabinet key; corrupt the stored `signature` → `CheckpointSignatureInvalid` (distinct from body break); mutate a record between two checkpoints → break localized to its sequence and head-hash mismatch surfaced; rotated signing key (new `key_version`) still validates historical checkpoints with their original version.
7. **Isolation tests**: verifying tenant A's chain issues no read against tenant B's store / platform store; admin platform verify never reads tenant rows unless `?tenantId` given.
8. **RBAC / per-mode tests**: SaaS `member` → 403 on tenant verify; `tenant_admin` → 200; cross-tenant → 404/403; platform endpoints reject non-owner; single-user mode lets the sole authenticated user verify.
9. **Endpoint tests**: response shape, `from`/`to` bounding, `AUDIT.CHAIN.VERIFIED` / `AUDIT.CHAIN.TAMPER_DETECTED` emitted with correct plane + tags; tamper raises a `critical` alert (assert `IAlertSink` invoked).
10. **Scheduler tests**: `RunOnStartup=false` gates the loop; single tick writes one checkpoint per active scope; multi-pod advisory lock yields one leader (mirror `HourlyAnalyticsRollupSchedulerTests`).
11. **Append-only trigger test**: direct `UPDATE`/`DELETE` on `audit_records` chain columns is rejected by the trigger.
12. **Performance test**: 100k-record verify within the documented budget; per-append chaining overhead under the documented threshold.

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Audit/AuditChainGenesis.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Audit/AuditRecordCanonicalizer.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Audit/AuditChainHasher.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Audit/IAuditChainVerifier.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Audit/AuditChainVerifier.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Audit/ChainVerificationResult.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Audit/AuditChainScope.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AuditChainCheckpoint.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Audit/AuditProjector.cs` | Modify (37-1 file — add insert-time chaining + advisory lock) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AuditRecord.cs` | Modify (37-1 file — add chain columns) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (configure new columns + checkpoint entity + index) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/*` | Create (EF migration — chain columns + append-only trigger) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*` | Create (EF migration — chain columns + checkpoint table + trigger) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IAuditChainRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/AuditChainRepository.cs` | Create (streaming reads + advisory lock helper) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/IAuditChainSigner.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/SecretCabinetAuditChainSigner.cs` | Create (uses `ISecretStore`/`TenantSecretProtector`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditChainEventEmitter.cs` | Create (`AUDIT.CHAIN.*` via `IEventRepository`/`IPlatformEventPublisher` + alert raise) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditChainEventTypes.cs` | Create (event-type constants) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AuditChainCheckpointWorkflow.cs` | Create |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AuditChainCheckpointScheduler.cs` | Create (mirror `HourlyAnalyticsRollupScheduler`) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | Modify (add tenant `audit/verify` handler) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` | Modify (add admin `audit/verify` + on-demand checkpoint) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Rules/BuiltInAlertRules.cs` | Modify (add `audit-chain-tamper` rule) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/AuditChainServiceCollectionExtensions.cs` | Create (DI wiring; mirror `AlertServiceCollectionExtensions`) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (wire extension + scheduler + endpoints) |
| `apps/tamma-elsa/tests/Tamma.Core.Tests/Audit/*` | Create (canonicalizer, hasher, verifier matrix) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/*` | Create (projector chaining, signer, endpoints, RBAC, scheduler, trigger, perf) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes/bugs/findings/decisions (notably any 37-1 projection notes and Epic 28/29 KEK decisions)
3. Confirmed Story 37-1 has landed and its `audit_records` schema + `AuditProjector` transaction boundary are stable — 37-2's chaining hooks INTO that transaction
4. Planned the TDD cycle (canonicalizer + hasher first; they are pure and the whole chain's determinism rests on them)
5. Run C# tests via `sg docker -c "dotnet test ..."` for docker-bound suites (the build itself needs no wrapper) — see `reference_dotnet_test_docker`

### Why two chains, not one

The codebase already splits events tenant-vs-platform (`AlertEventEmitter`, `IEventRepository` vs `IPlatformEventPublisher`). Audit records live in `TenantDbContext` (per-tenant) and `ControlPlaneDbContext` (platform). A single global chain would force every tenant's audit append to serialize behind one lock and would couple tenant data lifecycles (a tenant purge would punch a hole in a shared chain). Per-scope chains keep each tenant's integrity independent and let a tenant verify its own trail without platform access.

### Genesis constant

`AuditChainGenesis.Hash` is a fixed, documented 32-byte value (e.g. `SHA-256("tamma.audit.chain.genesis.v1")`). It is a public well-known constant, NOT a secret — its only job is to give the first record a deterministic `prev_hash` so verification has a defined start. Document it inline so an external auditor can reproduce verification.

### Canonicalization is load-bearing

Verification only works if `canonical(record)` is byte-identical at write time and at verify time, forever. Avoid `JsonSerializer` default key ordering, `Dictionary` enumeration order, `DateTime.ToString()` without a fixed format, and culture-sensitive number formatting. Pin a fixed field order, UTC ISO-8601 with millisecond precision (`dayjs`-equivalent `"yyyy-MM-ddTHH:mm:ss.fffZ"` in C#), and invariant culture. Add a `canonicalVersion` byte so a future format change is detectable rather than silently breaking old records.

### Append-only is defense, the chain is the proof

The Postgres trigger blocks accidental ORM writes but a DBA with `ALTER TABLE` can disable it. That is acceptable: the cryptographic chain + signed external-key checkpoints are what make tampering *detectable*. State this in the runbook so operators don't treat the trigger as the security boundary.

### Payload column is `text`, not `jsonb` (code-review fix — CRITICAL)

`audit_records.PayloadJson` is stored as **`text`**, NOT `jsonb`. The chain hash (AC2) is computed at INSERT over the in-memory payload STRING, and verification recomputes it over the value read back from the column — so the stored representation must round-trip byte-for-byte. `jsonb` does **not**: Postgres reorders object keys, strips whitespace, and normalizes numbers/unicode, so write-bytes ≠ read-bytes and **every** chain would verify as `Tampered` at `chain_sequence = 1`. `text` preserves the exact bytes. No code uses jsonb operators on this column (the only jsonb-aware read is `"PayloadJson"::text ILIKE` in `AuditQueryService`, and `text::text` is a no-op cast), so `text` is safe. If a future feature needs server-side JSON operators on the payload, add a separate computed/generated `jsonb` column — do NOT change the hashed column back to `jsonb`. There is a Postgres round-trip verify test (`AuditChainPostgresVerifyTests`) guarding this.

### Checkpointing MUST be enabled for the full tamper-evidence guarantee (op-note — code-review fix)

`record_hash` is unkeyed, so the chain alone cannot detect **tail-truncation** (deletion of the most recent records) — the remaining records still form a self-consistent chain. The only thing that reveals a clipped tail is a **signed checkpoint** whose `head_sequence` exceeds the current chain head. Two hardening pieces make this work:

1. `audit_chain_checkpoints` has its **own append-only trigger** (rejects `DELETE`/`UPDATE`) so an attacker cannot delete the covering checkpoint after clipping records.
2. The verifier asserts the live chain head `>= MAX(checkpoint.head_sequence)` for the scope; a regression is reported as `ChainBreakReason.HeadBelowCheckpoint`.

**Operational dependency:** these only bite if checkpoints actually exist. `AuditChainCheckpointScheduler.RunOnStartup` is **opt-in (`false`)** — mirroring the projector — so periodic checkpointing MUST be explicitly enabled in each deployment (or checkpoints written on demand via `POST /api/admin/audit/checkpoint`) for the full tamper-evidence guarantee against tail-truncation. Document this in the runbook. The default is intentionally left opt-in in this fix (flipping it is an operational change, not a code fix).

### Signing key, not env key (AC5)

Per the spec boundary, the checkpoint signature key comes from the Epic 29 cabinet via `ISecretStore`, encrypted at rest by `TenantSecretProtector` (AES-GCM). Do NOT add a `appsettings`/`env` signing key — that would make a config-file reader able to forge checkpoints. The cabinet read is itself audited (`ISecretAccessAuditor`), so signing-key access leaves a trail.

## Logging Requirements

- **INFO**: chain checkpoint written (scope, head_sequence, key_version), verification completed (scope, range, result), scheduler dispatched.
- **DEBUG**: per-record chaining (scope, chain_sequence) — sampled, not per-row in hot paths; verify cursor progress.
- **WARN**: checkpoint scheduler tick failed (continue), signing-key read degraded/cached-stale, verify range empty.
- **ERROR / CRITICAL**: `AUDIT.CHAIN.TAMPER_DETECTED` (always raises the critical alert), checkpoint signature validation failure, append-only trigger violation surfaced from the DB.
- **Structured context**: `{ scope, tenantId, chainSequence, recordId, reason, keyVersion, rangeFrom, rangeTo }` where applicable.
- **Credential safety**: NEVER log the signing key, key material, or raw audit-record payload contents; hashes are safe to log (they are non-reversible) but record payloads are not.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
