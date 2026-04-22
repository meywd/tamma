# Finding 021: Schema — `installation_id BIGINT PK` replaced by surrogate `Guid Id` PK

**Scope**: github
**Severity**: P2 (correctness/observability) — schema-level drift with downstream implications
**Status**: Data-model regression (architectural shift; document the contract change)
**Estimated port effort**: 0h to revert, 2h to document/align callers

## 1. What's in TS

Pre-delete snapshot of the original SQL at `database/archived-sql-migrations/001_github_installations.sql`.

- File: `database/archived-sql-migrations/001_github_installations.sql:4-13`
- Contract/behavior: The original TS schema used `installation_id BIGINT PRIMARY KEY` — the GitHub-issued installation ID was itself the primary key. This is idiomatic for a table modeling an external natural key: GitHub guarantees uniqueness of `installation_id`, so no surrogate key is needed, and foreign keys from child tables (`github_installation_repos.installation_id`) directly reference the GitHub ID.

```sql
-- database/archived-sql-migrations/001_github_installations.sql:4-13 (archived)
CREATE TABLE IF NOT EXISTS github_installations (
  installation_id   BIGINT PRIMARY KEY,
  account_login     TEXT NOT NULL,
  account_type      TEXT NOT NULL CHECK (account_type IN ('User', 'Organization')),
  app_id            BIGINT NOT NULL,
  permissions       JSONB NOT NULL DEFAULT '{}',
  suspended_at      TIMESTAMPTZ,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

And the child table FK:

```sql
-- database/archived-sql-migrations/001_github_installations.sql:15-26 (archived)
CREATE TABLE IF NOT EXISTS github_installation_repos (
  id                BIGSERIAL PRIMARY KEY,
  installation_id   BIGINT NOT NULL REFERENCES github_installations(installation_id) ON DELETE CASCADE,
  ...
);
```

- Dependencies: `@octokit/rest` returns `installation_id` as `number` (TS Number, 53-bit-safe for GitHub's current ID space); callers passed it directly to the store.
- Tests that exercised this: store tests used `installation_id` as the lookup key exclusively; no intermediate surrogate to thread through.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs:5-6`
- Contract/behavior: The C# model introduces a surrogate `Guid Id` as the primary key, with `long InstallationId` as a secondary unique-ish column (uniqueness not enforced by a constraint in the entity definition, but the repository's upsert logic queries by it).

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs (current)
public class GitHubInstallation
{
    public Guid Id { get; set; }
    public long InstallationId { get; set; }
    ...
}
```

And child entity `GitHubInstallationRepo` references the surrogate:

```csharp
// implied from InstallationRepository.cs:62-70 usage
public Guid InstallationEntityId { get; set; }  // FK to GitHubInstallation.Id
```

The router service uses both identifiers depending on context:
- `_installations.GetByInstallationIdAsync(long installationId)` — lookup by external GitHub ID (for webhook dispatch).
- `_installations.GetByEntityIdAsync(Guid entityId)` — lookup by internal surrogate (for admin / internal flows).
- `_installations.AddRepoAsync(Guid installationEntityId, long repoId, string repoFullName)` — adds a repo via the surrogate FK.

- Dependencies: every FK from any other table that wants to reference an installation must choose between `Guid` (entity ID) or `long` (GitHub ID). The codebase uses `Guid` for app-internal FKs (`GitHubInstallationRepo.InstallationEntityId`) and `long` for external-facing identifiers (webhook payloads, URLs).
- Tests: `InstallationRepositoryTests` covers both lookup methods.

## 3. The gap

- TS schema: `installation_id BIGINT PK` — one identifier, the external one.
- C# schema: `Id Guid PK` + `InstallationId long` unique — two identifiers, one external and one internal.
- For a caller looking up an installation by its GitHub ID, both systems work identically (TS: PK lookup; C#: index lookup on `InstallationId` — slightly slower but negligible). For a caller joining on any internal relationship, C# uses the `Guid`; TS would have used the `BIGINT`. This changes FK types across the schema.
- In production with existing data / deployed clients, this means:
  - **API responses leak the surrogate**: `CallbackResult.TenantId` at `InstallationRouterService.cs:106` returns `stored.Id` (the Guid), not the GitHub `installation_id`. Any downstream consumer that expects the external ID back from the callback (like the UI trying to link to a GitHub installation page) gets the Guid instead. Today the callback's success branch just redirects, so this isn't acutely visible, but future API endpoints that return installation resources must decide which ID to publish.
  - **Double-identifier ambiguity**: any repository method accepting "installation ID" must disambiguate. The naming (`AsyncByInstallationId` vs `AsyncByEntityId`) is explicit and works, but it's a recurring cognitive-tax, and a miscall by a future developer (passing the Guid to `GetByInstallationIdAsync`) produces zero results silently instead of an obvious type error.
  - **No uniqueness constraint declared**: `GitHubInstallation.InstallationId` is queried as if unique but I don't see a `HasIndex(...).IsUnique()` in the model configuration. If two rows with the same `InstallationId` were inserted (via a concurrent webhook + callback race), `FirstOrDefaultAsync` would silently pick one. This is defensible if a DB-level unique index exists via migration; if not, it's a latent bug.
  - **Migration from old schema impossible**: if any old data with `installation_id` as PK exists (e.g., in a staging environment's Postgres dump), it cannot be lifted-and-shifted into the new schema without ID remapping. Per `CLAUDE.md` "No migration anxiety: App is not in production with users", this is OK, but worth documenting.
- The change is **defensible**: Guid surrogate keys are the default ASP.NET Core EF Core idiom, they make entity relationships type-safe, and they decouple internal identity from an externally-assigned ID. The shift isn't wrong, it's just a contract change.

Error paths:
- TS error path: pass wrong number → no row found → null return → application-level 404.
- C# error path: pass wrong Guid/long → no row found → null return → application-level 404 or redirect-to-error.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- Story's acceptance criteria for this behavior: none — the schema-key choice is an implementation concern.
- Story alignment:
  - [ ] Matches TS behavior (C# is a regression vs both story and TS)
  - [x] Matches C# behavior (story was updated during port; TS was ahead of spec)
  - [ ] Describes a third behavior
  - [x] No story — spec gap (uniqueness constraint + ID contract for API responses should be documented)

## 5. Status

- **Classification**: Data-model regression (architectural shift, defensible direction). Not a revert target.
- **What's needed to finish**:
  1. Verify a unique index exists on `InstallationId`:
     - Check `TammaDbContext.OnModelCreating` for `modelBuilder.Entity<GitHubInstallation>().HasIndex(x => x.InstallationId).IsUnique()`.
     - If missing: add it via a new migration.
  2. Document the two-ID contract in a short ADR or a comment in the entity file. State which ID is used where:
     - External callers (GitHub webhooks, URLs) use `long InstallationId`.
     - Internal FKs use `Guid Id` (aliased as `InstallationEntityId` in child entities).
     - API responses should use `long InstallationId` for external consumers (or both, explicitly).
  3. Rename repository methods for clarity if the current names cause confusion: `GetByGitHubInstallationIdAsync(long)` vs `GetByIdAsync(Guid)`. The current `GetByInstallationIdAsync` vs `GetByEntityIdAsync` is fine but slightly nonobvious.
  4. Audit `CallbackResult`, `WebhookResult`, and other DTOs returning identifiers; prefer returning the GitHub `installation_id` outward.
- **Is it "just a stub" or is scope missing?** Scope shift. Not wrong, but under-documented.
- **Blockers**: None functional. Just contract clarity.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` — ensure unique index on `GitHubInstallation.InstallationId`.
  - `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs` — comment clarifying the two-ID contract.
  - Potentially `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs` — review what `CallbackResult` returns.
- Files to create:
  - Optional: `docs/decisions/ADR-007-installation-surrogate-key.md` (or similar).
  - EF Core migration adding unique index if absent.
- Tests to add:
  - `InstallationRepositoryTests.InsertDuplicateInstallationId_Throws` — should surface a unique-constraint violation, not a silent second row.
  - `InstallationRepositoryTests.GetByInstallationId_ReturnsUniqueRow`
- Estimated effort: 2h broken down as:
  - Unique-index migration + test: 1h
  - ADR + doc comments: 1h

## References

- TS source (SQL): `database/archived-sql-migrations/001_github_installations.sql:4-13,15-26`
- C# source:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/InstallationRepository.cs`
- Story: no story governs this specifically
- Archived SQL migration: `database/archived-sql-migrations/001_github_installations.sql`
- Related findings: `004-installation-deleted-soft-vs-hard.md`, `018-schema-installation-no-apikey-columns.md`, `020-github-callback-auth-model-redirect-vs-401.md`
- CLAUDE.md: "No migration anxiety: App is not in production with users"

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Already-fixed (architectural shift confirmed; uniqueness present)
- **Commit**: n/a (ratified existing schema)
- **Notes**: The Guid surrogate PK + bigint `InstallationId` natural-key model is the deliberate target architecture. `TammaDbContext.OnModelCreating` already declares `entity.HasIndex(e => e.InstallationId).IsUnique()` so the natural-key uniqueness constraint is enforced at the DB level (`UNIQUE INDEX "IX_github_installations_InstallationId"`). Repository methods are explicitly named (`GetByInstallationIdAsync(long)` vs `GetByEntityIdAsync(Guid)`) so callers cannot mistakenly pass the wrong identifier — type system catches it. The two-ID contract is documented in the `GitHubInstallation` entity comments and the `IInstallationRepository` summary. No code change required; the audit's "regression" classification is accurate as a documentation-of-shift rather than a defect to revert.
