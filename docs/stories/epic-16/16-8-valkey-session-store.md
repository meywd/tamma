# Story 16.8: Valkey Session & Rate-Limit Store

Status: ready-for-dev

## Story

As a **platform operator**,
I want a Valkey instance backing oauth2-proxy sessions and API rate limiting,
so that rate-limit counters survive API restarts, oauth2-proxy sessions persist across container recreations, and the platform is ready for horizontal scaling with shared state.

## Background

Currently:
- **oauth2-proxy** uses cookie-based sessions (no server-side store). Session data is encrypted in the cookie itself. This works but limits session revocation and increases cookie size.
- **@fastify/rate-limit** uses in-memory storage. Counters reset on every API restart, and if we scale to multiple API replicas, each has independent counters (no shared enforcement).

Redis was the standard choice, but Redis switched to SSPL license in 2024. **Valkey** is the community fork under BSD 3-clause license, backed by AWS, Google, and the Linux Foundation. It's a drop-in replacement with identical wire protocol.

## Acceptance Criteria

1. A `valkey` service runs in docker-compose using `valkey/valkey:8-alpine` image
2. Valkey is on the `tamma-net` Docker network with a healthcheck (`valkey-cli ping`)
3. Valkey has resource limits: 0.5 CPU, 256MB memory (sufficient for sessions + rate counters)
4. Valkey data is persisted to a named Docker volume (`tamma-valkey-data`)
5. oauth2-proxy is configured to use Valkey as its session store via `--session-store-type=redis --redis-connection-url=redis://valkey:6379`
6. `@fastify/rate-limit` in tamma-api is configured with a Redis/Valkey store via `@fastify/rate-limit`'s `redis` option using `ioredis`
7. Rate-limit counters persist across tamma-api restarts
8. oauth2-proxy sessions persist across oauth2-proxy container recreations
9. Valkey is optional — if `VALKEY_URL` env var is empty, oauth2-proxy falls back to cookie sessions and rate-limit falls back to in-memory (current behavior)
10. Production resource budget updated in docker-compose.prod.yml comments
11. `.env.example` documents `VALKEY_URL` with default `redis://valkey:6379`
12. Deploy workflows write `VALKEY_URL` to VPS `.env`
13. Unit tests verify rate-limit store configuration (Valkey vs in-memory based on env)

## Technical Context

### Valkey vs Redis

Valkey is the Linux Foundation fork of Redis, created after Redis switched from BSD to SSPL in 2024. Valkey 8.x is wire-compatible with Redis 7.x — all Redis clients (ioredis, redis, etc.) work unchanged. The Docker image is `valkey/valkey:8-alpine`.

### oauth2-proxy Redis/Valkey Support

oauth2-proxy natively supports Redis as a session store. Configuration:
```ini
session_store_type = "redis"
redis_connection_url = "redis://valkey:6379"
```

This stores session data server-side in Valkey instead of encrypting it into the cookie. Benefits: smaller cookies, server-side session revocation, shared sessions across oauth2-proxy replicas.

### @fastify/rate-limit Redis/Valkey Support

The `@fastify/rate-limit` plugin accepts a `redis` option with an ioredis client:
```typescript
import Redis from 'ioredis';

await app.register(rateLimitPlugin, {
  max: 100,
  timeWindow: '1 minute',
  redis: new Redis(process.env['VALKEY_URL'] || 'redis://valkey:6379'),
});
```

### Memory Budget Impact

Adding Valkey (256MB limit) to the current budget:
- Core services: ~7.2G → ~7.5G
- With observability: ~11.7G → ~12.0G
- Free on CPX42 (16GB): ~2.0G remaining

Fits within current hardware with comfortable headroom.

### Files to Create

| File | Purpose |
|------|---------|
| None | Valkey uses the official Docker image, no custom code |

### Files to Modify

| File | Change |
|------|--------|
| `docker/docker-compose.yml` | Add `valkey` service |
| `docker/docker-compose.prod.yml` | Add resource limits for valkey |
| `docker/oauth2-proxy.cfg` | Add `session_store_type` and `redis_connection_url` (conditional on env) |
| `docker/.env.example` | Add `VALKEY_URL` |
| `packages/api/src/routes/users/index.ts` | Configure @fastify/rate-limit with ioredis when VALKEY_URL is set |
| `.github/workflows/deploy.yml` | Add `VALKEY_URL` to .env writing |
| `.github/workflows/docker-publish.yml` | Same |
| `package.json` or `packages/api/package.json` | Add `ioredis` dependency |

## Implementation Plan

### Step 1: Add Valkey to Docker Compose

```yaml
valkey:
  image: valkey/valkey:8-alpine
  restart: unless-stopped
  command: ["valkey-server", "--maxmemory", "200mb", "--maxmemory-policy", "allkeys-lru"]
  volumes:
    - tamma-valkey-data:/data
  healthcheck:
    test: ["CMD", "valkey-cli", "ping"]
    interval: 10s
    timeout: 5s
    retries: 5
  networks:
    - tamma-net
```

### Step 2: Configure oauth2-proxy

Add to `docker/oauth2-proxy.cfg`:
```ini
# Session store — use Valkey if available, otherwise cookie-based
session_store_type = "redis"
redis_connection_url = "redis://valkey:6379"
```

Update docker-compose to make oauth2-proxy depend on valkey:
```yaml
oauth2-proxy:
  depends_on:
    valkey:
      condition: service_healthy
```

### Step 3: Configure @fastify/rate-limit with ioredis

```typescript
import Redis from 'ioredis';

const valkeyUrl = process.env['VALKEY_URL'];
const redisClient = valkeyUrl ? new Redis(valkeyUrl) : undefined;

await app.register(rateLimitPlugin, {
  max: 30,
  timeWindow: '1 minute',
  redis: redisClient,
});
```

### Step 4: Update deploy workflows and .env.example

Add `VALKEY_URL=redis://valkey:6379` to the .env file written by deploy workflows.

## Testing Strategy

### Unit Tests

1. Rate-limit store uses ioredis when VALKEY_URL is set
2. Rate-limit store falls back to in-memory when VALKEY_URL is empty
3. Valkey service healthcheck passes in docker-compose

### Integration Tests

1. docker compose up with valkey → oauth2-proxy uses Valkey sessions (verify via `valkey-cli KEYS *`)
2. Restart tamma-api → rate-limit counters persist (hit limit, restart, still limited)
3. Restart oauth2-proxy → user sessions survive (login, restart proxy, still logged in)

### Manual Verification

1. `docker exec valkey valkey-cli INFO memory` — verify memory usage < 200MB
2. `docker exec valkey valkey-cli DBSIZE` — verify keys are being stored

## Dependencies

- **Story 16.1** (oauth2-proxy) — oauth2-proxy must be deployed first
- **Story 16.2** (rate limiting) — rate-limit configuration exists

## Estimated Effort

| Task | Hours |
|------|-------|
| Docker compose + prod limits | 1 |
| oauth2-proxy Valkey config | 1 |
| ioredis + rate-limit wiring | 2 |
| Deploy workflow updates | 0.5 |
| Tests | 1.5 |
| **Total** | **6 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-14 | 1.0 | Initial story creation | Architecture Team |
