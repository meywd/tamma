using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Tamma.Core.Interfaces;

namespace Tamma.Api.Services;

/// <summary>
/// CI/CD integration service — trigger tests and query build status via GitHub Actions.
/// </summary>
public class CIIntegrationService : ICIIntegrationService
{
    private readonly ILogger<CIIntegrationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int _ciPollIntervalMs;
    private readonly int _ciPollMaxAttempts;

    /// <summary>
    /// Story 38 (Phase 1) — when set, this request-scoped token overrides the named
    /// "github" client's static <c>GitHub:Token</c> bearer for EVERY Actions call
    /// made through this instance, so the CI mediation layer uses the per-tenant
    /// token it resolved (the "token used == token resolved" invariant, mirroring
    /// <see cref="GitHubIntegrationService"/>). Minted per-request by
    /// <see cref="Ci.CiClientFactory"/>; never logged, returned, or persisted.
    /// Null ⇒ legacy static-token behaviour.
    /// </summary>
    private readonly string? _tokenOverride;

    public CIIntegrationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<CIIntegrationService> logger)
        : this(httpClientFactory, configuration, logger, tokenOverride: null)
    {
    }

    /// <summary>
    /// Story 38 (Phase 1) — construct bound to a request-scoped
    /// <paramref name="tokenOverride"/>. Used by <see cref="Ci.CiClientFactory"/> so
    /// the CI-mediation endpoints perform the Actions call with the resolved
    /// per-tenant token.
    /// </summary>
    public CIIntegrationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<CIIntegrationService> logger,
        string? tokenOverride)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _tokenOverride = tokenOverride;
        _ciPollIntervalMs = configuration.GetValue<int>("CI:PollIntervalMs", 5000);
        _ciPollMaxAttempts = configuration.GetValue<int>("CI:PollMaxAttempts", 10);
    }

    /// <summary>
    /// Create the named "github" <see cref="HttpClient"/>, applying the
    /// request-scoped <see cref="_tokenOverride"/> when present. The factory returns
    /// a fresh client instance per call, so mutating its default Authorization
    /// header is scoped to this instance only.
    /// </summary>
    private HttpClient CreateGitHubClient()
    {
        var client = _httpClientFactory.CreateClient("github");
        if (!string.IsNullOrWhiteSpace(_tokenOverride))
        {
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _tokenOverride);
        }
        return client;
    }

    public async Task<IntegrationResult<TestRunResult>> TriggerTestsAsync(string repository, string branch)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            _logger.LogInformation("Triggering tests for {Repo}/{Branch}", repository, branch);

            // Trigger workflow dispatch
            var payload = new { @ref = branch };
            var workflowId = _configuration["CI:WorkflowId"] ?? "test.yml";
            var response = await httpClient.PostAsJsonAsync(
                $"/repos/{repository}/actions/workflows/{workflowId}/dispatches", payload);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to dispatch workflow, checking latest run instead");
            }

            // Poll for the workflow run to appear and complete
            for (var poll = 0; poll < _ciPollMaxAttempts; poll++)
            {
                await Task.Delay(_ciPollIntervalMs);

                var runsResponse = await httpClient.GetAsync(
                    $"/repos/{repository}/actions/runs?branch={branch}&per_page=1");
                runsResponse.EnsureSuccessStatusCode();

                var runs = await runsResponse.Content.ReadFromJsonAsync<JsonElement>();
                var workflowRuns = runs.GetProperty("workflow_runs");

                if (workflowRuns.GetArrayLength() == 0)
                {
                    _logger.LogDebug(
                        "No workflow runs found yet for {Repo}/{Branch}, poll {Poll}/{Max}",
                        repository, branch, poll + 1, _ciPollMaxAttempts);
                    continue;
                }

                var run = workflowRuns[0];
                var status = run.GetProperty("status").GetString() ?? "Unknown";

                // Return immediately if the run has reached a terminal state
                if (status == "completed")
                {
                    var result = new TestRunResult
                    {
                        RunId = run.GetProperty("id").GetInt64().ToString(),
                        Status = run.GetProperty("conclusion").GetString() ?? status,
                        TotalTests = 0
                    };
                    return IntegrationResult<TestRunResult>.Ok(result);
                }

                // Return the in-progress run on the last poll attempt
                if (poll == _ciPollMaxAttempts - 1)
                {
                    var result = new TestRunResult
                    {
                        RunId = run.GetProperty("id").GetInt64().ToString(),
                        Status = status,
                        TotalTests = 0
                    };
                    return IntegrationResult<TestRunResult>.Ok(result);
                }
            }

            return IntegrationResult<TestRunResult>.Ok(new TestRunResult
            {
                RunId = "unknown",
                Status = "NotFound",
                TotalTests = 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger tests for {Repo}/{Branch}", repository, branch);
            return IntegrationResult<TestRunResult>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<BuildStatus>> GetBuildStatusAsync(string repository, string branch)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            var response = await httpClient.GetAsync(
                $"/repos/{repository}/actions/runs?branch={branch}&per_page=1");
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            var runs = data.GetProperty("workflow_runs");

            if (runs.GetArrayLength() == 0)
            {
                return IntegrationResult<BuildStatus>.Ok(new BuildStatus { Status = "NoRuns" });
            }

            var run = runs[0];
            var result = new BuildStatus
            {
                Status = run.GetProperty("conclusion").ValueKind == JsonValueKind.Null
                    ? run.GetProperty("status").GetString() ?? "Unknown"
                    : run.GetProperty("conclusion").GetString() ?? "Unknown",
                BuildUrl = run.GetProperty("html_url").GetString() ?? "",
                StartedAt = run.TryGetProperty("run_started_at", out var started)
                    ? started.GetDateTime() : null,
                FinishedAt = run.TryGetProperty("updated_at", out var finished)
                    ? finished.GetDateTime() : null
            };
            return IntegrationResult<BuildStatus>.Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get build status for {Repo}/{Branch}", repository, branch);
            return IntegrationResult<BuildStatus>.Fail(ex.Message);
        }
    }
}
