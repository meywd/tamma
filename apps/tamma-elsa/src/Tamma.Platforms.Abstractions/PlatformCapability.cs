namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-1 AC3 — feature flags surfaced by every git platform
/// driver.
///
/// <para>The set of capabilities a driver returns from
/// <see cref="IGitPlatformDriver.Capabilities"/> tells callers
/// (workflow runtime, onboarding UI, secrets provisioner) what the
/// platform actually supports. Callers must NOT branch on
/// <see cref="PlatformKind"/> — branch on the capability instead so a
/// new driver Just Works when it advertises the same flags.</para>
///
/// <para>Capabilities partition into three groups:</para>
/// <list type="bullet">
///   <item><b>CI surface</b>: <see cref="Actions"/>,
///         <see cref="Artifacts"/>.</item>
///   <item><b>Secrets</b>: <see cref="Secrets"/>,
///         <see cref="LibsodiumSecrets"/>,
///         <see cref="ProtectedVariables"/>,
///         <see cref="MaskedVariables"/>.</item>
///   <item><b>Source-host</b>: <see cref="PrFileReview"/>,
///         <see cref="WebhookHmac"/>,
///         <see cref="WebhookStaticToken"/>,
///         <see cref="PerAppInstallationAuth"/>,
///         <see cref="ListAccessibleRepos"/>.</item>
/// </list>
///
/// <para>The capability set is intentionally unsealed — Story 31-8 will
/// add CI-secrets-specific flags, 31-7 will add webhook-event flags, etc.
/// New values append to the end so existing matrix entries don't shift.</para>
/// </summary>
public enum PlatformCapability
{
    /// <summary>
    /// Driver implements <see cref="IGitPlatformActionsClient"/> — the
    /// platform has a CI dispatch + run-monitor surface (GitHub Actions,
    /// GitLab pipelines, Gitea Actions, Forgejo Actions, Bitbucket
    /// Pipelines, Azure DevOps Pipelines). Pure-git forges may omit this.
    /// </summary>
    Actions = 1,

    /// <summary>
    /// Driver supports artifact upload + signed download URLs for
    /// completed CI runs.
    /// </summary>
    Artifacts = 2,

    /// <summary>
    /// Driver supports repo-scoped CI secrets via API (writeable from
    /// the secrets provisioner in Story 31-8).
    /// </summary>
    Secrets = 3,

    /// <summary>
    /// Secrets ingest path uses GitHub-style libsodium sealed-box
    /// encryption. GitHub-only; other platforms accept the secret
    /// value verbatim over TLS.
    /// </summary>
    LibsodiumSecrets = 4,

    /// <summary>
    /// CI variables can be marked "protected" — only exposed to runs
    /// on protected branches. GitLab + Forgejo have this; GitHub
    /// approximates via environments.
    /// </summary>
    ProtectedVariables = 5,

    /// <summary>
    /// CI variables can be marked "masked" so platform-side log
    /// scrubbing redacts the value if it appears in run output.
    /// </summary>
    MaskedVariables = 6,

    /// <summary>
    /// Driver supports posting file/line-anchored review comments on
    /// a PR/MR (the "comment on this diff line" UX).
    /// </summary>
    PrFileReview = 7,

    /// <summary>
    /// Webhooks are authenticated via HMAC-SHA256 over the request
    /// body. Tamma's signature verifier must accept this shape.
    /// </summary>
    WebhookHmac = 8,

    /// <summary>
    /// Webhooks are authenticated via a static shared token in a
    /// header (GitLab style — "X-Gitlab-Token: &lt;value&gt;"). Less
    /// secure than HMAC but is what the platform offers.
    /// </summary>
    WebhookStaticToken = 9,

    /// <summary>
    /// Driver authenticates as a per-installation app (GitHub App,
    /// Gitea OAuth2 application, etc.) rather than a single OAuth
    /// access token. Required for multi-tenant SaaS mode.
    /// </summary>
    PerAppInstallationAuth = 10,

    /// <summary>
    /// Driver can enumerate the repos visible to the current
    /// credential without the caller knowing them in advance —
    /// powers the onboarding "pick a repo" UI (Story 31-9).
    /// </summary>
    ListAccessibleRepos = 11,

    /// <summary>
    /// Story 31-13 — driver implements the full PR lifecycle verbs:
    /// close, reopen, request reviewers, add/remove labels, and
    /// draft↔ready toggle. GitHub-only today; the other drivers answer
    /// these interface members with <c>capability_unsupported</c> and
    /// this flag records that they do not (yet) perform them.
    /// </summary>
    PrLifecycle = 12,
}
