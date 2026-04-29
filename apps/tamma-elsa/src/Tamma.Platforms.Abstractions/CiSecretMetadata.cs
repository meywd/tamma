namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-8 — neutral CI-secret metadata. Drivers honour what they
/// can and ignore the rest (logging at debug level when a flag is
/// non-default but unsupported on the platform).
///
/// <para>Per-platform handling:</para>
/// <list type="bullet">
///   <item><b>GitHub Actions</b>: ignores <see cref="Masked"/>
///         (always log-scrubbed) and <see cref="Protected"/>
///         (approximated via environments rather than a flag).
///         <see cref="EnvironmentScope"/> picks the GitHub environment
///         when scope is <see cref="CiSecretScope.Environment"/>.</item>
///   <item><b>GitLab</b>: honours every field — masked, protected,
///         <c>environment_scope</c> on the variable, <see cref="VariableType"/>
///         <c>env_var</c> (default) or <c>file</c>.</item>
///   <item><b>Gitea/Forgejo</b>: ignores all flags — Gitea CI secrets
///         have no protected/masked toggle in the API.</item>
/// </list>
/// </summary>
/// <param name="Protected">
/// CI variable should be exposed only on protected branches. Honoured
/// on platforms with <see cref="PlatformCapability.ProtectedVariables"/>;
/// silently ignored elsewhere.
/// </param>
/// <param name="Masked">
/// Platform-side log scrubbing replaces the value if it leaks into run
/// output. Honoured on platforms with
/// <see cref="PlatformCapability.MaskedVariables"/>; silently ignored
/// elsewhere. GitLab additionally enforces strict character-set rules
/// — see <c>MaskedVariableValidator</c>.
/// </param>
/// <param name="EnvironmentScope">
/// GitLab-style wildcard scope (e.g. <c>production</c>, <c>review/*</c>).
/// Required when <see cref="CiSecretScope.Environment"/> is used and
/// the target's environment name is a wildcard; otherwise drivers
/// derive it from the target's <c>EnvironmentName</c>.
/// </param>
/// <param name="VariableType">
/// GitLab-only. <c>env_var</c> (default) or <c>file</c>. <c>file</c>
/// causes GitLab to write the value as a runner-local file. Other
/// platforms ignore.
/// </param>
public sealed record CiSecretMetadata(
    bool Protected = false,
    bool Masked = false,
    string? EnvironmentScope = null,
    string VariableType = "env_var")
{
    /// <summary>
    /// All-defaults metadata — useful for tests + the common
    /// "just provision a secret, no flags" callsite.
    /// </summary>
    public static CiSecretMetadata Default { get; } = new();
}
