using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;

namespace Tamma.Core.Actions;

/// <summary>
/// The permitted git subcommand vocabulary (Story 43-2 AC8) — the same 14 names
/// as <c>GitOperationsTool.AllowedSubcommands</c>
/// (<c>Tamma.Activities/LlmCall/Tools/GitOperationsTool.cs</c>), each graded
/// <c>read</c> or <c>write</c> so <see cref="ToolAction.GitOperationsRead"/> /
/// <see cref="ToolAction.GitOperationsWrite"/> can be gated independently.
/// Fourteen in, fourteen out — this enum introduces NO policy change; parity with
/// the tool's private <c>HashSet</c> is pinned by
/// <c>Tamma.Activities.Tests/Actions/GitSubcommandParitySweepTests</c>.
///
/// <para>
/// NOTE: <c>GitOperationsTool</c> matches subcommands CASE-INSENSITIVELY
/// (<c>StringComparer.OrdinalIgnoreCase</c>) while <c>EnumWire</c> parsing is
/// ordinal case-sensitive. The Story 43-4 refactor that makes the tool read from
/// this enum must normalize to lower-case before parsing, or <c>"STATUS"</c> —
/// accepted today — would start being rejected.
/// </para>
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<GitSubcommand>))]
public enum GitSubcommand
{
    [Wire("status")] Status,
    [Wire("diff")] Diff,
    [Wire("log")] Log,
    [Wire("add")] Add,
    [Wire("commit")] Commit,
    [Wire("push")] Push,
    [Wire("branch")] Branch,
    [Wire("checkout")] Checkout,
    [Wire("stash")] Stash,
    [Wire("show")] Show,
    [Wire("fetch")] Fetch,
    [Wire("pull")] Pull,
    [Wire("rev-parse")] RevParse,
    [Wire("ls-files")] LsFiles,
}

/// <summary>Whether a <see cref="GitSubcommand"/> reads or writes (Story 43-2 AC8).</summary>
public enum GitSubcommandGrade
{
    Read,
    Write,
}

/// <summary><see cref="GitSubcommand"/> helpers.</summary>
public static class GitSubcommandExtensions
{
    /// <summary>The canonical wire string for <paramref name="subcommand"/>.</summary>
    public static string ToWire(this GitSubcommand subcommand) => EnumWire<GitSubcommand>.ToWire(subcommand);

    /// <summary>
    /// The read/write grade per Story 43-2 AC8:
    /// <c>status/diff/log/show/rev-parse/ls-files/fetch/branch</c> read;
    /// <c>add/commit/push/checkout/stash/pull</c> write. (<c>fetch</c> and
    /// <c>branch</c> mutate only local refs, never the remote — the grade tracks
    /// remote/workspace-content consequence, which is what the
    /// <see cref="ToolAction"/> split gates.)
    /// </summary>
    public static GitSubcommandGrade Grade(this GitSubcommand subcommand) => subcommand switch
    {
        GitSubcommand.Status => GitSubcommandGrade.Read,
        GitSubcommand.Diff => GitSubcommandGrade.Read,
        GitSubcommand.Log => GitSubcommandGrade.Read,
        GitSubcommand.Show => GitSubcommandGrade.Read,
        GitSubcommand.RevParse => GitSubcommandGrade.Read,
        GitSubcommand.LsFiles => GitSubcommandGrade.Read,
        GitSubcommand.Fetch => GitSubcommandGrade.Read,
        GitSubcommand.Branch => GitSubcommandGrade.Read,
        GitSubcommand.Add => GitSubcommandGrade.Write,
        GitSubcommand.Commit => GitSubcommandGrade.Write,
        GitSubcommand.Push => GitSubcommandGrade.Write,
        GitSubcommand.Checkout => GitSubcommandGrade.Write,
        GitSubcommand.Stash => GitSubcommandGrade.Write,
        GitSubcommand.Pull => GitSubcommandGrade.Write,
        _ => throw new ArgumentOutOfRangeException(nameof(subcommand), subcommand,
            "Ungraded git subcommand — grade every new member here (Story 43-2 AC8)."),
    };
}
