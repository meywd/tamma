# Epic 37: Audit, Compliance & Data Governance

## Overview

Tamma already captures **raw operational events** in the Epic 4 DCB single event stream (`domain_events` per-tenant + `platform_events` control-plane) and has point audit emitters for secrets (Epic 29) and impersonation (28-R2). Epic 37 turns that substrate into a **compliance-grade product layer**.

This epic builds **ON TOP OF** the Epic 4 DCB event store — it does **NOT** rebuild, duplicate, or replace it. Raw immutable `DomainEvent` / `PlatformEvent` rows stay the authoritative source of truth and audit substrate. Epic 37 adds:

- A **canonical taxonomy** of compliance-relevant sensitive actions (config/persona, RBAC, secret access, BYOK provider-key changes, billing/plan changes, data exports, logins, impersonation, tenant lifecycle, agent actions).
- A **curated, queryable audit-record read-model** (`audit_records`) materialized from the raw stream via a cursor-tracked projection, with a back-reference (`source_event_id` + `source_sequence_number`) to the originating event.
- A **tamper-evident hash chain** over the curated records, with signed periodic checkpoints, so any insertion, deletion, reordering, or in-place mutation — even by an attacker with direct DB write access — is detected and localized.
- Rich **query/search/filter** and **signed export** with an integrity manifest.
- **Retention policies, legal hold, and the GDPR rights** (DSAR access export, right-to-erasure via crypto-shredding) plus **consent/ROPA logging**.
- **SOC2-aligned control mapping and an evidence pack**, and **admin/tenant audit dashboards**.

The epic mirrors the Epic 27 **per-mode ownership** pattern (single-user `user_id` vs SaaS `tenant_id`, exactly-one XOR), the Epic 28 **schema-per-tenant** isolation (per-tenant audit lives in the tenant schema via `TenantDbContext`; platform audit lives in the control plane via `ControlPlaneDbContext` / `PlatformEvent`), and the Epic 29 **secret cabinet** crypto (signing keys and crypto-shred via `ISecretStore` / `TenantSecretProtector`). Target codebase is the C# app `apps/tamma-elsa/`. **`packages/api` is DELETED — it is never a target.**

### Supersedes

Epic 37 supersedes and re-targets the stale, TypeScript-era audit work absorbed from earlier epics:

- **Epic 23 Story 23-4 (configuration-audit)** — drafted spec-only. Its intent (audit of config/persona/convention/agent-config changes) is absorbed into the `CONFIG`/`PERSONA` categories of the Story 37-1 catalog and materialized into the curated `audit_records` projection. The standalone 23-4 story is retired.
- **Epic 23 Story 23-10 (security-access-audit)** — drafted spec-only. Its intent (auth/login, RBAC, secret-access, impersonation auditing) is absorbed into the `AUTH`/`RBAC`/`SECRET`/`IMPERSONATION` categories of the Story 37-1 catalog and the Story 37-10 emission-coverage work. The standalone 23-10 story is retired.
- **The thin `GET /orgs/{id}/audit` endpoint (18-7)** — superseded by the Story 37-3 query/search/filter API and the Story 37-12 dashboard, which read the curated, tamper-evident `audit_records` read-model rather than scanning the raw stream. The 18-7 endpoint shape is re-pointed at the new read-model.

## Stories

| Story | Title | Priority | Status | Est. Effort |
|-------|-------|----------|--------|-------------|
| 37-1 | Sensitive-Action Audit Taxonomy & Curated Audit-Record Projection | P0 | drafted | 5-6 days |
| 37-2 | Tamper-Evident Hash-Chain over Audit Records | P0 | drafted | 5-6 days |
| 37-3 | Audit Query, Search & Filter API | P0 | drafted | 4-5 days |
| 37-4 | Signed Audit Export (JSON/CSV) with Integrity Manifest | P1 | drafted | 3-4 days |
| 37-5 | Audit Retention Policies & Tamper-Aware Pruning | P1 | drafted | 4-5 days |
| 37-6 | Legal Hold | P1 | drafted | 3-4 days |
| 37-7 | GDPR DSAR — Data Subject Access Export | P1 | drafted | 4-5 days |
| 37-8 | GDPR Right-to-Erasure with Crypto-Shredding & Audit Preservation | P1 | drafted | 5-6 days |
| 37-9 | Consent & Data-Processing Logging | P2 | drafted | 3-4 days |
| 37-10 | Sensitive-Action Audit Emission Coverage (BYOK, Billing/Plan, Auth/Login, Agent Actions) | P0 | drafted | 4-5 days |
| 37-11 | SOC2-Aligned Control Mapping & Evidence Pack | P2 | drafted | 3-4 days |
| 37-12 | Admin & Tenant Audit Dashboard UI | P1 | drafted | 4-5 days |

## Architecture

```
+-----------------------------------------------------------------------------+
|              EPIC 37: AUDIT, COMPLIANCE & DATA GOVERNANCE                    |
|        (product layer ON TOP OF Epic 4 DCB — never rebuilds it)             |
+-----------------------------------------------------------------------------+
|                                                                             |
|  SUBSTRATE (Epic 4 — read-only source of truth, immutable):                |
|  +-----------------------------+   +-------------------------------------+  |
|  | domain_events (tenant)      |   | platform_events (control-plane)     |  |
|  | TenantDbContext, BIGSERIAL  |   | ControlPlaneDbContext, BIGSERIAL    |  |
|  +--------------+--------------+   +-----------------+-------------------+  |
|                 |  (cursor read by SequenceNumber)   |                     |
|  +-- LAYER 1: Curated Projection (37-1) -------------------------------+   |
|  |  AuditProjector (cursor-tracked BackgroundService, redacting)       |   |
|  |  ──► audit_records   [tenant schema]  |  audit_records [control-plane]|   |
|  |       per-mode XOR (tenant_id / user_id) · source_event_id (unique) |   |
|  +-------------------------------+------------------------------------+   |
|                                  |                                          |
|  +-- LAYER 2: Integrity (37-2) -----------------------------------------+  |
|  |  hash-chain (record_hash ‖ prev_hash, per-scope chain_sequence)      |  |
|  |  signed checkpoints (Epic 29 cabinet key) · AuditChainVerifier       |  |
|  |  per-tenant chains + one platform chain · tamper → critical alert    |  |
|  +-------------------------------+-------------------------------------+   |
|                                  |                                          |
|  +-- LAYER 3: Access (37-3, 37-4) -------------------------------------+   |
|  |  Query/Search/Filter API        |  Signed Export (JSON/CSV)          |   |
|  |  (actor/target/category/range)  |  + integrity manifest              |   |
|  +-------------------------------+-------------------------------------+   |
|                                  |                                          |
|  +-- LAYER 4: Governance (37-5, 37-6, 37-7, 37-8, 37-9) ---------------+   |
|  |  Retention   | Legal Hold | DSAR    | Right-to-Erasure | Consent /   |   |
|  |  (tamper-     | (blocks    | export  | (crypto-shred +  | ROPA log    |   |
|  |   aware prune)|  prune+    | (read)  |  chain re-anchor)|             |   |
|  |              |  erasure)  |        |                  |             |   |
|  +-------------------------------+-------------------------------------+   |
|                                  |                                          |
|  +-- LAYER 5: Evidence & Surfaces (37-10, 37-11, 37-12) ---------------+   |
|  |  Emission Coverage  |  SOC2 control map + evidence pack | Dashboards |   |
|  |  (BYOK/billing/auth/agent emitters feed Layer 1)                     |   |
|  +---------------------------------------------------------------------+   |
|                                                                             |
+-----------------------------------------------------------------------------+
```

## Key Technical Decisions

### Build ON the DCB stream — never rebuild it

The single biggest failure mode is treating this as "a new event store." It is not. Raw `DomainEvent` / `PlatformEvent` rows stay the immutable source of truth (BIGSERIAL `SequenceNumber` total-order cursor). The curated `audit_records` table is a **derived, fully rebuildable read-model** with a `source_event_id` back-reference: if it is ever wrong or corrupted, the fix is "truncate + reset cursor + re-project," never "patch the row." The projector only **reads** the DCB store; it never appends, mutates, or deletes raw events.

### Cursor-tracked projection (mirror `AlertRuleEvaluator`)

The `AuditProjector` is a near-clone of the existing `AlertRuleEvaluator` background poller: it reads new `DomainEvent` / `PlatformEvent` rows by `SequenceNumber`, persists progress in its own cursor entity (`AuditProjectorCursor`, mirroring `AlertEvaluatorCursor`), resumes on restart, and is crash-isolated per tick. Projection is **eventual and non-blocking** — the per-request hot path is never blocked. A `tamma.audit.projection_lag` OTel gauge tracks lag.

### Per-mode ownership (mirror Epic 27 prompt-store)

Every curated audit row answers ownership in both modes: single-user mode keys rows by `user_id` (`tenant_id` NULL); SaaS mode keys by `tenant_id` (`user_id` NULL). A CHECK constraint enforces exactly-one non-null (mirroring `prompt_overrides` `principal_xor`). There is **no per-user audit layer in SaaS** — tenant audit is owned by `tenant_owner`/`tenant_admin`; members read but never edit/configure.

### Tenant vs platform scope routing

Catalog-matched **tenant-scoped** events (those with a `TenantId` in SaaS) materialize into the **tenant schema** `audit_records`. **Platform-scoped** events (`TenantId` null — orchestrator/platform/lifecycle, e.g. impersonation against the platform) materialize into the **control-plane** `audit_records`. In single-user mode all rows collapse to the single-user `user_id` store. The tenant global query filter (same defence-in-depth as `EventRepository.ListByTenantAsync`) enforces cross-tenant isolation.

### Tamper-evidence: two independent hash chains

Mirroring the existing tenant-vs-platform event-plane split, there are **per-tenant chains** (one per `tenant_id`, in the tenant store) and a **single platform chain** (in the control-plane store). A record belongs to exactly one chain; chains never cross-link. `record_hash = SHA-256(prev_hash ‖ canonical(record))` over a deterministic, culture-invariant, field-ordered serialization. Per-scope insert is serialized with `pg_advisory_xact_lock` so concurrent appends stay strictly monotonic. Signed checkpoints anchor the chain head using an Epic 29 **cabinet** signing key (never a plaintext env key), with `key_version` so signing-key rotation does not invalidate historical anchors. A Postgres append-only trigger is belt-and-suspenders; the cryptographic chain is the actual proof.

### Crypto-shred for GDPR erasure, never event mutation

Right-to-erasure (37-8) never mutates the append-only event store. Plaintext `deletable` columns are hard-deleted; `must-retain-anonymized` columns (e.g. `users.email`) are overwritten with a stable pseudonymous tombstone; envelope-encrypted PII is **crypto-shredded** via the Epic 29 cabinet (`DeleteVersionAsync` scrubs ciphertext bytes, flips `SecretVersionRow.Status` to `revoked`, keeps the row). Curated audit identity fields are anonymized and the 37-2 chain is explicitly **re-anchored** (re-hashed, audited) — never silently edited. The erasure request, acting principal, reason, and destroyed `(SecretId, Version, KekId)` tuples are permanently retained as lawful-basis evidence.

### Redaction before persistence

`payload_json` on every curated record is passed through `Tamma.Core` redaction (`CredentialRedactor.Clean`) **before** the row is persisted — never "redact on read" — so no secret plaintext, API key, token, or password ever lands in `audit_records`.

## Dependencies

### On Other Epics

- **Epic 4 (DCB event store)** — `DomainEvent` / `PlatformEvent` shape + `SequenceNumber` cursor; `IEventRepository` read methods + plane routing. The projector READS this store; it never writes to it. (Hard prerequisite.)
- **Epic 28 (schema-per-tenant)** — `TenantDbContext`, `ControlPlaneDbContext`, the tenant-context global query filter, the cursor/background-pass infra (`AlertRuleEvaluator`, `AlertEvaluatorCursor`, `TaskQueueProcessor`), and KEK rotation lifecycle (checkpoint `key_version` coexistence).
- **Epic 27 (per-mode ownership)** — the single-user `user_id` vs SaaS `tenant_id` XOR pattern (`prompt_overrides`) and `ITammaModeProvider` (`TammaMode.cs`).
- **Epic 29 (secret cabinet)** — `ISecretStore` / `ISecretStoreBackend` / `TenantSecretProtector` (AES-GCM) for checkpoint signing keys and crypto-shred; `SecretVersionRow` per-version envelopes; `ISecretAccessAuditor` (`SecretAuditEventTypes`) as an existing emitter remapped by the catalog.
- **Epic 5 (alerts)** — `IAlertSink` / `AlertEventEmitter` / `BuiltInAlertRuleSeeder` for the critical tamper alert.
- **Epic 20 (billing)** — billing/plan/subscription/budget-change DCB events are absorbed as the `BILLING` category (emission coverage in 37-10).
- **28-R2 (impersonation)** — `IMPERSONATION.STARTED` / `IMPERSONATION.ENDED` emitters remapped by the catalog.

### Internal Story Dependencies

- **37-1** is the foundation: 37-2 (chain), 37-3 (query), 37-5 (retention), and 37-10 (emission coverage) all consume the `audit_records` read-model it produces. 37-1 reserves the `record_hash` / `prev_record_hash` columns and the deterministic insert order that 37-2 needs.
- **37-7 (DSAR / `SubjectDataMap`)**, **37-6 (legal hold / `ILegalHoldService`)**, and **37-2 (chain re-anchor / `AuditChainAnonymizer`)** are all consumed by **37-8 (erasure)** — DSAR (read) and erasure (destroy) share one PII inventory.
- **37-5 (pruning)** consults **37-6 (legal hold)** before deleting and re-anchors the **37-2** chain after tamper-aware pruning.
- **37-3 / 37-4 / 37-12** read the curated, tamper-evident model produced by 37-1 + 37-2.

## Database Schema

```sql
-- 37-1: curated audit read-model (added to BOTH tenant schema and control-plane)
CREATE TABLE audit_records (
  id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),  -- UUID v7
  action_code           TEXT NOT NULL,        -- canonical DCB event type
  category              TEXT NOT NULL,         -- CONFIG|RBAC|SECRET|BYOK|BILLING|EXPORT|
                                               --   AUTH|IMPERSONATION|TENANT|AGENT|PERSONA
  severity              TEXT NOT NULL,         -- info|notice|warning|critical
  actor_user_id         UUID NULL,
  actor_email_snapshot  TEXT NULL,             -- point-in-time email
  target_type           TEXT NULL,             -- 'secret' | 'user' | 'tenant' | ...
  target_id             TEXT NULL,
  outcome               TEXT NOT NULL DEFAULT 'success',  -- success | failure | denied
  ip_address            TEXT NULL,
  user_agent            TEXT NULL,
  occurred_at           TIMESTAMPTZ NOT NULL,
  source_event_id       UUID NOT NULL,         -- back-ref to raw DCB event (idempotency key)
  source_sequence_number BIGINT NOT NULL,      -- DCB total-order cursor (deterministic replay)
  payload_json          JSONB NOT NULL DEFAULT '{}',  -- REDACTED projection of raw Data/Tags
  -- per-mode ownership — exactly one non-null
  tenant_id             UUID NULL,             -- SaaS
  user_id               UUID NULL,             -- single-user
  -- reserved for 37-2 (left null by 37-1, populated by 37-2)
  chain_sequence        BIGINT NULL,           -- per-scope monotonic
  prev_hash             BYTEA NULL,            -- 32 bytes (genesis for first)
  record_hash           BYTEA NULL,            -- 32 bytes SHA-256
  CONSTRAINT ck_audit_records_principal_xor CHECK (
    (user_id IS NOT NULL AND tenant_id IS NULL)
    OR (user_id IS NULL AND tenant_id IS NOT NULL)
  )
);
CREATE UNIQUE INDEX uq_audit_records_source_event ON audit_records (source_event_id);
CREATE INDEX ix_audit_records_tenant_occurred ON audit_records (tenant_id, occurred_at);
CREATE INDEX ix_audit_records_seq ON audit_records (source_sequence_number);

-- 37-1: projector cursor (mirror AlertEvaluatorCursor)
CREATE TABLE audit_projector_cursors (
  projector_id                  TEXT PRIMARY KEY DEFAULT 'default',
  last_domain_sequence_number   BIGINT NOT NULL DEFAULT 0,
  last_platform_sequence_number BIGINT NOT NULL DEFAULT 0,
  updated_at                    TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- 37-2: signed chain checkpoints
CREATE TABLE audit_chain_checkpoints (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  scope         TEXT NOT NULL,            -- 'tenant' | 'platform'
  tenant_id     UUID NULL,                -- set for tenant scope; null for platform
  head_sequence BIGINT NOT NULL,
  head_hash     BYTEA NOT NULL,           -- 32 bytes
  signed_at     TIMESTAMPTZ NOT NULL,
  signature     BYTEA NOT NULL,           -- HMAC-SHA256 via Epic 29 cabinet key
  key_version   INTEGER NOT NULL,         -- which cabinet key version signed
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT scope_tenant_consistency CHECK (
    (scope = 'platform' AND tenant_id IS NULL)
    OR (scope = 'tenant' AND tenant_id IS NOT NULL)
  )
);
CREATE INDEX ix_audit_chain_checkpoints_scope_seq
  ON audit_chain_checkpoints (scope, tenant_id, head_sequence DESC);

-- 37-2: append-only trigger on audit_records rejects UPDATE/DELETE of chain + core fields
--        (belt-and-suspenders; the cryptographic chain is the actual proof)

-- 37-5 retention policies, 37-6 legal holds, 37-9 consent/ROPA records, and the
-- 37-7/37-8 DSAR/erasure request-status rows are added by their respective stories,
-- each per-mode-keyed (tenant_id / user_id XOR) in the appropriate store.
```

## Implementation Phases

### Phase 1: Foundation & Integrity (Stories 37-1, 37-2, 37-10) — P0

- Sensitive-action catalog (≥30 codes, 11 categories, SOC2 control ids), curated `audit_records` projection (cursor-tracked, redacting), per-mode + tenant/platform scope routing.
- Tamper-evident hash chain, signed cabinet-keyed checkpoints, `AuditChainVerifier`, verify endpoints, append-only trigger, critical tamper alert.
- Emission coverage: wire BYOK, billing/plan, auth/login, and agent-action emitters so the catalog has live events to project.
- Estimated: 14-17 days

### Phase 2: Access (Stories 37-3, 37-4) — P0/P1

- Query/search/filter API over the curated read-model (actor/target/category/severity/outcome/range), per-mode RBAC.
- Signed export (JSON/CSV) with an integrity manifest.
- Estimated: 7-9 days

### Phase 3: Governance (Stories 37-5, 37-6, 37-7, 37-8, 37-9) — P1/P2

- Retention policies + tamper-aware pruning (re-anchors the chain), legal hold.
- GDPR DSAR access export (`SubjectDataMap`), right-to-erasure with crypto-shredding + audit preservation, consent/ROPA logging.
- Estimated: 19-24 days

### Phase 4: Evidence & Surfaces (Stories 37-11, 37-12) — P1/P2

- SOC2-aligned control mapping + evidence pack.
- Admin & tenant audit dashboard UI.
- Estimated: 7-9 days

## Success Metrics

- **Coverage**: 100% of catalog-defined sensitive actions (≥30 codes across all 11 categories) materialize into `audit_records`; a catalog-completeness CI test fails if any existing emitter event type is dropped/renamed without updating the catalog.
- **No data leak**: zero secret plaintext, API key, token, or password ever appears in `audit_records.payload_json` (redaction-before-persist test is the gate).
- **Tamper detection**: any insertion, deletion, reordering, or in-place mutation of a curated record is detected and localized to the exact `chain_sequence`; verification of 100k records completes within the documented budget (target < 10s on the reference VPS).
- **Integrity**: the 37-2 chain still verifies after retention pruning (37-5) and after GDPR erasure (37-8); a real byte-level tamper still fails verification (proves the chain is not bypassed).
- **Isolation**: a tenant-A audit record never materializes into tenant-B's schema; a tenant-scoped event never lands in the control-plane store (and vice-versa); the tenant global query filter rejects cross-tenant reads.
- **Projection lag**: `tamma.audit.projection_lag` stays within threshold; a full pass drives it to 0.
- **GDPR SLA**: subject erasure reaches a terminal state within the configured SLA (default 30 days, GDPR Art. 12(3)); crypto-shredded ciphertext is permanently unrecoverable (`GetVersionPlaintextAsync` → null).
- **No write-path change**: zero `UPDATE`/`DELETE` against `domain_events` / `platform_events` from any Epic 37 component — the event store remains the immutable source of truth.

## Reference Documents

- [Epic 4 — DCB Event Sourcing](../epic-4/README.md) — the substrate Epic 37 builds on (read-only)
- [Epic 27 — Convention/Prompt Store](../epic-27/README.md) — per-mode ownership pattern (`prompt_overrides` XOR)
- [Epic 28 — Schema-per-Tenant](../epic-28/README.md) — tenant/control-plane context split, cursor/background-pass infra, KEK rotation
- [Epic 29 — Secret Cabinet](../epic-29/README.md) — `ISecretStore` / `TenantSecretProtector` crypto, crypto-shred primitive
- [GDPR Art. 17 — Right to erasure](https://gdpr-info.eu/art-17-gdpr/)
- [GDPR Art. 30 — Records of processing activities (ROPA)](https://gdpr-info.eu/art-30-gdpr/)
- [EDPB Guidelines 05/2021 — crypto-erasure](https://edpb.europa.eu/)
- [ISO/IEC 27040 — cryptographic erase](https://www.iso.org/standard/80194.html)
- [SOC2 Trust Services Criteria (Common Criteria CC6/CC7)](https://www.aicpa-cima.com/topic/audit-assurance/audit-and-assurance-greater-than-soc-2)

---

**Last Updated**: 2026-06-17
**Epic Owner**: TBD
**Implementation Start**: TBD
**Total Estimated Effort**: 47-59 days
