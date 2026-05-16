# Story 27-8: Convention Store Database Schema + Migration

Status: ready-for-dev

## Story

As a **platform engineer**,
I want PostgreSQL tables for storing coding convention entries with keyword-based matching and multi-tenant support,
so that conventions are persisted in the database with tenant-level isolation and can be resolved using a two-tier fallback (tenant override then system default) filtered by keyword match against the current LLM call context.

## Acceptance Criteria

1. A `conventions` table exists with columns: `id` (UUID PK), `tenant_id` (UUID nullable, FK to tenants), `key` (TEXT NOT NULL — stable slug), `name` (TEXT NOT NULL), `description` (TEXT), `category` (TEXT NOT NULL), `body` (TEXT NOT NULL), `match_mode` (TEXT NOT NULL DEFAULT 'any'), `always_apply` (BOOLEAN NOT NULL DEFAULT false), `priority` (INTEGER NOT NULL DEFAULT 0), `enabled` (BOOLEAN NOT NULL DEFAULT true), `version` (INTEGER NOT NULL DEFAULT 1), `created_at` (TIMESTAMPTZ), `updated_at` (TIMESTAMPTZ), `created_by` (UUID nullable), `updated_by` (UUID nullable)
2. A `convention_keywords` table exists with columns: `id` (UUID PK), `convention_id` (UUID NOT NULL, FK to conventions ON DELETE CASCADE), `keyword` (TEXT NOT NULL — lowercase, trimmed)
3. A UNIQUE constraint on `convention_keywords(convention_id, keyword)` prevents duplicate keywords per convention
4. B-tree index on `convention_keywords(keyword)` for the resolution hot path: `WHERE keyword IN (...)`
5. A partial unique index enforces one system default per key: `UNIQUE(key) WHERE tenant_id IS NULL`
6. A partial unique index enforces one tenant override per key per tenant: `UNIQUE(tenant_id, key) WHERE tenant_id IS NOT NULL`
7. B-tree indexes on: `conventions(tenant_id)`, `conventions(category)`, `conventions(enabled, priority DESC)`
8. CHECK constraint: `match_mode IN ('any', 'all')`
9. CHECK constraint: `version > 0`
10. Seed migration inserts 46 system default convention rows from `ConventionTemplates.cs` with `tenant_id = NULL`: 20 language/framework, 11 action-triggered, 8 role-triggered, 7 cross-cutting
11. Seed migration inserts corresponding `convention_keywords` rows for each convention (~160 rows total; e.g., `typescript-react` gets rows for `typescript`, `react`, `nextjs`, `tsx`; `universal-safety` and `universal-quality` get zero keywords but `always_apply = true`)
12. All seed inserts use `ON CONFLICT DO NOTHING` for idempotency
13. Migration is idempotent (running it twice produces no errors)

## Technical Context

### Current State

Convention templates are static in-code constants in `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionTemplates.cs`. There are 46 templates across four groups: 20 language/framework (keyed by language slug), 11 action-triggered (keyed by `action-*`), 8 role-triggered (keyed by `role-*`), and 7 cross-cutting (universal rules, git, error handling, API, database, observability). Each has:
- `Key`: stable identifier slug
- `Name`: human-readable name
- `Description`: one-line summary
- `Conventions`: full Markdown body injected into `{{conventions}}`

There is no database storage, no tenant scoping, and no keyword-based matching. The `ConventionSelector` UI component lets users pick a template and paste its body into the prompt editor — a one-time insert with no persistent link.

### Why a Separate Keywords Table

Keywords drive the resolution hot path — every LLM call runs: "given these N terms from the call context, which conventions match?" Two approaches:

| Approach | Resolution query | Performance |
|----------|-----------------|-------------|
| `keywords TEXT[]` + GIN index | `WHERE keywords && ARRAY['ts','react','auth']` | GIN decomposes arrays per row; degrades as conventions scale |
| `convention_keywords` table + B-tree | `WHERE keyword IN ('ts','react','auth')` → `GROUP BY convention_id` | Single B-tree index scan; O(N) in search terms, independent of convention count |

The normalized table is faster at the query that matters most and also provides:
- **Autocomplete**: `SELECT DISTINCT keyword FROM convention_keywords ORDER BY keyword` — one index scan
- **Reverse lookup**: "which conventions use keyword X?" — indexed
- **No duplicate keywords**: `UNIQUE(convention_id, keyword)` enforced at DB level
- **Keyword analytics**: frequency, most-used, orphaned keywords — trivial queries

### Why Keywords as the Matching Mechanism

Conventions need to be pulled into the `{{conventions}}` template variable at LLM-call time based on what the call is about. The matching signal is the keywords associated with each convention, matched against the action, tools, and content of the current call context.

Example: a convention with keywords `security`, `password`, `jwt`, `auth` would match when the LLM call context mentions any of those terms. A convention with keywords `typescript`, `react` would match when working on a TypeScript React repo.

The `match_mode` column on the conventions table controls how keywords combine:
- `'any'` (default): convention matches if ANY of its keywords appears in the call context
- `'all'`: convention matches only if ALL of its keywords appear

The `always_apply` flag bypasses keyword matching entirely — the convention is always injected (e.g., "house style" rules that apply everywhere).

### Override Semantics

Override works by `key` (the slug), same as prompts override by `(role, action)`:
- System default: `tenant_id IS NULL, key = 'typescript-react'`
- Tenant override: `tenant_id = 'acme-uuid', key = 'typescript-react'`

When both exist, the tenant row wins. Tenant can also add new keys that don't exist in system defaults. Tenant can disable a system convention by creating an override with `enabled = false`.

### NULL vs Sentinel for tenant_id

Same convention as Story 27-1:
- `NULL` = shipped with Tamma, managed by platform admin
- `DEFAULT_TENANT_ID` = the default tenant's own overrides (CLI/self-hosted mode)
- Any other UUID = a specific tenant's overrides

### Seed Data Mapping

The 40 `ConventionTemplates.cs` entries map to seed rows:

#### Language/Framework (20)

| Key | Name | Category | Keywords (derived) |
|-----|------|----------|--------------------|
| `typescript-react` | TypeScript + React/Next.js | coding | `['typescript','react','nextjs','tsx']` |
| `typescript-node` | TypeScript + Node.js | coding | `['typescript','nodejs','node','ts']` |
| `typescript-react-native` | TypeScript + React Native | coding | `['typescript','react-native','expo','mobile']` |
| `python` | Python | coding | `['python','py','pip']` |
| `python-fastapi` | Python + FastAPI | coding | `['python','fastapi','pydantic','uvicorn']` |
| `python-django` | Python + Django | coding | `['python','django','orm','drf']` |
| `go` | Go | coding | `['go','golang','goroutine']` |
| `rust` | Rust | coding | `['rust','cargo','tokio']` |
| `java` | Java + Spring Boot | coding | `['java','spring','springboot','maven']` |
| `kotlin` | Kotlin + Android | coding | `['kotlin','android','jetpack']` |
| `csharp` | C# + .NET | coding | `['csharp','dotnet','aspnet','ef']` |
| `swift` | Swift + iOS | coding | `['swift','ios','swiftui']` |
| `swift-uikit` | Swift + UIKit | coding | `['swift','ios','uikit']` |
| `dart-flutter` | Dart + Flutter | coding | `['dart','flutter','widget']` |
| `c` | C | coding | `['c','gcc','makefile','posix']` |
| `cpp` | C++ | coding | `['cpp','c++','cmake','stl']` |
| `ruby-rails` | Ruby on Rails | coding | `['ruby','rails','rspec']` |
| `php-laravel` | PHP + Laravel | coding | `['php','laravel','eloquent']` |
| `elixir-phoenix` | Elixir + Phoenix | coding | `['elixir','phoenix','ecto']` |
| `scala` | Scala | coding | `['scala','akka','zio','cats']` |

#### Action-Triggered (11)

| Key | Name | Category | Keywords (derived) |
|-----|------|----------|--------------------|
| `action-write-code` | Code Writing | coding | `['implement','writeCode','code']` |
| `action-review-code` | Code Review | review | `['code-review','reviewCode','review','pr']` |
| `action-design` | System Design | design | `['design','architect','plan']` |
| `action-write-tests` | Test Writing | testing | `['write-tests','writeTests','test','tdd']` |
| `action-debug` | Debugging | debugging | `['debug','fix','investigate','troubleshoot']` |
| `action-refactor` | Refactoring | coding | `['refactor','cleanup','restructure']` |
| `action-document` | Documentation Writing | documentation | `['summarize','writeDocs','document','readme']` |
| `action-plan` | Planning & Scoping | planning | `['plan','breakdown','estimate','scope']` |
| `action-context-scan` | Context Research | research | `['context-scan','research','analysis','codebase']` |
| `action-triage` | Issue Triage | planning | `['triage','prioritize','classify','assess']` |
| `action-deploy` | Deployment | devops | `['deploy','deployment','release','rollout']` |

#### Role-Triggered (8)

| Key | Name | Category | Keywords (derived) |
|-----|------|----------|--------------------|
| `role-security-reviewer` | Security Review | security | `['security','securityReviewer','owasp','vulnerability']` |
| `role-architect` | Architect | design | `['architect','systemDesign','scalability']` |
| `role-qa-engineer` | QA Engineer | testing | `['tester','qa','qualityAssurance']` |
| `role-devops-engineer` | DevOps Engineer | devops | `['devops','deploy','infrastructure','ci']` |
| `role-tech-lead` | Tech Lead | coding | `['senior_developer','techLead','mentor','standards']` |
| `role-developer` | Developer | coding | `['developer','implementer','coder']` |
| `role-product-owner` | Product Owner | planning | `['product_owner','analyst','stakeholder','requirements']` |
| `role-tech-writer` | Tech Writer | documentation | `['tech_writer','documenter','technical-writing']` |

#### Cross-Cutting (7)

| Key | Name | Category | Keywords (derived) | always_apply |
|-----|------|----------|--------------------|-------------|
| `universal-safety` | Universal Safety Rules | security | — | `true` |
| `universal-quality` | Universal Quality Standards | coding | ��� | `true` |
| `git-conventions` | Git & PR Conventions | coding | `['git','commit','branch','pr']` | `false` |
| `error-handling` | Error Handling & Resilience | coding | `['error','exception','retry','resilience']` | `false` |
| `api-design` | API Design | design | `['api','rest','graphql','endpoint']` | `false` |
| `database-conventions` | Database Conventions | coding | `['database','sql','migration','schema']` | `false` |
| `observability` | Observability & Monitoring | devops | `['logging','monitoring','tracing','metrics']` | `false` |

### Files to Create

| File | Purpose |
|------|---------|
| `database/migrations/018_convention_store.sql` | Create table, indexes, seed data |

### Files to Modify

| File | Change |
|------|--------|
| `docs/stories/migration-ordering.md` | Add migration 018 entry |

## Implementation Plan

### Step 1: Create the Tables

```sql
-- Main conventions table (no keywords column — keywords live in convention_keywords)
CREATE TABLE IF NOT EXISTS conventions (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     UUID REFERENCES tenants(id) ON DELETE CASCADE,
  key           TEXT NOT NULL,
  name          TEXT NOT NULL,
  description   TEXT,
  category      TEXT NOT NULL,
  body          TEXT NOT NULL,
  match_mode    TEXT NOT NULL DEFAULT 'any'
                CHECK (match_mode IN ('any', 'all')),
  always_apply  BOOLEAN NOT NULL DEFAULT false,
  priority      INTEGER NOT NULL DEFAULT 0,
  enabled       BOOLEAN NOT NULL DEFAULT true,
  version       INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by    UUID,
  updated_by    UUID
);

-- Partial unique indexes (same pattern as prompts)
CREATE UNIQUE INDEX IF NOT EXISTS idx_conventions_system_default
  ON conventions (key)
  WHERE tenant_id IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_conventions_tenant_override
  ON conventions (tenant_id, key)
  WHERE tenant_id IS NOT NULL;

-- Lookup indexes
CREATE INDEX IF NOT EXISTS idx_conventions_tenant_id
  ON conventions (tenant_id);
CREATE INDEX IF NOT EXISTS idx_conventions_category
  ON conventions (category);
CREATE INDEX IF NOT EXISTS idx_conventions_enabled_priority
  ON conventions (enabled, priority DESC);

-- Keywords table — normalized, B-tree indexed for fast resolution
CREATE TABLE IF NOT EXISTS convention_keywords (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  convention_id   UUID NOT NULL REFERENCES conventions(id) ON DELETE CASCADE,
  keyword         TEXT NOT NULL,
  UNIQUE (convention_id, keyword)
);

-- The resolution hot-path index: WHERE keyword IN ('typescript', 'react', 'auth')
CREATE INDEX IF NOT EXISTS idx_convention_keywords_keyword
  ON convention_keywords (keyword);

-- Reverse lookup: all keywords for a given convention
CREATE INDEX IF NOT EXISTS idx_convention_keywords_convention_id
  ON convention_keywords (convention_id);
```

### Step 2: Seed System Default Conventions

```sql
-- Insert convention rows (no keywords column)
INSERT INTO conventions (id, tenant_id, key, name, description, category, body, match_mode, always_apply, priority, enabled, version)
VALUES
  ('00000000-0000-0000-0000-000000000001', NULL, 'typescript-react',
   'TypeScript + React/Next.js',
   'TypeScript + React 19/Next.js 15, RSC, hooks, Tailwind CSS, Vitest/RTL',
   'coding', E'# TypeScript + React/Next.js Conventions\n...',
   'any', false, 0, true, 1),
  -- ... 45 more rows (20 language + 11 action + 8 role + 7 cross-cutting)
ON CONFLICT DO NOTHING;

-- Insert keywords (FK to conventions via deterministic UUIDs)
INSERT INTO convention_keywords (convention_id, keyword)
VALUES
  ('00000000-0000-0000-0000-000000000001', 'typescript'),
  ('00000000-0000-0000-0000-000000000001', 'react'),
  ('00000000-0000-0000-0000-000000000001', 'nextjs'),
  ('00000000-0000-0000-0000-000000000001', 'tsx'),
  -- ... keywords for remaining 38 conventions (~160 rows total)
  -- Note: universal-safety and universal-quality have always_apply=true and no keyword rows
ON CONFLICT DO NOTHING;
```

Seed uses deterministic UUIDs for convention IDs so keyword FK inserts can reference them reliably. An alternative is to use a CTE (`WITH ins AS (INSERT ... RETURNING id)`) to capture generated IDs.

### Step 3: Seed Script Generation

Same approach as Story 27-1: create a one-time script that reads `ConventionTemplates.cs` bodies and generates the INSERT statements with properly escaped strings. The generated SQL is committed into the migration file.

## Implementation Notes

1. The `tenant_id` FK references `tenants(id)` from Epic 17 migration 008. `ON DELETE CASCADE` means deleting a tenant removes its convention overrides; system defaults are unaffected.
2. Keywords are stored in a separate `convention_keywords` table, not as a `TEXT[]` column. This gives B-tree index lookups on the resolution hot path instead of GIN array scans. `ON DELETE CASCADE` on the FK means deleting a convention automatically removes its keywords.
3. The `keyword` column stores lowercase, trimmed text. Application code normalizes keywords before insert. The `UNIQUE(convention_id, keyword)` constraint prevents duplicates per convention.
4. The `key` column on `conventions` is the stable identifier used for override semantics. It is NOT the same as `keywords` — `key` is an identity slug, `keywords` are matching terms.
5. The `category` column groups conventions in the UI. Initial categories: `coding`, `security`, `testing`, `devops`, `api`, `docs`. Enforced by CHECK or application-level.
6. The `always_apply` flag means the convention is injected into every `{{conventions}}` resolution regardless of keyword match. Use for universal rules (e.g., "house style").
7. The `priority` column controls concatenation order when multiple conventions match. Higher priority = appears first in the merged output.
8. **No RLS on either table.** Same exemption as prompts (Story 17-2): resolution crosses tenant boundaries by design (reading system defaults when no tenant override). Application-level filtering is used.

## Testing Strategy

### Unit Tests

1. Migration SQL parses without syntax errors
2. Both tables created with correct columns and types
3. Partial unique indexes prevent duplicate system defaults (same key, both NULL tenant_id)
4. Partial unique indexes allow the same key for different tenant_ids
5. `convention_keywords` UNIQUE constraint prevents duplicate keywords per convention
6. B-tree index on `convention_keywords(keyword)` exists
7. Seed data inserts 46 convention rows and corresponding keyword rows (~190 keyword rows)
8. Re-running seed (ON CONFLICT DO NOTHING) does not change row counts
9. `match_mode` CHECK rejects values other than 'any' / 'all'
10. `version <= 0` rejected by CHECK constraint

### Integration Tests

11. Run migration against a test PostgreSQL database — verify tables, columns, indexes exist
12. Insert a system default and a tenant override for the same key — both convention rows exist
13. Delete a tenant — verify CASCADE deletes tenant overrides and their keywords, system defaults remain
14. Delete a convention — verify CASCADE deletes its keyword rows
15. `SELECT DISTINCT convention_id FROM convention_keywords WHERE keyword IN ('typescript', 'react')` returns expected conventions
16. `SELECT DISTINCT keyword FROM convention_keywords ORDER BY keyword` returns sorted unique keywords (autocomplete query)

## Migration Number

This story uses **migration 018** (`018_convention_store.sql`). See `/docs/stories/migration-ordering.md` for the cross-epic migration sequence.

## Dependencies

- **Epic 17** (Story 17-1: Tenant Model + Database Schema) — the `tenants` table must exist for FK references (migration 008)
- Internal: `apps/tamma-elsa/src/Tamma.Api/Services/Conventions/ConventionTemplates.cs` (source of seed data)

## Estimated Effort

| Task | Hours |
|------|-------|
| Migration SQL (2 tables, indexes, constraints) | 2.5 |
| Seed script to generate INSERT statements from ConventionTemplates.cs | 2 |
| Seed data SQL (46 convention rows + ~190 keyword rows) | 3 |
| Unit tests (10 tests) | 1.5 |
| Integration tests (6 tests) | 2 |
| Update migration-ordering.md | 0.5 |
| **Total** | **10.5 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-05-04 | 1.0 | Initial story creation | Architecture Team |
