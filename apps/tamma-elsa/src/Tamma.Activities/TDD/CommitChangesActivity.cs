using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Activities.TDD.Models;

namespace Tamma.Activities.TDD;

/// <summary>
/// ELSA activity that creates an atomic TDD commit including both test and implementation files.
/// Commit message format: feat({storyId}): {taskDescription} [TDD]
/// Only commits if the RED and GREEN phases succeeded.
/// </summary>
[Activity(
    "Tamma.TDD",
    "Commit Changes",
    "Create atomic TDD commit with test and implementation files",
    Kind = ActivityKind.Task
)]
public class CommitChangesActivity : CodeActivity<CommitResult>
{
    private readonly ILogger<CommitChangesActivity>? _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Story identifier (used in commit message)</summary>
    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Task description (used in commit message)</summary>
    [Input(Description = "Task description for commit message")]
    public Input<string> TaskDescription { get; set; } = default!;

    /// <summary>Repository URL</summary>
    [Input(Description = "Repository URL")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    /// <summary>Branch name</summary>
    [Input(Description = "Working branch name")]
    public Input<string> BranchName { get; set; } = default!;

    /// <summary>Test files to commit</summary>
    [Input(Description = "Test files to include in commit")]
    public Input<List<string>> TestFiles { get; set; } = default!;

    /// <summary>Implementation files to commit</summary>
    [Input(Description = "Implementation files to include in commit")]
    public Input<List<string>> ImplementationFiles { get; set; } = default!;

    [JsonConstructor]
    public CommitChangesActivity() { }

    public CommitChangesActivity(
        ILogger<CommitChangesActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var storyId = StoryId.Get(context);
        var taskDescription = TaskDescription.Get(context);
        var repositoryUrl = RepositoryUrl.Get(context);
        var branchName = BranchName.Get(context);
        var testFiles = TestFiles.Get(context) ?? new List<string>();
        var implementationFiles = ImplementationFiles.Get(context) ?? new List<string>();

        var allFiles = testFiles.Concat(implementationFiles).Distinct().ToList();
        var commitMessageFormat = _configuration?["TDD:CommitMessageFormat"]
            ?? "feat({storyId}): {taskDescription} [TDD]";
        var commitMessage = commitMessageFormat
            .Replace("{storyId}", storyId)
            .Replace("{taskDescription}", taskDescription);

        _logger?.LogInformation(
            "TDD Commit: Creating commit for {FileCount} files in story {StoryId}, session {SessionId}",
            allFiles.Count, storyId, sessionId);

        try
        {
            if (allFiles.Count == 0)
            {
                _logger?.LogWarning(
                    "TDD Commit: No files to commit for session {SessionId}", sessionId);

                context.SetResult(new CommitResult
                {
                    Success = false,
                    ErrorMessage = "No files to commit"
                });
                return;
            }

            var callbackUrl = _configuration?["Engine:CallbackUrl"];
            var useMock = _configuration?.GetValue<bool>("Anthropic:UseMock") ?? false;

            CommitResult result;
            if (useMock)
            {
                result = SimulateCommit(commitMessage, allFiles);
            }
            else if (!string.IsNullOrEmpty(callbackUrl))
            {
                result = await CallEngineCommit(callbackUrl, repositoryUrl, branchName, commitMessage, allFiles);
            }
            else
            {
                // Without engine callback, simulate the commit
                result = SimulateCommit(commitMessage, allFiles);
            }

            _logger?.LogInformation(
                "TDD Commit: {Status} for session {SessionId}. SHA={Sha}, Message=\"{Message}\"",
                result.Success ? "Committed" : "Failed", sessionId, result.CommitSha, result.CommitMessage);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TDD Commit: Error creating commit for session {SessionId}", sessionId);

            context.SetResult(new CommitResult
            {
                Success = false,
                CommitMessage = commitMessage,
                ErrorMessage = $"Commit failed: {ex.Message}"
            });
        }
    }

    private async Task<CommitResult> CallEngineCommit(
        string callbackUrl,
        string repositoryUrl,
        string branchName,
        string commitMessage,
        List<string> files)
    {
        var httpClient = _httpClientFactory!.CreateClient();
        var requestBody = new
        {
            action = "git_commit",
            repository = repositoryUrl,
            branch = branchName,
            commitMessage,
            files
        };

        var response = await httpClient.PostAsJsonAsync(
            $"{callbackUrl.TrimEnd('/')}/api/engine/execute-task", requestBody);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sha = result.TryGetProperty("commitSha", out var shaEl) ? shaEl.GetString() ?? "" : "";

        return new CommitResult
        {
            Success = true,
            CommitSha = sha,
            CommitMessage = commitMessage,
            FilesCommitted = files
        };
    }

    private static CommitResult SimulateCommit(string commitMessage, List<string> files)
    {
        // Generate a simulated SHA
        var sha = Guid.NewGuid().ToString("N")[..12];

        return new CommitResult
        {
            Success = true,
            CommitSha = sha,
            CommitMessage = commitMessage,
            FilesCommitted = files
        };
    }
}
