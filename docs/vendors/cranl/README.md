# Cranl.com — Tenant Provisioning Backend

Source: https://docs.cranl.com/ (fetched 2026-04-19)

## Role in Tamma architecture

**Central Tamma** (Hetzner VPS) = control plane: auth, orgs, tenant registry, routing.
**Cranl per tenant** = data + compute plane:
- 1 Cranl **Project** per tenant (container)
- 1 Cranl **Database** (Postgres) per tenant
- 1 Cranl **Application** (Elsa engine, deployed from Tamma's GitHub repo) per tenant

Central plane stores `tenants.cranl_project_id`, `cranl_database_id`, `cranl_app_id`, `cranl_region`, and the encrypted connection string. All tenant-scoped API calls route to the tenant's Cranl-assigned Elsa host.

## API basics

- **Base URL**: `https://app.cranl.com/api`
- **Auth**: `Authorization: Bearer cranl_sk_<32 chars>` (tamma stores 1 org-scoped key, not per-tenant)
- **Rate limit**: 120 req/min per key
- **Format**: JSON; errors `{ "error": "..." }`; status codes 200/400/401/403/404/429/500

## Core endpoints (control plane will call)

### Projects (tenant container)
- `POST /api/projects` body `{ name, organizationId }` → `{ id, name, organization_id }`
- `GET /api/projects` list
- `GET /api/projects/:id`
- `PUT /api/projects/:id` body `{ name }`
- `DELETE /api/projects/:id` (requires no apps; delete resources first)

### Databases (tenant DB)
- `POST /api/databases` body `{ name, projectId, type: "postgresql", serverId?, description? }` → `{ id, name, type, status: "pending" }`. Passwords auto-generated.
- `GET /api/databases/:id` returns details including connection information (CLI `cranl db info` surfaces: Host, User, Database, Connection string `postgresql://admin:pass@host:5432/mydb`)
- `PATCH /api/databases/:id` body `{ name?, description? }`
- `DELETE /api/databases/:id`
- `POST /api/databases/:id/:action` action ∈ `start|stop|reload|rebuild|deploy`

### Applications (tenant Elsa engine)
- `POST /api/applications` body `{ name, projectId, repositoryId, branch?, buildType: "nixpacks"|"dockerfile", serverId?, buildPath?, description? }` → `{ id, name, status: "pending" }`
- `GET /api/applications/:id`
- `DELETE /api/applications/:id` (removes app + DNS + CDN)
- `POST /api/applications/:id/deploy` → `{ id, status: "deploying" }`
- `POST /api/applications/:id/lifecycle` body `{ action: "start"|"stop"|"reload"|"rebuild" }`
- `GET /api/applications/:id/environment` → `{ env: "KEY=VALUE\n..." }`
- `PUT /api/applications/:id/environment` body `{ env: "..." }` — **replaces** all env vars
- `GET /api/applications/:id/deployments` → history
- `GET /api/applications/:id/deployments/:deploymentId/logs` — JSON once done, SSE in-progress
- `GET /api/applications/:id/domains` → `{ domains: [...], defaultDomain: "<app>-<id>.cranl.net" }`
- `POST /api/applications/:id/domains/custom` body `{ host }` — custom domain + free SSL

### Regions
Server IDs known from MCP docs:
- `germany-1`, `us-east-1`, `saudi-arabia-1`, `egypt-1`, `india-1` (available)
- `turkey-1`, `uae-1`, `singapore-1`, `japan-1` (coming soon)
- MENA regions require Pro/Enterprise plan

CLI aliases: `eu`, `us`, `mena`, `egypt`, `asia`

## Plan constraints (affects provisioning strategy)

| Plan | Apps+DBs combined | App resources | Custom domains |
|------|------------------:|---------------|---------------:|
| Basic | 3 | 2GB DDR5, 2 vCPU | 1 |
| Pro | 20 | 4GB DDR5, 4 vCPU | 10 |
| Enterprise | Unlimited | — | Unlimited |

→ **One Cranl account = one tenant ceiling at Pro is ~10 tenants** (1 DB + 1 app per tenant = 2 resources × 10 = 20). Need Enterprise or multiple Cranl accounts for scale.

## Provisioning flow per new tenant

```
1. POST /api/projects { name: "tamma-tenant-<uuid>", organizationId: <tamma-cranl-org> }
   → projectId

2. POST /api/databases {
     name: "tamma-<uuid>",
     projectId,
     type: "postgresql",
     serverId: <region>
   }
   → databaseId, status=pending

3. Poll GET /api/databases/:id until status=running
   → extract connection string (host, user, pass, db)
   → encrypt and store on tenants.cranl_database_url

4. POST /api/applications {
     name: "tamma-engine-<uuid>",
     projectId,
     repositoryId: <tamma-repo-id-in-cranl>,
     branch: "main",
     buildType: "dockerfile",
     serverId: <region>,
     buildPath: "/apps/tamma-elsa"
   }
   → appId, status=pending

5. PUT /api/applications/:appId/environment body {
     env: "DATABASE_URL=<connstring>\n
           TAMMA_CONTROL_PLANE_URL=https://api.tamma.dev\n
           TAMMA_TENANT_ID=<uuid>\n
           TAMMA_SHARED_SECRET=<hmac>\n..."
   }

6. POST /api/applications/:appId/deploy
7. Poll GET /api/applications/:appId until status=running
8. GET /api/applications/:appId/domains
   → store defaultDomain on tenants.cranl_app_url

Store all IDs on tenants row. Done.
```

## Gaps / assumptions that need validation

1. **Connection string retrieval**: docs show `GET /api/databases/:id` returns minimal fields in the example; the CLI implies richer data. Confirm via live API call that the full response includes `connection` / `host` / `username` / `password` fields.
2. **Repository ID**: `POST /api/applications` needs a `repositoryId` — this is Cranl's internal repo ID, obtained by syncing GitHub via their app (`cranl github repos`). We either pre-sync the Tamma repo once per Cranl account, or need a GitHub-repo-lookup endpoint.
3. **No organization creation endpoint**: a Tamma Cranl account is presumably a human-created Cranl org. Mapping strategy: one Cranl org per Tamma operator identity (not per tenant).
4. **Polling vs webhooks**: docs don't mention webhooks for provisioning completion. We poll status.
5. **Teardown on tenant delete**: must sequence `DELETE /apps/:id` → `DELETE /dbs/:id` → `DELETE /projects/:id`.

## Raw RST sources

Captured in sibling files:
- `api-reference.rst` — base URL, auth, status codes, rate limits
- `api-authentication.rst` — API key format, verify endpoint
- `api-projects.rst` — project endpoints including members
- `api-applications.rst` — full applications API including env, deployments, domains, monitoring, analytics
- `api-databases.rst` — database CRUD + lifecycle
- `cli-regions.rst` — region list with CLI aliases
- `cli-databases.rst` — CLI doc showing richer DB info (connection string format)
- `cli-applications.rst` — CLI doc for apps
- `platform-applications.rst` — plan-tier resources (2GB/4GB/Unlimited)
- `platform-databases.rst` — shared plan limit with apps
- `platform-environment-variables.rst` — `DATABASE_URL` injection pattern
- `platform-domains-ssl.rst` — default `*.cranl.net` subdomain, free SSL
- `platform-github-integration.rst` — CranL GitHub App flow
- `mcp-tools.rst` — their MCP server; `cranl_list_regions` returns server IDs like `germany-1`
