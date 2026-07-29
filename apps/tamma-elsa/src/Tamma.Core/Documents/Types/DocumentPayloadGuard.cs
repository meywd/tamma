using System.Text.Json;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// The shared fail-closed guard every <see cref="IDocumentType.Validate"/> body
/// runs under (Story 41-1b follow-up finding 4).
///
/// <para>
/// The defect it closes: each type guarded only the TOP-LEVEL null document
/// (<c>doc is null</c>) and then dereferenced per-element fields inside its loops
/// under a <c>JsonException</c>-only catch. A payload with a NULL ARRAY ELEMENT —
/// <c>{"criteria":[null]}</c>, <c>{"items":[null]}</c>, … — deserializes happily
/// (the list holds a null entry), so the first <c>criterion.Id</c> threw a
/// <see cref="NullReferenceException"/> straight out of <c>Validate</c>. Because
/// <c>DocumentLifecycleWorkflow</c> calls <c>Validate</c> unguarded, that
/// exception FAULTED the workflow instead of routing to the deterministic repair
/// ring — one null element from a model took down the run rather than earning a
/// repair turn. The shape was in every registered type, pre-existing ones
/// included, so the fix is one shared guard rather than N ad-hoc null checks.
/// </para>
///
/// <para>
/// Two layers, in order:
/// <list type="number">
/// <item><b>Structural pre-scan</b> (<see cref="FindNullArrayElement"/>) — the
/// primary mechanism. Walks the raw <see cref="JsonElement"/> BEFORE the type's
/// body runs and rejects any null element inside any array with the type's
/// <c>MALFORMED_PAYLOAD</c> code and a message naming the offending path, so
/// 39-9's repair turn can tell the model exactly which entry to fill in. A null
/// PROPERTY VALUE (<c>{"criteria":null}</c>) is deliberately NOT caught here —
/// every type already degrades that to <c>?? []</c> and reports its own
/// domain violation, and that outcome is preserved.</item>
/// <item><b>Widened catch</b> — the safety net for any other structurally
/// malformed member a body dereferences blindly (a null string where the DTO
/// declares non-nullable, a <see cref="JsonElement"/> accessor on the wrong
/// <see cref="JsonValueKind"/>, …). Structural exceptions become the same
/// <c>MALFORMED_PAYLOAD</c> violation. <c>TammaError</c> derives straight from
/// <see cref="Exception"/> and is NOT caught — a genuine invariant breach still
/// fails loud.</item>
/// </list>
/// </para>
///
/// <para>
/// A future type author inherits the behavior by writing the two-line shape every
/// registered type now uses — the body moves to <c>ValidateCore</c> and the
/// interface method delegates:
/// <code>
/// public DocumentValidationResult Validate(JsonElement payload) =&gt;
///     DocumentPayloadGuard.Run(payload, ValidateCore);
///
/// private DocumentValidationResult ValidateCore(JsonElement payload) { … }
/// </code>
/// <c>DocumentTypesNullElementSweepTests</c> enforces the property generically
/// over <c>DocumentTypeRegistry.All</c>, so a type that skips the wrapper goes
/// red rather than shipping the fault.
/// </para>
/// </summary>
internal static class DocumentPayloadGuard
{
    /// <summary>
    /// The single code every document type reports for a structurally malformed
    /// payload. Each type re-declares it as its own <c>public const string
    /// MalformedPayload</c> for call-site readability; the values are identical
    /// and pinned by the sweep test.
    /// </summary>
    internal const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>
    /// Run a document type's validation body fail-closed: a structurally
    /// malformed payload yields the type's <c>MALFORMED_PAYLOAD</c> violation,
    /// never an exception. A well-formed payload is handed to
    /// <paramref name="validateCore"/> untouched, so no currently-valid payload
    /// and no currently-reported violation changes.
    /// </summary>
    internal static DocumentValidationResult Run(
        JsonElement payload,
        Func<JsonElement, DocumentValidationResult> validateCore)
    {
        try
        {
            var nullPath = FindNullArrayElement(payload, "");
            if (nullPath is not null)
                return Malformed(
                    $"The payload has a null entry at '{nullPath}' — every list entry must be a complete " +
                    "value, never null. Fill the entry in or drop it from the list.");

            return validateCore(payload);
        }
        catch (Exception ex) when (IsStructural(ex))
        {
            return Malformed(
                "The payload is structurally malformed — a required member is missing, null, or of the " +
                "wrong shape, so the document could not be validated.");
        }
    }

    /// <summary>
    /// Depth-first scan for the FIRST null element inside any array, returning its
    /// path (e.g. <c>criteria[0]</c>, <c>flows[1].errorStates[0]</c>) or
    /// <c>null</c> when the payload has none. Enumeration follows document order,
    /// so the reported path is deterministic for a given payload. Recursion is
    /// bounded by <see cref="JsonDocument"/>'s own parse depth limit.
    /// </summary>
    private static string? FindNullArrayElement(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    var found = FindNullArrayElement(property.Value, childPath);
                    if (found is not null)
                        return found;
                }
                return null;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var itemPath = $"{path}[{index}]";
                    if (item.ValueKind == JsonValueKind.Null)
                        return itemPath;

                    var found = FindNullArrayElement(item, itemPath);
                    if (found is not null)
                        return found;

                    index++;
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// The exceptions a structurally malformed payload can raise while a body
    /// walks it. Deliberately a closed list rather than <c>catch (Exception)</c>:
    /// <c>TammaError</c> (a real invariant breach) and cancellation/host failures
    /// still propagate.
    /// </summary>
    private static bool IsStructural(Exception ex) => ex is
        JsonException or
        NullReferenceException or
        InvalidOperationException or
        ArgumentException or
        FormatException or
        OverflowException or
        IndexOutOfRangeException or
        KeyNotFoundException;

    private static DocumentValidationResult Malformed(string message) =>
        DocumentValidationResult.Invalid(new DocumentViolation(MalformedPayload, message));
}
