# Finding 001: Admin health aggregator regressed to trivial stub

**Scope**: admin-db
**Severity**: P2
**Status**: Not-yet-implemented (stub)
**Estimated port effort**: 8h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/admin/health-routes.ts`.

- File: `packages/api/src/routes/admin/health-routes.ts:60-170`
- Contract/behavior: `GET /api/admin/health` pings six infrastructure dependencies in parallel — Tamma API self, PostgreSQL (real `SELECT 1`), ELSA Server (`/health`), OpenSearch (`/_cluster/health`), RabbitMQ Management API (`/api/health/checks/alarms` with basic auth), and ChromaDB (`/api/v2/heartbeat`) — and returns a `{ services: ServiceCheck[], checkedAt }` envelope with `{ name, status: 'healthy'|'unhealthy'|'unknown', responseTime, checkedAt, details? }` per service. Each HTTP probe uses `AbortController` with a 5s timeout.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/admin/health-routes.ts (9e9a57c~1)
const checks: Promise<ServiceCheck>[] = [
  (async (): Promise<ServiceCheck> => ({ name: 'Tamma API', status: 'healthy', ... }))(),
  // PostgreSQL real SELECT 1
  (async (): Promise<ServiceCheck> => {
    if (!options?.pgPool) { return { name: 'PostgreSQL', status: 'unknown', ... }; }
    const start = Date.now();
    try { await options.pgPool.query('SELECT 1'); return { name: 'PostgreSQL', status: 'healthy', ... }; }
    catch (err) { return { name: 'PostgreSQL', status: 'unhealthy', details: ..., ... }; }
  })(),
  checkHttpService('ELSA Server', `${process.env['ELSA_SERVER_URL']}/health`),
  checkHttpService('OpenSearch', `${process.env['OPENSEARCH_URL']}/_cluster/health`),
  checkHttpService('RabbitMQ', `${process.env['RABBITMQ_MANAGEMENT_URL']}/api/health/checks/alarms`, { Authorization: `Basic ${...}` }),
  checkHttpService('ChromaDB', `${process.env['CHROMADB_URL']}/api/v2/heartbeat`),
];
const results = await Promise.all(checks);
return reply.send({ services: results, checkedAt: new Date().toISOString() });
```

- Dependencies: raw `fetch` + `AbortController`, `pgPool.query`, env vars `ELSA_SERVER_URL`, `OPENSEARCH_URL`, `RABBITMQ_MANAGEMENT_URL`, `RABBITMQ_USER`, `RABBITMQ_PASSWORD`, `CHROMADB_URL`, JWT cookie verification with admin-or-owner role check.
- Tests that exercised this: internal smoke checks via dashboard; no dedicated route test was found in `packages/api/src/__tests__/`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:12-15`
- Contract/behavior: returns a static success literal. No dependency ping, no timeouts, no parallel fan-out, no per-service result array. The response shape is incompatible with the TS one.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs (current)
public static Task<IResult> GetHealth()
{
    return Task.FromResult(Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow, database = "connected" }));
}
```

- Dependencies: none — the method takes zero DI arguments. ASP.NET Core has `AddHealthChecks().AddNpgSql(connectionString)` registered in `Program.cs:158` but that feeds `/health`, not `/api/admin/health`.
- Tests: no Tamma.Api.Tests file currently asserts shape of `/api/admin/health`. Any dashboard that reads `services[]` would silently read an empty structure.

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: ping 6 services in parallel, return an array of status objects with latency and details, catching per-service failures so one unhealthy service doesn't poison the others.
- C# does: returns a single success literal regardless of whether Postgres, ELSA, OpenSearch, RabbitMQ, or ChromaDB are reachable — even if they're all down.
- For a caller sending `GET /api/admin/health`, TS returns `{ services: [{name:"PostgreSQL",status:"unhealthy",details:"connection refused",...}, ...], checkedAt: "..." }` and C# returns `{ status:"ok", timestamp:"...", database:"connected" }` hardcoded.
- In production with existing data / deployed clients, this means: the admin dashboard's "Infrastructure Health" tile is effectively a lie. Operators lose the ability to tell at a glance whether ELSA, OpenSearch, RabbitMQ, or ChromaDB is degraded.

Error paths:
- TS error path: per-service failures are captured as `{ status:"unhealthy", details:"HTTP 503" | "Connection failed" | ... }` inside the 200 response. Auth failures: 401 (no JWT), 403 (wrong role).
- C# error path: none. Unconditional 200. Auth is enforced by `.RequireAuthorization("AdminAccess")` at the group level (`Program.cs:338`) so 401/403 still work, but there is no liveness signal beyond "endpoint reachable".

## 4. Gap from stories

Which Epic / story file describes what this surface SHOULD be?

- Referenced story: `docs/stories/epic-16/16-3-admin-dashboard.md` (admin dashboard) covers the dashboard consumer; the health aggregator itself is implied by "infrastructure status tile".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story — spec gap

No story calls out the six-service envelope explicitly — the TS implementation went ahead of spec, and the C# port simplified it away. Treat the TS behavior as the de-facto spec.

## 5. Status

- **Classification**: Not-yet-implemented (stub)
- **What's needed to finish**:
  1. Add an `IAdminHealthService` that wraps Postgres `SELECT 1`, `HttpClient` probes against ELSA/OpenSearch/RabbitMQ/ChromaDB with `CancellationTokenSource(TimeSpan.FromSeconds(5))`.
  2. Aggregate probes with `Task.WhenAll` and serialize the `ServiceCheck[]` envelope to match TS.
  3. Surface env vars via `IConfiguration` (`Elsa:ServerUrl`, `OpenSearch:Url`, `RabbitMQ:ManagementUrl`, `RabbitMQ:User`, `RabbitMQ:Password`, `ChromaDb:Url`).
  4. Unit test: one healthy Postgres, one mocked 503 ELSA; expected shape `services[0].status==="healthy"`, `services[1].status==="unhealthy"`.
- **Is it "just a stub" or is scope missing?** Just a stub — the scope was fully understood in TS and deliberately shed during the port. CLAUDE.md does not mandate this surface; the spec gap is real but low-risk.
- **Blockers**: none. RabbitMQ basic-auth wiring needs a named `HttpClient` or per-request Authorization header; pattern already exists in `Program.cs:83-100`.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`, `apps/tamma-elsa/src/Tamma.Api/Program.cs` (DI registration).
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Services/AdminHealthService.cs` + interface.
- Tests to add: `Tamma.Api.Tests/Admin/AdminHealthTests.cs` with `MockHttpMessageHandler` for each probe; assert envelope shape, per-service isolation (one failure doesn't poison the others), 5s timeout trips `unhealthy`.
- Estimated effort: 8h broken down as:
  - Service + probes (Postgres + 4 HTTP): 4h
  - DI + config wiring: 1h
  - Tests with `MockHttpMessageHandler`: 3h

## References

- TS source: `packages/api/src/routes/admin/health-routes.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: `docs/stories/epic-16/16-3-admin-dashboard.md`
- Related findings: `docs/audit/port-gaps/admin-db/002-health-liveness-readiness-split.md`
- CLAUDE.md section: none (not spec'd)
