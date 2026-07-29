using Tamma.Api.Services.Agents; // EnumWire<T> (historical namespace, Tamma.Core assembly)
using Tamma.Core.Actions;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-4 (AC1, D1/D3) — the RESOLUTION-ONLY map from an emitted/advertised
/// tool name to its catalog <see cref="ActionKey"/>, so policy (Story 43-5's
/// resolver, the Seam B tool-loop gate) can be evaluated under EITHER of the two
/// live tool vocabularies: the executor-registry names
/// (<c>file_read</c>/<c>file_write</c>/<c>search_code</c>/<c>shell_execute</c>/
/// <c>git_operations</c>/<c>run_tests</c>/<c>get_acceptance_rules</c>) and the
/// Claude-Code names <c>DefaultAgentConfig</c> advertises to the model
/// (<c>Read</c>/<c>Write</c>/<c>Edit</c>/<c>Bash</c>/<c>Grep</c>/<c>Glob</c>).
///
/// <para><b>THIS IS A POLICY MAP, NOT A RENAME.</b> It MUST NOT be applied to
/// <c>ManagedAgent.ToResolvedTools</c>' output, to <c>ResolvedTool.Name</c>, or
/// to the dictionary key in <c>ToolExecutorRegistry</c> — advertised names are
/// byte-identical before and after Story 43-4, pinned by
/// <c>AdvertisedToolNamesUnchangedTests</c> (including a source scan that fails
/// on any reference to this type from the advertisement path). Making the
/// Claude-Code names actually EXECUTE is a privilege expansion filed as a
/// separate story outside Epic 43; see
/// <c>docs/stories/epic-43/story-43-4/43-4-tool-vocabulary-reconciliation.md</c>.</para>
/// </summary>
internal static class ToolNameAliases
{
    private static ActionKey Key(ToolAction tool) => new(ActionNamespace.Tool, tool.ToWire());

    // Matching is OrdinalIgnoreCase — the same comparer ToolExecutorRegistry
    // keys its executor dictionary with (ToolExecutorRegistry.cs), so a name
    // the registry would dispatch is a name this map resolves.
    private static readonly Dictionary<string, ActionKey> s_map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Identity for every registry name.
            ["file_read"] = Key(ToolAction.FileRead),
            ["file_write"] = Key(ToolAction.FileWrite),
            ["search_code"] = Key(ToolAction.SearchCode),
            ["shell_execute"] = Key(ToolAction.ShellExecute),
            ["run_tests"] = Key(ToolAction.RunTests),
            ["get_acceptance_rules"] = Key(ToolAction.GetAcceptanceRules),

            // The Claude-Code advertised names (DefaultAgentConfig.Tools).
            ["Read"] = Key(ToolAction.FileRead),
            ["Write"] = Key(ToolAction.FileWrite),
            ["Edit"] = Key(ToolAction.FileWrite),
            ["Bash"] = Key(ToolAction.ShellExecute),
            ["Grep"] = Key(ToolAction.SearchCode),
            ["Glob"] = Key(ToolAction.SearchCode),

            // git_operations resolves PER SUBCOMMAND — see TryResolveGit. The
            // bare name (no parseable subcommand) resolves to the stricter
            // write member: fail-safe, stated in the TryResolveGit doc.
            ["git_operations"] = Key(ToolAction.GitOperationsWrite),
        };

    /// <summary>
    /// Resolve an emitted/advertised tool name to its catalog key
    /// (<c>OrdinalIgnoreCase</c>). <c>git_operations</c> resolves to the
    /// stricter <c>tool:git_operations.write</c> here; callers holding the
    /// call's <c>subcommand</c> argument should prefer
    /// <see cref="TryResolveGit"/> for the read/write split.
    /// </summary>
    public static bool TryResolve(string emittedName, out ActionKey key) =>
        s_map.TryGetValue(emittedName, out key);

    /// <summary>
    /// Resolve a <c>git_operations</c> call to <c>tool:git_operations.read</c>
    /// or <c>tool:git_operations.write</c> by its subcommand's
    /// <see cref="GitSubcommandExtensions.Grade"/> (Story 43-2 AC8's split).
    /// Matching tolerates the tool's own case-insensitive posture
    /// (<c>"STATUS"</c> resolves like <c>"status"</c> — the
    /// 2026-07-27 GitOperationsTool comparer trap). A null/blank/unknown
    /// subcommand resolves to <c>.write</c>, the stricter member — fail-safe:
    /// an unparseable git call must never be graded as a read.
    /// </summary>
    public static bool TryResolveGit(string? subcommand, out ActionKey key)
    {
        if (!string.IsNullOrWhiteSpace(subcommand)
            && EnumWire<GitSubcommand>.TryParse(subcommand.Trim().ToLowerInvariant(), out var parsed)
            && parsed.Grade() == GitSubcommandGrade.Read)
        {
            key = Key(ToolAction.GitOperationsRead);
            return true;
        }

        key = Key(ToolAction.GitOperationsWrite);
        return true;
    }

    /// <summary>
    /// The full alias map, for the startup validator and its tests (D3). Never
    /// exposed with a mutator.
    /// </summary>
    public static IReadOnlyDictionary<string, ActionKey> All => s_map;
}
