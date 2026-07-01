# Engine → platform_events Callback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Give the Elsa engine a durable control-plane audit path for `platform_events` by adding a `POST /api/engine/platform-events` callback (mirroring #373's `domain_events` drain) and replacing the engine's no-op `NullPlatformEventPublisher` with a real publisher that POSTs to it — so the 13 tenant-lifecycle + analytics emitters that are silently dropped today land durably in `platform_events`.

**Architecture:** The Elsa runtime is hosted only in `Tamma.ElsaServer` (the engine), which has **no control-plane DB access** by design; `PlatformEventRepository` is bound to `ControlPlaneDbContext` which lives only in `Tamma.Api`. So the engine must round-trip an `EngineServiceOnly` API callback (exactly like the existing `domain_events` path). One new endpoint + one new engine client method + one publisher swap covers all 13 emitters (they all call `IPlatformEventPublisher.AppendAndPublishAsync`).

**Tech Stack:** .NET 9 / EF Core 9 / Npgsql / NUnit + Moq / `ApiTestFixture` (WebApplicationFactory) / tests via `sg docker -c "dotnet test ..."`.

## Global Constraints

- **Build gate (every task):** `dotnet build apps/tamma-elsa/Tamma.sln -clp:ErrorsOnly` → 0 errors (build is NOT docker-wrapped).
- **Test runner:** `sg docker -c "dotnet test ..."` is REQUIRED for `dotnet test` (stale session docker group). Run focused tests while iterating; the full `Tamma.Api.Tests` project before committing.
- **No schema change:** the `platform_events` table already exists. Do NOT add a migration or edit `TammaModelConfiguration`. If you think you need to, stop — you've left scope.
- **Auth:** the new endpoint MUST require `EngineServiceOnly` (service-principal only — `ServicePrincipalRequirement`/`ServicePrincipalHandler`; a `ServiceAuthPrincipal` minted from the `Tamma:ApiToken` service key). A tenant-user JWT must be rejected (it never carries the `"*"` permission). Do NOT use `WorkflowsManage`/`WorkflowsView`.
- **Idempotency / fail-closed:** reuse the #373 contract — each event carries a stable `Guid Id` on the wire (`Id == Guid.Empty ? NewGuid() : Id`); the repo append is idempotent (PK + the partial unique index on `TENANT.PROVISION.STEP_*`); a partial-batch failure returns `502` so the engine does not advance / treats the POST as failed. A dedup no-op (`AppendAsync` returns `null`) counts as SUCCESS, not failure.
- **Persist + publish:** the endpoint runs in `Tamma.Api` where the real in-process subscribers live, so it calls `IPlatformEventPublisher.AppendAndPublishAsync` (persist via the idempotent repo + fan-out). `InMemoryPlatformEventBus` swallows per-subscriber exceptions, so fan-out can't fail the append.
- **Branch:** feature branch off `origin/main` (`f118e58d`) in worktree `/home/meywd/tamma-wt/engine-platform-events`. Suggested: `feat/engine-platform-events-callback`.

---

## Verified current-state appendix (origin/main `f118e58d`, 2026-06-29 — do not re-derive)

### The #373 `domain_events` pattern to mirror
- **Endpoint** `EngineEndpoints.AppendEvents` — `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs:693` `(AppendEventsRequest req, IEventRepository eventRepo, ITenantContext tc)`. Empty-batch → 400; per-event loop projects `EngineEventRecord`→`DomainEvent` (stable Guid at L748); partial failures → `502 partial_append_failure` (L783-795); full success → `201 Created "/api/engine/events"` (L797-799).
- **Route** — `apps/tamma-elsa/src/Tamma.Api/Program.cs:2222`: `engine.MapPost("/events", EngineEndpoints.AppendEvents).RequireAuthorization("EngineServiceOnly");` (group `var engine = app.MapGroup("/api/engine").RequireAuthorization("WorkflowsView");` at L2195; the `/events` line overrides to `EngineServiceOnly`, comment L2220-2221).
- **Request DTOs** — `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs`: `AppendEventsRequest(List<EngineEventRecord> Events)` (L109); `EngineEventRecord(Guid Id, string EventType, string? Status, string? Error, DateTime? Timestamp, double? DurationMs, string? ActivityId, string? ActivityName, string? WorkflowInstanceId, int? IssueNumber, JsonElement? Data, Dictionary<string,string?>? Tags)` (L119-131). **No tenant field** (domain_events derives tenant from context).
- **Engine client** — `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs:238` `AppendEventsAsync(IReadOnlyList<Models.EngineEventRecord> events, Guid? tenantId = null, CancellationToken ct = default) → Task<bool>`. POSTs to `$"{_baseUrl}/api/engine/events"` (L246); `AddTenantHeader` → `X-Tenant-Id` (L254/399-405); Bearer service token via ctor (L41-64) + `TammaEngineAuthHandler`; returns `true` only on 2xx (L259-273). Engine wire DTOs in `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/TammaApiModels.cs` (`AppendEventsRequest` L237, `EngineEventRecord` L246).
- **Auth policy** `EngineServiceOnly` — `Program.cs:1263-1268` (`Jwt`+`ApiKey` schemes, `RequireAuthenticatedUser`, `ServicePrincipalRequirement`); handler DI `Program.cs:1152`; dev-permissive list `Program.cs:1336-1338`. `ServicePrincipalRequirement`/`ServicePrincipalHandler` in `apps/tamma-elsa/src/Tamma.Api/Auth/PermissionHandler.cs:121`/`:136` (succeeds when principal is `ServiceAuthPrincipal` OR `"permission"` claim contains `"*"`). `ServiceAuthPrincipal(Guid KeyId, string ServiceName, IReadOnlyList<string> Permissions, Guid? TenantId)` — `apps/tamma-elsa/src/Tamma.Api/Auth/AuthPrincipal.cs:35`.

### The `platform_events` target seam
- **Publisher port** — `apps/tamma-elsa/src/Tamma.Data/Abstractions/IPlatformEventPublisher.cs:35`: `Task<PlatformEvent?> AppendAndPublishAsync(PlatformEvent evt, CancellationToken ct = default)` (L44; returns persisted event, or `null` on dedup no-op). Lives in `Tamma.Data.Abstractions` so `Tamma.Activities` can reference it.
- **Real impl** (Tamma.Api) — `apps/tamma-elsa/src/Tamma.Api/Services/PlatformEvents/PlatformEventPublisher.cs:20`: ctor `(IPlatformEventBus bus, IServiceScopeFactory scopeFactory)`; `AppendAndPublishAsync` opens a fresh scope, resolves scoped `IPlatformEventRepository`, calls `bus.AppendAndPublishAsync(repo, evt, ct)`. Registered (singleton) by `AddPlatformEventBus()` — `apps/tamma-elsa/src/Tamma.Api/Extensions/PlatformEventsServiceCollectionExtensions.cs:37` (called `Program.cs:812`).
- **Repository** — `apps/tamma-elsa/src/Tamma.Data/Repositories/IPlatformEventRepository.cs:22` `Task<PlatformEvent?> AppendAsync(PlatformEvent evt, ct)` (L32). Impl `PlatformEventRepository.cs:21` (ctor `ControlPlaneDbContext`); `AppendAsync` stamps `CreatedAt`, `Add`, `SaveChangesAsync`; on `DbUpdateException` detaches + returns `null` (dedup via PK + partial unique index `(tenant_id, type, tags->>'step', tags->>'attempt') WHERE type LIKE 'TENANT.PROVISION.STEP_%'`). DI: `apps/tamma-elsa/src/Tamma.Data/DependencyInjection.cs:175` `TryAddScoped<IPlatformEventRepository, PlatformEventRepository>()` (via `AddTammaData`).
- **Entity / table** — `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformEvent.cs:20`; EF cfg `TammaModelConfiguration.cs:589-622`. Columns: `Id` (Guid PK, `gen_random_uuid()`), `Type` (varchar 255), `TenantId` (Guid?, nullable), `UserId` (Guid?, nullable), `Tags`/`Metadata`/`Data` (jsonb, default `'{}'`), `CreatedAt` (now()), `SequenceNumber` (BIGSERIAL, unique). Control-plane: `ControlPlaneDbContext.cs:502` `DbSet<PlatformEvent> PlatformEvents`; `modelBuilder.Ignore<PlatformEvent>()` in the tenant branch (`TammaModelConfiguration.cs:1186`).
- **The Null seam to replace** — class `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/NullPlatformEventPublisher.cs:35` (`AppendAndPublishAsync` logs `platform_event.dropped` WARN + returns `null`, L42-51). Registration `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:290-292`: `TryAddSingleton<IPlatformEventPublisher, NullPlatformEventPublisher>` (comment L280-289 names the planned `POST /api/engine/platform-events` follow-up). `TammaApiClient` is available in the engine (`AddHttpClient<TammaApiClient>()` at `Program.cs:234`).

### The 13 dropped emitters (all run in the engine, all call `AppendAndPublishAsync`)
Tenant lifecycle (`Tamma.Activities/TenantLifecycle/`): `TenantLifecycleActivity` base (`PROVISION.STEP_*`/`DELETE.STEP_*`), `MarkTenantActiveActivity` (`TENANT.CREATED.SUCCESS`+`PROVISIONED.SUCCESS`), `MarkTenantDeletingActivity` (`DELETE.REQUESTED`), `DeleteTenantStepActivities`/`MarkTenantDeletingForDeleteActivity` (`DELETE.STARTED`), `EmitDeletedSuccessActivity`, `EmitCleanupTerminalEventActivity` + `CleanupStepActivity` base, `EmitDeleteTerminalEventActivity` (terminal `DELETE.ABORTED`/`DELETED.SUCCESS`/`DELETE.FAILED`). Analytics (`HourlyAnalyticsRollupWorkflow`): `ComputeTenantRollupActivity`, `FanOutTenantRollupsActivity`, `ComputePlatformRollupActivity`, `EmitHourCompletedActivity`, `PurgeStaleAnalyticsActivity`. `BuildEvent` sets `Metadata={"workflowVersion":"1.0.0","eventSource":"system"}`. **No emitter changes needed** — the publisher swap covers all.

### Out of scope
- Changing the 13 emitters; cross-process pub/sub (LISTEN/NOTIFY) for engine→Api subscriber fan-out (the endpoint's in-process publish only reaches Tamma.Api subscribers in that process — accepted, matches `domain_events`); at-least-once buffering/cursor for platform events (direct POST per call; degrade-to-log on failure — proportionate for low-volume lifecycle/analytics events). `RegisterSecrets`, deleting `NullPlatformEventPublisher` (keep it; tests/fallback may reference it).

---

## File Structure

**Modify:**
- `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs` — add `AppendPlatformEventsRequest` + `PlatformEventRecord`.
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs` — add `AppendPlatformEvents` handler.
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` — map `POST /api/engine/platform-events` (`EngineServiceOnly`).
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/TammaApiModels.cs` — add engine wire DTOs.
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` — add `AppendPlatformEventsAsync`.
- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` — swap the publisher registration (L290-292).

**Create:**
- `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/EngineApiPlatformEventPublisher.cs`
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/Engine/AppendPlatformEventsEndpointTests.cs`, `apps/tamma-elsa/tests/Tamma.Activities.Tests/.../EngineApiPlatformEventPublisherTests.cs` (+ a client test alongside the existing `AppendEventsAsync` tests).

---

## Task 1: `POST /api/engine/platform-events` endpoint + DTOs

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:2222` (area).
- Test: `apps/tamma-elsa/tests/Tamma.Api.Tests/Engine/AppendPlatformEventsEndpointTests.cs`.

**Interfaces:**
- Produces: `POST /api/engine/platform-events` accepting `AppendPlatformEventsRequest(List<PlatformEventRecord> Events)`; `PlatformEventRecord(Guid Id, string Type, Guid? TenantId, Guid? UserId, Dictionary<string,string?>? Tags, JsonElement? Metadata, JsonElement? Data, DateTime? CreatedAt)`. Returns `201` on full success, `502 partial_append_failure` if any event throws, `400` on empty batch. `EngineServiceOnly`.

- [ ] **Step 1: Add the DTOs** to `EngineDtos.cs` (mirror `AppendEventsRequest`/`EngineEventRecord` but platform-shaped — note `Type` not `EventType`, and the nullable `TenantId`/`UserId` carried in the body):

```csharp
/// <summary>Batch of platform (control-plane) events from the engine, written to platform_events.</summary>
public record AppendPlatformEventsRequest(List<PlatformEventRecord> Events);

/// <summary>One platform event. Id is the stable idempotency key (Guid.Empty → server assigns).
/// TenantId is nullable and carried in the body (platform_events is cross-tenant), unlike domain_events.</summary>
public record PlatformEventRecord(
    Guid Id,
    string Type,
    Guid? TenantId,
    Guid? UserId,
    Dictionary<string, string?>? Tags,
    System.Text.Json.JsonElement? Metadata,
    System.Text.Json.JsonElement? Data,
    DateTime? CreatedAt);
```

- [ ] **Step 2: Write the failing endpoint test** in `AppendPlatformEventsEndpointTests.cs` (mirror the existing `domain_events` endpoint test style — `ApiTestFixture` in Development mode is permissive for auth; for the auth-rejection assertion follow how an `EngineServiceOnly`-gated endpoint is tested elsewhere, e.g. an existing `/api/engine/events` auth test, or assert the route requires the policy):

```csharp
[Test]
public async Task AppendPlatformEvents_PersistsRowToPlatformEvents_AndIsIdempotent()
{
    var id = Guid.NewGuid();
    var tenantId = Guid.NewGuid();
    var body = new
    {
        events = new[] { new {
            id, type = "TENANT.DELETED.SUCCESS", tenantId, userId = (Guid?)null,
            tags = new Dictionary<string,string?> { ["source"] = "cleanup-workflow" },
            metadata = (object?)null, data = (object?)null, createdAt = (DateTime?)null } }
    };
    var resp = await Client.PostAsJsonAsync("/api/engine/platform-events", body);
    Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Created));

    // re-POST the SAME id → dedup no-op, still 201, still exactly one row
    var resp2 = await Client.PostAsJsonAsync("/api/engine/platform-events", body);
    Assert.That(resp2.StatusCode, Is.EqualTo(HttpStatusCode.Created));

    await using var db = NewControlPlaneDbContext(); // per the fixture's helper
    var rows = await db.PlatformEvents.Where(e => e.Id == id).ToListAsync();
    Assert.That(rows, Has.Count.EqualTo(1));
    Assert.That(rows[0].Type, Is.EqualTo("TENANT.DELETED.SUCCESS"));
    Assert.That(rows[0].TenantId, Is.EqualTo(tenantId));
}
```

(Use the fixture's actual control-plane DbContext accessor; copy it from a neighboring test that reads `platform_events`/control-plane rows. If `ApiTestFixture` runs auth-permissive, add a second test that the route carries `EngineServiceOnly` exactly as the existing `/api/engine/events` test does — or assert a tenant-user token gets 403 if that harness exists.)

- [ ] **Step 3: Run it to verify it fails**

Run: `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests --filter FullyQualifiedName~AppendPlatformEventsEndpointTests"`
Expected: FAIL (route 404 / handler missing).

- [ ] **Step 4: Add the handler** to `EngineEndpoints.cs` (mirror `AppendEvents`; resolve `IPlatformEventPublisher` so the append is persisted + fanned out in-process):

```csharp
public static async Task<IResult> AppendPlatformEvents(
    AppendPlatformEventsRequest req,
    Tamma.Data.Abstractions.IPlatformEventPublisher publisher)
{
    if (req?.Events is null || req.Events.Count == 0)
        return Results.BadRequest(new { error = "empty_batch" });

    var failures = new List<object>();
    var persisted = 0;
    foreach (var e in req.Events)
    {
        if (string.IsNullOrWhiteSpace(e.Type)) { failures.Add(new { id = e.Id, error = "empty_type" }); continue; }
        try
        {
            var evt = new Tamma.Data.Entities.PlatformEvent
            {
                Id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id,
                Type = e.Type,
                TenantId = e.TenantId,
                UserId = e.UserId,
                Tags = e.Tags is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(e.Tags),
                Metadata = e.Metadata.HasValue ? e.Metadata.Value.GetRawText() : "{}",
                Data = e.Data.HasValue ? e.Data.Value.GetRawText() : "{}",
                CreatedAt = e.CreatedAt ?? DateTime.UtcNow,
            };
            // null result = idempotent dedup no-op = success (already persisted).
            await publisher.AppendAndPublishAsync(evt);
            persisted++;
        }
        catch (Exception ex)
        {
            failures.Add(new { id = e.Id, type = e.Type, error = ex.Message });
        }
    }

    if (failures.Count > 0)
        return Results.Json(new { error = "partial_append_failure", persisted, failed = failures.Count, failures },
            statusCode: StatusCodes.Status502BadGateway);

    return Results.Created("/api/engine/platform-events", new { ok = true, persisted });
}
```

> Confirm `PlatformEvent`'s `Tags`/`Metadata`/`Data` are `string` (jsonb-as-string) per the entity — serialize accordingly. Match the exact column types from the appendix.

- [ ] **Step 5: Map the route** in `Program.cs` next to the `/events` line (≈L2222), gated `EngineServiceOnly`:

```csharp
engine.MapPost("/platform-events", EngineEndpoints.AppendPlatformEvents)
    .RequireAuthorization("EngineServiceOnly");
```

- [ ] **Step 6: Run the test to verify it passes**

Run: same filter as Step 3 → PASS. Then run the full `Tamma.Api.Tests` project → green.

- [ ] **Step 7: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.Api/Dtos/Engine/EngineDtos.cs \
        apps/tamma-elsa/src/Tamma.Api/Endpoints/EngineEndpoints.cs \
        apps/tamma-elsa/src/Tamma.Api/Program.cs \
        apps/tamma-elsa/tests/Tamma.Api.Tests/Engine/AppendPlatformEventsEndpointTests.cs
git commit -m "feat(platform-events): POST /api/engine/platform-events callback (EngineServiceOnly)"
```

---

## Task 2: Engine-side client `TammaApiClient.AppendPlatformEventsAsync`

**Files:**
- Modify: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/TammaApiModels.cs`, `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs`.
- Test: alongside the existing `AppendEventsAsync` client tests in `Tamma.Activities.Tests`.

**Interfaces:**
- Consumes: the Task-1 endpoint contract (`/api/engine/platform-events`, `AppendPlatformEventsRequest`).
- Produces: `TammaApiClient.AppendPlatformEventsAsync(IReadOnlyList<Models.PlatformEventRecord> events, CancellationToken ct = default) → Task<bool>` (true only on 2xx). Engine wire DTOs `Models.AppendPlatformEventsRequest` / `Models.PlatformEventRecord` (camelCase mirror).

- [ ] **Step 1: Add the engine wire DTOs** to `TammaApiModels.cs` (mirror `Models.EngineEventRecord`'s style):

```csharp
public sealed record AppendPlatformEventsRequest(IReadOnlyList<PlatformEventRecord> Events);

public sealed record PlatformEventRecord(
    Guid Id,
    string Type,
    Guid? TenantId,
    Guid? UserId,
    IReadOnlyDictionary<string, string?>? Tags,
    System.Text.Json.JsonElement? Metadata,
    System.Text.Json.JsonElement? Data,
    DateTime? CreatedAt);
```

- [ ] **Step 2: Write the failing client test** (mirror the existing `AppendEventsAsync` test — construct `TammaApiClient` over a fake `HttpMessageHandler` that records the request + returns `201`/`500`):

```csharp
[Test]
public async Task AppendPlatformEventsAsync_Posts_To_PlatformEvents_Endpoint_And_Returns_True_On_2xx()
{
    HttpRequestMessage? captured = null;
    var handler = new StubHandler((req) => { captured = req; return new HttpResponseMessage(HttpStatusCode.Created); });
    var client = NewTammaApiClient(handler); // mirror the AppendEventsAsync test's construction
    var evt = new Models.PlatformEventRecord(Guid.NewGuid(), "TENANT.DELETED.SUCCESS", Guid.NewGuid(), null, null, null, null, null);

    var ok = await client.AppendPlatformEventsAsync(new[] { evt }, CancellationToken.None);

    Assert.That(ok, Is.True);
    Assert.That(captured!.RequestUri!.AbsolutePath, Is.EqualTo("/api/engine/platform-events"));
}

[Test]
public async Task AppendPlatformEventsAsync_Returns_False_On_Non2xx()
{
    var client = NewTammaApiClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    var ok = await client.AppendPlatformEventsAsync(
        new[] { new Models.PlatformEventRecord(Guid.NewGuid(), "X", null, null, null, null, null, null) }, default);
    Assert.That(ok, Is.False);
}
```

(Reuse whatever stub-handler + client-construction helper the existing `AppendEventsAsync` tests use; do not invent a new harness.)

- [ ] **Step 3: Run to verify failure** — `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests --filter FullyQualifiedName~AppendPlatformEventsAsync"` → FAIL (method missing).

- [ ] **Step 4: Implement `AppendPlatformEventsAsync`** in `TammaApiClient.cs` (mirror `AppendEventsAsync` L238-273; no `X-Tenant-Id` header needed — `TenantId` travels per-event in the body, and `EngineServiceOnly` is satisfied by the service Bearer token):

```csharp
public async Task<bool> AppendPlatformEventsAsync(
    IReadOnlyList<Models.PlatformEventRecord> events,
    CancellationToken ct = default)
{
    if (events is null || events.Count == 0) return true;
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/engine/platform-events")
        {
            Content = JsonContent.Create(new Models.AppendPlatformEventsRequest(events)),
        };
        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger?.LogWarning("platform_events.append_failed status={Status} count={Count}",
                (int)resp.StatusCode, events.Count);
            return false;
        }
        return true;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        _logger?.LogWarning(ex, "platform_events.append_error count={Count}", events.Count);
        return false;
    }
}
```

> Match the exact field names this class already uses (`_baseUrl`, `_http`/the named client, `_logger`) — read `AppendEventsAsync` and copy its transport + auth approach verbatim.

- [ ] **Step 5: Run the tests → PASS.** Then the full `Tamma.Activities.Tests` project → green.

- [ ] **Step 6: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/TammaApiModels.cs \
        apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs \
        apps/tamma-elsa/tests/Tamma.Activities.Tests
git commit -m "feat(platform-events): engine client AppendPlatformEventsAsync"
```

---

## Task 3: Real engine publisher + replace the Null seam

**Files:**
- Create: `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/EngineApiPlatformEventPublisher.cs`.
- Modify: `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:290-292`.
- Test: `apps/tamma-elsa/tests/Tamma.Activities.Tests/.../EngineApiPlatformEventPublisherTests.cs`.

**Interfaces:**
- Consumes: `TammaApiClient.AppendPlatformEventsAsync` (Task 2), `IPlatformEventPublisher` (`Tamma.Data.Abstractions`), `PlatformEvent` (`Tamma.Data.Entities`).
- Produces: `EngineApiPlatformEventPublisher : IPlatformEventPublisher` — `AppendAndPublishAsync(evt, ct)` maps `PlatformEvent`→`Models.PlatformEventRecord`, POSTs via `TammaApiClient`, returns `evt` on success / `null` + WARN on failure. Resolves `TammaApiClient` per-call (avoid the singleton-captures-typed-client trap).

- [ ] **Step 1: Write the failing publisher test** (use a real or mocked `TammaApiClient` over a stub handler returning 201/500; assert the record shape + the degrade path):

```csharp
[Test]
public async Task AppendAndPublishAsync_Posts_Event_And_Returns_It_On_Success()
{
    HttpRequestMessage? captured = null;
    var api = NewTammaApiClient(new StubHandler(req => { captured = req; return new HttpResponseMessage(HttpStatusCode.Created); }));
    var pub = NewPublisher(api); // see ctor note below
    var evt = new PlatformEvent { Id = Guid.NewGuid(), Type = "TENANT.DELETED.SUCCESS", TenantId = Guid.NewGuid(), Tags = "{}", Metadata = "{}", Data = "{}" };

    var result = await pub.AppendAndPublishAsync(evt, CancellationToken.None);

    Assert.That(result, Is.SameAs(evt));
    Assert.That(captured!.RequestUri!.AbsolutePath, Is.EqualTo("/api/engine/platform-events"));
}

[Test]
public async Task AppendAndPublishAsync_Returns_Null_And_Does_Not_Throw_On_Post_Failure()
{
    var api = NewTammaApiClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    var pub = NewPublisher(api);
    var evt = new PlatformEvent { Id = Guid.NewGuid(), Type = "X", Tags = "{}", Metadata = "{}", Data = "{}" };
    var result = await pub.AppendAndPublishAsync(evt, CancellationToken.None);
    Assert.That(result, Is.Null); // degraded: logged, not thrown (mirrors Null seam philosophy)
}
```

- [ ] **Step 2: Run → FAIL** (`sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests --filter FullyQualifiedName~EngineApiPlatformEventPublisher"`).

- [ ] **Step 3: Implement `EngineApiPlatformEventPublisher`** (resolve `TammaApiClient` per-call via `IServiceScopeFactory` — mirror `PlatformEventPublisher`'s scope-resolution to avoid a singleton capturing the transient typed client):

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Engine-side <see cref="IPlatformEventPublisher"/>: the engine has no control-plane
/// DB access, so it POSTs platform events to POST /api/engine/platform-events (mirroring
/// the domain_events drain). Persistence + idempotency happen server-side. On POST failure
/// it logs and returns null (degraded, not throwing) — same philosophy as the prior
/// NullPlatformEventPublisher, but events now land durably when the API is reachable.
/// </summary>
public sealed class EngineApiPlatformEventPublisher : IPlatformEventPublisher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EngineApiPlatformEventPublisher> _logger;

    public EngineApiPlatformEventPublisher(
        IServiceScopeFactory scopeFactory, ILogger<EngineApiPlatformEventPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<PlatformEvent?> AppendAndPublishAsync(PlatformEvent evt, CancellationToken ct = default)
    {
        if (evt is null) return null;
        using var scope = _scopeFactory.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<TammaApiClient>();

        var record = new TammaApiClient.Models.PlatformEventRecord(  // use the actual Models namespace path
            evt.Id,
            evt.Type,
            evt.TenantId,
            evt.UserId,
            ParseTags(evt.Tags),
            ToJsonElement(evt.Metadata),
            ToJsonElement(evt.Data),
            evt.CreatedAt == default ? null : evt.CreatedAt);

        var ok = await api.AppendPlatformEventsAsync(new[] { record }, ct).ConfigureAwait(false);
        if (!ok)
        {
            _logger.LogWarning("platform_event.post_failed type={Type} tenantId={TenantId}", evt.Type, evt.TenantId);
            return null;
        }
        return evt;
    }

    private static IReadOnlyDictionary<string, string?>? ParseTags(string? json) =>
        string.IsNullOrWhiteSpace(json) || json == "{}" ? null
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(json);

    private static System.Text.Json.JsonElement? ToJsonElement(string? json) =>
        string.IsNullOrWhiteSpace(json) || json == "{}" ? (System.Text.Json.JsonElement?)null
            : System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
}
```

> Fix the `Models.PlatformEventRecord` reference to the real namespace (`Tamma.Activities.LlmCall.Models.PlatformEventRecord`). Confirm `PlatformEvent.Tags/Metadata/Data` are `string` (jsonb-as-string); if they're already `JsonElement`/objects, adjust the mapping. The ctor-injection style + `IServiceScopeFactory` mirrors `PlatformEventPublisher`.

- [ ] **Step 4: Swap the registration** in `Tamma.ElsaServer/Program.cs:290-292` — replace the `NullPlatformEventPublisher` `TryAddSingleton` with the real publisher:

```csharp
// Real engine→API platform-events publisher (replaces the NullPlatformEventPublisher
// no-op seam): tenant-lifecycle + analytics activities now POST to
// /api/engine/platform-events instead of dropping. See EngineApiPlatformEventPublisher.
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
    .TryAddSingleton<Tamma.Data.Abstractions.IPlatformEventPublisher,
        Tamma.Activities.TenantLifecycle.EngineApiPlatformEventPublisher>(builder.Services);
```

Keep the `NullPlatformEventPublisher` class (do not delete — it documents the seam and may be referenced by tests). Update the surrounding comment (L280-289) to say the callback now exists.

- [ ] **Step 5: Add a wire-up test** confirming the engine resolves the real publisher (not Null). If the engine has a DI-composition test harness, assert `IPlatformEventPublisher` resolves to `EngineApiPlatformEventPublisher`. If not, this is covered by the unit tests + Step 3 — note that in the report.

- [ ] **Step 6: Run the publisher tests → PASS;** build the solution → 0 errors; run the full `Tamma.Activities.Tests` project → green.

- [ ] **Step 7: Commit**

```bash
git add apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/EngineApiPlatformEventPublisher.cs \
        apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs \
        apps/tamma-elsa/tests/Tamma.Activities.Tests
git commit -m "feat(platform-events): real engine publisher replaces NullPlatformEventPublisher"
```

---

## Risks

| Risk | Mitigation |
|---|---|
| Endpoint reachable by tenant users (audit forgery / spend) | `EngineServiceOnly` (service-principal only) copied verbatim from the `/events` line; Task 1 includes the auth-rejection assertion (or route-policy assertion). |
| Singleton publisher captures a transient typed `TammaApiClient` (stale handler) | Resolve `TammaApiClient` per-call via `IServiceScopeFactory` (mirrors `PlatformEventPublisher`). |
| Lost audit event on a transient API failure | Degrade-to-WARN (`platform_event.post_failed`) — strictly better than today's always-drop; the authoritative lifecycle state is still the `tenants` columns the terminals write directly. At-least-once buffering is a documented follow-up, out of scope. |
| Duplicate rows on activity retry for non-`PROVISION.STEP` types | PK-level dedup applies only when the caller sends a stable non-empty `Id`; in production all 11 lifecycle emitters go through `TenantLifecycleEvents.BuildEvent` (never sets `Id` → `Guid.Empty` → server mints a fresh Id per POST), and the 2 analytics emitters use `Guid.NewGuid()` per build — so PK-dedup is effectively dormant. The real cross-retry guard is the partial unique index on `(tenant_id, type, tags->>'step', tags->>'attempt') WHERE type LIKE 'TENANT.PROVISION.STEP_%'`, which does survive round-trips. `DELETE.STEP_*`, terminal, and analytics events are not index-covered and can duplicate on a lost-success retry. Tracked as a follow-up (see below). |
| jsonb column mapping (`Tags`/`Metadata`/`Data` string vs object) | Task 1/3 steps say to confirm the entity's actual types and serialize/parse accordingly. |
| Engine in-process subscribers don't fire (endpoint publishes in Tamma.Api, not the engine) | Accepted + same as `domain_events`; the endpoint's `AppendAndPublishAsync` fans out to the Tamma.Api in-process subscribers (where they live). Cross-process fan-out is a separate documented concern. |

## Acceptance criteria

1. `POST /api/engine/platform-events` exists, `EngineServiceOnly`, accepts a batch, persists to `platform_events` via `IPlatformEventPublisher.AppendAndPublishAsync`, idempotent on stable `Id` (re-POST → one row, 201), partial failure → 502, empty batch → 400; covered by an endpoint test.
2. `TammaApiClient.AppendPlatformEventsAsync` POSTs to it (true on 2xx, false otherwise); covered by a client test.
3. `EngineApiPlatformEventPublisher` replaces `NullPlatformEventPublisher` in the engine (`ElsaServer/Program.cs`); resolves `TammaApiClient` per-call; returns the event on success and degrades to WARN+null on failure; covered by a publisher test. All 13 emitters now flow through it (no emitter changes).
4. Build 0 errors; full `Tamma.Api.Tests` + `Tamma.Activities.Tests` green; **no schema change / no migration**.

## Follow-ups

- **FOLLOW-UP (before any exactly-once consumer of `platform_events`, e.g. aggregation/billing):** give the 13 emitters deterministic/stable event Ids (or add natural-key unique indexes for `DELETE.STEP_*`/terminal/analytics types), and consider at-least-once buffering for the engine publisher. Today's PK-dedup is dormant (emitters send `Guid.Empty`); only `TENANT.PROVISION.STEP_*` is index-protected.

## Self-review

- Spec coverage: the memory's ask ("mirror #373 → platform_events via `IPlatformEventRepository`, `EngineServiceOnly`, replace `NullPlatformEventPublisher`") maps to Tasks 1/3; the engine client is Task 2. The endpoint uses `IPlatformEventPublisher` (which wraps the repo) rather than the bare repo to also fan out to in-process subscribers — a deliberate, justified deviation noted in Global Constraints.
- Type consistency: `PlatformEventRecord` field order is identical in the API DTO (Task 1) and the engine wire DTO (Task 2); the publisher (Task 3) maps `PlatformEvent`→that record.
- Highest blast radius: Task 3's `Program.cs` swap (changes behavior for all 13 emitters) — gated behind the publisher unit tests + the build; the change is additive (dropped → POSTed) and degrades safely.
