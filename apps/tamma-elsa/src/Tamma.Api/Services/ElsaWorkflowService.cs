using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Tamma.Api.Services;

/// <summary>
/// Implementation of ELSA workflow service.
/// Connects to ELSA v3 server via REST API to manage workflow instances.
/// </summary>
public partial class ElsaWorkflowService : IElsaWorkflowService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ElsaWorkflowService> _logger;
    private readonly string _elsaServerUrl;
    private static volatile bool _healthChecked;
    private static readonly object _healthCheckLock = new();

    /// <summary>
    /// Matches any control character (CR, LF, TAB, and all C0/C1 control chars).
    /// Used to prevent log forging by stripping characters that could inject
    /// fake log entries.
    /// </summary>
    [GeneratedRegex(@"[\x00-\x1F\x7F-\x9F]", RegexOptions.Compiled)]
    private static partial Regex ControlCharsRegex();

    /// <summary>
    /// Sanitize a string for safe logging by removing all control characters
    /// in a single atomic pass to prevent log forging.
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        return ControlCharsRegex().Replace(input, "");
    }

    public ElsaWorkflowService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ElsaWorkflowService> logger)
    {
        _logger = logger;
        _elsaServerUrl = configuration["Elsa:ServerUrl"] ?? "http://localhost:5000";
        _httpClient = httpClientFactory.CreateClient("elsa");
    }

    /// <summary>
    /// Ensure the ELSA server is reachable before making calls.
    /// </summary>
    private async Task EnsureHealthyAsync()
    {
        if (_healthChecked) return;

        const int maxRetries = 5;
        const int delayMs = 2000;

        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                var response = await _httpClient.GetAsync("/elsa/api/health");
                if (response.IsSuccessStatusCode)
                {
                    lock (_healthCheckLock)
                    {
                        _healthChecked = true;
                    }
                    _logger.LogInformation("ELSA server health check passed at {Url}", _elsaServerUrl);
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "ELSA health check attempt {Attempt}/{Max} failed", i + 1, maxRetries);
            }

            if (i < maxRetries - 1)
            {
                await Task.Delay(delayMs);
            }
        }

        throw new InvalidOperationException(
            $"ELSA server at {_elsaServerUrl} is not reachable after {maxRetries} attempts");
    }

    /// <summary>
    /// Start a new workflow instance by definition name.
    /// </summary>
    public async Task<string> StartWorkflowAsync(string workflowName, Dictionary<string, object> input)
    {
        _logger.LogInformation("Starting workflow {WorkflowName} with input: {@Input}", SanitizeForLog(workflowName), input);

        await EnsureHealthyAsync();

        try
        {
            var payload = new { input };
            var response = await _httpClient.PostAsJsonAsync(
                $"/elsa/api/workflow-definitions/{workflowName}/execute",
                payload);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WorkflowExecutionResult>(JsonOptions);
            var instanceId = result?.WorkflowInstanceId
                ?? throw new InvalidOperationException("ELSA returned null workflow instance ID");

            _logger.LogInformation("Started workflow instance {InstanceId}", instanceId);
            return instanceId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start workflow {WorkflowName}", workflowName);
            throw;
        }
    }

    /// <summary>
    /// Pause (suspend) a running workflow.
    /// </summary>
    public async Task PauseWorkflowAsync(string instanceId)
    {
        _logger.LogInformation("Pausing workflow instance {InstanceId}", instanceId);

        await EnsureHealthyAsync();

        try
        {
            var response = await _httpClient.PostAsync(
                $"/elsa/api/workflow-instances/{instanceId}/suspend", null);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Paused workflow instance {InstanceId}", instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pause workflow {InstanceId}", instanceId);
            throw;
        }
    }

    /// <summary>
    /// Resume a paused workflow.
    /// </summary>
    public async Task ResumeWorkflowAsync(string instanceId)
    {
        _logger.LogInformation("Resuming workflow instance {InstanceId}", instanceId);

        await EnsureHealthyAsync();

        try
        {
            var response = await _httpClient.PostAsync(
                $"/elsa/api/workflow-instances/{instanceId}/resume", null);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Resumed workflow instance {InstanceId}", instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume workflow {InstanceId}", instanceId);
            throw;
        }
    }

    /// <summary>
    /// Cancel a running workflow.
    /// </summary>
    public async Task CancelWorkflowAsync(string instanceId)
    {
        _logger.LogInformation("Cancelling workflow instance {InstanceId}", instanceId);

        await EnsureHealthyAsync();

        try
        {
            var response = await _httpClient.DeleteAsync(
                $"/elsa/api/workflow-instances/{instanceId}/cancel");
            response.EnsureSuccessStatusCode();

            _logger.LogInformation("Cancelled workflow instance {InstanceId}", instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel workflow {InstanceId}", instanceId);
            throw;
        }
    }

    /// <summary>
    /// Get workflow instance status.
    /// </summary>
    public async Task<WorkflowStatus> GetWorkflowStatusAsync(string instanceId)
    {
        _logger.LogDebug("Getting status for workflow instance {InstanceId}", instanceId);

        await EnsureHealthyAsync();

        try
        {
            var response = await _httpClient.GetAsync(
                $"/elsa/api/workflow-instances/{instanceId}");
            response.EnsureSuccessStatusCode();

            var instance = await response.Content.ReadFromJsonAsync<ElsaWorkflowInstance>(JsonOptions);

            return new WorkflowStatus
            {
                InstanceId = instance?.Id ?? instanceId,
                WorkflowName = instance?.DefinitionId ?? "unknown",
                Status = instance?.Status ?? "Unknown",
                CurrentActivity = instance?.CurrentActivity,
                StartedAt = instance?.CreatedAt,
                CompletedAt = instance?.FinishedAt,
                Variables = instance?.Variables ?? new Dictionary<string, object>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get status for workflow {InstanceId}", instanceId);
            throw;
        }
    }

    /// <summary>
    /// Send a signal to a workflow instance.
    /// </summary>
    public async Task SendSignalAsync(string instanceId, string signalName, object? payload = null)
    {
        _logger.LogInformation(
            "Sending signal {SignalName} to workflow instance {InstanceId}",
            signalName, instanceId);

        await EnsureHealthyAsync();

        try
        {
            var body = new { input = payload };
            var response = await _httpClient.PostAsJsonAsync(
                $"/elsa/api/signals/{signalName}/execute",
                body);
            response.EnsureSuccessStatusCode();

            _logger.LogInformation(
                "Sent signal {SignalName} to workflow instance {InstanceId}",
                signalName, instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send signal {SignalName} to workflow {InstanceId}",
                signalName, instanceId);
            throw;
        }
    }

    /// <summary>
    /// IMPORTANT-2 — forward a merge-approval gate resume to the engine's
    /// in-process resume endpoint, which looks up the
    /// <c>adl-merge-approval-{issue}-{pr}</c> bookmark and runs the owning
    /// instance with <c>{decision, feedback, approver}</c> injected as input.
    /// A 404 from the engine (no gate waiting) is surfaced as
    /// <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown,
    /// so the controller can map it to a 404 for the caller.
    /// </summary>
    public async Task<MergeApprovalResumeResult> ResumeMergeApprovalAsync(
        int issueNumber, int prNumber, string decision, string? feedback, string? approver)
    {
        _logger.LogInformation(
            "Resuming merge-approval gate for issue #{Issue} PR #{Pr} (decision={Decision})",
            issueNumber, prNumber, SanitizeForLog(decision));

        await EnsureHealthyAsync();

        var payload = new
        {
            issueNumber,
            prNumber,
            decision,
            feedback,
            approver,
        };

        var response = await _httpClient.PostAsJsonAsync(
            "/elsa/api/adl/merge-approval/resume", payload, JsonOptions);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "No merge-approval gate waiting for issue #{Issue} PR #{Pr}", issueNumber, prNumber);
            return new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null);
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EngineResumeResponse>(JsonOptions);
        return new MergeApprovalResumeResult(
            Resumed: result?.Resumed ?? true,
            GateNotFound: false,
            WorkflowInstanceId: result?.WorkflowInstanceId);
    }

    private sealed class EngineResumeResponse
    {
        public bool Resumed { get; set; }
        public string? WorkflowInstanceId { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Response from ELSA workflow execution endpoint.
/// </summary>
public class WorkflowExecutionResult
{
    public string WorkflowInstanceId { get; set; } = string.Empty;
}

/// <summary>
/// ELSA workflow instance model (subset of fields we use).
/// </summary>
internal class ElsaWorkflowInstance
{
    public string Id { get; set; } = string.Empty;
    public string DefinitionId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? CurrentActivity { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public Dictionary<string, object> Variables { get; set; } = new();
}
