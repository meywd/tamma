using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch.Models;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Story 38-2 (Class-C cutover) — the thin <see cref="IAgentDispatchService"/>.
/// It no longer injects the co-hosted, credential-holding
/// <see cref="IGitHubActionsClient"/>; it composes the dispatch inputs engine-side
/// (pure, token-free) and delegates the actual <c>workflow_dispatch</c> — including
/// the workflow-file check + 429/5xx retry — to <c>Tamma.Api</c> over the wire via
/// <see cref="TammaApiClient.DispatchAgentRunAsync"/>, where the per-repo GitHub App
/// installation token lives, the tenant↔repo guard runs, and the audit event is
/// emitted. The engine holds no Actions token.
/// </summary>
public sealed class AgentDispatchService : IAgentDispatchService
{
    private readonly TammaApiClient _api;
    private readonly ILogger<AgentDispatchService>? _logger;

    public AgentDispatchService(
        TammaApiClient api,
        ILogger<AgentDispatchService>? logger = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _logger = logger;
    }

    public async Task<AgentDispatchResult> DispatchAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Repo-format validation stays engine-side (pure, no platform call) so a
        // malformed request never reaches the API — matches the pre-cutover behavior.
        if (!TryParseRepository(request.Repository))
        {
            return new AgentDispatchResult(
                Success: false,
                WorkflowRunUrl: null,
                ErrorMessage: $"Invalid repository format '{request.Repository}' (expected 'owner/repo')",
                DispatchedAt: DateTime.UtcNow);
        }

        var workflowFile = string.IsNullOrWhiteSpace(request.WorkflowFileName)
            ? "tamma-agent.yml"
            : request.WorkflowFileName!;

        var apiRequest = new AgentDispatchRunApiRequest
        {
            WorkflowFileName = workflowFile,
            Ref = request.BranchName,
            Inputs = BuildDispatchInputs(request),
            CorrelationId = request.SessionId,
        };

        var response = await _api.DispatchAgentRunAsync(
            request.Repository, apiRequest, request.TenantId?.ToString(), cancellationToken).ConfigureAwait(false);

        return MapResponse(response);
    }

    /// <summary>
    /// Story 38-2 (AC5) — pure map of the mediated wire response → the
    /// <see cref="AgentDispatchResult"/> the surrounding workflow consumes. A null
    /// response (guard 403 / auth 401 / transport 5xx nulled the body) fails closed
    /// with a mediation-unavailable message so <c>DispatchAgentWorkflowActivity</c>
    /// routes to its Failed outcome exactly as it did on a platform failure today.
    /// </summary>
    public static AgentDispatchResult MapResponse(AgentDispatchRunApiResponse? response)
    {
        if (response is null)
        {
            return new AgentDispatchResult(
                Success: false,
                WorkflowRunUrl: null,
                ErrorMessage: "agent-dispatch mediation unavailable",
                DispatchedAt: DateTime.UtcNow);
        }

        return new AgentDispatchResult(
            Success: response.Success,
            WorkflowRunUrl: response.WorkflowRunUrl,
            ErrorMessage: response.Success ? null : response.FailureReason,
            DispatchedAt: response.DispatchedAt == default ? DateTime.UtcNow : response.DispatchedAt);
    }

    private static Dictionary<string, string> BuildDispatchInputs(AgentExecutionRequest request)
    {
        // GitHub workflow_dispatch inputs are string-only and capped at 10 entries /
        // 65535 chars each. Composed engine-side (pure, token-free); the API forwards
        // them verbatim to the platform with the resolved installation token.
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["issue_number"] = request.IssueNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["task"] = request.Task ?? "implement",
            ["plan_json"] = request.PlanJson ?? "{}",
            ["branch_name"] = request.BranchName,
            ["tamma_session_id"] = request.SessionId,
            ["agent_provider"] = request.AgentProvider ?? "claude-code",
            ["agent_config_json"] = request.AgentConfigJson ?? "{}"
        };
    }

    private static bool TryParseRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return false;
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]);
    }
}
