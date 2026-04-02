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
/// Posts a status update comment on the GitHub issue.
/// Used after every step in SingleIssueCycle to keep the issue
/// as a living log of what Tamma is doing.
/// </summary>
[Activity(
    "Tamma.ADL",
    "Update Issue Status",
    "Post a status comment on the GitHub issue",
    Kind = ActivityKind.Task
)]
public class UpdateIssueStatusActivity : TammaAsyncActivity
{
    public override string? EventType => "CYCLE.ISSUE.UPDATE";

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Status message to post")]
    public Input<string> Message { get; set; } = default!;

    [Input(Description = "Optional labels to add")]
    public Input<string[]?> AddLabels { get; set; } = default!;

    [Input(Description = "Optional labels to remove")]
    public Input<string[]?> RemoveLabels { get; set; } = default!;

    [JsonConstructor]
    public UpdateIssueStatusActivity() { }

    public UpdateIssueStatusActivity(
        ILogger<UpdateIssueStatusActivity> logger,
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
        var issueNum = IssueNumber.Get(context);
        var message = Message.Get(context);
        var addLabels = AddLabels.Get(context);
        var removeLabels = RemoveLabels.Get(context);

        var callbackUrl = _configuration?["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl) || _httpClientFactory == null)
        {
            Logger?.LogInformation("[Issue #{IssueNumber}] {Message}", issueNum, message);
            return;
        }

        var httpClient = _httpClientFactory.CreateClient();
        var baseUrl = callbackUrl.TrimEnd('/');

        // Retry with backoff: 1s, 2s, 4s
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Post comment
                var commentPayload = new
                {
                    repository = repo,
                    issueNumber = issueNum,
                    body = message,
                };
                var response = await httpClient.PostAsJsonAsync(
                    $"{baseUrl}/api/engine/issue-comment", commentPayload);
                response.EnsureSuccessStatusCode();

                // Add labels if specified
                if (addLabels is { Length: > 0 })
                {
                    var labelPayload = new { repository = repo, issueNumber = issueNum, labels = addLabels };
                    await httpClient.PostAsJsonAsync($"{baseUrl}/api/engine/issue-labels", labelPayload);
                }

                // Remove labels if specified
                if (removeLabels is { Length: > 0 })
                {
                    foreach (var label in removeLabels)
                    {
                        await httpClient.DeleteAsync(
                            $"{baseUrl}/api/engine/issue-labels/{Uri.EscapeDataString(repo)}/{issueNum}/{Uri.EscapeDataString(label)}");
                    }
                }

                return; // success — exit retry loop
            }
            catch (Exception ex)
            {
                if (attempt < 2)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1s, 2s, 4s
                    Logger?.LogWarning(ex, "Issue update attempt {Attempt} failed, retrying in {Delay}s", attempt + 1, delay.TotalSeconds);
                    await Task.Delay(delay);
                }
                else
                {
                    // Final attempt failed — log and continue (don't block workflow)
                    Logger?.LogWarning(ex, "Failed to update issue #{IssueNumber} after 3 attempts", issueNum);
                }
            }
        }
    }

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
        ["message"] = Message.Get(context),
    };
}
