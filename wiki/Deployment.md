# Deployment

This page covers environment variables, the Phase-3 RLS runbook, and the optional Redis / Cranl activations. For the overall architecture see [Architecture](Architecture).

## Docker Compose stack

Production deployment runs on Hetzner CPX42 (16 GB). Services (see `docker-compose.yml`):

| Service | Tech | Memory | Purpose |
|---------|------|--------|---------|
| postgres | 17 | 2 GB | Data, events, ELSA state |
| rabbitmq | Latest | 512 MB | Message broker |
| elsa-server | .NET 8 | 1 GB | ELSA workflow engine |
| tamma-api-dotnet | .NET 8 | 512 MB | .NET REST API (this is the Tamma.Api surface) |
| tamma-api | Node.js 22 | 512 MB | Fastify REST API (legacy, being ported out) |
| tamma-engine | Node.js 22 | 1 GB | TypeScript engine |
| tamma-dashboard | nginx | 256 MB | React SPA |
| elsa-studio | nginx | 128 MB | Custom Blazor WASM |
| nginx-proxy | nginx | 128 MB | Reverse proxy |
| chromadb | Latest | 1 GB | Vector store |
| opensearch (opt-in) | 2.x | 3 GB | Log aggregation |

Deploy is **layered** (postgres → rabbitmq → elsa → APIs → dashboard + nginx) so in-flight upgrades never race their own dependencies. DNS + SSL via Cloudflare (`app.tamma.dev`, `api.tamma.dev`, `elsa.tamma.dev`, `wiki.tamma.dev`).

## Core environment variables

Configuration uses ASP.NET's colon-separated config keys, which translate to `__` (double underscore) in env vars. E.g. `GitHub:AppId` → `GitHub__AppId`.

### Database (required)

| Key | Purpose |
|-----|---------|
| `ConnectionStrings:TammaDb` | **Admin** connection. Superuser role. Used by migrations and background services. |
| `ConnectionStrings:TammaAppDb` | **App** connection. Role `tamma_app` (non-superuser). Per-request `DbContext`s. RLS policies bite here because the role lacks `BYPASSRLS`. |
| `ConnectionStrings:DefaultConnection` | Legacy alias. Treated as admin when `TammaDb` is unset. Pre-Phase-3 configs still boot. |

If `TammaAppDb` is absent in production the API logs a warning and falls through to the admin connection — **RLS is inactive until you wire the app-role connection**. This is expected in local dev with a single-role Postgres.

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

### Cranl (optional; activates per-tenant provisioning)

| Key | Purpose |
|-----|---------|
| `Cranl:ApiKey` | Cranl API key. |
| `Cranl:OrganizationId` | Cranl organization ID. |
| `Cranl:BaseUrl` | Override default Cranl API root (optional). |

When both `ApiKey` and `OrganizationId` are set, DI wires `CranlTenantProvisioner` + `CranlProvisioningWorkflow` + the task-queue handler. Otherwise `NullTenantProvisioner` is the default — every tenant stays on the shared central Postgres via RLS.

### Agent dispatch (optional mode override)

| Key | Purpose |
|-----|---------|
| `TAMMA_AGENT_MODE` | `Local` \| `GitHubActions`. Overrides auto-detection. |
| `Agent:ExecutorMode` | Same, via config. `Auto` means "detect via GitHub App presence". |

See [Agent Dispatch](Agent-Dispatch).

### SMTP (required for register / reset emails)

| Key | Purpose |
|-----|---------|
| `Smtp:Host`, `Smtp:Port`, `Smtp:Username`, `Smtp:Password` | MailKit SMTP sender. |
| `Smtp:From` | Sender address for outbox mail. |

### OpenBao / HashiCorp Vault (deferred)

A KEK (key-encryption-key) backend is stubbed but currently env-var-only (Doc 01 §8.2). Vault wiring is deferred to a future story — see `docs/stories/` for Epic 28 when that lands.

## Phase-3 RLS runbook

Phase-3 is the **dual-connection row-level-security** layer. It's active when `ConnectionStrings:TammaAppDb` is set to a connection that authenticates as the `tamma_app` role.

### Activation steps

1. **Migrate to the Phase-2 schema** if you haven't already. The `admin-db` Phase-1/2 migrations create the `tamma_app` role, the RLS policies, and the `SET LOCAL app.current_tenant_id` usage pattern.

2. **Set a password on the `tamma_app` role** (the migration creates the role but leaves authentication to the operator):

    ```sql
    ALTER ROLE tamma_app WITH PASSWORD '<generated-strong-password>';
    ```

3. **Wire the app-role connection string**. In docker-compose env or your orchestrator's secret manager:

    ```
    ConnectionStrings__TammaAppDb=Host=postgres;Database=tamma;Username=tamma_app;Password=${TAMMA_APP_DB_PASSWORD};Pooling=true
    TAMMA_APP_DB_PASSWORD=<generated-strong-password>
    ```

    Keep `ConnectionStrings__TammaDb` on the superuser so migrations still work.

4. **Restart the API**. On boot, if the app-role connection is reachable, Serilog logs confirm RLS is active; if it falls through to admin, a Warning fires with "RLS will be inactive until the app-role connection is wired".

5. **Verify**: hit a tenant-scoped endpoint as a user from tenant A, then manually query `tenant_b`'s data. EF query filters + Postgres RLS policies must both refuse — defence in depth. The interceptor runs `SET LOCAL app.current_tenant_id = '<tenantA>'` at the start of every request's first query.

### What fails closed

- A DbContext opened with no tenant in scope (e.g. via a forgotten DI configuration) returns an **empty result set** instead of "show all" — query filters enforce this.
- A DbContext opened on the admin connection still bypasses RLS; this is intentional (migrations), but means **admin endpoints must do their own authorization**. Never take user input and run it on the admin connection.

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
