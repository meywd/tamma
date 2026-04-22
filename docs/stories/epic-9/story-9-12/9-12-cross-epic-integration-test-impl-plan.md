# Story 9-12 Implementation Plan — Cross-Epic Integration Test Harness

**Status**: Planned (2026-04-20)
**Story brief**: [`9-12-cross-epic-integration-test.md`](./9-12-cross-epic-integration-test.md)
**Team**: Layer 4 Team A (Epic 9 completion)
**Runs in worktree**: `/home/meywd/tamma-worktrees/layer-4-team-a-9-12-integration-test`
**Branch**: `feat/story-9-12-cross-epic-integration`

---

## 1. Objective

Ship an end-to-end integration test that exercises the full cross-epic chain
from Elsa workflow dispatch through tenant-scoped prompt resolution to
diagnostics recording. The test is the regression harness that proves Epic 9
(agent resolution), Epic 17 (tenancy), Epic 18 (users/orgs), Epic 27 (prompt
store), and Epic 28 (database-per-tenant) all cooperate under multi-tenant
isolation. It is the earliest stable target Layer 5's section 5.1
cross-epic harness inherits from — Layer 5 extends this test suite rather
than replacing it.

## 2. Dependencies

Hard blockers (must be merged before this plan starts):

- **Story 9-5** (provider chain) — resolver endpoint callable with tenant header.
- **Story 9-9** (engine integration) — TS engine calls C# API via service JWT.
- **Story 9-10** (CLI wiring) — optional; enables CLI-driven test variant.
- **Story 9-11** (Elsa diagnostics queue) — activity HTTP hops land on C# API.
- **Story 27-6** (Elsa prompt integration) — `ResolvePromptFromRegistryActivity`
  sends `X-Tenant-Id`.
- **Story 18-3** (organization / tenant creation endpoints) — create tenants in setup.
- **Story 28-3** (tenant DbContext factory) — per-tenant connections routed by factory.
- **Story 28-8** (tenant context middleware) — `X-Tenant-Id` / JWT flow wired.
- **Migrations 008–017** applied on the test database.

Soft dependency:

- **Story 12-7e** (Elsa tool loop) — if merged, the harness also covers the
  tool loop path. If not merged, the harness gates that test case behind a
  feature flag and treats it as a skipped placeholder.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.IntegrationTests/CrossEpic/CrossEpicIntegrationTests.cs` | xUnit collection that owns the 7 test cases from the brief. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.IntegrationTests/CrossEpic/Fixtures/TwoTenantFixture.cs` | Testcontainers-backed fixture that brings up a real Postgres, applies all migrations, seeds tenants A and B, and returns HTTP client + service tokens. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.IntegrationTests/CrossEpic/Fixtures/MockLlmProvider.cs` | In-process mock `IAIProvider` that records (prompt, headers, tenant) and returns a deterministic completion. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.IntegrationTests/CrossEpic/Fixtures/ElsaDispatchHarness.cs` | Wraps Elsa workflow start so the test can kick off `LlmCallWorkflow` with a tenantId and wait for completion without the full Elsa UI stack. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.IntegrationTests/CrossEpic/TestData/seed-prompts.sql` | Minimal system-default prompt rows (1 per role used in tests) + tenant A override. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.IntegrationTests/CrossEpic/TestData/seed-agent-configs.json` | Agent-config seeds for tenants A (custom: openai primary) and B (default). |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.IntegrationTests/CrossEpic/Assertions/TenantIsolationAsserts.cs` | Helpers: `AssertOnlyTenantRowsReturned`, `AssertNoCrossTenantLeak`, `AssertAuditEventEmitted`. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.IntegrationTests/CrossEpic/README.md` | Developer guide: how to run locally (`dotnet test --filter CrossEpic`), how to attach a debugger, how the fixture drops tenants on teardown. |
| `/home/meywd/tamma/.github/workflows/cross-epic-integration.yml` | CI workflow that runs this suite nightly + on every PR touching `Tamma.Api`, `Tamma.Data`, `Tamma.Activities`, or `packages/orchestrator`. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/Tamma.sln` | Add the new `Tamma.IntegrationTests` project if it doesn't exist; if it does, add the new test files to its include list. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.IntegrationTests/Tamma.IntegrationTests.csproj` | Add Testcontainers.PostgreSQL 4.x + xunit.v3 test dependencies if missing. Add `<ProjectReference Include=".../Tamma.Api.csproj" />` so the in-process host can spin up the minimal API. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Add an `Environment=IntegrationTest` branch that registers `MockLlmProvider` instead of the real provider chain. Guarded by `if (builder.Environment.IsEnvironment("IntegrationTest"))`. No production-path changes. |
| `/home/meywd/tamma/docs/stories/plans/layer-5-validation.md` | Reference this harness from §5.1 as the starting point for Layer 5's cross-epic test extension (append a pointer in the "Test cases" list). |

## 5. Sequence of changes

Each step below is a coherent commit candidate. Run `dotnet test --filter CrossEpic`
between steps to gate progress.

### Step 1 — Fixture scaffolding (3h)

- Add `TwoTenantFixture` + Testcontainers Postgres start/stop.
- Apply migrations 001-017 against the container via `MigrationRunner`.
- Seed tenants A and B (via `OrgEndpoints` in-process, not raw SQL) so the
  tests exercise the real creation path.
- Seed system-default prompts via `seed-prompts.sql`.
- **Commit**: `test(integration): cross-epic fixture skeleton`.

### Step 2 — Mock LLM + Elsa harness (2h)

- `MockLlmProvider` registered in `IntegrationTest` environment.
- `ElsaDispatchHarness.StartLlmCallAsync(tenantId, role, action)` posts an
  Elsa workflow start and polls `/workflow-instances/{id}` until
  `Status=Completed`.
- **Commit**: `test(integration): mock provider + Elsa harness`.

### Step 3 — Tests 1 & 2 — Full chain per tenant (4h)

- Test 1: tenant A — custom config + prompt override → assert openai is
  primary, override text wins, diagnostics recorded with `tenant_id = A`.
- Test 2: tenant B — no overrides → assert defaults flow through,
  diagnostics recorded with `tenant_id = B`.
- Both tests call the harness end-to-end: `OrgEndpoints.CreateOrg` →
  `PromptsEndpoints.SetOverride` (tenant A only) → `AgentsEndpoints.Upsert`
  (tenant A only) → `ElsaDispatchHarness.StartLlmCallAsync`.
- **Commit**: `test(integration): cross-epic tenant A + B happy path`.

### Step 4 — Tests 3 & 7 — Isolation + RLS (2h)

- Test 3: after tests 1 and 2 ran, query diagnostics for tenant A and
  assert only A's rows. Repeat for agent configs. Also assert via raw
  `SELECT * FROM agent_configs` connecting as `tamma_app` with
  `SET app.current_tenant_id = tenant_A_id` — returns only A's rows.
- Test 7: same as 3 but for `prompts` — assert tenant A sees A's override
  AND system defaults (per Story 17-2 exemption list).
- **Commit**: `test(integration): RLS isolation + prompt exemption`.

### Step 5 — Test 4 — Prompt fallback (1h)

- Tenant B requests `developer/context-scan`, which has no tenant override.
  Assert system default is returned.
- **Commit**: `test(integration): prompt store fallback to system default`.

### Step 6 — Test 5 — Elsa → API chain (3h)

- Wire a real Elsa workflow (minimal `LlmCallWorkflow` harness) and assert
  the `X-Tenant-Id` header reaches the prompt render endpoint. Recording
  client that captures headers lives in `MockLlmProvider.CapturedCalls`.
- **Commit**: `test(integration): Elsa workflow propagates tenant header`.

### Step 7 — Test 6 — Circuit breaker isolation (1h)

- Force tenant A's openai into OPEN via `HealthEndpoints.Trip`. Assert
  tenant B's openai remains CLOSED. Verify via `GET /health/providers/openai?tenantId=...`.
- **Commit**: `test(integration): circuit breaker per-tenant isolation`.

### Step 8 — CI workflow + README (1h)

- `.github/workflows/cross-epic-integration.yml` runs on PR and nightly.
- Caches NuGet + pulls Postgres 17 container.
- `CrossEpic/README.md` documents local run, debugger attach,
  Testcontainers disk-cleanup note.
- **Commit**: `ci(integration): cross-epic nightly + PR-gated workflow`.

### Step 9 — Layer-5 handoff reference (0.5h)

- Edit `docs/stories/plans/layer-5-validation.md` §5.1 to link this suite
  as the starting point for the extended Layer 5 harness.
- **Commit**: `docs(plans): layer-5 references 9-12 harness`.

## 6. Test strategy

The deliverable of this story IS a test. So "test strategy" here means
meta-level: how we ensure this test harness itself is correct and fast.

### Unit-level harness coverage

- `TwoTenantFixture` has its own micro-tests asserting idempotent setup
  (calling the fixture twice drops and re-creates cleanly).
- `MockLlmProvider` unit tests assert it records every prompt once and
  zero-clears captured buffers between cases.

### Integration scenarios (the story's deliverable)

Explicit test cases (match brief AC numbering):

1. **AC1/Tests 1 & 2** — full-chain per-tenant happy path, provider + prompt + diagnostics.
2. **AC3/Test 3** — cross-tenant isolation on diagnostics, agent_configs.
3. **AC4/Test 4** — prompt fallback to system default.
4. **AC7/Test 7** — RLS enforcement at the DB layer (bypass the API, assert via raw connection).
5. **AC9/Test 5** — Elsa → API header propagation.
6. **AC4/Test 6** — circuit breaker isolation per tenant.
7. **AC2/Test 7 extension** — budget isolation per tenant (if 9-2 shipped the budget endpoint).

### Performance gate

- Full suite wall clock < 60s on CI with the Testcontainers Postgres cold-start
  amortised (single fixture shared across tests via `ICollectionFixture`).
- Individual test p95 < 2s (matches brief AC 8).

### Negative tests

- Tenant A tries to read tenant B's agent config by raw ID → 404 (not 403 —
  per RLS, the row doesn't exist from A's perspective).
- Service JWT with wrong tenantId claim → 401 at the middleware.

## 7. Rollback plan

- **Pure additive**: no production code is altered except the
  `IntegrationTest` environment branch in `Program.cs`, which is a
  compile-time no-op in all other environments.
- **Revertable with `git revert`**: the commit list in §5 has no
  migrations, no secret generation, no schema drift.
- **CI workflow safe to disable**: if the nightly run is noisy, flip the
  workflow's `on.schedule` off — no production impact.
- **Non-reversible artifacts**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Fixture scaffolding | 3 |
| 2. Mock LLM + Elsa harness | 2 |
| 3. Tests 1 & 2 | 4 |
| 4. Tests 3 & 7 | 2 |
| 5. Test 4 | 1 |
| 6. Test 5 | 3 |
| 7. Test 6 | 1 |
| 8. CI workflow + README | 1 |
| 9. Layer-5 cross-reference | 0.5 |
| **Total** | **17.5** (brief estimated 17; matches within rounding) |

## 9. Open questions

- **Harness execution path for Elsa workflows.** Do we run full Elsa
  (`Tamma.ElsaServer` startup in-process) or a shim that fires a minimal
  workflow? Decision lives in the Team A worktree; recommendation is the
  shim for test speed (<1s per case) since 9-11's own integration tests
  already cover the full Elsa startup path. *Open until fixture author
  confirms.*
- **Mocked LLM vs. `Claude-Haiku` test credentials.** Brief specifies a
  mock. Layer 5 may want a small number of real-provider runs — decide
  whether to keep those separate (Layer 5's own harness) or add a
  `CROSS_EPIC_USE_REAL_LLM=1` env-var escape hatch here. Current plan:
  mock only; Layer 5 adds the escape hatch.
- **Testcontainers image cache.** CI cold-starts take ~40s for the
  first Postgres pull. Consider a sidecar container in the CI runner
  image to amortise. Not a blocker; a Layer-5 CI optimisation.
- **Is there a matching TS-side test to run?** `packages/orchestrator`
  has its own integration suite. The consensus is that 9-9 already
  covers the TS→API hop; this story only owns the C#-side harness.
  *Open question for the Team A coordinator — confirm before kickoff.*
