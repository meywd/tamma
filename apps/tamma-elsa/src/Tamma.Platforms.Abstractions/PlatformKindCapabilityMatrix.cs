using System.Collections.Frozen;

namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-1 AC4 — static capability matrix encoding the *expected*
/// support level for each <see cref="PlatformKind"/>. Source of truth
/// is the brief's matrix table (see
/// <c>docs/stories/epic-31/31-1-git-platform-abstraction.md</c>).
///
/// <para>Two consumers:</para>
/// <list type="number">
///   <item>The onboarding UI (Story 31-9) calls
///         <see cref="DefaultsFor"/> to render a "platform picker"
///         WITHOUT instantiating drivers — so missing creds don't
///         break the page.</item>
///   <item>Driver implementations use
///         <see cref="DefaultsFor"/> as the seed for their
///         <see cref="IGitPlatformDriver.Capabilities"/> property,
///         then add or remove flags based on actual config (e.g.
///         GitHub driver advertises <see cref="PlatformCapability.LibsodiumSecrets"/>
///         only when libsodium is wired up).</item>
/// </list>
///
/// <para>Bitbucket and Azure DevOps entries are populated even though
/// drivers don't ship until 31-11/31-12 — the picker can render them
/// now and the matrix stays the contract those stories must satisfy.</para>
/// </summary>
public static class PlatformKindCapabilityMatrix
{
    private static readonly FrozenDictionary<PlatformKind, FrozenSet<PlatformCapability>> Defaults =
        new Dictionary<PlatformKind, FrozenSet<PlatformCapability>>
        {
            [PlatformKind.GitHub] = new[]
            {
                PlatformCapability.Actions,
                PlatformCapability.Artifacts,
                PlatformCapability.Secrets,
                PlatformCapability.LibsodiumSecrets,
                PlatformCapability.PrFileReview,
                PlatformCapability.WebhookHmac,
                PlatformCapability.PerAppInstallationAuth,
                PlatformCapability.ListAccessibleRepos,
                // Story 31-13 / Epic 31 P1 stage 2 — the GitHub DRIVER
                // (Tamma.Platforms.GitHub) now implements the full PR
                // lifecycle (close/reopen/reviewers/labels/draft-via-GraphQL)
                // for real; the flag is no longer a lie over a stub.
                PlatformCapability.PrLifecycle,
                // Epic 31 P1 stage 2 — the loop verbs are real in the GitHub
                // driver: issue close/labels, release create, review-comment
                // listing, commit + branch-file-change reads.
                PlatformCapability.IssueLifecycle,
                PlatformCapability.Releases,
                PlatformCapability.CommitReads,
                PlatformCapability.PrReviewCommentRead,
            }.ToFrozenSet(),

            [PlatformKind.Gitea] = new[]
            {
                PlatformCapability.Actions,
                PlatformCapability.Artifacts,
                PlatformCapability.Secrets,
                PlatformCapability.PrFileReview,
                PlatformCapability.WebhookHmac,
                PlatformCapability.ListAccessibleRepos,
                // Epic 31 P5 M1 — the Gitea driver implements the six 31-13
                // lifecycle verbs for real (PATCH state, requested_reviewers,
                // issue-side labels, WIP-title draft toggle). The driver's
                // ComputeCapabilities narrows this away below the 1.14 floor
                // (requested_reviewers endpoint) or when the version probe
                // failed.
                PlatformCapability.PrLifecycle,
                // PerAppInstallationAuth: Gitea OAuth2 apps support
                // installation-style flows but only partial vs GitHub.
                // Driver may add it conditionally in 31-4.
            }.ToFrozenSet(),

            [PlatformKind.Forgejo] = new[]
            {
                PlatformCapability.Actions,
                PlatformCapability.Artifacts,
                PlatformCapability.Secrets,
                PlatformCapability.PrFileReview,
                PlatformCapability.WebhookHmac,
                PlatformCapability.ListAccessibleRepos,
                // Epic 31 P5 M1 — Forgejo rides the Gitea shim, so the
                // lifecycle verbs are real here too (same version floor,
                // narrowed by ForgejoPlatformDriver.ComputeCapabilities).
                PlatformCapability.PrLifecycle,
                // Forgejo retains Gitea API compat; 31-5 ships a
                // compat-mode driver that re-uses Gitea's. Same
                // baseline.
            }.ToFrozenSet(),

            [PlatformKind.GitLab] = new[]
            {
                PlatformCapability.Actions,           // pipelines
                PlatformCapability.Artifacts,         // job artifacts
                PlatformCapability.Secrets,           // CI variables
                PlatformCapability.ProtectedVariables,
                PlatformCapability.MaskedVariables,
                PlatformCapability.PrFileReview,      // MR diff notes
                PlatformCapability.WebhookStaticToken,
                PlatformCapability.ListAccessibleRepos,
            }.ToFrozenSet(),

            [PlatformKind.Bitbucket] = new[]
            {
                PlatformCapability.Actions,           // Pipelines
                PlatformCapability.Artifacts,         // Downloads API
                PlatformCapability.Secrets,
                PlatformCapability.PrFileReview,
                PlatformCapability.WebhookHmac,
                PlatformCapability.ListAccessibleRepos,
            }.ToFrozenSet(),

            [PlatformKind.AzureDevOps] = new[]
            {
                PlatformCapability.Actions,           // Pipelines
                PlatformCapability.Artifacts,
                PlatformCapability.Secrets,           // variable groups
                PlatformCapability.MaskedVariables,
                PlatformCapability.PrFileReview,      // limited
                PlatformCapability.WebhookHmac,       // service hooks
                PlatformCapability.ListAccessibleRepos,
            }.ToFrozenSet(),
        }.ToFrozenDictionary();

    /// <summary>
    /// Returns the default capability set for the platform. Throws
    /// <see cref="ArgumentOutOfRangeException"/> if a new
    /// <see cref="PlatformKind"/> is added without a matrix entry — the
    /// abstraction's coverage test enforces this so a forgotten matrix
    /// row fails loudly.
    /// </summary>
    public static IReadOnlySet<PlatformCapability> DefaultsFor(PlatformKind kind)
    {
        if (!Defaults.TryGetValue(kind, out var set))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind), kind,
                $"No capability matrix entry for PlatformKind.{kind} — " +
                "every PlatformKind must have a matrix entry. Update " +
                "PlatformKindCapabilityMatrix.");
        }
        return set;
    }

    /// <summary>
    /// Read-only view over the entire matrix — used by tests to assert
    /// every <see cref="PlatformKind"/> is covered without hard-coding
    /// the value list.
    /// </summary>
    public static IReadOnlyDictionary<PlatformKind, IReadOnlySet<PlatformCapability>> All =>
        Defaults.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlySet<PlatformCapability>)kvp.Value);

    /// <summary>
    /// Convenience: <c>true</c> when the named platform's default
    /// matrix advertises the capability. Onboarding picker calls this
    /// to grey out unsupported options.
    /// </summary>
    public static bool Supports(PlatformKind kind, PlatformCapability capability) =>
        DefaultsFor(kind).Contains(capability);
}
