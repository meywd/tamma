# Story 37-4: Signed Audit Export (JSON/CSV) with Integrity Manifest

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **compliance officer / platform owner / tenant admin**,
I want to export a time- and filter-bounded slice of the tamper-evident audit
trail in JSON and CSV, accompanied by an integrity manifest (record count,
chain head hash + checkpoint reference, and a cryptographic signature),
So that I can hand the export to an auditor or external reviewer who can
verify offline that the export was not altered and corresponds to the
hash-chained audit log — and so that the act of exporting the audit log is
itself captured in the audit log.

## Priority

P1 - Required compliance-evidence capability for SOC2 / ISO27001 / GDPR audits.

## Scope

Let admins export a slice of the audit trail (the curated, hash-chained
`audit_records` read-model from Stories 37-1/37-2, queried with the same
filters as 37-3) for external review, in JSON and CSV, accompanied by an
**integrity manifest** so the recipient can verify the export was not altered
and corresponds to the tamper-evident chain.

- **Tenant exports own their own audit** (`audit_records` rows scoped to the
  caller's tenant); **platform exports own platform audit** (platform-scope
  records). Per-mode ownership is settled the same way the rest of the audit
  product is (single-user: the sole user; SaaS: tenant_admin+ for the tenant
  slice, platform owner for the platform slice).
- **Large exports run asynchronously** (a `QueuedTask` job + a downloadable,
  encrypted, auto-expiring artifact) to avoid request timeouts — mirroring the
  async pattern already used by tenant provisioning
  (`TaskQueueProcessor`/`QueuedTask`). Small exports (< 10k rows) MAY stream
  synchronously.
- **The export action is itself a sensitive, audited action** — it emits
  `AUDIT.EXPORTED` into `audit_records`, so exporting the audit log appears in
  the audit log.

Target codebase: **C# `apps/tamma-elsa`** — `Tamma.Api` (endpoints + export
service), `Tamma.Data` (the `audit_records` read-model + hash chain from
37-1/37-2, per-tenant; `QueuedTask` for async jobs), signing via the existing
Epic 29 secret cabinet crypto. (The deleted `packages/api` TypeScript API is
NOT a target.)

## Acceptance Criteria

1. **Tenant + platform export endpoints (same filters as 37-3 + format).**
   `POST /api/v1/orgs/{tenantId}/audit/export` (tenant_admin+, gated by
   `RequireTenantMembershipFilter` + admin-role check) and
   `POST /api/admin/audit/export` (`PlatformOwnerAccess`) accept the **same
   filter set as Story 37-3** (time range, actor, action/event type, resource,
   severity, etc.) plus `format=json|csv`, and return **`202 Accepted` with a
   `jobId`**. Small exports (< 10k matching rows) MAY be produced and streamed
   synchronously instead of queued (config-gated threshold).

2. **Export bundle + manifest.json.** The export bundle is a single artifact
   (a `.zip` containing the data file `audit.json` / `audit.csv` plus
   `manifest.json`). `manifest.json` includes: `record_count`, the `filter`
   criteria (echoed), `scope` (`tenant`/`platform` + the tenant id when
   tenant-scoped), the audit chain `head_hash` and `checkpoint_id` at export
   time (from 37-2), `format`, `export_signature`, `generated_at`,
   `generated_by` (actor id), and `manifest_version`.

3. **Signature over canonical manifest.** `export_signature` is computed over a
   canonical (stable key order, no-whitespace) serialization of the manifest's
   signed fields, including a content digest of the exported data file
   (`data_sha256`), using the **Epic 29 cabinet key** (resolved via
   `ISecretStore`/`KekProvider`). The signing algorithm + key version are
   recorded in the manifest so a verifier can select the right key.

4. **Async job lifecycle + download URL.**
   `GET /api/v1/orgs/{tenantId}/audit/export/{jobId}` and
   `GET /api/admin/audit/export/{jobId}` report job state
   (`pending → generating → ready → expired`, plus `failed`) and, when `ready`,
   yield a **time-limited download URL** (or a `download` sub-route guarded by a
   single-use, signed token). The handler that produces the artifact is an
   `ITaskHandler` (`audit.export.*`) driven by `TaskQueueProcessor`.

5. **Encrypted at rest + auto-expiry.** The generated artifact is **stored
   encrypted at rest** (AES-GCM, mirroring `TenantSecretProtector` /
   `AesGcmConnectionStringDecryptor`) and **auto-expires** (default 24h,
   configurable `Audit:Export:ArtifactTtl`). After expiry the job reports
   `expired`, the download route returns `410 Gone`, and a reaper purges the
   ciphertext + DB row.

6. **Offline verifiability.** A verifier (a documented procedure + a small
   `tamma audit verify-export` helper / standalone script) can, given the
   bundle and the public verification material, **(a)** recompute `data_sha256`
   over the data file, **(b)** recompute and confirm `export_signature` over the
   canonical manifest, and **(c)** recompute the audit chain hash over the
   exported rows and confirm it matches the recorded `head_hash`/`checkpoint_id`
   — thereby detecting any post-export tampering of the data file OR the
   manifest. The verification recipe is documented in the story’s Dev Notes and
   the bundle’s `README`.

7. **Export is itself audited (`AUDIT.EXPORTED`).** Initiating an export emits
   an `AUDIT.EXPORTED` audit record (a sensitive action captured in
   `audit_records`, NOT just a DCB event) tagging `actor` (`generated_by`),
   `scope`, `record_count`, `filter` summary, and `format`. Exporting the audit
   log therefore appears in the audit log. (The `AUDIT.EXPORTED` row is written
   so it falls AFTER the exported chain head — it does not retroactively alter
   the slice that was exported.)

8. **CSV formula-injection neutralization.** CSV output **escapes/neutralizes
   formula injection**: any cell whose value begins with `=`, `+`, `-`, `@`,
   tab, or carriage-return is prefixed with a single quote (`'`) (or wrapped per
   the chosen safe-CSV rule), and standard CSV quoting/escaping of `"`, `,`, and
   newlines is applied to every field.

9. **Redaction is preserved in both formats.** The field-level redaction applied
   by Story 37-1 to sensitive audit fields is preserved identically in BOTH JSON
   and CSV output — the export reads the already-redacted read-model and never
   re-derives or un-redacts values.

10. **Per-mode RBAC + isolation.**
    - single-user: the sole user can export their instance's audit
      (system/platform + their own records) — no role gate beyond authn.
    - SaaS: `POST /api/v1/orgs/{tenantId}/audit/export` requires
      `tenant_owner`/`tenant_admin` for the route tenant (member → `403`);
      `POST /api/admin/audit/export` requires `PlatformOwnerAccess`. A tenant
      export NEVER includes platform-scope or other-tenant records (defence in
      depth via the read-model's tenant scoping); a platform export NEVER leaks
      into a tenant's `/orgs/{id}/...` job namespace. `jobId`s are
      scope-namespaced so a tenant cannot fetch/download another scope's job.

11. **Job ownership + cross-tenant fetch rejected.** `GET .../export/{jobId}`
    and the download route resolve the job ONLY within the caller's scope; a
    job belonging to another tenant (or to the platform scope) returns `404`
    (not `403`, to avoid leaking existence).

12. **Graceful failure.** If artifact generation fails (DB error, signing key
    unavailable), the job transitions to `failed` with a non-leaky error
    summary, the partial artifact (if any) is purged, and the failure is logged
    at ERROR; the initiating request still returned `202` and is not blocked.

## Technical Design

### Component overview

```
apps/tamma-elsa/src/
  Tamma.Api/Endpoints/
    OrgEndpoints.cs                         (MODIFY: tenant audit export + status + download)
    AdminEndpoints.cs                       (MODIFY: platform audit export + status + download)
  Tamma.Api/Services/Audit/                 (NEW directory)
    IAuditExportService.cs                  (NEW)
    AuditExportService.cs                   (NEW: orchestrates query → serialize → sign → encrypt → persist)
    AuditExportManifest.cs                  (NEW: manifest record + canonical-serialization + signed-field set)
    AuditExportSigner.cs                    (NEW: sign/verify over canonical manifest via ISecretStore cabinet key)
    AuditCsvWriter.cs                       (NEW: RFC-4180 CSV + formula-injection neutralization)
    AuditJsonWriter.cs                      (NEW: deterministic/streamed JSON array of redacted rows)
    AuditExportTaskHandler.cs               (NEW: ITaskHandler for "audit.export.*")
    AuditExportArtifactProtector.cs         (NEW: AES-GCM at-rest wrapper, mirrors TenantSecretProtector)
    AuditExportOptions.cs                   (NEW: SyncRowThreshold, ArtifactTtl, MaxRows, ...)
  Tamma.Api/Extensions/
    AuditExportServiceCollectionExtensions.cs (NEW: AddAuditExport(); called once from Program.cs)
  Tamma.Data/Entities/
    AuditExportJob.cs                       (NEW: job + artifact metadata row)
    QueuedTask.cs                           (REUSE: async job queue; new Type "audit.export.{scope}")
  Tamma.Data/Migrations/ControlPlane/       (NEW: additive migration for audit_export_jobs)
```

> `audit_records` + the hash chain (`head_hash`, checkpoint) come from Stories
> 37-1 (curated record + redaction) and 37-2 (hash chain + checkpoints); the
> shared filter model comes from 37-3. This story consumes those read APIs and
> does not redefine them. Where 37-1/37-2/37-3 types are not yet merged, the
> implementation depends on their public surfaces and is sequenced after them
> (see Dependencies).

### Endpoint shapes

```
POST /api/v1/orgs/{tenantId}/audit/export      → 202 { jobId, status }      (tenant_admin+)
GET  /api/v1/orgs/{tenantId}/audit/export/{id} → 200 { status, recordCount?, downloadUrl?, expiresAt? }
GET  /api/v1/orgs/{tenantId}/audit/export/{id}/download → 200 (application/zip) | 410 Gone

POST /api/admin/audit/export                    → 202 { jobId, status }      (PlatformOwnerAccess)
GET  /api/admin/audit/export/{id}               → 200 { status, ... }
GET  /api/admin/audit/export/{id}/download      → 200 (application/zip) | 410 Gone
```

Request body (both scopes), reusing the 37-3 filter DTO:

```jsonc
{
  "format": "json",            // "json" | "csv"
  "filter": {                  // identical shape to Story 37-3 query filter
    "from": "2026-01-01T00:00:00Z",
    "to":   "2026-06-17T00:00:00Z",
    "actor": "...",            // optional
    "action": "...",          // optional (audit event type)
    "resource": "...",        // optional
    "severity": "..."         // optional
  }
}
```

Sync fast path: when the matching `record_count < SyncRowThreshold` (default
10_000) the handler MAY build the bundle inline and return the artifact (or a
`ready` job with an immediate `downloadUrl`); otherwise it enqueues a
`QueuedTask` of type `audit.export.tenant` / `audit.export.platform` and
returns `202` with the persisted `AuditExportJob.Id`.

### Manifest

```jsonc
// manifest.json (signed fields are everything except `export_signature`)
{
  "manifest_version": "1.0",
  "scope": "tenant",                 // "tenant" | "platform"
  "tenant_id": "…",                  // present when scope=tenant
  "format": "csv",
  "record_count": 4821,
  "filter": { /* echoed 37-3 filter */ },
  "chain": {
    "head_hash": "…",                // 37-2 chain head at export time
    "checkpoint_id": "…",            // 37-2 checkpoint reference
    "algorithm": "sha-256"
  },
  "data_sha256": "…",                // digest of the data file (audit.json/csv)
  "generated_at": "2026-06-17T12:00:00.000Z",
  "generated_by": "<actor user id>",
  "signing": { "alg": "HMACSHA256", "key_version": 3 },  // or ECDSA per cabinet key type
  "export_signature": "base64(…)"    // over canonical(signed-fields)
}
```

- **Canonical serialization**: stable (sorted) key order, UTC ISO-8601 ms
  timestamps, no insignificant whitespace — deterministic so the verifier
  reproduces byte-for-byte.
- **`data_sha256`** binds the signature to the actual data file so tampering
  with rows is detected even if the manifest is untouched.
- **`chain.head_hash` / `checkpoint_id`** bind the export to the tamper-evident
  chain so a verifier can recompute the chain over the exported rows and confirm
  they reconstruct the recorded head (detecting row insertion/deletion).

### Signing (Epic 29 cabinet key)

`AuditExportSigner` resolves the platform signing key via `ISecretStore`
(`SecretScope` platform, a dedicated `SecretPurpose` such as
`AuditExportSigning`) / `KekProvider`. The implementation uses the existing
crypto already present in the repo (`System.Security.Cryptography` —
HMAC-SHA-256 with a cabinet-held key as the minimum bar, or ECDSA/RSA if a
cabinet asymmetric key is provisioned, which is preferable for offline
verification without sharing a secret). `key_version` is recorded so rotation
(Epic 28-12 KEK rotation precedent) does not break old-bundle verification.
`Verify(manifest, key)` re-canonicalizes the signed fields and checks the
signature — used by tests and the verify helper.

### At-rest encryption + expiry

`AuditExportArtifactProtector` wraps the `.zip` bytes with AES-GCM (12-byte
nonce ‖ ciphertext ‖ 16-byte tag), keyed from the Epic 29 cabinet, mirroring
`TenantSecretProtector` / `AesGcmConnectionStringDecryptor`. The ciphertext is
stored as a `bytea` on `audit_export_jobs` (or an object-store ref behind the
same interface). `ExpiresAt = generated_at + ArtifactTtl` (default 24h); a
periodic reaper (or the existing `QueuedTask` visibility reaper pattern) flips
expired jobs to `expired` and scrubs the ciphertext. The download route
decrypts on the fly and streams.

### `audit_export_jobs` (additive migration)

```sql
CREATE TABLE audit_export_jobs (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  scope         TEXT NOT NULL,                 -- 'tenant' | 'platform'
  tenant_id     UUID,                          -- set when scope='tenant'; NULL for platform
  requested_by  UUID NOT NULL,                 -- actor (generated_by)
  format        TEXT NOT NULL,                 -- 'json' | 'csv'
  filter        JSONB NOT NULL,                -- echoed 37-3 filter
  status        TEXT NOT NULL DEFAULT 'pending', -- pending|generating|ready|expired|failed
  record_count  BIGINT,
  manifest      JSONB,                         -- the signed manifest (for status/audit)
  artifact_ciphertext BYTEA,                   -- AES-GCM bundle; NULL until ready / after purge
  error         TEXT,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at    TIMESTAMPTZ,
  CONSTRAINT audit_export_scope_xor CHECK (
    (scope = 'tenant'   AND tenant_id IS NOT NULL) OR
    (scope = 'platform' AND tenant_id IS NULL)
  )
);
CREATE INDEX idx_audit_export_jobs_scope_tenant ON audit_export_jobs (scope, tenant_id, status);
CREATE INDEX idx_audit_export_jobs_expiry       ON audit_export_jobs (expires_at) WHERE status = 'ready';
```

> Additive table — run `dotnet ef migrations add` and confirm
> `has-pending-model-changes` reports none; mirror entity config in
> `TammaModelConfiguration.cs` (the single source for model config).

### Async via QueuedTask / TaskQueueProcessor

`AuditExportTaskHandler : ITaskHandler` with `TypePrefix = "audit.export."`.
The `QueuedTask.Payload` carries `{ jobId }`; the handler loads the
`AuditExportJob`, flips `generating`, runs query → serialize → digest → sign →
zip → encrypt → persist → `ready`, and is idempotent on retry (re-running a
`ready`/`generating` job is a no-op or regenerates safely). `QueuedTask.TenantId`
is set for tenant-scope so per-tenant routing/visibility holds; platform-scope
uses the platform task path (`PlatformTaskWorker`) per the provisioning
precedent. Failures count against the retry budget; terminal failure flips the
job to `failed`.

### Per-mode ownership (mandatory two-scoping-model answer)

| Question | single-user | SaaS |
|---|---|---|
| Who can export the **tenant** slice? | The sole user (no role gate beyond authn). | `tenant_owner`/`tenant_admin` of the route tenant; `member` → 403. |
| Who can export the **platform** slice? | The sole user (their instance's platform audit). | `PlatformOwnerAccess` only. |
| What rows are in a tenant export? | The user's records. | ONLY the route tenant's `audit_records` (read-model tenant scoping = defence in depth); never platform/other-tenant rows. |
| Where does the artifact live? | Encrypted, the user's job. | Encrypted, scope-namespaced job; cross-scope/cross-tenant fetch → 404. |
| Who can fetch/download the job? | The user. | The export's scope owner only; other scopes → 404 (no existence leak). |
| Mode source | `ITammaModeProvider` (process-stable). | same |

## Dependencies

- **Prerequisite — Story 37-1**: curated `audit_records` read-model + field-level
  redaction (export reads the already-redacted rows — AC9).
- **Prerequisite — Story 37-2**: hash chain + checkpoints (`head_hash`,
  `checkpoint_id`) bound into the manifest (AC2, AC6).
- **Prerequisite — Story 37-3**: the shared audit query/filter model reused by
  the export request body (AC1).
- **Prerequisite — Epic 29 (signing key)**: `ISecretStore` / `KekProvider`
  cabinet key for `export_signature` and at-rest encryption (AC3, AC5). Key
  rotation precedent: Story 28-12.
- **Reuses (no change required)**: `QueuedTask` + `TaskQueueProcessor` /
  `ITaskHandler` async pattern; `TenantSecretProtector` /
  `AesGcmConnectionStringDecryptor` AES-GCM helper; `RequireTenantMembershipFilter`
  + `OwnerAccess`/`PlatformOwnerAccess` policies; `IEventRepository`/audit append.

## Testing Strategy

Tests are TDD-first, xUnit under `apps/tamma-elsa/tests/Tamma.Api.Tests/`
(docker-bound suites run via `sg docker -c "dotnet test ..."`).

1. **Manifest signature round-trip**: build a manifest, sign with a fixture
   cabinet key, `Verify` succeeds; flip one byte of the data file (so
   `data_sha256` changes) → verify fails; flip one signed manifest field →
   verify fails; tamper with `export_signature` → verify fails.
2. **Chain reconstruction**: recompute the audit chain over the exported rows →
   matches the manifest `head_hash`/`checkpoint_id`; remove/insert one exported
   row → reconstruction mismatch (post-export tampering detected — AC6).
3. **Async job lifecycle**: `POST .../export` returns `202` + `jobId`; the task
   handler drives `pending → generating → ready`; `GET .../export/{id}` reflects
   each state and yields a `downloadUrl` only when `ready`; handler retry is
   idempotent; terminal failure → `failed` with non-leaky error (AC4, AC12).
4. **Sync fast path**: a sub-threshold export (< `SyncRowThreshold`) produces a
   downloadable bundle without queueing.
5. **Expiry**: after `ArtifactTtl`, the job reports `expired`, download → `410`,
   reaper scrubs ciphertext (AC5).
6. **CSV injection neutralization**: cells starting `= + - @`, tab, CR are
   neutralized; embedded `"`, `,`, newlines are RFC-4180-quoted; round-trips
   through a CSV parser to the safe value (AC8).
7. **Redaction preserved**: a record with a 37-1-redacted field exports redacted
   in BOTH JSON and CSV; the writer never un-redacts (AC9).
8. **`AUDIT.EXPORTED` emission**: initiating an export writes exactly one
   `AUDIT.EXPORTED` audit record tagging actor/scope/record_count/filter/format;
   the row sorts AFTER the exported head and is not in the exported slice (AC7).
9. **RBAC per mode**: SaaS matrix (member → 403 on tenant export; non-owner →
   403 on platform export; tenant export excludes platform/other-tenant rows;
   cross-scope/cross-tenant `GET {jobId}` → 404). single-user: sole user exports
   both slices (AC10, AC11).
10. **Encryption at rest**: persisted `artifact_ciphertext` is not the plaintext
    zip; decrypt-and-stream yields the original bundle; tampered ciphertext
    fails AES-GCM auth (AC5).
11. **Suite green + migration**: full suite stays green; the additive migration
    applies + rolls back cleanly; `has-pending-model-changes` → none.

## Estimated Effort

4-5 days.

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/IAuditExportService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditExportService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditExportManifest.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditExportSigner.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditCsvWriter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditJsonWriter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditExportTaskHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditExportArtifactProtector.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Audit/AuditExportOptions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/AuditExportServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AuditExportJob.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddAuditExportJobs.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` | Modify (tenant export + status + download) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` | Modify (platform export + status + download) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (AuditExportJob model config) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (DbSet) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (call `AddAuditExport()`; map routes) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditExportSignerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditExportServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditCsvWriterTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditExportEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Audit/AuditExportTaskHandlerTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes/bugs/findings/decisions (audit, signing,
   CSV-injection, AES-GCM at-rest).
3. Confirmed the public surfaces of 37-1/37-2/37-3 (read-model shape, chain
   head/checkpoint API, filter DTO) — this story consumes them.
4. Confirmed the Epic 29 cabinet key purpose/scope used for signing
   (`ISecretStore`/`KekProvider`); coordinate a dedicated signing purpose rather
   than reusing a KEK.
5. Planned TDD (Red-Green-Refactor) — signer + CSV + manifest are pure and
   should be tested first.

### Offline verification recipe (ships in the bundle README)

Given `audit.(json|csv)` + `manifest.json` + the public verification material:

1. Compute `sha256(audit.<format>)` → must equal `manifest.data_sha256`.
2. Re-canonicalize `manifest` minus `export_signature` (sorted keys, no
   whitespace, UTC ms timestamps) and verify `export_signature` with the key
   identified by `manifest.signing` (`alg` + `key_version`).
3. Recompute the audit hash chain over the exported rows in order and confirm
   the reconstructed head equals `manifest.chain.head_hash` (and the recorded
   `checkpoint_id`). Any mismatch = tampering or an incomplete slice.

A `tamma audit verify-export <bundle.zip>` helper performs all three and exits
non-zero on any failure.

### Safe CSV

Neutralize formula injection per OWASP CSV-injection guidance: prefix any field
beginning with `= + - @`, tab (`0x09`), or CR (`0x0D`) with a leading `'`; then
apply RFC-4180 quoting (wrap in `"…"` and double internal `"`) to every field
containing `" , \n \r`. Apply neutralization to the value, not just the display.

### Reuse, don't reinvent

- AES-GCM at rest: copy the nonce‖ciphertext‖tag layout from
  `TenantSecretProtector`; do not hand-roll a new scheme.
- Async: `QueuedTask` + `ITaskHandler` (TypePrefix `audit.export.`) +
  `TaskQueueProcessor`; platform-scope uses `PlatformTaskWorker` like
  provisioning.
- RBAC: `RequireTenantMembershipFilter` + admin-role check for tenant;
  `PlatformOwnerAccess` for platform — mirror `OrgEndpoints.ListTenantAudit`
  and the admin endpoints.

### Audit-of-the-audit ordering

Write `AUDIT.EXPORTED` AFTER capturing the chain head for the slice, so the
export record is never inside the slice it describes (avoids a chicken-and-egg
self-reference and keeps the recorded `head_hash` reproducible).

### Migration discipline

`audit_export_jobs` is additive: standard `dotnet ef migrations add`, then
verify `has-pending-model-changes` reports none and mirror config in
`TammaModelConfiguration.cs` only.

## Logging Requirements

- **INFO**: export requested (scope, format, sync|async, filter summary, actor),
  job state transitions (`generating`→`ready`/`failed`), reaper purged N expired
  artifacts, download served (jobId, scope).
- **DEBUG**: row count resolved, sync-vs-async decision, signing key_version
  selected, artifact byte size.
- **WARN**: download requested for `expired`/missing job (410/404),
  cross-scope/cross-tenant fetch rejected.
- **ERROR**: artifact generation failed (jobId, non-leaky reason), signing key
  unavailable, AES-GCM decrypt failure on download, migration/model mismatch.
- **Structured context**: `{ jobId, scope, tenantId?, format, recordCount,
  actorId }` where applicable.
- **Credential safety**: NEVER log the cabinet signing key, the AES-GCM key, the
  decrypted artifact bytes, or any un-redacted audit field. Signatures + hashes
  are safe to log; key MATERIAL is not.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
