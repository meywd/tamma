namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Formats validate→retry feedback so it can be merged INTO a template variable
/// the target prompt actually DECLARES.
///
/// Why: <c>PromptStoreService.Render</c> substitutes only the {{placeholders}}
/// present in the template body — a supplied-but-undeclared variable (like the
/// old <c>validationErrors</c> dispatch key) is silently dropped at render, so
/// every retry re-prompted blind. Conversely, a declared-but-unsupplied variable
/// leaks a literal <c>{{...}}</c> into the prompt, and the Plan-family body is
/// SHARED across ~17 (role, action) cells — so we must NOT add a new placeholder
/// to a shared template. The fix: append a clearly-delimited feedback block to a
/// variable the template already declares (e.g. <c>contextFindings</c> for the
/// Plan family, <c>testTarget</c> for WriteTests).
///
/// <para>The Plan family's own bespoke retry loop is retired (Story 39-14): the
/// generic <c>document-lifecycle</c> now owns validate → repair/revise, feeding
/// notes back through its <c>feedbackVariableName = "contextFindings"</c> seam
/// (39-6 D11) — the SAME declared-carrier discipline this helper established, so
/// the render-drop lesson is preserved where the logic now lands. This helper
/// stays the shared formatter for the legacy TaskCreation / TestCaseCreation
/// producers (39-15 scope) and for the lifecycle's revise-notes rendering.</para>
///
/// Pure logic, no Elsa runtime dependency.
/// </summary>
public static class ValidationFeedbackHelper
{
    /// <summary>Heading that delimits the retry-feedback block in the prompt.</summary>
    public const string FeedbackHeader = "## Previous attempt failed validation — fix these issues";

    /// <summary>
    /// The separator the validate steps use when joining individual error
    /// messages into the single ValidationErrors workflow variable
    /// (<c>string.Join("; ", errors)</c> in the task/test-case validate lambdas).
    /// </summary>
    private const string ErrorJoinSeparator = "; ";

    /// <summary>
    /// Merge validation errors into an existing declared-variable value.
    ///
    /// No errors (null/empty/whitespace — the first attempt): returns
    /// <paramref name="baseValue"/> unchanged (empty string when null), so the
    /// rendered prompt is byte-identical to a call without feedback.
    ///
    /// With errors (a retry): appends a delimited block —
    /// <c>{base}\n\n## Previous attempt failed validation — fix these issues\n- err1\n- err2</c>.
    /// Individual error messages flow through verbatim; the "; " join separator
    /// applied by the validate step is unpacked into one bullet per error.
    /// When <paramref name="baseValue"/> is empty the block is returned without
    /// leading blank lines.
    /// </summary>
    public static string AppendFeedback(string? baseValue, string? validationErrors)
    {
        var baseText = baseValue ?? string.Empty;
        if (string.IsNullOrWhiteSpace(validationErrors))
            return baseText;

        var bullets = validationErrors
            .Split(ErrorJoinSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => "- " + e);

        var block = FeedbackHeader + "\n" + string.Join("\n", bullets);
        return baseText.Length == 0 ? block : baseText + "\n\n" + block;
    }
}
