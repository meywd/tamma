namespace Tamma.Api.Services.Engine;

/// <summary>
/// Outcome of an execute-task call. Mirrors the deleted TS
/// <c>ExecuteTaskResponse</c> shape (audit finding 001).
/// </summary>
/// <param name="Success">Whether the underlying agent / LLM call succeeded.</param>
/// <param name="Output">
/// LLM-generated output text. The 11 deployed Elsa activities call
/// <c>result.GetProperty("output").GetString()</c>; this MUST be present
/// (string.Empty if no output) on every response, even error paths.
/// </param>
/// <param name="TokensUsed">Total tokens consumed (prompt + completion).</param>
/// <param name="CostUsd">Estimated cost in USD.</param>
/// <param name="DurationMs">Wall-clock execution time in milliseconds.</param>
/// <param name="ToolCalls">Number of tool/function invocations during the run.</param>
/// <param name="Error">Short error description on failure paths; null on success.</param>
public sealed record ExecuteTaskResult(
    bool Success,
    string Output,
    int TokensUsed,
    decimal CostUsd,
    long DurationMs,
    int ToolCalls,
    string? Error);

/// <summary>
/// Inputs for <see cref="IExecuteTaskService.ExecuteAsync"/>. Maps the
/// deployed Elsa activity payloads <c>{prompt, role, analysisType?}</c>.
/// </summary>
public sealed record ExecuteTaskInput(
    string Prompt,
    string? Role,
    string? AnalysisType,
    string? Repository,
    bool? EnableTools,
    string? Model,
    double? MaxBudgetUsd,
    string? Cwd);

/// <summary>
/// Bridge between the engine callback HTTP surface
/// (<c>POST /api/engine/execute-task</c>) and the underlying agent / LLM
/// provider stack.
///
/// <para>Audit finding 001 (P0): the original C# port stubbed this endpoint;
/// every LLM-driven Elsa activity (TDD red/green/refactor, Debug
/// hypothesis refinement, ADL review fixes, mentorship guidance, Claude
/// analysis) calls into it and crashes on the missing <c>output</c>
/// field. This service is the short-term bridge: it delegates to
/// <c>ILlmProxyService</c> (already used by the SaaS lane) so the
/// activities at least receive a non-stub <c>{output, tokensUsed,
/// costUsd, durationMs, toolCalls}</c> payload.</para>
///
/// <para>The full role-based provider chain that TS resolved via
/// <c>IRoleBasedAgentResolver</c> is NOT yet ported. Once the
/// <c>@tamma/providers</c> equivalent lands in C# this service should
/// route per role with provider chain fallback.
/// TODO(epic-1/story-1-10): real role-based agent resolution.</para>
/// </summary>
public interface IExecuteTaskService
{
    Task<ExecuteTaskResult> ExecuteAsync(
        ExecuteTaskInput input,
        Guid? tenantId,
        CancellationToken ct = default);
}
