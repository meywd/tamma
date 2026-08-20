using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Contracts;
using Elsa.Workflows.Runtime.Requests;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Dispatches a new ADL Orchestrator workflow instance (fire &amp; forget).
/// The current instance finishes; the new one picks up with fresh config.
/// This is how the ADL runs forever — each cycle is a fresh instance.
/// </summary>
[Activity(
    "Tamma.ADL",
    "Dispatch ADL",
    "Dispatch a new ADL Orchestrator cycle",
    Kind = ActivityKind.Task
)]
public class DispatchAdlActivity : TammaAsyncActivity
{
    public override string? EventType => AdlLoopEvents.SelfDispatch;

    /// <summary>
    /// Transient-property key carrying whether the restart actually happened, so
    /// <see cref="BuildEndData"/> reports the truth. Without it a swallowed dispatch
    /// failure still emitted <c>ADL.SELF.DISPATCH.COMPLETED</c> with
    /// <c>status=success</c> — the audit trail claiming the loop restarted at the exact
    /// moment it died.
    /// </summary>
    private const string DispatchedKey = "adl:selfDispatch:dispatched";

    private readonly IWorkflowDispatcher? _dispatcher;
    private readonly AdlLoopConfigCache? _configCache;

    [Input(Description = "Config JSON to pass to the new instance")]
    public Input<string> ConfigJson { get; set; } = default!;

    [JsonConstructor]
    public DispatchAdlActivity() { }

    public DispatchAdlActivity(
        ILogger<DispatchAdlActivity> logger,
        IWorkflowDispatcher dispatcher,
        AdlLoopConfigCache? configCache = null)
    {
        Logger = logger;
        _dispatcher = dispatcher;
        _configCache = configCache;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        context.TransientProperties[DispatchedKey] = false;
        var configJson = ConfigJson.Get(context);

        // Remember the live config BEFORE dispatching: if the dispatch fails, this is
        // the only surviving copy of what the loop was running with, and it is what the
        // watchdog re-arms from (see AdlLoopConfigCache).
        (_configCache ?? context.GetService<AdlLoopConfigCache>())?.Remember(configJson);

        // Resolve from the execution context when the DI-injected field is null —
        // Elsa rehydrates a persisted definition through the [JsonConstructor], on
        // which path the field IS null, and returning quietly there would silently
        // end the autonomous loop with nothing but a warning. Same fallback shape as
        // ClosePullRequestActivity (`_apiClient ?? context.GetRequiredService<…>()`).
        var dispatcher = _dispatcher ?? context.GetService<IWorkflowDispatcher>();
        if (dispatcher == null)
        {
            ReportLoopStopped(context, "no IWorkflowDispatcher available in this execution scope", ex: null);
            return;
        }

        // DURABILITY (loop restart) — this dispatch is the ONLY thing that starts the
        // next ADL cycle from inside the workflow: it is the LAST step of the instance
        // it restarts, so an exception here does not merely fail a tick — it faults the
        // instance before the successor exists. A transient blip (broker/DB/dispatcher)
        // must therefore never propagate: retry with backoff, and if every attempt
        // fails, report it LOUDLY (durable event + Critical log) rather than throwing.
        // Deliberately bounded and short — this is an in-process dispatch, not an
        // external API call. The version-id resolve (2026-08-13 — the request ctor takes
        // the VERSION id, not the definition id; see PublishedWorkflowDispatch) lives
        // INSIDE the retry for the same reason.
        //
        // The out-of-band safety net is AdlLoopWatchdogService, which notices "no live
        // adl-orchestrator instance for N minutes" and re-arms. Retry + event + watchdog
        // are three independent layers because losing this dispatch silently is the one
        // failure that ends the autonomous loop outright.
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var definitionVersionId = await Tamma.Activities.Core.PublishedWorkflowDispatch
                    .ResolvePublishedVersionIdAsync(
                        context.GetRequiredService<Elsa.Workflows.Management.IWorkflowDefinitionService>(),
                        "adl-orchestrator");
                var request = new DispatchWorkflowDefinitionRequest(definitionVersionId)
                {
                    Input = new Dictionary<string, object>
                    {
                        ["configJson"] = configJson,
                    },
                };

                await dispatcher.DispatchAsync(request, default);
                context.TransientProperties[DispatchedKey] = true;
                Logger?.LogInformation("Dispatched new ADL Orchestrator cycle");
                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    ReportLoopStopped(context, $"all {maxAttempts} restart-dispatch attempts failed", ex);
                    return;
                }

                Logger?.LogWarning(
                    ex,
                    "ADL restart dispatch attempt {Attempt}/{Attempts} failed; retrying",
                    attempt,
                    maxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
    }

    /// <summary>
    /// The loop is now stopping. Record it three ways so it cannot die silently:
    /// a Critical log line, and a durable error-status DCB event
    /// (<see cref="AdlLoopEvents.SelfDispatchFailed"/>) that the drain persists on the
    /// workflow-completion backstop — an operator/alert rule can query the event stream
    /// for it, which a rotating log file on the VPS does not give them.
    ///
    /// <para>The activity still does NOT throw: faulting here would add nothing (the
    /// successor already does not exist) and would lose the emitted event's flush.</para>
    /// </summary>
    private void ReportLoopStopped(ActivityExecutionContext context, string detail, Exception? ex)
    {
        const string message =
            "ADL restart dispatch FAILED — the autonomous loop has STOPPED and will not resume "
            + "until the watchdog re-arms it or an adl-orchestrator instance is dispatched manually";

        if (ex is not null) Logger?.LogCritical(ex, message + " ({Detail})", detail);
        else Logger?.LogCritical(message + " ({Detail})", detail);

        TammaEventEmitter.Emit(context, this, Logger, new TammaEvent
        {
            EventType = AdlLoopEvents.SelfDispatchFailed,
            Status = "error",
            Error = ex?.Message ?? detail,
            Tags = new Dictionary<string, object?>
            {
                ["component"] = "adl-orchestrator",
                // Queryable marker: "is the autonomous loop dead right now" is one
                // tag filter over domain_events, not a log grep.
                ["loopStopped"] = "true",
            },
            Data = new Dictionary<string, object?>
            {
                ["detail"] = detail,
                ["exception"] = ex?.GetType().Name,
                ["message"] = message,
            },
        });
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["dispatched"] = context.TransientProperties.TryGetValue(DispatchedKey, out var d) && d is true,
    };
}
