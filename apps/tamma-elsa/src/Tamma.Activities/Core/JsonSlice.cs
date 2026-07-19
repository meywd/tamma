namespace Tamma.Activities.Core;

/// <summary>
/// Shared JSON-object slicer for LLM text responses. LLMs routinely wrap the JSON
/// they were asked for in markdown fences (<c>```json ... ```</c>) or prose — feeding
/// the whole reply to <c>JsonSerializer</c> throws on the leading backticks/text even
/// though a perfectly valid object is embedded. This mirrors the exact
/// first-<c>'{'</c>-to-last-<c>'}'</c> idiom the *Parsing.cs classes
/// (<c>ClarifyParsing</c>, <c>ResearchParsing</c>, <c>DecompositionParsing</c>,
/// <c>DesignParsing</c>, <c>AmbiguityParsing</c>) already use, extracted so activity
/// parse methods can share it instead of re-inlining it.
/// </summary>
public static class JsonSlice
{
    /// <summary>
    /// Returns the substring from the first <c>'{'</c> to the last <c>'}'</c> of
    /// <paramref name="text"/>, or <c>null</c> when no such slice exists (empty /
    /// whitespace input, no braces, or last <c>'}'</c> not after first <c>'{'</c>).
    /// Callers that want fail-identical behaviour on garbage should fall back to the
    /// original text (<c>JsonSlice.ExtractObject(s) ?? s</c>) so deserialization
    /// fails the same way it did before slicing was introduced.
    /// </summary>
    public static string? ExtractObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var objStart = text.IndexOf('{');
        var objEnd = text.LastIndexOf('}');
        if (objStart < 0 || objEnd <= objStart)
            return null;

        return text[objStart..(objEnd + 1)];
    }
}
