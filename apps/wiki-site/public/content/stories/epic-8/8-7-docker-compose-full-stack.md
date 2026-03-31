---
title: "Story 8-7: Docker Compose Full Stack"
sidebar:
  order: 80
---

## User Story

As a **platform operator**,
I want a Docker Compose configuration that runs the entire Tamma platform with a single command,
So that I can deploy and manage all services (engine, APIs, ELSA, database, message queue, dashboard) as a cohesive unit.

## Priority

P1 - Required for Tier 3 distribution

## Acceptance Criteria

1. `docker/docker-compose.yml` defines all 7 services: postgres, rabbitmq, elsa-server, tamma-api-dotnet, tamma-api, tamma-engine, tamma-dashboard
2. All services join a single bridge network (`tamma-net`) with DNS-based service discovery
3. Health checks defined for all services with appropriate intervals and start periods
4. Startup order enforced via `depends_on` with `condition: service_healthy` for infrastructure dependencies
5. Named volumes for persistent data: `tamma-pg-data`, `tamma-rmq-data`, `tamma-elsa-storage`, `tamma-engine-workdir`
6. `.env.example` documents all configuration variables with sensible defaults and clear required/optional labels
7. `docker-compose.override.yml` provides development defaults (exposed ports, debug logging)
8. `docker-compose.prod.yml` provides production overrides (resource limits, replicas, restricted ports)
9. `docker compose up -d` starts all services; all become healthy within 3 minutes
10. `.NET WorkflowSyncService` configured to sync ELSA state to TS API via `TammaServer__Url=http://tamma-api:3100`
11. `_FILE` suffix convention supported in CLI config loader for Docker secret file injection (e.g., `GITHUB_TOKEN_FILE=/run/secrets/github_token`)

## Technical Design

### Service Port Mapping

| Port | Service | Purpose |
|------|---------|---------|
| 3000 | tamma-api-dotnet | .NET REST API |
| 3001 | tamma-dashboard | Web dashboard (nginx) |
| 3100 | tamma-api | TypeScript Fastify API |
| 5000 | elsa-server | ELSA workflow engine |
| 5432 | postgres | PostgreSQL (dev only) |
| 5672 | rabbitmq | AMQP (dev only) |
| 15672 | rabbitmq | Management UI (dev only) |

### Startup Order

```
Level 0: postgres, rabbitmq
Level 1: elsa-server (depends: postgres, rabbitmq)
Level 2: tamma-api-dotnet (depends: elsa-server, postgres)
Level 3: tamma-api (depends: postgres)
Level 4: tamma-engine (depends: tamma-api), tamma-dashboard (depends: tamma-api)
```

### Resource Limits (Production)

| Service | CPU | Memory | Replicas |
|---------|-----|--------|----------|
| postgres | 2.0 | 2 GB | 1 |
| rabbitmq | 1.0 | 1 GB | 1 |
| elsa-server | 1.0 | 1 GB | 2 |
| tamma-api-dotnet | 0.5 | 512 MB | 2 |
| tamma-api | 0.5 | 512 MB | 2 |
| tamma-engine | 1.0 | 1 GB | 1 |
| tamma-dashboard | 0.25 | 256 MB | 2 |

### Docker Secrets Support

```typescript
// In config.ts loadConfig():
function readSecretFile(envVar: string): string | undefined {
  const filePath = process.env[`${envVar}_FILE`];
  if (filePath) return readFileSync(filePath, 'utf-8').trim();
  return process.env[envVar];
}
```

## Dependencies

- **Prerequisite**: Story 8-6 (Dockerfiles must exist)
- **Blocks**: Story 8-8 (CI needs compose for smoke tests)

## Testing Strategy

1. **Compose up**: Verify all services start and become healthy
2. **Connectivity**: Test cross-service HTTP calls (API → ELSA, WorkflowSync → TS API)
3. **Persistence**: `down && up` preserves Postgres data
4. **Env vars**: Verify `.env` changes are picked up after `up -d`
5. **Production**: Verify `docker compose -f docker-compose.yml -f docker-compose.prod.yml up` applies resource limits

## Estimated Effort

2-3 days

## Files Created/Modified

| File | Action |
|------|--------|
| `docker/docker-compose.yml` | Create |
| `docker/docker-compose.override.yml` | Create |
| `docker/docker-compose.prod.yml` | Create |
| `docker/.env.example` | Create |
| `docker/init-db.sql` | Create (copy from ELSA) |
| `packages/cli/src/config.ts` | Modify (add `_FILE` suffix support) |
| `apps/tamma-elsa/src/Tamma.Api/appsettings.json` | Modify (parameterize TammaServer:Url) |

## Logging Requirements

Container and CI/CD components MUST log structured output for container orchestration observability.

- **INFO**: Container started (image version, config), health check endpoints responding, CI pipeline stage completed
- **WARN**: Container resource limits approaching, health check degraded, build cache miss
- **ERROR**: Container failed to start, health check failed, CI pipeline step failed
- **Format**: All container logs MUST output structured JSON (one JSON object per line) for log aggregation compatibility (ELK, Loki, CloudWatch)
