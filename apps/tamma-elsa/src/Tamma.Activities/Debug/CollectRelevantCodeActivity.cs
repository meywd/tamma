using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.Debug.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Debug;

/// <summary>
/// Collects relevant code files and recent changes to those files for debugging context.
/// Part of the parallel debug context gathering (Fork).
/// </summary>
[Activity(
    "Tamma.Debug",
    "Collect Relevant Code",
    "Gather code files involved and recent changes",
    Kind = ActivityKind.Task
)]
public class CollectRelevantCodeActivity : CodeActivity<RelevantCode>
{
    private readonly ILogger<CollectRelevantCodeActivity>? _logger;
    private readonly IIntegrationService? _integrationService;

    /// <summary>Files involved in the error</summary>
    [Input(Description = "Files involved in the error (optional)")]
    public Input<List<string>?> RelevantFiles { get; set; } = default!;

    /// <summary>Repository URL</summary>
    [Input(Description = "Repository URL")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Branch name</summary>
    [Input(Description = "Branch name")]
    public Input<string> BranchName { get; set; } = default!;

    /// <summary>Debug context mode</summary>
    [Input(Description = "Debug context mode")]
    public Input<string> DebugContextMode { get; set; } = default!;

    [JsonConstructor]
    public CollectRelevantCodeActivity() { }

    public CollectRelevantCodeActivity(
        ILogger<CollectRelevantCodeActivity> logger,
        IIntegrationService integrationService)
    {
        _logger = logger;
        _integrationService = integrationService;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var relevantFiles = RelevantFiles.Get(context) ?? new List<string>();
        var repositoryUrl = RepositoryUrl.Get(context);
        var branchName = BranchName.Get(context);
        var mode = DebugContextMode.Get(context);

        _logger?.LogInformation(
            "Collecting relevant code for {FileCount} files in mode {Mode}",
            relevantFiles.Count, mode);

        try
        {
            var result = new RelevantCode
            {
                FilePaths = new List<string>(relevantFiles)
            };

            // Get file changes from the branch
            if (_integrationService != null && !string.IsNullOrEmpty(repositoryUrl))
            {
                var fileChanges = await _integrationService.GetGitHubFileChangesAsync(
                    repositoryUrl, branchName);

                // Add changed files not already in relevant files
                foreach (var change in fileChanges)
                {
                    if (!result.FilePaths.Contains(change.FilePath))
                        result.FilePaths.Add(change.FilePath);

                    result.RecentChanges.Add(
                        $"{change.ChangeType}: {change.FilePath} (+{change.Additions}/-{change.Deletions})");
                }

                // For TddFailure mode, emphasize implementation code
                if (mode == "TddFailure")
                {
                    // Prioritize non-test files
                    result.FilePaths = result.FilePaths
                        .OrderBy(f => f.Contains("test", StringComparison.OrdinalIgnoreCase)
                            || f.Contains("spec", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                        .ToList();
                }

                // Generate code snippets (simulated — in production would read file contents)
                foreach (var filePath in result.FilePaths.Take(10))
                {
                    result.Snippets.Add(new CodeSnippet
                    {
                        FilePath = filePath,
                        Content = $"// Content of {filePath} (retrieved via GitHub API)",
                        StartLine = 1,
                        EndLine = 100
                    });
                }
            }

            _logger?.LogInformation(
                "Collected {FileCount} relevant files, {ChangeCount} recent changes",
                result.FilePaths.Count, result.RecentChanges.Count);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to collect relevant code");
            context.SetResult(new RelevantCode
            {
                FilePaths = relevantFiles
            });
        }
    }
}
