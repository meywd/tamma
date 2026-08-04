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
/// Dispatches a new ADL Orchestrator workflow instance (fire & forget).
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
    public override string? EventType => "ADL.SELF.DISPATCH";

    private readonly IWorkflowDispatcher? _dispatcher;

    [Input(Description = "Config JSON to pass to the new instance")]
    public Input<string> ConfigJson { get; set; } = default!;

    [JsonConstructor]
    public DispatchAdlActivity() { }

    public DispatchAdlActivity(
        ILogger<DispatchAdlActivity> logger,
        IWorkflowDispatcher dispatcher)
    {
        Logger = logger;
        _dispatcher = dispatcher;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        // Resolve from the execution context when the DI-injected field is null —
        // Elsa rehydrates a persisted definition through the [JsonConstructor], on
        // which path the field IS null, and returning quietly there would silently
        // end the autonomous loop with nothing but a warning. Same fallback shape as
        // ClosePullRequestActivity (`_apiClient ?? context.GetRequiredService<…>()`).
        var dispatcher = _dispatcher ?? context.GetService<IWorkflowDispatcher>();
        if (dispatcher == null)
        {
            Logger?.LogCritical(
                "No IWorkflowDispatcher available — cannot restart ADL; the autonomous loop has "
                + "STOPPED and will not resume until an adl-orchestrator instance is dispatched manually");
            return;
        }

        var configJson = ConfigJson.Get(context);

        var request = new DispatchWorkflowDefinitionRequest("adl-orchestrator")
        {
            Input = new Dictionary<string, object>
            {
                ["configJson"] = configJson,
            },
        };

        // DURABILITY (loop restart) — this dispatch is the ONLY thing that starts the
        // next ADL cycle: there is no cron trigger and no watchdog re-dispatching
        // `adl-orchestrator`. It is also the LAST step of the instance it restarts, so
        // an exception here does not merely fail a tick — it faults the instance
        // before the successor exists and the autonomous loop stops PERMANENTLY until
        // a human dispatches one by hand. A transient blip (broker/DB/dispatcher) must
        // therefore never propagate: retry with backoff, and if every attempt fails,
        // say so at Critical rather than throwing. Deliberately bounded and short —
        // this is an in-process dispatch, not an external API call.
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await dispatcher.DispatchAsync(request, default);
                Logger?.LogInformation("Dispatched new ADL Orchestrator cycle");
                return;
            }
            catch (Exception ex)
            {
                if (attempt == maxAttempts)
                {
                    // The loop is now stopping. This is the one log line that explains
                    // why nothing else ever happens, so it must be unmissable.
                    Logger?.LogCritical(
                        ex,
                        "ADL restart dispatch FAILED after {Attempts} attempts — the autonomous loop "
                        + "has STOPPED and will not resume until an adl-orchestrator instance is "
                        + "dispatched manually",
                        maxAttempts);
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
}
