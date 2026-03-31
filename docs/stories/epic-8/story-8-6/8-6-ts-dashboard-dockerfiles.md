# Story 8-6: TypeScript & Dashboard Dockerfiles

## User Story

As a **DevOps engineer**,
I want Dockerfiles for the TypeScript engine, API server, and dashboard,
So that the full Tamma platform can be deployed as containers alongside the existing ELSA services.

## Priority

P1 - Required for Tier 3 distribution

## Acceptance Criteria

1. Multi-stage `docker/Dockerfile.ts` builds both `tamma-api` and `tamma-engine` targets from the monorepo using Docker build targets
2. Stage 1 (deps): Installs pnpm, copies lockfile + all workspace package.json files, runs `pnpm install --frozen-lockfile`
3. Stage 2 (build): Copies source, runs `pnpm run build`, prunes dev dependencies with `pnpm prune --prod`
4. Stage 3a (tamma-api): Node 22 Alpine + tini, runs `tamma server --port 3100 --host 0.0.0.0`, health check via wget
5. Stage 3b (tamma-engine): Node 22 Alpine + tini + git, runs `tamma start --mode service`, health check via file sentinel
6. `docker/Dockerfile.dashboard` builds the Vite React SPA and serves via nginx with API proxy
7. `docker/nginx-dashboard.conf` configures SPA fallback routing, `/api/` proxy to tamma-api, `/elsa-api/` proxy to tamma-api-dotnet, and static asset caching
8. All images use non-root users (tamma UID 1001, nginx)
9. All images build successfully: `docker build --target tamma-api -f docker/Dockerfile.ts .` and `docker build -f docker/Dockerfile.dashboard .`
10. Image sizes: TS images < 300MB, Dashboard < 30MB

## Technical Design

### Dockerfile.ts Architecture

```
node:22-alpine AS deps      → pnpm install (layer cached)
deps AS build               → copy source, tsc build, prune prod
node:22-alpine AS tamma-api → copy from build, EXPOSE 3100, CMD server
node:22-alpine AS tamma-engine → copy from build + git, CMD start --mode service
```

### Dashboard Architecture

```
node:22-alpine AS build     → pnpm install, vite build
nginx:1.27-alpine AS runtime → copy dist, nginx config
```

### nginx Config

```nginx
server {
    location / { try_files $uri $uri/ /index.html; }        # SPA fallback
    location /api/ { proxy_pass http://tamma-api:3100; }     # TS API proxy
    location /elsa-api/ { proxy_pass http://tamma-api-dotnet:3000/; }  # .NET proxy
    location ~* \.(js|css|png) { expires 1y; }               # Static caching
}
```

### Engine Headless Mode

Add `--mode service` flag to `tamma start` that:
- Skips Ink TUI rendering (no TTY in container)
- Uses `auto` approval mode
- Writes `/tmp/tamma-engine-healthy` sentinel on successful init
- Logs to stdout in structured JSON format

## Dependencies

- **Prerequisite**: None (can start in parallel with Tier 1/2)
- **Blocks**: Story 8-7 (compose needs Dockerfiles), Story 8-8 (CI needs Dockerfiles)

## Testing Strategy

1. **Build**: Verify all images build without errors
2. **Size**: Assert image sizes in CI
3. **Security**: Verify non-root user with `docker inspect`
4. **API**: Start tamma-api container, `curl /api/health` returns 200
5. **Dashboard**: Start dashboard container, `curl /` returns HTML

## Estimated Effort

2-3 days

## Files Created/Modified

| File | Action |
|------|--------|
| `docker/Dockerfile.ts` | Create |
| `docker/Dockerfile.dashboard` | Create |
| `docker/nginx-dashboard.conf` | Create |
| `packages/cli/src/commands/start.tsx` | Modify (add `--mode service` flag) |

## Logging Requirements

Container and CI/CD components MUST log structured output for container orchestration observability.

- **INFO**: Container started (image version, config), health check endpoints responding, CI pipeline stage completed
- **WARN**: Container resource limits approaching, health check degraded, build cache miss
- **ERROR**: Container failed to start, health check failed, CI pipeline step failed
- **Format**: All container logs MUST output structured JSON (one JSON object per line) for log aggregation compatibility (ELK, Loki, CloudWatch)
