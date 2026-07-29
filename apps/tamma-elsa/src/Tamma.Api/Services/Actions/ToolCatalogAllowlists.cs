namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-4 (AC5, D5) — the two SHRINK-ONLY ratchets consumed by
/// <see cref="ActionCatalogStartupValidator"/>. Both are count-pinned by
/// <c>ToolCatalogAllowlistTests</c> (the <c>ContractBindingTests</c> ratchet
/// idiom, plus the count assertion that harness lacks): adding an entry fails
/// the pin, and an entry whose justification has gone stale (the tool IS now
/// DI-registered / a real executor now carries a defensive alias's name) fails
/// the staleness checks. Never grow these to make a boot failure go away —
/// the boot failure is the feature.
/// </summary>
internal static class ToolCatalogAllowlists
{
    /// <summary>One ratchet entry: the key/name plus its cited justification.</summary>
    internal sealed record Entry(string Key, string Justification);

    /// <summary>
    /// Catalogued <c>tool:*</c> members that deliberately have NO DI-registered
    /// executor. Exactly one entry.
    /// </summary>
    public static readonly IReadOnlyList<Entry> NotDiRegisteredTools = new[]
    {
        new Entry(
            "tool:get_acceptance_rules",
            "39-5 D6: GetAcceptanceRulesToolFactory mints principal-bound instances per " +
            "tenant-agent session; a singleton registration would carry no principal " +
            "(Tamma.Api/Program.cs, the comment above the tool registrations)."),
    };

    /// <summary>
    /// <c>ToolCallValidator.ShellToolNames</c> members that name no executor and
    /// resolve to no catalog member BY DESIGN: their membership is about
    /// triggering <c>ActionGate</c>'s denylist for a shell-shaped tool call, not
    /// about resolving policy — conflating the two is exactly the trap (D5).
    /// Note <c>bash</c> is here even though <c>Bash</c> is an alias: the alias
    /// map is consulted first, so <c>bash</c> resolves and this entry is the
    /// defensive-membership record, kept for the day the alias is removed.
    /// </summary>
    public static readonly IReadOnlyList<Entry> KnownDefensiveAliases = new[]
    {
        "execute_shell_command", "run_command", "shell", "exec", "bash", "terminal",
        "run_shell", "execute_command", "system_command", "run_code", "execute", "cmd",
    }
    .Select(name => new Entry(
        name,
        "defensive alias: triggers ActionGate's denylist for a shell-shaped tool call; " +
        "names no executor by design (ToolCallValidator.ShellToolNames)."))
    .ToArray();
}
