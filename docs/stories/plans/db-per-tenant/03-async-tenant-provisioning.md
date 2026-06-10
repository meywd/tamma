# 03 — Async Tenant Provisioning (Global Elsa Workflow)

> **Superseded/extended by the unified schema-per-tenant model** — see `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (complete 2026-06-10).

**Status**: Design (pending implementation)
**Owner**: Epic 17 / Epic 18 (tenant lifecycle)
**Depends on**: `01-control-plane-split.md`, `02-elsa-two-tier.md`
**Companion track**: Epic 18 story 18-3 (Organization / Tenant Creation)
**Last updated**: 2026-04-16

> **Scope**: this document specifies the end-to-end *orchestration* of a new
> tenant. Schema decisions live in `01-control-plane-split.md`; topology
> decisions (where the Elsa workflow runs) live in `02-elsa-two-tier.md`.
> This file is the glue between them.

---

## 0. Problem statement

`POST /api/v1/auth/register` currently creates `user + tenant + membership`
rows synchronously in a single control-plane database
(`apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs::Register`). In
the database-per-tenant world we cannot do that synchronously: per-tenant
`CREATE DATABASE`, Elsa migration runs, and seeding take seconds to tens
of seconds, and a blocking `HTTP 201` that waits 30s is hostile.

**Directive from product**: registration must return `201` after the
control-plane rows are written. A **global Elsa workflow**
(`CreateTenantWorkflow`) takes over in the background. The user can log
in immediately; tenant-scoped endpoints return `503 tenant_not_ready`
until the workflow flips tenant status to `active`.

This design reuses the `EMAIL.QUEUED → OutboxSmtpSender → EMAIL.SENT`
outbox pattern (`apps/tamma-elsa/src/Tamma.Api/Services/Email/`) applied
to tenant provisioning: event-sourced, retryable, idempotent, with the
domain event stream as the only authoritative progress record.

---

## 1. End-to-end flow

### 1.1 Timeline

```
t0   Client:  POST /api/v1/auth/register { email, password, displayName }
     │
t1   API (control plane, single transaction):
     │   INSERT users(…)
     │   INSERT tenants(status='provisioning', provisioning_started_at=now)
     │   INSERT tenant_memberships(role='owner')
     │   INSERT platform_events(type='TENANT.REGISTERED.SUCCESS')
     │   — commit —
t2   API → Elsa global: dispatch CreateTenantWorkflow(tenantId)
     │   (via Elsa HTTP trigger OR RabbitMQ OR `IWorkflowRunner`; see §10.3)
t3   API returns 201 Created:
     │   { userId, tenantId, status: "provisioning",
     │     progressUrl: "/api/v1/tenants/{id}/provisioning-status" }
     │
───── async, potentially on a different process ─────
     │
t4   Elsa: CreateTenantWorkflow(tenantId) starts
t5   Step 1  create_role          CREATE ROLE tamma_tenant_<id>
t6   Step 2  create_tenant_db     CREATE DATABASE tamma_tenant_<id> OWNER tamma_tenant_<id>
t7   Step 3  migrate_tenant_db    dotnet ef database update -c TenantDbContext
t8   Step 4  create_elsa_db       CREATE DATABASE tamma_tenant_<id>_elsa OWNER tamma_tenant_<id>
t9   Step 5  migrate_elsa_db      run Elsa migrations against elsa db
t10  Step 6  register_elsa        (two-tier mode) register the tenant Elsa
     │                            instance in the control-plane registry,
     │                            OR (single-cluster mode) call elsa.api
     │                            to enable the tenant partition
t11  Step 7  seed_defaults        seed agent config, prompts, sanitization
t12  Step 8  flip_status          UPDATE tenants SET status='active',
     │                            provisioned_at=now WHERE id=<id>
t13  Step 9  emit TENANT.PROVISIONED.SUCCESS
t14  Step 10 queue_welcome_email  INSERT email_outbox_messages(template='welcome')
     │                            — the existing OutboxSmtpSender delivers it
     │
     End. GET /api/v1/tenants/{id}/provisioning-status returns
          { status: "active", steps: [all completed] }
```

### 1.2 Per-step contract

Each step below specifies:

- **Input** — what the step reads
- **Output** — what the step writes (DB state + event emitted)
- **Idempotency key** — what makes re-execution a no-op
- **Failure class** — transient vs permanent vs ambiguous
- **Compensation** — what a *later* step failure does to this step's work

| # | Name | Input | Output | Idempotency key | Transient failure | Permanent failure | Compensation if later step fails |
|---|---|---|---|---|---|---|---|
| 1 | `create_role` | tenantId | PG role `tamma_tenant_<id>` with random password stored in secret store | role name (check `pg_roles`) | `57P03` PG shutting down, connection reset, timeout | `42710` role already exists but owned by different entity (abort) | Keep role unless *everything* compensates; compensation = `DROP OWNED BY` + `DROP ROLE` |
| 2 | `create_tenant_db` | tenantId | Database `tamma_tenant_<id>` | db name (check `pg_database.datname`) | `57P03`, `08006`, template1 busy | `42P04` exists-but-wrong-owner (abort) | `DROP DATABASE tamma_tenant_<id> WITH (FORCE)` |
| 3 | `migrate_tenant_db` | tenantId, migrations bundle | Tenant schema applied, `__EFMigrationsHistory` rows | EF Core's `__EFMigrationsHistory` | EF connection errors, DDL lock wait | Migration assertion failure, schema conflict | compensating drop of database from step 2 supersedes |
| 4 | `create_elsa_db` | tenantId | Database `tamma_tenant_<id>_elsa` | db name | same as step 2 | same as step 2 | `DROP DATABASE tamma_tenant_<id>_elsa WITH (FORCE)` |
| 5 | `migrate_elsa_db` | tenantId, Elsa migrations bundle | Elsa schema applied | `__EFMigrationsHistory` in elsa db | same as step 3 | same as step 3 | compensating drop of elsa db from step 4 supersedes |
| 6 | `register_elsa` | tenantId, elsa topology mode | Row in `control_plane.tenant_elsa_registry` OR Elsa API call | registry row PK | HTTP 5xx from Elsa, network | `4xx` config rejection, slot exhaustion | `DELETE FROM tenant_elsa_registry WHERE tenant_id=…` OR Elsa API deregister |
| 7 | `seed_defaults` | tenantId | Seeded rows in tenant DB (agent config, prompts, sanitization rules) | natural keys on each row (`ON CONFLICT DO NOTHING`) | DB connection error | Unique constraint collision from a prior partial seed | Seed rows disappear when tenant DB is dropped |
| 8 | `flip_status` | tenantId | `control_plane.tenants.status = 'active'`, `provisioned_at = now()` | `status='active'` is itself the idempotency marker | Connection error | Row missing (tenant was hard-deleted mid-workflow) | Reverse: `status='failed'` or `status='provisioning'` depending on strategy |
| 9 | `emit_provisioned_event` | tenantId | `platform_events` row `TENANT.PROVISIONED.SUCCESS` | dedupe by `(tenant_id, type)` when replaying | — | — | Not compensated — event stream is append-only |
| 10 | `queue_welcome_email` | tenantId, ownerEmail | `email_outbox_messages` row (Template=`welcome`) | `(tenant_id, template='welcome')` uniqueness | — | — | Not compensated — send-once policy is enforced at insert time |

Steps 9 and 10 are marked **side-effect only** — they do not alter the
tenant's durable state and therefore do not require compensation. They
do however require idempotency (§3.2).

---

## 2. Event taxonomy

All events are written to `control_plane.platform_events` (the table
formerly known as `domain_events` relocated to the control plane — see
`01-control-plane-split.md`). Reusing the existing `DomainEvent` entity
is mandatory: `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs`.

### 2.1 New event types

| Type | When emitted | Emitted by |
|---|---|---|
| `TENANT.REGISTERED.SUCCESS` | Control-plane rows committed, workflow about to be dispatched | `AuthEndpoints.Register` |
| `TENANT.PROVISION.STEP_STARTED` | Entering a workflow step | `CreateTenantWorkflow` activity |
| `TENANT.PROVISION.STEP_COMPLETED` | Step returned OK | `CreateTenantWorkflow` activity |
| `TENANT.PROVISION.STEP_FAILED` | Step threw; retry scheduled | `CreateTenantWorkflow` activity |
| `TENANT.PROVISIONED.SUCCESS` | Final step flipped status to active | `CreateTenantWorkflow` activity |
| `TENANT.PROVISION.FAILED` | Workflow-level terminal failure (retries exhausted OR compensation finished) | `CreateTenantWorkflow` activity |
| `TENANT.DELETE_REQUESTED` | Admin or user triggered deletion | Admin endpoint / compensator |
| `TENANT.DELETE.STEP_STARTED` | `CleanUpFailedTenantWorkflow` step enters | Cleanup workflow |
| `TENANT.DELETE.STEP_COMPLETED` | Cleanup step OK | Cleanup workflow |
| `TENANT.DELETE.STEP_FAILED` | Cleanup step failed | Cleanup workflow |
| `TENANT.DELETED.SUCCESS` | Tenant fully torn down | Cleanup workflow |
| `TENANT.DELETE.FAILED` | Cleanup terminal failure — human intervention required | Cleanup workflow |

### 2.2 Tag schema

Every event carries the tags below. `DomainEvent.Tags` is JSON, matching
the existing pattern in `OutboxSmtpSender.EmitSentAsync`.

```json
{
  "tenant_id":  "<guid>",
  "user_id":    "<owner-guid>",
  "workflow_id":"<elsa-workflow-instance-id>",
  "step":       "create_role|create_tenant_db|...|flip_status",
  "attempt":    "1|2|3",
  "correlation_id": "<request-id-from-POST-/register>"
}
```

- `step` and `attempt` are present only on `STEP_STARTED/COMPLETED/FAILED`.
- `correlation_id` comes from the inbound HTTP `X-Request-Id` header (or
  is generated by the API) and propagates through the workflow's
  variables bag; it lets operators trace a single registration from HTTP
  log → control-plane event → Elsa log.
- `tenant_id` is also denormalised into `DomainEvent.TenantId` for fast
  queries (the existing repository's `QueryAsync(tenantId, …)` already
  uses it).

### 2.3 Data payload schema (no PII)

```json
// STEP_STARTED
{ "step": "create_tenant_db", "attempt": 2 }

// STEP_COMPLETED
{ "step": "create_tenant_db", "attempt": 2, "duration_ms": 412 }

// STEP_FAILED
{
  "step": "create_tenant_db",
  "attempt": 2,
  "duration_ms": 1200,
  "error_class": "Npgsql.PostgresException",
  "error_code": "57P03",
  "retryable": true,
  "next_retry_at": "2026-04-16T10:15:33.456Z"
}

// TENANT.PROVISIONED.SUCCESS
{
  "total_duration_ms": 4823,
  "steps": [
    { "name": "create_role", "attempts": 1, "duration_ms": 18 },
    { "name": "create_tenant_db", "attempts": 2, "duration_ms": 412 },
    ...
  ]
}

// TENANT.PROVISION.FAILED
{
  "failed_at_step": "migrate_tenant_db",
  "attempts": 3,
  "terminal_error_class": "Npgsql.PostgresException",
  "compensation_outcome": "cleaned"  // "cleaned" | "partial" | "not_attempted"
}

// TENANT.REGISTERED.SUCCESS
{ "tenant_type": "personal|team|organization", "plan": "free" }
```

**Never** placed on events or tags: email addresses, user display names,
raw SQL, full connection strings, tenant role passwords. Use owner's
`user_id` — the User row in the control plane carries email.

### 2.4 Dedupe / idempotency on event writes

Steps 9, 10 and all `STEP_*` events use an insert-if-absent guard keyed
on `(tenant_id, type, tags->>'step', tags->>'attempt')`. The control
plane gets a partial unique index:

```sql
CREATE UNIQUE INDEX ix_platform_events_tenant_provision_step
  ON control_plane.platform_events ((tenant_id),
                                    (type),
                                    ((tags->>'step')),
                                    ((tags->>'attempt')))
  WHERE type LIKE 'TENANT.PROVISION.STEP_%';
```

On replay, duplicate writes silently no-op (catch the unique-violation).
`TENANT.PROVISIONED.SUCCESS` dedupes on `(tenant_id, type)` without the
attempt key.

---

## 3. Idempotency

The workflow is **at-least-once**. Elsa retries, RabbitMQ redeliveries,
and operator-triggered replays all happen. Every step must be a no-op
on second execution.

### 3.1 Per-step keys

| Step | Natural key | Re-run behaviour |
|---|---|---|
| `create_role` | role name `tamma_tenant_<id>` | DO-block `IF NOT EXISTS`; skip if role already present (detected via `pg_roles`) |
| `create_tenant_db` | `pg_database.datname` | Skip `CREATE DATABASE` if row present; verify owner matches expected role |
| `migrate_tenant_db` | EF Core `__EFMigrationsHistory` | EF skips already-applied migrations; only the last few should run on replay |
| `create_elsa_db` | as step 2 | as step 2 |
| `migrate_elsa_db` | as step 3 | as step 3 |
| `register_elsa` | `tenant_elsa_registry.tenant_id` PK, or Elsa API's own idempotency | `INSERT … ON CONFLICT (tenant_id) DO NOTHING`; Elsa API exposes GET before POST |
| `seed_defaults` | per-row natural key (e.g. `agent_config.key`, `prompt_override.(scope,role,action)`) | `INSERT … ON CONFLICT … DO NOTHING` across all seed rows |
| `flip_status` | `tenants.status = 'active'` is the sentinel | If already `active`, no-op; if `failed`, abort (caller must invoke cleanup first) |
| `emit_provisioned_event` | unique index on `(tenant_id, type)` | On replay, insert swallows unique-violation |
| `queue_welcome_email` | unique `(tenant_id, template='welcome', status != 'failed')` | `INSERT ... ON CONFLICT DO NOTHING` — welcome mail sends once per tenant, ever |

### 3.2 Non-trivial cases

**Step 3 (`migrate_tenant_db`) partial application.** EF Core writes each
migration's row to `__EFMigrationsHistory` inside the migration
transaction. If a migration fails mid-file, the transaction rolls back
and no row is written — replay is safe. However, DDL that writes outside
a transaction (rare in our migrations, but `CREATE INDEX CONCURRENTLY`
is one example) can leave orphan objects. We ban non-transactional DDL
in tenant migrations — enforced by the Migration Steward (see
`plans/db-per-tenant/01-control-plane-split.md`).

**Step 7 (`seed_defaults`) upsert semantics.** Seeds use upsert, not
append. If an operator ever needs to *reset* seeds for a tenant, that is
an explicit admin action, not a side-effect of workflow replay.

**Step 10 (`queue_welcome_email`) exactly-once-per-tenant.** Enforced by
a unique index on `(tenant_id, template) WHERE status <> 'failed'`. A
*failed* welcome email leaves the uniqueness slot open so an operator
can manually requeue.

---

## 4. Compensation

Goal: a failed provision leaves the system in one of two documented
states — **cleaned** (no tenant DB, no elsa DB, no role, tenant row
marked `failed`) or **quarantined** (tenant row marked `failed` with
`requires_manual_cleanup=true` and a clear ladder of what was and wasn't
done). We never leave silent half-provisioned tenants.

### 4.1 Compensation policy: "rollback all, retain evidence"

When a step fails after retries are exhausted, Elsa runs the
**compensation ladder** in reverse order of what succeeded:

```
Success up to step N → compensation runs from step N back to step 1.
```

| Succeeded through | Compensation order |
|---|---|
| Step 1 only | drop role |
| Step 2 | drop tenant db, drop role |
| Step 3 | drop tenant db (migrations disappear with it), drop role |
| Step 4 | drop elsa db, drop tenant db, drop role |
| Step 5 | drop elsa db, drop tenant db, drop role |
| Step 6 | deregister Elsa, drop elsa db, drop tenant db, drop role |
| Step 7 | deregister Elsa, drop elsa db, drop tenant db, drop role (seeds go with tenant db) |

### 4.2 Terminal state transitions

After compensation runs:

- **All compensation steps succeed** →
  `tenants.status = 'failed'`,
  `tenants.provisioning_failed_at = now()`,
  `tenants.failure_reason = 'clean'`,
  emit `TENANT.PROVISION.FAILED` with `compensation_outcome='cleaned'`.
  **The control-plane row is kept** — the user still has a login and can
  see the failed tenant on `/me`; the row is what lets us show the
  failure UX in §8. Operators can re-run the workflow from this state
  (see §4.4) or permanently remove the row via admin tooling.
- **A compensation step itself fails** →
  `tenants.status = 'failed'`,
  `tenants.failure_reason = 'partial'`,
  `tenants.requires_manual_cleanup = true`,
  emit `TENANT.PROVISION.FAILED` with `compensation_outcome='partial'`.
  An alert fires. Operators invoke `CleanUpFailedTenantWorkflow`
  manually.

### 4.3 `CleanUpFailedTenantWorkflow` (operator sidecar)

A separate **global** Elsa workflow, triggered manually by a platform
admin from the admin UI (`DELETE /api/admin/tenants/{id}/provision` or a
button in the ops dashboard). It is idempotent and safe to run multiple
times. Its steps mirror §4.1 in reverse, but each step first **probes**
for existence before attempting deletion.

Steps:

1. probe + `Elsa` deregister (skip if absent)
2. probe + `DROP DATABASE tamma_tenant_<id>_elsa` (skip if absent)
3. probe + `DROP DATABASE tamma_tenant_<id>` (skip if absent)
4. probe + `REASSIGN OWNED BY … ; DROP OWNED BY …; DROP ROLE tamma_tenant_<id>` (skip if absent)
5. Delete tenant-scoped rows in control plane (`tenant_memberships`,
   `tenant_elsa_registry`, and a scoped purge of `platform_events`
   *only* on operator confirmation — default is to retain for audit).
6. `UPDATE tenants SET deleted_at = now(), status = 'deleted',
   requires_manual_cleanup = false WHERE id = <id>`
7. Emit `TENANT.DELETED.SUCCESS`

If any probe-and-delete step still fails after 3 retries, emit
`TENANT.DELETE.FAILED` and leave the row in `requires_manual_cleanup`
state. A human DBA is now on the hook.

### 4.4 Retry from `failed` state

A platform admin may invoke `POST /api/admin/tenants/{id}/reprovision`.
Preconditions:

- `tenants.status = 'failed'`
- `tenants.requires_manual_cleanup = false`

The endpoint:

1. Verifies no tenant DB exists (belt-and-braces probe).
2. Flips `status = 'provisioning'`, resets `provisioning_failed_at`,
   increments `tenants.provisioning_attempt_count`.
3. Dispatches a new `CreateTenantWorkflow` instance with a fresh
   `workflow_id` (the old one stays in the audit log).

---

## 5. Timeouts and retries

### 5.1 Per-step config

| Step | Per-attempt timeout | Retry schedule | Max attempts | Retryable errors |
|---|---|---|---|---|
| `create_role` | 10 s | 5 s → 30 s → 2 min | 3 | `57P03`, connection failures |
| `create_tenant_db` | 30 s | 10 s → 1 min → 5 min | 3 | as above |
| `migrate_tenant_db` | 5 min | 30 s → 2 min → 10 min | 3 | connection, lock-wait |
| `create_elsa_db` | 30 s | 10 s → 1 min → 5 min | 3 | as above |
| `migrate_elsa_db` | 5 min | 30 s → 2 min → 10 min | 3 | as above |
| `register_elsa` | 30 s | 5 s → 30 s → 2 min | 3 | HTTP 5xx, network |
| `seed_defaults` | 60 s | 5 s → 30 s → 2 min | 3 | connection |
| `flip_status` | 10 s | 5 s → 30 s → 2 min | 3 | connection |
| `emit_provisioned_event` | 10 s | 1 s → 5 s → 30 s | 3 | connection |
| `queue_welcome_email` | 10 s | 1 s → 5 s → 30 s | 3 | connection |

### 5.2 Workflow-level overall timeout

- **Hard ceiling**: 2 hours from workflow start to `TENANT.PROVISIONED.SUCCESS`.
- **Budget alerting**: at 60s elapsed, emit a `warn`-level log (not an
  event) — this is the threshold for the UI "taking longer than usual"
  banner (§8).
- **Soft timeout**: at 15 min elapsed, emit `TENANT.PROVISION.SLOW` (a
  pure diagnostic event, not a failure) and page on-call.
- **Hard timeout**: at 2 h, short-circuit to compensation and emit
  `TENANT.PROVISION.FAILED` with `terminal_error_class = "WorkflowTimeout"`.

### 5.3 Error classification

| Class | Examples | Action |
|---|---|---|
| **Transient** | connection reset, `57P03` shutting down, HTTP 5xx, lock-wait timeout | retry per schedule |
| **Permanent-abort** | `42710` role exists but wrong owner, `42P04` DB exists but unexpected, schema assertion failure, `4xx` from Elsa register API | fail immediately (no retries), run compensation |
| **Ambiguous** | network timeout *during* a `CREATE DATABASE` | treat as transient; the idempotency probe on replay disambiguates |

The activity code inspects `PostgresException.SqlState` and the `.NET`
exception type (`TimeoutException`, `SocketException`,
`Npgsql.NpgsqlException`) to classify. Classification is deterministic
and stored in `data.error_class` on the `STEP_FAILED` event.

### 5.4 What the user sees vs time

| Elapsed | User state | Banner | Tenant-scoped API |
|---|---|---|---|
| 0–1 s | after 201 | "Setting up your workspace…" | 503 |
| 1–60 s | provisioning | "Setting up your workspace… usually < 60s" | 503 |
| 60 s – 5 min | provisioning | "Still setting up — almost done" | 503 |
| 5 – 15 min | provisioning-slow | "Taking longer than expected. We've been notified." | 503 |
| 15 min – 2 h | provisioning-alert | same + "Our team is looking into it." (on-call paged) | 503 |
| > 2 h | failed | "Setup failed. [Contact support] [Retry]" | 503 with `status="failed"` |

---

## 6. API status surface

### 6.1 `GET /api/auth/me` during provisioning

Extend the existing `MeResponse`
(`AuthEndpoints.GetMe`). New required fields on each tenant entry:

```json
{
  "userId": "...",
  "email": "owner@example.com",
  "tenants": [
    {
      "id": "...",
      "name": "Acme",
      "status": "provisioning",
      "provisioningStartedAt": "2026-04-16T10:01:00.000Z",
      "provisionedAt": null,
      "failureReason": null,
      "progressUrl": "/api/v1/tenants/.../provisioning-status"
    }
  ]
}
```

`status` values: `provisioning | active | failed | deleted`. If the
user has access to no `active` tenant, login still succeeds — their JWT
carries `tenantId=null` and they see a "workspace setting up" shell.

### 6.2 Tenant-scoped endpoints return 503

Middleware `RequireActiveTenant` runs after auth and before any handler
that expects a tenant DB. If `tenants.status != 'active'`, the middleware
returns:

```http
HTTP/1.1 503 Service Unavailable
Retry-After: 30
Content-Type: application/json

{
  "error": "tenant_not_ready",
  "status": "provisioning",
  "retryAfter": 30,
  "progressUrl": "/api/v1/tenants/{id}/provisioning-status"
}
```

`retryAfter` is dynamic: 30 s during normal window, 15 s if the workflow
just entered its final steps (detectable via the last event type), 0
(plus `status: "failed"`) once terminal failure has fired — in which
case the client should stop polling and show the failure UI.

### 6.3 `GET /api/v1/tenants/{id}/provisioning-status`

**Auth**: the requesting user must have a membership for `{id}` (any
role). Unlike most tenant-scoped endpoints this one is allowed during
`provisioning` — that's its whole purpose.

**Handler logic**:

1. Load tenant row from control plane. 404 if absent.
2. Query `platform_events WHERE tenant_id = <id> AND type LIKE
   'TENANT.PROVISION.STEP_%' OR type IN ('TENANT.PROVISIONED.SUCCESS',
   'TENANT.PROVISION.FAILED')` ordered by `created_at`.
3. Fold the event stream into a step ladder (projection — see §6.4).
4. Return JSON.

**Response**:

```json
{
  "tenantId": "...",
  "status": "provisioning",
  "startedAt": "2026-04-16T10:01:00.000Z",
  "completedAt": null,
  "estimatedCompletion": "2026-04-16T10:01:45.000Z",
  "currentStep": "migrate_tenant_db",
  "correlationId": "req_abc",
  "steps": [
    { "name": "create_role",        "status": "completed",   "attempts": 1, "startedAt": "...", "completedAt": "...", "durationMs": 18 },
    { "name": "create_tenant_db",   "status": "completed",   "attempts": 2, "startedAt": "...", "completedAt": "...", "durationMs": 412 },
    { "name": "migrate_tenant_db",  "status": "in_progress", "attempts": 1, "startedAt": "...", "completedAt": null,  "durationMs": null },
    { "name": "create_elsa_db",     "status": "pending",     "attempts": 0 },
    { "name": "migrate_elsa_db",    "status": "pending",     "attempts": 0 },
    { "name": "register_elsa",      "status": "pending",     "attempts": 0 },
    { "name": "seed_defaults",      "status": "pending",     "attempts": 0 },
    { "name": "flip_status",        "status": "pending",     "attempts": 0 },
    { "name": "queue_welcome_email","status": "pending",     "attempts": 0 }
  ]
}
```

Terminal states add:

```json
// Success
"status": "active",
"completedAt": "2026-04-16T10:01:45.678Z",
"totalDurationMs": 45678

// Failure
"status": "failed",
"failedAtStep": "migrate_tenant_db",
"failureReason": "clean",       // "clean" | "partial"
"requiresManualCleanup": false,
"supportRefId": "evt_<last-failed-event-id>"
```

### 6.4 Status projection (read model)

Rebuilt on every request from events (cheap — bounded at ~30 events max
per tenant). Fold rules:

```
initialise state: steps[1..10] all "pending", attempts=0, startedAt=null
for each event in order:
  if STEP_STARTED:        state[step].status = "in_progress";
                          state[step].startedAt = event.time;
                          state[step].attempts = event.tags.attempt
  if STEP_COMPLETED:      state[step].status = "completed";
                          state[step].completedAt = event.time;
                          state[step].durationMs = event.data.duration_ms
  if STEP_FAILED:         state[step].status = "in_progress"  // retry still possible
                          state[step].lastError = event.data.error_class
  if PROVISIONED_SUCCESS: set tenant.status = "active"
  if PROVISION_FAILED:    set tenant.status = "failed";
                          state[event.data.failed_at_step].status = "failed"
```

The middleware in §6.2 may later replace the folder with a materialised
view if this projection becomes a hot path, but for MVP we fold on each
request. It's ~30 events; the cost is negligible.

`estimatedCompletion` is derived from a rolling average of recent
successful provisions (stored in the control plane as a single
`provisioning_p50_ms` gauge, updated by `TENANT.PROVISIONED.SUCCESS`
handlers). Default fallback: `startedAt + 45 seconds`.

---

## 7. Welcome email — which DB, which outbox

### 7.1 Decision

**The welcome email is queued to the *control-plane* outbox, not the
tenant outbox.**

Reasoning:

1. **The tenant DB only becomes useful at step 8** (status flips to
   active). Enqueuing the welcome into the tenant's own outbox table
   works — step 10 runs *after* step 7 — but it couples email delivery
   to tenant-DB availability. If the tenant DB goes offline for a
   maintenance window an hour later, a still-pending welcome mail would
   be stuck. The control-plane outbox is more durable.
2. **The existing `OutboxSmtpSender` already scans a single outbox
   table** in the control-plane DB (`apps/tamma-elsa/src/Tamma.Api/Services/Email/OutboxSmtpSender.cs`).
   Reusing it costs zero new code. A per-tenant outbox would require a
   tenant-DB-aware scan loop — non-trivial.
3. **Welcome email payload is not tenant-scoped PII.** It's the owner's
   email (already in the control-plane `users` table) plus a link. No
   tenant DB content.
4. The outbox row carries `TenantId` as a tag anyway so per-tenant
   reporting still works.

Verification email and password reset already sit on the control-plane
outbox (because at registration time the user exists but the tenant may
not yet — see `AuthEndpoints.Register`). Welcome follows the same
rule: **all lifecycle emails about tenant onboarding live on the
control-plane outbox**. Per-tenant *content* emails (notifications,
digests) can live on per-tenant outboxes once we have them; that's a
later epic.

### 7.2 Edge case: welcome enqueue succeeds, delivery fails

This is the existing SMTP-outbox story, unchanged:

- Step 10 inserts the outbox row → row becomes the durable contract.
- Step 10 returns success; workflow emits `TENANT.PROVISIONED.SUCCESS`.
- `OutboxSmtpSender` attempts delivery, retries per its own backoff
  (60s → 5m → 30m → 2h → 6h), and ultimately either emits
  `EMAIL.SENT.SUCCESS` or `EMAIL.SENT.FAILED`.
- **The tenant's provisioning status is not gated on email delivery.**
  `TENANT.PROVISIONED.SUCCESS` means the tenant is usable; the user's
  ability to receive a welcome email is secondary.
- Operators monitor `EMAIL.SENT.FAILED WHERE template='welcome'` and
  reach out manually if a new tenant's owner can't log in. (Optional
  enhancement: if the welcome email is marked `failed` *and* the owner
  has never logged in within 7 days, emit a `TENANT.ONBOARDING.STALLED`
  event for the CS team. Deferred.)

### 7.3 Edge case: workflow succeeds, outbox insert fails

Step 10 has its own retry (§5.1, 3 attempts, 1s → 5s → 30s backoff). If
it *still* fails after 3 attempts we have a choice:

- **Treat as non-fatal** and still emit `TENANT.PROVISIONED.SUCCESS`
  with a `welcome_email_queued=false` flag in the data. **Preferred.**
  The tenant works; the welcome email is recoverable manually.
- ~~Treat as fatal and compensate.~~ Rejected: it makes the whole
  provision failure-prone on a mail-only fault.

---

## 8. Failure UX

### 8.1 User-facing banners (dashboard shell)

Rendered by the dashboard based on the tenant's `status` in the JWT +
`GET /me` response. Dashboard polls
`/api/v1/tenants/{id}/provisioning-status` every 5 s while
`status="provisioning"`.

| Banner | Trigger | Dismissible |
|---|---|---|
| "Setting up your workspace. This usually takes less than 60 seconds." | `status=provisioning`, elapsed < 60 s | No |
| "Still setting up — almost done." | `status=provisioning`, 60 s ≤ elapsed < 5 min | No |
| "Taking longer than expected. We've been notified." | `status=provisioning`, 5 min ≤ elapsed < 15 min | No |
| "This is taking unusually long. Our team has been alerted and will follow up." + support contact | 15 min ≤ elapsed | No |
| "Setup failed. [Contact support] [Try again]" | `status=failed`, `requires_manual_cleanup=false` | No |
| "Setup failed and requires manual cleanup. [Contact support]" | `status=failed`, `requires_manual_cleanup=true` | No |

During any `provisioning` state, navigation surfaces outside the welcome
shell are disabled (greyed links, tooltip "available once setup
finishes").

### 8.2 Operator dashboard — stuck-tenant query

Problem: find tenants stuck in `provisioning` for more than 15 minutes.

Primary query (event-sourced, always correct):

```sql
-- Control plane
SELECT t.id, t.name, t.slug, t.owner_id, t.provisioning_started_at,
       EXTRACT(EPOCH FROM (now() - t.provisioning_started_at)) AS elapsed_s,
       (SELECT pe.tags->>'step'
          FROM platform_events pe
         WHERE pe.tenant_id = t.id
           AND pe.type = 'TENANT.PROVISION.STEP_STARTED'
         ORDER BY pe.created_at DESC
         LIMIT 1) AS current_step,
       (SELECT pe.data->>'error_class'
          FROM platform_events pe
         WHERE pe.tenant_id = t.id
           AND pe.type = 'TENANT.PROVISION.STEP_FAILED'
         ORDER BY pe.created_at DESC
         LIMIT 1) AS last_error
FROM tenants t
WHERE t.status = 'provisioning'
  AND t.provisioning_started_at < now() - interval '15 minutes'
ORDER BY t.provisioning_started_at ASC;
```

Exposed via:

- `GET /api/admin/tenants/stuck` (platform admin only).
- A dashboard panel under **Admin → Tenants → Stuck provisioning**
  showing: tenant id, owner email, elapsed, current step, last error,
  buttons `[View events] [Force retry] [Force cleanup]`.

Alerting (observability repo, not this doc): an alert fires when the
count of stuck tenants > 0 for > 5 minutes, paging on-call. A second
alert fires on any `TENANT.PROVISION.FAILED` event, so the responders
know whether they're chasing a transient or a pattern.

### 8.3 Alert payload

When on-call is paged, the alert links to:

```
https://admin.tamma.dev/admin/tenants/<id>/provisioning-status
```

which shows the same step ladder from §6.3 plus the raw event list and
links to:

- force retry (`POST /api/admin/tenants/{id}/reprovision`) — only
  available after cleanup,
- force cleanup (`POST /api/admin/tenants/{id}/cleanup`) — invokes
  `CleanUpFailedTenantWorkflow`,
- hard delete (`DELETE /api/admin/tenants/{id}`) — removes the control
  plane row entirely; audit trail retained in `platform_events` unless
  a second confirmation flag is passed.

---

## 9. Tests

### 9.1 Integration tests (required before Story 18-3 closes)

| # | Scenario | What it verifies |
|---|---|---|
| T1 | **Happy path** | POST `/register` returns 201 within 200 ms. Tenant row has `status='provisioning'`. Workflow completes within 30 s in test. Final tenant row has `status='active'`, `provisioned_at != null`. Events in order: `TENANT.REGISTERED`, 10× `STEP_STARTED/COMPLETED` pairs, `TENANT.PROVISIONED.SUCCESS`. A welcome email outbox row exists with `template='welcome'`. |
| T2 | **/me reflects provisioning** | Immediately after 201, user logs in, `GET /me` returns tenant with `status='provisioning'`, `progressUrl` populated. After workflow completes, `status='active'`. |
| T3 | **Tenant endpoint returns 503 during provisioning** | Log in, hit a tenant-scoped endpoint during the provisioning window. Expect 503 with body matching §6.2. After workflow completes, same request succeeds. |
| T4 | **Progress endpoint is accessible during provisioning** | `GET /tenants/{id}/provisioning-status` returns 200 with the step ladder. Not blocked by the `RequireActiveTenant` middleware. |
| T5 | **Workflow retry — transient failure** | Inject a transient `Npgsql.PostgresException{SqlState='57P03'}` on the first `CREATE DATABASE` attempt via a test-only fault-injection hook. Verify: step 2 attempts=2 on `STEP_COMPLETED`. `status='active'` at the end. |
| T6 | **Workflow terminal failure** | Inject a permanent failure at step 3 (migration) that reproduces on all 3 attempts. Verify: `TENANT.PROVISION.FAILED`, tenant db + role removed (compensation), `tenants.status='failed'`, `requires_manual_cleanup=false`. Verify alert hook fires. |
| T7 | **Compensation leaves quarantine on partial failure** | Inject failure at step 4 (create_elsa_db) then inject failure in the compensation's drop-tenant-db step. Verify: `tenants.status='failed'`, `requires_manual_cleanup=true`. Verify `CleanUpFailedTenantWorkflow` can be run manually and clears the state. |
| T8 | **Concurrent registrations with same tenant slug** | Two concurrent POSTs, identical slug. One succeeds with 201. Other gets 409 `slug_taken`. No dangling roles / databases created. |
| T9 | **Workflow re-entry / at-least-once** | Run workflow to completion, then re-dispatch the same workflow with the same tenantId. Verify: every step's idempotency key trips, zero new events emitted (unique index holds), tenant remains `active`, no duplicate welcome email in outbox. |
| T10 | **Long-run soft timeout fires event** | Slow the migration activity to 16 min via a test hook (wall-clock fast-forwarded). Verify a `TENANT.PROVISION.SLOW` event is emitted exactly once. |
| T11 | **Hard timeout triggers compensation** | Use a fake workflow clock set to 2 h 1 min after start. Verify workflow short-circuits to compensation and emits `TENANT.PROVISION.FAILED` with `WorkflowTimeout`. |
| T12 | **Reprovision path** | From a T6 end-state, `POST /admin/tenants/{id}/reprovision` succeeds, new workflow runs, tenant becomes `active`. `provisioning_attempt_count` is 2. |
| T13 | **Welcome email enqueue failure does not fail the workflow** | Inject outbox insert failure on all 3 attempts of step 10. Verify `TENANT.PROVISIONED.SUCCESS` is still emitted with `welcome_email_queued=false`. Tenant usable. |
| T14 | **No PII in events** | Scan `platform_events.data/tags` for the T1 run; assert absence of the owner's email string and any raw SQL. |

### 9.2 Unit tests

Standard coverage on:

- Step activity classes — pure functions `(input) → (output | TammaError)`.
- Compensation ladder selector — "given succeeded through step N, return
  the compensation list".
- Status projection folder — property tests: folding any prefix of a
  valid event sequence produces a monotonic status ladder (no step
  regresses from `completed` to `in_progress`).
- Error classifier — table-driven: Postgres SQL-state → `{transient |
  permanent}`.

### 9.3 Contract / observability tests

- Every emitted event validates against the JSON schema in §2.3 —
  enforced by a shared `DomainEventValidator` in test setup.
- Log redaction — the fault-injected failure in T6 must never include
  the tenant role password in a log line; CodeQL `private-data` rule
  catches this in CI.

---

## 10. Open design choices (defer to companion docs, noted here for reference)

### 10.1 Where does the workflow run?

Two-tier topology (see `02-elsa-two-tier.md`): a **global Elsa** in the
control plane runs `CreateTenantWorkflow` and `CleanUpFailedTenantWorkflow`;
per-tenant Elsa instances run after step 6. This file assumes that
shape.

### 10.2 Which database holds `platform_events`?

`01-control-plane-split.md` decides. This doc assumes **control plane**,
which is why the existing `DomainEvent` entity (currently shared) works
unchanged from its API surface. If ultimately the project settles on a
split platform/tenant event model, §2.4's unique index moves with it.

### 10.3 How is the workflow dispatched from the API?

Out of scope for this design. The options (pick one in
`02-elsa-two-tier.md`):

- **In-process** via `IWorkflowRunner.RunAsync` (simplest, fine while
  API and global Elsa share a process).
- **HTTP trigger** to the Elsa server (decoupled; the API is one-way).
- **RabbitMQ message** consumed by the Elsa dispatcher (most durable;
  survives Elsa restarts without dropping work).

Regardless of choice, the **contract** from this document's perspective
is: after `AuthEndpoints.Register` commits and emits
`TENANT.REGISTERED.SUCCESS`, something guaranteed-at-least-once causes
`CreateTenantWorkflow(tenantId)` to run. The existing email outbox
pattern (commit then scan) is the preferred blueprint if we want a
durable, in-process, zero-new-infra option — a
`TenantProvisioningDispatcher` hosted service scans for tenants stuck
in `provisioning` with no workflow instance yet and (re)dispatches.

---

## 11. Summary of key decisions

1. **Registration returns 201 immediately** after writing three control-plane rows and emitting `TENANT.REGISTERED.SUCCESS`.
2. **One global Elsa workflow** (`CreateTenantWorkflow`) with ten named steps, each with its own idempotency key, retry schedule, and compensation.
3. **Event taxonomy mirrors the email outbox** — `STEP_STARTED / STEP_COMPLETED / STEP_FAILED` plus terminal `PROVISIONED.SUCCESS` / `PROVISION.FAILED`. Dedupe via partial unique index on `(tenant_id, type, step, attempt)`.
4. **Compensation policy is "rollback all, retain evidence"** — the tenant row stays as `status='failed'` (never silently deleted); every resource created by the workflow is reversed by `CleanUpFailedTenantWorkflow`; partial compensation failures are quarantined (`requires_manual_cleanup=true`) for human attention.
5. **Tenant-scoped endpoints return 503** with `Retry-After: 30` and a pointer to a dedicated, always-accessible progress endpoint `GET /api/v1/tenants/{id}/provisioning-status`.
6. **Welcome email queues to the *control-plane* outbox**, not the tenant's own outbox — reuses the existing `OutboxSmtpSender` untouched; delivery failure is decoupled from provisioning success.
7. **Retries and timeouts are per-step** (10 s–5 min per attempt, 3 attempts, exponential backoff) plus a **workflow-wide 2 h hard ceiling** with a soft 15-min alerting threshold and a 60-s UI threshold.
8. **No PII on events** — only `tenant_id`, `user_id`, `step`, `attempt`, `correlation_id`, `error_class`, `duration_ms`.
9. **Operators get a stuck-tenant query** on control plane + admin endpoints for reprovision, manual cleanup, and hard delete.
10. **14 integration tests** cover happy path, retry, terminal failure, quarantine, concurrency, re-entry, timeout, reprovision, welcome-email fault, and PII-leak check.
