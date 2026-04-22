namespace Tamma.Api.Dtos.Prompts;

public record UpsertPromptRequest(string Template, string? SystemPrompt, string[]? Variables, bool? EnableTools, int? MaxTokens);
public record RenderPromptRequest(Dictionary<string, string> Variables);
public record PromptResponse(string? Role, string? Action, string Template, string? SystemPrompt, string[]? Variables, bool EnableTools, int MaxTokens, string Source);

/// <summary>
/// Render-endpoint response. Field shape matches the TS <c>RenderedPrompt</c>
/// interface (port-gap audit prompts/003): role/action/version are echoed back
/// alongside the rendered prompt halves so callers can route, budget, and
/// detect stale snapshots without an extra GET.
/// </summary>
/// <param name="Role">The role the render was scoped to.</param>
/// <param name="Action">The action the render was scoped to.</param>
/// <param name="Version">The override version (1 for system defaults / unversioned overrides).</param>
/// <param name="RenderedTemplate">The interpolated user-prompt body.</param>
/// <param name="RenderedSystemPrompt">The interpolated system-prompt preamble.</param>
/// <param name="EnableTools">Whether tool use is enabled for this prompt.</param>
/// <param name="MaxTokens">Maximum response tokens for this prompt.</param>
/// <param name="UnresolvedVariables">Variables referenced by the template but not provided.</param>
public record RenderedPromptResponse(
    string Role,
    string Action,
    int Version,
    string RenderedTemplate,
    string RenderedSystemPrompt,
    bool EnableTools,
    int MaxTokens,
    string[] UnresolvedVariables);

/// <summary>
/// Bulk payload returned by <c>GET /api/prompts/system</c>. Exposes every layer
/// of the system-shipped prompt registry.
/// </summary>
public record SystemDefaultsResponse(
    IReadOnlyList<PromptResponse> RoleActionTemplates,
    IReadOnlyDictionary<string, string> SystemPrompts,
    IReadOnlyDictionary<string, PromptResponse> ActionDefaults);
