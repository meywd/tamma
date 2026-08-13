namespace Tamma.Api.Services.Ci;

// ============================================================
// Story 38 (Phase 1) — server-side binding records for the CI-mediation endpoints.
// Bound from the engine client's camelCase JSON. NONE carry a token — the API
// resolves the per-tenant credential server-side (BYOK→platform); the engine holds
// no git/CI token.
// ============================================================

/// <summary>
/// <c>POST /api/v1/ci/{owner}/{repo}/test-runs</c>. Triggers the configured CI
/// workflow on <see cref="Branch"/> and polls to a terminal (or last-observed)
/// state with the resolved token.
/// </summary>
public sealed record TriggerTestsRequest
{
    public string Branch { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>
    /// Epic 31 P3 — optional workflow file to dispatch. Null falls back to the
    /// deployment's <c>CI:WorkflowId</c> (default <c>test.yml</c>), preserving
    /// the pre-P3 behavior. Lets <c>/api/engine/trigger-ci</c> delegate into
    /// this same mediation core without losing its explicit
    /// <c>workflowFile</c> parameter. Additive — existing callers omit it.
    /// </summary>
    public string? WorkflowFile { get; init; }

    /// <summary>
    /// Epic 31 P3 — optional workflow-dispatch inputs (additive; the engine
    /// trigger-ci callback carries them, the test-runs plane historically has
    /// not).
    /// </summary>
    public Dictionary<string, string>? Inputs { get; init; }
}
