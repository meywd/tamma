namespace Tamma.Api.Dtos.Agents;

public record UpdateAgentConfigRequest(object Config);
public record ValidateConfigRequest(object Config);

/// <summary>
/// Per-task scope-down overrides for <see cref="ResolveForPhaseRequest"/>.
/// Each field is individually optional; clamping rules are applied by the
/// resolver (ceiling is role-level, overrides can only restrict). Finding 007.
/// </summary>
/// <param name="MaxBudgetUsd">Upper cap on USD spend. Clamped to
/// <c>min(taskOverride, role)</c>.</param>
/// <param name="AllowedTools">Per-task tool whitelist. Intersected with the
/// role's tool list — new tools cannot be added.</param>
/// <param name="PermissionMode">One of <c>default</c>, <c>acceptEdits</c>,
/// <c>bypassPermissions</c>. <c>bypassPermissions</c> requires the operator
/// env-var <c>TAMMA_ALLOW_BYPASS_PERMISSIONS=true</c>.</param>
/// <param name="Model">Optional per-task model override.</param>
public record TaskOverrides(
    decimal? MaxBudgetUsd = null,
    IReadOnlyList<string>? AllowedTools = null,
    string? PermissionMode = null,
    string? Model = null);

/// <summary>
/// Resolve-for-phase request body. Accepts either <c>role</c> (new shape)
/// or <c>taskType</c> (legacy). One must be provided.
/// </summary>
public record ResolveForPhaseRequest(
    string Phase,
    string TaskType = "",
    string? Role = null,
    TaskOverrides? TaskOverrides = null);

public record AgentConfigResponse(object Config, string Source, int Version);
public record ResolvedAgentResponse(string Provider, string Model, object Config);
