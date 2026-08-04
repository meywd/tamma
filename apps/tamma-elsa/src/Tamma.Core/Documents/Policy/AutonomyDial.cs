namespace Tamma.Core.Documents.Policy;

/// <summary>
/// THE validated autonomy dial range (Story 43-1; epic-43 decision D3). The range
/// is <c>[1,100]</c> — widened from <c>[70,100]</c> by Story 43-11 (the one-line
/// edit 43-1 promised: <see cref="Min"/> <c>70 → 1</c>). Nothing else in
/// production C#, SQL or TypeScript may restate a bound — no DataAnnotations
/// <c>[Range]</c>, no DB CHECK constraint, no TypeScript range validator.
///
/// <para>
/// The model deliberately carries NO lower-bound-below-<see cref="Min"/> concept
/// (no <c>AbsoluteMin</c>, no "widened range" flag): <c>[Min,Max]</c> is one named
/// constant pair, and widening WAS editing <see cref="Min"/>. Every catalogued
/// dial action now carries an explicitly-chosen level in <c>[1,100]</c>, so moving
/// the dial changes what the system does by itself — Story 43-11 M2.
/// </para>
///
/// <para>
/// <see cref="Min"/> is NOT the shipped default dial: a fresh deployment ships at
/// <c>AcceptanceDefaults.DefaultAutonomyLevel</c> (70), which is a separate,
/// higher default. <see cref="Min"/> being below it is pinned by
/// <c>AutonomyDialTests.Min_is_below_the_shipped_default</c> so the two concepts
/// can never re-fuse.
/// </para>
///
/// <para>
/// NOTE: <see cref="AlwaysHuman"/> is derived from <see cref="Max"/>, so the
/// one-line claim holds DOWNWARD only — raising <see cref="Max"/> would silently
/// reinterpret every stored <c>101</c> as an ordinary threshold. Only the
/// downward case was asked for; the claim is not unconditional. The sentinel is
/// NOT deleted (Story 43-11 M6): it still serves <c>ActionCatalog.UnclassifiedFallback</c>,
/// the fail-closed unreadable-policy substitution, and the legacy always-escalate
/// floor. What ended is its use as a shipped DESCRIPTOR default — no catalog row
/// carries it any more.
/// </para>
///
/// <para>Pinned by <c>AutonomyDialTests</c> (Tamma.Core.Tests).</para>
/// </summary>
public static class AutonomyDial
{
    /// <summary>
    /// Lowest valid autonomy level. Widened to 1 by Story 43-11 (was 70); the
    /// supervised default dial is <c>AcceptanceDefaults.DefaultAutonomyLevel</c>
    /// (70), a separate constant.
    /// </summary>
    public const int Min = 1;

    /// <summary>Highest valid autonomy level (full auto).</summary>
    public const int Max = 100;

    /// <summary>
    /// Sentinel threshold meaning "a person decides at every level in the
    /// validated range" — a LEGAL threshold value (not a nullable, not a magic
    /// number), strictly above <see cref="Max"/> so <c>currentDial &gt;= MinAutonomy</c>
    /// is false at every valid dial position.
    /// </summary>
    public const int AlwaysHuman = Max + 1;

    /// <summary>Is <paramref name="level"/> a valid dial position (<c>[Min, Max]</c>)?</summary>
    public static bool IsValidLevel(int level) => level >= Min && level <= Max;

    /// <summary>
    /// Is <paramref name="threshold"/> a valid per-action minimum-autonomy
    /// threshold? Accepts <c>[Min, Max]</c> OR exactly <see cref="AlwaysHuman"/> —
    /// a closed set, not an open tail (<c>Max + 2</c> is rejected).
    /// </summary>
    public static bool IsValidThreshold(int threshold) =>
        (threshold >= Min && threshold <= Max) || threshold == AlwaysHuman;

    /// <summary>Every valid dial position, <see cref="Min"/> through <see cref="Max"/> inclusive.</summary>
    public static IEnumerable<int> ValidLevels() => Enumerable.Range(Min, Max - Min + 1);
}
