using System.Text.Json;
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
/// Applies triage results: sets labels on the issue/creates issue for alerts,
/// posts a triage summary comment.
/// </summary>
[Activity(
    "Tamma.ADL",
    "Apply Triage Result",
    "Apply labels and post triage comment on the issue",
    Kind = ActivityKind.Task
)]
public class ApplyTriageResultActivity : TammaAsyncActivity
{
    public override string? EventType => "TRIAGE.APPLY.RESULT";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Item JSON")]
    public Input<string> ItemJson { get; set; } = default!;

    [Input(Description = "PO decision JSON with labels, priority, comment")]
    public Input<string> DecisionJson { get; set; } = default!;

    [JsonConstructor]
    public ApplyTriageResultActivity() { }

    public ApplyTriageResultActivity(
        ILogger<ApplyTriageResultActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        Logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        var repo = Repository.Get(context);
        var itemJson = ItemJson.Get(context);
        var decisionJson = DecisionJson.Get(context);

        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || _httpClientFactory == null)
        {
            Logger?.LogInformation("[Mock] Would apply triage result");
            return;
        }

        try
        {
            var item = JsonSerializer.Deserialize<TriageItem>(itemJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var decision = JsonSerializer.Deserialize<TriageDecision>(decisionJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (item == null || decision == null) return;

            var httpClient = _httpClientFactory.CreateClient();
            var baseUrl = callbackUrl.TrimEnd('/');

            if (item.Type == "issue" && item.Number > 0)
            {
                // Apply labels to existing issue
                if (decision.Labels is { Count: > 0 })
                {
                    await httpClient.PostAsJsonAsync(
                        $"{baseUrl}/api/engine/issue-labels",
                        new { repository = repo, issueNumber = item.Number, labels = decision.Labels });
                }

                // Post triage comment
                if (!string.IsNullOrEmpty(decision.Comment))
                {
                    await httpClient.PostAsJsonAsync(
                        $"{baseUrl}/api/engine/issue-comment",
                        new { repository = repo, issueNumber = item.Number, body = decision.Comment });
                }
            }
            else
            {
                // Create issue for security alert
                var createResult = await httpClient.PostAsJsonAsync(
                    $"{baseUrl}/api/engine/create-issue",
                    new
                    {
                        repository = repo,
                        title = item.Title,
                        body = $"{item.Body}\n\n---\n{decision.Comment}",
                        labels = decision.Labels,
                    });

                Logger?.LogInformation("Created issue for {Type}: {Title}", item.Type, item.Title);
            }
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Failed to apply triage result");
        }
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["repository"] = Repository.Get(context),
    };
}

public class TriageDecision
{
    public string Priority { get; set; } = "normal";
    public string Type { get; set; } = "unknown";
    public string Complexity { get; set; } = "medium";
    public string Automation { get; set; } = "needs-human";
    public List<string> Labels { get; set; } = new();
    public string? Comment { get; set; }
    public string? Reasoning { get; set; }
}
