# Finding 020: No rate limiting on any settings/provider/agent endpoint

**Scope**: providers
**Severity**: P2 (abuse surface)
**Status**: Incomplete port
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/agents/index.ts` and
`git show 9e9a57c~1:packages/api/src/routes/agents/agent-config-routes.ts`.

- TS used `@fastify/rate-limit` scoped to each route group. Per-route caps:
  - `GET /api/v1/agents/config` — 100 req/min
  - `PUT /api/v1/agents/config` — 30 req/min (tighter for writes)
  - `POST /api/v1/agents/config/validate` — 100 req/min
  - `GET /api/v1/agents/:role/resolve` — 100 req/min (default group rate)
  - `POST /api/v1/agents/resolve-for-phase` — 100 req/min

```typescript
// packages/api/src/routes/agents/index.ts (9e9a57c~1) — lines 37-44
await scoped.register((await import('@fastify/rate-limit')).default, {
  max: 100,
  timeWindow: '1 minute',
  keyGenerator: (request) => request.ip,
});
```

```typescript
// packages/api/src/routes/agents/agent-config-routes.ts (9e9a57c~1) — lines 132-138
app.put(
  '/config',
  {
    config: {
      rateLimit: { max: 30, timeWindow: '1 minute' },
    },
  },
  ...
);
```

- Cap was per-IP. Exceeded → `429 Too Many Requests` with `retry-after` header.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- No rate limiting is registered. `grep -rn "AddRateLimiter\|UseRateLim" apps/tamma-elsa/src/Tamma.Api/` returns nothing.
- `Program.cs` does not call `builder.Services.AddRateLimiter(...)` or `app.UseRateLimiter()`.
- All `/api/v1/agents/*`, `/api/config/*`, `/api/providers/*` routes are unthrottled from the server side.

## 3. The gap

- Any authenticated member can hit the endpoints at unlimited QPS.
- `POST /api/providers/diagnostics` (ingest) — can be used to fill the `provider_diagnostics` table by a misbehaving Elsa worker or a hostile tenant member.
- `PUT /api/v1/agents/config` — can be used to thrash the `agent_configs` row (each update increments `version`), forcing unbounded version numbers.
- `POST /api/providers/providers/create` — creates in-memory `ProviderSession` entries, each holding a provider state machine. Session TTL cleanup at `ProviderSessionCleanupService` runs every N seconds, but a flood can keep `_sessions` at `MAX_SESSIONS` until GC catches up.
- `GET /api/providers/diagnostics/query` — expensive full-table scan without `(TenantId, CreatedAt DESC)` index (finding 023). Unthrottled callers can melt the DB.

For a caller doing `for (let i=0; i<10000; i++) fetch('/api/v1/agents/config', {method:'PUT', ...})`:
- TS: 99 succeed, the rest return `429` until the minute rolls over.
- C#: all 10000 succeed (subject to network/CPU limits).

Error paths:
- TS: `429 {error:'Rate limit exceeded, retry in X seconds'}` with `retry-after` header.
- C#: no throttling error; `200` / `500` depending on DB contention.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md`, and implicitly the whole Epic 16 (auth foundation) expected rate-limiting.
- Story 9-1 (implementation plan task file) mentions "Rate limiting: 100 req/min read, 30 req/min write" at the top of `agent-config-routes.ts`.
- CLAUDE.md doesn't explicitly mandate rate limits but the security-first posture implies it.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression).
  - [ ] Matches C# behavior.

## 5. Status

- **Classification**: Incomplete port.
- **What's needed to finish**:
  1. Add `builder.Services.AddRateLimiter(...)` in `Program.cs` with a global IP-based policy.
  2. Add `app.UseRateLimiter()` in the request pipeline.
  3. Define named policies: `ConfigRead` (100/min), `ConfigWrite` (30/min), `ProviderIngest` (500/min — higher because Elsa workers batch), `ProviderExecute` (50/min — more expensive).
  4. Apply `.RequireRateLimiting("ConfigRead" | "ConfigWrite" | ...)` on each endpoint per the TS matrix.
  5. Configure `RateLimitOptions.RejectionStatusCode = 429` and write a custom response body that matches TS shape.
  6. Consider the .NET 7+ `FixedWindowLimiter` or `SlidingWindowLimiter`.
- **Is it "just a stub" or is scope missing?** The scope was understood (the TS comment block explicitly states per-route limits); rate limiting was simply omitted.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` (register + use + apply)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Infrastructure/RateLimitPolicies.cs` (helper for policy definitions)
- Tests to add:
  - `AgentConfig_PUT_31stCallInAMinute_Returns429`
  - `Diagnostics_Ingest_FloodTest_RejectsAt500InAMinute`
  - `ProvidersCreate_FloodTest_RejectsAt50InAMinute`
- Estimated effort: 2h.

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed
- **Commit**: `32bba50` `fix(providers): land P1 sanitizer/clamping/chain/rate-limit fixes [findings 006, 007, 011, 020]`
- **Notes**: Wired `AddRateLimiter` + `UseRateLimiter` in `Program.cs` with four named fixed-window policies: `ConfigRead` (100/min), `ConfigWrite` (30/min), `ProviderIngest` (500/min for Elsa-batched diagnostics), `ProviderExecute` (50/min for expensive provider dispatch). `RejectionStatusCode = 429`. Applied per-route via `RequireRateLimiting` on `/api/v1/agents`, `/api/config`, `/api/providers` route groups — read endpoints inherit the group default, write endpoints override on the verb. Note: this stream's `RateLimitService` is a per-key counter for auth flows (registration/reset), not for HTTP throttling — the new `AddRateLimiter` integration is the right primitive for per-IP HTTP rate limits.

## References

- TS source: `packages/api/src/routes/agents/index.ts:37-44`, `packages/api/src/routes/agents/agent-config-routes.ts:113-138`, `packages/api/src/routes/settings/index.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Program.cs` (no rate-limit registration)
- Story: `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md`
- Related findings: `002-settings-rbac-status.md`, `023-diagnostics-missing-composite-indexes.md`
- CLAUDE.md section: "Security Requirements" (implicit)
