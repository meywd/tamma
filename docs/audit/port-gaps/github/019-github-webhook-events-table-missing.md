# Finding 019: Schema — `github_webhook_events` idempotency table does not exist

**Scope**: github
**Severity**: P2 (correctness/observability)
**Status**: Not-yet-implemented (stub) — companion to Finding 003 from the schema angle
**Estimated port effort**: 1h (on top of Finding 003's endpoint work)

## 1. What's in TS

Pre-delete snapshot: not present.

- File: No TS file defined a `github_webhook_events` table or a webhook-deliveries persistence module. The audit summary confirms: "Not found in audited files — may have lived outside scope or was planned."
- Contract/behavior: TS did not have a webhook idempotency table. Deliveries were processed immediately; duplicates were not filtered.
- Dependencies: none.
- Tests that exercised this: none.

This finding exists because the **schema-level artifact** — a table, an entity, a migration — is necessary to resolve Finding 003 (endpoint-level idempotency). Finding 003 describes the behavior; this finding describes the schema needed to support it.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: No entity exists. Grep for `GitHubWebhookDelivery`, `GitHubWebhookEvent`, `WebhookDelivery` → zero hits across:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/*.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/*.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/*.cs`
- Contract/behavior: No table. `TammaDbContext.DbSet<>` does not reference any such entity. No migration adds such a table.
- Dependencies: none.
- Tests: none.

## 3. The gap

- TS did: nothing (same state).
- C# does: nothing.
- For a caller (GitHub) redelivering the same webhook with the same `X-GitHub-Delivery` header, both TS and C# re-process. But because the C# task queue is **durable** (Postgres-backed), the downstream effect in C# is more damaging than in TS's in-memory queue: duplicates survive restarts, occupy task-queue slots, and each runs its full handler (task queue handlers are not idempotent by default).
- In production with existing data / deployed clients, this means: see Finding 003's impact. This finding is specifically about the schema required to fix that. Without the table, there's nowhere to record "we saw delivery X at time Y" and nothing to check against on redelivery.

Error paths: see Finding 003.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: none — idempotency is implicit.
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap

## 5. Status

- **Classification**: Not-yet-implemented (stub). Green-field schema add.
- **What's needed to finish**:
  1. Add entity `Tamma.Data.Entities.GitHubWebhookDelivery`:
     - `DeliveryId string PK` (UUID from GitHub, stored as text because GitHub's UUID format matches the default .NET `Guid` parse shape — using `Guid` is acceptable too).
     - `ReceivedAt DateTime NOT NULL` (UTC).
     - `EventType string NOT NULL` (e.g. `installation`, `issues`).
     - `Action string NULL` (e.g. `created`, `opened`).
     - `InstallationId long NULL` (the target installation if extractable from payload).
     - `ProcessedAt DateTime? NULL` (set after successful dispatch; nullable for in-flight rows).
  2. Index: `(ReceivedAt)` for TTL cleanup; `(InstallationId, ReceivedAt)` for per-installation forensics.
  3. Constraint: `PK (DeliveryId)` ensures duplicates cannot be inserted even if application logic has a race window — `ON CONFLICT DO NOTHING` semantics at insert time via `EF Core`'s `HasNoDefaultValueSql` + explicit `db.GitHubWebhookDeliveries.Add(...)` wrapped in try/catch `DbUpdateException`, OR an explicit upsert.
  4. EF Core migration: `20260417_AddGitHubWebhookDeliveries.cs`.
  5. Repository interface: `IGitHubWebhookDeliveryRepository` with `TryInsertAsync(...)` returning bool (true = new, false = duplicate) and `CleanupOlderThanAsync(DateTime cutoff)`.
  6. Optional: a `BackgroundService` or Elsa workflow that periodically deletes rows older than 30 days.
- **Is it "just a stub" or is scope missing?** Stub. Simple greenfield addition.
- **Blockers**: coordinates with Finding 003 (endpoint logic). Should be landed together.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` — add `DbSet<GitHubWebhookDelivery>`; configure in `OnModelCreating`.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubWebhookDelivery.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IGitHubWebhookDeliveryRepository.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/GitHubWebhookDeliveryRepository.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/202604XX_AddGitHubWebhookDeliveries.cs`
  - Optional: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/WebhookDeliveryCleanupService.cs`
- Tests to add:
  - `GitHubWebhookDeliveryRepositoryTests.TryInsert_New_ReturnsTrue`
  - `GitHubWebhookDeliveryRepositoryTests.TryInsert_Duplicate_ReturnsFalse`
  - `GitHubWebhookDeliveryRepositoryTests.Cleanup_RemovesOldRows_PreservesRecent`
  - `GitHubWebhookDeliveryRepositoryTests.Cleanup_Idempotent` — calling twice doesn't double-delete.
- Estimated effort: 1h (excluding Finding 003's endpoint wiring) broken down as:
  - Entity + migration: 0.3h
  - Repository impl: 0.3h
  - Tests: 0.4h

## References

- TS source: none (gap existed pre-port)
- C# source: no entity
- Archived SQL migration: none — this is a new table.
- Story: spec gap — same as Finding 003.
- Related findings: `003-webhook-idempotency-missing.md` (the behavior that needs this table)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `6dead62`
- **Notes**: Added `GitHubWebhookDelivery` entity (PK `DeliveryId uuid`, columns `ReceivedAt timestamptz`, `EventType varchar(100)`, `Action varchar(100)?`, `InstallationId bigint?`), `IGitHubWebhookDeliveryRepository` + EF impl with `TryRecordAsync` (returns true on first insert, false on conflict — handles the race via `DbUpdateException` catch) and `CleanupOlderThanAsync(cutoff)`. Migration `GitHubWebhookDeliveries` creates the table with indexes on `(ReceivedAt)` and `(InstallationId, ReceivedAt)`. Background cleanup hosted-service intentionally deferred (finding 003 notes manual SQL pruning is sufficient today; rows are <1KB and growth is bounded by GitHub's ~8h retry window).
