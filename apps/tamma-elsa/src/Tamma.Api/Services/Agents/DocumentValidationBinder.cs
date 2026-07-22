using System.Text.Json;
using Tamma.Core.Documents;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 39-9 (Design Decision D2) — composes the <see cref="DocumentContentValidation"/>
/// delegate API-side from a document-type wire KEY. A validator delegate cannot ride
/// HTTP from the engine, so the wire carries only the key; this binder resolves it to
/// an <see cref="IDocumentType"/> via <see cref="DocumentTypeRegistry"/> and wraps
/// <see cref="IDocumentType.Validate"/> behind a fence-strip + <see cref="JsonDocument"/>
/// parse front.
///
/// <para><b>Fail-loud (D2).</b> An unknown / unregistered key throws the registry's
/// <c>TammaError</c> — the endpoint maps that to the <c>AGENT_UNRESOLVED</c> 422-in-200
/// envelope (fail-closed; never "skip validation"). A null / empty key means "no
/// validation" and returns <c>null</c> (the default for the 30+ existing dispatchers).</para>
///
/// <para><b>Unparseable payload.</b> When the produced document is not JSON (after
/// stripping a markdown code fence), the delegate does NOT throw — it returns an
/// invalid result carrying a synthetic <c>PAYLOAD_NOT_JSON</c> violation, so the
/// repair ring can feed it back to the model like any other violation.</para>
/// </summary>
public static class DocumentValidationBinder
{
    /// <summary>The synthetic violation code emitted when the produced document is
    /// not parseable JSON.</summary>
    public const string PayloadNotJsonCode = "PAYLOAD_NOT_JSON";

    /// <summary>
    /// Compose the document-content validation seam for <paramref name="documentTypeKey"/>.
    /// Null / empty ⇒ <c>null</c> (no validation). Otherwise resolves the type
    /// (fail-loud on an unknown key) and returns a delegate over its validator.
    /// </summary>
    public static DocumentContentValidation? Bind(string? documentTypeKey)
    {
        if (string.IsNullOrWhiteSpace(documentTypeKey))
        {
            return null;
        }

        var key = documentTypeKey.Trim();

        // Fail-loud (D2): an unknown wire string or an unregistered key throws a
        // TammaError here (DOCUMENT.TYPE.UNKNOWN / DOCUMENT.TYPE.NOT_REGISTERED),
        // which the endpoint catches and maps to a 422 envelope.
        var documentType = DocumentTypeRegistry.Resolve(key);

        return new DocumentContentValidation(
            key,
            payload => Validate(documentType, payload));
    }

    private static DocumentValidationResult Validate(IDocumentType documentType, string producedText)
    {
        var json = ExtractJson(producedText);

        JsonElement element;
        try
        {
            using var doc = JsonDocument.Parse(json);
            element = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(
                new DocumentViolation(
                    PayloadNotJsonCode,
                    "The produced document is not valid JSON. Re-emit the document as a single JSON object."));
        }

        return documentType.Validate(element);
    }

    /// <summary>
    /// Strip a leading/trailing markdown code fence (<c>```json … ```</c> or
    /// <c>``` … ```</c>) so a fenced model response still parses. Precedent:
    /// <c>ApplyReviewFixesActivity.ExtractJson</c>.
    /// </summary>
    private static string ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "{}";
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^3].TrimEnd();
            }
        }

        return trimmed;
    }
}
