using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Simple delay activity used in the concurrency wait-loop.
/// Waits a configurable number of seconds before the next concurrency check.
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Concurrency Wait Delay",
    "Delay before retrying concurrency check",
    Kind = ActivityKind.Task
)]
public class ConcurrencyWaitDelayActivity : CodeActivity
{
    private readonly ILogger<ConcurrencyWaitDelayActivity>? _logger;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Delay in seconds before next concurrency check (0 = use config or default 5)")]
    public Input<int> DelaySeconds { get; set; } = new(0);

    public ConcurrencyWaitDelayActivity() : this(null, null) { }

    public ConcurrencyWaitDelayActivity(
        ILogger<ConcurrencyWaitDelayActivity>? logger,
        IConfiguration? configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var seconds = DelaySeconds.Get(context);
        if (seconds <= 0)
        {
            var configValue = _configuration?.GetValue<int?>("Tamma:ConcurrencyWaitDelaySeconds");
            seconds = configValue ?? 5;
        }

        _logger?.LogInformation(
            "Concurrency wait: delaying {Seconds}s before next check",
            seconds);

        await Task.Delay(TimeSpan.FromSeconds(seconds));
    }
}
