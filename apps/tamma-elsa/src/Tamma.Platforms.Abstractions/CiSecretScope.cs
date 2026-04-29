namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-8 — neutral CI-secrets scope. Every platform supports a
/// subset (see <see cref="PlatformKindCapabilityMatrix"/>); drivers
/// translate to the platform-native shape:
///
/// <list type="bullet">
///   <item><b>GitHub</b>: Repo → repo secret; Org → org secret;
///         Environment → environment secret; User/Global →
///         <c>scope_not_supported_on_platform</c>.</item>
///   <item><b>Gitea/Forgejo</b>: Repo → repo secret; Org → org secret;
///         User → user secret; Environment → unsupported on 1.25;
///         Global → admin endpoint on 1.25+.</item>
///   <item><b>GitLab</b>: Repo → project variable; Org → group
///         variable; Environment → project variable with
///         <c>environment_scope</c>; User/Global → unsupported.</item>
/// </list>
///
/// <para>Scope is paired with <see cref="CiSecretTarget"/>; the target
/// carries the platform-specific identifiers (owner, repo, env name)
/// so the driver does not have to parse the scope+target out of a
/// flat string tuple.</para>
/// </summary>
public enum CiSecretScope
{
    /// <summary>
    /// A single repository's CI secrets (most common). Target is
    /// <see cref="CiSecretTarget.Repo"/>.
    /// </summary>
    Repo = 1,

    /// <summary>
    /// Org-level (GitHub / Gitea / Forgejo) or group-level (GitLab)
    /// CI secrets shared across every repo in the org/group. Target
    /// is <see cref="CiSecretTarget.Org"/>.
    /// </summary>
    Org = 2,

    /// <summary>
    /// Per-user CI secrets (Gitea-specific scope; GitHub has Codespaces
    /// personal secrets but no general user-actions secrets;
    /// GitLab has none). Target is <see cref="CiSecretTarget.User"/>.
    /// </summary>
    User = 3,

    /// <summary>
    /// Instance-wide / admin-managed secrets (Gitea 1.25+ admin endpoint;
    /// the rest of the matrix returns
    /// <c>scope_not_supported_on_platform</c>). Target is
    /// <see cref="CiSecretTarget.Global"/>.
    /// </summary>
    Global = 4,

    /// <summary>
    /// Repo-scoped secret bound to a deploy environment (GitHub
    /// environments; GitLab <c>environment_scope</c>). Target is
    /// <see cref="CiSecretTarget.Environment"/>.
    /// </summary>
    Environment = 5,
}
