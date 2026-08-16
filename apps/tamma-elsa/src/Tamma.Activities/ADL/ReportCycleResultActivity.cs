using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;

namespace Tamma.Activities.ADL;

/// <summary>
/// Reports the cycle result back to the orchestrator/engine via callback API.
/// Every exit path in SingleIssueCycle goes through this activity.
/// </summary>
[Activity(
    "Tamma.ADL",
    "Report Cycle Result",
    "Report completion/failure back to the orchestrator engine",
    Kind = ActivityKind.Task
)]
public class ReportCycleResultActivity : TammaAsyncActivity
{
    public override string? EventType => "CYCLE.RESULT.REPORT";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Exit reason: success, error, deferred, split, needsHuman")]
    public Input<string> Reason { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = new(0);

    [Input(Description = "Optional error message")]
    public Input<string?> Error { get; set; } = default!;

    [JsonConstructor]
    public ReportCycleResultActivity() { }

    public ReportCycleResultActivity(
        ILogger<ReportCycleResultActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        Logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var reason = Reason.Get(context);
        var issueNumber = IssueNumber.Get(context);
        // 2026-08-13 (engine-driven E2E): Input<T>.Get THROWS "Error is required."
        // on any null evaluated value — an OPTIONAL input must read GetOrDefault,
        // or every non-error exit path (needsHuman included) faults right here.
        var error = Error.GetOrDefault(context);

        // Set workflow output
        context.WorkflowExecutionContext.Output["exitReason"] = reason;
        context.WorkflowExecutionContext.Output["issueNumber"] = issueNumber;

        // 2026-08-13 (engine-driven E2E): store-rehydrated activities are built
        // by the [JsonConstructor] with NULL ctor-injected members — resolve
        // from the execution context (ctor-or-GetService idiom).
        var configuration = _configuration ?? context.GetService<IConfiguration>();
        var httpClientFactory = _httpClientFactory ?? context.GetService<IHttpClientFactory>();

        // Call engine callback if configured
        var callbackUrl = configuration?["Engine:CallbackUrl"];
        if (!string.IsNullOrEmpty(callbackUrl) && httpClientFactory != null)
        {
            try
            {
                var httpClient = httpClientFactory.CreateClient();
                var payload = new
                {
                    exitReason = reason,
                    issueNumber,
                    error,
                    timestamp = DateTime.UtcNow,
                };
                await httpClient.PostAsJsonAsync(
                    $"{callbackUrl.TrimEnd('/')}/api/engine/cycle-result", payload);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to report cycle result to engine");
            }
        }

        Logger?.LogInformation(
            "Cycle result: {Reason} for issue #{IssueNumber}",
            reason, issueNumber);
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["reason"] = Reason.Get(context),
        ["issueNumber"] = IssueNumber.Get(context),
    };
}
