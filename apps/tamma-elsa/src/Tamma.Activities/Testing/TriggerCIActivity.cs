using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Testing.Models;

namespace Tamma.Activities.Testing;

/// <summary>
/// ELSA activity that triggers a CI pipeline run for a given repository and branch.
/// Supports real CI integration (via callback URL) and mock mode for testing.
/// </summary>
[Activity(
    "Tamma.Testing",
    "Trigger CI",
    "Trigger a CI/CD pipeline run for the specified repository and branch",
    Kind = ActivityKind.Task
)]
public class TriggerCIActivity : CodeActivity<CITriggerResult>
{
    private readonly ILogger<TriggerCIActivity> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Repository URL or owner/repo</summary>
    [Input(Description = "Repository URL or owner/repo")]
    public Input<string> Repository { get; set; } = default!;

    /// <summary>Branch to run CI against</summary>
    [Input(Description = "Branch name to run CI against")]
    public Input<string> Branch { get; set; } = default!;

    /// <summary>Commit SHA to target (optional)</summary>
    [Input(Description = "Specific commit SHA to target")]
    public Input<string?> CommitSha { get; set; } = default!;

    [JsonConstructor]
    public TriggerCIActivity()
    {
        _logger = null!;
        _httpClientFactory = null!;
        _configuration = null!;
    }

    public TriggerCIActivity(
        ILogger<TriggerCIActivity> logger,
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
        var repository = Repository.Get(context);
        var branch = Branch.Get(context);
        var commitSha = CommitSha.Get(context);

        _logger.LogInformation(
            "Triggering CI pipeline for session {SessionId}, repo {Repository}, branch {Branch}",
            sessionId, repository, branch);

        try
        {
            var useMock = _configuration.GetValue<bool>("Testing:UseMock");

            CITriggerResult result;
            if (useMock)
            {
                result = SimulateCITrigger(sessionId, repository, branch);
            }
            else
            {
                result = await TriggerRealCI(sessionId, repository, branch, commitSha);
            }

            _logger.LogInformation(
                "CI pipeline triggered: RunId={RunId}, Success={Success}",
                result.RunId, result.Success);

            context.SetResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger CI pipeline for session {SessionId}", sessionId);

            context.SetResult(new CITriggerResult
            {
                Success = false,
                Error = $"Failed to trigger CI: {ex.Message}",
                TriggeredAt = DateTime.UtcNow
            });
        }
    }

    private async Task<CITriggerResult> TriggerRealCI(
        Guid sessionId, string repository, string branch, string? commitSha)
    {
        var callbackUrl = _configuration["Engine:CallbackUrl"];
        if (string.IsNullOrEmpty(callbackUrl))
        {
            throw new InvalidOperationException(
                "Engine:CallbackUrl is required for real CI integration");
        }

        var httpClient = _httpClientFactory.CreateClient();
        var requestBody = new
        {
            sessionId = sessionId.ToString(),
            repository,
            branch,
            commitSha,
            action = "trigger-ci"
        };

        var response = await httpClient.PostAsJsonAsync(
            $"{callbackUrl.TrimEnd('/')}/api/engine/trigger-ci", requestBody);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        return new CITriggerResult
        {
            Success = true,
            RunId = result.TryGetProperty("runId", out var runId)
                ? runId.GetString() ?? Guid.NewGuid().ToString()
                : Guid.NewGuid().ToString(),
            PipelineUrl = result.TryGetProperty("pipelineUrl", out var url)
                ? url.GetString() ?? string.Empty
                : string.Empty,
            TriggeredAt = DateTime.UtcNow
        };
    }

    private static CITriggerResult SimulateCITrigger(
        Guid sessionId, string repository, string branch)
    {
        var runId = $"run-{Guid.NewGuid():N}";

        return new CITriggerResult
        {
            Success = true,
            RunId = runId,
            PipelineUrl = $"https://ci.example.com/{repository}/runs/{runId}",
            TriggeredAt = DateTime.UtcNow
        };
    }
}
