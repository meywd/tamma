using Tamma.Core.Actions;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-14 (Amendment 2-C, D7) — the STATIC fixture of approval chains,
/// shared by <see cref="ApprovalGrantMinter"/> (what a human approval mints) and
/// the AC6/AC7 tests (fixture ↔ minting code ↔ catalog levels). One source of
/// truth: a chain's entry human-approval, its gated links, and the exact set of
/// correlation-standing grants that approval mints.
///
/// <para><b>Machinery chains (Amendment 4 / caller-kind re-audit).</b> The
/// rotation, tenant-move and tenant-delete chains are entirely MACHINERY — never
/// dial-gated — so "the chain's gated target keys" is the EMPTY SET for those
/// three today. They are recorded with a justification string, and the minter
/// seam is wired at all five entries so a future machinery→dial reclassification
/// is a fixture edit that fails the parity test, not new plumbing.</para>
/// </summary>
public static class ApprovalChains
{
    public const string MergeComposite = "merge-composite";
    public const string DeployTail = "deploy-tail";
    public const string Rotation = "rotation";
    public const string TenantMove = "tenant-move";
    public const string TenantDelete = "tenant-delete";

    /// <summary>A gated link in a chain.</summary>
    /// <param name="TargetKeyWire">The action-key wire (e.g. <c>effect:deploy.prod</c>).</param>
    /// <param name="HasOwnResumableHumanWait">TRUE when this link suspends on its
    /// OWN human bookmark — a level above the chain entry is then legitimate,
    /// because a person is asked again at the link.</param>
    public sealed record Link(string TargetKeyWire, bool HasOwnResumableHumanWait = false);

    /// <summary>One approval chain.</summary>
    /// <param name="Name">The chain id (a <c>MintForChain</c> argument).</param>
    /// <param name="EntryApprovalActionWire">The highest-level action the human
    /// explicitly consents to at the chain's entry human wait — the ceiling
    /// monotonicity compares link levels against.</param>
    /// <param name="Links">Every gated link the chain executes after the entry
    /// approval.</param>
    /// <param name="MintedTargetKeys">The correlation-standing grants the entry
    /// approval mints (all TargetKind=<c>action</c>). EMPTY for a machinery chain.</param>
    /// <param name="Justification">Why the minted set is what it is (esp. an empty
    /// machinery set).</param>
    public sealed record Chain(
        string Name,
        string EntryApprovalActionWire,
        IReadOnlyList<Link> Links,
        IReadOnlyList<string> MintedTargetKeys,
        string Justification);

    // Per-target merge keys (43-12): the merge-approval endpoint cannot know the
    // PR base branch, so the composite mint names all three (D8). The in-composite
    // branch delete rides the merge grant (Amendment 2-C3); the standalone delete
    // route keeps its own level.
    private const string MergeDev = "effect:git.merge.dev";
    private const string MergeQa = "effect:git.merge.qa";
    private const string MergeMain = "effect:git.merge.main";
    private const string IssuePatch = "effect:git.issue.patch";
    private const string BranchDelete = "effect:git.branch.delete";
    private const string DeployProd = "effect:deploy.prod";
    private const string ReleaseCreate = "effect:git.release.create";

    /// <summary>The five chains (AC6).</summary>
    public static readonly IReadOnlyList<Chain> All = new[]
    {
        new Chain(
            MergeComposite,
            // The human approving a merge consents to the highest merge target;
            // all three merge keys + issue patch + in-composite branch delete are
            // minted, so they are covered by the head grant regardless of level.
            EntryApprovalActionWire: MergeMain,
            Links: new[]
            {
                new Link(MergeDev), new Link(MergeQa), new Link(MergeMain),
                new Link(IssuePatch), new Link(BranchDelete),
            },
            MintedTargetKeys: new[] { MergeDev, MergeQa, MergeMain, IssuePatch, BranchDelete },
            Justification: "The merge-approval human 'merge' decision covers the whole composite: "
                + "the per-target merge (43-12; base branch unknown at decide time, so all three), "
                + "the issue close/patch, and the in-composite branch delete (Amendment 2-C3 — the "
                + "95 level is the STANDALONE delete route, the composite deletion rides the merge grant)."),

        new Chain(
            DeployTail,
            EntryApprovalActionWire: DeployProd,
            Links: new[] { new Link(DeployProd), new Link(ReleaseCreate) },
            MintedTargetKeys: new[] { DeployProd, ReleaseCreate },
            Justification: "The production-deploy approval covers the deploy tail: the prod deploy "
                + "itself and the release/tag creation that follows it."),

        new Chain(
            Rotation,
            // Machinery — no dial-gated entry action; the entry is the rotation
            // trigger (rot_{guid} correlation), all links are machinery.
            EntryApprovalActionWire: string.Empty,
            Links: Array.Empty<Link>(),
            MintedTargetKeys: Array.Empty<string>(),
            Justification: "Amendment 4 / caller-kind re-audit: every link of the secret-rotation "
                + "chain is MACHINERY (never dial-gated), so its gated-target set is EMPTY. The minter "
                + "seam is still wired at the rot_{guid} entry so a future reclassification is a fixture edit."),

        new Chain(
            TenantMove,
            EntryApprovalActionWire: string.Empty,
            Links: Array.Empty<Link>(),
            MintedTargetKeys: Array.Empty<string>(),
            Justification: "Amendment 4: the tenant-move chain is entirely MACHINERY "
                + "(platform-task:* links), so its gated-target set is EMPTY."),

        new Chain(
            TenantDelete,
            EntryApprovalActionWire: string.Empty,
            Links: Array.Empty<Link>(),
            MintedTargetKeys: Array.Empty<string>(),
            Justification: "Amendment 4: the tenant-delete chain is entirely MACHINERY "
                + "(platform-task:* links), so its gated-target set is EMPTY."),
    };

    /// <summary>Look up a chain by name, or null.</summary>
    public static Chain? Find(string name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The catalog level for a target-key wire: the descriptor's
    /// <c>DefaultMinAutonomy</c>, or NULL for a machinery row / an unknown key
    /// (a machinery link has no dial level and can never "exceed" an entry).
    /// </summary>
    public static int? CatalogLevelOf(string targetKeyWire)
    {
        if (ActionKey.TryParse(targetKeyWire, out var key)
            && ActionCatalog.TryGet(key, out var descriptor) && descriptor is not null
            && !descriptor.IsMachinery)
        {
            return descriptor.DefaultMinAutonomy;
        }
        return null;
    }

    /// <summary>
    /// Story 43-14 (AC7) — the CHAIN-MONOTONICITY rule as a pure function: no
    /// gated link may carry a level ABOVE its chain's entry approval unless the
    /// link is covered by the head's grant (in <see cref="Chain.MintedTargetKeys"/>)
    /// or has its OWN resumable human wait. Returns one human-readable violation
    /// string per offending (chain, link), naming the chain — so a level edit that
    /// breaks a chain fails the build with the chain named. Empty = clean.
    /// </summary>
    /// <param name="levelOf">Level source (catalog by default; injectable for the
    /// self-test). Null means "no dial level" — never a violation.</param>
    public static IReadOnlyList<string> FindMonotonicityViolations(
        IEnumerable<Chain> chains, Func<string, int?> levelOf)
    {
        ArgumentNullException.ThrowIfNull(chains);
        ArgumentNullException.ThrowIfNull(levelOf);

        var violations = new List<string>();
        foreach (var chain in chains)
        {
            var entryLevel = string.IsNullOrEmpty(chain.EntryApprovalActionWire)
                ? (int?)null
                : levelOf(chain.EntryApprovalActionWire);

            foreach (var link in chain.Links)
            {
                var linkLevel = levelOf(link.TargetKeyWire);
                if (linkLevel is not int lvl) continue;           // no dial level → can't exceed
                var entry = entryLevel ?? int.MinValue;            // no entry ⇒ every dial link is "above"
                if (lvl <= entry) continue;                        // within the head's consent
                if (chain.MintedTargetKeys.Contains(link.TargetKeyWire)) continue; // covered by head grant
                if (link.HasOwnResumableHumanWait) continue;       // asked again at the link

                violations.Add(
                    $"Chain '{chain.Name}': link '{link.TargetKeyWire}' at level {lvl} exceeds the "
                    + $"entry approval '{chain.EntryApprovalActionWire}' at level "
                    + $"{(entryLevel?.ToString() ?? "<none>")}, and is neither in the head's minted "
                    + "grant set nor guarded by its own resumable human wait.");
            }
        }
        return violations;
    }

    /// <summary>The production check: monotonicity over all chains against the
    /// shipped catalog levels.</summary>
    public static IReadOnlyList<string> FindMonotonicityViolations() =>
        FindMonotonicityViolations(All, CatalogLevelOf);
}
