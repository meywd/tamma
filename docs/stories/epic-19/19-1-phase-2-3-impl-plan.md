# Epic 19 Phase 2+3 Implementation Plan

## Wiring Everything to the C# API + Deleting the TS API

**Prerequisite**: Phase 1 complete -- all 141 C# endpoints built, tested, and
runnable via `dotnet run` on port 3100.

---

## Phase 2: Wire Everything Up

### Task 2.1: Dashboard API Client Update

The dashboard has three API client files, all using the same pattern:
`fetch(`${API_BASE}${url}`, ...)` where `API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api'`.

**Files to audit**:

| File | Routes | Status |
|---|---|---|
| `packages/dashboard/src/services/admin/admin-api-client.ts` | `/auth/me`, `/admin/users`, `/admin/users/:id/keys`, `/admin/health` | Paths match C# story 1:1 |
| `packages/dashboard/src/services/settings/settings-api-client.ts` | `/config/agents`, `/config/security`, `/providers/health`, `/providers/diagnostics`, `/config/prompts` | Paths match C# story 1:1 |
| `packages/dashboard/src/services/knowledge-base/api-client.ts` | `/knowledge-base/*` (30 routes) | **Mismatch**: C# story uses `/kb/*`, TS API uses `/knowledge-base/*` |

**Action items**:

1. **URL path mismatch for KB routes**. The C# story defines routes under
   `/api/kb/*` but the dashboard client and the existing TS API both use
   `/api/knowledge-base/*`. Decision: make the C# API use `/api/knowledge-base/*`
   to match the dashboard, OR update all 30 dashboard KB routes. The simpler
   option is to register the C# KB endpoints under `/api/knowledge-base/` since
   the dashboard client + stores already use that prefix. **No dashboard changes
   needed if we keep the existing prefix in C#.**

2. **Base URL**. The dashboard uses `VITE_API_BASE_URL` env var, defaulting to
   `/api`. This works because nginx proxies `/api/` to the API. No change needed.

3. **Auth token format**. The dashboard uses `credentials: 'include'` (cookie-based).
   The C# API issues a `tamma_session` cookie same as the TS API. Verify:
   - Cookie name: `tamma_session`
   - Cookie domain: `.tamma.dev`
   - Cookie flags: `HttpOnly; Secure; SameSite=Lax`
   - JWT claims: `sub`, `tid`, `role`, `email` -- dashboard reads `role` and
     `email` from `/api/auth/me`, not from the JWT directly, so claim shape
     changes are transparent.

4. **TypeScript types**. The admin client defines inline types (`CurrentUser`,
   `AdminUser`, `ApiKeyEntry`, etc.). If C# response shapes differ (e.g.,
   `camelCase` vs `PascalCase`), update the types. ASP.NET Core's
   `System.Text.Json` defaults to camelCase serialization, so shapes should match.
   Verify each type's fields against the C# endpoint response DTOs.

5. **SSE removal**. The dashboard does not use SSE/EventSource. No changes needed.

6. **Settings client imports from `@tamma/shared`**. Types like `IAgentsConfig`,
   `SecurityConfig`, `DiagnosticsEvent` are used for type safety but not for
   runtime. They stay. The C# API must return JSON that conforms to these shapes.

7. **Zustand stores**. Three store files mirror the API clients:
   - `packages/dashboard/src/stores/admin/store.ts`
   - `packages/dashboard/src/stores/knowledge-base/store.ts`
   - `packages/dashboard/src/stores/settings/store.ts`
   
   These call the API client functions. If the API clients remain unchanged, the
   stores remain unchanged.

**Estimated effort**: 4 hours (mostly verification, not code changes).

---

### Task 2.2: CLI Update

The CLI makes HTTP calls to the API in two distinct patterns:

**Pattern A: Direct import of `@tamma/api`**

| File | Import | Usage |
|---|---|---|
| `packages/cli/src/commands/api.ts` | `import { startApiServer } from '@tamma/api'` | Spawns the Fastify process |
| `packages/cli/src/commands/server.ts` | `import { createApp, EngineRegistry, InMemoryWorkflowStore } from '@tamma/api'` | Creates Fastify app with engine |

These are the critical files. After consolidation:

- **`api.ts`**: Replace `startApiServer()` with spawning a child process:
  `child_process.spawn('dotnet', ['Tamma.Api.dll', ...])`. Alternatively,
  if we bundle the C# API as a standalone executable
  (`dotnet publish -r linux-x64 --self-contained`), spawn that binary.
  The command should:
  1. Locate the C# binary (via env var `TAMMA_API_BINARY` or a well-known path)
  2. Forward port/host options as command-line args or env vars
  3. Wait for the health endpoint to respond before returning

- **`server.ts`**: This is more complex -- it creates an in-process Fastify
  app, registers the TammaEngine, and serves engine routes. After C# migration,
  the engine still runs in the TS process but the HTTP API is the C# process.
  Two options:
  - Option A: `tamma server` spawns both the C# API process AND the TS engine
    process, with the engine calling the C# API over HTTP.
  - Option B: `tamma server` only spawns the C# API; the engine runs as an
    Elsa workflow, not a standalone process.
  
  **Recommendation**: Option A for now. The `server.ts` command spawns the C#
  API as a sidecar and wires the engine to call it over HTTP. The existing
  `WorkerResultCallback` pattern already does this.

**Pattern B: HTTP calls at runtime**

| File | Target | Endpoint |
|---|---|---|
| `packages/cli/src/worker/result-callback.ts` | `${apiUrl}/api/v1/workflows/:id/status` | Status reporting |
| `packages/cli/src/worker/result-callback.ts` | `${apiUrl}/api/v1/workflows/:id/result` | Result reporting |
| `packages/cli/src/commands/process-issue.ts` | Uses `WorkerResultCallback` above | Worker mode |

These use `fetch()` with `apiUrl` from `TAMMA_API_URL` env var (default
`https://api.tamma.dev`). **No changes needed** -- the C# API serves the same
endpoints at the same paths.

**Pattern C: Miscellaneous HTTP calls**

| File | Purpose |
|---|---|
| `packages/cli/src/commands/upgrade.ts` | Checks npm registry, not the API |
| `packages/cli/src/update-check.ts` | Checks npm registry, not the API |
| `packages/cli/src/commands/init.tsx` | Creates local config, no API calls |
| `packages/cli/src/commands/init-fullstack.ts` | Scaffolds project, no API calls |
| `packages/cli/src/preflight.ts` | Checks local env, no API calls |

No changes needed for these.

**Action items**:

1. Rewrite `packages/cli/src/commands/api.ts` to spawn C# process instead of
   importing `@tamma/api`.
2. Rewrite `packages/cli/src/commands/server.ts` to spawn C# API as sidecar
   while keeping the TS engine in-process.
3. Remove `@tamma/api` from `packages/cli/package.json` dependencies.
4. Verify `worker/result-callback.ts` endpoints match C# routes (they should).

**Estimated effort**: 8 hours.

---

### Task 2.3: Elsa Activities -- HTTP to DI Migration

Currently, 22 Elsa activities make HTTP calls to the TS API via
`IHttpClientFactory` + `IConfiguration["Engine:CallbackUrl"]`. The callback URL
is `TammaApi__BaseUrl` = `http://tamma-api:3100` (set in `docker-compose.yml`
line 92).

Since the C# API and Elsa server share the .NET ecosystem, activities should
inject repository/service interfaces directly instead of making HTTP calls.

**Complete list of activities making HTTP calls to the broker API**:

| # | Activity | Endpoint(s) Called | Replace With |
|---|---|---|---|
| 1 | `ADL/ApplyTriageResultActivity.cs` | `POST /api/engine/issue-labels`, `POST /api/engine/issue-comment`, `POST /api/engine/create-issue` | `IEngineService.AddLabels()`, `.CreateComment()`, `.CreateIssue()` |
| 2 | `ADL/ApplyReviewFixesActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 3 | `ADL/FetchUntriagedItemsActivity.cs` | `GET /api/engine/issues`, `GET /api/engine/security-alerts` | `IEngineService.ListIssues()`, `.ListSecurityAlerts()` |
| 4 | `ADL/ReportCycleResultActivity.cs` | `POST /api/engine/cycle-result` | `IEngineService.SubmitCycleResult()` |
| 5 | `ADL/SelectWorkItemActivity.cs` | `GET /api/engine/issues` | `IEngineService.ListIssues()` |
| 6 | `ADL/UpdateIssueStatusActivity.cs` | `POST /api/engine/issue-comment`, `POST /api/engine/issue-labels`, `DELETE /api/engine/issue-labels/:repo/:issue/:label` | `IEngineService.CreateComment()`, `.AddLabels()`, `.RemoveLabel()` |
| 7 | `AI/ClaudeAnalysisActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 8 | `Context/ReadRepoConventionsActivity.cs` | `GET /api/engine/repo-config` | `IEngineService.GetRepoConfig()` |
| 9 | `Context/StoreFindingsActivity.cs` | `POST /api/engine/store-context` | `IEngineService.StoreContext()` |
| 10 | `Context/StoreRoleFindingActivity.cs` | `POST /api/engine/store-context` | `IEngineService.StoreContext()` |
| 11 | `Testing/CommitFixActivity.cs` | `POST /api/engine/commit-fix` | `IEngineService.CommitFix()` |
| 12 | `Testing/TriggerCIActivity.cs` | `POST /api/engine/trigger-ci` | `IEngineService.TriggerCi()` |
| 13 | `TDD/AnalyzeCodeActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 14 | `TDD/ApplyRefactoringActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 15 | `TDD/CommitChangesActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 16 | `TDD/RevertRefactoringActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 17 | `TDD/WriteImplementationActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 18 | `TDD/WriteTestsActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 19 | `Debug/AIDiagnosisActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 20 | `Debug/RefineHypothesisActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 21 | `Debug/WriteRegressionTestActivity.cs` | `POST /api/engine/execute-task` | `IEngineService.ExecuteTask()` |
| 22 | `LlmCall/ResolvePromptFromRegistryActivity.cs` | Reads from `Engine:CallbackUrl` but calls prompt API | `IPromptService.Resolve()` |

**Unique API endpoints consumed**:
- `POST /api/engine/execute-task` (11 activities)
- `POST /api/engine/store-context` (2 activities)
- `POST /api/engine/issue-labels` (2 activities)
- `POST /api/engine/issue-comment` (2 activities)
- `POST /api/engine/create-issue` (1 activity)
- `POST /api/engine/cycle-result` (1 activity)
- `POST /api/engine/trigger-ci` (1 activity)
- `POST /api/engine/commit-fix` (1 activity)
- `GET /api/engine/issues` (2 activities)
- `GET /api/engine/security-alerts` (1 activity)
- `GET /api/engine/repo-config` (1 activity)
- `DELETE /api/engine/issue-labels/:repo/:issue/:label` (1 activity)

**Implementation approach**:

1. Define `IEngineService` interface in `Tamma.Core` (or `Tamma.Data`) with
   methods matching the 12 unique endpoints above.

2. Implement `EngineService` in `Tamma.Api/Services/` that calls the C# API's
   own engine logic directly (same process, no HTTP).

3. Register `IEngineService` in the Elsa server's DI container. The Elsa server
   already references `Tamma.Activities`, so add a project reference from
   `Tamma.Activities` to `Tamma.Core` (if not already present).

4. In each of the 22 activities, replace the `IHttpClientFactory` +
   `IConfiguration["Engine:CallbackUrl"]` pattern with a constructor-injected
   `IEngineService`.

5. Remove `TammaApi__BaseUrl` from `docker-compose.yml` elsa-server environment
   (line 92), since Elsa no longer needs to know the API URL.

6. Update activity unit tests to mock `IEngineService` instead of
   `IHttpClientFactory`.

**Note**: Activities that use `IHttpClientFactory` for LLM provider calls
(`CallLlmActivity.cs`, `CallLlmInlineActivity.cs`) are NOT in this list -- those
call external LLM APIs (Anthropic, OpenAI, OpenRouter), not the Tamma API. They
keep `IHttpClientFactory`.

**Estimated effort**: 12 hours.

---

### Task 2.4: nginx Configuration

Current nginx routes `tamma-api:3100` to the TS API. The C# API will also
listen on port 3100, so the upstream name stays the same.

**Current routing** (from `docker/nginx-proxy.conf.template`):

| Server Block | Location | Upstream |
|---|---|---|
| Bare IP :80 | `/api/` | `tamma-api:3100` |
| `app.tamma.dev` | `/api/` | `tamma-api:3100` |
| `app.tamma.dev` | SSE `^/api/(engine/events|workflows/.*/events)` | `tamma-api:3100` |
| `api.tamma.dev` | `/api/github/webhooks` | `tamma-api:3100` |
| `api.tamma.dev` | `/` | `tamma-api:3100` |
| `elsa.tamma.dev` | `/auth/role-check` | `tamma-api:3100` |
| `logs.tamma.dev` | `/auth/role-check` | `tamma-api:3100` |

**Changes**:

1. **Remove SSE location block**. The C# API does not serve SSE endpoints.
   Delete the `location ~ ^/api/(engine/events|workflows/.*/events)` block
   (lines 145-157). If we add SignalR later, add a WebSocket upgrade block then.

2. **Remove `tamma-api-dotnet` references**. Currently there is no explicit
   nginx routing to `tamma-api-dotnet:3000` -- the .NET API was used only by
   Elsa internally. But verify no location blocks reference it.

3. **Keep all other routing intact**. The C# API on port 3100 replaces the TS
   API on port 3100 -- same hostname, same port, same paths. All existing
   `proxy_pass http://tamma-api:3100` directives work unchanged.

4. **Keep oauth2-proxy integration**. The role-check endpoint path
   (`/api/auth/role-check`) is the same in the C# API.

5. **Keep Cloudflare origin cert TLS**. No changes to SSL configuration.

**Estimated effort**: 2 hours.

---

### Task 2.5: Docker Compose Update

**Current service layout** (from `docker/docker-compose.yml`):

| Service | Image | Port | Purpose |
|---|---|---|---|
| `tamma-api` | `Dockerfile.ts` target `tamma-api` | 3100 | TS Fastify API |
| `tamma-api-dotnet` | `Tamma.Api/Dockerfile` | 3000 | C# .NET API (Elsa mgmt) |
| `elsa-server` | `Tamma.ElsaServer/Dockerfile` | 5000 | Elsa workflow engine |
| `tamma-engine` | `Dockerfile.ts` target `tamma-engine` | -- | TS autonomous engine |

**Target layout**:

| Service | Image | Port | Purpose |
|---|---|---|---|
| `tamma-api` | `Tamma.Api/Dockerfile` | 3100 | C# consolidated API |
| `elsa-server` | `Tamma.ElsaServer/Dockerfile` | 5000 | Elsa workflow engine |
| `tamma-engine` | `Dockerfile.ts` target `tamma-engine` | -- | TS autonomous engine (keep for now) |

**Action items**:

1. **Replace `tamma-api` service definition**:
   - Change `build.context` from `..` to `../apps/tamma-elsa/src`
   - Change `build.dockerfile` from `docker/Dockerfile.ts` to `Tamma.Api/Dockerfile`
   - Remove `target: tamma-api` (C# Dockerfile has no multi-stage targets)
   - Keep port 3100 (configure in C# via `ASPNETCORE_URLS=http://+:3100`)
   - Merge environment variables: keep all from the TS service (`DATABASE_URL`,
     `GITHUB_APP_ID`, `GITHUB_WEBHOOK_SECRET`, `JWT_SECRET`, etc.) and translate
     to .NET naming convention (`ConnectionStrings__DefaultConnection`,
     `Jwt__Secret`, etc.)
   - Keep volume mount for `private-key.pem`
   - Keep `depends_on: postgres: condition: service_healthy`
   - Update healthcheck to `curl -f http://localhost:3100/api/health` (same URL)

2. **Delete `tamma-api-dotnet` service** entirely. Its functionality is now
   absorbed into `tamma-api`.

3. **Update `elsa-server` environment**:
   - Remove `TammaApi__BaseUrl: http://tamma-api:3100` (activities now use DI)
   - Keep all other env vars

4. **Update `depends_on` chains**:
   - `elsa-server` depends on `postgres` + `rabbitmq` (remove any tamma-api dep)
   - `tamma-engine` depends on `tamma-api` (unchanged)
   - `nginx-proxy` depends on `tamma-api` (unchanged)

5. **Update `docker-compose.prod.yml`**:
   - Remove `tamma-api-dotnet` resource limits section
   - `tamma-api` keeps 512M limit (or bump to 768M since it now serves all
     endpoints including mentorship)
   - Update memory budget comment

6. **Update `docker-compose.images.yml` template** in CI (generated in the
   deploy step). Remove `tamma-api-dotnet` line. The `tamma-api` image now
   points to the C# image from `ghcr.io`.

**Estimated effort**: 4 hours.

---

### Task 2.6: CI Workflow Update

Current workflow (`.github/workflows/docker-publish.yml`) has three build jobs:

| Job | What It Builds |
|---|---|
| `build-ts` | Matrix: `[tamma-api, tamma-engine]` via `Dockerfile.ts` |
| `build-dashboard` | `tamma-dashboard` via `Dockerfile.dashboard` |
| `build-dotnet` | Matrix: `[tamma-elsa, tamma-api-dotnet, tamma-studio]` via .NET Dockerfiles |

**Changes**:

1. **`build-ts` job**: Remove `tamma-api` from the matrix. It becomes
   `matrix: target: [tamma-engine]`. If `tamma-engine` is the only target,
   simplify by removing the matrix and hardcoding.

2. **`build-dotnet` job**: Rename the `tamma-api-dotnet` matrix entry to
   `tamma-api`. Update:
   - `name: tamma-api`
   - `context: apps/tamma-elsa/src`
   - `dockerfile: apps/tamma-elsa/src/Tamma.Api/Dockerfile`
   
   The C# API Dockerfile must be updated to build a standalone image that
   listens on port 3100.

3. **Deploy job**:
   - **Layer 3**: Change from `tamma-api-dotnet tamma-api` to just `tamma-api`.
   - **Verify layer 3**: Remove the `tamma-api-dotnet` health check. Only check
     `tamma-api` at `http://127.0.0.1:3100/api/health`.
   - **`docker-compose.images.yml` generation**: Remove `tamma-api-dotnet` entry.
     The `tamma-api` entry now uses the C# image:
     ```yaml
     tamma-api:
       image: ghcr.io/${OWNER}/tamma-api:${IMAGE_TAG}
     ```
   - **Failure log dump**: Remove `tamma-api-dotnet` from the service list.

4. **Migration step**: The deploy job runs `database/migrations/*.sql` via
   `psql`. After Phase 3, these files will be archived. For now, EF Core
   migrations run at C# API startup (`app.Database.Migrate()` or via a startup
   task). Add a note that the SQL migration step will be removed once
   `database/migrations/` is archived.

**Estimated effort**: 6 hours.

---

### Task 2.7: Post-Deploy Tests Update

Current test file: `docker/post-deploy-tests.sh`.

The tests use `curl` with `--resolve` to test endpoints via nginx. Since the
C# API serves the same paths, most tests work unchanged.

**Changes needed**:

1. **Verify endpoint response shapes**. The tests only check HTTP status codes,
   not response bodies. Status codes should be identical:
   - `GET /api/health` -> 200
   - `POST /api/github/webhooks` with bad data -> 401 (webhook signature check)
   - `GET /api/admin/users` without auth -> 401
   - `POST /api/v1/auth/register` with bad data -> 400
   - `POST /api/v1/auth/login` with bad creds -> 401
   - `POST /api/v1/auth/password-reset/request` with empty body -> 400
   
   Verify the C# API returns these exact status codes for these inputs.

2. **Remove `tamma-api-dotnet` health check from diagnostics** (if any). The
   current pre-test diagnostics only inspect nginx and oauth2-proxy, not the
   .NET API directly, so this should be fine.

3. **Add a new test for the C# health endpoint** to verify it returns the
   expected JSON shape (e.g., `{"status":"ok"}`).

4. **Update layer verification in CI** to not reference `tamma-api-dotnet`.

**Estimated effort**: 2 hours.

---

### Phase 2 Verification Checklist

Before proceeding to Phase 3, verify all of the following:

- [ ] Dashboard login flow works (GitHub OAuth -> cookie -> `/api/auth/me`)
- [ ] Dashboard admin pages load data (`/api/admin/users`, `/api/admin/health`)
- [ ] Dashboard settings pages work (`/api/config/agents`, `/api/providers/health`)
- [ ] Dashboard KB pages work (`/api/knowledge-base/*` -- stubs are fine)
- [ ] CLI `tamma api` spawns C# process and health check passes
- [ ] CLI `tamma server` starts engine + C# sidecar
- [ ] Worker `tamma process-issue` reports results to C# API
- [ ] Elsa activities run without HTTP calls to `Engine:CallbackUrl`
- [ ] nginx routes all traffic correctly (spot-check each server block)
- [ ] Docker compose builds and runs with only `tamma-api` (no `tamma-api-dotnet`)
- [ ] CI pipeline green on a test branch
- [ ] Post-deploy tests pass on VPS

---

## Phase 3: Delete

### Task 3.1: Delete `packages/api/` Directory

```bash
rm -rf packages/api
```

This removes:
- 40+ route files (`routes/*.ts`)
- 19 persistence store files (`stores/*.ts`, `stores/**/*.ts`)
- 13 auth files (`auth/*.ts`, `middleware/*.ts`)
- 5 middleware files
- 18 service files (`services/*.ts`)
- 75+ test files (`__tests__/*.test.ts`)
- `package.json`, `tsconfig.json`, `vitest.config.ts`
- Approximately 15,000 lines of TypeScript

**Estimated effort**: 0.5 hours.

---

### Task 3.2: Remove from pnpm-workspace.yaml

The current `pnpm-workspace.yaml` uses glob patterns:

```yaml
packages:
  - 'packages/*'
  - 'apps/*'
```

Since `packages/api` is deleted, the glob no longer matches it. No explicit
change needed to `pnpm-workspace.yaml`.

**However**, verify no other package imports `@tamma/api`:

| Package | Depends on `@tamma/api`? | Action |
|---|---|---|
| `packages/cli` | Yes (`"@tamma/api": "workspace:*"`) | Remove from `package.json` (already done in Task 2.2) |
| All others | No | None |

Run `pnpm install` to verify clean resolution. Then `pnpm build` to confirm
no compilation errors.

**Estimated effort**: 1 hour.

---

### Task 3.3: Remove TS-Only Dependencies

Check which dependencies were exclusive to `packages/api`:

| Dependency | Used By `packages/api` | Used Elsewhere? | Action |
|---|---|---|---|
| `fastify` | Yes | No | Removed with `packages/api` |
| `@fastify/cookie` | Yes | No | Removed with `packages/api` |
| `@fastify/cors` | Yes | No | Removed with `packages/api` |
| `@fastify/helmet` | Yes | No | Removed with `packages/api` |
| `@fastify/jwt` | Yes | No | Removed with `packages/api` |
| `@fastify/rate-limit` | Yes | No | Removed with `packages/api` |
| `fastify-plugin` | Yes | No | Removed with `packages/api` |
| `pg` | Yes | Yes (`packages/workers`, `packages/events`, `packages/intelligence`) | **Keep** |
| `@types/pg` | Yes | Yes (same packages) | **Keep** |

Since all Fastify deps are in `packages/api/package.json` (not the root), they
are automatically removed when the directory is deleted. No manual cleanup of
the root `package.json` needed.

**Estimated effort**: 0.5 hours (verification only).

---

### Task 3.4: Archive SQL Migrations

Move the 18 hand-written SQL migrations to an archive directory:

```bash
mkdir -p database/archived-sql-migrations
mv database/migrations/*.sql database/archived-sql-migrations/
```

Keep the directory for historical reference. EF Core migrations
(`apps/tamma-elsa/src/Tamma.Data/Migrations/`) are the new source of truth.

**Also update CI**:
- The deploy job runs `database/migrations/*.sql` via `psql`. Remove this step
  from `.github/workflows/docker-publish.yml` (the "Run database migrations"
  step, lines 516-528). EF Core migrations run at C# API startup.

**Estimated effort**: 1 hour.

---

### Task 3.5: Update CLAUDE.md References

Search `CLAUDE.md` for references to the TS API and update:

1. **Repository Structure**: Remove `packages/api` from the tree. The C# API
   lives at `apps/tamma-elsa/src/Tamma.Api/`.

2. **Technology Stack**: Note that the REST API is now ASP.NET Core (not Fastify).
   Keep Fastify in the list only if other packages still use it (they don't).

3. **Development Commands**: Update `pnpm dev` examples if they referenced
   `@tamma/api`.

4. **API Endpoints**: Update the endpoint reference to note they are now served
   by the C# API.

5. **Key Architectural Decisions**: Update the Fastify decision to note migration
   to ASP.NET Core. Add a new decision entry for the consolidation.

**Estimated effort**: 2 hours.

---

### Task 3.6: Remove `Dockerfile.ts` tamma-api Target

The `docker/Dockerfile.ts` is a multi-stage Dockerfile with targets for
`tamma-api` and `tamma-engine`. After removing the TS API:

1. Remove the `tamma-api` stage from `Dockerfile.ts`.
2. If `tamma-engine` is the only remaining target, rename the file to
   `Dockerfile.engine` or keep as-is (both work).
3. Update any references in CI and docker-compose.

**Estimated effort**: 1 hour.

---

### Task 3.7: Final Verification

Run the full verification suite to confirm nothing is broken:

```bash
# TypeScript side
pnpm install          # Should succeed without @tamma/api
pnpm build            # All remaining packages compile
pnpm test             # All remaining tests pass

# .NET side
cd apps/tamma-elsa
dotnet build           # All projects compile
dotnet test            # All xUnit tests pass

# Docker
cd docker
docker compose build   # All images build
docker compose up -d   # All services start

# Post-deploy
bash post-deploy-tests.sh   # All endpoints respond correctly
```

Verify:
- [ ] `pnpm install` succeeds (no missing `@tamma/api` dependency)
- [ ] `pnpm build` succeeds (no import errors)
- [ ] `pnpm test` passes (CLI tests may need mocks updated)
- [ ] `dotnet build` succeeds
- [ ] `dotnet test` passes
- [ ] `docker compose up` starts all services
- [ ] Only one API container running (no `tamma-api-dotnet`)
- [ ] Post-deploy tests pass
- [ ] CI pipeline green
- [ ] Dashboard accessible and functional
- [ ] Elsa Studio accessible behind RBAC
- [ ] `tamma api` CLI command works

**Estimated effort**: 4 hours.

---

## Summary

| Phase | Task | Effort |
|---|---|---|
| **Phase 2** | 2.1 Dashboard API client | 4h |
| | 2.2 CLI update | 8h |
| | 2.3 Elsa activities DI migration | 12h |
| | 2.4 nginx config | 2h |
| | 2.5 Docker Compose | 4h |
| | 2.6 CI workflow | 6h |
| | 2.7 Post-deploy tests | 2h |
| **Phase 3** | 3.1 Delete `packages/api/` | 0.5h |
| | 3.2 pnpm workspace cleanup | 1h |
| | 3.3 TS-only deps check | 0.5h |
| | 3.4 Archive SQL migrations | 1h |
| | 3.5 Update CLAUDE.md | 2h |
| | 3.6 Dockerfile cleanup | 1h |
| | 3.7 Final verification | 4h |
| **Total** | | **48h** |

Compared to the original story estimate of 60h (Phase 2: 40h + Phase 3: 20h),
this plan is tighter because several dashboard/CLI changes are smaller than
expected (paths match, only spawn logic needs rewriting).
