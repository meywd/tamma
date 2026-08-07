namespace Tamma.Activities.ADL;

/// <summary>
/// Epic 31 P2 — the one place the <c>owner/repo</c> wire string is
/// split into the (owner, name) pair the platform abstraction's verbs
/// take. The mediation layer and the retyped ADL cores both consume
/// this so the split cannot drift.
/// </summary>
public static class GitRepoName
{
    /// <summary>
    /// Split <c>owner/repo</c>. A string without a slash maps to
    /// (empty, whole-string) — the platform will answer NotFound,
    /// which is the same failure the live path produced for a
    /// malformed repository value.
    /// </summary>
    public static (string Owner, string Name) Split(string repository)
    {
        var value = repository ?? string.Empty;
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1)
        {
            return (string.Empty, value);
        }
        return (value[..slash], value[(slash + 1)..]);
    }
}
