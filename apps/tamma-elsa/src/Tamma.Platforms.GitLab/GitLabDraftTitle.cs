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
    /// <summary>Prefixes recognised on READ, checked case-insensitively at
    /// the start of the title. Write side always uses <c>"Draft: "</c>.</summary>
    internal static readonly string[] DraftPrefixes =
        ["Draft:", "[Draft]", "(Draft)", "WIP:", "[WIP]"];

    internal static bool HasDraftPrefix(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        var trimmed = title.TrimStart();
        foreach (var prefix in DraftPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static string AddDraftPrefix(string title) =>
        HasDraftPrefix(title) ? title : $"Draft: {title}";

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
