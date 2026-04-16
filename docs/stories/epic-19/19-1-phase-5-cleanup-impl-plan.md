# Epic 19 / Story 19-1 — Phase 5: Cleanup & Docker Consolidation

## Implementation Plan

**Prerequisite**: Phases 1-4 are complete. All 90+ TS API endpoints have been
ported to the C# ASP.NET Core API (`apps/tamma-elsa/src/Tamma.Api`). Both APIs
are running side-by-side and nginx is routing 100% of traffic to the C# API via
per-path location blocks. Post-deploy tests pass against the C# endpoints.

**Goal**: Remove the TS API entirely. A single `tamma-api` Docker service (C#)
serves all traffic on port 3100. No TS API process runs anywhere.

**Estimated effort**: 24 hours

---

## Table of Contents

1. [Pre-Flight Checks](#1-pre-flight-checks)
2. [Remove `packages/api` from the Monorepo](#2-remove-packagesapi-from-the-monorepo)
3. [Rewrite CLI Commands That Import `@tamma/api`](#3-rewrite-cli-commands-that-import-tammaapi)
4. [Update `packages/cli/package.json`](#4-update-packagesclipackagejson)
5. [Merge Docker Services: `tamma-api` + `tamma-api-dotnet` into One](#5-merge-docker-services-tamma-api--tamma-api-dotnet-into-one)
6. [Update `docker-compose.prod.yml`](#6-update-docker-composeprodyml)
7. [Update `docker-compose.override.yml`](#7-update-docker-composeoverrideyml)
8. [Update C# Dockerfile to Listen on Port 3100](#8-update-c-dockerfile-to-listen-on-port-3100)
9. [Remove `Dockerfile.ts` Target `tamma-api`](#9-remove-dockerfilets-target-tamma-api)
10. [Simplify nginx Configuration](#10-simplify-nginx-configuration)
11. [Update CI Workflow: `docker-publish.yml`](#11-update-ci-workflow-docker-publishyml)
12. [Update CI Workflow: `deploy.yml`](#12-update-ci-workflow-deployyml)
13. [Update Post-Deploy Tests](#13-update-post-deploy-tests)
14. [Update Dashboard API Client Base URLs](#14-update-dashboard-api-client-base-urls)
15. [Update Elsa Server Configuration](#15-update-elsa-server-configuration)
16. [Update `init-fullstack` CLI Command](#16-update-init-fullstack-cli-command)
17. [Remove TS-Only Dependencies from Root `package.json`](#17-remove-ts-only-dependencies-from-root-packagejson)
18. [Update `pnpm-workspace.yaml` and Lockfile](#18-update-pnpm-workspaceyaml-and-lockfile)
19. [Update `Dockerfile.ts` Build Stage (Engine-Only)](#19-update-dockerfilets-build-stage-engine-only)
20. [Update Root `tsconfig` / `typecheck` Script](#20-update-root-tsconfig--typecheck-script)
21. [Archive SQL Migrations](#21-archive-sql-migrations)
22. [Update Documentation References](#22-update-documentation-references)
23. [Final Verification Checklist](#23-final-verification-checklist)

---

## 1. Pre-Flight Checks

Before starting any deletions, confirm:

- [ ] All Phase 4 C# xUnit tests pass: `dotnet test` in `apps/tamma-elsa/`
- [ ] Post-deploy tests pass with 100% traffic on C# API (no WARN results)
- [ ] No TS API routes are still receiving traffic (check nginx access logs)
- [ ] Create a rollback branch: `git branch phase-5-rollback-point`
- [ ] Verify the C# API `/api/health` returns 200 on the live VPS
- [ ] Verify the C# API serves all 3 SSE-replacement SignalR endpoints
- [ ] Confirm JWT tokens issued by the C# API work end-to-end (dashboard login)

---

## 2. Remove `packages/api` from the Monorepo

**What**: Delete the entire `packages/api/` directory (75 test files, 19
persistence stores, 90+ route files, all Fastify plugins).

**Files affected**:
- `packages/api/` (entire directory tree)

**Steps**:

1. `rm -rf packages/api`
2. Remove the `packages/api/package.json` COPY line from `docker/Dockerfile.ts`
   (line 21: `COPY packages/api/package.json packages/api/`). The engine target
   does not import `@tamma/api`, so this line only existed for the now-deleted
   `tamma-api` target.
3. Verify no other package has a `"@tamma/api"` dependency in its `package.json`.
   As of now, only `packages/cli/package.json` does (handled in task 4).
4. Search the codebase for stale `from '@tamma/api'` imports and fix them.

**Current import sites** (must all be rewritten or deleted):

| File | Import | Action |
|---|---|---|
| `packages/cli/src/commands/api.ts` | `startApiServer`, `ApiServerOptions` | Rewrite (task 3) |
| `packages/cli/src/commands/server.ts` | `createApp`, `EngineRegistry`, `InMemoryWorkflowStore` | Rewrite (task 3) |
| `packages/cli/scripts/prepare-package.mjs` | path mapping for `@tamma/api` | Remove entry |

---

## 3. Rewrite CLI Commands That Import `@tamma/api`

**What**: The CLI has two commands that directly import and start the TS Fastify
server. After consolidation, these must delegate to the C# API binary instead.

### 3a. `packages/cli/src/commands/api.ts`

**Current behavior**: Imports `startApiServer()` from `@tamma/api` and starts a
Fastify server in-process.

**New behavior**: Spawn the C# API as a child process.

```typescript
// New implementation sketch
import { spawn } from 'node:child_process';

export async function apiCommand(options: ApiCommandOptions): Promise<void> {
  const dllPath = options.dllPath
    ?? process.env['TAMMA_API_DLL_PATH']
    ?? 'Tamma.Api.dll';

  const args = ['dotnet', dllPath];
  if (options.port) {
    process.env['ASPNETCORE_URLS'] = `http://+:${options.port}`;
  }

  const child = spawn(args[0], args.slice(1), {
    stdio: 'inherit',
    env: { ...process.env },
  });

  child.on('exit', (code) => process.exit(code ?? 1));
}
```

Key considerations:
- `dotnet` must be on PATH (or the published self-contained binary used)
- Environment variables (`JWT_SECRET`, `DATABASE_URL`, etc.) pass through
- Port defaults to 3100 (matching the consolidated service)

### 3b. `packages/cli/src/commands/server.ts`

**Current behavior**: Creates a Fastify app with engine routes, auth plugin,
workflow sync routes, and dashboard routes via `createApp()` from `@tamma/api`.

**New behavior**: Spawn the C# API. The engine is a separate process
(`tamma-engine`) and already communicates with the API via HTTP. Remove the
in-process engine embedding from the `server` command.

Alternatively, this command can be deprecated entirely. The recommended way to
run the platform is:
1. `docker compose up` (production)
2. `tamma start` (engine only, CLI mode)
3. `tamma api` (API server only)

### 3c. `packages/cli/scripts/prepare-package.mjs`

Remove the `'@tamma/api': 'api'` entry from the path mapping object (line 28).

---

## 4. Update `packages/cli/package.json`

**Remove**:
```json
"@tamma/api": "workspace:*"
```

**Add** (if spawning dotnet process, no new deps needed):
No new npm dependencies required. The CLI uses `child_process.spawn`.

**File**: `packages/cli/package.json` line 23

---

## 5. Merge Docker Services: `tamma-api` + `tamma-api-dotnet` into One

**What**: In `docker/docker-compose.yml`, delete the `tamma-api-dotnet` service
and rename/reconfigure the `tamma-api` service to use the C# Dockerfile.

**Before** (lines 132-198):
```yaml
tamma-api-dotnet:   # C# ASP.NET (port 3000)
  build:
    context: ../apps/tamma-elsa/src
    dockerfile: Tamma.Api/Dockerfile
  ...

tamma-api:          # TS Fastify (port 3100)
  build:
    context: ..
    dockerfile: docker/Dockerfile.ts
    target: tamma-api
  ...
```

**After** (single service):
```yaml
tamma-api:
  build:
    context: ../apps/tamma-elsa/src
    dockerfile: Tamma.Api/Dockerfile
  restart: unless-stopped
  environment:
    ConnectionStrings__DefaultConnection: "Host=postgres;Port=5432;Database=${POSTGRES_DB:-tamma};Username=${POSTGRES_USER:-tamma};Password=${POSTGRES_PASSWORD}"
    ElsaServer__Url: http://elsa-server:5000
    TAMMA_SERVICE_API_KEY: ${TAMMA_SERVICE_API_KEY:-}
    Jwt__Secret: ${JWT_SECRET:-tamma-internal-jwt-secret-change-in-production}
    GITHUB_APP_ID: ${GITHUB_APP_ID:?GITHUB_APP_ID is required}
    GITHUB_APP_PRIVATE_KEY_PATH: /etc/tamma/private-key.pem
    GITHUB_WEBHOOK_SECRET: ${GITHUB_WEBHOOK_SECRET:?GITHUB_WEBHOOK_SECRET is required}
    GITHUB_OAUTH_CLIENT_ID: ${GITHUB_OAUTH_CLIENT_ID:-}
    GITHUB_OAUTH_CLIENT_SECRET: ${GITHUB_OAUTH_CLIENT_SECRET:-}
    API_BASE_URL: ${API_BASE_URL:-https://api.tamma.dev}
    DASHBOARD_URL: ${DASHBOARD_URL:-https://app.tamma.dev}
    ASPNETCORE_URLS: http://+:3100
    OpenSearch__Url: ${OPENSEARCH_URL:-http://opensearch:9200}
    OpenSearch__Enabled: ${OPENSEARCH_ENABLED:-true}
    OpenSearch__IndexPrefix: tamma-api
  volumes:
    - ../secrets/private-key.pem:/etc/tamma/private-key.pem:ro
  depends_on:
    elsa-server:
      condition: service_healthy
    postgres:
      condition: service_healthy
  healthcheck:
    test: ["CMD", "curl", "-f", "http://localhost:3100/health"]
    interval: 15s
    timeout: 5s
    start_period: 15s
    retries: 3
  networks:
    - tamma-net
```

**Key changes**:
- Build context switches from repo root to `../apps/tamma-elsa/src`
- Dockerfile changes from `docker/Dockerfile.ts` to `Tamma.Api/Dockerfile`
- No `target:` field (C# Dockerfile has a single final stage)
- Port changes from 3000 to 3100 via `ASPNETCORE_URLS=http://+:3100`
- Merge env vars from both old services (DB, GitHub, JWT, OpenSearch)
- `depends_on` includes `elsa-server` (from old `tamma-api-dotnet`)
- `TammaServer__Url` removed (no longer needed; this service IS the API)
- `CHROMADB_URL` added if the C# API has taken over vector DB routes
- OpenSearch index prefix changes from `tamma-api-dotnet` to `tamma-api`

**Also update** any `depends_on` references:
- `tamma-engine` depends on `tamma-api: condition: service_started` -- no change needed (name preserved)
- `tamma-dashboard` depends on `tamma-api: condition: service_started` -- no change
- `elsa-server` has `TammaApi__BaseUrl: http://tamma-api:3100` -- update port if it was pointing to 3000
- `nginx-proxy` depends on `tamma-api` -- no change
- `oauth2-proxy` depends on `tamma-api` -- no change

---

## 6. Update `docker-compose.prod.yml`

**Remove** the `tamma-api-dotnet` block (lines 65-70).
**Remove** the `tamma-api` block that references TS API resources (lines 73-80).

**Replace** with a single `tamma-api` block:

```yaml
tamma-api:
  restart: unless-stopped
  deploy:
    resources:
      limits:
        cpus: "1.0"
        memory: 1G
```

**Memory budget note**: Removing one service frees 512MB. The consolidated C#
API gets 1G (sum of both old services). Update the header comment's memory
budget table:
- Remove `tamma-api-dotnet: 512M` and `tamma-api: 512M` lines
- Add `tamma-api: 1G`
- Total drops from ~7.1G to ~6.6G

---

## 7. Update `docker-compose.override.yml`

**Remove** the `tamma-api-dotnet` block (lines 24-25):
```yaml
  tamma-api-dotnet:
    ports:
      - "3000:3000"
```

**Update** the `tamma-api` block to expose 3100 (already correct):
```yaml
  tamma-api:
    ports:
      - "3100:3100"
```

Remove the `LOG_LEVEL: debug` env var (TS-specific). For the C# API, use
`Logging__LogLevel__Default: Debug` if needed.

---

## 8. Update C# Dockerfile to Listen on Port 3100

**File**: `apps/tamma-elsa/src/Tamma.Api/Dockerfile`

**Change** (line 48 and 55):
```dockerfile
# Before
EXPOSE 3000
ENV ASPNETCORE_URLS=http://+:3000
HEALTHCHECK ... CMD curl -f http://localhost:3000/health || exit 1

# After
EXPOSE 3100
ENV ASPNETCORE_URLS=http://+:3100
HEALTHCHECK ... CMD curl -f http://localhost:3100/health || exit 1
```

**Rationale**: The consolidated service takes over the `tamma-api` name and port
3100 so that all existing nginx `proxy_pass http://tamma-api:3100` directives
work without change. Docker compose environment `ASPNETCORE_URLS` overrides the
Dockerfile default, but keeping the Dockerfile default at 3100 ensures
standalone usage also works correctly.

---

## 9. Remove `Dockerfile.ts` Target `tamma-api`

**File**: `docker/Dockerfile.ts`

**Remove** Stage 3a entirely (lines 44-61):
```dockerfile
# ---- Stage 3a: API Server ----
FROM node:22-alpine AS tamma-api
...
CMD ["node", "packages/api/dist/serve.js"]
```

**Remove** from Stage 2 (build):
- Remove `--filter @tamma/api` from the `pnpm` build command (line 40)
- Remove `COPY packages/api/package.json packages/api/` from Stage 1 (line 21)

**Keep** Stage 3b (`tamma-engine`) intact. The engine does not import
`@tamma/api`; it communicates with the API over HTTP.

**After**: `Dockerfile.ts` contains only:
- Stage 1: deps
- Stage 2: build
- Stage 3: tamma-engine

---

## 10. Simplify nginx Configuration

**File**: `docker/nginx-proxy.conf.template`

**Current state**: All `proxy_pass` directives already point to
`http://tamma-api:3100`. There are no `tamma-api-dotnet` references in this
file (they were only in `nginx-dashboard.conf`).

**Changes needed**:

1. **Remove split routing**: Since all endpoints are now served by one service,
   simplify the `api.tamma.dev` server block. Remove per-path location blocks
   that differentiated between TS and C# backends.

2. **SSE to SignalR**: If Phase 4 replaced SSE with SignalR WebSockets, update
   the SSE-specific nginx location block:
   ```nginx
   # Before (SSE)
   location ~ ^/api/(engine/events|workflows/.*/events) {
       proxy_pass http://tamma-api:3100;
       proxy_buffering off;
       ...
   }

   # After (SignalR WebSocket)
   location /api/hubs/ {
       proxy_pass http://tamma-api:3100;
       proxy_http_version 1.1;
       proxy_set_header Upgrade $http_upgrade;
       proxy_set_header Connection "upgrade";
       proxy_buffering off;
       ...
   }
   ```

3. **Update comments**: Remove references to "TS API" and "split routing
   between TS and C# backends" in the file header (lines 16-19).

4. **`docker/nginx-dashboard.conf`**: Update line 24:
   ```nginx
   # Before
   proxy_pass http://tamma-api-dotnet:3000/;

   # After
   proxy_pass http://tamma-api:3100/;
   ```

---

## 11. Update CI Workflow: `docker-publish.yml`

**File**: `.github/workflows/docker-publish.yml`

### 11a. Remove `tamma-api` from the TS build matrix

**Before** (line 38):
```yaml
matrix:
  target: [tamma-api, tamma-engine]
```

**After**:
```yaml
matrix:
  target: [tamma-engine]
```

(If `tamma-engine` is the only TS target, the matrix can be replaced with a
single job. However, keeping the matrix allows easy addition of future TS
targets.)

### 11b. Remove `tamma-api-dotnet` from the .NET build matrix

The `tamma-api-dotnet` GHCR image is replaced by a new `tamma-api` image built
from the C# Dockerfile.

**Before** (lines 210-216):
```yaml
matrix:
  include:
    - name: tamma-elsa
      ...
    - name: tamma-api-dotnet
      context: apps/tamma-elsa/src
      dockerfile: apps/tamma-elsa/src/Tamma.Api/Dockerfile
    - name: tamma-studio
      ...
```

**After**:
```yaml
matrix:
  include:
    - name: tamma-elsa
      ...
    - name: tamma-api
      context: apps/tamma-elsa/src
      dockerfile: apps/tamma-elsa/src/Tamma.Api/Dockerfile
    - name: tamma-studio
      ...
```

Note: The image name changes from `tamma-api-dotnet` to `tamma-api`. This
means the GHCR image URL becomes `ghcr.io/meywd/tamma-api` instead of
`ghcr.io/meywd/tamma-api-dotnet`.

### 11c. Update `docker-compose.images.yml` generation

**Before** (lines 397-417):
```yaml
services:
  tamma-api:
    image: ghcr.io/${OWNER}/tamma-api:${IMAGE_TAG}
    build: !reset null
  tamma-engine:
    image: ghcr.io/${OWNER}/tamma-engine:${IMAGE_TAG}
    build: !reset null
  ...
  tamma-api-dotnet:
    image: ghcr.io/${OWNER}/tamma-api-dotnet:${IMAGE_TAG}
    build: !reset null
```

**After**:
```yaml
services:
  tamma-api:
    image: ghcr.io/${OWNER}/tamma-api:${IMAGE_TAG}
    build: !reset null
  tamma-engine:
    image: ghcr.io/${OWNER}/tamma-engine:${IMAGE_TAG}
    build: !reset null
  ...
  # tamma-api-dotnet removed — tamma-api is now the C# image
```

The `tamma-api` image override now points to the C# build (same name, different
content). Because the TS `tamma-api` build job no longer exists, the `tamma-api`
image is produced by the .NET build job.

### 11d. Update deploy `needs`

**Before** (line 288):
```yaml
needs: [build-ts, build-dashboard, build-dotnet]
```

If the TS build matrix now only has `tamma-engine`, keep the `build-ts` dependency.
No change needed here unless the `build-ts` job is renamed.

### 11e. Update deploy step: "Start layer 3"

**Before** (line 585):
```bash
up -d --force-recreate tamma-api-dotnet tamma-api
```

**After**:
```bash
up -d --force-recreate tamma-api
```

### 11f. Update deploy step: "Verify layer 3"

**Before**: Checks both `tamma-api` (port 3100) and `tamma-api-dotnet` (port 3000).

**After**: Check only `tamma-api` on port 3100:
```bash
API_OK=$(docker exec $($COMPOSE ps -q tamma-api | head -1) \
  curl -4sf -o /dev/null http://127.0.0.1:3100/health 2>/dev/null \
  && echo 'ok' || echo 'fail')
echo "Check $i/30: tamma-api=$API_OK"
[ "$API_OK" = 'ok' ] && echo 'Layer 3 healthy' && exit 0
```

### 11g. Update failure log dump

**Before** (line 678): Lists `tamma-api-dotnet` in the loop.

**After**: Remove `tamma-api-dotnet` from the service list.

---

## 12. Update CI Workflow: `deploy.yml`

**File**: `.github/workflows/deploy.yml`

Apply the same changes as `docker-publish.yml`:

1. **`docker-compose.images.yml` generation** (lines 131-148): Remove
   `tamma-api-dotnet` entry. Ensure `tamma-api` points to the C# image.

2. **"Start and verify: APIs" step** (lines 228-237): Remove
   `tamma-api-dotnet` from `up -d` command and health check loop.

3. **"Start: engine + dashboard" step** (line 246): No change (does not
   reference `tamma-api-dotnet`).

4. **Failure log dump** (line 306): Remove `tamma-api-dotnet` from the loop.

---

## 13. Update Post-Deploy Tests

**File**: `docker/post-deploy-tests.sh`

**Current state**: All tests hit `api.tamma.dev` which nginx routes to
`tamma-api:3100`. Since the C# API now sits behind the same name and port,
no URL changes are needed in the test endpoints.

**Changes needed**:

1. **Remove the diagnostics section** that probes `tamma-api-dotnet` (if any).
   Currently the diagnostics section only probes nginx and oauth2-proxy, so no
   changes are needed there.

2. **Update health check expectations** if the C# health endpoint returns a
   different response body. Verify that `curl -f http://localhost:3100/health`
   works in the C# container (it uses `curl`, not `wget`).

3. **Add a Phase 5 validation section**:
   ```bash
   header "Phase 5: Consolidated API"
   test_endpoint "C# API /api/health" "api.tamma.dev" "/api/health" "200"
   test_endpoint "C# API /health (root)" "api.tamma.dev" "/health" "200"
   ```

4. **Verify SignalR hub** (if applicable):
   ```bash
   # SignalR negotiate endpoint returns 200 with connectionId
   test_endpoint "SignalR negotiate" "api.tamma.dev" "/api/hubs/tamma/negotiate" "401"
   ```

5. **Confirm no TS process**: On VPS, add a local-only check:
   ```bash
   if [ "${TARGET}" = "localhost" ]; then
     TS_CONTAINERS=$(docker ps --filter "ancestor=*tamma-api*" --filter "name=tamma-api" --format '{{.Image}}' | grep -c node || true)
     if [ "${TS_CONTAINERS}" -eq 0 ]; then
       PASS=$((PASS + 1)); printf "  PASS  No TS API container running\n"
     else
       FAIL=$((FAIL + 1)); printf "  FAIL  TS API container still running\n"
     fi
   fi
   ```

---

## 14. Update Dashboard API Client Base URLs

**Files**:
- `packages/dashboard/src/services/admin/admin-api-client.ts`
- `packages/dashboard/src/services/knowledge-base/api-client.ts`
- `packages/dashboard/src/services/settings/settings-api-client.ts`
- `packages/dashboard/src/hooks/useAuth.ts`
- `packages/dashboard/src/pages/MyApiKeysPage.tsx`
- `packages/dashboard/src/components/layout/NavHeader.tsx`
- `packages/dashboard/src/pages/AccountPage.tsx`

**Current state**: All API clients use `VITE_API_BASE_URL ?? '/api'` or
hardcoded `/api/*` relative paths. These resolve to the same origin
(`app.tamma.dev` or `api.tamma.dev`) which nginx proxies to `tamma-api:3100`.

**No URL changes needed**. The path prefix `/api/` remains the same. The only
potential change is if the C# API returns different response shapes -- but that
is Phase 4's responsibility, not Phase 5.

**SignalR client** (if added in Phase 4): Verify that
`@microsoft/signalr` `HubConnectionBuilder` uses `/api/hubs/tamma` and that
the dashboard Vite config proxies WebSocket connections correctly in dev mode.

---

## 15. Update Elsa Server Configuration

**File**: `docker/docker-compose.yml`, `elsa-server` service

**Current** (line 92):
```yaml
TammaApi__BaseUrl: http://tamma-api:3100
```

**No change needed**. The C# API keeps the service name `tamma-api` and port
3100. The Elsa server's configuration already points here.

---

## 16. Update `init-fullstack` CLI Command

**File**: `packages/cli/src/commands/init-fullstack.ts`

This command generates a docker-compose template for self-hosted users. Update:

1. **Remove `tamma-api-dotnet` service** (lines 129-148)
2. **Update `tamma-api` service** to use the C# image:
   ```yaml
   tamma-api:
     image: ghcr.io/meywd/tamma-api:latest
     environment:
       ConnectionStrings__DefaultConnection: ...
       ASPNETCORE_URLS: http://+:3100
   ```
3. **Update nginx config template** (line 292): Remove
   `proxy_pass http://tamma-api-dotnet:3000/;` and route all `/api/` to
   `tamma-api:3100`
4. **Remove `TammaServer__Url`** from the generated config (line 134)

---

## 17. Remove TS-Only Dependencies from Root `package.json`

**File**: `package.json` (repo root)

Check if any of these deps exist at root and remove them:
- `fastify`
- `@fastify/cors`
- `@fastify/helmet`
- `@fastify/cookie`
- `@fastify/jwt`
- `@fastify/rate-limit`
- `fastify-plugin`
- `pg`
- `@types/pg`
- `@octokit/rest`
- `@octokit/auth-app`
- `libsodium-wrappers`
- `@types/libsodium-wrappers`

**Current root `package.json`**: Does NOT have these as direct dependencies
(they were scoped to `packages/api/package.json`). After deleting
`packages/api/`, running `pnpm install` will naturally drop them from the
lockfile.

**Action**: Run `pnpm install` and verify the lockfile no longer references
these packages (unless another workspace package still uses them). Check:

```bash
pnpm why fastify
pnpm why pg
pnpm why @octokit/rest
```

If any other package still depends on them (e.g., `@tamma/events` uses `pg`),
leave them. Only remove truly orphaned deps.

Also update the root `typecheck` script in `package.json` (line 24):
```json
// Before
"typecheck": "tsc --build packages/shared packages/platforms packages/providers packages/orchestrator packages/api packages/cli"

// After
"typecheck": "tsc --build packages/shared packages/platforms packages/providers packages/orchestrator packages/cli"
```

---

## 18. Update `pnpm-workspace.yaml` and Lockfile

**File**: `pnpm-workspace.yaml`

**Current**:
```yaml
packages:
  - 'packages/*'
  - 'apps/*'
```

**No change needed**. The workspace glob `packages/*` will naturally exclude the
deleted `packages/api/` since the directory no longer exists. The `pnpm install`
after deletion will clean up the workspace resolution.

**Action**:
```bash
rm -rf packages/api
pnpm install
```

Verify:
```bash
pnpm ls --depth 0 -r | grep @tamma/api
# Should return nothing
```

---

## 19. Update `Dockerfile.ts` Build Stage (Engine-Only)

**File**: `docker/Dockerfile.ts`

Since the API target is removed, simplify the build:

**Stage 1 (deps)**: Remove line 21:
```dockerfile
# Remove this line:
COPY packages/api/package.json packages/api/
```

**Stage 2 (build)**: Update the build filter (line 38-41):
```dockerfile
# Before
RUN pnpm --filter @tamma/shared --filter @tamma/platforms --filter @tamma/providers \
    --filter @tamma/orchestrator --filter @tamma/observability --filter @tamma/events \
    --filter @tamma/api --filter @tamma/cli \
    run build

# After
RUN pnpm --filter @tamma/shared --filter @tamma/platforms --filter @tamma/providers \
    --filter @tamma/orchestrator --filter @tamma/observability --filter @tamma/events \
    --filter @tamma/cli \
    run build
```

**Stage 3a (tamma-api)**: Delete entirely (lines 44-61).

**Stage 3b (tamma-engine)**: Remains unchanged. Renumber comment to "Stage 3".

---

## 20. Update Root `tsconfig` / `typecheck` Script

**File**: `package.json` line 24

```json
// Before
"typecheck": "tsc --build packages/shared packages/platforms packages/providers packages/orchestrator packages/api packages/cli"

// After
"typecheck": "tsc --build packages/shared packages/platforms packages/providers packages/orchestrator packages/cli"
```

If there is a root `tsconfig.json` with project references to `packages/api`,
remove that reference as well.

---

## 21. Archive SQL Migrations

**What**: Move hand-written SQL migrations to an archive directory.

```bash
git mv database/migrations database/migrations-archived
```

EF Core migrations in `apps/tamma-elsa/src/Tamma.Data/Migrations/` are now the
sole source of truth.

**Update CI deploy steps** that run SQL migrations:

**`docker-publish.yml`** (lines 521-528):
```bash
# Before
for f in database/migrations/*.sql; do
  echo "Running migration: $f"
  ...
done

# After
# EF Core migrations run automatically on C# API startup — no manual SQL step.
# Archive: database/migrations-archived/ (historical reference only)
echo "SQL migrations archived. EF Core handles schema via C# API startup."
```

Same change in `deploy.yml` (lines 189-199).

---

## 22. Update Documentation References

Update or add notes in these files:

1. **`docs/architecture.md`**: Update the architecture diagram to show a single
   C# API service. Remove references to "TS API" and "Fastify".

2. **`docs/guides/MONOREPO_SETUP.md`** (line 257): Remove or update the
   `@tamma/api` description.

3. **`wiki/Architecture.md`** (line 16): Update the ASCII diagram that shows
   `@tamma/api`.

4. **`CLAUDE.md`**: Update the repository structure section. Remove
   `packages/api` from the tree. Update the "Development Commands" and
   "Architecture Principles" sections that reference Fastify.

5. **`.dev/findings/local-test-workflow.md`** (line 32): Remove the
   `pnpm test --filter @tamma/api` command.

6. **`apps/wiki-site/src/components/ArchitecturePage.tsx`** (line 134): Update
   the package description from "Fastify REST API" to indicate the package is
   removed and the C# API handles all endpoints.

7. **Story files** in `docs/stories/epic-17/` and `docs/stories/epic-18/` that
   reference `pnpm --filter @tamma/api`: Add a note that these commands are
   historical (pre-Phase 5).

---

## 23. Final Verification Checklist

Execute all checks before merging:

### Build & Install

- [ ] `pnpm install` completes without errors
- [ ] `pnpm ls -r | grep @tamma/api` returns nothing
- [ ] `pnpm build` completes (all remaining packages build)
- [ ] `pnpm typecheck` passes
- [ ] `pnpm test` passes (all remaining Vitest tests)
- [ ] `dotnet test` passes (all C# xUnit tests in `apps/tamma-elsa/`)

### Docker

- [ ] `docker compose -f docker-compose.yml config` validates (no errors)
- [ ] `docker compose -f docker-compose.yml -f docker-compose.prod.yml config` validates
- [ ] `docker compose build tamma-api` builds the C# image successfully
- [ ] `docker compose build tamma-engine` builds the TS engine image successfully
- [ ] `docker compose up -d` starts all services
- [ ] `curl http://localhost:3100/health` returns 200 from the C# API
- [ ] `curl http://localhost:3100/api/health` returns 200
- [ ] No container named `tamma-api-dotnet` exists
- [ ] No container using `Dockerfile.ts` target `tamma-api` exists

### CI/CD

- [ ] `docker-publish.yml` builds: TS matrix has only `tamma-engine`
- [ ] `docker-publish.yml` builds: .NET matrix has `tamma-elsa`, `tamma-api`, `tamma-studio`
- [ ] `docker-compose.images.yml` generation has no `tamma-api-dotnet` entry
- [ ] Deploy steps reference only `tamma-api` (not `tamma-api-dotnet`)
- [ ] Health check in deploy verifies only `tamma-api` on port 3100

### Live Deployment

- [ ] All post-deploy integration tests pass
- [ ] Dashboard loads and authenticates via JWT
- [ ] Elsa Studio loads (oauth2-proxy -> nginx -> elsa-studio)
- [ ] GitHub webhook delivery succeeds (check GitHub App > Recent Deliveries)
- [ ] SignalR real-time updates work in dashboard
- [ ] Engine processes an issue end-to-end
- [ ] `docker ps` shows no `tamma-api-dotnet` or TS-based `tamma-api` container
- [ ] VPS memory usage is lower than before (one fewer container)

### Cleanup

- [ ] `packages/api/` does not exist in the repo
- [ ] `database/migrations/` has been moved to `database/migrations-archived/`
- [ ] No stale `from '@tamma/api'` imports anywhere in the codebase
- [ ] GHCR: old `tamma-api` (TS) images can be deleted after 30 days
- [ ] GHCR: old `tamma-api-dotnet` images can be deleted after 30 days

---

## Task Execution Order

The recommended execution order to minimize risk:

| Step | Task | Depends On | Risk |
|------|------|-----------|------|
| 1 | Pre-flight checks (task 1) | Phases 1-4 done | None |
| 2 | Update C# Dockerfile port to 3100 (task 8) | - | Low |
| 3 | Rewrite CLI commands (tasks 3, 4) | - | Medium |
| 4 | Delete `packages/api/` (task 2) | Task 3 | High |
| 5 | Update `Dockerfile.ts` (tasks 9, 19) | Task 4 | Low |
| 6 | Update root `package.json` + workspace (tasks 17, 18, 20) | Task 4 | Low |
| 7 | Merge Docker services (tasks 5, 6, 7) | Task 2 | High |
| 8 | Update nginx (task 10) | Task 7 | Medium |
| 9 | Update CI workflows (tasks 11, 12) | Tasks 7, 9 | Medium |
| 10 | Update post-deploy tests (task 13) | Task 7 | Low |
| 11 | Update init-fullstack (task 16) | Task 7 | Low |
| 12 | Update Elsa config (task 15) | Task 7 | Low |
| 13 | Archive SQL migrations (task 21) | Task 7 | Low |
| 14 | Update docs (task 22) | All above | None |
| 15 | Final verification (task 23) | All above | None |

---

## Rollback Plan

If Phase 5 causes issues after deployment:

1. `git revert` the Phase 5 commit(s)
2. Re-deploy: the old `docker-compose.yml` with both services will be restored
3. nginx routing rules will re-enable split routing
4. Both TS and C# APIs will run again, traffic splits as before

This is safe because the C# API (from Phases 1-4) can handle all traffic by
itself, and the TS API (if restored) would also still work since the database
schema has not changed.
