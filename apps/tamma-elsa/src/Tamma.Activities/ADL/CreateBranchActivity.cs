using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.ADL;

/// <summary>
/// Creates a feature branch for the selected issue.
/// Branch name format: adl/{issueNumber}-{sanitized-title}
///
/// Outcomes:
///   - Created: branch created successfully
///   - Error: branch creation failed
/// </summary>
[Activity(
    "Tamma.ADL",
    "Create Branch",
    "Create a feature branch for autonomous development",
    Kind = ActivityKind.Task
)]
[FlowNode("Created", "Error")]
public class CreateBranchActivity : Activity
{
    private readonly ILogger<CreateBranchActivity>? _logger;
    private readonly IGitHubIntegrationService? _github;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Issue title for branch naming")]
    public Input<string> IssueTitle { get; set; } = default!;

    [Output(Description = "Created branch name")]
    public Output<string?> BranchName { get; set; } = default!;

    [JsonConstructor]
    public CreateBranchActivity() { }

    public CreateBranchActivity(
        ILogger<CreateBranchActivity> logger,
        IGitHubIntegrationService github)
    {
        _logger = logger;
        _github = github;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context);
        var issueNumber = IssueNumber.Get(context);
        var issueTitle = IssueTitle.Get(context) ?? "";

        var sanitized = SanitizeBranchName(issueTitle);
        var branchName = $"adl/{issueNumber}-{sanitized}";

        try
        {
            var result = await _github!.CreateGitHubBranchAsync(repository, branchName);
            if (!result.Success)
            {
                _logger?.LogError("Failed to create branch {Branch}: {Error}",
                    branchName, result.Error);
                await context.CompleteActivityWithOutcomesAsync("Error");
                return;
            }

            BranchName.Set(context, branchName);

            _logger?.LogInformation("Created branch {Branch} for issue #{Number}",
                branchName, issueNumber);
            await context.CompleteActivityWithOutcomesAsync("Created");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error creating branch {Branch}", branchName);
            await context.CompleteActivityWithOutcomesAsync("Error");
        }
    }

    private static string SanitizeBranchName(string title)
    {
        var sanitized = title.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('/', '-')
            .Replace('\\', '-');

        // Keep only alphanumeric and hyphens, limit length
        var chars = sanitized
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .Take(40)
            .ToArray();

        return new string(chars).Trim('-');
    }
}
