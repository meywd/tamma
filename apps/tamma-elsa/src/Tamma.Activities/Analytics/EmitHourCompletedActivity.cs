using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.Analytics;

/// <summary>
/// Story 28-10 — terminal event for the hourly-rollup workflow. Emits
/// <c>ANALYTICS.ROLLUP.HOUR_COMPLETED</c> with the aggregated success /
/// failure counts so the ops dashboard can answer "did the 14:05 rollup
/// cover every active tenant?" with a single query.
/// </summary>
[Activity(
    "Tamma.Analytics",
    "Emit Hour Completed",
    "Emit the terminal ANALYTICS.ROLLUP.HOUR_COMPLETED event.",
    Kind = ActivityKind.Task)]
public sealed class EmitHourCompletedActivity : TammaAsyncActivity
{
    [Input(Description = "UTC top-of-hour bucket just rolled up.")]
    public Input<DateTime> Hour { get; set; } = default!;

    [Input(Description = "Number of tenants rolled up successfully.")]
    public Input<int> TenantsSuccess { get; set; } = default!;

    [Input(Description = "Number of tenants whose rollup threw.")]
    public Input<int> TenantsFailed { get; set; } = default!;

    public override string? EventType => "ANALYTICS.ROLLUP.HOUR";

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var hour = AnalyticsRollupEvents.TruncateToHour(Hour.Get(context));
        var success = TenantsSuccess.Get(context);
        var failed = TenantsFailed.Get(context);

        Logger ??= context.GetService<ILogger<EmitHourCompletedActivity>>();

        var publisher = context.GetRequiredService<IPlatformEventPublisher>();
        await publisher.AppendAndPublishAsync(
            AnalyticsRollupEvents.BuildEvent(
                AnalyticsRollupEvents.HourCompleted,
                hour,
                tenantId: null,
                data: new Dictionary<string, object?>
                {
                    ["tenantsSuccess"] = success,
                    ["tenantsFailed"] = failed,
                    ["totalTenants"] = success + failed,
                }),
            context.CancellationToken).ConfigureAwait(false);

        Logger?.LogInformation(
            "analytics.rollup.hour_completed hour={Hour} success={Success} failed={Failed}",
            hour, success, failed);
    }
}
