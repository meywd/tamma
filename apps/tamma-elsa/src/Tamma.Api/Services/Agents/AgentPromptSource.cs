namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-17 — provenance of a resolved agent prompt, surfaced on the
/// materialised <see cref="ResolvedAgentConfig"/> and emitted as the
/// <c>promptSource</c> tag on managed-run / resolution events (32-5).
/// </summary>
public enum AgentPromptSource
{
    /// <summary>
    /// The prompt came from the Epic 27 prompt store via the persona/public
    /// branch (32-15's <see cref="IPersonaPromptResolver"/>). Wire form
    /// <c>"epic27-store"</c>.
    /// </summary>
    Epic27Store,

    /// <summary>
    /// The prompt came from the custom (private) agent's own embedded
    /// <c>ConfigJson.prompts</c> via the custom branch (32-17's
    /// <see cref="ICustomAgentPromptResolver"/>). Wire form
    /// <c>"custom-agent"</c>.
    /// </summary>
    CustomAgent,
}

/// <summary>Wire-form helpers for <see cref="AgentPromptSource"/>.</summary>
public static class AgentPromptSourceExtensions
{
    /// <summary>The stable lower-kebab wire form used in event tags / logs.</summary>
    public static string ToWire(this AgentPromptSource source) => source switch
    {
        AgentPromptSource.CustomAgent => "custom-agent",
        AgentPromptSource.Epic27Store => "epic27-store",
        _ => "epic27-store",
    };
}
