# Deployment

This page covers environment variables, the least-privilege app-role runbook, and the optional Redis / Cranl activations. For the overall architecture see [Architecture](Architecture).

## Docker Compose stack

Production deployment runs on Hetzner CPX42 (16 GB). Services (see `docker/docker-compose.yml`; memory column = prod limit from `docker-compose.prod.yml`):

| Service | Tech | Memory | Purpose |
|---------|------|--------|---------|
| postgres | Postgres 17 | 2 GB | Data, events, ELSA state |
| rabbitmq | RabbitMQ 3.13 | 512 MB | Message broker |
| chromadb | Chroma 1.5.8 | 1 GB | Vector store (KB/RAG) |
| ollama | Ollama 0.31.1 | 2 GB | Local embedding server (`nomic-embed-text`, 768-dim) — self-hosted KB/RAG embeddings, no OpenAI key or per-token cost |
| intelligence-server | Node.js 22 (Fastify) | — (no prod limit) | KB/RAG sidecar backing the C# API's `/api/kb/*` endpoints |
| elsa-server | .NET 8 | 1 GB | ELSA workflow engine |
| tamma-api | .NET 8 | 768 MB | Consolidated C# REST API (the `Tamma.Api` surface — the old `tamma-api-dotnet` / Node Fastify split no longer exists) |
| tamma-engine | Node.js 22 | 1 GB | TypeScript engine |
| tamma-dashboard | nginx | 256 MB | React SPA |
| elsa-studio | nginx | 128 MB | Custom Blazor WASM |
| oauth2-proxy | oauth2-proxy v7.7.1 | 128 MB | GitHub OAuth `auth_request` gate |
| nginx-proxy | nginx 1.27 | 128 MB | Reverse proxy |
| opensearch (+ dashboards, opt-in) | 2.19 | 3 GB (+1.5 GB) | Log aggregation (`observability` profile) |

Memory budget (sum of prod limits, not reservations): **~8.8 GB** without the observability profile, **~13.4 GB** with it — fits the 16 GB VPS, but mind headroom when running observability + ollama together.

Deploy is **layered** (postgres + rabbitmq + chromadb → elsa-server → tamma-api — which pulls up ollama + intelligence-server via `depends_on` — → engine + dashboard + studio + proxies) so in-flight upgrades never race their own dependencies. DNS + SSL via Cloudflare (`app.tamma.dev`, `api.tamma.dev`, `elsa.tamma.dev`, `wiki.tamma.dev`).

## Core environment variables

Configuration uses ASP.NET's colon-separated config keys, which translate to `__` (double underscore) in env vars. E.g. `GitHub:AppId` → `GitHub__AppId`.

### Database (required)

| Key | Purpose |
|-----|---------|
| `ConnectionStrings:TammaDb` | **Admin** connection. Superuser role. Used by migrations and background services. |
| `ConnectionStrings:TammaAppDb` | **App** connection. Role `tamma_app` (non-superuser, least-privilege: plain DML on the control plane, no DDL). Per-request `DbContext`s. |
| `ConnectionStrings:DefaultConnection` | Legacy alias. Treated as admin when `TammaDb` is unset. Older configs still boot. |

If `TammaAppDb` is absent in production the API logs a warning and falls through to the admin connection — **the least-privilege role is bypassed until you wire the app-role connection**. This is expected in local dev with a single-role Postgres.

### GitHub App (optional; activates SaaS + Actions executor)

| Key | Purpose |
|-----|---------|
| `GitHub:AppId` | Your GitHub App numeric ID. Required for the real Octokit client. |
| `GitHub:PrivateKey` | PEM-encoded private key for installation-scoped JWT minting. |
| `GitHub:WebhookSecret` | HMAC secret for webhook verification. Fail-closed when unset. |
| `GitHub:ClientId` / `GitHub:ClientSecret` | OAuth app credentials (user sign-in). |

When both `AppId` > 0 and `PrivateKey` are set, DI swaps in `OctokitGitHubAppClient`, `OctokitGitHubActionsClient`, and `OctokitGitHubEngineCallbackService`. Otherwise the Null seams return `github_client_not_configured`. See [GitHub Integration](GitHub-Integration).

### Redis (optional; activates distributed rate limit)

| Key | Purpose |
|-----|---------|
| `ConnectionStrings:Redis` | StackExchange.Redis connection string. |

When set, the API swaps `InMemoryDistributedRateLimitBackend` → `RedisDistributedRateLimitBackend` (Lua `INCR + EXPIRE`). Multi-pod-safe. Unset means single-pod in-process rate limiting (safe default, matches pre-Redis behaviour).

### KB / RAG sidecar embeddings (optional overrides; defaults to local Ollama)

The `intelligence-server` sidecar's embedding config is passed through compose with self-hosted defaults — **no OpenAI key is required**:

| Variable (`docker/.env`) | Default | Purpose |
|-----|---------|---------|
| `INTELLIGENCE_EMBEDDING_PROVIDER` | `ollama` | Embedding provider. Set `openai` (plus an API key) only if a cloud provider is wanted. |
| `INTELLIGENCE_EMBEDDING_MODEL` | `nomic-embed-text` | 768-dim local embedding model, pulled by the `ollama` service on first boot. |
| `INTELLIGENCE_EMBEDDING_BASE_URL` | `http://ollama:11434` | Embedding API endpoint (the in-stack Ollama server). |
| `INTELLIGENCE_LOG_LEVEL` | `info` | Sidecar log level. |

Compose maps these onto the sidecar's own env (`EMBEDDING_PROVIDER` / `EMBEDDING_MODEL` / `EMBEDDING_BASE_URL`); the vector store is fixed to `CHROMADB_URL=http://chromadb:8000`. The sidecar bootstraps its RAG collection (`KB_RAG_COLLECTION`, default `codebase`) at boot — a fresh, never-indexed deployment reports **configured** (retrieval just returns nothing until content is indexed). With no vector-store env at all, the sidecar degrades to its `not_configured` stubs instead of crashing. The `ollama` service has **no host port mapping** — its API is unauthenticated and must stay internal to `tamma-net`; the model persists in the `tamma-ollama-data` volume, so the pull cost is first-boot-only.

### Cranl (optional; activates per-tenant provisioning)

| Key | Purpose |
|-----|---------|
| `Cranl:ApiKey` | Cranl API key. |
| `Cranl:OrganizationId` | Cranl organization ID. |
| `Cranl:BaseUrl` | Override default Cranl API root (optional). |

When both `ApiKey` and `OrganizationId` are set, DI wires `CranlTenantProvisioner` + `CranlProvisioningWorkflow` + the task-queue handler. Otherwise `NullTenantProvisioner` is the default — no external resources are minted and tenant placement stays on the `tenant_databases` pool (central DB by default; every tenant still gets its own `t_<hex>` schema + role).

### Agent dispatch (optional mode override)

| Key | Purpose |
|-----|---------|
| `TAMMA_AGENT_MODE` | `Local` \| `GitHubActions`. Overrides auto-detection. |
| `Agent:ExecutorMode` | Same, via config. `Auto` means "detect via GitHub App presence". |

See [Agent Dispatch](Agent-Dispatch).

### Tenant pre-drop backup (optional; OFF by default)

| Key | Purpose |
|-----|---------|
| `Backup:DeletionBackup` | `true` enables a `pg_dump` snapshot of a tenant DB before `DROP DATABASE` in the delete workflow. Default `false`. |
| `Backup:Directory` | Destination for dump files. **Must be a durable mounted volume** in prod. Default `/var/backups/tamma`. |
| `Backup:PgDumpPath` | Path to `pg_dump`. Default `pg_dump`. |
| `Backup:TimeoutSeconds` | Dump timeout. Default `1800`. |

Runs on the **elsa-server** host. Enabling requires `postgresql-client`
in the elsa-server image (the base image ships only `curl`) plus a mounted
backup volume — see [tenant-deletion-backup.md](https://github.com/Tam-ma/tamma/blob/main/docs/deployment/tenant-deletion-backup.md).
The password is passed via `PGPASSWORD`, never on argv.

### SMTP (required for register / reset emails)

| Key | Purpose |
|-----|---------|
| `Smtp:Host`, `Smtp:Port`, `Smtp:Username`, `Smtp:Password` | MailKit SMTP sender. |
| `Smtp:From` | Sender address for outbox mail. |

### OpenBao / HashiCorp Vault (deferred)

A KEK (key-encryption-key) backend is stubbed but currently env-var-only (Doc 01 §8.2). Vault wiring is deferred to a future story — see `docs/stories/` for Epic 28 when that lands.

## Least-privilege app-role runbook

`tamma_app` is the **least-privilege runtime role** for the control-plane API: plain DML on the control-plane tables, no DDL, no CREATEDB/CREATEROLE. It's active when `ConnectionStrings:TammaAppDb` is set to a connection that authenticates as `tamma_app`. (Tenant isolation itself does not depend on this role — every tenant has its own `t_<hex>` schema + per-tenant Postgres role under the unified tenancy model; the legacy shared-tables RLS layer was removed in unified-tenancy Phase 5.)

### Activation steps

1. **Bootstrap the roles** if you haven't already: `scripts/db/postgres-roles.sql` creates `tamma_admin` / `tamma_provisioner` / `tamma_app` (the three-role privilege split); the EF migration pipeline issues the table-level grants.

2. **Set a password on the `tamma_app` role** (the bootstrap creates the role; the docker-entrypoint hook binds passwords, or set one manually):

    ```sql
    ALTER ROLE tamma_app WITH PASSWORD '<generated-strong-password>';
    ```

3. **Wire the app-role connection string**. In docker-compose env or your orchestrator's secret manager:

    ```
    ConnectionStrings__TammaAppDb=Host=postgres;Database=tamma;Username=tamma_app;Password=${TAMMA_APP_DB_PASSWORD};Pooling=true
    TAMMA_APP_DB_PASSWORD=<generated-strong-password>
    ```

    Keep `ConnectionStrings__TammaDb` on the superuser so migrations still work.

4. **Restart the API**. On boot, `DbRoleLeastPrivilegeCheck` runs `SELECT current_user` against TammaAppDb and refuses readiness (Production) if the API is running as `tamma_provisioner` or `tamma_admin`; if TammaAppDb falls through to admin, a Warning fires.

5. **Verify**: hit a tenant-scoped endpoint as a user from tenant A, then manually query tenant B's schema as tenant A's role — Postgres must refuse (the per-tenant role has no privileges outside its own `t_<hex>` schema).

### What fails closed

- A control-plane DbContext opened with no tenant in scope (e.g. via a forgotten DI configuration) returns an **empty result set** instead of "show all" — EF query filters enforce this.
- A DbContext opened on the admin connection bypasses the least-privilege role; this is intentional (migrations), but means **admin endpoints must do their own authorization**. Never take user input and run it on the admin connection.

## Cranl per-tenant provisioning (optional)

Each tenant can optionally get its own isolated Cranl project + Postgres + Elsa app instead of sharing the central Postgres. Activate by setting `Cranl:ApiKey` + `Cranl:OrganizationId`, then:

```http
POST /api/admin/tenants/{tenantId}/provision
```

This enqueues a `CranlProvisioningWorkflow` task. The workflow:

1. Creates the Cranl project via the Cranl API.
2. Provisions a Postgres DB + admin role within that project.
3. Deploys the Elsa workflow app into the project.
4. Updates the `tenants` row with the Cranl project/db identifiers (columns added in the Cranl-integration migration).
5. Marks the tenant as `Provisioned`.

Teardown: `DELETE /api/admin/tenants/{tenantId}/provision` runs the reverse. If Cranl config is absent, the Null seam returns 501 `cranl_not_configured`.

## CI / CD

GitHub Actions workflows in `.github/workflows/`:

| Workflow | Purpose |
|----------|---------|
| `ci.yml` | Build, lint, test on PRs |
| `deploy.yml` | Deploy to VPS via SSH |
| `docker-publish.yml` | Build and push Docker images to GHCR |
| `docker-smoke-test.yml` | Smoke test Docker Compose stack |
| `release.yml` | Create GitHub releases |
| `tamma-worker.yml` | Template consumed by tenants running the `GitHubActionsExecutor` |
| `codeql.yml` | Code security scanning |

Deploy Action is DNS-stable and idempotent. **The wiki-site deploy Action is not documented here** — leave it alone; it's owned by Epic 25 and already working.

## Related

- [Architecture → Tenancy & Data Isolation](Architecture#tenancy--data-isolation)
- [Security](Security)
- [Agent Dispatch](Agent-Dispatch)
- [GitHub Integration](GitHub-Integration)
