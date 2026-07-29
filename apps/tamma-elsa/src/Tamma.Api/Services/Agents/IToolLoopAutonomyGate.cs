using Tamma.Core.Actions;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Seam B — the tool-dispatch autonomy gate (Epic 43; Story 43-4 slice of the
/// 43-9 enforcement plan). Evaluated by <see cref="InlineToolLoopRunner"/> for
/// every model-emitted tool call, AFTER sanitization/validation and BEFORE the
/// parallel/sequential execution fork, and NEVER nested inside the optional
/// validator block — the gate must be present even when every optional
/// collaborator on that path is absent, which is why the runner takes it as a
/// REQUIRED constructor parameter (epic binding decision; contrast every other
/// nullable collaborator).
///
/// <para>The outcome is named <b>Denied</b>, not RequiresHuman: there is no
/// human wait on the tool-dispatch path, and calling it escalation would be a
/// lie (epic README, Seam B). A denial becomes a rejected-tool-call entry that
/// the loop's existing machinery feeds back to the model as a tool result — no
/// exception, no new plumbing.</para>
/// </summary>
public interface IToolLoopAutonomyGate
{
    /// <summary>
    /// Decide whether the emitted tool call may execute automatically.
    /// </summary>
    /// <param name="toolName">The tool name exactly as the model emitted it.</param>
    /// <param name="argumentsJson">The (sanitized) call arguments — consulted for
    /// the <c>git_operations</c> read/write subcommand split only.</param>
    ToolLoopGateDecision Evaluate(string toolName, string? argumentsJson);
}

/// <summary>Gate outcome for one tool call. See <see cref="IToolLoopAutonomyGate"/>.</summary>
public enum ToolLoopGateOutcome
{
    /// <summary>The call may execute automatically at the current dial.</summary>
    Allowed,

    /// <summary>The call may NOT execute automatically; it is rejected back to the model.</summary>
    Denied,
}

/// <summary>
/// One gate decision, with the evaluated policy inputs for logging/audit tags.
/// </summary>
/// <param name="Outcome">Allowed or Denied.</param>
/// <param name="ActionKey">The resolved catalog key, when the name resolved.</param>
/// <param name="MinAutonomy">The effective minimum-autonomy threshold applied, when one was.</param>
/// <param name="Dial">The dial position the decision was taken at.</param>
/// <param name="Reason">Machine-readable reason tag (e.g. <c>below-min-autonomy</c>, <c>uncatalogued</c>).</param>
public sealed record ToolLoopGateDecision(
    ToolLoopGateOutcome Outcome,
    ActionKey? ActionKey,
    int? MinAutonomy,
    int Dial,
    string Reason)
{
    /// <summary>Convenience: is this a denial?</summary>
    public bool IsDenied => Outcome == ToolLoopGateOutcome.Denied;
}
