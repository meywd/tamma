using System.Text;
using Tamma.Core.Documents;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 39-9 (AC8, Design Decision D9) — the PURE, deterministic composer of the
/// harness-generated repair message appended to the conversation when a produced
/// document fails its deterministic validator.
///
/// <para>Contract (golden-pinned by <c>RepairMessageComposerTests</c>): a fixed
/// instruction preamble + one line per violation (<c>- [{Code}] {Message}</c>,
/// verbatim, in input order) + a fixed re-emit instruction. Same violations in ⇒
/// byte-identical message out. It contains ONLY validator output plus fixed text —
/// NEVER a provider error body (those live on a different axis,
/// <c>NormalizedLlmResponse.ErrorMessage</c>) and NEVER credentials. Because a
/// violation message can quote model output, the CALLER runs the composed message
/// through <c>ToolOutputHelper.RedactSecrets</c> before appending it (the runner
/// append site — D9), keeping this function free of I/O and side effects.</para>
/// </summary>
public static class RepairMessageComposer
{
    // Fixed template text. Any change here is a CONSCIOUS edit — the golden test
    // pins the exact output so prompt drift cannot slip through silently.
    private const string Preamble =
        "The document you produced did not pass validation. The following problems were found:";

    private const string ReEmitInstruction =
        "Fix every problem listed above and re-emit the COMPLETE corrected document. " +
        "Output only the corrected document — do not include explanations, apologies, or commentary.";

    /// <summary>
    /// Compose the deterministic repair message for <paramref name="violations"/>.
    /// Pure and order-preserving; the caller redacts the result before appending.
    /// </summary>
    public static string Compose(IReadOnlyList<DocumentViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);

        var sb = new StringBuilder();
        sb.Append(Preamble);
        foreach (var v in violations)
        {
            sb.Append('\n');
            sb.Append("- [").Append(v.Code).Append("] ").Append(v.Message);
        }
        sb.Append('\n').Append('\n');
        sb.Append(ReEmitInstruction);
        return sb.ToString();
    }
}
