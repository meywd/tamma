# Task 4b Report — Complete the V2 Cranl path (narrow the deletion + wire the missing platform handler)

Built forward from HEAD `d69c42bb` (which over-deleted the Cranl engine). The
3 true `ITenantProvisioner` surface files stay deleted; the REST-walk engine is
restored; the missing platform-queue handlers are added so the V2 Cranl
provisioning path is functional end-to-end.

## Outcome
- **Build gate** `dotnet build apps/tamma-elsa/Tamma.sln -clp:ErrorsOnly` → **0 errors**.
- **Full suite** `Tamma.Api.Tests` → **Passed 3791, Failed 0, Skipped 6, Total 3797** (baseline 3771 + 7 restored workflow tests + 13 new handler tests = 3791).
- **Grep gates** both → **0 hits** (literal commands, incl. doc-comments).

## DELETE — kept deleted (the true `ITenantProvisioner` surface)
Confirmed absent on disk: `ITenantProvisioner.cs`, `NullTenantProvisioner.cs`,
`CranlTenantProvisioner.cs`, `NullTenantProvisionerTests.cs`,
`CranlTenantProvisionerTests.cs`. The `ProvisioningStatus` record stays deleted
(0 references; the engine returns `Task`, not `ProvisioningStatus`).

## RESTORE (over-deleted — brought back from base `7678e794`)
- `src/Tamma.Api/Services/Provisioning/CranlProvisioningWorkflow.cs` — the 435-line Cranl REST-walk engine (`git checkout 7678e794 -- …`). One doc-comment edit: its class summary referenced `<see cref="CranlTenantProvisioner"/>` (a now-deleted type) → reworded to describe the platform-queue handlers that drive it (fixes a dangling cref AND grep gate 2).
- `tests/Tamma.Api.Tests/Provisioning/CranlProvisioningWorkflowTests.cs` — restored verbatim from base (REST-walk coverage: full state machine, resume-from-existing, db-error→Failed, CranlApiException→Failed+rethrow, teardown delete order app→db→project, 404-as-absent, ShortenForName). **7/7 pass.**
- `ProvisioningModels.cs` — re-added `public sealed record ProvisioningOptions(string Region, string? CustomName = null);` (the engine's `ProvisionAsync(Guid, ProvisioningOptions, CancellationToken)` consumes it). `ProvisioningTaskPayload` (relocated by the prior commit) and `ProvisioningState`/extensions kept.
- `TenantProvisioningTaskHandler.cs` was **not** restored (it was the mis-wired per-tenant `ITaskHandler`); replaced by the platform handlers below.

## ADD — the missing wiring (the new work)
Two `IPlatformTaskHandler` implementations in `src/Tamma.Api/Services/Provisioning/`, ctor-injection style mirroring `ProvisionTenantV2TaskHandler`:

- **`CranlProvisionPlatformTaskHandler`** — `TaskType => CranlTenantProviderV2.ProvisioningTaskType` (`"provisioning.tenant"`). Deserialises `ProvisioningTaskPayload`; region falls back to `CranlOptions.DefaultRegion` when blank (injected `CranlOptions`, correcting the deleted handler's hardcoded `"germany-1"`); drives `CranlProvisioningWorkflow.ProvisionAsync(tenantId, new ProvisioningOptions(region, customName), ct)`.
- **`CranlDeprovisionPlatformTaskHandler`** — `TaskType => CranlTenantProviderV2.DeprovisioningTaskType` (`"provisioning.tenant.deprovision"`). Drives `CranlProvisioningWorkflow.DeprovisionAsync(tenantId, ct)`.
- **`ProvisioningTaskPayloadParser`** (internal) — shared `ParseOrThrow(PlatformQueuedTask)` so both handlers throw `PlatformTaskTerminalException` identically on malformed JSON / missing payload / empty TenantId (non-retryable → dead-letter, mirroring `ProvisionTenantV2TaskHandler`). Two task types ⇒ two classes because the registry routes by **exact** `TaskType`.

### DI registration
In `ProvisioningServiceCollectionExtensions.AddTenantProviderCranl()` (called only on the `if (options.IsConfigured)` Cranl path):
```csharp
services.TryAddScoped<CranlProvisioningWorkflow>();
services.AddPlatformTaskHandler<CranlProvisionPlatformTaskHandler>();
services.AddPlatformTaskHandler<CranlDeprovisionPlatformTaskHandler>();
```
`AddPlatformTaskHandler<T>()` does `services.AddScoped<IPlatformTaskHandler, T>()` — the exact helper `ProvisionTenantV2TaskHandler` (`…Extensions.cs:106` via interface) / `MoveTenantTaskHandler` (`Program.cs:1093`) use. No concrete-type registration needed (handlers are resolved only as `IPlatformTaskHandler`). No V1 `ITenantProvisioner`/`ITaskHandler` registration restored.

## Grep-gate doc-comment fixes (AC1 — literal 0, incl. doc-comments)
The prior author used a narrow "only `<see cref>`" interpretation; the brief
requires literal 0. Reworded every bare-token prose/`<c>` reference:
- `ITenantInfrastructureProvider.cs:5` (`<c>ITenantProvisioner</c>` → "legacy v1 Cranl-only provisioner contract") — fixes gate 1.
- `ProvisionTenantV2TaskPayload.cs`, `ProvisionTenantV2Dispatcher.cs` (×2), `NullTenantProvider.cs`, `DbTaskQueue.cs`, `ProvisionTenantV2TaskHandler.cs`, `CranlTenantProviderV2.cs`, restored `CranlProvisioningWorkflow.cs` — all `NullTenantProvisioner` / `CranlTenantProvisioner` mentions reworded to "v1 null provisioner" / "v1 Cranl provisioner" — fixes gate 2.
`CranlTenantProviderV2` (the live V2 provider) is untouched as a type and stays.

## TDD evidence (RED → GREEN)

**RED** — wrote `CranlPlatformTaskHandlerTests.cs` before the handlers existed:
```
$ dotnet build apps/tamma-elsa/tests/Tamma.Api.Tests/Tamma.Api.Tests.csproj -clp:ErrorsOnly
CranlPlatformTaskHandlerTests.cs(85,13): error CS0246: The type or namespace name
  'CranlProvisionPlatformTaskHandler' could not be found …
CranlPlatformTaskHandlerTests.cs(88,13): error CS0246: The type or namespace name
  'CranlDeprovisionPlatformTaskHandler' could not be found …
    2 Error(s)
```

**GREEN** — after creating the handlers + DI:
```
$ sg docker -c "dotnet test … --filter FullyQualifiedName~CranlPlatformTaskHandlerTests"
Passed!  - Failed: 0, Passed: 13, Skipped: 0, Total: 13
```
Coverage (13): TaskType-matches-provider-constant (both); provision happy-path
drives the real engine to `ready` over the Postgres fixture with a strict
`ICranlApiClient` mock (verifies `CreateProjectAsync` ran); blank-region →
`CranlOptions.DefaultRegion` fallback; provision null-task → `ArgumentNullException`;
provision malformed/empty/empty-TenantId → `PlatformTaskTerminalException`;
deprovision happy-path → `deprovisioned` (cranl_* columns cleared);
deprovision malformed/empty-TenantId → terminal; **registry resolves a handler
for `CranlTenantProviderV2.ProvisioningTaskType` AND `…DeprovisioningTaskType`**
(proves the orphan-enqueue is now consumed) + the no-Cranl-config negative.

**Restored engine tests:**
```
$ sg docker -c "dotnet test … --filter FullyQualifiedName~CranlProvisioningWorkflowTests"
Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7
```

## Gate evidence
```
$ dotnet build apps/tamma-elsa/Tamma.sln -clp:ErrorsOnly
Build succeeded.  0 Error(s)

$ sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests --no-build"
Passed!  - Failed: 0, Passed: 3791, Skipped: 6, Total: 3797, Duration: 7m 47s

$ grep -rn "ITenantProvisioner\b" apps/tamma-elsa/src apps/tamma-elsa/tests
(no output — 0 hits)
$ grep -rn "NullTenantProvisioner\|CranlTenantProvisioner\b" apps/tamma-elsa/src apps/tamma-elsa/tests
(no output — 0 hits)
```
Note: one **gitignored build artifact** (`tests/Tamma.Api.Tests/bin/Debug/net8.0/logs/tamma-api-20260629.log`) held 9 stale lines from earlier runs of the now-deleted `NullTenantProvisioner` type. Removed it (regenerates on the next run with no dead-type lines). Source `.cs` was already 0 before/after.

## Self-review
- Two-queue distinction respected: handlers are `IPlatformTaskHandler` (platform queue / `platform_queued_tasks` / `PlatformTaskWorker`, exact `TaskType` match) — NOT the per-tenant `ITaskHandler` the prior deleted handler wrongly used. The TaskType strings exactly equal the constants `CranlTenantProviderV2` enqueues, asserted by the registry-resolves test.
- Failure semantics mirror `ProvisionTenantV2TaskHandler`: malformed/empty → terminal (dead-letter, no retry burn); a Cranl error inside the engine flips the row to `Failed` and re-throws → worker retries → engine resumes from the last good step (idempotent).
- No schema change; `PlatformTaskWorker.RunOnStartup` untouched.
- Region correctness: `CranlOptions.DefaultRegion` instead of the deleted handler's hardcoded `"germany-1"`.

## Concerns
1. `PlatformTaskWorker.RunOnStartup` is a separate ops decision (per memory, it stays `false` to avoid the retire-saga dead-letter hazard). With it `false`, the worker doesn't auto-poll on boot; the V2 Cranl provision rows are still enqueued + consumed once the worker runs. Left untouched per the brief.
2. The two new handlers are registered only on the Cranl-configured DI path (`options.IsConfigured`); a deployment without `Cranl:ApiKey`+`OrganizationId` neither enqueues these task types nor needs the handlers (verified by the no-config negative test).
3. The registry-resolves test registers `TimeProvider.System` + a mock `IPlatformEventPublisher` because resolving the `IPlatformTaskHandler` enumerable also constructs the sibling `ProvisionTenantV2TaskHandler`'s workflow; this couples that test to the sibling's deps but proves real DI routing.
