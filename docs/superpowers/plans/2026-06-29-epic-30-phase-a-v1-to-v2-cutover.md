# Epic 30 Phase A — Provisioning V1→V2 Cutover (Wave C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retire the `[Obsolete]` V1 tenant-provisioner surface and re-point the three admin provisioning endpoints onto the already-wired V2 `ProvisionTenantV2Dispatcher` / `TenantProviderRegistry`, preserving the exact external HTTP contract.

**Architecture:** The V2 provision path (dispatcher → platform queue → `ProvisionTenantV2TaskHandler` → 8-step saga) is already DI-registered but has **no production caller**. This plan wires the admin endpoints to it, adds the missing V2 **deprovision** path (payload-flag on the same handler), changes the null-provider dispatch semantics from `Failed/NoProvisioningInThisMode` → `Ready` (because under the unified schema-per-tenant model a no-backend tenant's schema is already minted at creation, so "provision" is a genuine no-op success), then deletes the V1 surface. Database routing is unaffected (still the unified `EncryptedConnectionString` path).

**Tech Stack:** .NET 9 / EF Core 9 / Npgsql / NUnit + Moq / `ApiTestFixture` (WebApplicationFactory) / real-Postgres integration tests via `sg docker -c "dotnet test ..."`.

## Global Constraints

- **Test runner:** C# tests in `apps/tamma-elsa` run as `sg docker -c "dotnet test ..."` (the session docker group is stale; **build** needs no wrapper). Reference: memory `reference_dotnet_test_docker`.
- **Build gate (run after every task):** `dotnet build apps/tamma-elsa/Tamma.sln -clp:ErrorsOnly` → 0 errors.
- **EF model gate (any task touching `TammaModelConfiguration`/entities/migrations):** `dotnet ef migrations has-pending-model-changes` must report none. **This plan changes NO schema** — no migration, no `TammaModelConfiguration` edit. If you find yourself editing those, stop: it means you've left Phase A scope.
- **No false success / fail-closed:** resolution is tenant→system→error, never empty fallback (memory `feedback_resolution_no_empty_fallback`). A provisioning failure must surface a `Failed` snapshot, never a silent success.
- **Auth policy:** the three admin routes require `PlatformOwnerAccess` (platform-owner only — keys off the JWT `platformRole` claim) composed over the group's `AdminAccess`. NOT `OwnerAccess` (which admits every personal-tenant owner). Preserve both verbatim.
- **External HTTP contract is frozen:** route templates, `ProvisionTenantRequest`, `TenantProvisioningResponse`, the `202 + Location` / `200` status codes, and the snake_case `State` strings must not change.
- **Deletion gate (Acceptance Criterion 1):** `grep -rn "ITenantProvisioner\b" apps/tamma-elsa/src apps/tamma-elsa/tests` → **0 hits** at the end.
- **Branch:** create a feature branch off `origin/main` (currently `f118e58d`) in the worktree `/home/meywd/tamma-wt/epic30-phaseA`. Suggested: `feat/epic-30-phase-a-v1v2-cutover`.

---

## Verified current-state appendix (origin/main `f118e58d`, 2026-06-29 — DO NOT re-derive)

The original Epic-30 plan's §1 findings were from `98cfb1c2` (3 weeks + a major pivot stale). These are re-verified against `origin/main`.

### V1 surface (to delete) — `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/`
| File | Key facts |
|---|---|
| `ITenantProvisioner.cs` | `[Obsolete("...Removed in Wave C.")]` at **L27**; methods: `ProvisionAsync(Guid, ProvisioningOptions, CancellationToken)→Task<ProvisioningStatus>` (L36-37), `GetStatusAsync(Guid, CancellationToken)→Task<ProvisioningStatus>` (L40), `DeprovisionAsync(Guid, CancellationToken)→Task` (L47). |
| `NullTenantProvisioner.cs` | `[Obsolete]` L24. Marks **Ready** immediately: L41 `tenant.ProvisioningState = ProvisioningState.Ready.ToStorageString();`, detail `"shared_infrastructure_no_cranl_configured"` (L42-43). Deprovision → `Deprovisioned`, detail `"shared_infrastructure_deprovision_noop"` (L61-72). |
| `CranlTenantProvisioner.cs` | 191 lines, `[Obsolete]` L49. Task-type consts: L52 `ProvisioningTaskType = "provisioning.tenant"`, L53 `DeprovisioningTaskType = "provisioning.tenant.deprovision"`. Enqueues to platform queue; `ProvisioningTaskPayload` defined L186-191. |
| `CranlProvisioningWorkflow.cs` | 435 lines, NOT `[Obsolete]`. 9-step resumable saga; injects `ICranlApiClient`, `TenantSecretProtector`, etc. |
| `TenantProvisioningTaskHandler.cs` | 55 lines. `: TaskHandlerBase` (→ `ITaskHandler`, **not** `IPlatformTaskHandler`). `TypePrefix => "provisioning.tenant"` (L30); branches on `task.Type == CranlTenantProvisioner.DeprovisioningTaskType` (L43). |
| `ProvisioningModels.cs` | **KEEP** `enum ProvisioningState` (L22, members `None`/`Pending`/`DatabaseProvisioning`/`DatabaseReady`/`AppProvisioning`/`AppDeploying`/`Ready`/`Failed`/`Deprovisioning`/`Deprovisioned`) + `ProvisioningStateExtensions.ToStorageString` (L57) / `ParseState` (L72). **DELETE** the V1-only records: `ProvisioningOptions(string Region, string? CustomName)` (L89) and `ProvisioningStatus(ProvisioningState, string?, string?, DateTimeOffset)` (L92). |

### V2 surface (target) — `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/`
| File | Key facts |
|---|---|
| `ProvisionTenantV2Dispatcher.cs` | `DispatchAsync(Guid tenantId, string providerKey, ProvisioningRequest request, Guid? invokingOrgId = null, CancellationToken ct = default)→Task<ProvisioningResult>`. **Null-provider branch L104-113** currently stamps `Failed`/`NoProvisioningInThisMode` (detail `"single_user_or_dev_mode"`) — **this is what Task 1 changes to Ready**. Unknown key → `Failed`/`ProviderNotRegistered`. Accept → flip `Pending` + `EnqueueAsync` (L162-188). Helpers: `StampFailureAsync` (L207), `PersistFailureAsync` (L231), `BuildSyntheticFailure` (L245). **No deprovision method.** |
| `ProvisionTenantV2TaskPayload.cs` | `const string TaskType = "provisioning.tenant.v2"` (L28). Fields: `TenantId`, `ProviderKey`, `Topology`, `Region`, `Tier`, `CustomName`, `ExistingDatabaseUrl`, `ExistingEngineUrl`, `InvokingOrgId`. `ToProvisioningRequest()` L68. **No operation discriminator.** |
| `ProvisionTenantV2TaskHandler.cs` | `: IPlatformTaskHandler`, `TaskType => "provisioning.tenant.v2"` (L51). `HandleAsync` deserializes, validates (terminal-throws on malformed/empty-tenant/blank-key, L66-85), then `_workflow.ExecuteAsync(payload, ct)` (L91). Provision-only. |
| `ProvisionTenantV2Workflow.cs` | 8-step saga, `ExecuteAsync(ProvisionTenantV2TaskPayload, ct)→Task<ProvisioningResult>` (L113). `RegisterSecrets` (L366-379) is a no-op `"deferred_to_30_3"`; quota (L216-225) is a discard no-op. Compensation calls `provider.DeprovisionAsync(tenantId, new DeprovisioningRequest(DeprovisioningCleanupMode.BestEffort, ...))` (L298, L333) — reuse this construction for the deprovision path. |
| `NullTenantProvider.cs` | `const Key = "null"`. `GetCapabilities()` → `ProviderCapabilities.None(Key, ...)` (topology `None`). `ProvisionAsync`/`DeprovisionAsync`/`ResolveEndpointsAsync` **throw** `NotSupportedException`. `GetStatusAsync` returns a `None` snapshot (does not throw). |
| `CranlTenantProviderV2.cs` | `ProviderKey => CranlCapabilities.ProviderKey` (== `"cranl"`). `DeprovisionAsync(...)` implemented L194. Capabilities (`CranlCapabilities`): topologies `DatabaseOnly \| DedicatedCompute`, regions `germany-1/us-east-1/saudi-arabia-1/egypt-1/india-1`. |
| `TenantProviderRegistry.cs` | Singleton over `IEnumerable<ITenantInfrastructureProvider>`. `bool TryGetProvider(string, out ITenantInfrastructureProvider?)` (L67); `IReadOnlyCollection<string> RegisteredKeys` (L85). Always contains `"null"`; contains `"cranl"` only when Cranl is configured. |
| `ProvisioningResult.cs` | `record ProvisioningResult(ProvisioningStatusSnapshot Status, IReadOnlyDictionary<string,string> ProviderResourceIds, TenantEndpoints? Endpoints = null, double? ProvisioningDurationSeconds = null)`. |
| `ProvisioningStatusSnapshot.cs` | `record ProvisioningStatusSnapshot(ProvisioningState State, string? Detail, string? FailureReason, DateTimeOffset UpdatedAt)`. |
| `ProvisioningRequest.cs` | `record ProvisioningRequest(ProvisioningTopology Topology, string? Region = null, string? Tier = null, string? CustomName = null, string? ExistingDatabaseUrl = null, string? ExistingEngineUrl = null, IReadOnlyDictionary<string,string>? ExtraTags = null)`. |
| `DeprovisioningRequest.cs` | `record DeprovisioningRequest(DeprovisioningCleanupMode CleanupMode = BestEffort, string? Reason = null)`. |

### Admin endpoints + DI
- **Routes** — `Program.cs:1522-1527`: `POST /api/admin/tenants/{tenantId:guid}/provision`, `GET .../provisioning`, `POST .../deprovision`, each `.RequireAuthorization("PlatformOwnerAccess")` over group `AdminAccess` (`Program.cs:1474`).
- **Handlers** — `AdminEndpoints.cs`: `ProvisionTenant` (L428-450, injects `ITenantProvisioner` + `CranlOptions`), `GetTenantProvisioning` (L452-465), `DeprovisionTenant` (L467-482). Provision/deprovision → `Results.Accepted("/api/admin/tenants/{tenantId}/provisioning", body)`; status → `Results.Ok(body)`.
- **DTOs** — `AdminDtos.cs`: `ProvisionTenantRequest(string? Region = null, string? CustomName = null)` (L73); `TenantProvisioningResponse(Guid TenantId, string State, string? Detail, string? AppDefaultDomain, DateTimeOffset UpdatedAt)` (L82-87).
- **DI** — `ProvisioningServiceCollectionExtensions.cs` (`AddTenantProvisioning`, called `Program.cs:406`): V1 at L73/76/80/93 (CS0618 pragmas at 74-77 + 91-94); **V2 dispatcher/handler/workflow registered UNCONDITIONALLY** at L129/130/135. Cranl V2 provider only on `options.IsConfigured` (L87 → `AddTenantProviderCranl` L170-176). A second extension `AddTenantProvisioningV2()` (`V2/V2ProvisioningServiceCollectionExtensions.cs:37`, called `Program.cs:275`) wires the routing directory + key lookup — leave it untouched.

### Tests
- **Endpoint contract (keep green):** `tests/Tamma.Api.Tests/Provisioning/ProvisioningAdminEndpointsTests.cs` — runs with Cranl **not** configured (null path), permissive auth. `Provision_NewTenant_Returns202WithReadyStateFromNullProvisioner` (L50-67, expects 202 + `State=="ready"`), `GetProvisioning_AfterProvision_ReturnsCurrentState` (L69-85, 200 + `"ready"`), `Deprovision_NullProvisioner_FlipsToDeprovisioned` (L87-102, 202 + `"deprovisioned"`). **These pass today via V1; after cutover they must pass via V2 — which requires Task 1 (null→Ready) + Task 2 (null deprovision→Deprovisioned).**
- **V1 tests (delete after porting unique behavior):** `Provisioning/NullTenantProvisionerTests.cs`, `Provisioning/CranlTenantProvisionerTests.cs`, `Provisioning/CranlProvisioningWorkflowTests.cs`. Behaviors to confirm-or-port into `Provisioning/V2/CranlTenantProviderV2Tests.cs` before deleting: env-var payload (`DATABASE_URL`/`TAMMA_*`), encrypted-conn round-trip, teardown ordering app→db→project, 404-as-already-absent, `ShortenForName`.
- **V2 tests (extend):** `Provisioning/V2/ProvisionTenantV2DispatcherTests.cs` (has `DispatchAsync_NullProviderKey_ShortCircuitsAsNoProvisioningInThisMode` L94-116 — **Task 1 rewrites this expectation**), `ProvisionTenantV2WorkflowTests.cs`, `ProvisionTenantV2TaskHandlerTests.cs`, `CranlTenantProviderV2Tests.cs`, `NullTenantProviderTests.cs`.

### Out of Phase A (deferred — do NOT build here)
- `RegisterSecrets` step: **hard-blocked** — needs `ISecretStore` which does not exist in code (Epic 29 is briefs-only). Stays the `"deferred_to_30_3"` no-op.
- Per-org **quota** enforcement: deferred to Story 30-3 (needs the per-org tenant-count helper). Stays the discard no-op.
- `provider_resource_ids`/`provider_key` **DB persistence** + `SqlTenantProviderKeyLookup` activation: Phase B / Story 30-3 (column exists but is unwritten).
- Pool-row registration / `TenantMoveService` integration (the V2↔unified-model reconciliation): **Phase B**.
- CHECK-constraint tightening, Elsa-runner closeout, OpenBao review: **Phases C/D/E**.

---

## File Structure

**Modify (production):**
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/ProvisionTenantV2Dispatcher.cs` — null→Ready short-circuit + add `DispatchDeprovisionAsync`; add `StampReadyAsync`/`StampDeprovisionedAsync` helpers.
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/ProvisionTenantV2TaskPayload.cs` — add `Operation` field.
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/ProvisioningOperation.cs` — **create** the discriminator enum.
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/ProvisionTenantV2Workflow.cs` — add `DeprovisionAsync(payload, ct)`.
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/ProvisionTenantV2TaskHandler.cs` — branch on `payload.Operation`.
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` — re-point the three handlers to V2.
- `apps/tamma-elsa/src/Tamma.Api/Extensions/ProvisioningServiceCollectionExtensions.cs` — remove V1 registrations + CS0618 pragmas.
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ProvisioningModels.cs` — delete `ProvisioningOptions` + `ProvisioningStatus` records (keep enum + extensions).
- `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` — fix dangling cref at L48.
- `CLAUDE.md` + `docs/stories/epic-30/README.md` — truth-up (Task 5).

**Delete (production):** `ITenantProvisioner.cs`, `NullTenantProvisioner.cs`, `CranlTenantProvisioner.cs`, `CranlProvisioningWorkflow.cs`, `TenantProvisioningTaskHandler.cs` (all under `…/Services/Provisioning/`).

**Modify/Create (tests):**
- `tests/Tamma.Api.Tests/Provisioning/V2/ProvisionTenantV2DispatcherTests.cs` — rewrite null-provision test; add null/Cranl deprovision tests.
- `tests/Tamma.Api.Tests/Provisioning/V2/ProvisionTenantV2TaskHandlerTests.cs` — add Operation-routing tests.
- `tests/Tamma.Api.Tests/Provisioning/V2/ProvisionTenantV2WorkflowTests.cs` — add deprovision tests.
- `tests/Tamma.Api.Tests/Provisioning/ProvisioningAdminEndpointsTests.cs` — keep assertions; adjust only `Detail` expectations if needed.
- `tests/Tamma.Api.Tests/Provisioning/V2/CranlTenantProviderV2Tests.cs` — port any V1-unique behavior.

**Delete (tests):** `NullTenantProvisionerTests.cs`, `CranlTenantProvisionerTests.cs`, `CranlProvisioningWorkflowTests.cs`.

---

## Task 1: Null-provider provision → `Ready` short-circuit (dispatcher)

**Why:** V1 `NullTenantProvisioner` returns `Ready` ("shared infra, nothing to do"); the V2 dispatcher returns `Failed/NoProvisioningInThisMode`. Under unified schema-per-tenant the tenant schema is minted at creation, so a no-backend provision is a genuine no-op success. The endpoint contract test expects `"ready"`. Change the dispatcher; this is the single behavioral change that lets the null endpoint test pass on V2.

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/ProvisionTenantV2Dispatcher.cs:98-113` (null branch) + add `StampReadyAsync` helper near L207.
- Test: `apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/V2/ProvisionTenantV2DispatcherTests.cs:94-116` (rewrite).

**Interfaces:**
- Consumes: `ProvisioningState.Ready`, `ToStorageString()`, `ProvisioningResult`, `ProvisioningStatusSnapshot` (existing).
- Produces: dispatcher `DispatchAsync` with null `providerKey` → `ProvisioningResult` with `Status.State == Ready`, `Status.Detail == "shared_infrastructure_no_backend_configured"`, `FailureReason == null`, **no** `EnqueueAsync` call.

- [ ] **Step 1: Rewrite the failing test** — replace `DispatchAsync_NullProviderKey_ShortCircuitsAsNoProvisioningInThisMode` (L94-116) with the new contract:

```csharp
[Test]
public async Task DispatchAsync_NullProviderKey_ShortCircuitsAsReadyNoBackend()
{
    var tenantId = await SeedAsync("none");
    var dispatcher = BuildDispatcher(); // registry = [NullTenantProvider] only

    var result = await dispatcher.DispatchAsync(
        tenantId,
        NullTenantProvider.Key,
        new ProvisioningRequest(ProvisioningTopology.DatabaseOnly, "germany-1"),
        invokingOrgId: null,
        CancellationToken.None);

    Assert.That(result.Status.State, Is.EqualTo(ProvisioningState.Ready));
    Assert.That(result.Status.Detail, Is.EqualTo("shared_infrastructure_no_backend_configured"));
    Assert.That(result.Status.FailureReason, Is.Null);
    _platformTasks.Verify(q => q.EnqueueAsync(It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()), Times.Never);

    var row = await ReloadTenantAsync(tenantId);
    Assert.That(row.ProvisioningState, Is.EqualTo("ready"));
}
```

(If `ReloadTenantAsync` isn't already a helper in this fixture, read the row via a fresh `ControlPlaneDbContext` exactly as the existing happy-path test at L204-240 does.)

- [ ] **Step 2: Run it to verify it fails**

Run: `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests --filter FullyQualifiedName~ProvisionTenantV2DispatcherTests.DispatchAsync_NullProviderKey_ShortCircuitsAsReadyNoBackend"`
Expected: FAIL (current code stamps `Failed`/`NoProvisioningInThisMode`).

- [ ] **Step 3: Add the `StampReadyAsync` helper** in `ProvisionTenantV2Dispatcher.cs` (place it directly above `StampFailureAsync` at L207):

```csharp
private async Task<ProvisioningResult> StampReadyAsync(
    Tenant tenant,
    string detail,
    CancellationToken ct)
{
    var nowUtc = _clock.GetUtcNow();
    tenant.ProvisioningState = ProvisioningState.Ready.ToStorageString();
    tenant.ProvisioningDetail = detail;
    tenant.ProvisioningUpdatedAt = nowUtc.UtcDateTime;
    tenant.UpdatedAt = nowUtc.UtcDateTime;
    await _db.SaveChangesAsync(ct).ConfigureAwait(false);

    return new ProvisioningResult(
        new ProvisioningStatusSnapshot(
            ProvisioningState.Ready,
            Detail: detail,
            FailureReason: null,
            UpdatedAt: nowUtc),
        ProviderResourceIds: new Dictionary<string, string>(),
        Endpoints: null,
        ProvisioningDurationSeconds: null);
}
```

- [ ] **Step 4: Change the null branch** at L104-113 from `StampFailureAsync(... NoProvisioningInThisMode ...)` to:

```csharp
if (string.Equals(providerKey, NullTenantProvider.Key, StringComparison.Ordinal))
{
    // Unified schema-per-tenant: the tenant's schema is minted at tenant
    // creation, so provisioning dedicated infrastructure is a genuine
    // no-op for a no-backend deployment. Report Ready (matches the
    // retired V1 NullTenantProvisioner) rather than letting
    // NullTenantProvider.ProvisionAsync throw downstream.
    _logger.LogInformation(
        "v2_provisioning.short_circuit_null_provider_ready tenantId={TenantId}", tenantId);
    return await StampReadyAsync(
        tenant,
        detail: "shared_infrastructure_no_backend_configured",
        ct).ConfigureAwait(false);
}
```

Also update the class XML doc-comment (L14-21) so the single-user bullet says the null seam short-circuits to **Ready** (no-op success), not `Failed/NoProvisioningInThisMode`.

- [ ] **Step 5: Run the test to verify it passes**

Run: same filter as Step 2.
Expected: PASS. Then run the whole dispatcher test class — Expected: all green (the unknown-key/topology/region/happy-path tests are untouched).

- [ ] **Step 6: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/ProvisionTenantV2Dispatcher.cs \
        apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/V2/ProvisionTenantV2DispatcherTests.cs
git commit -m "feat(30-A): null-provider dispatch short-circuits to Ready (unified-model no-op)"
```

---

## Task 2: V2 deprovision dispatch path (payload-flag on the same handler)

**Why:** V2 has no deprovision path; the endpoint contract test expects null-deprovision → `"deprovisioned"`, and deleting V1 must not regress real-backend (Cranl) teardown. Add an `Operation` discriminator to the queue payload, a `DispatchDeprovisionAsync` on the dispatcher (null → short-circuit `Deprovisioned`; real → flip `Deprovisioning` + enqueue), a `DeprovisionAsync` on the workflow, and an `Operation` branch in the handler.

**Files:**
- Create: `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/ProvisioningOperation.cs`
- Modify: `ProvisionTenantV2TaskPayload.cs` (add `Operation`), `ProvisionTenantV2Dispatcher.cs` (add `DispatchDeprovisionAsync` + `StampDeprovisionedAsync`), `ProvisionTenantV2Workflow.cs` (add `DeprovisionAsync`), `ProvisionTenantV2TaskHandler.cs` (branch).
- Test: `ProvisionTenantV2DispatcherTests.cs`, `ProvisionTenantV2TaskHandlerTests.cs`, `ProvisionTenantV2WorkflowTests.cs`.

**Interfaces:**
- Produces:
  - `enum ProvisioningOperation { Provision, Deprovision }`
  - `ProvisionTenantV2TaskPayload.Operation` (default `Provision`)
  - `ProvisionTenantV2Dispatcher.DispatchDeprovisionAsync(Guid tenantId, string providerKey, string? reason = null, CancellationToken ct = default)→Task<ProvisioningResult>` — null key → `Deprovisioned` no-enqueue; unknown → `Failed/ProviderNotRegistered`; real → `Deprovisioning` + enqueue `Operation=Deprovision`.
  - `ProvisionTenantV2Workflow.DeprovisionAsync(ProvisionTenantV2TaskPayload payload, CancellationToken ct)→Task<ProvisioningResult>` — resolves provider, calls `provider.DeprovisionAsync`, stamps `Deprovisioned` (or `Failed` on throw under `Strict`; `BestEffort` swallows).

- [ ] **Step 1: Create `ProvisioningOperation.cs`**

```csharp
namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Discriminates the lifecycle action a <see cref="ProvisionTenantV2TaskPayload"/>
/// carries on the platform queue. Story 30-9 (reduced to Cranl+Null scope by
/// Epic 30 Phase A): provision and deprovision ride the SAME queue type
/// (<see cref="ProvisionTenantV2TaskPayload.TaskType"/>) + handler, branched
/// on this flag.
/// </summary>
public enum ProvisioningOperation
{
    /// <summary>Default — run the 8-step provisioning saga.</summary>
    Provision,

    /// <summary>Reverse path — tear down provisioned infrastructure.</summary>
    Deprovision
}
```

- [ ] **Step 2: Add `Operation` to the payload** — in `ProvisionTenantV2TaskPayload.cs`, after `TenantId` (L31) add:

```csharp
    /// <summary>Which lifecycle action this task performs. Defaults to
    /// <see cref="ProvisioningOperation.Provision"/> so payloads serialized
    /// before this field existed still deserialize as provision tasks.</summary>
    public ProvisioningOperation Operation { get; set; } = ProvisioningOperation.Provision;
```

- [ ] **Step 3: Write the failing dispatcher deprovision tests** in `ProvisionTenantV2DispatcherTests.cs`:

```csharp
[Test]
public async Task DispatchDeprovisionAsync_NullProviderKey_ShortCircuitsToDeprovisioned()
{
    var tenantId = await SeedAsync("ready");
    var dispatcher = BuildDispatcher(); // null-only registry

    var result = await dispatcher.DispatchDeprovisionAsync(
        tenantId, NullTenantProvider.Key, reason: "tenant_deleted", CancellationToken.None);

    Assert.That(result.Status.State, Is.EqualTo(ProvisioningState.Deprovisioned));
    Assert.That(result.Status.FailureReason, Is.Null);
    _platformTasks.Verify(q => q.EnqueueAsync(It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()), Times.Never);
    var row = await ReloadTenantAsync(tenantId);
    Assert.That(row.ProvisioningState, Is.EqualTo("deprovisioned"));
}

[Test]
public async Task DispatchDeprovisionAsync_RealProvider_FlipsToDeprovisioningAndEnqueues()
{
    var tenantId = await SeedAsync("ready");
    var dispatcher = BuildDispatcher(FakeProvider("cranl")); // registry has a real provider

    PlatformQueuedTask? captured = null;
    _platformTasks
        .Setup(q => q.EnqueueAsync(It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()))
        .Callback<PlatformQueuedTask, CancellationToken>((t, _) => captured = t)
        .Returns(Task.CompletedTask);

    var result = await dispatcher.DispatchDeprovisionAsync(
        tenantId, "cranl", reason: "plan_downgrade", CancellationToken.None);

    Assert.That(result.Status.State, Is.EqualTo(ProvisioningState.Deprovisioning));
    Assert.That(captured, Is.Not.Null);
    Assert.That(captured!.Type, Is.EqualTo(ProvisionTenantV2TaskPayload.TaskType));
    var payload = JsonSerializer.Deserialize<ProvisionTenantV2TaskPayload>(captured.Payload)!;
    Assert.That(payload.Operation, Is.EqualTo(ProvisioningOperation.Deprovision));
    Assert.That(payload.ProviderKey, Is.EqualTo("cranl"));
}

[Test]
public async Task DispatchDeprovisionAsync_UnknownProviderKey_StampsProviderNotRegistered()
{
    var tenantId = await SeedAsync("ready");
    var dispatcher = BuildDispatcher(); // null-only registry
    var result = await dispatcher.DispatchDeprovisionAsync(
        tenantId, "hetzner", reason: null, CancellationToken.None);
    Assert.That(result.Status.State, Is.EqualTo(ProvisioningState.Failed));
    Assert.That(result.Status.FailureReason, Is.EqualTo(ProvisioningFailureReasons.ProviderNotRegistered));
    _platformTasks.Verify(q => q.EnqueueAsync(It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

(Match `FakeProvider`/`BuildDispatcher` to the existing helpers in this file — the happy-path provision test at L204-240 shows the exact `FakeProvider`/registry construction; reuse it. `FakeProvider("cranl")` must advertise a capability set whose `ProviderKey == "cranl"`.)

- [ ] **Step 4: Run the new tests to verify they fail**

Run: `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests --filter FullyQualifiedName~ProvisionTenantV2DispatcherTests.DispatchDeprovisionAsync"`
Expected: FAIL — `DispatchDeprovisionAsync` does not exist (compile error).

- [ ] **Step 5: Implement `DispatchDeprovisionAsync` + `StampDeprovisionedAsync`** in `ProvisionTenantV2Dispatcher.cs` (after `DispatchAsync`, before the private helpers):

```csharp
/// <summary>
/// Submit a deprovisioning request. Mirrors <see cref="DispatchAsync"/>:
/// the null seam short-circuits to <see cref="ProvisioningState.Deprovisioned"/>
/// without enqueueing (nothing to tear down under the unified model); a
/// real provider flips the tenant to <see cref="ProvisioningState.Deprovisioning"/>
/// and enqueues a <see cref="ProvisioningOperation.Deprovision"/> task on the
/// same platform queue + handler.
/// </summary>
public async Task<ProvisioningResult> DispatchDeprovisionAsync(
    Guid tenantId,
    string providerKey,
    string? reason = null,
    CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(providerKey))
    {
        return await PersistFailureAsync(
            tenantId, ProvisioningFailureReasons.ProviderNotRegistered, "provider_key_blank", ct)
            .ConfigureAwait(false);
    }

    var tenant = await _db.Tenants
        .IgnoreQueryFilters()
        .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct)
        .ConfigureAwait(false);
    if (tenant is null)
        return BuildSyntheticFailure(ProvisioningFailureReasons.TenantNotFound, $"tenant_{tenantId}_not_found");

    if (string.Equals(providerKey, NullTenantProvider.Key, StringComparison.Ordinal))
    {
        _logger.LogInformation(
            "v2_deprovisioning.short_circuit_null_provider tenantId={TenantId}", tenantId);
        return await StampDeprovisionedAsync(tenant, "shared_infrastructure_deprovision_noop", ct)
            .ConfigureAwait(false);
    }

    if (!_registry.TryGetProvider(providerKey, out var provider) || provider is null)
    {
        return await StampFailureAsync(
            tenant, ProvisioningFailureReasons.ProviderNotRegistered, $"provider_key_{providerKey}_unknown", ct)
            .ConfigureAwait(false);
    }

    var nowUtc = _clock.GetUtcNow();
    tenant.ProvisioningState = ProvisioningState.Deprovisioning.ToStorageString();
    tenant.ProvisioningDetail = "queued_for_v2_deprovisioning";
    tenant.ProvisioningUpdatedAt = nowUtc.UtcDateTime;
    tenant.UpdatedAt = nowUtc.UtcDateTime;
    await _db.SaveChangesAsync(ct).ConfigureAwait(false);

    var payload = new ProvisionTenantV2TaskPayload
    {
        TenantId = tenantId,
        ProviderKey = providerKey,
        Operation = ProvisioningOperation.Deprovision,
        // Topology is unused for deprovision but kept non-None so the
        // handler's blank-key/payload guards stay satisfied; DedicatedCompute
        // is the only Cranl deprovision shape today.
        Topology = ProvisioningTopology.DedicatedCompute,
        CustomName = reason,
    };

    await _platformTasks.EnqueueAsync(new PlatformQueuedTask
    {
        Type = ProvisionTenantV2TaskPayload.TaskType,
        TenantId = tenantId,
        Payload = JsonSerializer.Serialize(payload),
    }, ct).ConfigureAwait(false);

    return new ProvisioningResult(
        new ProvisioningStatusSnapshot(
            ProvisioningState.Deprovisioning, "queued_for_v2_deprovisioning", null, nowUtc),
        ProviderResourceIds: new Dictionary<string, string>(),
        Endpoints: null,
        ProvisioningDurationSeconds: null);
}

private async Task<ProvisioningResult> StampDeprovisionedAsync(
    Tenant tenant, string detail, CancellationToken ct)
{
    var nowUtc = _clock.GetUtcNow();
    tenant.ProvisioningState = ProvisioningState.Deprovisioned.ToStorageString();
    tenant.ProvisioningDetail = detail;
    tenant.ProvisioningUpdatedAt = nowUtc.UtcDateTime;
    tenant.UpdatedAt = nowUtc.UtcDateTime;
    await _db.SaveChangesAsync(ct).ConfigureAwait(false);

    return new ProvisioningResult(
        new ProvisioningStatusSnapshot(ProvisioningState.Deprovisioned, detail, null, nowUtc),
        ProviderResourceIds: new Dictionary<string, string>(),
        Endpoints: null,
        ProvisioningDurationSeconds: null);
}
```

- [ ] **Step 6: Add `DeprovisionAsync` to `ProvisionTenantV2Workflow.cs`** — a reduced reverse path (resolve provider, call `DeprovisionAsync`, stamp `Deprovisioned`; on throw, `BestEffort` swallows + still `Deprovisioned`). Mirror the event-emission style of `ExecuteAsync` (`EmitStepEventAsync`). Add after `ExecuteAsync`:

```csharp
/// <summary>
/// Reverse path for a real provider (the null seam is short-circuited in the
/// dispatcher and never enqueued). Resolves the provider, tears down its
/// infrastructure best-effort, and stamps Deprovisioned. Story 30-9 reduced
/// to Cranl+Null scope.
/// </summary>
public async Task<ProvisioningResult> DeprovisionAsync(
    ProvisionTenantV2TaskPayload payload, CancellationToken ct)
{
    if (payload is null) throw new ArgumentNullException(nameof(payload));

    await EmitStepEventAsync(payload.TenantId, "deprovision", "STEP_STARTED", null, ct).ConfigureAwait(false);

    var tenant = await _db.Tenants
        .IgnoreQueryFilters()
        .FirstOrDefaultAsync(t => t.Id == payload.TenantId && t.DeletedAt == null, ct)
        .ConfigureAwait(false);
    if (tenant is null)
        return BuildSyntheticFailure(ProvisioningFailureReasons.TenantNotFound, $"tenant_{payload.TenantId}_not_found");

    if (!_registry.TryGetProvider(payload.ProviderKey, out var provider) || provider is null)
        return await StampFailureAsync(tenant, ProvisioningFailureReasons.ProviderNotRegistered,
            $"provider_key_{payload.ProviderKey}_unknown", ct).ConfigureAwait(false);

    try
    {
        await provider.DeprovisionAsync(
            payload.TenantId,
            new DeprovisioningRequest(DeprovisioningCleanupMode.BestEffort, payload.CustomName),
            ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // BestEffort: log + still mark Deprovisioned; the 30-9 reconciliation
        // sweep (Phase B+) reclaims any orphan. Do NOT rethrow (a throw here
        // re-enqueues the task and re-runs teardown).
        _logger.LogWarning(ex,
            "v2_deprovisioning.provider_threw tenantId={TenantId} providerKey={ProviderKey}",
            payload.TenantId, payload.ProviderKey);
    }

    var nowUtc = _clock.GetUtcNow();
    tenant.ProvisioningState = ProvisioningState.Deprovisioned.ToStorageString();
    tenant.ProvisioningDetail = "deprovision_complete";
    tenant.ProvisioningUpdatedAt = nowUtc.UtcDateTime;
    tenant.UpdatedAt = nowUtc.UtcDateTime;
    await _db.SaveChangesAsync(ct).ConfigureAwait(false);

    await EmitStepEventAsync(payload.TenantId, "deprovision", "STEP_COMPLETED",
        new Dictionary<string, object?> { ["state"] = "deprovisioned" }, ct).ConfigureAwait(false);

    return new ProvisioningResult(
        new ProvisioningStatusSnapshot(ProvisioningState.Deprovisioned, "deprovision_complete", null, nowUtc),
        ProviderResourceIds: new Dictionary<string, string>(),
        Endpoints: null,
        ProvisioningDurationSeconds: null);
}
```

> **Implementer note:** confirm the workflow already has `_db`, `_registry`, `_clock`, `_logger`, `StampFailureAsync`, `BuildSyntheticFailure`, and `EmitStepEventAsync` in scope (the `ExecuteAsync` body uses them). If `StampFailureAsync`/`BuildSyntheticFailure` live only on the dispatcher, inline the equivalent stamp here rather than cross-referencing — keep the workflow self-contained.

- [ ] **Step 7: Branch the handler** — in `ProvisionTenantV2TaskHandler.cs`, replace the single `ExecuteAsync` call (L91) with:

```csharp
var result = payload.Operation == ProvisioningOperation.Deprovision
    ? await _workflow.DeprovisionAsync(payload, ct).ConfigureAwait(false)
    : await _workflow.ExecuteAsync(payload, ct).ConfigureAwait(false);
```

- [ ] **Step 8: Write the failing handler-routing test** in `ProvisionTenantV2TaskHandlerTests.cs` (mirror the existing provision-routing test; use a workflow test-double or `Mock<ProvisionTenantV2Workflow>` per how this file already fakes the workflow — if the workflow is concrete-mocked, assert `DeprovisionAsync` is invoked for `Operation=Deprovision`):

```csharp
[Test]
public async Task HandleAsync_DeprovisionOperation_RoutesToWorkflowDeprovision()
{
    var payload = new ProvisionTenantV2TaskPayload
    {
        TenantId = Guid.NewGuid(),
        ProviderKey = "cranl",
        Operation = ProvisioningOperation.Deprovision,
        Topology = ProvisioningTopology.DedicatedCompute,
    };
    var task = new PlatformQueuedTask { Type = ProvisionTenantV2TaskPayload.TaskType, Payload = JsonSerializer.Serialize(payload) };

    await _handler.HandleAsync(task, CancellationToken.None);

    _workflow.Verify(w => w.DeprovisionAsync(It.IsAny<ProvisionTenantV2TaskPayload>(), It.IsAny<CancellationToken>()), Times.Once);
    _workflow.Verify(w => w.ExecuteAsync(It.IsAny<ProvisionTenantV2TaskPayload>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

> **Implementer note:** if `ProvisionTenantV2TaskHandlerTests` currently constructs a *real* `ProvisionTenantV2Workflow` (not a mock), make `ExecuteAsync`/`DeprovisionAsync` `virtual` (they are public instance methods on a non-sealed-method class) OR introduce a thin seam interface. Prefer making the two methods `virtual` over a new interface — minimal surface, matches how the codebase mocks concretes elsewhere. (`ProvisionTenantV2Workflow` is `sealed`; either unseal it for the test or extract an `IProvisionTenantV2Workflow` seam — pick whichever the existing handler test already assumes.)

- [ ] **Step 9: Add a workflow deprovision happy-path test** in `ProvisionTenantV2WorkflowTests.cs` using the existing `FakeTenantInfrastructureProvider`:

```csharp
[Test]
public async Task DeprovisionAsync_RealProvider_TearsDownAndStampsDeprovisioned()
{
    var tenantId = await SeedAsync("ready");
    var provider = new FakeTenantInfrastructureProvider("cranl");
    var workflow = Build(RegistryWith(provider));
    var payload = new ProvisionTenantV2TaskPayload
    {
        TenantId = tenantId, ProviderKey = "cranl",
        Operation = ProvisioningOperation.Deprovision,
        Topology = ProvisioningTopology.DedicatedCompute,
    };

    var result = await workflow.DeprovisionAsync(payload, CancellationToken.None);

    Assert.That(result.Status.State, Is.EqualTo(ProvisioningState.Deprovisioned));
    Assert.That(provider.DeprovisionCallCount, Is.EqualTo(1));
    var row = await ReloadAsync(tenantId);
    Assert.That(row.ProvisioningState, Is.EqualTo("deprovisioned"));
}
```

(If `FakeTenantInfrastructureProvider` lacks a `DeprovisionCallCount`/ctor key arg, extend the test double minimally — it already tracks provision/status calls per the workflow tests.)

- [ ] **Step 10: Run all new Task-2 tests to verify they pass**

Run: `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests --filter FullyQualifiedName~Provisioning.V2"`
Expected: all green (new + existing V2 tests).

- [ ] **Step 11: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/V2/ \
        apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/V2/
git commit -m "feat(30-A): V2 deprovision path (payload-flag, same handler) + null short-circuit"
```

---

## Task 3: Port the three admin endpoints to V2

**Why:** wire the dangling V2 dispatcher to the admin surface while preserving the exact HTTP contract. This is the actual cutover.

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:428-482` (three handlers).
- Test: `apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/ProvisioningAdminEndpointsTests.cs` (keep `State` assertions; adjust only `Detail` if asserted).

**Interfaces:**
- Consumes: `ProvisionTenantV2Dispatcher.DispatchAsync` / `.DispatchDeprovisionAsync` (Tasks 1-2), `TenantProviderRegistry.RegisteredKeys`, `CranlCapabilities.ProviderKey`, `NullTenantProvider.Key`, `ProvisioningTopology`, `ProvisioningRequest`, `TenantProvisioningResponse`.
- Produces: identical HTTP responses — `202 + Location` (provision/deprovision), `200` (status), `TenantProvisioningResponse` body, `404` when the tenant is absent (`FailureReason == TenantNotFound`).

**Provider/topology selection (the Phase-A rule):** the endpoint picks the single default backend from the registry — `"cranl"` if registered (Cranl configured), else `"null"`; topology `DedicatedCompute` for Cranl (the V1 Cranl shape), irrelevant for null (short-circuited before the topology check). Phase B replaces this with the persisted `tenants.ProviderKey` + onboarding-driven topology (Story 30-7). Encode it as a private helper so the rule lives in one place.

- [ ] **Step 1: Confirm the contract tests still describe the intended behavior.** Read `ProvisioningAdminEndpointsTests.cs` L50-102. They assert (null path) provision→202+`"ready"`, status→200+`"ready"`, deprovision→202+`"deprovisioned"`. After Tasks 1-2 these are the V2 outcomes. **Do not change the assertions on `State`/status codes.** If a test asserts a specific `Detail` string from the V1 null provisioner (e.g. `"shared_infrastructure_no_cranl_configured"`), update only that string to the V2 detail (`"shared_infrastructure_no_backend_configured"` / `"shared_infrastructure_deprovision_noop"`).

- [ ] **Step 2: Run the endpoint tests to confirm they currently pass on V1 (baseline)**

Run: `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests --filter FullyQualifiedName~ProvisioningAdminEndpointsTests"`
Expected: PASS (still on V1). This is the regression baseline.

- [ ] **Step 3: Rewrite `ProvisionTenant`** (`AdminEndpoints.cs:428-450`):

```csharp
public static async Task<IResult> ProvisionTenant(
    Guid tenantId,
    ProvisionTenantRequest? req,
    ProvisionTenantV2Dispatcher dispatcher,
    TenantProviderRegistry registry,
    CranlOptions cranlOptions,
    CancellationToken ct)
{
    var (providerKey, topology) = ResolveDefaultBackend(registry);
    var region = req?.Region ?? cranlOptions.DefaultRegion;
    var request = new ProvisioningRequest(topology, region, Tier: null, req?.CustomName);

    var result = await dispatcher.DispatchAsync(tenantId, providerKey, request, invokingOrgId: null, ct);
    if (result.Status.FailureReason == ProvisioningFailureReasons.TenantNotFound)
        return Results.NotFound(new { error = "tenant_not_found", tenantId });

    return Results.Accepted(
        $"/api/admin/tenants/{tenantId}/provisioning",
        new TenantProvisioningResponse(
            tenantId,
            result.Status.State.ToStorageString(),
            result.Status.Detail,
            result.Endpoints?.AppDefaultDomain,
            result.Status.UpdatedAt.UtcDateTime));
}
```

> Verify `TenantEndpoints` exposes `AppDefaultDomain` (read `…/V2/TenantEndpoints.cs`); if the property is named differently (e.g. `EngineUrl`/`AppUrl`), map the closest equivalent, or pass `null` for the synchronous `Pending`/`Ready` snapshot where `Endpoints` is always `null`. The V1 null path returned `AppDefaultDomain == null`, so `null` preserves the contract.

- [ ] **Step 4: Add the private selection helper** in `AdminEndpoints.cs` (near the three handlers):

```csharp
// Phase-A backend selection: one real backend (Cranl) or the null seam.
// Phase B replaces this with the persisted tenants.ProviderKey + a topology
// chosen at onboarding (Story 30-7).
private static (string providerKey, ProvisioningTopology topology) ResolveDefaultBackend(
    TenantProviderRegistry registry)
{
    if (registry.RegisteredKeys.Contains(CranlCapabilities.ProviderKey))
        return (CranlCapabilities.ProviderKey, ProvisioningTopology.DedicatedCompute);
    return (NullTenantProvider.Key, ProvisioningTopology.DatabaseOnly);
}
```

- [ ] **Step 5: Rewrite `GetTenantProvisioning`** (`AdminEndpoints.cs:452-465`) to read the persisted tenant row (the workflow keeps `ProvisioningState`/`Detail`/`UpdatedAt` current):

```csharp
public static async Task<IResult> GetTenantProvisioning(
    Guid tenantId,
    ControlPlaneDbContext db,
    CancellationToken ct)
{
    var tenant = await db.Tenants
        .IgnoreQueryFilters()
        .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null, ct);
    if (tenant is null)
        return Results.NotFound(new { error = "tenant_not_found", tenantId });

    return Results.Ok(new TenantProvisioningResponse(
        tenantId,
        tenant.ProvisioningState,
        tenant.ProvisioningDetail,
        AppDefaultDomain: null,
        tenant.ProvisioningUpdatedAt ?? tenant.UpdatedAt));
}
```

> `tenant.ProvisioningState` is already the storage string — return it directly (no `ToStorageString`). Confirm `ControlPlaneDbContext` + `Microsoft.EntityFrameworkCore` are usable from `AdminEndpoints` (other handlers in this file already inject `ControlPlaneDbContext`).

- [ ] **Step 6: Rewrite `DeprovisionTenant`** (`AdminEndpoints.cs:467-482`):

```csharp
public static async Task<IResult> DeprovisionTenant(
    Guid tenantId,
    ProvisionTenantV2Dispatcher dispatcher,
    TenantProviderRegistry registry,
    CancellationToken ct)
{
    var (providerKey, _) = ResolveDefaultBackend(registry);
    var result = await dispatcher.DispatchDeprovisionAsync(tenantId, providerKey, reason: "admin_deprovision", ct);
    if (result.Status.FailureReason == ProvisioningFailureReasons.TenantNotFound)
        return Results.NotFound(new { error = "tenant_not_found", tenantId });

    return Results.Accepted(
        $"/api/admin/tenants/{tenantId}/provisioning",
        new TenantProvisioningResponse(
            tenantId,
            result.Status.State.ToStorageString(),
            result.Status.Detail,
            AppDefaultDomain: null,
            result.Status.UpdatedAt.UtcDateTime));
}
```

- [ ] **Step 7: Add the `using`s** needed in `AdminEndpoints.cs` (`Tamma.Api.Services.Provisioning.V2`, `Tamma.Api.Services.Provisioning.V2.Cranl`, `Microsoft.EntityFrameworkCore`, `Tamma.Data`) and remove the now-unused `Tamma.Api.Services.Provisioning` V1 references if no other handler in the file uses them. Routes in `Program.cs:1522-1527` are unchanged (same method names, same `PlatformOwnerAccess`).

- [ ] **Step 8: Run the endpoint tests to verify they pass on V2**

Run: `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests --filter FullyQualifiedName~ProvisioningAdminEndpointsTests"`
Expected: PASS — provision→202+`"ready"`, status→200+`"ready"`, deprovision→202+`"deprovisioned"`.

- [ ] **Step 9: Build the whole solution**

Run: `dotnet build apps/tamma-elsa/Tamma.sln -clp:ErrorsOnly`
Expected: 0 errors (V1 types still exist at this point — they're deleted in Task 4).

- [ ] **Step 10: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs \
        apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/ProvisioningAdminEndpointsTests.cs
git commit -m "feat(30-A): cut admin provision/status/deprovision endpoints over to V2 dispatcher"
```

---

## Task 4: Delete the V1 surface

**Why:** with no remaining caller, remove the `[Obsolete]` surface and satisfy Acceptance Criterion 1.

**Execution note (deviation from plan):** The plan originally called for deleting
`CranlProvisioningWorkflow.cs` along with the other V1 files. During implementation, review found
that deleting the engine would have orphaned the V2 Cranl path — `CranlTenantProviderV2` delegates
to `CranlProvisioningWorkflow` for the actual REST-walk (project→db→app), and there was no other
implementation of that logic. Deleting the engine without a replacement would leave a Cranl-configured
deployment silently timing out to `Failed` instead of completing. The engine was therefore **kept**
(`CranlProvisioningWorkflow.cs` remains in the codebase), and Task 4's deletion was narrowed to
the four true V1 files (`ITenantProvisioner`, `NullTenantProvisioner`, `CranlTenantProvisioner`,
`TenantProvisioningTaskHandler`). As a follow-on in the same commit (`d69c42bb` + `c9f2c353`), two
new `IPlatformTaskHandler`s were wired to drive the now-kept engine: `CranlProvisionPlatformTaskHandler`
(task type `provisioning.tenant`) and `CranlDeprovisionPlatformTaskHandler` (task type
`provisioning.tenant.deprovision`). The Cranl provision/deprovision paths are functional end-to-end
as a result. See parent plan `docs/superpowers/plans/2026-06-11-epic-30-pluggable-provisioning.md`
Phase A deviation note for the full record.

**Files:**
- Delete: `ITenantProvisioner.cs`, `NullTenantProvisioner.cs`, `CranlTenantProvisioner.cs`,
  `TenantProvisioningTaskHandler.cs` (under `…/Services/Provisioning/`); tests
  `NullTenantProvisionerTests.cs`, `CranlTenantProvisionerTests.cs`, `CranlProvisioningWorkflowTests.cs`.
  **`CranlProvisioningWorkflow.cs` was NOT deleted** (see deviation note above).
- Modify: `ProvisioningModels.cs` (delete `ProvisioningOptions` L89 + `ProvisioningStatus` L92), `ProvisioningServiceCollectionExtensions.cs` (remove V1 registrations + CS0618), `Tenant.cs:48` (fix cref), `CranlTenantProviderV2Tests.cs` (port unique V1 behavior + drop the L15 CS0618 pragma if now unused).

> **Superseded:** the steps below describe the original delete-all approach; the deviation note above records what actually shipped (engine kept, Cranl platform handlers wired in c9f2c353).

- [ ] **Step 1: Behavior-port analysis (blocking).** Diff `CranlProvisioningWorkflowTests.cs` against `CranlTenantProviderV2Tests.cs`. For each V1-unique behavior — env-var payload (`DATABASE_URL`,`TAMMA_CONTROL_PLANE_URL`,`TAMMA_TENANT_ID`,`TAMMA_SHARED_SECRET`), encrypted-conn round-trip, teardown order app→db→project, 404-as-absent, `ShortenForName` — confirm an equivalent assertion exists in `CranlTenantProviderV2Tests.cs`. If any is missing, add it there FIRST (against the real `CranlTenantProviderV2` + a strict `ICranlApiClient` mock). Record in the commit body which behaviors were already covered vs newly ported.

- [ ] **Step 2: Delete the five V1 production files + three V1 test files.**

```bash
git rm apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ITenantProvisioner.cs \
       apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/NullTenantProvisioner.cs \
       apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/CranlTenantProvisioner.cs \
       apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/CranlProvisioningWorkflow.cs \
       apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantProvisioningTaskHandler.cs \
       apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/NullTenantProvisionerTests.cs \
       apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/CranlTenantProvisionerTests.cs \
       apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/CranlProvisioningWorkflowTests.cs
```

- [ ] **Step 3: Delete the V1-only records** from `ProvisioningModels.cs` — remove `public record ProvisioningOptions(...)` (L89) and `public record ProvisioningStatus(...)` (L92). KEEP `enum ProvisioningState` + `ProvisioningStateExtensions`. First confirm `V2/ProvisioningResult.cs`'s mention of `ProvisioningStatus` is only an XML `<see>`/doc reference (it is — verified) and not a live type dependency; if any non-doc reference remains, fix it.

- [ ] **Step 4: Remove the V1 DI registrations** in `ProvisioningServiceCollectionExtensions.cs`: delete L73 (`CranlProvisioningWorkflow`), the CS0618 block L74-77 (`ITenantProvisioner, CranlTenantProvisioner`), L80 (`ITaskHandler, TenantProvisioningTaskHandler`), and the CS0618 block L91-94 (`ITenantProvisioner, NullTenantProvisioner`). Keep the Cranl V2 client/secret-protector registrations (L58-72) — `CranlTenantProviderV2` still needs `ICranlApiClient` + `TenantSecretProtector`. Update the method's XML doc (L23) + the coexistence comment (L99) to drop V1 references.

- [ ] **Step 5: Fix the dangling cref** at `Tenant.cs:48` — change `<see cref="Tamma.Core.Enums.ProvisioningState"/>` to `<see cref="Tamma.Api.Services.Provisioning.ProvisioningState"/>` (the real location).

- [ ] **Step 6: Run the deletion gate**

Run: `grep -rn "ITenantProvisioner\b" apps/tamma-elsa/src apps/tamma-elsa/tests`
Expected: **0 hits.** (If `CranlTenantProviderV2Tests.cs:15`'s `#pragma warning disable CS0618` is now unused — it referenced the V1 concretes — remove it.)

- [ ] **Step 7: Build + full Api test suite**

Run: `dotnet build apps/tamma-elsa/Tamma.sln -clp:ErrorsOnly` → 0 errors.
Run: `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"`
Expected: all green (≥ the pre-cutover count minus the 3 deleted V1 test files' cases, plus the new V2 cases).

- [ ] **Step 8: Commit**

```bash
git add -A apps/tamma-elsa/src/Tamma.Api/Services/Provisioning \
           apps/tamma-elsa/src/Tamma.Api/Extensions/ProvisioningServiceCollectionExtensions.cs \
           apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs \
           apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning
git commit -m "refactor(30-A): delete V1 ITenantProvisioner surface (Wave C); 0 grep hits"
```

---

## Task 5: Documentation truth-up (Phase A slice)

**Files:** `CLAUDE.md`, `docs/stories/epic-30/README.md`, `docs/superpowers/plans/2026-06-11-epic-30-pluggable-provisioning.md` (Phase A status).

- [ ] **Step 1: CLAUDE.md** — in the "Multi-tenant provisioning (Cranl)" section, change the `NullTenantProvisioner` reference to the V2 `NullTenantProvider`/`ProvisionTenantV2Dispatcher` seam; note that admin provision/status/deprovision now ride the V2 dispatcher and that a no-backend deployment short-circuits to `Ready` (no-op under unified schema-per-tenant).
- [ ] **Step 2: Epic 30 README** — mark 30-3's "Wave C" / V1-retirement DONE (commit refs); note deprovision delivered at reduced Cranl+Null scope; RegisterSecrets/quota/pool-row reconciliation remain Phase B/30-3.
- [ ] **Step 3: Parent plan** — set Phase A status to DONE with the commit range; leave Phases B-E as PLANNED.
- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md docs/stories/epic-30/README.md docs/superpowers/plans/2026-06-11-epic-30-pluggable-provisioning.md
git commit -m "docs(30-A): truth-up provisioning V1→V2 cutover (Wave C complete)"
```

---

## Risks

| Risk | Mitigation |
|---|---|
| Changing the dispatcher null path (Failed→Ready) breaks a non-endpoint consumer that relied on `NoProvisioningInThisMode`. | Verified: the only callers of `DispatchAsync` are the dispatcher tests + (post-Task-3) the endpoint. The workflow's own null handling is untouched and is never reached via the endpoint (dispatcher short-circuits first). |
| Endpoint contract drift breaks the dashboard. | Route templates, DTOs, status codes, and `State` strings are frozen (Global Constraints); endpoint tests are the regression baseline (Task 3 Step 2 runs them green on V1 first). |
| Deleting V1 loses a behavior only its tests covered. | Task 4 Step 1 mandates a test-diff + port BEFORE deletion. |
| `AppDefaultDomain` mapping wrong after cutover (V2 `Endpoints` shape differs from V1 `ProvisioningStatus.AppDefaultDomain`). | Synchronous dispatch snapshots always have `Endpoints == null` → map to `null`, which matches the V1 null-path contract. For Cranl, the eventual domain lands via the saga + status read; the synchronous 202 never carried it either. |
| Orphaned queue type: V1 handler (`ITaskHandler`, `provisioning.tenant`) deleted while a real `provisioning.tenant` row is in flight. | The V2 path uses a different type (`provisioning.tenant.v2`) on a different handler (`IPlatformTaskHandler`) — already registered. No new orphan is created; pre-existing in-flight V1 rows are not expected on a fresh deploy (no production V2-or-V1 caller distinction at runtime since admin is the only entry). If operating on a live DB with queued V1 rows, drain them before deploy (note in Task 5 docs). |
| Mocking a `sealed` `ProvisionTenantV2Workflow` in the handler-routing test. | Task 2 Step 8 note: unseal + `virtual` the two methods, or extract an `IProvisionTenantV2Workflow` seam — follow whatever the existing handler test already does. |

## Acceptance criteria (Phase A)

1. `grep -rn "ITenantProvisioner\b" apps/tamma-elsa/src apps/tamma-elsa/tests` → 0 hits; the five V1 files + three V1 test files are deleted; admin provision/status/deprovision ride `ProvisionTenantV2Dispatcher`/`TenantProviderRegistry`; no-backend deployments still return `"ready"`/`"deprovisioned"`.
2. External HTTP contract unchanged (routes, DTOs, `202+Location`/`200`, `PlatformOwnerAccess`); `ProvisioningAdminEndpointsTests` green on V2.
3. V2 deprovision works end-to-end (null short-circuit + real-provider enqueue→handler→`provider.DeprovisionAsync`), covered by dispatcher + handler + workflow tests.
4. `dotnet build … -clp:ErrorsOnly` → 0 errors; full `Tamma.Api.Tests` green; **no migration / no `TammaModelConfiguration` change**.
5. Docs (CLAUDE.md, Epic 30 README, parent plan Phase-A status) reflect the cutover; RegisterSecrets/quota/pool-row reconciliation explicitly recorded as deferred (Phase B / 30-3, RegisterSecrets hard-blocked on Epic 29 `ISecretStore`).

## Self-review notes

- **Scope:** deliberately just the cutover (plan Phase A / Wave C). Phase B (pool-row reconciliation, the V2↔unified-model routing fix, legacy Cranl-column drop) and Phases C-E (CHECK tightening, Elsa runner, OpenBao) are separate plans — they involve schema changes and the `tenant_databases` pool, which this plan deliberately does not touch.
- **Highest-blast-radius:** Task 1 (null semantics change) and Task 3 (the endpoint swap). Both gate behind the frozen endpoint contract tests + the dispatcher unit tests.
- **Deferred-and-recorded, not silently skipped:** RegisterSecrets (hard-blocked on `ISecretStore`), per-org quota, `provider_resource_ids`/`provider_key` persistence, pool-row registration — all called out in the appendix + AC5.
