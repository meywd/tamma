# Story 37-2 — Tamper-Evident Hash-Chain over Audit Records (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan step-by-step. Steps use
> checkbox (`- [ ]`) syntax. Project is test-first (TDD) — every step writes tests before
> implementation. Read [BEFORE_YOU_CODE.md](../../guides/BEFORE_YOU_CODE.md) first.

**Story:** [37-2](../../stories/epic-37/story-37-2/37-2-tamper-evident-hash-chain-over-audit-records.md)

**Goal:** Make Tamma's curated `audit_records` projection (from Story 37-1) tamper-evident. Each
record carries `record_hash = SHA-256(prev_hash ‖ canonical(record))` linking it to the prior
record in its scope, forming an append-only hash-chain — one chain per tenant (`TenantDbContext`) and
one platform chain (`ControlPlaneDbContext`). A verifier detects insertion, deletion, reordering, and
mutation and localizes the first broken link. Signed checkpoints (key from the Epic 29 cabinet, not a
plaintext env key) anchor the chain head on a schedule + on demand so even an attacker with DB write
access cannot silently rewrite history. Tamper detection raises a critical alert through the existing
Story 5.6 pipeline.

**Tech stack:** .NET 8 / EF Core 8 / Npgsql in `apps/tamma-elsa`. Crypto via
`System.Security.Cryptography` (SHA-256 + HMAC; `TenantSecretProtector` AES-GCM is the existing
at-rest envelope). Tests in `apps/tamma-elsa/tests/Tamma.Core.Tests/` and
`apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites via
`sg docker -c "dotnet test ..."`). `packages/api` is DELETED — never a target.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

| Seam | Where | State |
|---|---|---|
| Raw DCB event | `src/Tamma.Data/Entities/DomainEvent.cs` | EXISTS — `Type`/`TenantId`/`Tags`/`Data` + BIGSERIAL `SequenceNumber`. This story does NOT touch it; it works on the 37-1 curated read-model. |
| Event append + plane routing | `src/Tamma.Data/Repositories/IEventRepository.cs`, `src/Tamma.Api/Services/Alerts/AlertEventEmitter.cs` | EXISTS — `IEventRepository.AppendAsync` (tenant) vs `IPlatformEventPublisher.AppendAndPublishAsync` (platform). Copy this tenant-vs-platform routing for `AUDIT.CHAIN.*` events. |
| Alert raise + built-ins | `src/Tamma.Api/Services/Alerts/IAlertSink.cs`, `AlertPayload.cs`, `Rules/BuiltInAlertRules.cs`, `BuiltInAlertRuleSeeder` | EXISTS — `RaiseAsync(AlertPayload)`; severities `critical`/`warning`/`info`; built-ins seeded idempotently. Add `audit-chain-tamper` (critical). |
| Crypto envelope | `src/Tamma.Api/Services/Provisioning/TenantSecretProtector.cs` | EXISTS — AES-GCM (12-byte nonce ‖ ct ‖ 16-byte tag), key from cabinet. Reuse as the at-rest envelope; do NOT invent new key handling. |
| Secret cabinet | `src/Tamma.Api/Services/Secrets/ISecretStore.cs` (+ `ISecretAccessAuditor`) | EXISTS — typed cabinet; plaintext only via out-of-band path; every read audited. Source of the checkpoint signing key. |
| Scheduled workflow template | `src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` (+ `HourlyAnalyticsRollupWorkflow.cs`) | EXISTS — `BackgroundService` with `Enabled`/`FireAtMinute`/`PollInterval`, `pg_try_advisory_lock` leader election, `_lastFired` dedup, WARN-and-continue. Copy structure for the checkpoint scheduler. |
| Tenant audit endpoint + RBAC | `src/Tamma.Api/Endpoints/OrgEndpoints.cs` `ListTenantAudit` (~527) | EXISTS — reads role from `RequireTenantMembershipFilter.TenantRoleItemKey`, requires `TenantRoleHierarchy.Admin`, sets ambient `ITenantContext`. Mirror for `audit/verify`. |
| Authz policies | `src/Tamma.Api/Program.cs` (~966–991) | EXISTS — `OwnerAccess`, `PlatformOwnerAccess`, `MemberAccess`, etc. Admin verify uses `PlatformOwnerAccess`. |
| Entity model config | `src/Tamma.Data/TammaModelConfiguration.cs` | EXISTS — single source for EF model config; mirror new columns + checkpoint entity here. |
| Migrations | `src/Tamma.Data/Migrations/{Tenant,ControlPlane}/` | EXISTS — both stores have migration trees. Chain columns are additive on both; checkpoint table + append-only trigger via raw SQL in the migration. |
| Mode provider | `Tamma.Api` `ITammaModeProvider` (SingleUser/SaaS) | EXISTS — drives per-mode RBAC. |
| **37-1 `audit_records` + `AuditProjector`** | `docs/stories/epic-37/story-37-1/` | **EMPTY — 37-1 not yet written.** This story HARD-depends on it. The projector transaction boundary and the curated-record field set are 37-1's deliverable; 37-2 chains inside that transaction. |
| `Tamma.Core` project | `src/Tamma.Core/` | EXISTS — home for the pure crypto/verify logic (`Tamma.Core/Audit/`, NEW). |

**Spec-path reconciliation:** the spec's `Tamma.Core/Audit/AuditChainVerifier.cs`,
`Tamma.Data/Audit/AuditProjector.cs`, `Tamma.Data/Entities/AuditChainCheckpoint.cs` are TARGETS that
don't exist yet (AuditProjector ships in 37-1; the rest are NEW here). `ISecretStore.cs`,
`OrgEndpoints.cs`, `AdminEndpoints.cs` EXIST and are consumed/extended.

---

## Non-goals (YAGNI guard)

- NO change to the raw DCB `domain_events` store or its semantics — integrity there is already
  covered by `SequenceNumber`; this layer is the curated read-model's independent proof.
- NO Merkle tree / blockchain / external timestamp-authority anchoring in v1. A linear hash-chain +
  internally-signed checkpoints is the scope. (External RFC-3161 / transparency-log anchoring is a
  documented future follow-up, not this story.)
- NO re-hashing or back-fill UI. Existing pre-37-2 records (if any from a 37-1 ship without chaining)
  get a one-time genesis-anchored backfill in the migration; no ongoing re-chain tooling.
- NO plaintext signing key in `appsettings`/env (explicitly forbidden by AC5).
- NO per-record online verification on the read path — verification is on-demand / scheduled, not in
  the audit-query hot path.
- NO cross-tenant or global "one big chain" — per-scope chains only (rationale in the story Dev Notes).

---

## Architecture

**Write path (insert-time chaining):** `AuditProjector` (37-1) → acquire per-scope
`pg_advisory_xact_lock` → read chain head → set `prev_hash`/`chain_sequence` → `record_hash =
AuditChainHasher.Compose(prev, AuditRecordCanonicalizer.ToBytes(record))` → persist atomically inside
the 37-1 projection transaction.

**Anchor path (checkpoints):** `AuditChainCheckpointScheduler` (BackgroundService, copy of
`HourlyAnalyticsRollupScheduler`) → dispatch `AuditChainCheckpointWorkflow` → per active scope, read
head → `SecretCabinetAuditChainSigner` signs `canonical(scope ‖ head_sequence ‖ head_hash ‖
signed_at)` with the cabinet key → write `audit_chain_checkpoints` row with `key_version`. On-demand
checkpoint = same per-scope routine called from the admin endpoint.

**Verify path:** `AuditChainVerifier.VerifyAsync(scope, from, to)` streams records, recomputes hashes,
checks `prev_hash` linkage + `chain_sequence` contiguity, then validates the covering checkpoint's
signature + head-hash → returns `Ok` or first broken link `{recordId, chainSequence, reason}`. Verify
emits `AUDIT.CHAIN.VERIFIED` / `AUDIT.CHAIN.TAMPER_DETECTED` (plane-routed); tamper raises a `critical`
alert via `IAlertSink`.

**Per-mode ownership (mandatory two-scoping answer):**

| Question | single-user | SaaS |
|---|---|---|
| Who owns/verifies a **tenant** chain? | The sole user (their one chain). | `tenant_owner`/`tenant_admin` via `/api/v1/orgs/{tenantId}/audit/verify`; `member` → 403. |
| Who owns/verifies the **platform** chain? | The sole user (their instance). | Platform owner only (`PlatformOwnerAccess`); never exposed to tenants. |
| Where do tamper alerts fan out? | The user's feed (`TenantId` null). | Tenant-scope tamper → `TenantId` set (tenant feed); platform-scope → null (admin feed). |
| Mode source | `ITammaModeProvider` | same |

---

## Story breakdown

### 37-2-A: Pure crypto core — canonicalizer + hasher + genesis (no DB)

**Scope:** The deterministic, allocation-careful primitives the whole chain rests on. Pure
`Tamma.Core`, no EF, no I/O.

**Files (new):** `src/Tamma.Core/Audit/AuditChainGenesis.cs` (well-known 32-byte constant +
`canonicalVersion`), `AuditRecordCanonicalizer.cs` (fixed field order, UTC ISO-8601 ms, invariant
culture, no `Dictionary` order dependence), `AuditChainHasher.cs` (`Compose(prev, canon)` → 32-byte
SHA-256), `AuditChainScope.cs` (tenant/platform discriminated value).

**Tests (first):** `tests/Tamma.Core.Tests/Audit/AuditRecordCanonicalizerTests.cs`,
`AuditChainHasherTests.cs` — byte-stability across two runs + across `Dictionary` insertion orders;
locale invariance (run under a non-invariant culture); single-bit-flip avalanche; genesis constant is
reproducible from its documented preimage.

**Acceptance criteria:**
- [ ] `canonical(record)` is byte-identical across runs/cultures/key-orderings; differing payloads differ.
- [ ] `Compose` returns 32 deterministic bytes; flipping one bit of either input changes output.
- [ ] Genesis constant matches `SHA-256("tamma.audit.chain.genesis.v1")` (documented inline).
- [ ] Full `Tamma.Core` suite green.

### 37-2-B: Chain columns + checkpoint entity + append-only trigger (schema)

**Scope:** Additive schema on both stores. Depends on 37-1's `AuditRecord` entity existing.

**Files:** modify `src/Tamma.Data/Entities/AuditRecord.cs` (add `ChainSequence`/`PrevHash`/`RecordHash`),
new `src/Tamma.Data/Entities/AuditChainCheckpoint.cs`, modify `TammaModelConfiguration.cs` (columns,
unique index on `chain_sequence`, checkpoint entity + `scope_tenant_consistency` CHECK + index). New
EF migrations under `Migrations/Tenant/` (chain columns + append-only trigger) and
`Migrations/ControlPlane/` (chain columns + `audit_chain_checkpoints` + trigger; append-only trigger
raw SQL in `migrationBuilder.Sql`). One-time genesis backfill for any pre-existing rows.

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuditChainSchemaTests.cs` (docker-bound) — migration
applies + rolls back cleanly; `has-pending-model-changes` reports none; append-only trigger rejects
`UPDATE`/`DELETE` on chain columns; checkpoint CHECK rejects `scope='platform' AND tenant_id NOT NULL`.

**Acceptance criteria:**
- [ ] Both migrations apply + roll back; no pending model changes.
- [ ] Direct `UPDATE`/`DELETE` of an `audit_records` chain column is rejected by the trigger.
- [ ] Checkpoint CHECK enforces scope↔tenant_id consistency.

### 37-2-C: Insert-time chaining in AuditProjector + chain repository

**Scope:** Hook chaining into 37-1's `AuditProjector` transaction; add the streaming/advisory-lock
repo. Per-scope serialization via `pg_advisory_xact_lock`.

**Files:** modify `src/Tamma.Data/Audit/AuditProjector.cs` (37-1 file — head read, prev/seq/hash
assignment within the existing projection transaction); new
`src/Tamma.Data/Repositories/IAuditChainRepository.cs` + `AuditChainRepository.cs` (head read,
`StreamRecordsAsync(scope, from, to)`, `AcquireScopeAdvisoryLockAsync`, checkpoint read/write).

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuditProjectorChainingTests.cs` (docker-bound) —
sequential appends give `chain_sequence` 1..N with `prev_hash[i]==record_hash[i-1]`; first record's
`prev_hash==genesis`; N parallel appends to one scope → N rows, strictly monotonic, no fork;
tenant-A appends never affect tenant-B / platform chain.

**Acceptance criteria:**
- [ ] Appended records form a valid chain (prev linkage + contiguous sequence).
- [ ] Concurrent same-scope appends stay strictly monotonic (advisory lock holds).
- [ ] Chaining is atomic with the 37-1 projection (rollback leaves no orphan sequence).

### 37-2-D: Verifier — tamper detection + localization

**Scope:** `AuditChainVerifier` over the repo's stream; structured first-broken-link result.

**Files:** new `src/Tamma.Core/Audit/IAuditChainVerifier.cs`, `AuditChainVerifier.cs`,
`ChainVerificationResult.cs` (`Ok` / `Mutated` / `Missing` / `Reordered` / `PrevHashMismatch` /
`CheckpointSignatureInvalid` / `CheckpointHeadMismatch`, each carrying `recordId` + `chainSequence`).

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuditChainVerifierTests.cs` (the AC14 matrix) — clean
→ `Ok`; mutate a payload → `Mutated` at its seq; delete a middle record → `Missing` gap; swap two
sequences → `Reordered`/`PrevHashMismatch`; clip tail → detected at truncation. (Checkpoint-boundary
cases land in 37-2-E once checkpoints exist.)

**Acceptance criteria:**
- [ ] Each tamper class is detected and localized to the exact `chain_sequence`.
- [ ] Verify is O(n) streaming (no whole-chain materialization); 100k verify within budget (perf test in 37-2-H).

### 37-2-E: Signed checkpoints — signer + entity wiring + boundary verify

**Scope:** Cabinet-backed signer + checkpoint write/read; extend verifier to confirm checkpoint
signature + head-hash across a boundary.

**Files:** new `src/Tamma.Api/Services/Audit/IAuditChainSigner.cs`,
`SecretCabinetAuditChainSigner.cs` (resolve signing key via `ISecretStore`; HMAC-SHA256 over canonical
checkpoint preimage; cache active version w/ short TTL; record `key_version`); checkpoint
read/write on `AuditChainRepository`; verifier checkpoint-confirmation branch.

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuditChainSignerTests.cs` +
`CheckpointBoundaryVerifyTests.cs` — sign/verify round-trips against a fake cabinet; corrupt stored
`signature` → `CheckpointSignatureInvalid` (distinct from body break); mutate a record between two
checkpoints → break localized + head-hash mismatch; rotated key (new `key_version`) still validates
historical checkpoints; signing key is read via `ISecretStore` out-of-band path (never logged/returned).

**Acceptance criteria:**
- [ ] Checkpoint signature validates against the cabinet key for its `key_version`.
- [ ] Verify across a checkpoint confirms `head_hash` matches the recomputed head.
- [ ] Tampered signature reported distinctly from a chain-body break.
- [ ] No plaintext env/appsettings signing key exists anywhere (grep-clean).

### 37-2-F: Checkpoint scheduler + Elsa workflow

**Scope:** Periodic + on-demand checkpoint writing. Copy `HourlyAnalyticsRollupScheduler` structure
verbatim.

**Files:** new `src/Tamma.ElsaServer/Workflows/AuditChainCheckpointScheduler.cs` (BackgroundService,
`Enabled`/`FireAtMinute`/`PollInterval`, `pg_try_advisory_lock` leader, `_lastFired` dedup,
WARN-and-continue) + `AuditChainCheckpointWorkflow.cs` (enumerate active scopes → write one signed
checkpoint each, emit `AUDIT.CHAIN.CHECKPOINTED`).

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuditChainCheckpointSchedulerTests.cs` — `Enabled=false`
gates the loop; single tick writes one checkpoint per active scope; multi-pod advisory lock yields one
leader (mirror `HourlyAnalyticsRollupSchedulerTests`).

**Acceptance criteria:**
- [ ] Scheduler writes one checkpoint per active scope per cadence; idempotent within an hour.
- [ ] Multi-pod safe (one leader); failure-isolated (one tick failure doesn't kill the loop).
- [ ] On-demand routine reused by the admin endpoint (37-2-G).

### 37-2-G: Verify endpoints + events + critical alert + built-in rule

**Scope:** HTTP surface, DCB events, alert raise, built-in rule. Per-mode RBAC.

**Files:** modify `src/Tamma.Api/Endpoints/OrgEndpoints.cs` (tenant `GET .../audit/verify`, mirror
`ListTenantAudit` RBAC), modify `AdminEndpoints.cs` (`GET /api/admin/audit/verify` +
`POST /api/admin/audit/checkpoint`, `PlatformOwnerAccess`); new
`src/Tamma.Api/Services/Audit/AuditChainEventEmitter.cs` + `AuditChainEventTypes.cs`
(`AUDIT.CHAIN.VERIFIED|TAMPER_DETECTED|CHECKPOINTED`, plane-routed via `IEventRepository` /
`IPlatformEventPublisher`, tamper → `IAlertSink.RaiseAsync` critical); modify
`Services/Alerts/Rules/BuiltInAlertRules.cs` (`audit-chain-tamper`, critical, EventType
`AUDIT.CHAIN.TAMPER_DETECTED`, throttle 300); new
`src/Tamma.Api/Extensions/AuditChainServiceCollectionExtensions.cs`; wire in `Program.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Audit/AuditChainEndpointsTests.cs` +
`AuditChainAlertTests.cs` — RBAC matrix (member 403 / tenant_admin 200 / cross-tenant 404 / non-owner
rejected on admin / single-user sole-user OK); tenant verify never reads other tenants/platform;
`from`/`to` bounding; clean verify emits `AUDIT.CHAIN.VERIFIED`; tamper emits
`AUDIT.CHAIN.TAMPER_DETECTED` + raises a `critical` alert (`IAlertSink` invoked, correct plane/tags);
seeder creates `audit-chain-tamper`; on-demand checkpoint endpoint returns 202 + writes a checkpoint.

**Acceptance criteria:**
- [ ] Endpoint shape identical between modes; auth middleware decides scope.
- [ ] Tamper produces a `critical` alert visible at `/api/v1/admin/alerts` (+ tenant feed for tenant scope) with no manual setup.
- [ ] Events carry `scope`/`tenantId`/`chainSequence`/`reason` tags; no record payloads in event data.

### 37-2-H: Performance + hardening pass

**Scope:** Perf budget proof + edge hardening.

**Files:** `tests/Tamma.Api.Tests/Audit/AuditChainPerfTests.cs`; tighten streaming batch sizes;
document budgets in the story Dev Notes / runbook.

**Tests:** 100k-record verify within the documented budget (target < 10s on reference VPS); per-append
chaining overhead under the documented threshold.

**Acceptance criteria:**
- [ ] Verify perf within budget; append overhead within threshold.
- [ ] Full suite green; no new lint/analyzer warnings.

---

## Story order & dependencies

37-2-A → 37-2-B → 37-2-C → 37-2-D → 37-2-E → 37-2-F → 37-2-G → 37-2-H.

- **37-1 is the hard gate for everything from 37-2-B onward** (needs `AuditRecord` + `AuditProjector`).
  37-2-A (pure crypto) can start immediately and in parallel with 37-1.
- 37-2-D depends on 37-2-A (+ a repo from 37-2-C for live tests, but logic is unit-testable on fakes first).
- 37-2-E checkpoint-boundary verify extends 37-2-D and needs the signer (Epic 29 cabinet).
- 37-2-G needs A–F (verifier + signer + checkpoints + events/alerts).

## Risks

- **Canonicalization drift** — the single highest risk. If `canonical(record)` ever changes shape,
  every historical record fails verification. Mitigation: pin field order + invariant culture + UTC
  ms, add a `canonicalVersion` byte, and freeze the canonicalizer behind exhaustive byte-stability
  tests (37-2-A). Any future format change is a new version, never an edit.
- **37-1 not yet written** — `audit_records`/`AuditProjector` are this story's foundation and don't
  exist (`story-37-1/` empty). Do NOT start 37-2-B+ until 37-1's projection transaction boundary and
  record field set are stable; an unstable 37-1 schema churns the canonicalizer + migrations.
- **Per-scope lock on the projection hot path** — `pg_advisory_xact_lock` serializes appends within a
  scope. Audit appends are not ultra-high-frequency, but hold the lock for the minimum window (head
  read + insert only) and keep it transaction-scoped so it auto-releases. Measure in 37-2-H.
- **Signing-key rotation** — checkpoints must survive Epic 28/29 KEK rotation. `key_version` on every
  checkpoint + the signer keeping access to prior versions for verification is load-bearing; coordinate
  with the KEK rotation lifecycle (`project_epic28_kek_decision`).
- **Append-only trigger is not a security boundary** — a DBA can `DISABLE TRIGGER`. The cryptographic
  chain + externally-keyed signed checkpoints are the actual proof. State this in the runbook so the
  trigger isn't mistaken for the guarantee.
- **Two stores, two migration trees** — chain columns are additive on both Tenant + ControlPlane; the
  checkpoint table + trigger live in the right store per scope. Verify `has-pending-model-changes`
  reports none on BOTH after migrations, and mirror entity config only in `TammaModelConfiguration.cs`.
- **Event-store topology shift (Story 28-1 / Epic 30)** — `AUDIT.CHAIN.*` events route tenant-scope →
  tenant store, platform-scope → CP store today (copy `AlertEventEmitter`). When events move
  per-tenant, platform-scope chain events must stay CP-resident — keep the emitter routing explicit.
