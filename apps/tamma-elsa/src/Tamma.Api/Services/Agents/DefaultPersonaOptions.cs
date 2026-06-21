namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-15 — bound from <c>Tamma:Agents:DefaultPersonaName</c>. Names the
/// public persona <see cref="AgentRegistryService.GetSystemDefaultPublicAsync"/>
/// returns as the platform default for ANY role (personas are cross-role, so the
/// default is role-INDEPENDENT). Defaults to <c>"claude"</c> — the persona the
/// seeder ships first. A configured persona that is not seeded is a fail-loud
/// error (<c>AGENT_DEFAULT_PERSONA_MISSING</c>), never an empty fallback.
/// </summary>
public sealed class DefaultPersonaOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionPath = "Tamma:Agents";

    /// <summary>
    /// The handle of the platform-default public persona. Default <c>"claude"</c>.
    /// </summary>
    public string DefaultPersonaName { get; set; } = "claude";
}
