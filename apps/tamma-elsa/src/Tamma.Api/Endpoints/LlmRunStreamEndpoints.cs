using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Streaming;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using SseWriter = Tamma.Api.Services.Engine.Lifecycle.SseWriter;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 32-23 (AC1/AC2/AC8) — <c>GET /api/v1/llm/runs/{correlationId}/stream</c>,
/// the human-facing streaming run tap. Dashboard (JWT) / <c>tamma</c> CLI
/// (ApiKey) callers subscribe to a live SSE view of a managed LLM run — each
/// <c>tool_call</c>/<c>tool_result</c>/<c>token</c>/<c>question</c>/<c>answer</c>/<c>final</c>
/// frame as it happens — fed by the decoupled in-process
/// <see cref="ILlmRunStreamBus"/>. The tap is READ-ONLY observability: it never
/// holds up, retries, or influences the engine's buffered <c>/llm/call</c>.
///
/// <para><b>Auth is the human plane (AC2)</b>: the route rides
/// <c>AuthenticatedAny</c> (JWT + ApiKey), NOT the engine bearer. In SaaS the
/// caller may only tap runs its tenant owns — a <c>correlationId</c> for another
/// tenant returns <b>404</b> (never a cross-tenant existence oracle). In
/// single-user the sole user may tap any local run. Missing/invalid auth ⇒ 401
/// (from the policy, before this handler runs).</para>
///
/// <para><b>SSE hardening</b> is copied from
/// <see cref="Admin.AdminTenantEventsSseEndpoint"/>: <see cref="SseWriter"/>
/// headers, 30s <c>: keepalive</c> heartbeats during quiet turns, a
/// <see cref="MaxStreamDurationSeconds"/> ceiling that kicks an abandoned tab,
/// and a clean <c>event: end</c> close.</para>
/// </summary>
public static class LlmRunStreamEndpoints
{
    /// <summary>Maximum stream lifetime — the server closes the tap at this
    /// point so an abandoned dashboard tab doesn't pin a connection. Matches
    /// the admin SSE ceiling.</summary>
    public const int MaxStreamDurationSeconds = 30 * 60;

    /// <summary>Keepalive cadence so proxies don't drop the connection during a
    /// quiet run (matches the admin SSE cadence).</summary>
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(30);

    /// <summary>The terminal <c>AGENT.RUN.*</c> event types. Their presence in the
    /// tenant store means the run has already finished — the tap must NOT park on a
    /// live <c>final</c> that will never come.</summary>
    private static readonly string[] TerminalRunEventTypes =
        { "AGENT.RUN.SUCCESS", "AGENT.RUN.FAILED" };

    public static async Task StreamRun(
        string correlationId,
        [FromServices] ILlmRunStreamBus bus,
        [FromServices] ITenantContext tenantContext,
        [FromServices] ITammaModeProvider modeProvider,
        [FromServices] IEventRepository eventRepo,
        [FromServices] ILoggerFactory loggerFactory,
        [FromServices] TimeProvider timeProvider,
        HttpContext http,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.Endpoints.LlmRunStreamEndpoints");

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("correlationId required", ct).ConfigureAwait(false);
            return;
        }

        // The run correlationId is the workflow-instance Guid. Reject a non-Guid
        // route value with 400: it can never match a real run, it keeps the value
        // newline-free (no SSE-line injection into the `: stream-open` comment below),
        // and it hardens the tenant-scoped `Tags->>'correlationId'` lookups.
        if (!Guid.TryParse(correlationId, out _))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("correlationId must be a GUID", ct).ConfigureAwait(false);
            return;
        }

        // AC2 — SaaS ownership guard. A correlationId not owned by the caller's
        // tenant returns 404 (never confirm existence cross-tenant). The check is a
        // targeted, tenant-scoped EXISTS on Tags.correlationId — volume-independent,
        // so a busy tenant's live run whose AGENT.RUN.STARTED has aged past any
        // recent-N window is STILL recognised as owned (no false 404 on one's own
        // in-flight run). The read is structurally tenant-scoped
        // (t_<hex>.domain_events), so a foreign run is physically absent from the
        // caller's store. Single-user owns every run.
        Guid? tenantId = tenantContext.TenantId;
        if (modeProvider.Mode == TammaMode.SaaS)
        {
            if (tenantId is null
                || !await eventRepo.ExistsByCorrelationIdAsync(tenantId.Value, correlationId).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "run-tap denied (cross-tenant / unknown run) => 404. correlationId={CorrelationId} callerTenantId={TenantId}",
                    correlationId, tenantId);
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        SseWriter.WriteHeaders(http.Response);

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct, http.RequestAborted);
        streamCts.CancelAfter(TimeSpan.FromSeconds(MaxStreamDurationSeconds));
        var token = streamCts.Token;

        var startedAt = timeProvider.GetUtcNow();
        var framesSent = 0;
        var reason = "run_complete";
        var replayRequested = IsReplayRequested(http);

        logger.LogInformation(
            "run-tap opened. correlationId={CorrelationId} tenantId={TenantId} replay={Replay}",
            correlationId, tenantId, replayRequested);

        try
        {
            await WriteRawAsync(http, $": stream-open correlationId={correlationId}\n\n", token).ConfigureAwait(false);

            // Read the run's stored DCB events once (when we have a real tenant). Used for
            // BOTH the early-terminal short-circuit and the optional replay catch-up.
            // Single-user with a null (or empty) tenant has no tenant-scoped read path, so
            // it falls through to the live tail (unchanged). The `!= Guid.Empty` guard keeps
            // an empty ambient tenant off the tenant-scoped read, which now throws on
            // Guid.Empty (defence-in-depth parity with the other correlation reads).
            IReadOnlyList<DomainEvent> stored = tenantId is { } tid && tid != Guid.Empty
                ? await eventRepo.ListByCorrelationIdAsync(tid, correlationId).ConfigureAwait(false)
                : Array.Empty<DomainEvent>();

            // Early terminal check — if a terminal AGENT.RUN.SUCCESS/FAILED is already
            // persisted, the run has finished. The terminal DCB event is written BEFORE
            // the bus `final` frame (ManagedAgent), so if we can see it the live topic
            // is already gone (or will be, without another subscriber). Do NOT Subscribe
            // (that would recreate a zombie topic that never gets a `final`) and do NOT
            // park on WaitToReadAsync for a live `final` that will never come — that is
            // the 30-minute hang on a finished run. A replay tap streams the stored
            // frames first; then both close promptly.
            if (stored.Any(IsTerminalRunEvent))
            {
                if (replayRequested)
                {
                    framesSent += await ReplayStoredAsync(stored, correlationId, http, token)
                        .ConfigureAwait(false);
                }
                reason = "already_complete";
                return; // finally writes the terminal `event: end`
            }

            // Live (not-yet-terminal) run. Subscribe BEFORE the (optional) replay read
            // so no live frame produced between the catch-up read and the live tail is
            // missed (the admin SSE resume idiom). Dispose detaches the channel and —
            // with remove-on-empty — reclaims the topic, so nothing leaks on disconnect.
            using var subscription = bus.Subscribe(correlationId);

            // AC8 — optional catch-up from the tenant's DCB store, then live tail.
            if (replayRequested)
            {
                framesSent += await ReplayStoredAsync(stored, correlationId, http, token)
                    .ConfigureAwait(false);
            }

            var reader = subscription.Reader;
            Task<bool>? waitTask = null;
            var closedOnFinal = false;

            while (!token.IsCancellationRequested)
            {
                // A single pending wait reused across heartbeats — never abandoned
                // (so waiters don't accumulate on a long quiet run).
                waitTask ??= reader.WaitToReadAsync(token).AsTask();

                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var delayTask = Task.Delay(KeepaliveInterval, heartbeatCts.Token);

                var winner = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
                if (winner == delayTask)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                    await WriteRawAsync(http, ": keepalive\n\n", token).ConfigureAwait(false);
                    continue; // keep waitTask pending for the next iteration
                }

                heartbeatCts.Cancel(); // the reader won the race — stop the timer

                bool more;
                try
                {
                    more = await waitTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                waitTask = null; // consumed — renew next iteration

                if (!more)
                {
                    break; // channel completed => the run published `final`
                }

                while (reader.TryRead(out var frame))
                {
                    var safe = RunStreamFrameScrubber.Scrub(frame); // AC9 — key-free
                    await SseWriter.WriteEventAsync(http.Response, frame.Type, safe, token).ConfigureAwait(false);
                    framesSent++;
                    if (string.Equals(frame.Type, RunStreamFrameType.Final, StringComparison.Ordinal))
                    {
                        closedOnFinal = true;
                        break;
                    }
                }

                if (closedOnFinal)
                {
                    break;
                }
            }

            if (!closedOnFinal)
            {
                reason = http.RequestAborted.IsCancellationRequested ? "client_disconnect" : "max_duration";
            }
        }
        catch (OperationCanceledException)
        {
            reason = "client_disconnect";
        }
        finally
        {
            // Clean close marker — matches AdminTenantEventsSseEndpoint's
            // convention so a still-reading client sees a clean termination.
            try
            {
                await WriteRawAsync(
                    http,
                    $"event: end\ndata: {{\"reason\":\"{reason}\"}}\n\n",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                /* client already gone */
            }

            logger.LogInformation(
                "run-tap closed. correlationId={CorrelationId} framesSent={FramesSent} durationMs={DurationMs} reason={Reason}",
                correlationId, framesSent, (timeProvider.GetUtcNow() - startedAt).TotalMilliseconds, reason);
        }
    }

    /// <summary>Is <paramref name="e"/> a terminal <c>AGENT.RUN.SUCCESS</c>/
    /// <c>AGENT.RUN.FAILED</c> event? Its presence means the run has already
    /// finished, so the tap must not wait for a live <c>final</c>.</summary>
    private static bool IsTerminalRunEvent(DomainEvent e)
        => Array.IndexOf(TerminalRunEventTypes, e.Type) >= 0;

    /// <summary>AC8 — replay the run's already-emitted DCB events (scrubbed,
    /// key-free) as catch-up frames from <paramref name="stored"/> (already
    /// oldest-first, tenant-scoped, and matched to this correlationId by
    /// <see cref="IEventRepository.ListByCorrelationIdAsync"/> — the FULL set, never
    /// truncated). Returns the number of catch-up frames written.</summary>
    private static async Task<int> ReplayStoredAsync(
        IReadOnlyList<DomainEvent> stored, string correlationId, HttpContext http, CancellationToken ct)
    {
        var count = 0;
        foreach (var e in stored)
        {
            // Key-free catch-up payload: the fixed-vocabulary event type +
            // correlationId + the DCB sequence. Tags/Data bodies are NOT emitted.
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["correlationId"] = correlationId,
                ["seq"] = e.SequenceNumber,
                ["type"] = e.Type,
                ["replay"] = true,
            };
            await SseWriter.WriteEventAsync(http.Response, "replay", payload, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private static bool IsReplayRequested(HttpContext http)
        => string.Equals(http.Request.Query["replay"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteRawAsync(HttpContext http, string frame, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        await http.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
