# Story 27-9: Convention Store Service (C#)

Status: ready-for-dev

## Story

As a **backend developer**,
I want a PostgreSQL-backed convention store that resolves conventions using tenant-level overrides with system default fallback, filtered by keyword matching against the current LLM call context,
so that each tenant gets its own convention configuration while inheriting sensible defaults from the platform, and the `{{conventions}}` template variable is populated with context-relevant coding rules.

## Acceptance Criteria

### Core CRUD

1. An `IConventionStore` interface defines all convention store operations
2. `Get(tenantId, key)` returns the tenant override if it exists, otherwise falls back to the system default (`tenant_id IS NULL`)
3. `Upsert(tenantId, key, data)` creates or updates a convention, bumping the version number
4. `Delete(tenantId, key)` removes a tenant override (the system default remains available)
5. `List(tenantId)` returns all conventions for a tenant: tenant overrides merged with system defaults (overrides take precedence by key)
6. `ListByCategory(tenantId, category)` returns merged conventions filtered by category

### System Default Operations

7. `GetSystemDefault(key)` returns a specific system default convention
8. `UpsertSystemDefault(key, data)` creates or updates a system default
9. `DeleteSystemDefault(key)` removes a system default
10. `ListSystemDefaults()` returns all system default conventions
11. `ResetSystemDefault(key)` restores the hardcoded default from `ConventionTemplates.cs`

### Keyword Resolution

12. `Resolve(tenantId, context)` is the main resolution method — returns the merged body of all matching conventions for the given call context
13. Resolution algorithm:
    a. Tokenize the call context into a set of lowercase search terms (from action, tools, repo languages, searchable text)
    b. Query `convention_keywords` for conventions whose keywords overlap the search terms: `SELECT DISTINCT convention_id FROM convention_keywords WHERE keyword IN (@terms)`
    c. Pull those convention rows plus all `always_apply` conventions, filtered to enabled, for both system defaults and tenant overrides
    d. Merge by `key` with tenant precedence (tenant row shadows system row with same key)
    e. For `match_mode = 'all'` conventions: verify ALL of the convention's keywords appear in the search terms (post-filter)
    f. Concatenate matched conventions ordered by `priority` DESC
14. `Resolve` returns a `ConventionResolution` containing: merged body string, list of triggered convention keys with match reasons, list of skipped convention keys, total character count, estimated token count
15. The `ConventionResolution.Body` is what gets substituted into `{{conventions}}`

### Resolution Context

16. The `LlmCallContext` passed to `Resolve` contains:
    - `Action` (string): the current action (e.g., `writeCode`, `reviewCode`, `design`)
    - `Tools` (string[]): tools available in this call (e.g., `edit`, `bash`, `write`)
    - `SearchableText` (string): the user input / prompt content for keyword matching
    - `RepoLanguages` (string[]): detected languages in the repository (e.g., `typescript`, `react`)
17. All four fields are checked against each convention's `keywords` array during matching
18. The action name, tool names, and repo languages are treated as implicit search terms in addition to the searchable text

### Error Handling & Edge Cases

19. All methods use async/await with proper error handling; database errors are wrapped in `TammaError`
20. When no conventions match, `Resolve` returns an empty body (not an error)
21. Backward compatibility: existing code that reads `{{conventions}}` from repo config continues to work — repo-config is a fallback source when `Resolve` returns empty

## Technical Context

### New Interface Design

```csharp
public interface IConventionStore
{
    // --- Tenant-scoped CRUD ---
    Task<Convention?> GetAsync(Guid? tenantId, string key, CancellationToken ct = default);
    Task<Convention> UpsertAsync(Guid? tenantId, string key, UpsertConventionInput input,
        Guid? userId = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid tenantId, string key,
        Guid? userId = null, CancellationToken ct = default);
    Task<IReadOnlyList<ConventionSummary>> ListAsync(Guid? tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<ConventionSummary>> ListByCategoryAsync(Guid? tenantId, string category,
        CancellationToken ct = default);

    // --- System default operations ---
    Task<Convention?> GetSystemDefaultAsync(string key, CancellationToken ct = default);
    Task<Convention> UpsertSystemDefaultAsync(string key, UpsertConventionInput input,
        Guid? userId = null, CancellationToken ct = default);
    Task<bool> DeleteSystemDefaultAsync(string key,
        Guid? userId = null, CancellationToken ct = default);
    Task<IReadOnlyList<ConventionSummary>> ListSystemDefaultsAsync(CancellationToken ct = default);
    Task<Convention?> ResetSystemDefaultAsync(string key,
        Guid? userId = null, CancellationToken ct = default);

    // --- Resolution ---
    Task<ConventionResolution> ResolveAsync(Guid? tenantId, LlmCallContext context,
        CancellationToken ct = default);
}
```

### Resolution Algorithm (Detail)

```
Resolve(tenantId, context):
  1. Tokenize context into search terms (lowercase, deduplicated):
     terms = tokenize(context.Action, context.Tools, context.RepoLanguages, context.SearchableText)

  2. Find candidate convention IDs via keywords table (B-tree index scan):
     keywordHits = SELECT convention_id, keyword
                   FROM convention_keywords
                   WHERE keyword IN (@terms)
     → gives us { conventionId → [matched keywords] }

  3. Also pull all always_apply convention IDs:
     alwaysApplyIds = SELECT id FROM conventions
                      WHERE always_apply = true AND enabled = true
                      AND (tenant_id IS NULL OR tenant_id = @tenantId)

  4. Union the candidate IDs, fetch full convention rows:
     candidates = SELECT * FROM conventions
                  WHERE id IN (@keywordHitIds UNION @alwaysApplyIds)
                  AND enabled = true
                  AND (tenant_id IS NULL OR tenant_id = @tenantId)

  5. Merge by key with tenant precedence:
     merged = dictionary<key, Convention>
     for each row in candidates WHERE tenant_id IS NULL: merged[row.key] = row
     for each row in candidates WHERE tenant_id IS NOT NULL: merged[row.key] = row  // tenant wins

  6. Post-filter for match_mode = 'all':
     for each convention in merged where match_mode == 'all':
       allKeywords = SELECT keyword FROM convention_keywords WHERE convention_id = @id
       if not allKeywords.All(kw => terms.Contains(kw)) → remove from matched, add to skipped

  7. Filter out conventions disabled by tenant override (enabled=false)

  8. Order matched by priority DESC

  9. Concatenate bodies with "\n\n---\n\n" separator

  10. Return ConventionResolution { Body, Triggered, Skipped, TotalChars, EstimatedTokens }
```

**Query efficiency**: Step 2 is a single B-tree index scan on `convention_keywords(keyword)` — the hot path. Steps 3-4 are indexed lookups by primary key. The whole resolution is 2-3 queries regardless of how many conventions exist.

### Data Models

```csharp
public sealed record Convention(
    Guid Id,
    Guid? TenantId,
    string Key,
    string Name,
    string? Description,
    string Category,
    string Body,
    string[] Keywords,
    string MatchMode,
    bool AlwaysApply,
    int Priority,
    bool Enabled,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? CreatedBy,
    Guid? UpdatedBy);

public sealed record ConventionSummary(
    string Key,
    string Name,
    string? Description,
    string Category,
    string[] Keywords,
    string MatchMode,
    bool AlwaysApply,
    int Priority,
    bool Enabled,
    int Version,
    bool IsOverride,
    DateTimeOffset UpdatedAt);

public sealed record UpsertConventionInput(
    string Name,
    string? Description,
    string Category,
    string Body,
    string[] Keywords,
    string MatchMode = "any",
    bool AlwaysApply = false,
    int Priority = 0,
    bool Enabled = true);

public sealed record LlmCallContext(
    string Action,
    string[] Tools,
    string SearchableText,
    string[] RepoLanguages);

public sealed record ConventionResolution(
    string Body,
    IReadOnlyList<TriggeredConvention> Triggered,
    IReadOnlyList<string> SkippedKeys,
    int TotalChars,
    int EstimatedTokens);

public sealed record TriggeredConvention(
    string Key,
    string Reason,
    string Source);  // "system" or "tenant"
```

### List with Merged View (SQL)

```sql
-- Same pattern as prompt store list
SELECT DISTINCT ON (key)
  *,
  CASE WHEN tenant_id IS NOT NULL THEN true ELSE false END AS is_override
FROM conventions
WHERE tenant_id IS NULL OR tenant_id = @tenantId
ORDER BY key,
  CASE WHEN tenant_id IS NOT NULL THEN 0 ELSE 1 END;
```

### Whole-Word Keyword Matching

```csharp
private static bool MatchesKeyword(string corpus, string keyword)
{
    var pattern = $@"\b{Regex.Escape(keyword)}\b";
    return Regex.IsMatch(corpus, pattern, RegexOptions.IgnoreCase);
}
```

Whole-word prevents "auth" matching "author". Case-insensitive because keywords and content may differ in casing.

### Files to Create

| File | Purpose |
|------|---------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/IConventionStore.cs` | Interface definition |
| `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/PgConventionStore.cs` | PostgreSQL implementation |
| `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionModels.cs` | Data models (Convention, LlmCallContext, etc.) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Conventions/PgConventionStoreTests.cs` | Unit + integration tests |

### Files to Modify

| File | Change |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionTemplateService.cs` | Add `GetDefaults()` for reset functionality |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` (or DI registration) | Register `IConventionStore` → `PgConventionStore` |

## Implementation Plan

### Step 1: Define Models and Interface

Create `ConventionModels.cs` with all record types. Create `IConventionStore.cs` with the interface.

### Step 2: Implement PgConventionStore CRUD

Connect to PostgreSQL via DI-injected `NpgsqlDataSource`. Each method maps to corresponding SQL:
- `GetAsync()`: two queries with early return (tenant override, then system default)
- `UpsertAsync()`: `INSERT ... ON CONFLICT (tenant_id, key) DO UPDATE SET ...` with version bump
- `DeleteAsync()`: `DELETE FROM conventions WHERE tenant_id = @t AND key = @k`
- `ListAsync()`: single query with `DISTINCT ON` for merged view

### Step 3: Implement Resolve

The resolution method pulls all enabled rows for both layers, merges by key with tenant precedence, evaluates keyword triggers, concatenates matching bodies, and returns the resolution result.

### Step 4: Implement ResetSystemDefault

`ResetSystemDefault(key)` looks up the matching entry in `ConventionTemplates.All`, then calls `UpsertSystemDefaultAsync` with the hardcoded values.

### Step 5: Wire DI

Register `IConventionStore` as a scoped service backed by `PgConventionStore`.

## Implementation Notes

1. `PgConventionStore` takes `NpgsqlDataSource` via constructor injection, following the C# codebase pattern (not `pg.Pool` — that's the TS side).
2. The `Resolve` method caches the system default rows per-request (they don't change within a single HTTP request). Tenant rows are always fresh.
3. Keyword matching uses compiled `Regex` for hot-path performance. Consider caching compiled patterns for frequently used keywords.
4. The `SearchableText` in `LlmCallContext` should NOT include the full prompt template — only the user input / issue body / dynamic content. Template text is static and would cause every convention to match.
5. The `EstimatedTokens` field uses `TotalChars / 4` as a rough approximation. This is sufficient for display purposes.
6. The `Source` field in `TriggeredConvention` ("system" or "tenant") enables the UI to show which layer each convention came from.

## Testing Strategy

### Unit Tests

1. `GetAsync(null, key)` returns system default
2. `GetAsync(tenantId, key)` returns tenant override when it exists
3. `GetAsync(tenantId, key)` falls back to system default when no tenant override exists
4. `UpsertAsync(tenantId, key, input)` creates new convention
5. `UpsertAsync(tenantId, key, input)` updates existing and bumps version
6. `DeleteAsync(tenantId, key)` removes override; subsequent `GetAsync()` returns system default
7. `ListAsync(null)` returns all system defaults
8. `ListAsync(tenantId)` returns merged view with correct `IsOverride` flags
9. `ListByCategoryAsync(tenantId, "coding")` filters correctly
10. `ResolveAsync` includes `always_apply` conventions regardless of keywords
11. `ResolveAsync` includes convention when ANY keyword matches (match_mode='any')
12. `ResolveAsync` includes convention only when ALL keywords match (match_mode='all')
13. `ResolveAsync` does NOT include convention when no keywords match
14. `ResolveAsync` uses whole-word matching (keyword 'auth' does NOT match 'author')
15. `ResolveAsync` merges by key with tenant precedence
16. `ResolveAsync` tenant override with `enabled=false` suppresses system default
17. `ResolveAsync` returns empty body when no conventions match
18. `ResolveAsync` concatenates by priority DESC with separator
19. `ResolveAsync` populates Triggered with correct reasons and Source
20. `ResetSystemDefaultAsync` restores hardcoded values from `ConventionTemplates.cs`

### Integration Tests

21. Full CRUD cycle against test PostgreSQL: create, read, update, delete
22. `ListAsync(tenantId)` returns correctly merged results from DB
23. Concurrent `UpsertAsync` calls do not produce duplicate rows
24. `ResolveAsync` end-to-end with DB-backed conventions
25. Keywords with special regex characters are safely escaped

## Dependencies

- **Story 27-8** (Convention Store Database Schema) — table must exist
- Internal: `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionTemplates.cs` (for reset defaults)

## Estimated Effort

| Task | Hours |
|------|-------|
| Models + interface definition | 1.5 |
| PgConventionStore CRUD implementation | 4 |
| Resolve method (keyword matching, merging, concat) | 4 |
| ResetSystemDefault | 0.5 |
| DI registration | 0.5 |
| Unit tests (20 tests) | 3 |
| Integration tests (5 tests) | 2 |
| **Total** | **15.5 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-05-04 | 1.0 | Initial story creation | Architecture Team |
