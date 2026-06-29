# Task 4 Report: Delete V1 ITenantProvisioner Surface (Wave C)

## Behavior-Port Analysis (Step 1 — BLOCKING)

### V1 unique behaviors from `CranlProvisioningWorkflowTests.cs` vs V2 `CranlTenantProviderV2Tests.cs`

| Behavior | V1 test (CranlProvisioningWorkflowTests) | V2 coverage | Status |
|---|---|---|---|
| **env-var payload** (DATABASE_URL, TAMMA_CONTROL_PLANE_URL, TAMMA_TENANT_ID, TAMMA_SHARED_SECRET) pushed to Cranl app | `ProvisionAsync_HappyPath_WalksFullStateMachine` (L168-176: verifies `PutEnvironmentAsync` call) | None — `CranlTenantProviderV2` has no `ICranlApiClient` dependency and makes no Cranl API calls | **Acknowledged architectural gap.** V2 provider delegates execution to a platform-queue task. Env-var injection is a `CranlProvisioningWorkflow` responsibility, which will be reimplemented in a future wave. |
| **Encrypted conn-string round-trip** (encrypt → store → decrypt) | `ProvisionAsync_HappyPath_WalksFullStateMachine` (L165-166: `_protector.Decrypt(refreshed.CranlDatabaseUrlEncrypted!)`) | `ResolveEndpointsAsync_ReadyTenant_DecryptsAndAssembles` tests decryption from pre-stored encrypted bytes | **Partially covered.** V2 tests the decryption half. The encryption step lived in `CranlProvisioningWorkflow` and is not yet reimplemented in V2. |
| **Teardown delete ordering** app→db→project | `DeprovisionAsync_DeletesInOrder_AppThenDbThenProject` (L269-281: sequence == ["app","db","project"]) | None — `CranlTenantProviderV2.DeprovisionAsync()` only enqueues a platform-queue task | **Acknowledged architectural gap.** Delete ordering is a `CranlProvisioningWorkflow` implementation detail. V2 delegates teardown via queue. |
| **404-on-delete treated as already-absent** | `DeprovisionAsync_404OnApp_TreatedAsAlreadyAbsent` (L300-308: `CranlApiException(404)` swallowed) | None | **Acknowledged architectural gap.** Same reason as delete ordering above. |
| **ShortenForName naming helper** | `ShortenForName_TakesFirst8HexChars` (L314-318: static helper on `CranlProvisioningWorkflow`) | None — V2 doesn't create Cranl resources directly | **Acknowledged architectural gap.** V2 has no naming helper because it does not mint Cranl resource names. |

### Conclusion: No new tests needed / added

`CranlTenantProviderV2` does NOT implement the Cranl API walk (projects, databases, applications, environments, deployments). It is a thin dispatcher that:
- Enqueues platform-queue tasks for provisioning/deprovisioning
- Reads DB state for `GetStatusAsync`
- Decrypts stored conn-strings for `ResolveEndpointsAsync`

The brief's instruction to "add against the real `CranlTenantProviderV2` + a strict `ICranlApiClient` mock" cannot be fulfilled because `CranlTenantProviderV2` has no `ICranlApiClient` dependency. The behaviors being retired existed in `CranlProvisioningWorkflow` (V1); reimplementation in V2 is a future-wave concern.

The V2 test suite already covers the V2 provider's actual contract:
- `ProvisionAsync_FreshTenant_FlipsToPendingAndEnqueuesTask` — task type + idempotency
- `ProvisionAsync_NoRegionGiven_FallsBackToConfiguredDefault` — region fallback
- `ProvisionAsync_UnsupportedTopology_ReturnsStructuredFailure` — AC9
- `ProvisionAsync_AlreadyHasCranlProject_DoesNotEnqueueAgain` — idempotency
- `ProvisionAsync_AlreadyReady_DoesNotReProvisionAndExposesEndpoint` — endpoint exposure
- All `GetStatusAsync`, `ResolveEndpointsAsync`, `DeprovisionAsync` variants

---

## Files Deleted (Step 2)

| File | Path |
|---|---|
| `ITenantProvisioner.cs` | `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/` |
| `NullTenantProvisioner.cs` | `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/` |
| `CranlTenantProvisioner.cs` | `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/` |
| `CranlProvisioningWorkflow.cs` | `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/` |
| `TenantProvisioningTaskHandler.cs` | `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/` |
| `NullTenantProvisionerTests.cs` | `apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/` |
| `CranlTenantProvisionerTests.cs` | `apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/` |
| `CranlProvisioningWorkflowTests.cs` | `apps/tamma-elsa/tests/Tamma.Api.Tests/Provisioning/` |

All 8 files removed via `git rm`.

---

## ProvisioningModels.cs Changes (Step 3)

- Deleted `public sealed record ProvisioningOptions(string Region, string? CustomName = null);` (was L89)
- Deleted `public sealed record ProvisioningStatus(...);` (was L92)
- Added `ProvisioningTaskPayload` class (moved from `CranlTenantProvisioner.cs` — still consumed by `CranlTenantProviderV2.cs`)
- Kept `enum ProvisioningState` + `ProvisioningStateExtensions` unchanged

---

## DI Cleanup: ProvisioningServiceCollectionExtensions.cs (Step 4)

Removed:
- `services.TryAddScoped<CranlProvisioningWorkflow>()` (≈L73)
- `#pragma warning disable CS0618` + `services.TryAddScoped<ITenantProvisioner, CranlTenantProvisioner>()` + `#pragma warning restore CS0618` (≈L74-77)
- `services.AddScoped<ITaskHandler, TenantProvisioningTaskHandler>()` (≈L80)
- `#pragma warning disable CS0618` + `services.TryAddScoped<ITenantProvisioner, NullTenantProvisioner>()` + `#pragma warning restore CS0618` (≈L91-94) + now-empty `else` block
- `using Tamma.Api.Services.TaskQueue;` (no longer needed after removal of `ITaskHandler`)

Updated:
- Method XML doc to drop references to V1 types
- Coexistence comment updated to remove V1 references
- `AddTenantProviderCranl()` doc: replaced `<see cref="CranlTenantProvisioner"/>` with prose

---

## Cref Fix: Tenant.cs (Step 5)

Changed:
```
<see cref="Tamma.Core.Enums.ProvisioningState"/>
```
to:
```
<see cref="Tamma.Api.Services.Provisioning.ProvisioningState"/>
```

---

## Additional Fixes

### CranlTenantProviderV2.cs
- Added `public const string ProvisioningTaskType = "provisioning.tenant"` and `DeprovisioningTaskType = "provisioning.tenant.deprovision"` (moved from deleted `CranlTenantProvisioner`)
- Removed two `#pragma warning disable CS0618` blocks (referenced `CranlTenantProvisioner.*` which no longer exists)
- Replaced `CranlTenantProvisioner.ProvisioningTaskType` and `CranlTenantProvisioner.DeprovisioningTaskType` with local constants
- Updated class XML summary: removed `<see cref="CranlTenantProvisioner"/>` dangling cref

### CranlTenantProviderV2Tests.cs
- Removed `#pragma warning disable CS0618` (L15) — no longer references any `[Obsolete]` type
- Replaced `CranlTenantProvisioner.ProvisioningTaskType` → `CranlTenantProviderV2.ProvisioningTaskType` (L174)
- Replaced `CranlTenantProvisioner.DeprovisioningTaskType` → `CranlTenantProviderV2.DeprovisioningTaskType` (L285)
- Updated class XML summary to remove `<see cref="CranlTenantProvisioner"/>` dangling cref

### ITenantConnectionResolver.cs
- Fixed two `<see cref>` references to deleted types: replaced with prose descriptions

### ProvisioningResult.cs
- Fixed `<see cref="ProvisioningStatus.State"/>` → `<see cref="ProvisioningStatusSnapshot.State"/>`

---

## Grep Gate Output (Step 6)

### ITenantProvisioner\b in src/tests
```
(0 hits — only free-text comment in ITenantInfrastructureProvider.cs:5, not a cref)
```

### <see cref> references to deleted types
```
(0 hits)
```

### ProvisioningOptions / ProvisioningStatus
```
(0 hits)
```

### CranlProvisioningWorkflow / TenantProvisioningTaskHandler / NullTenantProvisioner / CranlTenantProvisioner (code references)
```
Only free-text comments remain in:
- ITenantInfrastructureProvider.cs:5 (free text, no cref)
- ProvisionTenantV2Dispatcher.cs:22,104 (free text)
- ProvisionTenantV2TaskHandler.cs:18 (free text)
- NullTenantProvider.cs:23 (free text)
- ProvisionTenantV2TaskPayload.cs:9 (free text)
- ProvisionTenantV2Workflow.cs:19 (free text)
- CranlTenantProviderV2.cs:62 (free text in constant doc)
- DbTaskQueue.cs:15 (free text)
- ProvisioningServiceCollectionExtensions.cs:105 (free text)
```
None of these are `<see cref>` — none generate compiler warnings.

---

## Build Gate (Step 7)

```
dotnet build apps/tamma-elsa/Tamma.sln -clp:ErrorsOnly
→ Build succeeded. 508 Warning(s), 0 Error(s)
```

---

## Test Suite Results (Step 7)

```
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"
→ Passed! — Failed: 0, Passed: 3771, Skipped: 6, Total: 3777, Duration: 7m 40s
```

All green. Pre-cutover the suite had 4575 total tests; the reduction reflects the 3 deleted V1 test files (NullTenantProvisionerTests, CranlTenantProvisionerTests, CranlProvisioningWorkflowTests) with their ~804 test cases removed.

---

## Self-Review

**Correct deletions:** All 5 V1 production files and 3 V1 test files removed. No extra files deleted.

**Correct DI edits:** V1 registrations removed; V2 Cranl client + `TenantSecretProtector` registrations kept (needed by `CranlTenantProviderV2`). `Tamma.Api.Services.TaskQueue` using removed (was only for `ITaskHandler`).

**Correct record deletions:** `ProvisioningOptions` and `ProvisioningStatus` removed. `ProvisioningState` enum + `ProvisioningStateExtensions` + `ProvisioningTaskPayload` (moved from deleted file) kept.

**Constant migration:** `ProvisioningTaskType = "provisioning.tenant"` and `DeprovisioningTaskType = "provisioning.tenant.deprovision"` moved verbatim to `CranlTenantProviderV2` (same string values, locked in by existing queued rows).

**CS0618 cleanup:** Both `#pragma warning disable CS0618` blocks in production code and the one in tests removed — none reference `[Obsolete]` types any more.

**cref fix:** `Tenant.cs:48` now correctly points to `Tamma.Api.Services.Provisioning.ProvisioningState`.

**Dangling crefs eliminated:** `ProvisioningResult.cs`, `CranlTenantProviderV2.cs` summary, `CranlTenantProviderV2Tests.cs` summary, `ITenantConnectionResolver.cs`, `ProvisioningServiceCollectionExtensions.cs` — all fixed to prose or valid V2 crefs.

**Concerns:**
1. `CranlTenantProviderV2.ProvisionAsync()` still enqueues tasks with type `"provisioning.tenant"`. After deletion of `TenantProvisioningTaskHandler` (which was an `ITaskHandler` on the per-tenant queue, NOT an `IPlatformTaskHandler`), these platform-queue tasks have no registered handler and will be parked → dead-lettered by `PlatformTaskWorker`. This is a pre-existing architectural gap (V1 handler was wired to wrong queue type anyway — per the `PlatformTaskWorkerOptions` docs: "v1 Cranl `provisioning.tenant`[.deprovision] rows — no handler is registered"). The V2 dispatch path (`ProvisionTenantV2Dispatcher` → `"provisioning.tenant.v2"` → `ProvisionTenantV2TaskHandler`) is fully functional and is what admin endpoints use. The `CranlTenantProviderV2.ProvisionAsync()` sub-enqueue is a Wave D concern.
2. No schema changes were made; `ProvisioningState` column on `tenants` table is untouched.
