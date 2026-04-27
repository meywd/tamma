using Tamma.Data.Entities;

namespace Tamma.Data.Defaults;

/// <summary>
/// Code-resident platform defaults for <see cref="AgentConfig"/>.
///
/// <para>
/// Story 28-1 PR A (Decision #1, <c>.dev/decisions/story-28-1-design-calls.md</c>):
/// the legacy <c>agent_configs.tenant_id IS NULL</c> row in
/// <see cref="ControlPlaneDbContext"/> is being replaced by an in-code
/// default — the same pattern Tamma already uses for prompt-store defaults
/// (CLAUDE.md → "System defaults remain in code"). Each
/// <see cref="IAgentConfigRepository"/> read for a null tenant resolves to
/// <see cref="Empty"/> instead of querying the platform-default row.
/// </para>
///
/// <para>
/// The actual per-role agent template lives in
/// <c>Tamma.Api.Services.Agents.DefaultAgentConfig</c>; that class is the
/// canonical platform-default for resolution. This struct only carries the
/// shape the repository hands back when no tenant override exists, so the
/// downstream <see cref="AgentResolverService"/> still sees a stable
/// <c>Config = "{}"</c> sentinel and falls through to its per-role defaults.
/// </para>
/// </summary>
public static class AgentConfigDefaults
{
    /// <summary>
    /// JSON document representing the platform default agent-config row.
    /// Empty object — callers fall through to per-role defaults in
    /// <c>DefaultAgentConfig.ForRole</c>. Keep this synchronised with the
    /// schema <see cref="AgentConfig.Config"/> default ("<c>{}</c>").
    /// </summary>
    public const string ConfigJson = "{}";

    /// <summary>
    /// Build a fresh, mutable <see cref="AgentConfig"/> snapshot representing
    /// the platform-wide default. Callers MUST treat the returned instance as
    /// read-only — mutating it does not persist anywhere because defaults
    /// live in code, not in the database.
    /// </summary>
    /// <remarks>
    /// A brand-new object is returned on every call so EF / serializers
    /// cannot accidentally observe shared mutable state across requests.
    /// </remarks>
    public static AgentConfig Snapshot() => new()
    {
        Id = Guid.Empty,
        TenantId = null,
        Config = ConfigJson,
        Version = 0,
        CreatedAt = DateTime.MinValue,
        UpdatedAt = DateTime.MinValue,
        CreatedBy = null,
        UpdatedBy = null,
    };
}
