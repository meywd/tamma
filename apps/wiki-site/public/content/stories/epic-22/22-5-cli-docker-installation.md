---
title: "Story 22.5: CLI Docker Installation & Lifecycle Management"
sidebar:
  order: 220
---

Status: ready-for-dev

## Story

As a **developer using Tamma CLI**,
I want `tamma install` to pull and start all required infrastructure containers (PostgreSQL, ELSA, API, Dashboard, Studio, OpenSearch) on my local machine,
so that I have a complete self-hosted Tamma platform running locally without needing to understand Docker or configure services manually.

## Acceptance Criteria

1. `tamma install` detects Docker availability, pulls all required images, and starts the full stack via Docker Compose
2. `tamma start` starts all containers if stopped, then begins issue processing
3. `tamma stop` stops issue processing but keeps containers running
4. `tamma down` stops and removes all containers (data volumes preserved)
5. `tamma status` shows health of all containers (PostgreSQL, ELSA, API, Dashboard, nginx) with green/red indicators
6. `tamma logs [service]` tails logs from all or a specific service
7. `tamma update` pulls latest Docker images and recreates containers
8. All services accessible via localhost: Dashboard at `localhost:3000`, API at `localhost:3100`, ELSA Studio at `localhost:5000`
9. A bundled `docker-compose.local.yml` is generated during install with local-mode overrides (port mappings, no TLS, no Cloudflare)
10. Data persists across `tamma stop` / `tamma start` cycles via Docker volumes
11. `tamma install --with-observability` optionally includes OpenSearch + Dashboards
12. Works on macOS, Linux, and Windows (WSL2) — Docker Desktop or Docker Engine
13. First-time install includes the init wizard (API keys, GitHub token, repo selection)
14. `tamma uninstall` removes containers, images, and optionally volumes

## Technical Context

### Files to Create

| File | Purpose |
|---|---|
| `packages/cli/src/commands/install.tsx` | Install command — Docker detection, image pull, compose up |
| `packages/cli/src/commands/down.tsx` | Tear down containers |
| `packages/cli/src/commands/status.tsx` | Show container health |
| `packages/cli/src/commands/logs.tsx` | Tail service logs |
| `packages/cli/src/commands/update.tsx` | Pull latest images, recreate |
| `packages/cli/src/commands/uninstall.tsx` | Remove everything |
| `packages/cli/src/docker/compose-manager.ts` | Wrapper around `docker compose` CLI |
| `packages/cli/src/docker/health-checker.ts` | Poll container health endpoints |
| `packages/cli/src/docker/docker-compose.local.yml` | Bundled compose for local mode |

### Files to Modify

| File | Change |
|---|---|
| `packages/cli/src/index.ts` | Register new commands |
| `packages/cli/src/commands/start.tsx` | Add container lifecycle before engine start |
| `docker/docker-compose.yml` | Ensure all services have sensible defaults for local mode |

### docker-compose.local.yml

Overrides for local development:
```yaml
services:
  nginx-proxy:
    ports:
      - "3000:80"      # Dashboard
      - "3100:3100"    # API (direct, no proxy)
      - "5000:5000"    # ELSA Studio
    # No TLS — localhost doesn't need certs
    volumes:
      - ./nginx-local.conf:/etc/nginx/conf.d/default.conf:ro

  postgres:
    ports:
      - "5432:5432"    # Direct access for debugging

  opensearch:
    profiles: []       # Remove profile gate — always start if --with-observability
    ports:
      - "9200:9200"

  opensearch-dashboards:
    profiles: []
    ports:
      - "5601:5601"

  oauth2-proxy:
    profiles: ["disabled"]  # Not needed locally — no multi-domain auth
```

### ComposeManager

```typescript
class ComposeManager {
  constructor(private composePath: string, private projectName: string) {}

  async pull(): Promise<void>           // docker compose pull
  async up(services?: string[]): Promise<void>   // docker compose up -d
  async down(removeVolumes?: boolean): Promise<void>
  async ps(): Promise<ContainerStatus[]> // parse docker compose ps --format json
  async logs(service?: string, follow?: boolean): Promise<void>
  async exec(service: string, command: string[]): Promise<string>
  async isDockerAvailable(): Promise<boolean>
}
```

## Implementation Notes

1. **Docker detection**: Check `docker --version` and `docker compose version`. If Docker not found, show install instructions for the user's OS.
2. **Image pull progress**: Show pull progress using Ink components (spinner per image)
3. **Health check polling**: After `docker compose up`, poll health endpoints every 2s until all services healthy or 120s timeout
4. **Compose file bundling**: The `docker-compose.local.yml` is bundled into the CLI npm package. At install time, it's written to `~/.tamma/docker/`
5. **Port conflicts**: Before starting, check if ports 3000/3100/5000/5432 are available. Show which process is using them if occupied.
6. **First install flow**: `tamma install` → Docker check → pull images → start containers → wait healthy → run `tamma init` wizard → done
7. **Data directory**: `~/.tamma/data/` for PostgreSQL volumes, `~/.tamma/logs/` for log buffers

## Dependencies

- Story 22-1 (IAgentExecutor) — determines how the engine dispatches to local agents
- Story 22-2 (Standalone workflow engine) — the engine that runs after containers start

## Estimated Effort

5 days

## Logging Requirements

| Event | Level | Properties |
|---|---|---|
| Docker detected | INFO | {DockerVersion}, {ComposeVersion}, {OS} |
| Image pull started | INFO | {ImageName}, {Tag} |
| Image pull completed | INFO | {ImageName}, {DurationMs} |
| Container started | INFO | {ServiceName}, {ContainerId} |
| Container health check | DEBUG | {ServiceName}, {Status}, {AttemptNumber} |
| All services healthy | INFO | {TotalStartupMs}, {ServiceCount} |
| Container failed to start | ERROR | {ServiceName}, {ExitCode}, {Logs} |
| Port conflict detected | WARN | {Port}, {ProcessName}, {ProcessPid} |
