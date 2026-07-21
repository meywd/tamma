# Implementation Plan — Story 39-11: Document Store & Lineage API

## Scope & Deliverable

When this story is done, every work-document instance is durably persisted per-tenant in a new `document_instances` table (envelope fields as columns, typed body as JSONB validated by the 39-2/39-3/39-4 registry before write), written exclusively through `IDocumentInstanceRepository` with immutable revisions and a supersession chain. Two tenant-scoped, `MemberAccess`-gated read endpoints expose the full lineage trail per issue (`GET /api/documents/issues/{issueId}/lineage`) and the latest-accepted-per-type state (`GET /api/documents/issues/{issueId}/latest` — also a repository method for 39-10's in-process re-entry), plus a single-document fetch with entity-level tenant re-check. The engine writes through a fail-loud engine→API seam mirroring the `POST /api/engine/events` drain, and every row carries the correlating `DOCUMENT.*` event id so store↔stream cross-checks are mechanical. Lineage DTOs live in `Tamma.Core` (named `IssueDocumentLineage` — distinct from 39-6's in-run `DocumentLineage`, which is what 39-8's `ESCALATION.TRIGGERED` payload embeds; the DTOs here back the read endpoints).

## Pre-Reading

- `docs/stories/epic-39/story-39-11/39-11-document-store-and-lineage-api.md` — the story (ACs are source of truth)
- `docs/stories/epic-39/README.md` — "Issues are anchors, not documents"; the lineage chain Issue → Findings → Decomposition → Plan → Reviews → outcome
- `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` — per-tenant context; where the `Documents` DbSet lands (story-referenced, exists)
- `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` — single source of entity config; `ConfigureAuditEntities` (~L1249) is the sectioned-method precedent, called from `ConfigureTenantEntities` (~L1898); `ApplyTenantFilter` seam; mentorship entities (~L2237) for explicit snake_case `HasColumnName`
- `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/20260619003624_AddAuditRecords.cs` — the house table-adding tenant migration shape (CreateTable + CHECKs + named indexes)
- `apps/tamma-elsa/src/Tamma.Data/Entities/AuditRecord.cs` — Story 37-1's "derived read-model, rebuildable, back-references the raw event" doc-comment posture this entity copies
- `apps/tamma-elsa/src/Tamma.Data/Repositories/ConventionRepository.cs` + `IConventionRepository.cs` — the tenant-scoped repository style (`ITenantDbContextFactory` + `ITenantContext`, `RequireTenantId`, explicit TenantId predicates)
- `apps/tamma-elsa/src/Tamma.Data/DependencyInjection.cs` (~L122–166) — repository DI registration home
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/ReposRunsEndpoints.cs` — fail-closed null-tenant guard (L61–67), entity-level `TenantId` re-check on bare-id lookup (L152–158) (story-referenced, exists)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` — `MemberAccess` policy (L1463), per-route mapping precedent (L2735–2741), `EngineServiceOnly` policy (L1552) + `engine.MapPost("/events", …)` (L2715)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs` — `AppendEvents`: the engine→API persist callback shape (TammaEvent→DomainEvent projection, tenant from `ITenantContext`)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` (~L518–562) — the engine-side HTTP client the persist seam extends
- `apps/tamma-elsa/src/Tamma.Activities/Core/TammaActivity.cs` — `TammaEvent.Id` is pre-minted at emission and becomes `DomainEvent.Id` (the AC7 linkage hook); `Core/EventPersistenceMiddleware.cs` — `context.GetService<T>()` resolution pattern
- `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` + `Repositories/IEventRepository.cs` — the stream side of the AC7 cross-check (`AppendAsync`, `QueryAsync`)
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Dashboard/ReposRunsEndpointsGuardTests.cs` — guard-test style to mirror (story-referenced, exists)
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/TenantAnalyticsIntegrationTests.cs` — the AC6 precedent: two tenant schemas in one Postgres 17 Testcontainer via `TenantNaming.SchemaName` + `EfTenantDbMigrator.MigrateTenantAppAsync` + a `SchemaRoutingFactory` test factory
- `docs/stories/epic-39/story-39-2/implementation-plan.md` — `DocumentEnvelope` field set, `DocumentTypeRegistry.Resolve`, `DocumentJson.Options`, `[Wire]`/`EnumWire` discipline
- `docs/stories/epic-39/story-39-6/implementation-plan.md` — `DocumentEvents.cs`, `EmitDocumentEventActivity`, lifecycle graph (the writer this store hooks into); note its `DocumentLineage` record (naming collision avoided here, D7)
- `docs/stories/epic-39/story-39-10/implementation-plan.md` — `LifecycleReEntryService` consumes `GetLatestAcceptedAsync` in-process (the AC4 lockstep signature)
- **All story-referenced repo paths exist.** NOT FOUND (planned by prerequisite stories, no code yet): `apps/tamma-elsa/src/Tamma.Core/Documents/` (39-2: `DocumentEnvelope`, `DocumentTypeRegistry`, `DocumentStateMachine`, `DocumentJson`), `Tamma.Core/Documents/Types/` (39-3/39-4 validators), `apps/tamma-elsa/src/Tamma.Activities/Documents/` (39-6: `DocumentEvents.cs`, `EmitDocumentEventActivity`), `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs` (39-6). See Dependencies & Sequencing.

## Design Decisions

- **D1 — Explicit snake_case column names, per the story's AC1 list.** AC1 names the columns (`document_type`, `issue_id`, …). The audit_records/prompt_overrides precedent uses EF's PascalCase defaults, but the mentorship entities prove the explicit `HasColumnName` style. Story text wins: every column gets `HasColumnName("…")` exactly as AC1 spells it. Consequence: raw-SQL seeds/tests quote lowercase names, no `"PascalCase"` quoting.
- **D2 — Envelope superset columns beyond AC1's minimum.** The story's own rule is "Envelope fields are **columns**", and 39-2's envelope carries `schemaVersion`, `correlationId`, `parentDocumentId`, and `producedBy.workflow` that AC1's list omits. Add `schema_version` (int), `correlation_id` (text), `parent_document_id` (uuid?), `produced_by_workflow` (text) — plus `correlating_event_id` (uuid?, the AC7 linkage). `parent_document_id` is load-bearing: it is how a Review's subject resolves in the lineage response (D8). `correlation_id` is how AC7's auditor pivots to `IEventRepository.ListByCorrelationIdAsync`. A superset satisfies "with envelope columns (…)"; dropping any listed column would not.
- **D3 — Store status closed set = the story's six + `rejected` (story-vs-canon tension, recorded).** AC's Architectural Context pins `draft | validated | in_review | accepted | superseded | escalated`, but 39-2's state machine, 39-6's `DOCUMENT.REJECTED`, and 39-8's AC7(b) all land documents in a `Rejected` terminal the store must be able to represent. Adding `rejected` is additive (no story-listed value is dropped), so the CHECK set is the seven wire strings. Mapping from 39-2's `DocumentState`: `Draft→draft`, `Validated→validated`, `Reviewed→in_review`, `Accepted→accepted`, `Rejected→rejected`, `Escalated→escalated`; `superseded` is store-only, set exclusively by the revision write (D4). Ship `DocumentInstanceStatus` as a `[Wire]` enum in `Tamma.Core/Documents/Store/` with a total `FromState(DocumentState)` map, count-pinned at 7. Flag the addition to the story owner in the PR description.
- **D4 — One write door; supersession is a branch of insert, not an update API.** `InsertAsync(tenantId, envelope, correlatingEventId)` is the only row-creating method: `envelope.SupersedesDocumentId == null` → `revision = 1`; non-null → load the prior row (tenant-checked), insert with `revision = prior.Revision + 1`, and set `prior.Status = superseded` in the same `SaveChangesAsync` transaction. `SetStatusAsync` transitions status only (never body, never to `superseded`). There is NO body-update method and NO delete method — immutability by API absence, matching the "no delete endpoint" technical note. A unique filtered index on `supersedes_document_id` keeps the chain linear (two rows can't supersede the same prior).
- **D5 — Write-time validation is the registry, fail-loud; read-time is a corruption tripwire.** `InsertAsync` calls `DocumentTypeRegistry.Resolve(envelope.Type).Validate(payload)` BEFORE persisting: violations → `TammaError DOCUMENT.STORE.INVALID_BODY` (violations in Context); an unknown/unregistered type key bubbles the registry's `DOCUMENT.TYPE.UNKNOWN`/`NOT_REGISTERED` — the store cannot contain a document it cannot validate. On read, the lineage assembler re-resolves the type and re-validates; a stored-invalid body (should be unreachable) throws `TammaError DOCUMENT.STORE.CORRUPT_BODY` rather than handing out an un-typed blob — the "typed edges" technical note enforced at both edges.
- **D6 — Engine writes ride a fail-loud engine→API hop; the pre-minted event id is the AC7 linkage.** The lifecycle runs in `Tamma.ElsaServer`, which registers neither `ITenantDbContextFactory` nor any repository (the `EventPersistenceMiddleware` header documents this); the sanctioned engine→store path is HTTP to `Tamma.Api` (`AppendEvents` precedent). So: `TammaApiClient` gains `PersistDocumentAsync`/`SetDocumentStatusAsync` posting to `POST /api/engine/documents` and `POST /api/engine/documents/{documentId}/status` (`EngineServiceOnly` policy), and a new `PersistDocumentInstanceActivity` resolves the client via `context.GetService<T>()`. Unlike the best-effort event drain, persist failures FAULT the activity (`TammaError DOCUMENT.STORE.PERSIST_FAILED`) — the document is the lifecycle's product, not telemetry. Linkage: the workflow pre-mints the transition event's `Guid` and passes it to BOTH the emit site (as `TammaEvent.Id` — `EmitDocumentEventActivity` gains an optional `EventId` input, a small lockstep MODIFY on 39-6's file) and the persist/status call → `correlating_event_id == domain_events."Id"`. AC7's "single operation flow" = adjacent persist+emit nodes per transition in the lifecycle graph.
- **D7 — Lineage DTOs in `Tamma.Core/Documents/Lineage/`; assembler in `Tamma.Api`.** The technical note wants the DTOs shared so 39-8 doesn't duplicate shapes → they live in Core with `[JsonPropertyName]` + `DocumentJson.Options` (39-2 D8 discipline). The root record is named `IssueDocumentLineage` — deliberately NOT `DocumentLineage`, which 39-6 already uses for the in-run drafts/reviews/rounds record; the two are different granularities and must not collide. The assembler (`LineageAssembler`) is a pure static in `Tamma.Api/Services/Documents/` because it maps `Tamma.Data` entities → Core DTOs and Core cannot reference Data.
- **D8 — Review linkage resolves parent-first, body-probe fallback, never dropped.** A Review instance's subject is its envelope `parent_document_id` (the writer — 39-6/39-7 — sets it to the reviewed document's id; lockstep note). Fallback for rows without it: a tolerant `JsonElement` probe of the body's subject reference (39-4's Review shape), isolated in one pure function. A review that resolves to no in-response subject lands in an `unlinkedReviews` list on the response — data is surfaced, not silently dropped.
- **D9 — RBAC: per-route `MemberAccess`, coexisting with 39-8's `/api/documents` group.** 39-8 maps `app.MapGroup("/api/documents")` with `AuthenticatedAny` for the decision-resume surface (its D10). This story's AC5 requires `MemberAccess` on the read endpoints — story wins for its own routes: map them individually with `.RequireAuthorization("MemberAccess")` (the `ReposRunsEndpoints` L2735-style mapping), independent of whether 39-8's group has landed. Fail-closed guard + entity re-check copied byte-for-byte in posture from `ReposRunsEndpoints`.
- **D10 — The 39-10 lockstep signature is `GetLatestAcceptedAsync(tenantId, issueId, ct)`.** 39-10's plan names this read; pin it here so both stories fake/consume one shape. It returns per-type latest accepted instances (superseded/draft/in_review/rejected/escalated rows never appear); "latest" = highest `revision` with `status = accepted` per `document_type` (the supersession write makes at most one non-superseded accepted row per chain — asserted in tests).
- **D11 — `issue_id` is a non-nullable string column.** 39-2's envelope `IssueId` is a string (the DCB tag convention), not a Guid — the column is `text NOT NULL` and documents without it are unrepresentable (technical note; enforced by the envelope's `required` member + a DB NOT NULL).

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Store/DocumentInstanceStatus.cs`** — `[Wire]` enum + extensions, copying the `AgentRole.cs` file shape (per 39-2's D1 namespace note):

   ```csharp
   namespace Tamma.Core.Documents;
   public enum DocumentInstanceStatus
   {
       [Wire("draft")] Draft, [Wire("validated")] Validated, [Wire("in_review")] InReview,
       [Wire("accepted")] Accepted, [Wire("rejected")] Rejected,
       [Wire("superseded")] Superseded, [Wire("escalated")] Escalated,
   }
   public static class DocumentInstanceStatusExtensions
   {
       public static string ToWire(this DocumentInstanceStatus s);
       public static DocumentInstanceStatus Parse(string input); // TammaError DOCUMENT.STORE.UNKNOWN_STATUS
       public static DocumentInstanceStatus FromState(DocumentState state); // total map, D3
   }
   ```

2. **CREATE `apps/tamma-elsa/src/Tamma.Core/Documents/Lineage/IssueDocumentLineage.cs`** (D7) — the shared response shapes (all wire properties `[JsonPropertyName]`d, serialized via `DocumentJson.Options`):

   ```csharp
   public sealed record LineageDocumentEntry(          // one instance (one revision)
       Guid Id, string DocumentType, string IssueId, string ProducedByRole, string ProducedByAction,
       int Revision, string Status, Guid? SupersedesDocumentId, Guid? ParentDocumentId,
       Guid? CorrelatingEventId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
       JsonElement Body, IReadOnlyList<LineageDocumentEntry> Reviews); // linked Review instances, D8
   public sealed record DocumentTypeTrail(string DocumentType, IReadOnlyList<LineageDocumentEntry> Revisions);
   public sealed record IssueDocumentLineage(
       string IssueId, IReadOnlyList<DocumentTypeTrail> Types,
       IReadOnlyList<LineageDocumentEntry> UnlinkedReviews,
       string Outcome);                                 // "accepted" | "escalated" | "in-progress"
   public sealed record LatestAcceptedDocuments(
       string IssueId, IReadOnlyList<LineageDocumentEntry> Documents); // ≤1 per type, AC4
   ```

3. **CREATE `apps/tamma-elsa/src/Tamma.Data/Entities/DocumentInstance.cs`** — plain entity, doc-comment in the `AuditRecord.cs` narrative style ("read-optimized product layer over the DCB stream; stream wins on disagreement; rebuildable via truncate + re-write from events"): `Id` (Guid, client-set from the envelope's UUID v7 — NO `gen_random_uuid()` default; the envelope id IS the row id), `DocumentType`, `IssueId`, `ProducedByRole`, `ProducedByAction`, `ProducedByWorkflow`, `SchemaVersion`, `CorrelationId`, `Revision`, `Status`, `SupersedesDocumentId`, `ParentDocumentId`, `CorrelatingEventId`, `TenantId` (Guid?, transitional predicate column), `BodyJson`, `CreatedAt`, `UpdatedAt`.

4. **MODIFY `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` and `TenantDbContext.cs`** — new sectioned method `ConfigureDocumentEntities(ModelBuilder, Guid? fixedTenantId)` (copy `ConfigureAuditEntities`'s shape), called at the end of `ConfigureTenantEntities` (beside the ~L1898 audit call — tenant model only; the CP context never sees this table): `ToTable("document_instances", t => t.HasCheckConstraint("ck_document_instances_status", "status IN ('draft','validated','in_review','accepted','rejected','superseded','escalated')"))`; every column `HasColumnName` snake_case (D1); `body jsonb` with `'{}'::jsonb` default; `issue_id`/`document_type`/`produced_by_*`/`status` `IsRequired()` with max lengths; indexes `IX_document_instances_issue_type_status (issue_id, document_type, status)` and `IX_document_instances_issue_created (issue_id, created_at)` (AC1); self-FK `HasOne().WithMany().HasForeignKey(SupersedesDocumentId).OnDelete(DeleteBehavior.Restrict)`; unique filtered index `UX_document_instances_supersedes` on `supersedes_document_id` `WHERE supersedes_document_id IS NOT NULL` (D4); `ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId)`. Add `public DbSet<DocumentInstance> Documents => Set<DocumentInstance>();` to `TenantDbContext`.

5. **CREATE the migration `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<stamp>_AddDocumentInstances.cs`** via `dotnet ef migrations add AddDocumentInstances --context TenantDbContext --output-dir Migrations/Tenant` (the `TenantDesignTimeDbContextFactory` serves design time; output matches the `AddAuditRecords` house shape). Verify `dotnet ef migrations has-pending-model-changes --context TenantDbContext` reports none (AC1).

6. **CREATE `apps/tamma-elsa/src/Tamma.Data/Repositories/IDocumentInstanceRepository.cs` + `DocumentInstanceRepository.cs`; MODIFY `apps/tamma-elsa/src/Tamma.Data/DependencyInjection.cs`** (one `AddScoped` line beside `IConventionRepository`). Constructor `(ITenantDbContextFactory, ITenantContext)`, `RequireTenantId()` + explicit `TenantId` predicates — the `ConventionRepository` style:

   ```csharp
   public interface IDocumentInstanceRepository
   {
       Task<DocumentInstance> InsertAsync(Guid tenantId, DocumentEnvelope envelope,
           Guid? correlatingEventId, CancellationToken ct);              // D4/D5 — the ONLY row creator
       Task<DocumentInstance> SetStatusAsync(Guid tenantId, Guid documentId,
           DocumentInstanceStatus status, Guid? correlatingEventId, CancellationToken ct); // never body/superseded
       Task<DocumentInstance?> GetByIdAsync(Guid tenantId, Guid documentId, CancellationToken ct);
       Task<IReadOnlyList<DocumentInstance>> ListByIssueAsync(Guid tenantId, string issueId, CancellationToken ct);
       Task<IReadOnlyList<DocumentInstance>> GetLatestAcceptedAsync(Guid tenantId, string issueId, CancellationToken ct); // D10, 39-10 lockstep
   }
   ```

   `InsertAsync` validates via `DocumentTypeRegistry` BEFORE `Add` (D5), maps envelope→row (`Status = DocumentInstanceStatusExtensions.FromState(envelope.State)`), and runs the supersession branch (D4) in one transaction. `SetStatusAsync` throws `TammaError DOCUMENT.STORE.ILLEGAL_STATUS` on `Superseded` and 404-style returns null-guarded `TammaError DOCUMENT.STORE.NOT_FOUND` on a missing/foreign row.

7. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Documents/LineageAssembler.cs`** (D7/D8) — pure static: `Assemble(string issueId, IReadOnlyList<DocumentInstance> rows) : IssueDocumentLineage` — groups by `DocumentType` in first-produced order, revisions ascending; attaches Review instances to their subject entry via `ParentDocumentId` then body probe (`ResolveReviewSubject(JsonElement body) : Guid?`); computes `Outcome` (`escalated` if any non-superseded row is `escalated`; `accepted` if every latest revision per type is `accepted`; else `in-progress`); re-validates bodies per D5. Also `AssembleLatest(...) : LatestAcceptedDocuments`.

8. **CREATE `apps/tamma-elsa/src/Tamma.Api/Endpoints/DocumentEndpoints.cs`; MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs`** — static handlers in the `ReposRunsEndpoints` shape, each opening with the verbatim fail-closed guard (`if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty) return Results.NotFound(new { error = "no_active_tenant" });`):
   - `GetIssueLineage(string issueId, IDocumentInstanceRepository, ITenantContext)` → `ListByIssueAsync` + `LineageAssembler.Assemble` (AC3; empty trail → 200 with empty `types`, not 404 — an issue with no documents is a valid state).
   - `GetLatestAccepted(string issueId, …)` → `GetLatestAcceptedAsync` + `AssembleLatest` (AC4).
   - `GetDocument(Guid documentId, …)` → `GetByIdAsync`, then the entity-level re-check `if (row is null || row.TenantId != tenantId) return Results.NotFound(new { error = "document_not_found" });` (AC5, `GetRunDetail` L152–158 posture).
   - Engine persist callbacks (D6): `PersistFromEngine(PersistDocumentRequest, IDocumentInstanceRepository, ITenantContext)` and `SetStatusFromEngine(...)` — deserialize the envelope via `DocumentJson`, call the repository, map `TammaError` → 400 with code (the engine retries/faults loudly).
   Program.cs mapping (beside L2735, D9): `app.MapGet("/api/documents/issues/{issueId}/lineage", DocumentEndpoints.GetIssueLineage).RequireAuthorization("MemberAccess");` + `/latest` + `/api/documents/{documentId:guid}`; engine group: `engine.MapPost("/documents", DocumentEndpoints.PersistFromEngine).RequireAuthorization("EngineServiceOnly");` + `engine.MapPost("/documents/{documentId:guid}/status", …)`.

9. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Documents/PersistDocumentInstanceActivity.cs`; MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs`** (D6) — client methods `PersistDocumentAsync(PersistDocumentRequest, ct)` / `SetDocumentStatusAsync(Guid documentId, string status, Guid? correlatingEventId, ct)` (fail-loud: non-2xx throws, unlike the swallowing `/api/engine/events` poster at ~L518). Activity inputs: `EnvelopeJson`, `CorrelatingEventId`, `TenantId`; resolves the client via `context.GetService<TammaApiClient>()`; missing service or failed persist → `TammaError DOCUMENT.STORE.PERSIST_FAILED`. **Lockstep MODIFY (if 39-6 has landed; hand-off note in its plan otherwise):** `EmitDocumentEventActivity` gains an optional `EventId` input mapped onto `TammaEvent.Id`; `DocumentLifecycleWorkflow` pre-mints the transition event Guid and wires persist+emit adjacently per transition (PRODUCED→`InsertAsync` draft; VALIDATED→`SetStatusAsync(validated)`; REVIEW_REQUESTED→`in_review`; revision→`InsertAsync` superseding; ACCEPTED/REJECTED/ESCALATED→terminal statuses). If 39-6 is not merged, ship a test-only `DocumentPersistHarnessWorkflow` in the step-10 fixture (the 39-8 `GateHarnessWorkflow` fallback pattern) so nothing here blocks.

10. **CREATE the test suites** per the Test Plan; finish with `dotnet ef migrations has-pending-model-changes` (clean) + `dotnet test`.

## Data & Migrations

- New tenant-schema table **`document_instances`** (columns per D1/D2: `id` uuid PK; `document_type` varchar(64) NOT NULL; `issue_id` text NOT NULL; `produced_by_role`/`produced_by_action` varchar(64) NOT NULL; `produced_by_workflow` varchar(128); `schema_version` int NOT NULL; `correlation_id` text; `revision` int NOT NULL; `status` varchar(16) NOT NULL + `ck_document_instances_status` CHECK (7 values, D3); `supersedes_document_id` uuid NULL + self-FK (Restrict) + unique filtered `UX_document_instances_supersedes`; `parent_document_id` uuid NULL; `correlating_event_id` uuid NULL; `tenant_id` uuid NULL; `body` jsonb NOT NULL default `'{}'::jsonb`; `created_at`/`updated_at` timestamptz).
- Indexes: `IX_document_instances_issue_type_status (issue_id, document_type, status)`, `IX_document_instances_issue_created (issue_id, created_at)` (AC1).
- Migration: `Migrations/Tenant/<stamp>_AddDocumentInstances` (EF-generated, `AddAuditRecords` shape). Tenant model only — no ControlPlane migration. `has-pending-model-changes` clean after (AC1).

## Events

- **Emits: none.** The store is a projection target; the `DOCUMENT.*` family is emitted by the lifecycle (39-6). The engine persist endpoints append no events.
- **Consumes (linkage only):** `DOCUMENT.PRODUCED.SUCCESS`, `DOCUMENT.VALIDATED.SUCCESS`, `DOCUMENT.REVIEW_REQUESTED`, `DOCUMENT.REVISION_STARTED`, `DOCUMENT.ACCEPTED`, `DOCUMENT.REJECTED`, `DOCUMENT.ESCALATED` (39-6's `DocumentEvents.cs` constants) — as `correlating_event_id` values stamped on rows and read back by the AC7 cross-check test. Per 39-8's split, `ESCALATION.*` stays the exception surface; lineage outcome derivation reads row statuses, never events.

## Test Plan

All NUnit + FluentAssertions (+ Moq fakes; Testcontainers Postgres 17 for the integration suites).

- **`DocumentInstanceStatusTests`** (`tests/Tamma.Core.Tests/Documents/`) — drift pins: exactly 7 members, exact wire strings (incl. `in_review`), `Parse` throws on unknowns, `FromState` is total over all 6 `DocumentState` members and never yields `Superseded`. **Covers the D3 vocabulary underpinning AC1/AC2/AC4.**
- **`LineageAssemblerTests`** (`tests/Tamma.Api.Tests/Documents/`, pure) — grouping/ordering (types in first-produced order, revisions ascending); reviews attach via `parent_document_id`; body-probe fallback; unresolvable review lands in `unlinkedReviews` (never dropped); outcome matrix (all-latest-accepted → `accepted`; any escalated → `escalated`; else `in-progress`); corrupt stored body → `TammaError DOCUMENT.STORE.CORRUPT_BODY`. **Covers AC3 (shape/ordering/linkage), AC8.**
- **`DocumentEndpointsGuardTests`** (`tests/Tamma.Api.Tests/Documents/`, `ReposRunsEndpointsGuardTests` style: direct handler calls, recording fake repository + fake `ITenantContext`) — null AND `Guid.Empty` tenant → `404 no_active_tenant` on all three read endpoints BEFORE any repository call (recorder asserts not called); `GetDocument` with a row whose `TenantId != tenantId` → `404 document_not_found`; happy-path projection field pins. **Covers AC5, the null-tenant clause of AC6, AC8.**
- **`DocumentInstanceRepositoryTests`** (`tests/Tamma.Api.Tests/Documents/`, Testcontainers, single schema via `EfTenantDbMigrator`) — invalid body (registered type, violating payload) → `DOCUMENT.STORE.INVALID_BODY`, nothing persisted; unregistered type key → registry error, nothing persisted; revision chain: insert r1 → superseding insert → r2 has `revision 2`, r1 flips `superseded`, r1's body unchanged; second superseding insert against r1 → unique-index violation (chain linearity); `SetStatusAsync` never touches body and rejects `Superseded`; `GetLatestAcceptedAsync` excludes draft/in_review/superseded/rejected/escalated rows and returns ≤1 per type; CHECK constraint rejects a junk status via raw SQL. **Covers AC2, AC4, AC8.**
- **`DocumentStoreIsolationTests`** (`tests/Tamma.Api.Tests/Documents/`, Testcontainers, the `TenantAnalyticsIntegrationTests` two-schema pattern: `TenantNaming.SchemaName`, per-schema `EfTenantDbMigrator.MigrateTenantAppAsync`, `SchemaRoutingFactory`) — seed documents for issue X in tenant A's schema and issue Y in B's; B's `ListByIssueAsync`/lineage for X → empty; B's `GetByIdAsync` with A's document id → null (endpoint → 404); A sees only A. **Covers AC6, AC8.**
- **`DocumentStoreStreamConsistencyTests`** (`tests/Tamma.Api.Tests/Documents/`, Testcontainers) — pre-mint an event Guid; `AppendAsync` a `DOCUMENT.ACCEPTED` `DomainEvent` with that `Id` (tags `issueId`/`documentType`) via `EventRepository`; insert + `SetStatusAsync(accepted, thatGuid)` the row; assert the row's `correlating_event_id` resolves to an existing `domain_events` row whose type is `DOCUMENT.ACCEPTED` and whose tags match the row's `issue_id`/`document_type`. If 39-6 has merged, add a variant driving `DocumentPersistHarnessWorkflow`/the lifecycle so persist+emit run in one flow. **Covers AC7.**
- **`DocumentEngineWriteSeamTests`** (`tests/Tamma.Api.Tests/Documents/` + `tests/Tamma.Activities.Tests/Documents/`) — `PersistFromEngine` maps the wire envelope onto `InsertAsync` and surfaces `TammaError` codes as 400; `PersistDocumentInstanceActivity` faults loudly (`DOCUMENT.STORE.PERSIST_FAILED`) on a failing fake client — never swallows. **Covers the D6 half of AC7, AC8.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — entity + config in `TammaModelConfiguration` + additive tenant migration, indexes, self-FK, clean snapshot | 3, 4, 5 | `DocumentInstanceRepositoryTests` (CHECK/unique/FK live), step-10 `has-pending-model-changes` run; reviewer checks config lives in `TammaModelConfiguration` only |
| 2 — repository sole writer; registry validation before write; immutable revisions + supersession | 6 (D4/D5) | `DocumentInstanceRepositoryTests` (invalid-body, unregistered-type, revision chain, no body mutation); reviewer: no other code touches the DbSet |
| 3 — lineage query ordered for rendering, reviews resolved, outcome terminal | 2, 7, 8 | `LineageAssemblerTests`, `DocumentEndpointsGuardTests` projection pins |
| 4 — latest-accepted endpoint + in-process repository method; superseded/drafts never appear | 6 (D10), 7, 8 | `DocumentInstanceRepositoryTests` (`GetLatestAcceptedAsync` filter matrix), guard-test projection |
| 5 — MemberAccess + fail-closed null-tenant + entity-level re-check on bare-id fetch | 8 (D9) | `DocumentEndpointsGuardTests` (guards before repo, 404 re-check); reviewer checks `.RequireAuthorization("MemberAccess")` on all three routes |
| 6 — cross-tenant isolation through every endpoint + repository read; null-tenant rejected pre-query | 6, 8 | `DocumentStoreIsolationTests` (two schemas), `DocumentEndpointsGuardTests` (null-tenant recorder) |
| 7 — write + `DOCUMENT.*` emit in one operation flow; row carries event linkage; cross-check test | 9 (D6), 3 (`correlating_event_id`) | `DocumentStoreStreamConsistencyTests`, `DocumentEngineWriteSeamTests` |
| 8 — NUnit unit + integration coverage of all the above | 10 | The six suites above, each tagged to its ACs |

## Dependencies & Sequencing

- **Hard prerequisites (compile-time):** 39-2 (`DocumentEnvelope`, `DocumentTypeRegistry`, `DocumentState`, `DocumentJson` — `Tamma.Core/Documents/` does not exist yet; do not start steps 1–2/6 before it compiles). 39-3 for at least the `Decomposition` type: the registry rejects unregistered keys (39-2 D3), so the write path is untestable with an empty registry — the story sanctions landing with batch-1 types only. `Tamma.Data → Tamma.Core` and `Tamma.Api → Tamma.Data/Core/Activities` references already exist (verified in csprojs).
- **Lockstep — 39-6 (the writer):** step 9's wiring edits its files; the `EventId` input on `EmitDocumentEventActivity` and the persist-node placement are the agreed seam. If 39-6 lands second, this story ships the activity/client/endpoints + harness workflow and 39-6 performs the wiring (mirror of 39-8's gate hand-off).
- **Lockstep — 39-10:** `GetLatestAcceptedAsync(Guid tenantId, string issueId, ct)` is the pinned in-process read (D10) its `LifecycleReEntryService` consumes — never HTTP. Coordinate any rename in both plans.
- **Stubbed, not pulled in:** 39-8 (its escalation payload embeds 39-6's `DocumentLineage`; the `IssueDocumentLineage` DTOs here back the read endpoints — nothing imported here); 39-21 (`DOCUMENT.ACCEPTED`-triggered indexing reads this store later); dashboards (consume the endpoints as-is).
- **In place, verified:** `TenantDbContext`/`TammaModelConfiguration`/`ApplyTenantFilter`, `Migrations/Tenant` + `EfTenantDbMigrator` + `TenantDesignTimeDbContextFactory`, `ITenantDbContextFactory`/`ITenantContext`, `MemberAccess` + `EngineServiceOnly` policies, `TammaApiClient`, `EventRepository`, both test precedents.
- **Feeds:** 39-10 (re-entry read), 39-8 (lineage payloads), 39-12..39-15 (every migrated workflow persists through this), 39-21 (RAG indexing), dashboards.

## Risks & Mitigations

- **Prerequisite stack (39-2/39-3/39-6) is plan-only today** — the schedule risk. Mitigation: steps 3–5 (entity/config/migration) and 7–8 depend only on 39-2's small model surface; every consumed name is pinned in the sibling plans (drift = mechanical rename); step 9 has the harness fallback.
- **Status-vocabulary tension (D3) surprises a reviewer.** Mitigation: the drift test pins the 7-member set with a comment citing this decision; the PR description flags the `rejected` addition against the story text for an explicit sign-off.
- **Engine→API write hop makes persist+emit non-atomic** (the emit drains best-effort, the persist is synchronous). Mitigation: the row carries the PRE-minted event id, and the drain's idempotent append (`ON CONFLICT (Id) DO NOTHING`) guarantees the referenced event eventually exists; the stream-wins doctrine (story context) plus `correlation_id` makes any gap auditable; the AC7 test pins the linkage mechanics.
- **Lineage response growth on long-lived issues** (all revisions, all types, bodies included). Mitigation: bodies ride jsonb straight through (no N+1); if size bites, a `?bodies=false` projection is an additive follow-up — the DTO shape already isolates `Body`.
- **`GetLatestAcceptedAsync` correctness depends on the supersession invariant** (one non-superseded accepted row per chain). Mitigation: the invariant is enforced by the single write door + unique filtered index (D4) and asserted directly in the repository tests.
- **Two `/api/documents` mapping owners (this story + 39-8).** Mitigation: per-route mapping (D9) is order-independent; whichever lands second folds its routes beside the other's with a cross-reference comment.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | `DocumentInstanceStatus` + lineage DTO records | 0.5 |
| 3–5 | Entity + `ConfigureDocumentEntities` + migration + snapshot check | 0.75 |
| 6 | Repository (validation, supersession transaction, reads) + DI | 1.0 |
| 7 | `LineageAssembler` (grouping, review linkage, outcome) | 0.5 |
| 8 | Endpoints + Program.cs mapping (public reads + engine writes) | 0.5 |
| 9 | `TammaApiClient` methods + persist activity + 39-6 lockstep wiring/harness | 0.5 |
| 10 | Six test suites incl. two-schema isolation + consistency Testcontainers runs | 1.0 |
| — | Lockstep coordination (39-6/39-10 signatures), review polish | 0.25 |
| **Total** | | **5.0** (story estimate: 4–5 days) |
