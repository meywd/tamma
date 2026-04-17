# DB-per-Tenant — Control-Plane / Tenant Split

**Status**: Design proposal — not yet approved, no code changes committed.
**Audience**: Platform engineers implementing the migration from shared
`public` schema + `TenantId` column to a database-per-tenant topology.
**Branch basis**: `feat/auth-foundation` (current 22-entity shared-DB
snapshot).

## 0. Goal and summary

Move from one Postgres database shared across all tenants (EF global query
filter on a `TenantId` column) to:

- One **control-plane DB** holding auth, tenant directory, and
  cross-tenant mappings.
- One **tenant DB per tenant** holding all business data for that tenant
  only.
- One **global-Elsa DB** for platform-wide workflow definitions +
  instances (orchestrator, tenant-provisioning, tenant-deletion).
- One **per-tenant Elsa DB per tenant** for workflow instances that run
  on behalf of that tenant's engine.

Reasons: cryptographic tenant isolation, simple GDPR "delete me"
semantics (drop the database), elimination of query-filter bypass bugs,
independent tenant scaling, and per-tenant encryption-at-rest options.

The remaining sections give every decision needed to implement the
migration. No code is attached. All current identifiers quoted below
come from `feat/auth-foundation`.

---

## 1. Entity placement

Every `DbSet<T>` on the current `TammaDbContext` maps to exactly one
store. No entity lives in two places.

### 1.1 Legend

- **CP** — control plane (one shared DB)
- **T**  — tenant DB (one per tenant)
- **GE** — global-Elsa DB (Elsa workflow management + runtime for global
  workflows)
- **TE** — per-tenant Elsa DB (Elsa workflow management + runtime for
  this tenant's engine)

### 1.2 Placement table

| # | Entity | Current table | Destination | Rationale |
|---|---|---|---|---|
| 1 | `User` | `users` | **CP** | One user identity shared across tenants. Must exist before any tenant DB does. |
| 2 | `RefreshToken` | `refresh_tokens` | **CP** | Session material attached to a user, not a tenant. A refresh must succeed even when the user's active tenant is being provisioned or deleted. |
| 3 | `PasswordResetToken` | `password_reset_tokens` | **CP** | Password is a user-level credential. Reset flow runs before tenant context is known. |
| 4 | `Tenant` | `tenants` | **CP** | The tenant directory itself. Holds encrypted connection string, status, slug, plan. |
| 5 | `TenantMembership` | `tenant_memberships` | **CP** | The user-to-tenant join. Needed for login ("which tenants can I switch to") and permission checks. |
| 6 | `UserInvite` | `user_invites` | **CP** | An invite is redeemed before the recipient has a user row on the tenant. Must be writable without a tenant context. |
| 7 | `ApiKey` (platform-scoped, `Scope='platform'` or `Scope='user'` without `TenantId`) | `api_keys` | **CP** | Keys that authenticate a human or a cross-tenant automation. |
| 8 | `ApiKey` (tenant-scoped, `Scope='tenant'`, `TenantId` set) | `api_keys` | **T** | Keys that act on behalf of a single tenant — move to that tenant's DB, deleted together when the tenant is deleted. See §3 on the "deferred resolution" pattern for the `OwnerId` linkage. |
| 9 | `GitHubInstallation` | `github_installations` | **CP** | A GitHub App installation can be remapped between tenants (trial → paid org), and is resolved by installation ID before we know the tenant. The `TenantId` column becomes the pointer from installation → tenant. |
| 10 | `GitHubInstallationRepo` | `github_installation_repos` | **CP** | Child of `GitHubInstallation` — FK must stay in the same DB. |
| 11 | `AgentConfig` | `agent_configs` | **T** | Pure tenant data. One row per tenant today; moves to one row in the tenant's DB. |
| 12 | `PromptOverride` | `prompt_overrides` | **T** | Per-tenant prompt customisations. System defaults stay in code, overrides stay with the tenant. |
| 13 | `ProviderHealth` | `provider_health` | **T** | Circuit-breaker state per `(provider, tenant)`. Per-tenant isolation is the whole point — one tenant's bad API key must not open another tenant's circuit. |
| 14 | `ProviderDiagnostic` | `provider_diagnostics` | **T** | Per-call usage + cost records. Drives per-tenant billing. |
| 15 | `SanitizationRule` | `sanitization_rules` | **T** | Per-tenant PII regex overlays. |
| 16 | `WorkflowDefinition` (tenant-authored) | `workflow_definitions` | **T** | Definitions a tenant writes for their own engine. |
| 17 | `WorkflowDefinition` (global orchestrator, tenant-provisioning, tenant-deletion) | n/a today | **GE** | Platform-authored workflows, shipped with the product. Not tenant data. |
| 18 | `WorkflowInstance` (engine runs for a tenant) | `workflow_instances` | **T** | Tenant-bound runtime state. |
| 19 | `WorkflowInstance` (orchestrator tick, provisioning run) | n/a today | **GE** | Runs cross-tenant; cannot live in any one tenant's DB. |
| 20 | Elsa's own `WorkflowDefinitionStore` + `WorkflowInstanceStore` + bookmarks + triggers for **global** workflows | in `tamma-elsa` Elsa EF tables today | **GE** | Elsa's persistence for the global workflow server. |
| 21 | Elsa's own `WorkflowDefinitionStore` + `WorkflowInstanceStore` + bookmarks + triggers for **tenant** workflows | in `tamma-elsa` Elsa EF tables today | **TE** | Elsa's persistence for the per-tenant engine instance. |
| 22 | `DomainEvent` | `domain_events` | **T** (tenant-scoped events) and **CP** as `PlatformEvent` (global events) | **See §1.3** — the DCB event stream splits into two tiers. |
| 23 | `QueuedTask` (with `TenantId` set — webhook payloads for a specific install) | `queued_tasks` | **T** | The rows carry tenant-scoped work (GitHub push for tenant X). Live with the tenant. |
| 24 | `QueuedTask` (with `TenantId = null` — installation-routing or admin tasks before routing) | `queued_tasks` | **CP** (new: `platform_queued_tasks`) | Needed by the installation router before the tenant is known. Tenant-bound tasks are enqueued to the tenant DB *after* routing resolves the tenant. |
| 25 | `EmailOutboxMessage` (tenant-scoped — onboarding, invites, workflow alerts) | `email_outbox` | **T** | Tenant-scoped mail. |
| 26 | `EmailOutboxMessage` (system-scoped — registration verification, password reset, platform admin alerts) | `email_outbox` | **CP** (new: `platform_email_outbox`) | Must deliver before a tenant DB exists (registration) or after one is gone (deletion confirmation). |
| 27 | `MentorshipSession` | `mentorship_sessions` | **T** | Pure tenant business data. |
| 28 | `MentorshipEvent` | `mentorship_events` | **T** | Same. |
| 29 | `JuniorDeveloper` | `junior_developers` | **T** | Same. |
| 30 | `Story` | `stories` | **T** | Same. |

Note: row numbering above describes logical destinations, not physical
entities — `ApiKey`, `DomainEvent`, `WorkflowDefinition`,
`WorkflowInstance`, `QueuedTask`, `EmailOutboxMessage` each split by a
column value. The net is **22 source entities → 30 placement decisions**.

### 1.3 Entities with non-obvious placement

Six entities have a justified double-placement:

- **`ApiKey`**. The current row has a `Scope` column (`platform`,
  `tenant`, `user`). `platform` and `user` scopes live in CP; `tenant`
  scope moves to the tenant DB. This is the cleanest split: a platform
  admin API key must keep working after any tenant is deleted, and a
  tenant API key must be destroyed with its tenant. Trade-off: two
  physical tables with the same schema, named `api_keys` in both DBs.
  The repository layer picks the right store from the `Scope`.

- **`WorkflowDefinition` and `WorkflowInstance`**. Platform-shipped
  workflows (orchestrator, `CreateTenantWorkflow`, `DeleteTenantWorkflow`)
  live in global-Elsa; tenant-authored ones live in the tenant's Elsa.
  Trade-off: two Elsa servers, two Elsa DBs per tenant. Alternative
  considered: one central Elsa with a `tenant` tag on every instance.
  Rejected because a tenant "delete me" cannot drop central Elsa rows
  without coordinating across tenants. Shipping two Elsa servers is
  simpler.

- **`DomainEvent` vs new `PlatformEvent`**. See §5. Recommendation: two
  physical stores with identical schema.

- **`QueuedTask`** and **`EmailOutboxMessage`**. Two physical tables
  (control-plane `platform_queued_tasks`, tenant `queued_tasks` — and
  `platform_email_outbox` vs `email_outbox`). Routing into the right
  table happens at enqueue time based on whether a tenant has been
  resolved yet.

### 1.4 What does NOT exist after the split

No `TenantId` column remains in the tenant DB — a tenant DB is for one
tenant only, so the discriminator is implicit in the connection string.
The EF global query filter is removed. Every `HasQueryFilter` in the
current `TammaDbContext` disappears. This removes an entire class of
query-filter-bypass bugs.

The one exception is `ApiKey` on the tenant DB, which does keep the
original `Scope` column for backward compatibility of the single-row
shape, but the row always has `Scope='tenant'` on a tenant DB; a
DB-level `CHECK` constraint enforces it.

---

## 2. Auth / identity model (AD-style)

### 2.1 Model

One user, many memberships, one active tenant at a time. Modelled on
Active Directory's "one account, multiple forest trusts". The JWT
carries the user id (`sub`) and the **currently active** tenant id
(`tid`).

Control plane owns:

- `users`
- `refresh_tokens`
- `password_reset_tokens`
- `tenant_memberships`
- `user_invites`
- `api_keys` where `Scope IN ('platform','user')`

### 2.2 End-to-end flows

#### 2.2.1 Login

1. Client `POST /api/v1/auth/login` with email + password.
   `TenantContextMiddleware` skips (path is already in `TenantFreePaths`).
2. Handler queries CP `users` by email, verifies password (`PasswordService`
   unchanged), calls `LoginLockoutService` unchanged.
3. Handler queries CP `tenant_memberships WHERE user_id = $1` and returns
   `{ user, memberships: [{ tenantId, tenantSlug, role }], activeTenantId }`.
4. Server picks a default active tenant:
   - `user.DefaultTenantId` if the user has one set (new column on
     `users`, nullable), else
   - the first `TenantMembership` ordered by `JoinedAt ASC`, else
   - `null` — the user has zero tenants (just accepted no invite yet).
     In this state the server issues a "rootless" JWT with `tid` claim
     omitted; only `/api/v1/auth/*`, `/api/v1/tenants` (create/join),
     and `/api/v1/invites/*` accept a rootless JWT.
5. Server calls `JwtService.GenerateAccessToken(user, activeTenantId, role)`
   where `role` is the role from that specific membership (**not** from
   `users.Role` — the global-user role disappears with this migration).
6. Server stores the refresh token in CP.
7. Response: `{ access, refresh, user, memberships, activeTenantId }`.

#### 2.2.2 Switch active tenant

1. Client `POST /api/v1/auth/switch-org` body `{ tenantId }` with a valid
   JWT.
2. Server verifies a `TenantMembership` row exists for
   `(currentUserId, requestedTenantId)` in CP. If not, 403.
3. Server queries CP `tenants.Status` for `requestedTenantId`; must be
   `active`. If `provisioning`, 503 with a machine-readable
   `X-Tenant-Status: provisioning` header (front-end shows a spinner).
   If `deleting` or `deleted`, 410 Gone.
4. Server issues a new access token with `tid = requestedTenantId`. The
   old refresh token remains valid — switching does not rotate the
   refresh token, only the access token.
5. Response: `{ access, activeTenantId }`.

Refresh flow is unchanged except that the refresh handler re-reads the
`TenantMembership.Role` from CP and bakes the current role into the new
access token — picks up role changes made since the last access issue.

#### 2.2.3 `/api/auth/me`

1. Server reads user from CP (`sub` claim).
2. Server reads all memberships from CP (`user_id = sub`).
3. Server reads `tenants.Status` for every membership so the UI can grey
   out `provisioning`/`deleting` entries.
4. Server returns `{ user, memberships, activeTenantId }`.

Note: `/api/auth/me` **never** touches a tenant DB. This is intentional
— it must work during tenant provisioning.

### 2.3 Tenant resolution middleware

`TenantContextMiddleware` in its current form reads the `tid` claim and
sets `ITenantContext.TenantId`. After the split it does three more things:

1. Short-circuit for a rootless JWT (no `tid` claim) on paths outside
   the rootless allow-list — return 403 with
   `X-Tenant-Required: true`.
2. Resolve a `NpgsqlDataSource` for the tenant via the per-tenant pool
   cache (§9), decrypt the connection string on cache miss, add the
   data source to the request-scoped DI container as the "tenant-scoped"
   DB source. The existing `TammaDbContext` injection is replaced: two
   DbContexts are registered —
   - `ControlPlaneDbContext` (always the CP data source) and
   - `TenantDbContext` (resolved per request from the cache).
3. If the resolved tenant's `Status` is not `active`, return 503 with
   `X-Tenant-Status: provisioning` (422 with `X-Tenant-Status: failed`
   if provisioning terminally failed).

Public routes on the current `TenantFreePaths` list still skip all of
this. The list grows by one entry: `/api/v1/auth/switch-org` — that
endpoint resolves its own target tenant, the middleware must not try
to resolve from the current JWT.

### 2.4 Permission checks: JWT vs per-request CP lookup

The current `PermissionHandler` reads the role from `ClaimTypes.Role`
on the JWT. That is correct for the DB-per-tenant world **as long as**
the role in the JWT was read from `TenantMemberships[tid][sub].Role`
at issue time, not from `users.Role`. I am recommending:

- **Role is baked into the JWT at issue time** (login, switch-org,
  refresh).
- **No per-request CP query** for role lookups. The
  `PermissionHandler` stays fast (no DB touch).

Trade-off: a role demotion takes effect when the access token next
refreshes. Access tokens are 15 minutes (current `JwtService`), so
worst case a demoted user keeps admin rights for 15 minutes.

Mitigation for the "admin fired, must revoke now" case:

- A new CP table `token_revocations (user_id UUID, tenant_id UUID,
  revoked_at TIMESTAMPTZ, reason TEXT)`. The middleware, **only** on
  `/api/admin/*` paths (high-privilege), checks this table with a
  one-minute in-process cache. Normal paths stay query-free. Cost:
  one indexed query per admin request per minute per process.
- On a role demotion, insert a row into `token_revocations`; every
  access token issued before `revoked_at` is invalid on `/api/admin/*`.

This gives fast-by-default permission checks with a targeted immediate-
revocation path. The user does NOT need to log out; their next refresh
picks up the new role.

### 2.5 Claims on the JWT after the split

| Claim | Value | Source |
|---|---|---|
| `sub` | User id (UUID) | `users.Id` in CP |
| `tid` | Active tenant id (UUID), may be absent in rootless mode | `TenantMemberships.TenantId` in CP |
| `role` | Role in the active tenant | `TenantMemberships.Role` in CP |
| `email` | User email | `users.Email` in CP |
| `jti` | Token identifier | random |
| `iat` | Issued-at seconds | server |
| `exp` | Expires-at seconds | server, +15min |

Removed: the current `users.Role` as the sole source of role. That
column is kept for backward compatibility through the migration window
then dropped in a follow-up wave.

### 2.6 API keys after the split

- **Platform-scoped** (`Scope='platform'`): in CP. Authenticates a
  platform admin automation. Permissions array attached.
- **User-scoped** (`Scope='user'`): in CP. Acts as the user. On a
  tenant-bound request, the middleware reads the key, resolves the
  user, and then requires an explicit `X-Tenant-Id` header to pick the
  active tenant — the key is not tied to one tenant.
- **Tenant-scoped** (`Scope='tenant'`): in the tenant DB. Its
  `OwnerId` is a UUID of a user in CP (no FK). Auth flow: resolve the
  key by the `KeyHash` — but now we have to pick a DB first. This is
  the hard case — see §3.1.

---

## 3. Cross-database linkage (no FKs across DBs)

### 3.1 The "which DB holds the key?" problem

A `Bearer tk_...` header hits the API. We do not yet know the tenant.
We need to find which tenant DB (or CP) holds this key. Options:

1. **Key prefix encodes the tenant**. Format
   `tk_<tenant_id_b32><random>`. The auth handler peels off the
   tenant id, resolves the data source, queries that tenant's
   `api_keys` by `KeyHash`. Platform keys use a reserved prefix
   (`tk_pl_<random>`).
2. **Central index**. A CP table `api_key_index (key_hash TEXT PRIMARY
   KEY, tenant_id UUID NULL, scope TEXT NOT NULL)` mapping every hash
   to the home DB. Rotating a key updates the index; revoking deletes
   the index row and the tenant row.

**Recommendation: option 1** (prefix-encoded). Lower operational
complexity, no cross-DB sync hazards. Trade-off: the on-wire key gets
longer (base32 UUID = 26 chars plus 3-char `tk_` prefix plus 32 chars
random = 61 chars), still well within header limits.

The `ApiKeyAuthHandler` changes as follows (described in prose):

1. Parse the key. If prefix is `tk_pl_` → platform, query CP. If prefix
   is `tk_u_` → user-scoped, query CP. Else `tk_t_<tenant_b32>_...` →
   tenant-scoped, resolve tenant data source, query tenant DB.
2. Load the key, verify hash, apply expiration / revocation checks.
3. For tenant-scoped keys, set the `TenantContext` to the decoded
   tenant id — the middleware's JWT branch is bypassed.

### 3.2 User-id foreign keys in tenant DBs

Tenant DB tables carrying a user id have a simple rule: the column is
`user_id UUID` with no FK, no cascade, never null-checked against CP
in the write path. Display-time resolution is a **deferred join in the
application**:

- `domain_events.user_id` — raw Guid. Resolved only when rendering an
  audit trail UI: the dashboard loads a batch of events and then one
  CP query `SELECT Id, DisplayName, Email FROM users WHERE Id = ANY($1)`
  and stitches the rows. Missing users show as "Deleted user".
- `queued_tasks` — no user linkage today, unchanged.
- `agent_configs.created_by` / `updated_by` — same pattern.
- `email_outbox.user_id` — same pattern. On delivery, the renderer
  already has the recipient email embedded in the row; the `user_id`
  is only for auditing "this mail was sent on behalf of X".
- Tenant `api_keys.owner_id` — see §3.1. Resolution when rendering the
  "manage API keys" UI: after loading the tenant's keys, one CP query
  resolves display names.

This is the **application-level join** pattern. Trade-offs:

- **Pro**: No cross-DB ACID, simple delete semantics (drop tenant DB
  leaves no dangling FKs), GDPR compliance.
- **Con**: An orphaned `user_id` cannot be caught at write time. If a
  user is hard-deleted from CP, tenant DBs will contain stale ids.
  Mitigation: we do **not** hard-delete users. `users.DeletedAt` (soft
  delete) already exists on the entity; reads that join to users
  filter on `DeletedAt IS NULL` but the raw id remains resolvable to
  a "Deleted user" tombstone.

### 3.3 The `Tenant.OwnerId` link

`Tenant.OwnerId` is a nullable FK to `users.Id` in CP. Both are in CP,
so the current FK stays. No change.

---

## 4. Registration and async provisioning

### 4.1 Registration flow

`POST /api/v1/auth/register` writes **only** to CP. No tenant DB is
created synchronously.

Steps executed in a single CP transaction:

1. Validate payload (email format, password strength) — existing rules.
2. Insert `users` row with `EmailVerified=false`,
   `EmailVerificationTokenHash` set.
3. Insert `tenants` row with:
   - `Slug` = collision-safe slug of requested name
   - `Status = 'pending_verification'`
   - `OwnerId` = the new user id
   - `Type` = `'personal'` (or `'organization'` if org signup)
   - `EncryptedConnectionString = NULL` (populated by the provisioning
     workflow)
4. Insert `tenant_memberships` row with `Role='owner'`.
5. Insert `platform_email_outbox` row for the verification email
   (system-scoped outbox in CP, §1.2 row 26).
6. Insert `platform_events` row `TENANT.REGISTERED` (§5.2).
7. Commit.

### 4.2 Why not kick the provisioning workflow immediately

We gate DB creation on email verification. This avoids provisioning
cost for bots and typo addresses. The user journey is:

1. Register → email verification sent.
2. Click link → `POST /api/v1/auth/verify-email` → flips
   `users.EmailVerified=true` and flips `tenants.Status` from
   `pending_verification` to `provisioning`, emits
   `TENANT.PROVISIONING_REQUESTED`.
3. The global Elsa `CreateTenantWorkflow` correlates on that event.

### 4.3 CreateTenantWorkflow (global Elsa)

The workflow runs in **global-Elsa** with a single-step per resource so
each is independently retryable and compensatable. Inputs:
`{ tenantId: Guid }`. Reads `tenants` row from CP.

| Step | Action | Compensation |
|---|---|---|
| 1 | `CREATE ROLE tamma_tenant_<guid32hex> LOGIN PASSWORD '<generated>'` | `DROP ROLE` on retry reentry |
| 2 | `CREATE DATABASE tamma_tenant_<guid32hex> OWNER tamma_tenant_<guid32hex>` | `DROP DATABASE` |
| 3 | Run tenant-schema EF migrations against the new DB (out-of-process EF tool invocation) | n/a — migrations are idempotent; retry reruns them |
| 4 | `CREATE DATABASE tamma_tenant_<guid32hex>_elsa OWNER tamma_tenant_<guid32hex>` | `DROP DATABASE` |
| 5 | Run Elsa's EF migrations against the per-tenant Elsa DB | n/a |
| 6 | Seed defaults (agent config singleton row, sanitization-rule row with empty rules array, system prompts overrides = none) | n/a (tenant DB drop handles it) |
| 7 | AES-256-GCM encrypt the generated connection string with the master KEK (§8), write to `tenants.EncryptedConnectionString` | On reentry, decrypt and reuse the prior password |
| 8 | Flip `tenants.Status = 'active'` | n/a |
| 9 | Emit `TENANT.PROVISIONED` to `platform_events` | n/a |
| 10 | Send welcome email — see below for which outbox | n/a |

Welcome email goes through the **tenant's outbox**
(`email_outbox` in the tenant DB), not the platform outbox. Rationale:
a welcome mail is tenant-lifecycle data and should be reachable by the
tenant's audit views. The registration-verification mail goes through
the **platform outbox** because at that point no tenant DB exists yet.

### 4.4 User experience during provisioning

After email verification the user is redirected to the app. The first
call returns 503 + `X-Tenant-Status: provisioning`. The client polls
`/api/v1/tenants/{activeTenantId}/status` (rootless allow-listed,
CP-only) once every 2 seconds until the status flips to `active`.
Typical provisioning time on Hetzner CPX42: 5–15 seconds (database
creation + EF migrations × 2 + seeding).

### 4.5 Error handling and compensation

- Transient failures (migration step 3 or 5 throws) → Elsa retries with
  exponential backoff, 5 attempts, `baseDelay=2s`, `maxDelay=60s`.
- Permanent failure after retry budget → workflow runs the compensation
  steps in reverse (drop Elsa DB, drop tenant DB, drop role). Sets
  `tenants.Status='failed'`, `tenants.FailureReason='<stage>:<msg>'`,
  emits `TENANT.PROVISIONING_FAILED`, pages a platform admin via a
  configured webhook (Slack / PagerDuty).
- Idempotency: each step checks the observable state first. Example:
  step 2 checks `pg_database` for the name; if present, skip to step 3.
  This lets an admin manually complete a stuck workflow.
- Consistency: the "DB was created but migrations failed" case is
  the dangerous one. The workflow compensates forward by always
  dropping the half-built DB on terminal failure rather than leaving
  it.

---

## 5. Cross-tenant and global analytics

### 5.1 Two-tier model

- **Per-tenant events (`domain_events` in the tenant DB)**. Same shape
  as today. Created by LLM calls, workflow steps, code-generation
  events, user actions inside the tenant. Used for that tenant's
  dashboard, time-travel debug, compliance audit.
- **Global events (`platform_events` in CP, new table)**. Same schema
  as `domain_events` (`Id`, `Type`, `Tags`, `Metadata`, `Data`,
  `CreatedAt`). Used for cross-tenant rollups and any event that
  happens outside a tenant DB.

### 5.2 Event classification

| Event type | Tier | Why |
|---|---|---|
| `TENANT.REGISTERED` | CP | Before tenant DB exists |
| `TENANT.PROVISIONING_REQUESTED` | CP | Fires while workflow is starting |
| `TENANT.PROVISIONED` | CP | Tenant DB now exists, but the event itself is tenant-lifecycle |
| `TENANT.PROVISIONING_FAILED` | CP | No tenant DB to write to |
| `TENANT.DELETE_REQUESTED` | CP | Tenant DB about to go away |
| `TENANT.DELETED` | CP | Tenant DB no longer exists |
| `USER.REGISTERED`, `USER.LOGIN.SUCCESS/FAILED` | CP | Auth runs before tenant context |
| `USER.SWITCHED_ORG` | CP | Crosses tenants |
| `ORCHESTRATOR.TICK.*` | CP | Global workflow |
| `GITHUB.INSTALLATION.*` (before tenant resolution) | CP | Router-level events, no tenant yet |
| `CODE.GENERATED.*`, `LLM.CALL.*`, `WORKFLOW.STEP.*` | Tenant | Tenant-scoped |
| `ISSUE.ASSIGNED.*`, `PR.CREATED.*`, `GATE.*` | Tenant | Tenant-scoped |
| `AGENT_CONFIG.UPDATED`, `PROMPT.OVERRIDE.*`, `SANITIZATION_RULE.*` | Tenant | Tenant-scoped |

### 5.3 Aggregation

A **nightly** job (02:00 UTC) named `PlatformAnalyticsRollupWorkflow`
runs in global-Elsa. Per tenant, it:

1. Opens a short-lived tenant connection.
2. Aggregates `domain_events` for the last 24 hours into per-tenant
   hourly buckets: total events, events by `Type`, LLM cost (sum from
   `ProviderDiagnostic.Cost`), workflow completions, failure counts.
3. Writes one row per `(tenant_id, hour, metric)` to a CP table
   `platform_analytics_hourly`.

Rationale for nightly rather than streaming: simpler, cheap, good
enough for a growth dashboard. Real-time cross-tenant alerts live on
`platform_events` directly; the aggregated table is for charts and
exports.

A **weekly** job (Sunday 03:00 UTC) rolls the hourly table forward
into `platform_analytics_daily` and trims hourly rows older than 30
days.

### 5.4 Which dashboard queries hit which tier

- **Tenant dashboard** (`/api/v1/analytics/*`) → tenant DB's
  `domain_events`.
- **Platform admin "all tenants"** (`/api/admin/analytics/*`) →
  `platform_analytics_hourly` + `platform_events` for lifecycle signals.
  Never reads tenant DBs directly on a dashboard request.
- **Per-tenant drill-down in admin UI** → when an admin clicks on one
  tenant, the request carries a platform JWT and the handler reads
  that tenant's `domain_events` via a platform-elevated tenant
  connection (§7).

---

## 6. Naming and collision safety

### 6.1 Scheme

Given a tenant id `82f74e8c-2b1a-4e0e-9c2a-6c9e1d0c8e72`:

| Resource | Value | Length (bytes) | Limit |
|---|---|---|---|
| Tenant DB | `tamma_tenant_82f74e8c2b1a4e0e9c2a6c9e1d0c8e72` | 45 | 63 |
| Tenant role | `tamma_tenant_82f74e8c2b1a4e0e9c2a6c9e1d0c8e72` | 45 | 63 |
| Per-tenant Elsa DB | `tamma_tenant_82f74e8c2b1a4e0e9c2a6c9e1d0c8e72_elsa` | 50 | 63 |
| Control-plane DB | `tamma_control` | 13 | 63 |
| Global-Elsa DB | `tamma_global_elsa` | 17 | 63 |
| Superuser role | `tamma_admin` | 11 | 63 |
| Runtime role (CP) | `tamma_app` | 10 | 63 |
| Provisioning role | `tamma_provisioner` | 18 | 63 |

Postgres identifier length limit is 63 bytes. All values fit with
margin.

### 6.2 Hyphens

Stripped. Postgres identifiers do not require quoting if they are
lowercase alphanumeric + underscore. Stripping hyphens keeps
identifiers unquoted and reduces surface area for quoting bugs in
dynamic SQL.

### 6.3 Slugs vs DB names

`tenants.Slug` stays human-readable (`my-startup`) and is used only in
URLs and UI. The DB name is derived from `tenants.Id` — never from
the slug, because a slug rename would otherwise require renaming the
database (slow, locks hard).

### 6.4 Collision impossibility

Uniqueness of `Id` (generated from `gen_random_uuid()`) implies
uniqueness of the derived DB name. A 128-bit UUID collision is not a
concern at our scale. No additional hashing or suffixing is needed.

### 6.5 Reserved names

- `tamma_control`, `tamma_global_elsa`, `tamma_admin`, `tamma_app`,
  `tamma_provisioner` are reserved. The provisioning workflow
  hard-codes these so a UUID that happens to produce one of them
  cannot exist (no UUID can, given the fixed prefixes).

---

## 7. Superuser and provisioning credentials

### 7.1 Three Postgres roles

| Role | Privileges | Used by | Secret location |
|---|---|---|---|
| `tamma_admin` | `SUPERUSER` | Manual migrations, disaster recovery only (human operator) | Vault / Hetzner sealed secret, NOT on any running host |
| `tamma_provisioner` | `CREATEDB`, `CREATEROLE`; NO `SUPERUSER` | `CreateTenantWorkflow`, `DeleteTenantWorkflow` — runs inside global-Elsa | `TAMMA_PROVISIONER_DB_URL` env var on the global-Elsa pod |
| `tamma_app` | Read/write on CP only. No CREATE privileges. | Control-plane API runtime | `TAMMA_CONTROL_DB_URL` env var on the API pod |
| `tamma_tenant_<id>` | Owner of its own tenant DB; no privileges elsewhere. | The API runtime, using the per-tenant connection string resolved at request time | Encrypted in `tenants.EncryptedConnectionString` |

### 7.2 Why three roles not one

- Least privilege. The API runtime cannot `CREATE DATABASE`, so an
  SQL-injection or logic bug cannot provision arbitrary DBs.
- Blast radius. A leak of `tamma_provisioner` does not leak the
  superuser. A leak of `tamma_app` is tenant-data-at-risk only on CP
  (user auth rows).
- Rotation. Rotating `tamma_provisioner` requires a global-Elsa pod
  restart only. Rotating `tamma_app` requires an API pod restart only.
  Rotating `tamma_admin` has zero runtime impact.
- `tamma_admin` is never set as a live env var anywhere. An operator
  sources it from the vault at the moment they need it, typically for
  `pnpm migrate:latest` during a deploy, then discards it.

### 7.3 Key rotation flow

- Rotate `tamma_provisioner` password: update in vault, update global-
  Elsa secret, restart global-Elsa. Running tenant provisioning
  workflows pause and resume. Zero impact on tenant request paths.
- Rotate `tamma_app` password: same for the API pod.
- Rotate per-tenant role password: a maintenance job re-keys one
  tenant at a time — `ALTER ROLE tamma_tenant_<id> PASSWORD '<new>'`,
  then re-encrypt the `EncryptedConnectionString`, then evict the
  tenant's cached data source so the next request picks up the new
  password.

### 7.4 Admin-elevated tenant access

The admin drill-down (§5.4) needs to read a tenant DB without being
that tenant. Implementation: at admin-request time, the control-plane
API decrypts the tenant's connection string using the KEK exactly the
same way a normal tenant request would. There is no second "admin"
connection to each tenant DB — the same role `tamma_tenant_<id>` is
used, authenticated through the same pool. The access is logged to
`platform_events` as `ADMIN.TENANT_ACCESSED` with the admin user id
and tenant id.

---

## 8. Connection-string encryption at rest

### 8.1 Schema

Add to `tenants`:

- `EncryptedConnectionString bytea` — envelope format below.
- `KekVersion int NOT NULL DEFAULT 1` — which KEK slot was used.

Envelope format (55+N bytes total, N = connection-string length):

```
[1 byte version=0x01]
[1 byte kek_slot]
[12 bytes nonce]
[ciphertext N bytes]
[16 bytes GCM tag]
```

Version byte lets the format evolve. `kek_slot` lets multiple master
keys coexist during rotation.

### 8.2 Storage of the master key

- Primary: environment variable `TAMMA_TENANT_KEK_BASE64` on the API
  pod, sourced from Hetzner sealed secrets. Secondary (rotation)
  slot: `TAMMA_TENANT_KEK_SECONDARY_BASE64`.
- Long-term recommendation: AWS KMS / GCP KMS / HashiCorp Vault
  Transit. Not in scope for the first implementation wave.
- The KEK never leaves the process. Derived per-tenant DEKs are not
  used — the KEK encrypts the connection string directly. Acceptable
  because the connection string is the only thing being encrypted and
  the data volume is tiny.

### 8.3 Rotation

1. Generate a new KEK.
2. Deploy it to the secondary slot (`_SECONDARY`).
3. Run a background job that reads every `tenants` row, decrypts with
   the slot indicated by `KekVersion`, re-encrypts with the new KEK,
   writes back with `KekVersion = 2`.
4. Promote the new KEK to primary, remove the old secondary.

Live requests during the rotation window try the primary KEK first;
on decrypt failure, try the secondary. This is only safe because
AES-GCM's auth tag makes "decrypt with wrong key" reliably detectable.

### 8.4 Alternative: deterministic derivation (rejected)

Alternative considered: derive the per-tenant password deterministically
from `HKDF(KEK, "tenant-pg-password:" || tenant_id)`, store nothing,
and re-derive at request time. Pros: no `EncryptedConnectionString`
column, no rotation re-encrypt loop. Cons:

- Rotating the KEK requires re-deriving and `ALTER ROLE` for every
  tenant — more operational cost than re-encrypting 10k small rows.
- Password complexity is bounded by the derivation — a KEK compromise
  reveals every password. An envelope-per-row means KEK compromise
  reveals every password just as thoroughly, but generates passwords
  with full entropy from Postgres' standpoint.
- Auditability: the stored-envelope path logs "re-encryption of
  `tenant_id = X` to `KekVersion = 2`", which is a visible audit
  event. Derivation has no such event.

**Recommendation: envelope format.** The operational simplicity of
"no re-encrypt on rotation" is not worth losing rotation audit
visibility.

---

## 9. Connection pool lifecycle

### 9.1 Shape

A process-wide `ConcurrentDictionary<Guid, NpgsqlDataSource>` keyed by
tenant id, holding warm data sources. Getting a data source:

1. Try the cache. Hit → return it.
2. Miss → acquire a per-tenant async lock, re-check cache (double-
   check), decrypt the tenant's connection string, build an
   `NpgsqlDataSource` with `MaxPoolSize=5`, `ConnectionIdleLifetime=60`,
   `ConnectionLifetime=300`. Put in cache. Release lock.
3. On eviction (§9.2), `DisposeAsync()` the data source cleanly.

Single lock per tenant prevents a thundering herd of first-hit
requests from building the same source N times.

### 9.2 Eviction — LRU of K=500 warm pools

- Target scale: 100 tenants short-term, 10k long-term.
- Realistic **concurrent active tenant** count is far smaller than
  total tenant count. Most tenants are idle at any given second.
- K = 500 gives headroom for a 5× burst over the 100-tenant target and
  is a safe starting point for the 10k-tenant horizon. At 5 pooled
  conns each: 2500 live Postgres connections worst case. Hetzner
  CPX42 Postgres can handle that with `max_connections=4000`.
- Eviction policy: LRU by last-use timestamp; a background sweep
  every 60s evicts entries older than 10 minutes idle, respecting K
  as a hard ceiling. If K is reached during a high-velocity burst,
  evict the single least-recently-used entry synchronously.

### 9.3 Observability

- Metric `tamma.tenant_pools.warm` — gauge, count of entries in the
  dictionary.
- Metric `tamma.tenant_pools.opened_total` — counter, incremented on
  cache miss.
- Metric `tamma.tenant_pools.evicted_total` — counter.
- Metric `tamma.tenant_pools.open_duration_ms` — histogram of the
  time spent building a fresh data source (decrypt + first connect).
  Pathological decrypt or network latency shows up here.

### 9.4 Per-tenant max conn sizing

- Hard cap at 5 per pool intentionally. A single tenant cannot starve
  Postgres. If a tenant needs more, they need a larger instance class
  — a discussion for the billing layer.
- `Timeout=5s` on connect; `CommandTimeout=30s` default.

---

## 10. Tenant deletion (erasure)

### 10.1 Flow

1. `DELETE /api/admin/tenants/{id}` (platform admin permission).
2. CP transaction:
   - `tenants.Status = 'delete_requested'`.
   - `tenants.DeleteRequestedAt = NOW()`.
   - Emit `TENANT.DELETE_REQUESTED` to `platform_events`.
3. Return `202 Accepted` — the request is queued. UI shows a banner
   with the configured cancellation window.
4. A **5-minute cooling-off timer** runs in global-Elsa. During this
   window, `POST /api/admin/tenants/{id}/cancel-delete` flips
   `Status` back to `active` and emits `TENANT.DELETE_CANCELLED`.
   This window is user-configurable via env (default 5 min; legal-
   hold tenants can configure 24h).
5. When the timer fires, `DeleteTenantWorkflow` runs in global-Elsa:

| Step | Action | On failure |
|---|---|---|
| 1 | Terminate active sessions: revoke all `refresh_tokens` where `user_id IN (members of this tenant)` AND `issued_for_tenant = this`. Insert `token_revocations` rows so access tokens for this tenant are rejected immediately by the `/api/admin/*` check path (§2.4). | Retry step |
| 2 | Evict the tenant's data source from the pool cache; block any further acquisition by inserting a `blocked` row in a CP `tenant_pool_blocklist` table. | Retry step |
| 3 | `ALTER DATABASE tamma_tenant_<id> CONNECTION LIMIT 0`. | Retry step |
| 4 | Terminate any lingering backend: `SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'tamma_tenant_<id>'`. | Retry step |
| 5 | `DROP DATABASE tamma_tenant_<id>`. | **Pause** — surface to admin. See §10.2. |
| 6 | `DROP DATABASE tamma_tenant_<id>_elsa`. | **Pause**. |
| 7 | `DROP ROLE tamma_tenant_<id>`. | Retry step (usually harmless). |
| 8 | CP transaction: delete `tenant_memberships` for this tenant, delete `user_invites`, delete `api_keys` rows where `TenantId = this AND Scope='tenant'` (should be none — those were in the tenant DB — but any leaked rows are cleaned here), delete `github_installations.TenantId = this` (nullify, do not delete — re-installable), delete `queued_tasks`/`platform_queued_tasks` where `TenantId = this`. | Retry step |
| 9 | `tenants.Status = 'deleted'`, `tenants.DeletedAt = NOW()`, `tenants.EncryptedConnectionString = NULL`. Row is NOT dropped — soft-delete retained for audit. | Retry step |
| 10 | Emit `TENANT.DELETED` to `platform_events`. | Retry |
| 11 | Final platform admin notification email via `platform_email_outbox`. | Retry |

### 10.2 Compensation and partial-delete handling

Step 5 or 6 can fail — most often because a connection slipped past
step 4 (a long-running transaction holding a lock on `pg_database`).
Compensation is conservative: the workflow **pauses** rather than
retrying. Rationale: a `DROP DATABASE` half-succeeding is not really
possible (it is atomic in Postgres), but "cannot drop because N users
connected" is recoverable once the operator has investigated. A Slack/
PagerDuty notification fires; the admin re-runs step 4 by hand and
then clicks "resume workflow" in Elsa Studio. The tenant stays in
`Status='deleting'` the whole time.

The user-visible state machine for `tenants.Status`:

```
pending_verification → provisioning → active
                                    ↘ failed
active → delete_requested → deleting → deleted
                        ↘ active (cancelled)
```

### 10.3 Data that intentionally survives deletion

- `tenants` row itself, with `Status='deleted'`, `DeletedAt` set,
  `EncryptedConnectionString=NULL`. Keeps the tenant id globally
  unique forever — same UUID cannot be re-issued.
- `platform_events` rows referencing the tenant (for compliance).
- `platform_analytics_hourly` rows referencing the tenant — hashed
  `tenant_id` keys are retained but resolve to the tombstone row.

Everything tenant-specific is gone. GDPR "erasure of personal data"
is satisfied: the only personally-identifying data that survives is
rollup aggregates with no raw user content.

---

## Appendix A: ADR-style summary of key decisions

| # | Decision | Alternatives rejected | Why |
|---|---|---|---|
| 1 | Separate control plane | Single shared DB with query filter | Current state — the problem we are solving. |
| 2 | Two Elsa servers (global + per-tenant) | Single central Elsa with `tenant` tag | Cleaner tenant delete. |
| 3 | User-id is a raw `Guid` in tenant DBs, no FK | Cross-DB FK enforcement via postgres_fdw | postgres_fdw complicates delete and pooling. Application joins are good enough. |
| 4 | JWT carries baked-in role | Per-request CP lookup | Performance. 15-minute demotion window accepted with `token_revocations` for high-privilege paths. |
| 5 | Async provisioning via global-Elsa workflow | Synchronous DB creation in the register handler | Registration must return fast; bots must not cost us DBs. |
| 6 | Envelope-encrypted connection string | Deterministic KDF derivation | Better rotation audit, comparable security. |
| 7 | Prefix-encoded API keys (`tk_t_<tenant>_...`) | Central index table | Fewer cross-DB sync hazards. |
| 8 | LRU pool cache K=500, MaxPoolSize=5 | One pool per tenant, unlimited | Bounded resource usage. |
| 9 | 5-minute cooling-off window before drop | Immediate drop, or 24h default | Balances cancellation safety with storage cost. |
| 10 | `platform_events` + nightly rollup to `platform_analytics_hourly` | Streaming ETL to a warehouse | Simpler. Enough for dashboards. |

---

## Appendix B: Work items this document unblocks

The follow-up implementation wave will, at minimum, produce:

1. EF migration scripts (one per DB: CP migrations, tenant migrations).
2. `ControlPlaneDbContext` splitting off from the current
   `TammaDbContext`.
3. `TenantDbContext` (renamed or repurposed `TammaDbContext` with
   query filters and `ITenantContext` injection removed).
4. Tenant data-source cache service.
5. `CreateTenantWorkflow` and `DeleteTenantWorkflow` (Elsa).
6. Platform-events / platform-queued-tasks / platform-email-outbox
   tables and repositories.
7. API key prefix scheme + new `ApiKeyAuthHandler` routing.
8. `TenantContextMiddleware` updates.
9. Updated `JwtService` claims and login/switch-org/refresh flows.
10. `platform_analytics_hourly` rollup workflow.
11. Admin UX for `tenants.Status` state machine.
12. Secrets plumbing for `tamma_admin`, `tamma_provisioner`,
    `tamma_app`, KEK + secondary KEK.

Each of these is a separate story.
