# Installation & Setup

This page is the ground-truth setup guide for running Tamma. It re-bases the old "npm / standalone binary" framing onto how Tamma **actually** ships today: a **Docker Compose** stack (Postgres 17 → RabbitMQ → ELSA engine → APIs → dashboard/nginx) deployed to a Hetzner VPS via a `qa-*`-tag-gated GitHub Actions pipeline.

> The CLI (`tamma start`, `tamma api`, …) is a separate, single-user / standalone entry point covered in [Usage & Configuration](Usage-and-Configuration). This page covers the container stack that runs the SaaS deployment and local full-stack dev.

Related: [Deployment](Deployment) (env-var reference, least-privilege runbook, Redis/Cranl activation) · [Architecture](Architecture) · [API Reference](API-Reference).

## Which Compose files to use

The **deployed** stack is `docker/docker-compose.yml` plus its prod override. Do **not** use `apps/tamma-elsa/docker-compose*.yml` — that is a separate, older self-contained variant (different ports, image names, RabbitMQ tag) and is not what CI deploys.

| File | Role |
|------|------|
| `docker/docker-compose.yml` | Base stack (all services). |
| `docker/docker-compose.override.yml` | Dev overrides — auto-loaded by `docker compose up`; exposes host ports and sets `LOG_LEVEL=debug`. |
| `docker/docker-compose.prod.yml` | Production overrides — per-service memory/CPU limits; exposes only `nginx-proxy` (80/443). |
| `docker/docker-compose.images.yml` | Generated on the VPS at deploy time; pins each service to a GHCR image tag (`sha-<commit>`). |
| `docker/docker-compose.test.yml` | A single throwaway `postgres-test` for integration tests (host port `5433`). |

## Prerequisites

- **Docker Engine** + **Docker Compose v2** (the `docker compose` sub-command, not the legacy `docker-compose` binary). On a fresh VPS the deploy step installs Docker via `curl -fsSL https://get.docker.com | sh`.
- To **run** the stack you only need Docker (images are built by CI and pulled from GHCR).
- To **build images from source** locally you additionally need: **.NET 8 SDK** (all C# services target .NET 8 — `sdk:8.0` / `aspnet:8.0`), **Node.js 22** (`node:22-alpine`), **pnpm 9** (`corepack prepare pnpm@latest`).

There is no pinned Docker/Compose version in the repo; any recent Docker Engine with the Compose v2 plugin works.

## Services in the stack

Service names, images, and internal ports are from `docker/docker-compose.yml`. The "Prod limit" column is from `docker-compose.prod.yml`.

| Service | Image | Purpose | Internal port | Healthcheck | Prod limit |
|---------|-------|---------|---------------|-------------|------------|
| `postgres` | `postgres:17-alpine` | Data, DCB events, ELSA state | 5432 | `pg_isready -U $POSTGRES_USER` | 2G / 2 cpu |
| `rabbitmq` | `rabbitmq:3.13-management-alpine` | Message broker | 5672 / 15672 | `rabbitmq-diagnostics check_running` | 512M / 1 cpu |
| `chromadb` | `chromadb/chroma:1.5.8` | Vector store (RAG) | 8000 | HTTP probe of `/api/v2/heartbeat` | 1G / 1 cpu |
| `intelligence-server` | built (`packages/intelligence-server`) | TS Fastify sidecar for `/api/kb/*` | 4100 | `curl -fsS http://localhost:4100/health` | — |
| `elsa-server` | built (`apps/tamma-elsa/…/Tamma.ElsaServer`) | ELSA .NET 8 workflow engine | 5000 | `curl -f http://localhost:5000/health` | 1G / 1 cpu |
| `elsa-studio` | built (`Tamma.Studio`) | Blazor WASM designer (nginx) | 80 | — | 128M |
| `tamma-api` | built (`Tamma.Api`) | Consolidated **C#** REST API | 3100 | `curl -f http://localhost:3100/api/health` | 768M / 1 cpu |
| `tamma-engine` | built (`docker/Dockerfile.ts`) | Autonomous issue-processing engine (TS) | — | file marker `/tmp/tamma-engine-healthy` | 1G / 1 cpu |
| `tamma-dashboard` | built (`docker/Dockerfile.dashboard`) | React SPA (nginx) | 3001 | `wget -qO- http://127.0.0.1:3001/` | 256M |
| `oauth2-proxy` | `quay.io/oauth2-proxy/oauth2-proxy:v7.7.1` | GitHub OAuth `auth_request` gate | 4180 | disabled (distroless, no shell) | 128M |
| `nginx-proxy` | `nginx:1.27-alpine` | Reverse proxy (TLS, routing) | 80 / 443 | — | 128M |
| `opensearch` | `opensearchproject/opensearch:2.19.0` | Log aggregation (profile `observability`) | 9200 | cluster-health green/yellow | 3G / 2 cpu |
| `opensearch-dashboards` | `…/opensearch-dashboards:2.19.0` | Log viz (profile `observability`) | 5601 | `/api/status` probe | 1.5G |

Network: `tamma-net` (bridge). Volumes: `tamma-pg-data`, `tamma-rmq-data`, `tamma-chroma-data`, `tamma-engine-workdir`, `tamma-os-data`.

Note: `tamma-api` is the **C# .NET 8** API (built from `Tamma.Api/Dockerfile`, port 3100, health `/api/health`). The consolidation onto a single C# API already happened — an older separate `tamma-api-dotnet` service no longer exists.

### Layered startup order

Services start in dependency layers so an in-flight upgrade never races its own dependencies:

```
Layer 1:  postgres  rabbitmq  chromadb          (wait: healthy)
Layer 2:  elsa-server                            (wait: /health)
Layer 3:  tamma-api                              (wait: /api/health)
Layer 4:  tamma-engine  tamma-dashboard  elsa-studio
          oauth2-proxy  nginx-proxy
```

Compose enforces this with `depends_on: { condition: service_healthy }`; the deploy pipeline additionally gates each layer on a health probe before starting the next.

## Environment configuration (`.env`)

Copy `docker/.env.example` to `docker/.env` and fill it in. Config uses ASP.NET colon keys mapped to `__` in env; compose passes the variables below through to the services.

**Required:**

| Variable | Notes |
|----------|-------|
| `POSTGRES_PASSWORD` | Postgres superuser password. Compose fails fast if unset. |
| `GITHUB_APP_ID` | GitHub App numeric ID. |
| `GITHUB_WEBHOOK_SECRET` | HMAC secret for webhook verification (fail-closed when unset). |

**Authentication:**

| Variable | Notes |
|----------|-------|
| `GITHUB_OAUTH_CLIENT_ID` / `GITHUB_OAUTH_CLIENT_SECRET` | GitHub OAuth App for user sign-in (create at `github.com/settings/developers`). |
| `JWT_SECRET` | JWT signing secret — generate with `openssl rand -base64 32`. |
| `OAUTH2_PROXY_COOKIE_SECRET` | Required only when `oauth2-proxy` is enabled; leave empty to skip it. Generate a 32-byte urlsafe base64 value. |
| `ELSA_ADMIN_API_KEY` | Presented as `Authorization: ApiKey <key>` to the ELSA engine. The engine's `AdminApiKeyProvider` accepts **only** the all-zero GUID `00000000-0000-0000-0000-000000000000`. |
| `TAMMA_SERVICE_API_KEY` | Service-to-service key; generate via `POST /api/admin/service-keys` or `tamma admin create-service-key`. |

**URLs:**

| Variable | Default |
|----------|---------|
| `API_BASE_URL` | `https://api.tamma.dev` |
| `DASHBOARD_URL` | `https://app.tamma.dev` |

**Optional (commented in `.env.example`, shown with defaults):** `POSTGRES_USER=tamma`, `POSTGRES_DB=tamma`, `RABBITMQ_USER=tamma`, `RABBITMQ_PASSWORD=tamma`, `LOG_LEVEL=info`, plus the OpenSearch block (`OPENSEARCH_URL`, `OPENSEARCH_ENABLED`, `LOG_INDEX_PREFIX`).

For the full env-var reference — including the least-privilege `TammaAppDb` role, Redis, Cranl provisioning, SMTP, and tenant-backup keys — see [Deployment](Deployment#core-environment-variables). Two variables consumed in production but **absent** from `.env.example`: `TAMMA_APP_DB_PASSWORD` (activates the least-privilege `tamma_app` DB role) and `CRANL_ENCRYPTION_KEY` (only when Cranl provisioning is enabled).

### Database bootstrap

Postgres runs `docker/init-db.sql` on first boot: it enables `uuid-ossp` and creates the `elsa` and `tamma` schemas. The full application schema is **not** in that script — **EF Core migrations run automatically at C# API startup** (`Tamma.Api` calls `Database.Migrate()` on boot), so there is no separate migration step for the Docker path.

## Local full-stack setup

```bash
git clone https://github.com/meywd/tamma.git
cd tamma/docker

cp .env.example .env
# edit .env — at minimum set POSTGRES_PASSWORD, GITHUB_APP_ID,
# GITHUB_WEBHOOK_SECRET, JWT_SECRET

# dev: auto-loads docker-compose.override.yml (host ports exposed).
# add --build the first time / after code changes to build images from source.
docker compose up -d --build

docker compose ps
curl http://localhost:3100/api/health      # tamma-api
curl http://localhost:5000/health          # elsa-server
```

Dev host ports (from `docker-compose.override.yml`): postgres `5432`, rabbitmq `5672`/`15672`, elsa-server `5000`, tamma-api `3100`, tamma-dashboard `3001`, elsa-studio `14000`.

Opt into log aggregation with the `observability` profile (adds OpenSearch, ~5G extra RAM):

```bash
docker compose --profile observability up -d
```

## Production run

On the VPS the stack is launched with the base + prod + generated-images overrides and the shared secrets `.env`:

```bash
docker compose \
  -f docker-compose.yml \
  -f docker-compose.prod.yml \
  -f docker-compose.images.yml \
  --env-file ../.env up -d
```

`docker-compose.prod.yml` applies per-service memory/CPU limits and exposes only `nginx-proxy` on 80/443 — every other service is reachable through the proxy. Budget: ~6.8 GB without observability, ~11.8 GB with it (fits the 16 GB VPS).

## Health checks

| Endpoint | Service | Meaning |
|----------|---------|---------|
| `GET /api/health` | tamma-api (3100) | App liveness — the endpoint the container healthcheck and deploy probes hit. |
| `GET /health` | tamma-api / elsa-server | Full ASP.NET health-check aggregate (DB, KEK cabinet, DB-role). |
| `GET /health/live` | tamma-api | Liveness only (no dependency checks). |
| `GET /health/ready` | tamma-api | Readiness — checks tagged `ready` (Postgres, KEK, least-privilege role). |
| `GET /health` | elsa-server (5000) | ELSA engine health. |
| `GET /health` | intelligence-server (4100) | KB sidecar health. |

Public surface (through nginx): `https://api.tamma.dev/api/health`, `https://app.tamma.dev/`, `https://elsa.tamma.dev/health` (the ELSA `/health` route is proxied unauthenticated).

## Deploy pipeline (VPS)

Deployment is a job inside `.github/workflows/docker-publish.yml`. Key facts:

- **Trigger / gate:** the `deploy` job runs **only** for a pushed **`qa-*` tag** (`if: startsWith(github.ref, 'refs/tags/qa-')`). **Merging to `main` does not deploy** — cut a `qa-*` tag to ship:

  ```bash
  git tag qa-2026-07-02a
  git push origin qa-2026-07-02a
  ```

- **Build:** matrix jobs build and push images to `ghcr.io/<owner>/<service>` — `tamma-api`, `tamma-engine`, `tamma-dashboard`, `elsa-server` (as `tamma-elsa`), `elsa-studio` (as `tamma-studio`), `intelligence-server`. Docker steps retry up to 3× to ride out Docker Hub rate limits.
- **Deploy:** over SSH to the Hetzner **CPX42** (16 GB, amd64) VPS at `204.168.131.39` — writes secrets + `.env` (chmod 600), `rsync`s `docker/`, generates `docker-compose.images.yml` pinning `sha-<commit>` tags, `docker login ghcr.io` + `pull`, then the layered `up -d`, then runs `docker/post-deploy-tests.sh`.
- **Migrations:** none in the pipeline — EF Core migrations apply at C# API startup.

Verify a deploy actually shipped: the `Deploy to VPS` job must be **success** (not skipped). A failed image build skips deploy and the site keeps serving the prior version — **health 200 ≠ freshly deployed**.

`deploy.yml` is a separate manual (`workflow_dispatch`) re-deploy; note it still references a removed `tamma-api-dotnet` service, so treat `docker-publish.yml`'s flow as canonical.

## VPS operations

- **DNS / TLS:** Cloudflare (Full SSL, origin cert on nginx) for `app.tamma.dev`, `api.tamma.dev`, `elsa.tamma.dev`, `logs.tamma.dev`, `wiki.tamma.dev`.
- **`docker/tamma-update.sh`:** `pg_dump` backup → `docker compose pull` → `up -d` → `ps`.
- **`docker/cleanup-cron.sh`:** weekly cron (`0 3 * * 0`) — prunes old images/build cache/volumes, vacuums journald, warns above 80% disk.
- **`docker/post-deploy-tests.sh`:** read-only endpoint smoke tests using `curl --resolve host:443:IP` (SNI-correct) — asserts e.g. `api.tamma.dev/api/health` → 200, unauthenticated admin/webhook routes → 401.

## Related

- [Usage & Configuration](Usage-and-Configuration) — CLI commands, `.tamma/config.json`, operating modes, providers, BYOK.
- [API Reference](API-Reference) — REST surface, SSE, webhooks, RBAC, DCB events.
- [Deployment](Deployment) — env vars, least-privilege runbook, Redis/Cranl.
- [Security](Security) · [GitHub Integration](GitHub-Integration).
