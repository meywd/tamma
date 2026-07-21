# Story 39-11: Document Store & Lineage API

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

As a **tenant member** (via the dashboard) and as the **lifecycle engine itself** (via 39-10 re-entry),
I want every work-document instance persisted per-tenant with its envelope and lineage, queryable as "the full document trail for issue X" and "the latest accepted state for issue X",
So that a human can read Issue → Findings → Decomposition → Plan → Reviews → outcome end-to-end, and a re-entering workflow can resume from what was already accepted instead of starting from scratch.

## Priority

P1 — The persistence leg of the lifecycle. Events alone (DCB) already carry the audit trail; this store is the **read-optimized document product layer** on top (the same events-vs-projection split as Story 37-1's `audit_records`). 39-10's re-entry and 39-8's lineage-attached escalations both read it, so it lands before or alongside the pilot migration (39-12).

## Architectural Context (READ FIRST)

This store follows the established Epic 28 schema-per-tenant + Epic 37 projection conventions — do not invent new persistence patterns.

- `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` — per-tenant schema context; **document instances are tenant data and live here** (a `Documents` DbSet), never in `ControlPlaneDbContext`.
- `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` — the single source of entity configuration (indexes, constraints, query filters); configure the new entity here, not in scattered `OnModelCreating` overrides.
- `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/` — additive EF migration home; `dotnet ef migrations has-pending-model-changes` must report none after.
- **Envelope comes from Story 39-2** (document core): `documentId` (UUID v7), `documentType`, `issueId` (the mandatory lineage anchor — the existing DCB tag convention formalized), `producedBy` (role/action cell), `revision`, `status` (`draft | validated | in_review | accepted | superseded | escalated`), `supersedesDocumentId`, timestamps. Envelope fields are **columns**; the typed document body is a **JSONB column** validated by the 39-3/39-4 type registry before write.
- **RBAC + fail-closed pattern to copy:** `apps/tamma-elsa/src/Tamma.Api/Endpoints/ReposRunsEndpoints.cs` — the Story 23-6 / #283 hardening: resolve the ambient tenant context and **fail closed** when `tc.TenantId is not Guid tenantId || tenantId == Guid.Empty` (line ~61-64), plus defence-in-depth entity-level `TenantId` equality checks on bare-id lookups (line ~152-155) so a guessed document id cross-tenant reads nothing. The `MemberAccess` policy is registered in `apps/tamma-elsa/src/Tamma.Api/Program.cs` (line ~1463).
- **Relationship to the DCB stream:** every lifecycle transition already emits `DOCUMENT.*` events (39-6). The store is written in the same operation flow but is conceptually a **rebuildable product layer** — if store and stream ever disagree, the stream wins (Story 37-1's "truncate + re-project" doctrine applies).
- **Single-user mode:** per the CLAUDE.md two-scoping-models rule, single-user deployments key rows by the sole user's context exactly as other tenant-scoped tables do in that mode — the endpoint shape is identical in both modes.

## Acceptance Criteria

1. **Entity + migration.** A `DocumentInstance` entity (`apps/tamma-elsa/src/Tamma.Data/Entities/DocumentInstance.cs`) is added to `TenantDbContext` with envelope columns (`id`, `document_type`, `issue_id`, `produced_by_role`, `produced_by_action`, `revision`, `status`, `supersedes_document_id`, `tenant_id`, `created_at`, `updated_at`) and a JSONB `body` column. Entity config (indexes on `(issue_id, document_type, status)` and `(issue_id, created_at)`; FK-style self-reference for supersession) lives in `TammaModelConfiguration.cs`; an additive migration lands under `Migrations/Tenant/` and `has-pending-model-changes` reports none.

2. **Typed write path.** A repository (`IDocumentInstanceRepository` / `DocumentInstanceRepository` under `apps/tamma-elsa/src/Tamma.Data/Repositories/`) is the only writer. Writes validate the body against the 39-2/39-3/39-4 document type registry **before** persisting (an instance whose body fails its type's schema/domain rules is rejected — the store cannot contain invalid documents). Revisions are immutable: a revise round inserts a new row with `revision+1` and marks the prior row `superseded` — no in-place body updates.

3. **Lineage query by issue.** `GET /api/documents/issues/{issueId}/lineage` returns the full document trail for the issue ordered for rendering: every document instance (all revisions) grouped by type, each with envelope + linked Review documents (a Review's subject reference resolves within the response), terminating in the current outcome (latest accepted set, or the escalated state). Shape matches the epic's lineage chain: Issue → Findings → Decomposition → Plan → Reviews → outcome.

4. **Latest-accepted-state query.** `GET /api/documents/issues/{issueId}/latest` returns, per document type, the single latest **accepted** instance (or absence) — exactly the read 39-10's re-entry consumes. Also exposed as a repository method for in-process callers (`LifecycleReEntryService` must not go through HTTP). Superseded and draft revisions never appear here.

5. **MemberAccess RBAC, fail-closed null-tenant.** All document endpoints require the `MemberAccess` policy and resolve the tenant from ambient tenant context, **failing closed** (403/404, zero rows — mirroring `ReposRunsEndpoints`) when the tenant context is null or `Guid.Empty`. Single-document fetch (`GET /api/documents/{documentId}`) re-checks entity-level tenant ownership after load (defence-in-depth against bare-id guessing).

6. **Cross-tenant isolation test.** Tenant A's documents are invisible to tenant B through every endpoint and repository read (schema separation + the tenant guard). A test seeds documents in two tenant schemas and asserts: B's lineage query for A's issue returns empty/404; B fetching A's `documentId` directly gets 404; the null-tenant request is rejected before any query executes.

7. **Store/stream consistency.** Writing a document instance and emitting its `DOCUMENT.*` event happen in the lifecycle's single operation flow, and each instance row carries the correlating event linkage (e.g. the acceptance event id on accepted rows) so an auditor can cross-check store ↔ stream. A test asserts an accepted document's row references an existing `DOCUMENT.*` event for the same `issueId`/`documentType`.

8. **Unit + integration tests (NUnit, test-first).** Coverage includes: invalid-body rejection (AC2), revision immutability/supersession chain (AC2), lineage ordering + Review linkage (AC3), latest-accepted filtering out drafts/superseded (AC4), RBAC fail-closed matrix (AC5), and the isolation suite (AC6) against Testcontainers Postgres.

## Technical Notes

- **JSONB body, typed edges.** Postgres holds the body as JSONB for queryability, but all reads/writes deserialize through the static C# document types — the API never hands out un-typed blobs. Do not add per-field body columns; the envelope carries every indexable dimension.
- **`issueId` is non-nullable.** Documents without an issue anchor are unrepresentable (epic principle: "Issues are anchors, not documents"). If a future producer genuinely has no issue, that is a design conversation, not a nullable column.
- **No delete endpoint.** Documents are immutable history; supersession is the only "removal." Retention/erasure concerns route through Epic 37 machinery, not through this API.
- **Escalation payloads (39-8)** serialize a slice of the lineage query response — keep the lineage DTOs in a shared location (e.g. `Tamma.Core` contracts) so 39-8 does not duplicate shapes.
- Endpoint file: `apps/tamma-elsa/src/Tamma.Api/Endpoints/DocumentEndpoints.cs`, mapped in `Program.cs` beside the other endpoint groups; guard tests mirror `tests/Tamma.Api.Tests/Dashboard/ReposRunsEndpointsGuardTests.cs`.

## Dependencies

- **Story 39-2 (document core/envelope)** — envelope shape + type registry used for write-time validation. Blocking.
- **Stories 39-3/39-4 (document types)** — the concrete validators behind AC2. Blocking for full coverage; the store can land with batch-1 types only.
- **Story 39-6 (lifecycle)** — the writer; wires store writes to lifecycle transitions.
- **Epic 28** — `TenantDbContext`, schema-per-tenant, tenant-context guard conventions (existing).
- **Consumed by:** 39-10 (latest-accepted re-entry read), 39-8 (lineage-attached escalations), 39-12..39-15 (every migrated workflow persists through it), 39-21 (accepted documents indexed into the tenant's RAG knowledge on `DOCUMENT.ACCEPTED`), dashboards (lineage rendering).

## Estimated Effort

4–5 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
