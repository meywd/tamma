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

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var seconds = Seconds.Get(context);
        Logger?.LogInformation("Cooldown: waiting {Seconds}s before next cycle", seconds);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["seconds"] = Seconds.Get(context),
    };
}
