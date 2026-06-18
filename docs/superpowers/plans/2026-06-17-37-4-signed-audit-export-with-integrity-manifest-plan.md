# Story 37-4 — Signed Audit Export (JSON/CSV) with Integrity Manifest — Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Before any code, read
> [BEFORE_YOU_CODE.md](../../guides/BEFORE_YOU_CODE.md).

**Story:** `docs/stories/epic-37/story-37-4/37-4-signed-audit-export-with-integrity-manifest.md`
(Status: drafted). Epic 37 — Audit, Compliance & Data Governance.

**Goal:** Let admins export a time/filter-bounded slice of the tamper-evident audit trail
(`audit_records` read-model + hash chain from 37-1/37-2, queried with the 37-3 filter) in JSON and
CSV, packaged with an **integrity manifest** (record count, chain head hash + checkpoint ref, and a
cabinet-key signature) so a recipient can verify **offline** that the export was not altered and
corresponds to the chain. Large exports run **asynchronously** (`QueuedTask` + downloadable,
encrypted, auto-expiring artifact), mirroring tenant provisioning. The export is itself an audited
action (`AUDIT.EXPORTED`).

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`; the build itself needs no wrapper). **Target is the C# app only —
the deleted `packages/api` TypeScript API is NOT a target.**

---

## Non-goals (YAGNI guard)

- NO change to the audit read-model, redaction, or hash-chain semantics. 37-1 owns redaction, 37-2
  owns the chain, 37-3 owns the filter. This story READS their public surfaces and packages/signs —
  it never re-derives or un-redacts a field, and never recomputes the canonical chain definition.
- NO new signing/crypto primitive. Signing uses the **Epic 29 cabinet key** via
  `ISecretStore`/`KekProvider`; at-rest encryption reuses the AES-GCM layout from
  `TenantSecretProtector` / `AesGcmConnectionStringDecryptor`. Do not hand-roll a scheme.
- NO new async infrastructure. Reuse `QueuedTask` + `ITaskHandler` (`TypePrefix "audit.export."`) +
  `TaskQueueProcessor`; platform-scope rides `PlatformTaskWorker` like provisioning.
- NO object-store dependency in v1. The encrypted artifact lives as `bytea` on `audit_export_jobs`
  behind a tiny interface so swapping to S3/MinIO later is a one-class change.
- NO per-user export sharing / ACL beyond scope ownership. A job is owned by its scope; cross-scope
  fetch is a 404. No long-lived public links — download is time-limited + single-scope.
- NO streaming-zip-over-HTTP for the async path. Async builds the full bundle then serves it; the
  sync fast path may stream a small bundle inline.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists and is reused

| Capability | Where (verified) |
|---|---|
| Tenant audit read (DCB) + admin-role gate + tenant-scope defence-in-depth | `src/Tamma.Api/Endpoints/OrgEndpoints.cs` `ListTenantAudit` (~527); DTO `src/Tamma.Api/Dtos/Orgs/OrgDtos.cs` `AuditEventResponse` (~71) |
| RBAC policies | `Program.cs`: `OwnerAccess` (~971), `PlatformOwnerAccess` (~986); `RequireTenantMembershipFilter` registered (~352) |
| Async job queue | `src/Tamma.Data/Entities/QueuedTask.cs` (status pending/processing/completed/failed, TenantId, Payload jsonb, ClaimedAt reaper); `src/Tamma.Api/Services/TaskQueue/{ITaskHandler,TaskQueueProcessor,DbTaskQueue}.cs`; registration `Extensions/TaskQueueServiceCollectionExtensions.cs` (`AddTaskQueue`, DI-resolved `ITaskHandlerRegistry`, exact-then-longest-prefix match) |
| Platform-scope async | `src/Tamma.Api/Services/PlatformTasks/{PlatformTaskWorker,IPlatformTaskHandler}.cs` (provisioning precedent) |
| AES-GCM at rest | `src/Tamma.Api/Services/Provisioning/TenantSecretProtector.cs` (12-byte nonce ‖ ct ‖ 16-byte tag; key from cabinet/config); `src/Tamma.Api/Services/Secrets/AesGcmConnectionStringDecryptor.cs` |
| Epic 29 secret cabinet | `src/Tamma.Api/Services/Secrets/ISecretStore.cs`, `KekProvider.cs`, `SecretScope.cs`, `SecretPurpose.cs`; rotation precedent `KekRotationCoordinator.cs` / Story 28-12 |
| DCB / audit append | `src/Tamma.Data/Repositories/{IEventRepository,EventRepository}.cs` `AppendAsync` (tenant-required; platform via `IPlatformEventRepository`) |
| Crypto primitives in-repo | `System.Security.Cryptography` already used: `HMACSHA256` (`Tamma.Platforms/Webhooks/HmacWebhookSignatureVerifier.cs`, `OnboardingEndpoints.cs`), AES-GCM (above) |
| Model config single source | `src/Tamma.Data/TammaModelConfiguration.cs` + `ControlPlaneDbContext.cs`; migrations under `src/Tamma.Data/Migrations/ControlPlane/` |

### What this story DEPENDS on (not yet merged at plan time)

- **37-1** curated `audit_records` read-model + field-level redaction — the export reads
  already-redacted rows. **37-2** hash chain (`head_hash`) + checkpoints. **37-3** the shared filter
  DTO. The `Services/Audit/` directory does NOT exist yet (it is created by this story; 37-1..37-3
  add their own files alongside). Sequence 37-4 after 37-1/37-2/37-3 land; depend on their public
  types, not internals.
- **Epic 29** signing key — coordinate a dedicated `SecretPurpose` (e.g. `AuditExportSigning`)
  rather than reusing a KEK so rotation domains stay separate.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Export the **tenant** slice | sole user (authn only) | `tenant_owner`/`tenant_admin` of route tenant; member → 403 |
| Export the **platform** slice | sole user | `PlatformOwnerAccess` only |
| Rows in a tenant export | user's records | route tenant's `audit_records` ONLY (read-model scoping = defence in depth) |
| Fetch/download a job | the user | the export's scope owner only; other scope/tenant → 404 (no existence leak) |
| Mode source | `ITammaModeProvider` (process-stable) | same |

---

## Architecture

**query (37-3 filter) → serialize (redacted rows, JSON/CSV) → digest (`data_sha256`) → manifest
(chain head + checkpoint + counts) → sign (cabinet key) → zip → encrypt at rest (AES-GCM) →
persist job → ready → time-limited encrypted download.** Large slices run on the `QueuedTask`
handler; small slices may build inline. Initiation emits `AUDIT.EXPORTED`.

Key seams (all NEW, under `src/Tamma.Api/Services/Audit/` unless noted):

1. **`IAuditExportService` / `AuditExportService`** — orchestrator. `RequestExportAsync(scope,
   tenantId?, format, filter, actor)` → resolves row count, decides sync vs async, persists an
   `AuditExportJob`, emits `AUDIT.EXPORTED`, returns `{ jobId, status }`. `BuildBundleAsync(jobId)`
   is the pure-ish build path called by the sync path AND the task handler.
2. **`AuditJsonWriter` / `AuditCsvWriter`** — deterministic serializers over the redacted
   read-model rows. CSV neutralizes formula injection (`= + - @` / tab / CR) + RFC-4180 quoting.
3. **`AuditExportManifest` + `AuditExportSigner`** — manifest record, canonical (sorted-key,
   no-whitespace, UTC-ms) serialization of signed fields incl. `data_sha256` + `chain.head_hash` +
   `checkpoint_id`; sign/verify via the Epic 29 cabinet key (`ISecretStore`/`KekProvider`),
   recording `alg` + `key_version`.
4. **`AuditExportArtifactProtector`** — AES-GCM wrap/unwrap of the `.zip`, mirroring
   `TenantSecretProtector`.
5. **`AuditExportTaskHandler : ITaskHandler`** (`TypePrefix "audit.export."`) — drives a queued job
   `generating → ready`/`failed`, idempotent on retry.
6. **`AuditExportJob` entity + additive migration** — job + artifact metadata + ciphertext +
   expiry, scope XOR check.
7. **Endpoints** on `OrgEndpoints.cs` (tenant) + `AdminEndpoints.cs` (platform): export / status /
   download, scope-namespaced jobIds, cross-scope → 404.
8. **`AddAuditExport()` extension** wired once from `Program.cs`; routes mapped in `Program.cs`.

---

## Task breakdown (TDD — tests first in every task)

### T1: `AuditExportJob` entity + additive migration + model config

**Scope:** New entity, DbSet, `TammaModelConfiguration` mapping, additive EF migration. No service
logic yet.

- [ ] Write `AuditExportJobModelTests` (CHECK enforced: scope XOR tenant_id; status enum;
      round-trip insert/read; expiry index present).
- [ ] `src/Tamma.Data/Entities/AuditExportJob.cs` (fields per story migration: scope, tenant_id,
      requested_by, format, filter jsonb, status, record_count, manifest jsonb,
      artifact_ciphertext bytea, error, timestamps, expires_at).
- [ ] DbSet on `ControlPlaneDbContext.cs`; config in `TammaModelConfiguration.cs` (CHECK constraints,
      indexes). `dotnet ef migrations add AddAuditExportJobs` under
      `src/Tamma.Data/Migrations/ControlPlane/`; verify `has-pending-model-changes` → none.

**AC covered:** 4 (job model), 5 (expiry column), 10/11 (scope XOR for isolation).
**Done when:** migration applies + rolls back; full suite green.

### T2: `AuditCsvWriter` + `AuditJsonWriter` (pure serializers)

**Scope:** Deterministic serialization of redacted read-model rows; CSV safety.

- [ ] `AuditCsvWriterTests`: formula-injection neutralization for `= + - @`, tab, CR; RFC-4180
      quoting of `" , \n \r`; round-trip through a CSV parser yields the safe value; redacted field
      stays redacted; stable column order.
- [ ] `AuditJsonWriterTests`: deterministic JSON array of redacted rows; stable key order; redacted
      field stays redacted; UTC-ms timestamps.
- [ ] Implement both writers (stream-friendly: write to a `Stream`/`TextWriter`).

**AC covered:** 8 (CSV injection), 9 (redaction preserved both formats).
**Done when:** writer tests green; no un-redaction path exists.

### T3: `AuditExportManifest` + `AuditExportSigner` (canonical sign/verify)

**Scope:** Manifest model, canonical serialization, sign/verify against the Epic 29 cabinet key.

- [ ] `AuditExportSignerTests`: sign→verify round-trip with a fixture cabinet key; flip a signed
      manifest field → verify fails; flip `data_sha256` (data tampered) → verify fails; tamper
      `export_signature` → verify fails; wrong `key_version` → verify fails; canonical form is
      byte-stable across runs (sorted keys, no whitespace, UTC ms).
- [ ] `AuditExportManifest.cs` (record + signed-field set incl. `chain.head_hash`,
      `checkpoint_id`, `data_sha256`, counts, scope, filter, generated_*); canonical serializer.
- [ ] `AuditExportSigner.cs` resolving the signing key via `ISecretStore`/`KekProvider`
      (dedicated `SecretPurpose`); `Sign(manifest)` + `Verify(manifest, key)`; record `alg` +
      `key_version`. Prefer asymmetric (ECDSA) if a cabinet asymmetric key is available (offline
      verify without sharing a secret); HMAC-SHA-256 is the minimum bar.

**AC covered:** 2 (manifest contents), 3 (signature over canonical manifest), 6 (offline verify of
signature + digest).
**Done when:** signer tests green; canonical form documented (matches the bundle verify recipe).

### T4: `AuditExportArtifactProtector` (AES-GCM at rest)

**Scope:** Encrypt/decrypt the `.zip` bytes, mirroring `TenantSecretProtector`.

- [ ] `AuditExportArtifactProtectorTests`: encrypt→decrypt round-trip; ciphertext ≠ plaintext;
      single-byte flip on ciphertext fails AES-GCM auth; nonce uniqueness across two encrypts.
- [ ] `AuditExportArtifactProtector.cs` (12-byte nonce ‖ ct ‖ 16-byte tag; key from Epic 29
      cabinet).

**AC covered:** 5 (encrypted at rest).
**Done when:** protector tests green; reuses the existing AES-GCM layout (no new scheme).

### T5: `AuditExportService` — build path + sync/async decision + `AUDIT.EXPORTED`

**Scope:** Orchestrate query → serialize → digest → manifest → sign → zip → encrypt → persist;
decide sync vs async on `record_count` vs `SyncRowThreshold`; emit `AUDIT.EXPORTED`.

- [ ] `AuditExportServiceTests`: sub-threshold → built inline, job `ready` immediately; over-
      threshold → job `pending` + a `QueuedTask` enqueued (type `audit.export.{scope}`, payload
      `{ jobId }`); `AUDIT.EXPORTED` written exactly once (tags actor/scope/record_count/filter/
      format) and sorts AFTER the exported chain head (not in the slice); manifest binds the chain
      head/checkpoint captured for the slice; build failure → job `failed` + non-leaky error +
      partial artifact purged.
- [ ] `IAuditExportService.cs` / `AuditExportService.cs`; `AuditExportOptions.cs`
      (`SyncRowThreshold` default 10_000, `ArtifactTtl` default 24h, `MaxRows`). Read the 37-3
      filter + 37-1 redacted rows + 37-2 chain head/checkpoint via their public surfaces.

**AC covered:** 1 (sync vs async decision), 2/3 (manifest + signature), 6 (chain binding), 7
(`AUDIT.EXPORTED`), 12 (graceful failure).
**Done when:** service tests green; `AUDIT.EXPORTED` ordering pinned by test.

### T6: `AuditExportTaskHandler` (async generation)

**Scope:** `ITaskHandler` for `audit.export.` driving the queued build idempotently.

- [ ] `AuditExportTaskHandlerTests`: `TypePrefix` resolves via `DiTaskHandlerRegistry`; happy path
      `pending → generating → ready`; retry on a `generating`/`ready` job is a safe no-op/regen;
      exception counts against retry budget and terminal failure flips job `failed`; tenant-scope
      task carries `QueuedTask.TenantId`.
- [ ] `AuditExportTaskHandler.cs` (loads `AuditExportJob` by payload `jobId`; calls
      `AuditExportService.BuildBundleAsync`). Platform-scope rides `PlatformTaskWorker` per the
      provisioning precedent (or the unified queue if scope is encoded in `TenantId` null).

**AC covered:** 4 (async lifecycle), 12 (failure path).
**Done when:** handler tests green; idempotent retry proven.

### T7: Endpoints (tenant + platform) — export / status / download + RBAC

**Scope:** Wire the HTTP surface with per-mode RBAC + scope isolation.

- [ ] `AuditExportEndpointsTests`:
  - `POST .../export` returns `202` + `jobId` (tenant + platform); body reuses the 37-3 filter +
    `format`.
  - RBAC: SaaS member → 403 on tenant export; non-platform-owner → 403 on platform export; tenant
    export rows exclude platform/other-tenant (defence in depth); single-user sole user → both
    slices.
  - `GET .../export/{id}` reflects state; `downloadUrl` only when `ready`.
  - download: `ready` → `200 application/zip`; `expired` → `410`; cross-scope/cross-tenant `{jobId}`
    → `404` (no existence leak); tampered ciphertext → ERROR-logged failure.
- [ ] Add handlers to `OrgEndpoints.cs` (mirror `ListTenantAudit` membership+admin gate) and
      `AdminEndpoints.cs` (`PlatformOwnerAccess`); scope-namespaced job resolution.

**AC covered:** 1, 4, 5 (410 on expiry), 10, 11.
**Done when:** endpoint tests green; RBAC matrix + isolation pinned.

### T8: Expiry reaper + DI wiring + route mapping

**Scope:** Auto-expire artifacts; wire everything once from `Program.cs`.

- [ ] Reaper test: a `ready` job past `expires_at` flips to `expired` and `artifact_ciphertext` is
      scrubbed; download then 410. (Reuse the `QueuedTask` ClaimedAt-reaper pattern, or a small
      hosted service / periodic sweep.)
- [ ] `AuditExportServiceCollectionExtensions.cs` `AddAuditExport()` (TryAdd* idempotent; registers
      service, signer, protector, options, task handler, reaper). Call once from `Program.cs`; map
      the new routes there.

**AC covered:** 5 (auto-expiry), wiring for 1/4.
**Done when:** reaper test green; `AddAuditExport()` called once; routes reachable.

### T9: Offline verify helper + bundle README

**Scope:** Make the "verifiable offline" claim concrete + executable.

- [ ] Verify-helper test: a valid bundle verifies (digest + signature + chain reconstruction over
      exported rows == `head_hash`/`checkpoint_id`); remove/insert one exported row → chain
      reconstruction mismatch; flip the data file → digest mismatch; flip the manifest → signature
      mismatch.
- [ ] `tamma audit verify-export <bundle.zip>` helper (or a documented standalone script) running
      the three-step recipe and exiting non-zero on any failure; include a `README` in the bundle
      describing the recipe.

**AC covered:** 6 (offline verifiability — full three-step), reinforces 2/3.
**Done when:** verify-helper test green; recipe matches the manifest canonical form from T3.

---

## Task order & dependencies

T1 (entity/migration) → T2 (writers) ‖ T3 (manifest/signer) ‖ T4 (protector) — T2/T3/T4 are pure
and parallel-safe after T1 — → T5 (service ties T2-T4 + 37-1/37-2/37-3 reads) → T6 (task handler) →
T7 (endpoints) → T8 (reaper + wiring) → T9 (verify helper). T5 is the integration crux and the only
task that needs all three dependency stories merged.

## Risks

- **Dependency story drift (37-1/37-2/37-3):** their read-model/chain/filter surfaces are consumed
  here. Pin to their public types; sequence 37-4 after they merge. If a surface is still in flux,
  stub the read behind a thin port (`IAuditReadModel`) so T2-T4 proceed against a fixture.
- **Canonical-form divergence:** the signer's canonical serialization MUST match the offline verify
  helper byte-for-byte, or every verify fails. T3 and T9 share one canonicalizer — do NOT duplicate
  the logic; expose it as a single internal method and test both against the same fixture.
- **Key rotation breaks old bundles:** record `key_version` in the manifest (Story 28-12 rotation
  precedent) and have `Verify` select the right cabinet key version; test verify against a rotated
  cabinet.
- **CSV injection regression:** neutralization must apply to the VALUE (not display) and survive a
  real parser round-trip; the OWASP cases (`= + - @`, tab, CR) are table-tested.
- **`AUDIT.EXPORTED` self-reference:** writing the export record BEFORE capturing the slice head
  would put the export inside its own slice and make `head_hash` unreproducible. Capture head first,
  then write `AUDIT.EXPORTED` — pinned by a T5 ordering test.
- **Artifact storage bloat:** `bytea` artifacts + expiry reaper keep the table bounded; cap with
  `MaxRows` and `ArtifactTtl`. The protector interface allows an object-store backend later with no
  caller change.
- **Migration discipline:** `audit_export_jobs` is additive — standard `dotnet ef migrations add`,
  confirm `has-pending-model-changes` → none, mirror config in `TammaModelConfiguration.cs` only.
- **Async on the shared processor:** the build path runs on `TaskQueueProcessor` /
  `PlatformTaskWorker` threads (same as provisioning's long polls) — keep the build cancellable and
  chunk large row reads so one big export does not starve other queued tasks.
