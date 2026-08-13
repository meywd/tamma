namespace Tamma.Platforms.GitLab;

/// <summary>
/// Epic 31 P6 M1 — GitLab's draft mechanics are TITLE-PREFIX based (like
/// Gitea's WIP mechanism, different vocabulary). Verified against the
/// GitLab docs (<c>doc/user/project/merge_requests/drafts.md</c>):
///
/// <list type="bullet">
///   <item><b>Write side</b>: current GitLab recognises <c>Draft:</c>,
///         <c>[Draft]</c> and <c>(Draft)</c> at the start of the title.
///         The driver always writes <c>"Draft: "</c> — valid since 13.2,
///         i.e. on every instance above the 13.9 lifecycle floor.</item>
///   <item><b>Read side</b>: legacy <c>WIP:</c> / <c>[WIP]</c> prefixes
///         marked drafts until their removal in GitLab 14.8
///         (gitlab-org/gitlab!79693), so 13.9–14.7 instances can still
///         carry WIP-titled drafts — the reader accepts both families.
///         The response-side <c>draft</c> / <c>work_in_progress</c>
///         booleans are the primary signal; the prefix inference is the
///         defence for proxies/webhook payloads that omit them.</item>
/// </list>
/// </summary>
internal static class GitLabDraftTitle
{
    /// <summary>Current-generation prefixes — the ones GitLab (≥14.8, i.e.
    /// every instance above the lifecycle floor in practice) still treats as
    /// draft markers. Write side always uses <c>"Draft: "</c>.</summary>
    internal static readonly string[] CurrentDraftPrefixes =
        ["Draft:", "[Draft]", "(Draft)"];

    /// <summary>Legacy prefixes — draft markers only on GitLab 13.9–14.7;
    /// ordinary title text on anything newer. Read-side inference accepts
    /// them (for the payloads-without-booleans case on old instances), the
    /// write side never trusts them.</summary>
    internal static readonly string[] LegacyWipPrefixes = ["WIP:", "[WIP]"];

    /// <summary>Prefixes recognised on READ, checked case-insensitively at
    /// the start of the title.</summary>
    internal static readonly string[] DraftPrefixes =
        [.. CurrentDraftPrefixes, .. LegacyWipPrefixes];

    internal static bool HasDraftPrefix(string? title) =>
        HasAnyPrefix(title, DraftPrefixes);

    /// <summary>Only the current-generation Draft prefixes — a legacy WIP
    /// prefix does NOT count (GitLab ≥14.8 ignores it).</summary>
    internal static bool HasCurrentDraftPrefix(string? title) =>
        HasAnyPrefix(title, CurrentDraftPrefixes);

    private static bool HasAnyPrefix(string? title, string[] prefixes)
    {
        if (string.IsNullOrEmpty(title)) return false;
        var trimmed = title.TrimStart();
        foreach (var prefix in prefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Epic 31 review (F-medium) — the ONE draft predicate. The server's
    /// <c>draft</c>/<c>work_in_progress</c> booleans are authoritative
    /// whenever the payload carries either of them; title-prefix inference
    /// is ONLY the fallback for payloads that omit both. The old
    /// unconditional OR gave a stale <c>WIP:</c> title veto power over an
    /// explicit server <c>draft:false</c>, so <c>SetDraft(true)</c> on a
    /// WIP-titled ready MR (GitLab ≥14.8) silently no-oped while reporting
    /// IsDraft=true.
    /// </summary>
    internal static bool IsDraft(bool? draft, bool? workInProgress, string? title) =>
        draft.HasValue || workInProgress.HasValue
            ? (draft ?? false) || (workInProgress ?? false)
            : HasDraftPrefix(title);

    /// <summary>Prepend <c>"Draft: "</c> unless a CURRENT-generation Draft
    /// prefix is already present. A legacy WIP prefix deliberately does not
    /// suppress the write: on ≥14.8 "WIP: fix" is a ready MR, and only a
    /// real Draft prefix ("Draft: WIP: fix") actually drafts it.</summary>
    internal static string AddDraftPrefix(string title) =>
        HasCurrentDraftPrefix(title) ? title : $"Draft: {title}";

    /// <summary>Strip every leading draft/WIP prefix (handles stacked
    /// prefixes like <c>"Draft: [WIP] fix"</c>).</summary>
    internal static string StripDraftPrefix(string title)
    {
        if (string.IsNullOrEmpty(title)) return title;
        var current = title.TrimStart();
        var stripped = true;
        while (stripped)
        {
            stripped = false;
            foreach (var prefix in DraftPrefixes)
            {
                if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    current = current[prefix.Length..].TrimStart();
                    stripped = true;
                    break;
                }
            }
        }
        return current;
    }
}
