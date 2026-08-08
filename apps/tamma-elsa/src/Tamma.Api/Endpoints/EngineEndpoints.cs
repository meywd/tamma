using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Globalization;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Engine;
using Tamma.Api.Services.Engine;
using Tamma.Api.Services.Engine.Lifecycle;
using Tamma.Api.Services.Engine.Replay;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Engine callback HTTP surface — the API the deployed Elsa activities POST
/// to as they orchestrate workflows.
///
/// <para>Each handler corresponds to one or more deleted TS routes from
/// <c>packages/api/src/routes/engine/*</c>. The audit findings 001–013,
/// 016–028 in <c>docs/audit/port-gaps/engine/</c> document the remediation
/// status per endpoint.</para>
/// </summary>
public static class EngineEndpoints
{
    // ─── Engine lifecycle (state / events / stats / plan / history) ─
    // Story 43-12 — SendCommand DELETED (was a 200 "Command accepted" no-op; see the
    // route deletion in Program.cs and the story's engine.command resolution).

    public static async Task<IResult> GetState(IEventRepository eventRepo, ITenantContext tc)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, null, null, 10);
        return Results.Ok(new { state = "idle", events = events.Count });
    }

    public static async Task<IResult> GetStats(IEventRepository eventRepo, ITenantContext tc)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, null, null, 1000);
        return Results.Ok(new { totalEvents = events.Count, timestamp = DateTime.UtcNow });
    }

    public static Task<IResult> GetPlan() =>
        Task.FromResult(Results.Ok(new { plan = (object?)null, message = "No active plan" }));

    /// <summary>
    /// Story 4-7 (event query API for time-travel). Tenant-scoped paginated
    /// event read for the dashboard "history" timeline and for ad-hoc
    /// time-travel debugging.
    ///
    /// <para>Query parameters:
    /// <list type="bullet">
    ///   <item><c>limit</c> — page size, clamped to 1..200, default 50.</item>
    ///   <item><c>offset</c> — zero-based offset, default 0, negative
    ///     values clamped to 0.</item>
    ///   <item><c>eventType</c> — optional exact match on
    ///     <see cref="DomainEvent.Type"/>.</item>
    ///   <item><c>issueNumber</c> — optional issue-scoped filter (matches
    ///     <see cref="DomainEvent.IssueNumber"/>).</item>
    /// </list>
    /// Tenant scoping is implicit: the repo call is bound to
    /// <see cref="ITenantContext.TenantId"/> resolved by
    /// <c>TenantContextMiddleware</c>. Cross-tenant reads are not exposed
    /// here — that's the admin surface.</para>
    ///
    /// <para>Response shape: <c>{ events, total, limit, offset, hasMore,
    /// nextOffset? }</c>. <c>hasMore</c> is the canonical signal a UI uses
    /// to render a "next page" control; <c>nextOffset</c> is included only
    /// when <c>hasMore</c> is true so callers don't accidentally page off
    /// the end.</para>
    /// </summary>
    public static async Task<IResult> GetHistory(
        IEventRepository eventRepo,
        ITenantContext tc,
        int? limit,
        int? offset,
        string? eventType,
        int? issueNumber)
    {
        var clampedLimit = Math.Clamp(limit ?? 50, 1, 200);
        var clampedOffset = Math.Max(offset ?? 0, 0);

        // No tenant bound (anonymous in dev-permissive mode, or a JWT
        // missing the active_tenant_id claim): the endpoint is tenant-
        // scoped by design — return an empty page rather than crashing
        // the per-tenant DbContext factory with Guid.Empty.
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
        {
            return Results.Ok(new
            {
                events = Array.Empty<object>(),
                total = 0,
                limit = clampedLimit,
                offset = clampedOffset,
                hasMore = false,
                nextOffset = (int?)null,
            });
        }

        var (rows, total) = await eventRepo.QueryWithPaginationAsync(
            tenantId,
            string.IsNullOrWhiteSpace(eventType) ? null : eventType,
            issueNumber,
            clampedLimit,
            clampedOffset);

        var hasMore = clampedOffset + rows.Count < total;
        return Results.Ok(new
        {
            events = rows.Select(e => new
            {
                e.Id,
                e.Type,
                e.Data,
                e.CreatedAt,
                e.IssueNumber,
                e.SequenceNumber,
            }),
            total,
            limit = clampedLimit,
            offset = clampedOffset,
            hasMore,
            nextOffset = hasMore ? (int?)(clampedOffset + rows.Count) : null,
        });
    }

    /// <summary>
    /// Story 4-7 (event query API for time-travel) — the tenant-scoped,
    /// keyset-paginated query surface over the <c>domain_events</c> DCB stream.
    /// Where <see cref="GetHistory"/> is the simple offset-paged dashboard
    /// timeline (exact type + issue), this is the richer time-travel query:
    /// filter by a time window, correlation id, actor, and event type
    /// (exact OR prefix), paginate with a stable keyset cursor.
    ///
    /// <para>Query parameters (all optional):
    /// <list type="bullet">
    ///   <item><c>type</c> — event type. Exact match unless <c>prefix=true</c>,
    ///     then <c>LIKE 'type%'</c> (e.g. <c>type=AGENT.TASK&amp;prefix=true</c>
    ///     matches every <c>AGENT.TASK.*</c>).</item>
    ///   <item><c>correlationId</c> — the run / workflow-instance correlation id
    ///     (matches <c>Tags.correlationId</c>).</item>
    ///   <item><c>actor</c> — the acting principal (matches the DCB
    ///     <c>Tags.userId</c> convention).</item>
    ///   <item><c>from</c>/<c>to</c> — ISO-8601 half-open time window on the
    ///     event timestamp: <c>from &lt;= t &lt; to</c>.</item>
    ///   <item><c>cursor</c> — last <c>sequenceNumber</c> seen; the next page is
    ///     strictly older. Must be a positive integer.</item>
    ///   <item><c>limit</c> — page size, clamped to 1..200, default 50.</item>
    ///   <item><c>includeTotal</c> — when <c>true</c>, also compute the exact
    ///     match count (an unbounded scan; off by default — pagination uses the
    ///     cursor and <c>total</c> is <c>null</c> = "not computed").</item>
    /// </list></para>
    ///
    /// <para><b>Fail-loud.</b> An inverted time window (<c>from &gt; to</c>) or a
    /// non-positive <c>cursor</c> returns <c>400</c> rather than silently running
    /// a full scan. Non-numeric <c>cursor</c>/<c>from</c>/<c>to</c> are rejected by
    /// model binding (also <c>400</c>).</para>
    ///
    /// <para><b>Tenant-scoped.</b> The read is bound to
    /// <see cref="ITenantContext.TenantId"/>; a request with no resolved tenant
    /// gets an empty page (never another tenant's events). Cross-tenant / platform
    /// scope is NOT exposed here — that stays on the admin surface.</para>
    ///
    /// <para>Point-in-time state RECONSTRUCTION (replaying the filtered slice into
    /// a materialized snapshot) is deferred to Story 4-8 (#191); this endpoint is
    /// the query/filter half only.</para>
    /// </summary>
    public static async Task<IResult> QueryEvents(
        IEventRepository eventRepo,
        ITenantContext tc,
        string? type,
        bool? prefix,
        string? correlationId,
        string? actor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        long? cursor,
        int? limit,
        bool? includeTotal)
    {
        // Fail-loud on bad input rather than a silent full-scan.
        if (from is { } f && to is { } t && f > t)
        {
            return Results.BadRequest(new
            {
                error = "invalid time range: 'from' must be less than or equal to 'to'",
            });
        }
        if (cursor is { } c && c < 1)
        {
            return Results.BadRequest(new
            {
                error = "invalid cursor: must be a positive sequenceNumber returned by a prior page",
            });
        }

        var take = Math.Clamp(limit ?? 50, 1, 200);

        // No tenant bound (anonymous in dev-permissive mode, or a JWT missing the
        // active_tenant_id claim): the endpoint is tenant-scoped by design — return
        // an empty page rather than crashing the per-tenant DbContext factory with
        // Guid.Empty (and never leaking cross-tenant rows).
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
        {
            return Results.Ok(new
            {
                events = Array.Empty<object>(),
                total = (int?)null,
                limit = take,
                nextCursor = (long?)null,
                hasMore = false,
            });
        }

        var (rows, total) = await eventRepo.QueryEventsAsync(
            tenantId,
            type: string.IsNullOrWhiteSpace(type) ? null : type,
            typeIsPrefix: prefix ?? false,
            correlationId: string.IsNullOrWhiteSpace(correlationId) ? null : correlationId,
            actor: string.IsNullOrWhiteSpace(actor) ? null : actor,
            from: from,
            to: to,
            cursor: cursor,
            limit: take,
            includeTotal: includeTotal ?? false);

        // A full page implies there may be more; the cursor is the last (oldest)
        // sequence number on this page. A short page is the end of the stream.
        var nextCursor = rows.Count == take && rows.Count > 0
            ? rows[^1].SequenceNumber
            : (long?)null;

        return Results.Ok(new
        {
            events = rows.Select(e => new
            {
                e.Id,
                e.Type,
                tags = SafeParseJson(e.Tags),
                data = SafeParseJson(e.Data),
                e.CreatedAt,
                e.IssueNumber,
                e.SequenceNumber,
            }),
            total,
            limit = take,
            nextCursor,
            hasMore = nextCursor is not null,
        });
    }

    /// <summary>
    /// Story 4-8 (black-box replay for debugging) — the RECONSTRUCTION half Story
    /// 4-7 deferred. Reconstructs a run's point-in-time state by folding its ordered
    /// DCB event slice (from Story 4-7's
    /// <see cref="IEventRepository.ListByCorrelationIdAsync"/>) into a read-only
    /// <see cref="ReplayResult"/> — a pure, deterministic left-fold over recorded
    /// events. It re-executes nothing and mutates nothing (no Elsa runtime, no
    /// writes): time-travel for debugging, not re-run.
    ///
    /// <para>Route: <c>GET /api/engine/runs/{correlationId}/replay?upTo={seq|timestamp}&amp;from={seq}</c>.</para>
    ///
    /// <para>Query parameters:
    /// <list type="bullet">
    ///   <item><c>upTo</c> — the point-in-time marker. Either a positive
    ///     <c>SequenceNumber</c> (replay up to and including that event) OR an
    ///     ISO-8601 timestamp (replay up to and including that instant). Omitted =
    ///     replay the whole run. A value that is neither → <c>400</c>; a
    ///     non-positive sequence → <c>400</c>.</item>
    ///   <item><c>from</c> — optional positive <c>SequenceNumber</c>; when supplied
    ///     the result carries a <see cref="ReplayDelta"/> diff of everything after
    ///     that point up to <c>upTo</c> (AC6). A non-positive value → <c>400</c>.</item>
    /// </list></para>
    ///
    /// <para><b>Tenant-scoped, null-tenant fail-closed.</b> The read is bound to
    /// <see cref="ITenantContext.TenantId"/>; a request with no resolved tenant is a
    /// <c>404</c> (the run is not visible) — never another tenant's run. A run whose
    /// correlationId this tenant does not own returns no events → <c>404</c>. So a
    /// tenant can only replay THEIR OWN run (no IDOR).</para>
    ///
    /// <para>Point-in-time semantics: an <c>upTo</c> before the run began returns a
    /// known-but-empty state (<c>200</c>, <c>eventsReplayed = 0</c>); an <c>upTo</c>
    /// beyond the last event returns the full state. Determinism: the same slice
    /// always folds to the same result.</para>
    /// </summary>
    public static async Task<IResult> ReplayRun(
        string correlationId,
        IReplayService replay,
        ITenantContext tc,
        string? upTo,
        string? from)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return Results.BadRequest(new { error = "correlationId is required" });
        }

        // Parse upTo — a positive sequenceNumber OR an ISO-8601 timestamp. Fail loud
        // on anything else rather than silently replaying the whole run.
        long? upToSeq = null;
        DateTimeOffset? upToTs = null;
        if (!string.IsNullOrWhiteSpace(upTo))
        {
            if (long.TryParse(upTo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
            {
                if (seq < 1)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid upTo: a sequenceNumber must be a positive integer",
                    });
                }
                upToSeq = seq;
            }
            // Parse with AssumeUniversal | AdjustToUniversal (matching
            // AgentDispatchEndpoints.ParseCreatedAfterUtc): an offset-less ISO string is
            // PINNED to UTC (not treated as server-local then shifted by .UtcDateTime —
            // masked on the UTC VPS/CI), and an explicit offset is CONVERTED to UTC. The
            // downstream fold compares against the UTC-kind CreatedAt, so the boundary is
            // the same instant on every host (the recurring TZ lesson).
            else if (DateTimeOffset.TryParse(
                         upTo, CultureInfo.InvariantCulture,
                         DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
            {
                upToTs = ts;
            }
            else
            {
                return Results.BadRequest(new
                {
                    error = "invalid upTo: expected a positive sequenceNumber or an ISO-8601 timestamp",
                });
            }
        }

        long? fromSeq = null;
        if (!string.IsNullOrWhiteSpace(from))
        {
            if (!long.TryParse(from, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) || f < 1)
            {
                return Results.BadRequest(new
                {
                    error = "invalid from: a sequenceNumber must be a positive integer",
                });
            }
            fromSeq = f;
        }

        // Null-tenant fail-closed: no resolved tenant → the run is not visible to the
        // caller → 404. Never a cross-tenant read (mirrors 4-7 + the #283 fix).
        if (tc.TenantId is not Guid tenantId || tenantId == Guid.Empty)
        {
            return Results.NotFound(new { error = "run not found", correlationId });
        }

        ReplayResult? result;
        try
        {
            result = await replay.ReplayAsync(tenantId, correlationId, upToSeq, upToTs, fromSeq);
        }
        catch (ReplayRangeException ex)
        {
            // `from` resolved to a point AFTER `upTo` — the delta would be a
            // meaningless empty diff (newer ⊂ older). Fail loud rather than 200.
            return Results.BadRequest(new { error = ex.Message });
        }

        if (result is null)
        {
            return Results.NotFound(new { error = "run not found", correlationId });
        }

        return Results.Ok(result);
    }

    /// <summary>
    /// Audit finding 012: streams engine / workflow / task-queue lifecycle
    /// events as continuous Server-Sent Events, backed by
    /// <see cref="IEngineLifecycleBus"/>. Publishers (workflow domain-event
    /// writes, engine registry heartbeats, task-queue processor) push
    /// frames into the bus; this endpoint fans them out to all live
    /// dashboard <c>EventSource</c> clients filtered by the caller's
    /// tenant.
    ///
    /// <para>An immediate snapshot frame (<c>event: state</c>) is written
    /// on connect so a just-opened dashboard tile paints without waiting
    /// for the next publisher signal. A keep-alive comment frame
    /// (<c>:heartbeat</c>) is written every
    /// <see cref="EngineLifecycleOptions.HeartbeatInterval"/> while idle
    /// so reverse proxies and client socket timers don't tear the
    /// connection down.</para>
    ///
    /// <para>Tenant scoping mirrors finding 016: the bus filter rejects
    /// events whose <c>TenantId</c> doesn't match the resolved request
    /// tenant. Unauthenticated requests are rejected by the
    /// <c>WorkflowsView</c> policy before this handler ever runs.</para>
    /// </summary>
    public static async Task<IResult> GetEventsState(
        HttpContext ctx,
        HttpResponse response,
        IEngineLifecycleBus bus,
        IEventRepository eventRepo,
        ITenantContext tc,
        IOptions<EngineLifecycleOptions> opts,
        CancellationToken ct,
        int? limit)
    {
        var tenantId = tc.TenantId ?? Guid.Empty;

        SseWriter.WriteHeaders(response);

        // Force the HTTP headers to flush before any heavier work so
        // clients that requested <c>ResponseHeadersRead</c> (dashboards +
        // tests) don't block waiting for first body bytes when the
        // initial snapshot query returns empty.
        await SseWriter.WriteCommentAsync(response, "open", ct).ConfigureAwait(false);

        // Initial snapshot — recent events give the client an instant paint
        // even when no live events have fired since connect.
        var seed = await eventRepo.QueryAsync(tc.TenantId, null, null, limit ?? 20);
        await SseWriter.WriteEventAsync(response, "state",
            new { events = seed.Select(e => new { e.Id, e.Type, e.CreatedAt }) },
            ct).ConfigureAwait(false);

        await StreamLifecycleAsync(
            ctx, response, bus, tenantId,
            filter: null, // state stream surfaces every frame
            opts.Value.HeartbeatInterval, ct).ConfigureAwait(false);

        return Results.Empty;
    }

    /// <summary>
    /// Audit finding 012 — logs variant. Streams the raw event-store rows
    /// as they arrive via <see cref="IEngineLifecycleBus"/> workflow /
    /// task publishers, plus an initial backlog snapshot. Heartbeat and
    /// tenant-scoping are identical to state.
    /// </summary>
    public static async Task<IResult> GetEventsLogs(
        HttpContext ctx,
        HttpResponse response,
        IEngineLifecycleBus bus,
        IEventRepository eventRepo,
        ITenantContext tc,
        IOptions<EngineLifecycleOptions> opts,
        CancellationToken ct,
        int? limit)
    {
        var tenantId = tc.TenantId ?? Guid.Empty;

        SseWriter.WriteHeaders(response);

        // Force early header flush (see state endpoint for rationale).
        await SseWriter.WriteCommentAsync(response, "open", ct).ConfigureAwait(false);

        // Initial backlog so the logs panel is not blank on connect.
        var seed = await eventRepo.QueryAsync(tc.TenantId, null, null, limit ?? 50);
        foreach (var e in seed)
        {
            await SseWriter.WriteEventAsync(response, "log",
                new { id = e.Id, type = e.Type, data = SafeParseJson(e.Data), createdAt = e.CreatedAt },
                ct).ConfigureAwait(false);
        }

        await StreamLifecycleAsync(
            ctx, response, bus, tenantId,
            // The logs stream only surfaces workflow / task events (not
            // engine registry heartbeats), so the log tile isn't flooded
            // with heartbeat noise.
            filter: evt => evt.Type.StartsWith("workflow.", StringComparison.Ordinal)
                        || evt.Type.StartsWith("task.", StringComparison.Ordinal),
            opts.Value.HeartbeatInterval, ct).ConfigureAwait(false);

        return Results.Empty;
    }

    /// <summary>
    /// Shared SSE loop: pumps bus events to the response while a separate
    /// heartbeat timer writes keep-alive comment frames. Exits when the
    /// client disconnects (cancellation) or the bus subscription completes.
    /// </summary>
    private static async Task StreamLifecycleAsync(
        HttpContext ctx,
        HttpResponse response,
        IEngineLifecycleBus bus,
        Guid tenantId,
        Func<EngineLifecycleEvent, bool>? filter,
        TimeSpan heartbeatInterval,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, ctx.RequestAborted);
        var linked = cts.Token;

        // Heartbeat timer loop — writes per-subscriber keep-alive frames
        // directly to this response rather than publishing through the bus
        // (which would fan the same heartbeat out to every subscriber).
        var heartbeatTask = HeartbeatLoopAsync(response, heartbeatInterval, linked);

        // Event pump loop — drains the bus subscription into the response.
        var eventsTask = EventLoopAsync(bus, tenantId, response, filter, linked);

        // First one to finish (either because the socket closed, the loop
        // threw, or cancellation fired) cancels the other.
        try
        {
            await Task.WhenAny(heartbeatTask, eventsTask).ConfigureAwait(false);
        }
        finally
        {
            cts.Cancel();
            // Swallow benign exceptions from the cancelled sibling. A
            // genuine failure will have already bubbled through WhenAny.
            try { await Task.WhenAll(heartbeatTask, eventsTask).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { /* peer disconnect */ }
            catch (ObjectDisposedException) { /* response body torn down */ }
        }
    }

    private static async Task HeartbeatLoopAsync(
        HttpResponse response, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await SseWriter.WriteCommentAsync(response, "heartbeat", ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on disconnect */ }
    }

    private static async Task EventLoopAsync(
        IEngineLifecycleBus bus,
        Guid tenantId,
        HttpResponse response,
        Func<EngineLifecycleEvent, bool>? filter,
        CancellationToken ct)
    {
        try
        {
            await foreach (var evt in bus.SubscribeAsync(tenantId, ct).ConfigureAwait(false))
            {
                if (filter is not null && !filter(evt)) continue;

                await SseWriter.WriteEventAsync(response, evt.Type,
                    new
                    {
                        type = evt.Type,
                        tenantId = evt.TenantId,
                        timestamp = evt.Timestamp,
                        payload = evt.Payload
                    }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on disconnect */ }
    }

    // ─── Context endpoints (store / get / query) — finding 004 ────────────────

    public static async Task<IResult> StoreContext(
        StoreContextRequest req,
        IEventRepository eventRepo,
        IContextStore contextStore,
        ITenantContext tc)
    {
        if (string.IsNullOrWhiteSpace(req.Repository))
            return Results.BadRequest(new { error = "repository is required" });

        // Two payload shapes the deployed Elsa activities send:
        //   StoreFindingsActivity     → {repository, issueNumber, findings: {...}}
        //   StoreRoleFindingActivity  → {repository, issueNumber, role, finding}
        // Normalise to a single {role: content} object.
        JsonElement findingsToStore;
        if (req.Findings is JsonElement f && f.ValueKind != JsonValueKind.Undefined)
        {
            findingsToStore = f;
        }
        else if (!string.IsNullOrEmpty(req.Role) &&
                 req.Finding is JsonElement single && single.ValueKind != JsonValueKind.Undefined)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WritePropertyName(req.Role);
                single.WriteTo(writer);
                writer.WriteEndObject();
            }
            using var doc = JsonDocument.Parse(ms.ToArray());
            findingsToStore = doc.RootElement.Clone();
        }
        else
        {
            return Results.BadRequest(new { error = "findings or {role, finding} required" });
        }

        await contextStore.StoreAsync(req.Repository, req.IssueNumber, findingsToStore);

        await eventRepo.AppendAsync(new DomainEvent
        {
            Type = "CONTEXT.STORED",
            TenantId = tc.TenantId,
            IssueNumber = req.IssueNumber,
            Data = JsonSerializer.Serialize(new
            {
                repository = req.Repository,
                issueNumber = req.IssueNumber,
                role = req.Role,
                hasFindings = true
            })
        });

        return Results.Ok(new
        {
            ok = true,
            repository = req.Repository,
            issueNumber = req.IssueNumber,
            storedAt = DateTime.UtcNow
        });
    }

    public static async Task<IResult> GetContext(
        int issueNumber,
        IContextStore contextStore,
        [FromQuery] string? repository = null)
    {
        var entry = await contextStore.GetAsync(repository, issueNumber);
        if (entry is null)
            return Results.NotFound(new { error = "No context found" });

        return Results.Ok(new
        {
            repository = entry.Repository,
            issueNumber = entry.IssueNumber,
            findings = entry.Findings,
            storedAt = entry.StoredAt
        });
    }

    public static async Task<IResult> QueryContext(
        QueryContextRequest req,
        IContextStore contextStore)
    {
        if (string.IsNullOrWhiteSpace(req.Query))
            return Results.BadRequest(new { error = "query is required" });

        var (chunks, totalTokens) = await contextStore.QueryAsync(
            req.Repository, req.IssueNumber, req.Query, req.Role, req.MaxTokens);

        return Results.Ok(new
        {
            query = req.Query,
            chunks = chunks.Select(c => new { content = c.Content, role = c.Role, score = c.Score }),
            totalTokens
        });
    }

    // ─── Platform-proxy endpoints (findings 005-011; Epic 31 P3 seam 5) ──────
    // Rerouted off the GitHub-only IGitHubEngineCallbackService onto the
    // platform-agnostic IEngineGitCallbackService (IPlatformResolver →
    // driver.Client) with the installation-based tenant lookup they lacked.
    // Route paths + response shapes unchanged (EngineCallbackContractTests).

    public static async Task<IResult> GetRepoConfig(
        IEngineGitCallbackService platform,
        ITenantContext tc,
        [FromQuery] string? repo,
        [FromQuery] string? branch)
    {
        if (string.IsNullOrEmpty(repo))
            return Results.BadRequest(new { error = "Missing required query parameter: repo" });

        var (owner, name) = ParseOwnerRepo(repo);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = $"Invalid repo format: \"{repo}\". Expected \"owner/repo\"." });

        var result = await platform.ReadRepoConfigAsync(tc.TenantId, owner, name, branch ?? "main");
        if (result.ServiceUnavailable)
        {
            // TS contract: graceful degradation — return {} instead of 5xx so
            // the deployed Elsa activity falls through to its empty-conventions
            // path. Keeps workflows running when no platform driver resolves.
            return Results.Ok(JsonDocument.Parse("{}").RootElement);
        }
        return Results.Ok(result.Result);
    }

    public static async Task<IResult> GetIssues(
        IEngineGitCallbackService platform,
        ITenantContext tc,
        [FromQuery] string? repo,
        [FromQuery] string? state,
        [FromQuery] string? labels,
        [FromQuery] int? per_page,
        [FromQuery] int? page)
    {
        if (string.IsNullOrEmpty(repo))
            return Results.BadRequest(new { error = "Missing required query parameter: repo" });

        var (owner, name) = ParseOwnerRepo(repo);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = $"Invalid repo format: \"{repo}\"." });

        var result = await platform.ListIssuesAsync(
            tc.TenantId, owner, name, state ?? "open", labels, per_page ?? 30, page ?? 1);
        return ToHttpResult(result, r => Results.Ok(new { issues = r.Issues, total = r.Total }));
    }

    public static async Task<IResult> GetSecurityAlerts(
        IEngineGitCallbackService platform,
        ITenantContext tc,
        [FromQuery] string? repo,
        [FromQuery] string? type)
    {
        if (string.IsNullOrEmpty(repo))
            return Results.BadRequest(new { error = "Missing required query parameter: repo" });

        var (owner, name) = ParseOwnerRepo(repo);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = $"Invalid repo format: \"{repo}\"." });

        var result = await platform.ListSecurityAlertsAsync(tc.TenantId, owner, name, type ?? "all");
        return ToHttpResult(result, r => Results.Ok(new
        {
            dependabot = r.Dependabot,
            codeScanning = r.CodeScanning
        }));
    }

    public static async Task<IResult> PostIssueComment(
        IssueCommentRequest req,
        IEngineGitCallbackService platform,
        ITenantContext tc)
    {
        if (string.IsNullOrEmpty(req.Repository))
            return Results.BadRequest(new { error = "repository is required" });
        if (string.IsNullOrEmpty(req.Body))
            return Results.BadRequest(new { error = "body is required" });

        var (owner, name) = ParseOwnerRepo(req.Repository);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = "Invalid repo format" });

        var result = await platform.PostIssueCommentAsync(tc.TenantId, owner, name, req.IssueNumber, req.Body);
        return ToHttpResult(result, r => Results.Ok(new { id = r.Id, htmlUrl = r.HtmlUrl }));
    }

    public static async Task<IResult> PostIssueLabels(
        IssueLabelRequest req,
        IEngineGitCallbackService platform,
        ITenantContext tc)
    {
        if (string.IsNullOrEmpty(req.Repository))
            return Results.BadRequest(new { error = "repository is required" });
        if (req.Labels is null || req.Labels.Length == 0)
            return Results.BadRequest(new { error = "labels[] must not be empty" });

        var (owner, name) = ParseOwnerRepo(req.Repository);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = "Invalid repo format" });

        var result = await platform.AddIssueLabelsAsync(tc.TenantId, owner, name, req.IssueNumber, req.Labels);
        return ToHttpResult(result, r => Results.Ok(new { labels = r }));
    }

    public static async Task<IResult> DeleteIssueLabel(
        string repo,
        int issueNumber,
        string label,
        IEngineGitCallbackService platform,
        ITenantContext tc)
    {
        var (owner, name) = ParseOwnerRepo(repo);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = "Invalid repo format" });

        var result = await platform.RemoveIssueLabelAsync(tc.TenantId, owner, name, issueNumber, label);
        return ToHttpResult(result, _ => Results.Ok(new { removed = true, label }));
    }

    public static async Task<IResult> CreateIssue(
        CreateIssueRequest req,
        IEngineGitCallbackService platform,
        ITenantContext tc)
    {
        if (string.IsNullOrEmpty(req.Repository))
            return Results.BadRequest(new { error = "repository is required" });
        if (string.IsNullOrEmpty(req.Title))
            return Results.BadRequest(new { error = "title is required" });

        var (owner, name) = ParseOwnerRepo(req.Repository);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = "Invalid repo format" });

        var result = await platform.CreateIssueAsync(
            tc.TenantId, owner, name, req.Title, req.Body, req.Labels, req.Assignees);
        // Epic 31 P3: the Location header carries the platform's REAL issue
        // URL (drivers populate Issue.HtmlUrl) — the fabricated
        // https://github.com/... URL is gone. Falls back to the API path when
        // a platform returns no browse URL.
        return ToHttpResult(result, r => Results.Created(
            string.IsNullOrWhiteSpace(r.HtmlUrl) ? $"/api/engine/issues/{r.Number}" : r.HtmlUrl,
            new { number = r.Number, htmlUrl = r.HtmlUrl, title = r.Title }));
    }

    /// <summary>
    /// Epic 31 P3 (seam 4) — the engine trigger-ci callback now DELEGATES into
    /// the governed CI-mediation core (<see cref="Services.Ci.ICiMediationService"/>:
    /// guard → resolved driver's Actions surface → one DCB event) instead of the
    /// GitHub-only <c>IGitHubEngineCallbackService</c>. The route, its
    /// <c>Governs</c> key (<c>effect:ci.workflow.dispatch</c>) and the response
    /// SHAPES the deployed activities consume are unchanged and pinned by
    /// <c>EngineTriggerCiContractTests</c>: success ⇒
    /// <c>{dispatched, workflowFile, branch}</c>; no resolvable platform
    /// credential ⇒ the legacy 503 <c>github_client_not_configured</c> envelope;
    /// other failures ⇒ 502 <c>{error}</c> (guard denial ⇒ 403).
    /// </summary>
    public static async Task<IResult> TriggerCi(
        TriggerCiRequest req,
        Services.Ci.ICiMediationService ci,
        ITenantContext tc,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Repository))
            return Results.BadRequest(new { error = "repository is required" });
        if (string.IsNullOrEmpty(req.BranchName))
            return Results.BadRequest(new { error = "branchName is required" });
        if (string.IsNullOrEmpty(req.WorkflowFile))
            return Results.BadRequest(new { error = "workflowFile is required" });

        var (owner, name) = ParseOwnerRepo(req.Repository);
        if (owner is null || name is null)
            return Results.BadRequest(new { error = "Invalid repo format" });

        var result = await ci.TriggerTestsAsync(
            tc.TenantId,
            req.Repository,
            new Services.Ci.TriggerTestsRequest
            {
                Branch = req.BranchName,
                WorkflowFile = req.WorkflowFile,
                Inputs = req.Inputs,
                CorrelationId = $"engine-trigger-ci-{Guid.NewGuid():N}",
            },
            ct);

        if (result.Success)
        {
            return Results.Ok(new
            {
                dispatched = true,
                workflowFile = req.WorkflowFile,
                branch = req.BranchName
            });
        }

        return result.FailureCode switch
        {
            // Legacy contract: "no usable CI credential" surfaced as the 503
            // github_client_not_configured envelope — same shape, now meaning
            // "no platform driver resolved" rather than "no App client wired".
            Services.Ci.CiFailureCodes.TokenUnavailable => Results.Json(new
            {
                error = "github_client_not_configured",
                detail = "no git platform credential is configured for this deployment/tenant"
            }, statusCode: StatusCodes.Status503ServiceUnavailable),
            Services.Ci.CiFailureCodes.RepoNotAuthorized => Results.Json(
                new { error = result.FailureReason ?? "repository not authorized for the acting tenant" },
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Json(
                new { error = result.FailureReason ?? "github_error" },
                statusCode: StatusCodes.Status502BadGateway),
        };
    }

    // ─── Execute task — finding 001 ───────────────────────────────────────────

    /// <summary>
    /// Run an LLM-driven task on behalf of an Elsa activity.
    ///
    /// <para>Audit finding 001 (P0): the previous one-line stub returned
    /// <c>{message, taskType}</c> — none of the deployed activities can
    /// parse that. Restored to TS shape via <see cref="IExecuteTaskService"/>
    /// which delegates to <c>ILlmProxyService</c>. Real role-based agent
    /// resolution + tool loop ports later.</para>
    /// </summary>
    public static async Task<IResult> ExecuteTask(
        ExecuteTaskRequest req,
        IExecuteTaskService taskService,
        ITenantContext tc)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return Results.BadRequest(new { error = "prompt is required" });

        var input = new ExecuteTaskInput(
            Prompt: req.Prompt,
            Role: req.Role,
            AnalysisType: req.AnalysisType,
            Repository: req.Repository,
            EnableTools: req.EnableTools,
            Model: req.Model,
            MaxBudgetUsd: req.MaxBudgetUsd,
            Cwd: req.Cwd);

        var result = await taskService.ExecuteAsync(input, tc.TenantId);

        if (!result.Success)
        {
            // 500 with the documented response shape so the activity can
            // surface the error rather than throw on missing-property access.
            return Results.Json(new
            {
                success = false,
                output = string.Empty,
                tokensUsed = 0,
                costUsd = 0,
                durationMs = result.DurationMs,
                toolCalls = 0,
                error = result.Error
            }, statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(new
        {
            success = true,
            output = result.Output,
            tokensUsed = result.TokensUsed,
            costUsd = result.CostUsd,
            durationMs = result.DurationMs,
            toolCalls = result.ToolCalls
        });
    }

    // ─── Cycle results — finding 003 ──────────────────────────────────────────

    public static async Task<IResult> PostCycleResult(
        CycleResultRequest req, IEventRepository eventRepo, ITenantContext tc)
    {
        if (string.IsNullOrWhiteSpace(req.ExitReason))
            return Results.BadRequest(new { error = "exitReason is required" });

        // Persist all structured fields so the dashboard's failure-classification
        // queries see exitReason / error / durationMs first-class.
        await eventRepo.AppendAsync(new DomainEvent
        {
            Type = "CYCLE.RESULT",
            TenantId = tc.TenantId,
            IssueNumber = req.IssueNumber,
            Data = JsonSerializer.Serialize(new
            {
                exitReason = req.ExitReason,
                issueNumber = req.IssueNumber,
                repository = req.Repository,
                error = req.Error,
                durationMs = req.DurationMs,
                metadata = req.Metadata
            })
        });
        return Results.Created(
            $"/api/engine/cycle-results/{Guid.NewGuid()}",
            new { ok = true, storedAt = DateTime.UtcNow });
    }

    public static async Task<IResult> GetCycleResults(IEventRepository eventRepo, ITenantContext tc, int? limit)
    {
        var events = await eventRepo.QueryAsync(tc.TenantId, "CYCLE.RESULT", null, limit ?? 20);
        return Results.Ok(events.Select(e => new
        {
            e.Id,
            e.IssueNumber,
            data = SafeParseJson(e.Data),
            createdAt = e.CreatedAt
        }));
    }

    // ─── Generic DCB event append — durable engine→domain_events bridge ───────

    /// <summary>
    /// Generic engine event-append callback. Accepts a BATCH of
    /// <see cref="AppendEventsRequest.Events"/> the Elsa engine drained from
    /// its in-process <c>tamma:events</c> transient list and persists each one
    /// into the caller's tenant <c>domain_events</c> via
    /// <see cref="IEventRepository.AppendAsync"/>.
    ///
    /// <para>The engine (<c>Tamma.ElsaServer</c>) cannot reference
    /// <c>Tamma.Api</c> and registers neither <see cref="IEventRepository"/>
    /// nor <c>IPlatformEventPublisher</c>, so workflow activities have no
    /// in-process durable sink — this is the API callback that closes the
    /// audit trail. It mirrors <see cref="PostCycleResult"/>, the one existing
    /// engine→<c>domain_events</c> path.</para>
    ///
    /// <para>Tenant is resolved from <see cref="ITenantContext"/> (the
    /// <c>X-Tenant-Id</c> the engine sends). Partial-batch handling: every
    /// well-formed event that persists is counted; per-event failures are
    /// collected and reported so the engine can retry the whole batch (the
    /// drain cursor only advances on a 2xx). An empty <c>eventType</c> is
    /// rejected per-event, not for the whole batch.</para>
    /// </summary>
    public static async Task<IResult> AppendEvents(
        AppendEventsRequest req, IEventRepository eventRepo, ITenantContext tc)
    {
        if (req.Events is null || req.Events.Count == 0)
            return Results.BadRequest(new { error = "events array is required and must be non-empty" });

        var persisted = 0;
        var failures = new List<object>();

        for (var i = 0; i < req.Events.Count; i++)
        {
            var e = req.Events[i];

            if (string.IsNullOrWhiteSpace(e.EventType))
            {
                failures.Add(new { index = i, error = "eventType is required" });
                continue;
            }

            try
            {
                // Project TammaEvent → DomainEvent. The activity/workflow
                // identifiers + status + duration land in Tags so the audit
                // trail is queryable by workflow instance / activity. Tenant
                // is injected into Tags too (defence-in-depth — the row's
                // TenantId column is the authoritative scope, set by the repo).
                var tags = new Dictionary<string, string?>();
                if (e.Tags is not null)
                {
                    foreach (var kv in e.Tags)
                        tags[kv.Key] = kv.Value;
                }
                if (tc.TenantId is Guid tid && tid != Guid.Empty)
                    tags["tenantId"] = tid.ToString();
                if (!string.IsNullOrEmpty(e.WorkflowInstanceId))
                    tags["workflowInstanceId"] = e.WorkflowInstanceId;
                if (!string.IsNullOrEmpty(e.ActivityId))
                    tags["activityId"] = e.ActivityId;
                if (!string.IsNullOrEmpty(e.ActivityName))
                    tags["activityName"] = e.ActivityName;
                if (!string.IsNullOrEmpty(e.Status))
                    tags["status"] = e.Status;
                if (e.DurationMs is double d)
                    tags["durationMs"] = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (e.Timestamp is DateTime ts)
                    tags["emittedAt"] = ts.ToUniversalTime().ToString("O");

                await eventRepo.AppendAsync(new DomainEvent
                {
                    // Stable id minted by the engine at emit time (carried on
                    // the wire). The idempotent append (ON CONFLICT (Id) DO
                    // NOTHING) makes a retry of an already-persisted event a
                    // no-op, so a partial-batch failure + full-batch retry can
                    // never duplicate audit rows (C2). Guard against a missing
                    // id (older engine) by minting one server-side.
                    Id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id,
                    Type = e.EventType,
                    TenantId = tc.TenantId,
                    IssueNumber = e.IssueNumber,
                    Tags = JsonSerializer.Serialize(tags),
                    Metadata = JsonSerializer.Serialize(new
                    {
                        workflowVersion = "1.0.0",
                        eventSource = "system",
                        error = e.Error,
                    }),
                    Data = e.Data is JsonElement data && data.ValueKind != JsonValueKind.Undefined
                        ? data.GetRawText()
                        : "{}",
                    // CreatedAt is stamped server-side by the repository for a
                    // monotonic store clock; the engine timestamp is preserved
                    // in Tags so time-travel can reconstruct emit order.
                });

                persisted++;
            }
            catch (Exception ex)
            {
                // Per-event failure — collect and continue so a single bad
                // row doesn't lose the rest of the batch. The engine retries
                // the WHOLE batch on a non-2xx (the drain cursor stays put),
                // which re-sends the events that DID persist on this call.
                // That re-send is safe ONLY because AppendAsync is idempotent
                // on the stable per-event Id (ON CONFLICT DO NOTHING) — append-
                // only WITHOUT an idempotency key is exactly what would
                // duplicate those rows (C2).
                failures.Add(new { index = i, error = ex.GetType().Name });
            }
        }

        if (failures.Count > 0)
        {
            // Typed partial-failure error. 207-style semantics over a 502 so
            // the engine drain treats the batch as not-fully-persisted and
            // retries (cursor unchanged) — see EventPersistenceActivityMiddleware.
            return Results.Json(new
            {
                error = "partial_append_failure",
                persisted,
                failed = failures.Count,
                failures,
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Created(
            "/api/engine/events",
            new { ok = true, persisted, storedAt = DateTime.UtcNow });
    }

    // ─── Platform-events (control-plane) append ────────────────────────────────

    /// <summary>
    /// Engine→<c>platform_events</c> DCB-event callback. Persists a batch of
    /// cross-tenant lifecycle / analytics events to the control-plane store and
    /// fans them out to in-process subscribers via
    /// <see cref="IPlatformEventPublisher"/>.
    ///
    /// <para>This mirrors <see cref="AppendEvents"/> (which targets per-tenant
    /// <c>domain_events</c>). The tenant is nullable and carried in the body
    /// because platform events are cross-tenant — some fire before/after a
    /// tenant DB exists (e.g. <c>TENANT.DELETED.*</c>, <c>ORCHESTRATOR.TICK.*</c>).</para>
    ///
    /// <para>Partial-batch semantics: per-event failures are collected; any
    /// per-event failure → 502; full success → 201. A dedup no-op
    /// (<c>AppendAndPublishAsync</c> returns null) counts as success.
    /// PK-level dedup applies only when the caller sends a stable non-empty
    /// <c>Id</c>; in production all 11 lifecycle emitters go through
    /// <c>TenantLifecycleEvents.BuildEvent</c> (never sets <c>Id</c> →
    /// <c>Guid.Empty</c> → the server mints a fresh Id per POST), and the 2
    /// analytics emitters use <c>Guid.NewGuid()</c> per build — so PK-dedup is
    /// effectively dormant. The real cross-retry guard is the partial unique
    /// index on <c>(tenant_id, type, tags-&gt;&gt;'step', tags-&gt;&gt;'attempt')
    /// WHERE type LIKE 'TENANT.PROVISION.STEP_%'</c>, which does survive
    /// round-trips. <c>DELETE.STEP_*</c>, terminal, and analytics events are
    /// not index-covered and can duplicate on a lost-success retry.</para>
    /// </summary>
    public static async Task<IResult> AppendPlatformEvents(
        AppendPlatformEventsRequest req,
        IPlatformEventPublisher publisher)
    {
        if (req?.Events is null || req.Events.Count == 0)
            return Results.BadRequest(new { error = "events array is required and must be non-empty" });

        var persisted = 0;
        var failures = new List<object>();

        for (var i = 0; i < req.Events.Count; i++)
        {
            var e = req.Events[i];

            if (string.IsNullOrWhiteSpace(e.Type))
            {
                failures.Add(new { id = e.Id, error = "empty_type" });
                continue;
            }

            try
            {
                var evt = new PlatformEvent
                {
                    Id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id,
                    Type = e.Type,
                    TenantId = e.TenantId,
                    UserId = e.UserId,
                    Tags = e.Tags is null ? "{}" : JsonSerializer.Serialize(e.Tags),
                    Metadata = e.Metadata is JsonElement md && md.ValueKind != JsonValueKind.Undefined
                        ? md.GetRawText()
                        : "{}",
                    Data = e.Data is JsonElement d && d.ValueKind != JsonValueKind.Undefined
                        ? d.GetRawText()
                        : "{}",
                    CreatedAt = e.CreatedAt ?? DateTime.UtcNow,
                };

                // null result = idempotent dedup no-op = success (already persisted).
                await publisher.AppendAndPublishAsync(evt);
                persisted++;
            }
            catch (Exception ex)
            {
                // Per-event failure — collect and continue so a single bad row
                // doesn't lose the rest of the batch. A full-batch failure returns
                // 502. PK-dedup (ON CONFLICT DO NOTHING) applies only when the
                // caller sends a stable non-empty Id; current emitters send
                // Guid.Empty, so the server mints a fresh Id per call — dedup
                // is dormant for all but TENANT.PROVISION.STEP_* (which is
                // covered by the partial unique index on step+attempt tags).
                failures.Add(new { id = e.Id, type = e.Type, error = ex.Message });
            }
        }

        if (failures.Count > 0)
        {
            return Results.Json(new
            {
                error = "partial_append_failure",
                persisted,
                failed = failures.Count,
                failures,
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Created("/api/engine/platform-events", new { ok = true, persisted });
    }

    // ─── Agent availability — finding 002 ─────────────────────────────────────

    /// <summary>
    /// Audit finding 002 — converted from POST-with-body (the old
    /// engine-registration mis-port) to the TS contract: a parameter-free
    /// GET that returns <c>{available: bool}</c>.
    /// </summary>
    public static IResult AgentAvailable(IConfiguration config)
    {
        var available = !string.IsNullOrWhiteSpace(config["Anthropic:ApiKey"]);
        return Results.Ok(new { available });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static (string? Owner, string? Repo) ParseOwnerRepo(string repo)
    {
        if (string.IsNullOrEmpty(repo)) return (null, null);
        var parts = repo.Split('/');
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
            return (null, null);
        return (parts[0], parts[1]);
    }

    private static IResult ToHttpResult<T>(GitHubCallbackResult<T> result, Func<T, IResult> ok)
    {
        if (result.ServiceUnavailable)
        {
            return Results.Json(new
            {
                error = "github_client_not_configured",
                detail = "GitHub App client is not wired in this deployment"
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (result.Result is null)
        {
            return Results.Json(new { error = result.ErrorReason ?? "github_error" },
                statusCode: StatusCodes.Status502BadGateway);
        }
        return ok(result.Result);
    }

    private static JsonElement SafeParseJson(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return JsonDocument.Parse("null").RootElement.Clone();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("null").RootElement.Clone();
        }
    }
}
