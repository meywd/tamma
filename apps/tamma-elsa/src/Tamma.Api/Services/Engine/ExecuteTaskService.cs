using System.Diagnostics;
using Tamma.Api.Services.SaaS;

namespace Tamma.Api.Services.Engine;

/// <summary>
/// Concrete <see cref="IExecuteTaskService"/> that delegates to
/// <see cref="ILlmProxyService"/>.
///
/// <para>Audit finding 001 — the deleted TS endpoint resolved a per-role
/// agent via <c>IRoleBasedAgentResolver</c> with the full provider-chain
/// fallback. That layer is not yet ported. This implementation is the
/// short-term bridge: it builds a single Anthropic chat call with a
/// system prompt derived from the role, delegates to the existing LLM
/// proxy (already wired with diagnostics + per-tenant budget enforcement),
/// and returns the documented response shape so the deployed Elsa
/// activities can read <c>output</c> without crashing.</para>
///
/// <para>Limitations vs. TS:
/// <list type="bullet">
///   <item>No tool-loop. <c>EnableTools</c> is accepted but ignored; the
///         <c>ToolCalls</c> field on the response is always zero. Real
///         tool execution depends on the missing
///         <c>IRoleBasedAgentResolver</c> port.</item>
///   <item>No per-call max-budget cap. <c>MaxBudgetUsd</c> is accepted
///         but the only enforcement is the existing per-tenant aggregate
///         budget the LLM proxy already checks.</item>
///   <item>No CWD / repository-aware context. <c>Cwd</c> and
///         <c>Repository</c> are accepted for forward compatibility but
///         not consumed.</item>
/// </list>
/// </para>
///
/// <para>TODO(epic-1/story-1-10): replace with real role-based agent
/// resolution + tool-loop once <c>@tamma/providers</c> ports.
/// Requires running Elsa engine for E2E coverage.</para>
/// </summary>
public sealed class ExecuteTaskService : IExecuteTaskService
{
    private readonly ILlmProxyService _llmProxy;
    private readonly ILogger<ExecuteTaskService> _logger;

    public ExecuteTaskService(
        ILlmProxyService llmProxy,
        ILogger<ExecuteTaskService> logger)
    {
        _llmProxy = llmProxy;
        _logger = logger;
    }

    public async Task<ExecuteTaskResult> ExecuteAsync(
        ExecuteTaskInput input,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (string.IsNullOrWhiteSpace(input.Prompt))
        {
            return new ExecuteTaskResult(
                Success: false,
                Output: string.Empty,
                TokensUsed: 0,
                CostUsd: 0m,
                DurationMs: 0,
                ToolCalls: 0,
                Error: "prompt is required");
        }

        var systemPrompt = BuildSystemPromptForRole(input.Role, input.AnalysisType);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(new ChatMessage("system", systemPrompt));
        messages.Add(new ChatMessage("user", input.Prompt));

        var sw = Stopwatch.StartNew();
        var request = new ChatRequest(
            Model: input.Model,
            Messages: messages,
            MaxTokens: 4096,
            Temperature: null);

        var response = await _llmProxy.ChatAsync(request, tenantId, ct);
        sw.Stop();

        if (!response.Success)
        {
            _logger.LogWarning(
                "execute-task: LLM proxy failed (reason={Reason}, role={Role})",
                response.ErrorReason, input.Role);
            return new ExecuteTaskResult(
                Success: false,
                Output: string.Empty,
                TokensUsed: 0,
                CostUsd: 0m,
                DurationMs: sw.ElapsedMilliseconds,
                ToolCalls: 0,
                Error: response.ErrorReason ?? "upstream_error");
        }

        return new ExecuteTaskResult(
            Success: true,
            Output: response.Text ?? string.Empty,
            TokensUsed: response.TotalTokens,
            CostUsd: response.CostUsd,
            DurationMs: sw.ElapsedMilliseconds,
            ToolCalls: 0,
            Error: null);
    }

    /// <summary>
    /// Build a minimal role-aware system prompt. This is a placeholder until
    /// the <c>PromptStore</c> per-role/per-action lookup is wired into the
    /// execute-task path. Each role currently maps to a one-line behaviour
    /// guide; <c>analysisType</c> further specialises tester/debugger roles.
    /// </summary>
    private static string? BuildSystemPromptForRole(string? role, string? analysisType)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;

        var basePrompt = role.ToLowerInvariant() switch
        {
            "implementer" => "You are an implementer agent. Generate production-quality code that satisfies the user's task.",
            "tester" => "You are a tester agent. Write thorough, well-named tests that exercise the described behaviour.",
            "debugger" => "You are a debugger agent. Analyse the failure, refine hypotheses, and propose targeted diagnostics.",
            "reviewer" => "You are a reviewer agent. Surface correctness, security, and maintainability concerns.",
            "scrum-master" or "scrum_master" or "scrummaster" =>
                "You are a scrum-master agent. Triage the work item and decide next steps.",
            "mentor" => "You are a mentor agent. Provide pedagogical guidance, not direct answers.",
            _ => $"You are a {role} agent."
        };

        if (!string.IsNullOrWhiteSpace(analysisType))
        {
            basePrompt += $" Analysis type: {analysisType}.";
        }

        return basePrompt;
    }
}
