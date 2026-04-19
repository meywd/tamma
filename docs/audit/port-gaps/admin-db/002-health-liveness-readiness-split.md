# Finding 002: `/health` endpoint lacks live/ready split

**Scope**: admin-db
**Severity**: P3
**Status**: Incomplete
**Estimated port effort**: 2h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Notes**: `AddHealthChecks().AddNpgSql(..., tags: ["ready"])` plus three `MapHealthChecks` endpoints — `/health` (all checks), `/health/live` (always-pass liveness), `/health/ready` (only ready-tagged checks). Docker compose / k8s manifests aren't updated here (deployment scope), but the routes are now available.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/`.

- File: TS didn't ship a separate live/ready split either. The `GET /api/health` handler was a trivial liveness probe, and `/api/admin/health` (finding 001) was the aggregator.
- Contract/behavior: single shallow liveness endpoint; no readiness/startup differentiation. This finding documents a *desired* split that neither TS nor C# implements — but which Kubernetes deployments and zero-downtime rollouts typically assume.
- Key code: n/a (nothing there to quote).
- Dependencies: none.
- Tests that exercised this: none.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/HealthEndpoints.cs:5-6` and `apps/tamma-elsa/src/Tamma.Api/Program.cs:311`
- Contract/behavior: two routes are mapped but neither is a true liveness/readiness split:
  - `GET /api/health` — hardcoded `{ status:"ok", version:"2.0.0" }` from the static `GetHealth()` method.
  - `GET /health` — ASP.NET Core `MapHealthChecks("/health")` which runs a real `AddNpgSql(connectionString)` check.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/HealthEndpoints.cs (current)
public static IResult GetHealth()
    => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow, version = "2.0.0" });

// apps/tamma-elsa/src/Tamma.Api/Program.cs
builder.Services.AddHealthChecks().AddNpgSql(connectionString);
...
app.MapHealthChecks("/health");
```

- Dependencies: `Microsoft.Extensions.Diagnostics.HealthChecks`, `AspNetCore.HealthChecks.NpgSql`.
- Tests: none.

## 3. The gap

Concrete behavioral difference — what a Kubernetes/Docker caller experiences.

- TS did: single `/api/health` (static). Docker Compose healthchecks used it. No separation.
- C# does: adds a *real* DB-checking `/health` (improvement), plus retains `/api/health` (static). Still no liveness/readiness/startup split.
- For a caller sending a readiness probe during startup while Postgres is still warming, TS would return 200 (lie), and C# returns 503 from `/health` (correct) but 200 from `/api/health` (lie).
- In production with Kubernetes rolling deploys, this means: no startup probe to cover slow EF migration application on cold start (migrations run synchronously at boot per `Program.cs:530-577`). No distinction between "process alive, not serving" and "process serving but dependencies degraded".

Error paths:
- TS error path: 200 always.
- C# error path: `/health` returns 503 when Postgres is down; `/api/health` returns 200 always.

## 4. Gap from stories

- Referenced story: none directly. `docs/architecture.md` mentions Docker deployment but not probe separation.
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

CLAUDE.md does not mandate this surface. ASP.NET Core's `MapHealthChecks` supports `Predicate = r => r.Tags.Contains("ready")` for tag-based gating; this is the idiomatic fix.

## 5. Status

- **Classification**: Incomplete — C# is an improvement (real DB probe) but still missing the split.
- **What's needed to finish**:
  1. Tag the Postgres check as `"ready"`: `AddNpgSql(...).AddCheck("self", ..., tags: new[] {"live"})` pattern.
  2. Map three routes: `/health/live` (liveness, tags:["live"]), `/health/ready` (readiness, tags:["ready"]), `/health` (all).
  3. Add a startup probe distinct from readiness once migrations run out-of-process.
- **Is it "just a stub" or is scope missing?** Scope was never specified. C# is currently *closer* to correct than TS was — treat this as an enhancement opportunity, not a regression.
- **Blockers**: coordination with `docker-compose.yml` / deployment manifests to update probe paths.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Program.cs` (change `AddHealthChecks` configuration and `MapHealthChecks` calls), `docker-compose.yml` healthcheck URLs.
- Files to create: none.
- Tests to add: `Tamma.Api.Tests/Health/ReadinessTests.cs` — liveness returns 200 when DB is down; readiness returns 503 when DB is down.
- Estimated effort: 2h broken down as:
  - Health check tagging + route mapping: 1h
  - Tests + compose probe update: 1h

## References

- TS source: `packages/api/src/` (commit `9e9a57c~1`) — no split existed
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/HealthEndpoints.cs`, `apps/tamma-elsa/src/Tamma.Api/Program.cs`
- Story: none
- Related findings: `docs/audit/port-gaps/admin-db/001-admin-health-aggregator-stub.md`
- CLAUDE.md section: none
