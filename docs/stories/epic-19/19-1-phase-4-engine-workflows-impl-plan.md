# Phase 4 Implementation Plan: Engine + Workflows + SignalR

Epic 19, Story 19-1 -- API Consolidation from TypeScript to C#

Phase 4 ports **40 REST endpoints** and converts **3 SSE streams** to SignalR.
This is the most complex phase because it touches the real-time communication
layer, the GitHub webhook verification path, the engine registry, and the
dashboard client code.

**Prerequisite**: Phases 1-3 complete (EF Core DbContext, auth middleware, tenant
isolation, and all core/domain routes already running in C#).

**Estimated effort**: 56 hours

---

## Task 1: SignalR Infrastructure Setup

**Goal**: Wire up SignalR in the C# API before any endpoint porting begins.

### 1.1 Add NuGet package

```xml
<!-- Tamma.Api.csproj -->
<PackageReference Include="Microsoft.AspNetCore.SignalR" Version="8.*" />
```

SignalR is included in the ASP.NET Core shared framework, so the explicit
package reference is only needed if a newer patch is required. Verify by
checking `dotnet list package` after adding.

### 1.2 Create `TammaHub` stub

**File**: `Hubs/TammaHub.cs`

```csharp
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;

[Authorize]
public class TammaHub : Hub
{
    // Group subscriptions -- clients call these to receive targeted updates
    public async Task SubscribeEngineState(string engineId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"engine-state:{engineId}");
    }

    public async Task UnsubscribeEngineState(string engineId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"engine-state:{engineId}");
    }

    public async Task SubscribeEngineLogs(string engineId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"engine-logs:{engineId}");
    }

    public async Task UnsubscribeEngineLogs(string engineId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"engine-logs:{engineId}");
    }

    public async Task SubscribeWorkflowEvents(string instanceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"workflow:{instanceId}");
    }

    public async Task UnsubscribeWorkflowEvents(string instanceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"workflow:{instanceId}");
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Groups are automatically cleaned up by SignalR on disconnect.
        await base.OnDisconnectedAsync(exception);
    }
}
```

### 1.3 Register hub in `Program.cs`

```csharp
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// After app.Build():
app.MapHub<TammaHub>("/api/hubs/tamma");
```

### 1.4 Configure nginx WebSocket upgrade for hub endpoint

```nginx
location /api/hubs/ {
    proxy_pass http://tamma-api-dotnet:5080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_cache_bypass $http_upgrade;
    proxy_read_timeout 86400s;  # 24h -- SignalR connections are long-lived
}
```

### 1.5 Write 3 xUnit tests

- Hub connection with valid JWT succeeds.
- Hub connection without auth returns 401.
- `SubscribeEngineState` adds the connection to the correct group (use
  `InMemoryHubLifetimeManager` to inspect groups).

**Acceptance**: `dotnet test --filter "Category=SignalR"` passes.

---

## Task 2: Engine Registry Porting

**Goal**: Port `EngineRegistry` from TS to a C# singleton service so that
engine routes and dashboard routes can share the same registry.

### 2.1 Port `EngineRegistry` class

**TS source**: `packages/api/src/engine-registry.ts`

**C# target**: `Services/EngineRegistry.cs`

The TS registry holds a `Map<string, TammaEngine>`. In C# we need an
equivalent abstraction. Since `TammaEngine` is a TS class, define a C#
interface `IEngineHandle` representing the operations the API layer needs:

```csharp
public interface IEngineHandle
{
    string Id { get; }
    EngineState GetState();
    EngineStats GetStats();
    IssueData? GetCurrentIssue();
    DevelopmentPlan? GetCurrentPlan();
    string? GetCurrentBranch();
    IEventStore? GetEventStore();
    Task RunAsync(CancellationToken ct);
    Task DisposeAsync();
}
```

The registry itself:

```csharp
public class EngineRegistry
{
    private readonly ConcurrentDictionary<string, IEngineHandle> _engines = new();

    public void Register(string id, IEngineHandle engine) { ... }
    public IEngineHandle? Get(string id) { ... }
    public IReadOnlyList<EngineInfo> List() { ... }
    public async Task DisposeAsync(string id) { ... }
    public async Task DisposeAllAsync() { ... }
    public int Count => _engines.Count;
}
```

Key difference from TS: use `ConcurrentDictionary` instead of `Map` because
ASP.NET Core is inherently multi-threaded.

### 2.2 Register as singleton in DI

```csharp
builder.Services.AddSingleton<EngineRegistry>();
```

### 2.3 Write 5 xUnit tests

- Register/Get round-trip.
- Duplicate registration throws.
- List returns all engines with correct state.
- DisposeAsync removes from registry and calls engine dispose.
- DisposeAllAsync clears all.

---

## Task 3: Engine Core Endpoints

**Goal**: Port the 5 REST endpoints from `routes/engine/index.ts` and wire
the 2 SSE streams through SignalR.

### 3.1 Port REST endpoints

**C# file**: `Endpoints/Engine/EngineEndpoints.cs`

| # | Method | Path | Notes |
|---|--------|------|-------|
| 1 | POST | `/api/engine/command` | Validate with FluentValidation. Fire-and-forget for `start`/`resume` using `Task.Run`. |
| 2 | GET | `/api/engine/state` | Build snapshot from registry's default engine. |
| 3 | GET | `/api/engine/stats` | Delegate to `engine.GetStats()`. |
| 4 | GET | `/api/engine/plan` | Return current plan or `null`. |
| 5 | GET | `/api/engine/history` | Paginated event query from event store. |

The TS `EngineCommandSchema` is a Zod discriminated union. In C# use a
`record` with a `string Type` property and a `JsonDerivedType` discriminator,
or a simple DTO:

```csharp
public record EngineCommandRequest(string Type, string? Feedback = null);
```

Validate `Type` against the set `{start, stop, pause, resume, approve, reject, skip}`.

### 3.2 Engine state SSE to SignalR broadcast

**TS behavior**: `GET /api/engine/events/state` opens an SSE connection,
sends the current state immediately, then polls every 1 second and pushes
snapshots.

**C# replacement**: A `BackgroundService` that polls engine state and
broadcasts via `IHubContext<TammaHub>`:

```csharp
public class EngineStateBroadcaster : BackgroundService
{
    private readonly EngineRegistry _registry;
    private readonly IHubContext<TammaHub> _hub;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var info in _registry.List())
            {
                var snapshot = BuildSnapshot(info);
                await _hub.Clients
                    .Group($"engine-state:{info.Id}")
                    .SendAsync("EngineStateUpdate", snapshot, ct);
            }
            await Task.Delay(1000, ct);
        }
    }
}
```

Register in DI:
```csharp
builder.Services.AddHostedService<EngineStateBroadcaster>();
```

### 3.3 Engine logs SSE to SignalR broadcast

**TS behavior**: `GET /api/engine/events/logs` polls the event store every
500ms, pushes new events since last seen index.

**C# replacement**: A second `BackgroundService` (or a channel within the
same service) that tracks `lastSeenIndex` per engine and broadcasts to the
`engine-logs:{engineId}` group:

```csharp
public class EngineLogBroadcaster : BackgroundService
{
    // Tracks lastSeenIndex per engineId
    private readonly ConcurrentDictionary<string, int> _cursors = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var info in _registry.List())
            {
                var store = _registry.Get(info.Id)?.GetEventStore();
                if (store == null) continue;

                var events = await store.GetEventsAsync(tenantId);
                var lastSeen = _cursors.GetOrAdd(info.Id, 0);

                if (events.Count > lastSeen)
                {
                    var newEvents = events.Skip(lastSeen).ToList();
                    await _hub.Clients
                        .Group($"engine-logs:{info.Id}")
                        .SendAsync("EngineLogEntry", newEvents, ct);
                    _cursors[info.Id] = events.Count;
                }
            }
            await Task.Delay(500, ct);
        }
    }
}
```

### 3.4 Heartbeat

The TS implementation sends `:heartbeat\n\n` every 15 seconds. SignalR has
its own built-in keep-alive mechanism (`KeepAliveInterval`, default 15s).
Configure in `Program.cs`:

```csharp
builder.Services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});
```

No manual heartbeat code needed.

### 3.5 Write 8 xUnit tests

- POST `/api/engine/command` with each valid type returns 200.
- POST `/api/engine/command` with invalid type returns 400.
- GET `/api/engine/state` returns snapshot shape.
- GET `/api/engine/stats` returns stats.
- GET `/api/engine/plan` returns null when no plan active.
- GET `/api/engine/history` pagination works correctly.
- `EngineStateBroadcaster` sends to correct SignalR group.
- `EngineLogBroadcaster` only sends unseen events.

---

## Task 4: Engine Context Endpoints

**Goal**: Port 4 endpoints from `engine-context-routes.ts`.

### 4.1 Port in-memory context store to C# service

**TS source**: In-memory `Map<string, StoredContext>` with 10,000 entry cap.

**C# target**: `Services/EngineContextStore.cs` -- singleton with
`ConcurrentDictionary`, same eviction logic. For production, swap to EF Core
`CycleContext` entity (deferred to post-Phase 4).

```csharp
public class EngineContextStore
{
    private readonly ConcurrentDictionary<string, StoredContext> _store = new();
    private const int MaxSize = 10_000;

    public StoredContext Store(string repository, int issueNumber,
        Dictionary<string, object> findings) { ... }
    public StoredContext? Get(string repository, int issueNumber) { ... }
    public StoredContext? GetByIssueNumber(int issueNumber) { ... }
    public QueryContextResult Query(string repository, int issueNumber,
        string query, string? role, int maxTokens) { ... }
}
```

### 4.2 Port endpoints

**C# file**: `Endpoints/Engine/EngineContextEndpoints.cs`

| # | Method | Path | Notes |
|---|--------|------|-------|
| 1 | POST | `/api/engine/store-context` | Validate body, store findings. |
| 2 | GET | `/api/engine/context/{issueNumber}` | Lookup by issueNumber, optional `?repository=`. |
| 3 | POST | `/api/engine/query-context` | Simplified RAG: text matching + token budget. |
| 4 | GET | `/api/engine/repo-config` | Delegate to `IRepoConfigReader`. |

### 4.3 Port repo-config reader

The TS `RepoConfigReader` fetches `.tamma/config.json` from a GitHub repo.
In C# use `Octokit.net` to fetch the file content via
`client.Repository.Content.GetAllContents()`.

**C# file**: `Services/RepoConfigReader.cs`

### 4.4 Write 6 xUnit tests

- Store and retrieve context by exact key.
- Retrieve by issueNumber without repository scans all entries.
- Query context filters by role and respects maxTokens budget.
- Repo-config with valid owner/repo returns content.
- Repo-config with missing `repo` query param returns 400.
- Context store evicts oldest entry when exceeding 10,000.

---

## Task 5: Engine GitHub Endpoints

**Goal**: Port 7 GitHub-related engine endpoints from `engine-github-routes.ts`.

### 5.1 Port GitHub service layer

**C# file**: `Services/GitHubEngineService.cs`

Wraps `Octokit.net` (not the REST Octokit used in TS). Methods:

- `ListIssuesAsync(owner, repo, state, labels, perPage, page)`
- `GetSecurityAlertsAsync(owner, repo, type)` -- Dependabot + CodeQL
- `CreateCommentAsync(owner, repo, issueNumber, body)`
- `AddLabelsAsync(owner, repo, issueNumber, labels)`
- `RemoveLabelAsync(owner, repo, issueNumber, label)`
- `CreateIssueAsync(owner, repo, title, body, labels, assignees)`
- `TriggerCiAsync(owner, repo, workflowFile, branch, inputs)`

Each method handles the `owner/repo` parsing internally (port the TS
`parseRepo()` helper as a static method).

### 5.2 Port endpoints

**C# file**: `Endpoints/Engine/EngineGitHubEndpoints.cs`

| # | Method | Path | Notes |
|---|--------|------|-------|
| 1 | GET | `/api/engine/issues` | Query params: `repo`, `labels`, `state`, `per_page`, `page`. Filter out PRs. |
| 2 | GET | `/api/engine/security-alerts` | Query params: `repo`, `type`. Graceful degradation when Dependabot/CodeQL not enabled. |
| 3 | POST | `/api/engine/issue-comment` | Body: `{repository, issueNumber, body}`. |
| 4 | POST | `/api/engine/issue-labels` | Body: `{repository, issueNumber, labels[]}`. |
| 5 | DELETE | `/api/engine/issue-labels/{repo}/{issueNumber}/{label}` | URL params only. |
| 6 | POST | `/api/engine/create-issue` | Body: `{repository, title, body?, labels?, assignees?}`. Return 201. |
| 7 | POST | `/api/engine/trigger-ci` | Body: `{repository, branchName, workflowFile, inputs?}`. |

### 5.3 503 behavior when Octokit not configured

The TS routes return 503 when no Octokit instance is injected. Replicate
this in C# by checking if `IGitHubClient` is registered in DI:

```csharp
var client = httpContext.RequestServices.GetService<IGitHubClient>();
if (client is null)
    return Results.StatusCode(503, new { error = "GitHub integration not configured" });
```

### 5.4 Write 7 xUnit tests

One per endpoint, using a mock `IGitHubClient` (Moq or NSubstitute):

- List issues filters out PRs.
- Security alerts returns partial results when one alert type fails.
- Issue comment returns comment ID and HTML URL.
- Add labels returns label names.
- Remove label returns `{removed: true}`.
- Create issue returns 201 with number and URL.
- Trigger CI returns `{dispatched: true}`.

---

## Task 6: Engine Task Endpoints

**Goal**: Port 3 endpoints from `engine-task-routes.ts` and merge the
overlapping `engine-callback.ts` execute-task endpoint.

### 6.1 Port agent resolver interface

**C# file**: `Services/IAgentResolver.cs`

```csharp
public interface IAgentResolver
{
    Task<IAgentExecutor> GetAgentForRoleAsync(string role, AgentContext context);
}

public interface IAgentExecutor
{
    Task<AgentTaskResult> ExecuteTaskAsync(AgentTaskConfig config);
}

public record AgentContext(string ProjectId, string EngineId);
public record AgentTaskConfig(string Prompt, string Cwd, string? Model = null,
    decimal? MaxBudgetUsd = null);
public record AgentTaskResult(bool Success, string Output, decimal CostUsd,
    long DurationMs, string? Error = null);
```

### 6.2 Merge `engine-callback.ts` into task endpoints

The TS codebase has two `POST /api/engine/execute-task` handlers:

1. `engine-task-routes.ts` -- uses `IAgentResolver` (role-based).
2. `engine-callback.ts` -- uses raw `IAgentProvider` (Elsa callback).

In C# consolidate into a single endpoint that accepts an optional `role`
parameter. When `role` is provided, use the resolver. When absent, use
the default agent provider.

### 6.3 Port cycle-result store

**C# file**: `Services/CycleResultStore.cs` -- in-memory list with 10,000
cap. Same pattern as context store.

### 6.4 Port endpoints

**C# file**: `Endpoints/Engine/EngineTaskEndpoints.cs`

| # | Method | Path | Notes |
|---|--------|------|-------|
| 1 | POST | `/api/engine/execute-task` | Merged endpoint. Body: `{prompt, role?, repository?, model?, maxBudgetUsd?, cwd?}`. Returns `ExecuteTaskResponse`. |
| 2 | POST | `/api/engine/cycle-result` | Body: `{exitReason, issueNumber?, repository?, error?, durationMs?, metadata?}`. Returns 201. |
| 3 | GET | `/api/engine/cycle-results` | Query: `?issueNumber=&limit=`. Returns most recent first. |

### 6.5 Port agent-available endpoint

From `engine-callback.ts`:

| # | Method | Path | Notes |
|---|--------|------|-------|
| 4 | GET | `/api/engine/agent-available` | Returns `{available: bool}`. |

### 6.6 Port callback API key auth

The TS `engine-callback.ts` uses a `preHandler` hook that does timing-safe
comparison of `x-api-key` header against a configured key. In C# implement
this as an endpoint filter or a policy:

```csharp
public class CallbackApiKeyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var key = ctx.HttpContext.Request.Headers["x-api-key"].FirstOrDefault();
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(key ?? ""),
            Encoding.UTF8.GetBytes(_expectedKey)))
        {
            return Results.Unauthorized();
        }
        return await next(ctx);
    }
}
```

### 6.7 Write 6 xUnit tests

- Execute task with resolver returns agent output.
- Execute task returns 503 when no resolver configured.
- Execute task with callback API key auth rejects invalid key.
- Cycle result stores and returns 201.
- Cycle results pagination returns most recent first.
- Agent-available returns true/false based on provider status.

---

## Task 7: Workflow CRUD Endpoints

**Goal**: Port 7 REST endpoints from `routes/workflows/index.ts` and
convert the SSE stream to SignalR.

### 7.1 Port endpoints

**C# file**: `Endpoints/Workflows/WorkflowEndpoints.cs`

| # | Method | Path | RBAC Permission | Notes |
|---|--------|------|-----------------|-------|
| 1 | POST | `/api/workflows/definitions` | `workflows:manage` | Upsert. Return 201 if new, 200 if existing. |
| 2 | GET | `/api/workflows/definitions` | `workflows:view` | List all definitions. |
| 3 | POST | `/api/workflows/instances` | `workflows:manage` | Create instance. Return 201. |
| 4 | PUT | `/api/workflows/instances/{id}` | `workflows:manage` | Partial update (status, currentActivity, variables). |
| 5 | GET | `/api/workflows/instances` | `workflows:view` | Paginated list. Filters: `definitionId`, `tenantId`. |
| 6 | POST | `/api/workflows/instances/{id}/cancel` | `workflows:manage` | Set status to `cancelled`. Idempotent. |
| 7 | DELETE | `/api/workflows/instances/{id}` | `workflows:delete` | Owner only. |

### 7.2 RBAC authorization

The TS routes use `requirePermission('workflows:manage')` etc. In C# use
ASP.NET Core authorization policies:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("WorkflowsView",
        p => p.RequireClaim("permission", "workflows:view"));
    options.AddPolicy("WorkflowsManage",
        p => p.RequireClaim("permission", "workflows:manage"));
    options.AddPolicy("WorkflowsDelete",
        p => p.RequireClaim("permission", "workflows:delete"));
});

// In endpoint registration:
group.MapPost("/definitions", WorkflowEndpoints.UpsertDefinition)
    .RequireAuthorization("WorkflowsManage");
```

### 7.3 Workflow instance SSE to SignalR

**TS behavior**: `GET /api/workflows/instances/:id/events` opens an SSE
stream, sends the current instance state, then polls every 1 second for
updates (checking `updatedAt` timestamp).

**C# replacement**: When workflow state is updated via `PUT /instances/{id}`
or `POST /instances/{id}/cancel`, the endpoint itself broadcasts the update
through SignalR:

```csharp
// In the PUT handler, after updating the instance:
await hubContext.Clients
    .Group($"workflow:{id}")
    .SendAsync("WorkflowStateUpdate", updatedInstance);
```

This is more efficient than the TS polling approach -- updates are pushed
immediately on mutation rather than polled every second. No background
service needed for workflow events since the push happens at mutation time.

Additionally, add a REST fallback for clients that cannot use SignalR:

| # | Method | Path | Notes |
|---|--------|------|-------|
| 8 | GET | `/api/workflows/instances/{id}` | Returns current instance state (one-shot, no streaming). |

### 7.4 Write 8 xUnit tests

- Upsert definition creates new (201) and updates existing (200).
- List definitions returns all.
- Create instance returns 201 with generated id.
- Update instance returns 404 for missing id.
- List instances with pagination and filters.
- Cancel already-cancelled instance is idempotent.
- Delete instance returns 404 for missing id.
- SignalR `WorkflowStateUpdate` fires on instance mutation.

---

## Task 8: GitHub App Webhook Endpoint

**Goal**: Port `POST /api/github/webhooks` with HMAC-SHA256 signature
verification.

### 8.1 Webhook signature verification in C#

**TS source**: `github-webhook.ts` lines 42-46 -- `createHmac('sha256', secret)`,
`timingSafeEqual`.

**C# equivalent** using `System.Security.Cryptography`:

```csharp
public static class GitHubWebhookValidator
{
    public static bool VerifySignature(string payload, string signatureHeader,
        string secret)
    {
        if (!signatureHeader.StartsWith("sha256="))
            return false;

        var expectedBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload));
        var expected = "sha256=" + Convert.ToHexStringLower(expectedBytes);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signatureHeader));
    }
}
```

Key details:
- `CryptographicOperations.FixedTimeEquals` is the .NET equivalent of
  Node's `timingSafeEqual`.
- `Convert.ToHexStringLower` produces lowercase hex (matching Node's
  `digest('hex')` output). Available in .NET 8+.
- Must read raw request body as string before deserialization. Use
  `[FromBody]` with a `string` parameter or `EnableBuffering()` +
  `StreamReader`.

### 8.2 Raw body capture middleware

ASP.NET Core consumes the request body stream during model binding. To
verify the signature we need the raw bytes:

```csharp
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/github/webhooks"))
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body,
            leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync();
        context.Items["RawBody"] = rawBody;
        context.Request.Body.Position = 0;
    }
    await next();
});
```

### 8.3 Port webhook event handlers

**C# file**: `Endpoints/GitHub/GitHubWebhookEndpoints.cs`

Port the event dispatch logic from TS:

| Event | Handler | Actions |
|-------|---------|---------|
| `installation` (created) | `HandleInstallationCreated` | Upsert installation + store repos |
| `installation` (deleted) | `HandleInstallationDeleted` | Remove installation, invalidate cache |
| `installation` (suspend/unsuspend) | `HandleInstallationSuspend` | Update suspension state, invalidate cache |
| `installation_repositories` | `HandleInstallationRepos` | Add/remove repos |
| `issues` / `pull_request` / `push` | `EnqueueWebhookTask` | Enqueue to task queue |

### 8.4 Port `InstallationRouter` cache

**TS source**: `services/installation-router.ts` -- TTL-based
`Map<number, CacheEntry>`.

**C# target**: `Services/InstallationRouter.cs` -- use `MemoryCache` with
TTL entries:

```csharp
public class InstallationRouter
{
    private readonly IMemoryCache _cache;
    private readonly IInstallationRepository _repo;
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(60);

    public async Task<InstallationResolveResult?> ResolveAsync(int installationId)
    {
        return await _cache.GetOrCreateAsync(
            $"installation:{installationId}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = _ttl;
                var inst = await _repo.GetByIdAsync(installationId);
                return inst is null ? null : new InstallationResolveResult(inst,
                    inst.SuspendedAt == null);
            });
    }

    public void Invalidate(int installationId)
    {
        _cache.Remove($"installation:{installationId}");
    }
}
```

### 8.5 Port task queue interface

**TS source**: `services/task-queue.ts` -- `ITaskQueue` with enqueue/dequeue.

**C# target**: `Services/ITaskQueue.cs` -- same interface. Implementation
deferred (can use `Channel<T>` for in-memory or a DB-backed queue via EF
Core).

### 8.6 Rate limiting

The TS webhook route uses `@fastify/rate-limit` (300 req/min). In C# use
the built-in rate limiter:

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("webhook", opt =>
    {
        opt.PermitLimit = 300;
        opt.Window = TimeSpan.FromMinutes(1);
    });
});

// On the endpoint:
group.MapPost("/webhooks", GitHubWebhookEndpoints.HandleWebhook)
    .RequireRateLimiting("webhook");
```

### 8.7 Write 8 xUnit tests

- Valid signature passes verification.
- Invalid signature returns 401.
- Missing `x-hub-signature-256` header returns 401.
- Installation `created` event upserts installation and stores repos.
- Installation `deleted` event removes installation and invalidates cache.
- SaaS mode rejects webhooks without `installation.id` (400).
- Issue event enqueues a task with correct type and installationId.
- Rate limiter rejects requests beyond 300/min (429).

---

## Task 9: GitHub App Callback Endpoint

**Goal**: Port `GET /api/github/callback` from `github-callback.ts`.

### 9.1 Port callback handler

**C# file**: `Endpoints/GitHub/GitHubCallbackEndpoints.cs`

Flow:
1. Parse `installation_id` and `setup_action` from query string.
2. Create App-authenticated `GitHubClient` using `GitHubJwt` NuGet package
   or inline JWT generation.
3. Fetch installation details via `client.GitHubApps.GetInstallation()`.
4. Upsert installation record.
5. Fetch accessible repos via installation-scoped client.
6. Store repos.
7. Generate API key, hash it, store hash.
8. Provision API key as GitHub Actions secret to all repos.
9. Redirect to `successRedirectUrl`.

### 9.2 Port `GitHubSecretsProvisioner`

**TS source**: `services/github-secrets-provisioner.ts`

**C# target**: `Services/GitHubSecretsProvisioner.cs`

Uses `libsodium` sealed box encryption for GitHub Actions secrets. In C#
use the `Sodium.Core` NuGet package or `NSec.Cryptography`:

```xml
<PackageReference Include="Sodium.Core" Version="1.3.*" />
```

### 9.3 Port API key generation

Reuse the `ApiKeyService` from Phase 1 (`generateApiKey`, `hashApiKey`,
`getApiKeyPrefix`).

### 9.4 Write 4 xUnit tests

- Valid `install` action stores installation and repos.
- Valid `update` action updates existing installation.
- Missing `installation_id` returns 400.
- Failed GitHub API call returns 500 (does not redirect).

---

## Task 10: SaaS Endpoints

**Goal**: Port 4 SaaS endpoints from `routes/saas/`.

### 10.1 Port API key auth for SaaS scope

The TS SaaS routes are wrapped in `registerApiKeyAuthPlugin` which
validates the API key and injects the installation context. In C# create
a policy or endpoint filter:

```csharp
public class SaaSApiKeyFilter : IEndpointFilter
{
    // Validates x-api-key header against installation store
    // Sets HttpContext.Items["InstallationId"] on success
}
```

### 10.2 Port endpoints

**C# files**:

| # | Method | Path | Source | C# File |
|---|--------|------|--------|---------|
| 1 | POST | `/api/v1/llm/chat` | `saas/llm-proxy.ts` | `Endpoints/SaaS/LlmProxyEndpoints.cs` |
| 2 | POST | `/api/v1/workflows/{id}/status` | `saas/workflow-status.ts` | `Endpoints/SaaS/WorkflowStatusEndpoints.cs` |
| 3 | POST | `/api/v1/workflows/{id}/result` | `saas/workflow-result.ts` | `Endpoints/SaaS/WorkflowResultEndpoints.cs` |
| 4 | POST | `/api/v1/installations/{id}/rotate-key` | `saas/key-rotation.ts` | `Endpoints/SaaS/KeyRotationEndpoints.cs` |

Key notes:
- LLM proxy is currently a stub returning a fixed response. Port the stub
  as-is; actual LLM integration comes later.
- Workflow status/result endpoints update the workflow store and should
  trigger SignalR `WorkflowStateUpdate` broadcasts.
- Key rotation generates a new API key, updates the DB hash, and
  re-provisions to all repos via `GitHubSecretsProvisioner`.

### 10.3 Write 5 xUnit tests

- LLM proxy returns stub response with correct shape.
- Workflow status updates instance variables.
- Workflow result finalizes instance with correct status.
- Key rotation generates new key and provisions to repos.
- Key rotation on suspended installation returns 403.

---

## Task 11: Dashboard Endpoints

**Goal**: Port 3 dashboard aggregation endpoints from `routes/dashboard/index.ts`.

### 11.1 Port endpoints

**C# file**: `Endpoints/Dashboard/DashboardEndpoints.cs`

| # | Method | Path | Notes |
|---|--------|------|-------|
| 1 | GET | `/api/dashboard/summary` | Aggregates engine count, workflow definition count, recent events (top 20). |
| 2 | GET | `/api/dashboard/engines` | Returns `EngineRegistry.List()`. |
| 3 | GET | `/api/dashboard/workflows` | Returns definitions with instance counts. |

These endpoints depend on `EngineRegistry` (Task 2) and
`IWorkflowRepository` (Phase 1).

### 11.2 Write 3 xUnit tests

- Summary returns correct engine count and workflow definition count.
- Engines list returns all registered engines.
- Workflows list includes instance counts per definition.

---

## Task 12: Dashboard Client -- SignalR Migration

**Goal**: Update the React dashboard to use `@microsoft/signalr` instead
of `EventSource` for real-time updates.

### 12.1 Install SignalR client package

```bash
cd packages/dashboard
pnpm add @microsoft/signalr
```

### 12.2 Create SignalR connection hook

**File**: `packages/dashboard/src/hooks/useSignalR.ts`

```typescript
import { HubConnectionBuilder, HubConnection, LogLevel } from '@microsoft/signalr';
import { useEffect, useRef, useState } from 'react';

export function useSignalR(hubUrl: string) {
    const connRef = useRef<HubConnection | null>(null);
    const [connected, setConnected] = useState(false);

    useEffect(() => {
        const connection = new HubConnectionBuilder()
            .withUrl(hubUrl, {
                accessTokenFactory: () => {
                    // Read JWT from cookie or auth context
                    return getAccessToken();
                },
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(LogLevel.Warning)
            .build();

        connRef.current = connection;

        connection.start()
            .then(() => setConnected(true))
            .catch(err => console.error('SignalR connect failed:', err));

        return () => {
            connection.stop();
        };
    }, [hubUrl]);

    return { connection: connRef.current, connected };
}
```

### 12.3 Create engine state subscription hook

**File**: `packages/dashboard/src/hooks/useEngineState.ts`

```typescript
export function useEngineState(engineId: string) {
    const { connection, connected } = useSignalR('/api/hubs/tamma');
    const [state, setState] = useState<EngineStateSnapshot | null>(null);

    useEffect(() => {
        if (!connection || !connected) return;

        connection.invoke('SubscribeEngineState', engineId);
        connection.on('EngineStateUpdate', (data: EngineStateSnapshot) => {
            setState(data);
        });

        return () => {
            connection.invoke('UnsubscribeEngineState', engineId);
            connection.off('EngineStateUpdate');
        };
    }, [connection, connected, engineId]);

    return state;
}
```

### 12.4 Create engine logs subscription hook

**File**: `packages/dashboard/src/hooks/useEngineLogs.ts`

Same pattern as above but subscribes to `SubscribeEngineLogs` and listens
for `EngineLogEntry` events.

### 12.5 Create workflow events subscription hook

**File**: `packages/dashboard/src/hooks/useWorkflowEvents.ts`

Subscribes to `SubscribeWorkflowEvents` with instance ID, listens for
`WorkflowStateUpdate`.

### 12.6 Update dashboard pages to use new hooks

Replace any existing `EventSource` or polling-based state fetching in
dashboard pages with the new SignalR hooks. Since the current dashboard
does not yet have SSE consumers (no `EventSource` usage found in the
codebase), the hooks are new additions that will be wired into future
dashboard pages for engine monitoring and workflow tracking.

### 12.7 Write 3 Vitest tests for hooks

- `useSignalR` establishes connection and sets `connected` to true.
- `useEngineState` calls `SubscribeEngineState` and receives updates.
- `useWorkflowEvents` calls `SubscribeWorkflowEvents` and receives updates.

Use `@microsoft/signalr` mock or a simple stub connection.

---

## Task 13: nginx Configuration Update

**Goal**: Route Phase 4 path prefixes to the C# API.

### 13.1 Add location blocks

```nginx
# Phase 4: engine, workflows, github, saas, dashboard to C# API
location /api/engine/ {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/workflows/ {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/github/ {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/v1/llm/ {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/v1/workflows/ {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/v1/installations/ {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/dashboard/ {
    proxy_pass http://tamma-api-dotnet:5080;
}

# SignalR hub -- requires WebSocket upgrade
location /api/hubs/ {
    proxy_pass http://tamma-api-dotnet:5080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_cache_bypass $http_upgrade;
    proxy_read_timeout 86400s;
}

# Phase 2 + Phase 3 blocks remain active
# Catch-all for any remaining TS routes
location /api/ {
    proxy_pass http://tamma-api:3100/api/;
}
```

### 13.2 Validate routing

After deploying the nginx config change, verify:
- Engine endpoints hit C# API (check `X-Powered-By` header or response shape).
- WebSocket upgrade succeeds for `/api/hubs/tamma`.
- Dashboard SignalR connection establishes.
- GitHub webhooks still process correctly.

---

## Task 14: Integration Testing and Rollback Plan

### 14.1 End-to-end test suite

Run the following integration tests against the deployed Phase 4 API:

1. **Engine command cycle**: POST start, GET state, verify running.
2. **Engine context**: POST store-context, GET context/{issueNumber}, verify
   round-trip.
3. **GitHub engine**: GET issues (mock repo), POST issue-comment.
4. **Workflow lifecycle**: POST definition, POST instance, PUT update,
   verify SignalR broadcast received.
5. **GitHub webhook**: POST with valid HMAC signature, verify installation
   stored.
6. **GitHub webhook invalid**: POST with bad signature, verify 401.
7. **SaaS LLM proxy**: POST chat, verify stub response.
8. **SaaS key rotation**: POST rotate-key, verify new prefix returned.
9. **Dashboard summary**: GET summary, verify engine count.
10. **SignalR connection**: Connect to hub, subscribe to engine state,
    verify heartbeat within 20 seconds.

### 14.2 Rollback procedure

If Phase 4 deployment fails:

1. Remove Phase 4 nginx `location` blocks (engine, workflows, github, saas,
   dashboard, hubs).
2. Reload nginx (`nginx -s reload`).
3. All traffic falls back to TS API catch-all block.
4. In the dashboard, revert SignalR hooks to polling/EventSource if deployed.
5. No database rollback needed -- Phase 4 does not add EF Core migrations.

---

## Task Summary

| Task | Description | C# Files | Tests | Est. Hours |
|------|-------------|----------|-------|------------|
| 1 | SignalR infrastructure | `Hubs/TammaHub.cs`, `Program.cs` | 3 | 3 |
| 2 | Engine registry | `Services/EngineRegistry.cs` | 5 | 3 |
| 3 | Engine core endpoints | `Endpoints/Engine/EngineEndpoints.cs`, broadcasters | 8 | 8 |
| 4 | Engine context endpoints | `Endpoints/Engine/EngineContextEndpoints.cs` | 6 | 4 |
| 5 | Engine GitHub endpoints | `Endpoints/Engine/EngineGitHubEndpoints.cs` | 7 | 5 |
| 6 | Engine task endpoints | `Endpoints/Engine/EngineTaskEndpoints.cs` | 6 | 5 |
| 7 | Workflow CRUD + SignalR | `Endpoints/Workflows/WorkflowEndpoints.cs` | 8 | 6 |
| 8 | GitHub webhook | `Endpoints/GitHub/GitHubWebhookEndpoints.cs` | 8 | 6 |
| 9 | GitHub callback | `Endpoints/GitHub/GitHubCallbackEndpoints.cs` | 4 | 4 |
| 10 | SaaS endpoints | `Endpoints/SaaS/*.cs` | 5 | 4 |
| 11 | Dashboard endpoints | `Endpoints/Dashboard/DashboardEndpoints.cs` | 3 | 2 |
| 12 | Dashboard SignalR client | `packages/dashboard/src/hooks/*.ts` | 3 | 4 |
| 13 | nginx configuration | `nginx/conf.d/default.conf` | 0 | 1 |
| 14 | Integration testing | N/A | 10 | 1 |
| **Total** | | | **76** | **56** |

---

## Acceptance Criteria

- [ ] All 40 REST endpoints returning correct responses (verified by xUnit integration tests).
- [ ] SignalR hub delivering real-time engine state, engine logs, and workflow events.
- [ ] GitHub webhooks processing correctly in C# API with HMAC-SHA256 verification.
- [ ] Engine Elsa callbacks (`store-context`, `execute-task`, `cycle-result`) working.
- [ ] SaaS routes (LLM proxy, workflow status/result, key rotation) working.
- [ ] Dashboard endpoints returning correct aggregated data.
- [ ] Dashboard client connects via SignalR and receives real-time updates.
- [ ] nginx routing validated: all Phase 4 paths go to C# API, WebSocket upgrade works.
- [ ] All 76 xUnit + 3 Vitest tests green.
- [ ] Rollback procedure documented and tested.
