namespace Tamma.Core.Redaction;

/// <summary>
/// Neutralizes Slack control tokens in UNTRUSTED text before it is posted to Slack.
///
/// <para>Slack renders <c>&lt;!channel&gt;</c>, <c>&lt;!here&gt;</c>,
/// <c>&lt;!everyone&gt;</c>, <c>&lt;@Uxxxx&gt;</c>, <c>&lt;!subteam^Sxxx&gt;</c> and
/// <c>&lt;#Cxxx|name&gt;</c> as live broadcast / mention / channel links. If a body
/// derived from issue titles, task text, or LLM output contains any of those, an
/// otherwise-innocuous notification would ping the whole workspace. Applying Slack's
/// documented message escaping — <c>&amp;</c> → <c>&amp;amp;</c>, <c>&lt;</c> →
/// <c>&amp;lt;</c>, <c>&gt;</c> → <c>&amp;gt;</c> — renders every such token literally,
/// so untrusted content can never expand into pings beyond the intended audience.</para>
///
/// <para>Because every control token is delimited by <c>&lt;</c>…<c>&gt;</c>, escaping
/// those two characters (plus the leading <c>&amp;</c>) is sufficient and leaves ordinary
/// text and URLs intact — a plain URL carries no <c>&amp;&lt;&gt;</c>, and a query-string
/// <c>&amp;</c> becomes <c>&amp;amp;</c> which Slack still links correctly. Order matters:
/// escape <c>&amp;</c> FIRST so the <c>&amp;lt;</c> / <c>&amp;gt;</c> it introduces are not
/// double-escaped. This is the single shared implementation used by both the engine-side
/// <c>SlackActivity</c> formatters and the mediated <c>MediatedSlack</c> seam so a body is
/// escaped exactly once at its producer, before it is enqueued to <c>slack_outbox</c>.</para>
/// </summary>
public static class SlackTextSanitizer
{
    /// <summary>
    /// Escape Slack control characters in an untrusted body. Null/empty inputs return
    /// the empty string. Apply ONCE, at the point the untrusted text is folded into a
    /// posted body — never to our own emoji/label prefixes, and never twice.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
