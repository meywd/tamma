using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Cooldown delay between dispatch cycles with event emission.
/// </summary>
[Activity(
    "Tamma.ADL",
    "Cooldown",
    "Wait between dispatch cycles",
    Kind = ActivityKind.Task
)]
public class CooldownActivity : TammaAsyncActivity
{
    public override string? EventType => "ADL.COOLDOWN";

    [Input(Description = "Cooldown duration in seconds")]
    public Input<int> Seconds { get; set; } = new(10);

    [JsonConstructor]
    public CooldownActivity() { }

    public CooldownActivity(ILogger<CooldownActivity> logger)
    {
        Logger = logger;
    }

    protected override Task RunAsync(ActivityExecutionContext context)
    {
        // 2026-08-13 (engine-driven E2E): this activity EMITS the ADL.COOLDOWN
        // audit pair and completes immediately — the actual wait is the stock
        // scheduling Delay node the orchestrator sequences right after it
        // ("CooldownWait", a timer bookmark that SUSPENDS the instance). The
        // old in-process Task.Delay held the runtime's dispatch slot for the
        // whole cooldown, so a real 3600s cooldown queued every subsequently
        // dispatched workflow (all of the cycle's llm-calls included) behind
        // the sleeping orchestrator and the loop deadlocked itself.
        var seconds = Seconds.GetOrDefault(context);
        Logger?.LogInformation(
            "Cooldown: {Seconds}s until the next cycle (timer-bookmark wait follows)", seconds);
        return Task.CompletedTask;
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["seconds"] = Seconds.Get(context),
    };
}
