namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-8 — discriminated union of CI-secret targets. Pair with
/// <see cref="CiSecretScope"/>; the scope picks the platform endpoint
/// shape, the target carries the identifiers.
///
/// <para>Pattern-match against the concrete record types:</para>
/// <code>
/// var endpoint = (scope, target) switch
/// {
///     (CiSecretScope.Repo, CiSecretTarget.Repo r) =>
///         $"/repos/{r.Owner}/{r.Repo}/actions/secrets/{name}",
///     (CiSecretScope.Org, CiSecretTarget.Org o) =>
///         $"/orgs/{o.OrgOrGroup}/actions/secrets/{name}",
///     (CiSecretScope.Environment, CiSecretTarget.Environment e) =>
///         $"/repos/{e.Owner}/{e.Repo}/environments/{e.EnvironmentName}/secrets/{name}",
///     ...
/// };
/// </code>
///
/// <para>The driver does NOT validate that the scope matches the
/// target shape — that's the caller's responsibility, captured by
/// the constructor of each variant. Mismatches surface as a
/// per-target <see cref="CiSecretProvisionResult"/> with
/// <c>scope_target_mismatch</c>.</para>
/// </summary>
public abstract record CiSecretTarget
{
    private CiSecretTarget() { }

    /// <summary>
    /// Single-repo target. <see cref="Owner"/> is the org/user login;
    /// <see cref="Repo"/> is the repository slug. GitLab projects
    /// carry the same shape — the driver maps to project id.
    /// </summary>
    public sealed record Repo(string Owner, string RepoName) : CiSecretTarget;

    /// <summary>
    /// Org / group / namespace target. <see cref="OrgOrGroup"/> is
    /// the org login (GitHub / Gitea), the group id (GitLab), or the
    /// project collection (Azure DevOps).
    /// </summary>
    public sealed record Org(string OrgOrGroup) : CiSecretTarget;

    /// <summary>
    /// Single-user target (Gitea per-user secrets; rare elsewhere).
    /// </summary>
    public sealed record User(string UserLogin) : CiSecretTarget;

    /// <summary>
    /// Instance-wide / admin-managed target. Carries no identifier;
    /// the driver hits the platform's admin endpoint.
    /// </summary>
    public sealed record Global() : CiSecretTarget;

    /// <summary>
    /// Repo-scoped environment target — GitHub environments, GitLab
    /// <c>environment_scope</c>. <see cref="EnvironmentName"/> is the
    /// environment slug ("production", "staging", "*").
    /// </summary>
    public sealed record Environment(
        string Owner,
        string RepoName,
        string EnvironmentName) : CiSecretTarget;

    /// <summary>
    /// Stable, human-friendly descriptor for logs + per-target results.
    /// Never includes secret values.
    /// </summary>
    public string Descriptor() => this switch
    {
        Repo r => $"repo:{r.Owner}/{r.RepoName}",
        Org o => $"org:{o.OrgOrGroup}",
        User u => $"user:{u.UserLogin}",
        Global => "global",
        Environment e => $"env:{e.Owner}/{e.RepoName}/{e.EnvironmentName}",
        _ => $"unknown:{GetType().Name}",
    };
}
