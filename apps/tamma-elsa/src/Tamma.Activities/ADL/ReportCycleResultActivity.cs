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
        var error = Error.Get(context);

        // Set workflow output
        context.WorkflowExecutionContext.Output["exitReason"] = reason;
        context.WorkflowExecutionContext.Output["issueNumber"] = issueNumber;

        // Call engine callback if configured
        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (!string.IsNullOrEmpty(callbackUrl) && _httpClientFactory != null)
        {
            try
            {
                var httpClient = _httpClientFactory.CreateClient();
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
