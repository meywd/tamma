using System.Text.Json;

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

// ── Story 32-1 — first-class agent entity DTOs ──
// Distinct from the legacy agent_configs DTOs above; the new /api/v1/agents
// surface manages identity-bearing, versioned agent definitions.

/// <summary>
/// Create-agent request. <c>Visibility</c> is <c>"public"</c> or
/// <c>"private"</c>; the owner columns are derived server-side from the process
/// mode (SaaS → tenant; single-user → user). <c>Config</c> is the saved-config
/// snapshot validated before any write.
/// </summary>
public sealed record CreateAgentRequest(
    string Name,
    string Role,
    string Visibility,
    JsonElement Config,
    string? Notes);

/// <summary>Publish a new immutable version of an existing agent.</summary>
public sealed record PublishVersionRequest(
    JsonElement Config,
    string? Notes);

/// <summary>List/summary projection of an <c>Agent</c>.</summary>
/// <remarks>
/// Story 32-18 — <see cref="Enabled"/> is the per-tenant enablement flag for a
/// public persona (own-private agents are implicitly enabled). It is
/// <c>null</c> on the default member listing (which already filters to
/// <c>enabled(public) ∪ own-private</c>, so a flag is redundant) and is set only
/// on the admin <c>?includeDisabled=true</c> view, where the full catalog is
/// returned and the flag tells the admin what they could enable.
/// </remarks>
public sealed record AgentSummary(
    Guid Id,
    string Name,
    string? Role,
    string Visibility,
    string Status,
    Guid? OwnerTenantId,
    Guid? OwnerUserId,
    Guid? CurrentVersionId,
    bool? Enabled = null);

/// <summary>Full agent detail (summary + version list).</summary>
public sealed record AgentDetail(
    Guid Id,
    string Name,
    string? Role,
    string Visibility,
    string Status,
    Guid? OwnerTenantId,
    Guid? OwnerUserId,
    Guid? CurrentVersionId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<AgentVersionSummary> Versions);

/// <summary>Lightweight version row (no config body).</summary>
public sealed record AgentVersionSummary(
    Guid Id,
    int Version,
    string? Notes,
    DateTime CreatedAt);

/// <summary>Full version detail including the config snapshot.</summary>
public sealed record AgentVersionDetail(
    Guid Id,
    Guid AgentId,
    int Version,
    JsonElement Config,
    string? Notes,
    DateTime CreatedAt);

/// <summary>201 response for create.</summary>
public sealed record CreateAgentResponse(
    Guid Id,
    string Name,
    string? Role,
    string Visibility,
    string Status,
    int CurrentVersion);

/// <summary>200 response for publish-version.</summary>
public sealed record PublishVersionResponse(
    Guid Id,
    int Version,
    DateTime CreatedAt);
