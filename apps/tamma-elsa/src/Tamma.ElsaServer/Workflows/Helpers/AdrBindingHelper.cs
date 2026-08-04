using System.Text;
using System.Text.Json;
using Tamma.Core.Documents.Types;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 41-9 — the PURE, Elsa-free decision core of the <c>adr-authoring</c> binding, and the
/// REFERENCE SHAPE for the prose family (41-4, 41-5, 41-8's narrative, 41-22, 41-24, 41-25,
/// 41-26 all inherit it). Every function is TOTAL and FAIL-CLOSED — an unreadable body yields
/// the conservative projection, never a throw out of a routing lambda.
///
/// <para><see cref="LifecycleBindingHelper.ReadLifecycleResult"/> /
/// <see cref="LifecycleBindingHelper.IsAccepted"/> are reused verbatim — do NOT re-implement a
/// lifecycle-exit reader here.</para>
/// </summary>
public static class AdrBindingHelper
{
    /// <summary>The producer scope suffix that isolates this binding's prose slice (D3).</summary>
    public const string ProducerScope = "adr";

    /// <summary>The prose <c>kind</c> this binding produces (41-1c's closed vocabulary).</summary>
    public static readonly string Kind = ProseKind.Adr.ToWire();

    /// <summary>The default prose <c>audience</c> for an ADR (41-1c's closed vocabulary).</summary>
    public static readonly string DefaultAudience = ProseAudience.Engineering.ToWire();

    /// <summary>
    /// Resolve the caller-supplied audience against 41-1c's CLOSED vocabulary. An empty or
    /// out-of-vocabulary value falls back to <see cref="DefaultAudience"/> rather than being
    /// forwarded — an unknown audience is a hard <c>PROSE_AUDIENCE_OUT_OF_VOCABULARY</c>
    /// violation at validate, and a caller typo should not burn a repair round. A value the
    /// model then IGNORES still fails validation loudly, so this is a caller-input guard, not a
    /// silent normalisation of the model's reply.
    /// </summary>
    public static string ResolveAudience(string? requested)
        => ProseAudienceExtensions.TryParse(requested?.Trim(), out var audience)
            ? audience.ToWire()
            : DefaultAudience;

    /// <summary>
    /// D2/D3 — compose the seed context the ADR is written from: the accepted <c>design</c>
    /// (41-10's output, which <c>design-proposal</c> already produces today) and the accepted
    /// <c>findings</c>, each under a labelled heading. Both are OPTIONAL — an ADR is writable
    /// from the work item alone. Rides the DECLARED <c>findings</c> carrier
    /// (<c>write-adr.md</c> declares <c>role, workItemJson, findings, audience</c>), which is
    /// also <c>feedbackVariableName</c>, so repair/revise notes are not render-dropped.
    /// </summary>
    public static string BuildDecisionContext(string? designJson, string? findingsJson, string? decisionContext)
    {
        var design = Normalize(designJson);
        var findings = Normalize(findingsJson);
        var extra = decisionContext?.Trim() ?? "";

        var sb = new StringBuilder();
        if (extra.Length > 0)
        {
            sb.Append("## Decision Context\n");
            sb.Append(extra);
        }
        if (design.Length > 0)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("## Accepted Design\n");
            sb.Append(design);
        }
        if (findings.Length > 0)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("## Accepted Findings\n");
            sb.Append(findings);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Project the accepted prose payload for the binding's <c>adrJson</c> output. The whole
    /// <c>{kind, audience, title, body}</c> envelope is surfaced (not just the markdown body):
    /// the audience tag is the point of the prose type, and a consumer that drops it cannot
    /// filter. Fail-closed <c>""</c> on empty / unreadable / non-object input.
    /// </summary>
    public static string ProjectAdrBody(string? documentJson)
    {
        if (string.IsNullOrWhiteSpace(documentJson))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(documentJson!);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.GetRawText() : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// The prose <c>audience</c> the accepted document actually carries — the value the
    /// <c>ADR.ACCEPTED</c> event tags and a lineage read filters on. Fail-closed <c>""</c>.
    /// </summary>
    public static string ReadAudience(string? documentJson)
    {
        if (string.IsNullOrWhiteSpace(documentJson))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(documentJson!);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("audience", out var audience) &&
                   audience.ValueKind == JsonValueKind.String
                ? audience.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// The failure detail for a non-accepted ADR exit — names the lifecycle status and the typed
    /// outcome wire, so a degraded exit points at a typed escalation
    /// (<c>validation-exhausted</c> / <c>rounds-exhausted</c> / <c>review-undecidable</c>),
    /// never a dead terminal (AC2).
    /// </summary>
    public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit)
        => string.IsNullOrWhiteSpace(exit.Outcome)
            ? $"ADR lifecycle exited '{exit.Status}' without acceptance."
            : $"ADR lifecycle exited '{exit.Status}' with outcome '{exit.Outcome}'.";

    private static string Normalize(string? json)
    {
        var trimmed = json?.Trim() ?? "";
        // The 39-14 read seam reports "not found" as the empty carrier "{}".
        return trimmed is "" or "{}" or "[]" or "null" ? "" : trimmed;
    }
}
