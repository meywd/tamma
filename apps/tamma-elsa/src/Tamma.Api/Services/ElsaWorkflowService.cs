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
    /// Keys whose VALUES must never be logged. Matched case-insensitively
    /// against the substring patterns in <see cref="SensitiveKeyFragments"/>.
    /// <c>RotationTriggerService</c> puts the operator's <c>newPlaintext</c>
    /// into the dispatch dict; this is the only caller that places secret
    /// material in <c>input</c>, but the match is defensive (covers any
    /// future caller) so a fresh dispatch surface can't reintroduce the leak.
    /// </summary>
    private static readonly string[] SensitiveKeyFragments =
    {
        "plaintext", "secret", "password", "token", "apikey", "api_key", "credential",
    };

    private static bool IsSensitiveKey(string key)
    {
        foreach (var fragment in SensitiveKeyFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Render the dispatch input's KEY SET for safe logging — keys only,
    /// never values, and a sensitive key is rendered as <c>name=[redacted]</c>
    /// so its presence is auditable without leaking the value. The control
    /// chars in each key are stripped to prevent log forging.
    /// </summary>
    private static string DescribeInputKeys(Dictionary<string, object>? input)
    {
        if (input is null || input.Count == 0) return "(none)";
        var parts = new List<string>(input.Count);
        foreach (var key in input.Keys)
        {
            var safeKey = SanitizeForLog(key);
            parts.Add(IsSensitiveKey(key) ? $"{safeKey}=[redacted]" : safeKey);
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Start a new workflow instance by definition name.
    /// </summary>
    public async Task<string> StartWorkflowAsync(string workflowName, Dictionary<string, object> input)
    {
        // SECURITY — do NOT destructure the dispatch input ({@Input}). The
        // rotate-secret dispatch carries the operator's new secret plaintext
        // under the `newPlaintext` key; destructuring logged it in cleartext
        // at Information level. Log the workflow name + the input KEY SET only
        // (keys never values; sensitive keys shown as name=[redacted]). This
        // method is shared (MentorshipController is the other caller) so the
        // key-only contract is safe for every caller.
        _logger.LogInformation(
            "Starting workflow {WorkflowName} with input keys: {InputKeys}",
            SanitizeForLog(workflowName), DescribeInputKeys(input));

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
    /// <remarks>
    /// This hits Elsa's GENERIC <c>/resume</c> with no bookmark id and no input, so it cannot
    /// supply the <c>ProgressDetected</c> / <c>Resolved</c> / <c>SeniorResponse</c> keys the
    /// blocker-diagnosis progress / escalation bookmarks read. To reach the blocker
    /// <c>Resolved</c> terminal use the blocker-specific
    /// <see cref="ResumeBlockerResolutionAsync"/> (follow-up #15) instead — it targets the
    /// session-scoped bookmark and injects that input, mirroring the secure
    /// MergeApprovalResumeEndpoint.
    /// </remarks>
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
    /// in-process resume endpoint, which looks up the tenant+repo-scoped
    /// <c>adl-merge-approval-{tenant}-{repo}-{issue}-{pr}</c> bookmark and runs the
    /// owning instance with <c>{decision, feedback, approver}</c> injected as input.
    /// A 404 from the engine (no gate waiting) is surfaced as
    /// <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown,
    /// so the controller can map it to a 404 for the caller.
    /// </summary>
    public async Task<MergeApprovalResumeResult> ResumeMergeApprovalAsync(
        int issueNumber, int prNumber, string? tenantId, string? repository,
        string decision, string? feedback, string? approver)
    {
        _logger.LogInformation(
            "Resuming merge-approval gate for issue #{Issue} PR #{Pr} (decision={Decision})",
            issueNumber, prNumber, SanitizeForLog(decision));

        await EnsureHealthyAsync();

        var payload = new
        {
            issueNumber,
            prNumber,
            // SECURITY C1/C2 — tenant + repo scope the engine's bookmark lookup.
            tenantId,
            repository,
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

    /// <summary>
    /// Completeness audit P0 item 3 — forward a deployment-pipeline
    /// production-approval gate resume to the engine's in-process resume endpoint,
    /// which looks up the tenant+repo+SHA-scoped
    /// <c>adl-deploy-prod-approval-{tenant}-{repo}-{issue}-{mergeSha}</c> bookmark
    /// and runs the owning instance with <c>{decision, feedback, approver}</c>
    /// injected as input. A 404 (no gate waiting) is surfaced as
    /// <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown.
    /// </summary>
    public async Task<MergeApprovalResumeResult> ResumeDeploymentApprovalAsync(
        int issueNumber, string? tenantId, string? repository, string? mergeSha,
        string decision, string? feedback, string? approver)
    {
        _logger.LogInformation(
            "Resuming deploy-approval gate for issue #{Issue} (decision={Decision})",
            issueNumber, SanitizeForLog(decision));

        await EnsureHealthyAsync();

        var payload = new
        {
            issueNumber,
            // Tenant + repo + sha scope the engine's bookmark lookup.
            tenantId,
            repository,
            mergeSha,
            decision,
            feedback,
            approver,
        };

        var response = await _httpClient.PostAsJsonAsync(
            "/elsa/api/adl/deploy-approval/resume", payload, JsonOptions);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "No deploy-approval gate waiting for issue #{Issue}", issueNumber);
            return new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null);
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EngineResumeResponse>(JsonOptions);
        return new MergeApprovalResumeResult(
            Resumed: result?.Resumed ?? true,
            GateNotFound: false,
            WorkflowInstanceId: result?.WorkflowInstanceId);
    }

    /// <summary>
    /// Follow-up #15 — forward a blocker-diagnosis ladder resume to the engine's in-process
    /// resume endpoint, which looks up the session-scoped progress
    /// (<c>blocker-progress-{session}-{level}</c>) or escalation
    /// (<c>blocker-escalation-{session}</c>) bookmark and runs the owning instance with the
    /// payload injected as input. A 404 (no wait suspended) is surfaced as
    /// <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown, so the
    /// controller can map it to a 404 for the caller.
    /// </summary>
    public async Task<MergeApprovalResumeResult> ResumeBlockerResolutionAsync(
        Guid sessionId, string kind, string? level, bool resolved,
        string? progressType, string? details, string? seniorResponse, string? resolver)
    {
        _logger.LogInformation(
            "Resuming blocker gate for session {SessionId} (kind={Kind}, level={Level})",
            sessionId, SanitizeForLog(kind), SanitizeForLog(level));

        await EnsureHealthyAsync();

        var payload = new
        {
            sessionId,
            kind,
            level,
            resolved,
            progressType,
            details,
            seniorResponse,
            // I2 — server-derived acting identity, forwarded for the engine's audit log.
            resolver,
        };

        var response = await _httpClient.PostAsJsonAsync(
            "/elsa/api/adl/blocker/resume", payload, JsonOptions);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "No blocker gate waiting for session {SessionId} (kind={Kind}, level={Level})",
                sessionId, SanitizeForLog(kind), SanitizeForLog(level));
            return new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null);
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EngineResumeResponse>(JsonOptions);
        return new MergeApprovalResumeResult(
            Resumed: result?.Resumed ?? true,
            GateNotFound: false,
            WorkflowInstanceId: result?.WorkflowInstanceId);
    }

    /// <summary>
    /// Story 3.5 — forward a clarifying-questions answer-gate resume to the engine's
    /// in-process resume endpoint, which looks up the tenant+session-scoped
    /// <c>clarify-answers-{tenant}-{session}</c> bookmark and runs the owning instance with
    /// <c>{Answered, Answers}</c> injected as input. A 404 (no gate waiting) is surfaced as
    /// <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown.
    /// </summary>
    public async Task<MergeApprovalResumeResult> ResumeClarifyingQuestionsAsync(
        Guid sessionId, string? tenantId, string answers, string? resolver)
    {
        _logger.LogInformation(
            "Resuming clarify gate for session {SessionId}", sessionId);

        await EnsureHealthyAsync();

        var payload = new
        {
            sessionId,
            // Tenant scopes the engine's bookmark lookup (folded into the name).
            tenantId,
            answers,
            // I2 — server-derived acting identity, forwarded for the engine's audit log.
            resolver,
        };

        var response = await _httpClient.PostAsJsonAsync(
            "/elsa/api/adl/clarify/resume", payload, JsonOptions);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "No clarify gate waiting for session {SessionId}", sessionId);
            return new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null);
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EngineResumeResponse>(JsonOptions);
        return new MergeApprovalResumeResult(
            Resumed: result?.Resumed ?? true,
            GateNotFound: false,
            WorkflowInstanceId: result?.WorkflowInstanceId);
    }

    /// <summary>
    /// Story 3.7 — forward a design-proposal review-gate resume to the engine's in-process
    /// resume endpoint, which looks up the tenant+session-scoped
    /// <c>design-approval-{tenant}-{session}</c> bookmark and runs the owning instance with
    /// <c>{Approved, Feedback}</c> injected as input. A 404 (no gate waiting) is surfaced as
    /// <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown.
    /// </summary>
    public async Task<MergeApprovalResumeResult> ResumeDesignApprovalAsync(
        Guid sessionId, string? tenantId, bool approved, string? feedback, string? reviewer)
    {
        _logger.LogInformation(
            "Resuming design gate for session {SessionId} (approved={Approved})", sessionId, approved);

        await EnsureHealthyAsync();

        var payload = new
        {
            sessionId,
            // Tenant scopes the engine's bookmark lookup (folded into the name).
            tenantId,
            approved,
            feedback,
            // I2 — server-derived acting identity, forwarded for the engine's audit log.
            reviewer,
        };

        var response = await _httpClient.PostAsJsonAsync(
            "/elsa/api/adl/design/resume", payload, JsonOptions);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "No design gate waiting for session {SessionId}", sessionId);
            return new MergeApprovalResumeResult(Resumed: false, GateNotFound: true, WorkflowInstanceId: null);
        }

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EngineResumeResponse>(JsonOptions);
        return new MergeApprovalResumeResult(
            Resumed: result?.Resumed ?? true,
            GateNotFound: false,
            WorkflowInstanceId: result?.WorkflowInstanceId);
    }

    /// <summary>
    /// Story 39-8 — forward a document-decision gate resume to the engine's in-process resume
    /// endpoint, which looks up the tenant+session-scoped
    /// <c>document-decision-{tenant}-{session}</c> bookmark and runs the owning instance with
    /// the decision payload injected. A 404 (no gate waiting) is surfaced as
    /// <see cref="MergeApprovalResumeResult.GateNotFound"/> rather than thrown.
    /// </summary>
    public async Task<MergeApprovalResumeResult> ResumeDocumentDecisionAsync(
        Guid sessionId, string? tenantId, string decisionJson, string? feedback,
        string? deciderId, string? deciderDisplay, string channel, string? rulesReference)
    {
        _logger.LogInformation(
            "Resuming document-decision gate for session {SessionId} (channel={Channel})", sessionId, channel);

        await EnsureHealthyAsync();

        var payload = new
        {
            sessionId,
            // Tenant scopes the engine's bookmark lookup (folded into the name).
            tenantId,
            decisionJson,
            feedback,
            // Server-derived identity + channel (D6/D7), forwarded for the engine's audit log.
            deciderId,
            deciderDisplay,
            channel,
            rulesReference,
        };

        var response = await _httpClient.PostAsJsonAsync(
            "/elsa/api/documents/decision/resume", payload, JsonOptions);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "No document-decision gate waiting for session {SessionId}", sessionId);
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
