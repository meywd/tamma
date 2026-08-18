# Story 32-6: Agent Action Trail (DCB events tagged agent_id) in Tenant Store

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **tenant owner/member observing my agents**,
I want every managed agent run, iteration, panel aggregation, and recorded bug captured as DCB events in my own tenant event store — tagged with the agent's identity, config version, role, provider, model, and prompt reference — and queryable as a per-agent action history,
So that I can audit exactly what each agent did on my work, and so the benchmarking, leaderboard, and learning stories (32-8/32-9/32-10/32-11) have a complete, tenant-isolated data substrate to consume.

## Priority

P0 - The audit/analytics substrate the rest of Epic 32's tracking, benchmarking, and learning stories are built on. Without a captured trail there is nothing to project, leaderboard, or learn from.

## Acceptance Criteria

1. Every managed agent run (`32-5` `IManagedAgent` execution) and every tool-call step within it is recorded as one or more DCB `DomainEvent` rows in the **resolving tenant's** schema (`t_<hex>.domain_events` via `IEventRepository.AppendAsync`), never on the control plane. Each event carries `TenantId` = the resolving tenant.
2. Event `Type` values follow the `AGGREGATE.ACTION.STATUS` convention and cover these families: `AGENT.TASK.SUCCESS` / `AGENT.TASK.FAILED` / `AGENT.TASK.PARTIAL`, `AGENT.TOOL_CALL.SUCCESS` / `AGENT.TOOL_CALL.FAILED`, `AGENT.ITERATION.COMPLETED`, `AGENT.PANEL.AGGREGATED`, and `REVIEW.BUG.RECORDED`.
3. Every action-trail event's `Tags` JSONB carries at minimum: `agentId`, `agentVersion`, `role`, `provider`, `model`, `promptRef`, `issueId`, `iteration`, `correlationId`, and `credentialSource` (`byok` | `platform`). Tags are flat string values (DCB tag convention), populated from a shared builder so every emission site is consistent. `REVIEW.BUG.RECORDED` additionally carries `bugType` (`visual` | `functional` | `regression` | `security` | `perf` | `style`).
4. The trail is **strictly tenant-isolated**: all reads go through `IEventRepository`, which scopes to the ambient/resolving tenant via `ITenantDbContextFactory`; there is **no cross-tenant and no platform-admin read path** for a tenant's action trail. A platform owner (`OwnerAccess`/`PlatformOwnerAccess`) calling the trail API for another tenant gets a 404/403, never that tenant's events. This holds even when the run executed a public/system agent definition — one public agent produces N independent per-tenant trails (per the Epic 32 design spec tenancy rule). Explicitly tested.
5. A tenant-scoped query API is exposed and is **member-readable** (read = any tenant member; no mutation):
   - `GET /api/v1/orgs/{tenantId}/agents/{agentId}/runs` — paginated list of runs (one row per `AGENT.TASK.*`) for that agent within that tenant, filterable by `from`/`to` date, `role`, `provider`, and `outcome` (`success` | `failed` | `partial`).
   - `GET /api/v1/orgs/{tenantId}/agents/{agentId}/trail` — paginated flat stream of all trail events for that agent within that tenant (runs, tool calls, iterations, panels, bugs), same filters plus `type` prefix.
   - Both use `SequenceNumber` as the stable, total-order pagination cursor (immune to same-millisecond `CreatedAt` collisions), exposing `nextCursor` / `hasMore` on the wire.
6. Prompt bodies and large blob payloads (full tool input/output, rendered prompt text, RAG context) are **referenced** (`promptRef`, `blobRef`), never inlined into the event stream, to keep `domain_events` lean. Sensitive content is sanitized/redacted (reuse the existing `SecureAgentProvider` / sanitization seam) before any value is persisted to `Tags`/`Data`.
7. Trail capture is **best-effort-non-blocking** for the agent run: a trail-write failure does **not** abort or fail the run. The failure is itself captured durably — retried via the existing event-append durability path and, on terminal failure, surfaced as an `AGENT.TRAIL.WRITE_FAILED` event (or log + metric) so the gap is observable rather than silent.
8. `ProviderDiagnostic` rows (cost/latency/tokens, written by `DiagnosticsService`) emitted during an agent run are linked to the trail via `agentId` + `correlationId`, so existing cost/latency aggregation (consumed by 32-9/32-10) can be re-keyed by agent. `ProviderDiagnostic` already carries `CorrelationId` and `AgentType`; this story adds/asserts the `agentId` linkage so a trail event and its diagnostics row share the same `correlationId`.
9. **Tests**: tenant isolation (tenant B cannot read tenant A's trail through any path; platform admin cannot read either), pagination/cursor correctness (stable order across same-millisecond events, no dupes/skips at page boundaries), redaction (no raw prompt/secret content lands in `Tags`/`Data`), and link integrity (a run's `AGENT.TASK.*` event and its `ProviderDiagnostic` rows share `correlationId` + `agentId`).

## Technical Design

### Where the trail lives (architecture)

The action trail is **DCB events in the tenant's own `domain_events` table** — not a new table, not on the control plane. This is structural isolation: `IEventRepository.AppendAsync` resolves the tenant via `evt.TenantId ?? tenantContext.TenantId` and writes through `ITenantDbContextFactory.CreateAsync(tid)` into the `t_<hex>` schema. There is no code path that writes a tenant-scoped (`TenantId != null`) event anywhere but that tenant's schema, and the read methods (`QueryWithPaginationAsync`, `ListByTenantAsync`) **throw `NotSupportedException` on a null `tenantId`** for paginated/tenant-scoped reads — so a cross-tenant trail read is not merely unauthorized, it is unimplementable through this repository. That property is the backbone of AC 4.

```
Managed agent run (32-5 IManagedAgent)
  │
  ├─ AgentTrailEmitter.RunStarted/StepCompleted/IterationCompleted/...
  │     → builds DomainEvent { Type, TenantId = resolving tenant, Tags, Metadata, Data }
  │     → IEventRepository.AppendAsync(evt)            // tenant t_<hex>.domain_events
  │
  └─ DiagnosticsService.RecordEventAsync(ProviderDiagnostic { CorrelationId, AgentType, TenantId })
        → linked to trail by (agentId, correlationId)
```

### Event schema

All events reuse the existing `Tamma.Data.Entities.DomainEvent` row shape (no schema change):
`Id`, `Type`, `TenantId`, `IssueNumber`, `Tags` (JSONB), `Metadata` (JSONB), `Data` (JSONB), `CreatedAt`, `SequenceNumber` (server-side `BIGSERIAL`, the cursor).

| Event `Type` | When | Notable `Data` fields (blob-referenced, never inlined) |
|---|---|---|
| `AGENT.TASK.SUCCESS` / `.FAILED` / `.PARTIAL` | A managed agent run completes | `durationMs`, `iterations`, `inputTokens`, `outputTokens`, `costUsd`, `outcomeRef` |
| `AGENT.TOOL_CALL.SUCCESS` / `.FAILED` | One tool invocation in the run's tool loop | `toolName`, `argsRef` (sanitized ref), `resultRef`, `durationMs`, `errorCode?` |
| `AGENT.ITERATION.COMPLETED` | One design/review iteration of the loop finishes | `iteration`, `gatePassed`, `findingsCount` |
| `AGENT.PANEL.AGGREGATED` | `AggregatePanelActivity` combines N agent results (32-7) | `strategy` (`single`/`consensus`/`lead+critics`/`llm-judge-merge`), `participantAgentIds`, `chosenAgentId?` |
| `REVIEW.BUG.RECORDED` | A bug is classified at review/gate (32-8) | `bugType`, `severity`, `descriptionRef` |

**Tags builder (shared, consistent across all sites):**

```csharp
// src/Tamma.Api/Services/Agents/AgentTrailTags.cs
public static string Build(AgentTrailContext c) =>
    JsonSerializer.Serialize(new Dictionary<string, string?>
    {
        ["agentId"]          = c.AgentId.ToString(),
        ["agentVersion"]     = c.AgentVersion.ToString(),
        ["role"]             = c.Role,
        ["provider"]         = c.Provider,
        ["model"]            = c.Model,
        ["promptRef"]        = c.PromptRef,        // key/version, NOT prompt body
        ["issueId"]          = c.IssueId,
        ["iteration"]        = c.Iteration.ToString(),
        ["correlationId"]    = c.CorrelationId.ToString(),
        ["credentialSource"] = c.CredentialSource, // "byok" | "platform"
    });
```

`Metadata` is the standard DCB envelope (`workflowVersion`, `eventSource = "system"`), mirroring `AgentEndpoints.UpdateConfig`'s existing emission.

### C# emission helper

A thin, injectable emitter keeps every call site uniform and enforces the non-blocking contract (AC 7). It is the single seam 32-5/32-7/32-8 call; it never throws into the run.

```csharp
// src/Tamma.Api/Services/Agents/IAgentTrailEmitter.cs
public interface IAgentTrailEmitter
{
    Task RunCompletedAsync(AgentTrailContext ctx, AgentRunOutcome outcome, CancellationToken ct = default);
    Task ToolCallAsync(AgentTrailContext ctx, ToolCallRecord call, CancellationToken ct = default);
    Task IterationCompletedAsync(AgentTrailContext ctx, IterationRecord it, CancellationToken ct = default);
    Task PanelAggregatedAsync(AgentTrailContext ctx, PanelRecord panel, CancellationToken ct = default);
    Task BugRecordedAsync(AgentTrailContext ctx, BugRecord bug, CancellationToken ct = default);
}

// src/Tamma.Api/Services/Agents/AgentTrailEmitter.cs
public sealed class AgentTrailEmitter(
    IEventRepository events,
    IContentSanitizer sanitizer,   // SecureAgentProvider sanitization seam
    ILogger<AgentTrailEmitter> logger) : IAgentTrailEmitter
{
    public async Task RunCompletedAsync(AgentTrailContext ctx, AgentRunOutcome o, CancellationToken ct = default)
    {
        var type = o.Status switch
        {
            RunStatus.Success => "AGENT.TASK.SUCCESS",
            RunStatus.Partial => "AGENT.TASK.PARTIAL",
            _                 => "AGENT.TASK.FAILED",
        };
        await EmitAsync(ctx, type, BuildRunData(o), ct);
    }

    // Never throws into the run: a trail-write failure is logged, retried by
    // the durable append path, and on terminal failure emits a best-effort
    // AGENT.TRAIL.WRITE_FAILED breadcrumb (AC 7).
    private async Task EmitAsync(AgentTrailContext ctx, string type, object data, CancellationToken ct)
    {
        try
        {
            await events.AppendAsync(new DomainEvent
            {
                Id        = Guid.NewGuid(),
                Type      = type,
                TenantId  = ctx.TenantId,                         // resolving tenant — structural isolation
                IssueNumber = ctx.IssueNumber,
                Tags      = AgentTrailTags.Build(ctx),
                Metadata  = StandardMetadata(),
                Data      = JsonSerializer.Serialize(sanitizer.Redact(data)),
                CreatedAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Agent trail write failed for {Type} agent={AgentId} corr={Corr}",
                type, ctx.AgentId, ctx.CorrelationId);
            await TryWriteFailureBreadcrumbAsync(ctx, type, ex, ct); // best-effort, swallows
        }
    }
}
```

### Query endpoint

The trail read API is a thin projection over `IEventRepository` so isolation is inherited, not re-implemented. Runs = `AGENT.TASK.*` events; trail = all `AGENT.*` / `REVIEW.BUG.*` events for the agent. Both filter by `agentId` (a `Tags` JSONB predicate) within the path tenant, and page on `SequenceNumber`.

```csharp
// src/Tamma.Api/Endpoints/AgentTrailEndpoints.cs

// GET /api/v1/orgs/{tenantId}/agents/{agentId}/runs
public static async Task<IResult> ListRuns(
    HttpContext http, IEventRepository events, ITenantContext tenantContext,
    Guid tenantId, Guid agentId,
    DateTimeOffset? from = null, DateTimeOffset? to = null,
    string? role = null, string? provider = null, string? outcome = null,
    long? cursor = null, int? limit = null)
{
    // tenantId is route-bound and validated by RequireTenantMembershipFilter;
    // the read is physically scoped by IEventRepository to that tenant's schema.
    var take = Math.Min(limit is > 0 ? limit.Value : 50, 500);
    var (rows, total) = await events.QueryAgentTrailAsync(
        tenantId, agentId, typePrefix: "AGENT.TASK",
        from, to, role, provider, outcome, cursor, take);

    var items = rows.Select(ToRunDto).ToList();
    var nextCursor = rows.Count == take ? rows[^1].SequenceNumber : (long?)null;
    return Results.Ok(new { items, total, nextCursor, hasMore = nextCursor is not null });
}
```

`QueryAgentTrailAsync` is a **new** method on `IEventRepository` (mirrors `QueryWithPaginationAsync` but adds the `agentId` Tags predicate + `SequenceNumber` cursor + the trail filters). It throws `NotSupportedException` on a null/empty tenant id — the same hard guard the existing paginated read uses — so AC 4 cannot be bypassed.

```csharp
// src/Tamma.Data/Repositories/IEventRepository.cs  (additive)
Task<(IReadOnlyList<DomainEvent> Events, int Total)> QueryAgentTrailAsync(
    Guid tenantId, Guid agentId, string? typePrefix,
    DateTimeOffset? from, DateTimeOffset? to,
    string? role, string? provider, string? outcome,
    long? cursor, int limit);
```

### Diagnostics linkage (AC 8)

`ProviderDiagnostic` already has `CorrelationId` and `AgentType`. The managed agent run threads one `correlationId` through both the trail emission (`Tags.correlationId`) and every `DiagnosticsService.RecordEventAsync` call in that run, and sets `AgentType` = role. Re-keying by agent for 32-9/32-10 is then a join on `(correlationId, agentId)`. No diagnostics schema change required; if a dedicated `agentId` column is wanted later it is an additive migration, but `correlationId` is sufficient for this story's link-integrity test.

### Route wiring (`Program.cs`)

Both endpoints attach `RequireTenantMembershipFilter` under the existing `/api/v1/orgs/{tenantId}` group (`MemberAccess` policy), exactly like the tenant-scope alert endpoints — read is member-level, there is no mutation surface, and the path-tenant gate plus `IEventRepository` scoping together enforce isolation.

```csharp
orgs.MapGet("/{tenantId}/agents/{agentId}/runs",  AgentTrailEndpoints.ListRuns)
    .AddEndpointFilter<RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId}/agents/{agentId}/trail", AgentTrailEndpoints.ListTrail)
    .AddEndpointFilter<RequireTenantMembershipFilter>();
```

## Tasks / Subtasks

- [ ] Task 1: Trail emission core (AC 1, 2, 3, 6, 7)
  - [ ] Subtask 1.1: Add `AgentTrailContext`, record types (`AgentRunOutcome`, `ToolCallRecord`, `IterationRecord`, `PanelRecord`, `BugRecord`), and `AgentTrailTags.Build` (shared flat-tag builder)
  - [ ] Subtask 1.2: Add `IAgentTrailEmitter` + `AgentTrailEmitter`; wire sanitizer (`SecureAgentProvider` seam) so blob/prompt content is referenced + redacted, never inlined
  - [ ] Subtask 1.3: Enforce non-blocking contract — emit `AGENT.TRAIL.WRITE_FAILED` breadcrumb on terminal write failure; never throw into the run
  - [ ] Subtask 1.4: Register `IAgentTrailEmitter` in `Program.cs` DI
- [ ] Task 2: Wire emission into the managed run + panels (AC 1, 2, 8)
  - [ ] Subtask 2.1: Call `RunCompletedAsync`, `ToolCallAsync`, `IterationCompletedAsync` from the 32-5 `IManagedAgent` execution path (depends on 32-5)
  - [ ] Subtask 2.2: Call `PanelAggregatedAsync` / `BugRecordedAsync` from the 32-7/32-8 panel + review-gate seams (forward-compatible stubs if those land later)
  - [ ] Subtask 2.3: Thread one `correlationId` through trail emission and `DiagnosticsService.RecordEventAsync`; set `AgentType` = role
- [ ] Task 3: Tenant-scoped query API (AC 4, 5)
  - [ ] Subtask 3.1: Add `IEventRepository.QueryAgentTrailAsync` (agentId Tags predicate + `SequenceNumber` cursor + filters; `NotSupportedException` on null tenant)
  - [ ] Subtask 3.2: Add `AgentTrailEndpoints.ListRuns` / `ListTrail`; map under `/api/v1/orgs/{tenantId}` with `RequireTenantMembershipFilter`
  - [ ] Subtask 3.3: Add run/trail DTOs + cursor (`nextCursor`/`hasMore`) wire shape
- [ ] Task 4: Tests (AC 9)
  - [ ] Subtask 4.1: Tenant isolation — tenant B 404 on tenant A's agent trail; platform admin no read path; public-agent run produces only the running tenant's trail
  - [ ] Subtask 4.2: Pagination/cursor — stable order across same-millisecond events, no dup/skip across page boundaries
  - [ ] Subtask 4.3: Redaction — no raw prompt/secret in `Tags`/`Data`; blob refs only
  - [ ] Subtask 4.4: Link integrity — `AGENT.TASK.*` event + `ProviderDiagnostic` share `correlationId` + `agentId`
  - [ ] Subtask 4.5: Non-blocking — induced append failure does not fail the run; breadcrumb emitted

## Dependencies

**Internal Dependencies:**

- **Story 32-1** (Agent entity model & versioned saved config): supplies the immutable `agentId` + `agentVersion` that every trail event is tagged with. The `Agent`/`AgentVersion` entity does **not** exist yet (`apps/tamma-elsa/src/Tamma.Data/Entities/Agent.cs` is NEW in 32-1) — until it lands, `agentId`/`agentVersion` come from the resolved config identity. Hard prerequisite.
- **Story 32-5** (Managed agent execution layer): the `IManagedAgent` run is the producer that calls `IAgentTrailEmitter`. Hard prerequisite for the wiring in Task 2.
- **Story 32-3** (per-tenant provider credential resolution): supplies `credentialSource` (`byok` | `platform`) for the tag. Soft — default to `platform` if 32-3 not yet wired.
- **Story 32-7 / 32-8** (panels / outcome + bug taxonomy): consume `PanelAggregatedAsync` / `BugRecordedAsync`. Forward-compatible; this story ships the emitter API even if those sites are stubbed.
- **Epic 4** (DCB event store): provides `DomainEvent`, `IEventRepository`, and the `SequenceNumber` cursor — all reused, no new event infrastructure.
- **Epic 17/28** (tenant-scoped event store): `IEventRepository` already routes through `ITenantDbContextFactory` / `ITenantContext` per the unified schema-per-tenant model — the trail lands in `t_<hex>` structurally with no new tenancy plumbing.

**External Dependencies:**

- None new. Reuses EF Core 9 / Npgsql, the existing event store, `DiagnosticsService`, and the `SecureAgentProvider` sanitization seam.

## Testing Strategy

1. **Tenant-isolation tests (highest priority — AC 4):** seed agent-trail events for tenant A and tenant B; assert `GET /api/v1/orgs/{B}/agents/{agentId}/trail` returns only B's rows and never A's; assert a member of A hitting B's path is rejected by `RequireTenantMembershipFilter` (403/404); assert there is no `IEventRepository` code path that returns another tenant's events (null-tenant paginated read throws `NotSupportedException`); assert a run of a public/system agent by tenant A leaves a trail only in A's schema, none on the CP and none visible to a platform owner.
2. **Pagination/cursor tests (AC 5):** insert >page-size events including several sharing the same `CreatedAt` millisecond; page with `SequenceNumber` cursor; assert total-order stability, exact page sizes, `nextCursor`/`hasMore` correctness, and zero duplicate/skipped rows at boundaries.
3. **Redaction tests (AC 6):** emit a run whose prompt/tool args contain secret-shaped content; assert `Tags` and `Data` contain only `promptRef`/`blobRef` and sanitized values — no raw prompt body or secret material persisted.
4. **Link-integrity tests (AC 8):** run a managed agent that records both a trail event and `ProviderDiagnostic` rows; assert they share `correlationId` and resolve to the same `agentId`; assert re-keying diagnostics by agent yields the expected cost/latency rollup.
5. **Non-blocking tests (AC 7):** inject an `IEventRepository.AppendAsync` failure; assert the agent run still completes (no exception propagates), and an `AGENT.TRAIL.WRITE_FAILED` breadcrumb / WARN log + metric is produced.
6. **Tag-completeness tests (AC 3):** every emitted event family carries all required tag keys; `REVIEW.BUG.RECORDED` carries a valid `bugType`.

Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/`; docker-bound suites run via `sg docker -c "dotnet test ..."`. TDD: write the isolation + cursor tests first.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentTrailEmitter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentTrailEmitter.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentTrailTags.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentTrailContext.cs` | Create (records: context + run/tool/iteration/panel/bug) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentTrailEventTypes.cs` | Create (the `AGENT.*` / `REVIEW.BUG.RECORDED` type constants) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentTrailEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/AgentTrailDtos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` | Modify (add `QueryAgentTrailAsync`) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs` | Modify (implement `QueryAgentTrailAsync`; null-tenant guard) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI for `IAgentTrailEmitter`; map `/orgs/{tenantId}/agents/{agentId}/runs` + `/trail`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs` | Modify (assert/thread `correlationId` + `AgentType` on agent-run diagnostics) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentTrailEmitterTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentTrailIsolationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentTrailEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentTrailDiagnosticsLinkTests.cs` | Create |

> Note: `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` and `ProviderDiagnostic.cs` are **referenced, not modified** — the trail reuses the existing DCB row shape and diagnostics entity. (`packages/api` is deleted; all work is in the C# `apps/tamma-elsa` stack.)

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions — especially `.dev/decisions/story-28-1-design-calls.md` (Decision #2: cross-tenant read routing, which this story depends on for isolation)
3. Reviewed the Epic 32 design spec `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (the tenancy rule: definition ownership ≠ data ownership; action data is ALWAYS tenant-scoped)
4. Read `EventRepository.cs` to understand exactly how tenant scoping and the `NotSupportedException` guards work — they ARE the isolation guarantee
5. Planned TDD approach (Red-Green-Refactor cycle) — isolation + cursor tests first

### Key Design Decisions

- **No new table.** The trail is DCB events in the tenant's `domain_events` stream. Isolation is structural (schema-per-tenant + `ITenantDbContextFactory`), not enforced by an app-level filter that could be bypassed by a future bug. This is the cheapest correct design and it inherits Epic 4/28 guarantees.
- **`SequenceNumber` is the cursor, never `Id` or `CreatedAt`.** `CreatedAt` has millisecond collisions; `SequenceNumber` is a server-side `BIGSERIAL` total order. The existing `QueryWithPaginationAsync` already tie-breaks on it — match that.
- **Tags carry identity; Data carries metrics + refs.** Per the DCB convention (`AGENT_CONFIG.UPDATED.SUCCESS` precedent in `AgentEndpoints.cs`), `Tags` are flat strings used for cross-aggregate queries (`agentId`, `provider`, …); `Data` is the richer payload, always blob-referenced for large/sensitive content.
- **Emitter is the single seam.** 32-5/32-7/32-8 never build a `DomainEvent` by hand — they call `IAgentTrailEmitter`. One place enforces tag completeness, redaction, and the non-blocking contract.
- **Platform admin has no read path by construction.** There is no `OwnerAccess`/`PlatformOwnerAccess` route for a tenant's trail, and `IEventRepository` cannot return cross-tenant tenant-scoped events. A platform owner who owns a public agent definition sees zero of any tenant's runs of it — exactly the Epic 32 tenancy rule.

### Integration Points

- **32-5 managed run** is the primary producer; the emitter is injected into the run loop.
- **`DiagnosticsService`** is the cost/latency producer; trail ↔ diagnostics link is `correlationId` + `agentId`. 32-9/32-10 re-key off this.
- **`SecureAgentProvider` / sanitization** is reused for redaction before persistence — no new sanitizer.
- **`RequireTenantMembershipFilter` + `/api/v1/orgs/{tenantId}` group** is the existing tenant path-gate; the trail API rides it like the tenant-scope alert endpoints.

### Risks and Mitigations

| Risk | Severity | Mitigation |
| ---- | --------- | ---------- |
| Cross-tenant leakage of action data | Critical | Reads only via `IEventRepository` (schema-per-tenant scoped); null-tenant paginated read throws; route behind `RequireTenantMembershipFilter`; explicit isolation tests including platform-admin-denied |
| Trail write failure aborts a real agent run | High | Emitter swallows + retries + breadcrumbs; non-blocking test induces failure and asserts run completes |
| Sensitive prompt/tool content persisted into events | High | `promptRef`/`blobRef` only + `SecureAgentProvider` redaction before persistence; redaction test |
| Pagination dup/skip on same-millisecond bursts | Medium | `SequenceNumber` cursor (not `CreatedAt`); boundary test |
| `agentId`/`agentVersion` unavailable before 32-1 lands | Medium | Source from resolved config identity until the entity ships; treat 32-1 as a hard prerequisite |

### Success Metrics

- [ ] 100% of managed agent runs produce a terminal `AGENT.TASK.*` event in the correct tenant schema
- [ ] 0 cross-tenant trail reads possible (verified by isolation test suite)
- [ ] Every trail event carries all required tags; `REVIEW.BUG.RECORDED` carries a valid `bugType`
- [ ] Trail-write failures never fail an agent run (non-blocking test green)

## Logging Requirements

- **INFO**: agent run trail finalized (`agentId`, `correlationId`, outcome, event count); trail query served (`tenantId`, `agentId`, page size, total)
- **DEBUG**: individual trail event appended (`type`, `agentId`, `sequenceNumber`); diagnostics row linked (`correlationId`)
- **WARN**: trail write failed/retried (`type`, `agentId`, `correlationId`, error); `AGENT.TRAIL.WRITE_FAILED` breadcrumb emitted
- **ERROR**: terminal trail-write failure after retries (run still completes); query repository failure
- **Structured context**: include `{ tenantId, agentId, agentVersion, correlationId, type, sequenceNumber }` where applicable
- **Credential safety**: NEVER log raw prompts, tool args/results, or secret material — log refs only; redaction is applied before any persistence or log

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
