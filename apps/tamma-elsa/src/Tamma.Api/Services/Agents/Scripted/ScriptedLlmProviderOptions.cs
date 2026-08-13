namespace Tamma.Api.Services.Agents.Scripted;

/// <summary>
/// Config for the opt-in scripted LLM provider (2026-08-13). The ENABLE flag
/// itself is NOT here — it is the shared
/// <c>Tamma.Activities.Security.ScriptedProviderPosture.FlagKey</c>
/// (<c>Llm:EnableScriptedProvider</c>), checked with the structural
/// production-refusal guard at registration time. This section only carries
/// the optional per-test script override file.
/// </summary>
public sealed class ScriptedLlmProviderOptions
{
    public const string SectionName = "Llm:ScriptedProvider";

    /// <summary>
    /// Optional path to a JSON script-override file:
    /// <c>{ "responses": { "&lt;key&gt;": "&lt;response text&gt;", ... } }</c>
    /// with keys in the <c>{role}/{action}[@{documentType}]</c> /
    /// <c>@{documentType}</c> / <c>*</c> syntax. Entries override the built-in
    /// <see cref="ScriptedCycleLibrary"/> per key. A set-but-unreadable path
    /// fails registration LOUD (a test pointing at a missing script must not
    /// silently run the default script).
    /// </summary>
    public string? ScriptPath { get; set; }
}
