namespace Tamma.Core.Documents.Policy;

/// <summary>
/// THE validated autonomy dial range (Story 43-1; epic-43 decision D3). Widening
/// downward is a ONE-LINE change here (<see cref="Min"/>); nothing else in
/// production C#, SQL or TypeScript may restate a bound — no DataAnnotations
/// <c>[Range]</c>, no DB CHECK constraint, no TypeScript range validator.
///
/// <para>
/// The model deliberately carries NO lower-bound-below-<see cref="Min"/> concept
/// (no <c>AbsoluteMin</c>, no "widened range" flag): <c>[70,100]</c> is one named
/// constant pair, and widening IS editing <see cref="Min"/>.
/// </para>
///
/// <para>
/// NOTE: <see cref="AlwaysHuman"/> is derived from <see cref="Max"/>, so the
/// one-line claim holds DOWNWARD only — raising <see cref="Max"/> would silently
/// reinterpret every stored <c>101</c> as an ordinary threshold. Only the
/// downward case was asked for; the claim is not unconditional.
/// </para>
///
/// <para>Pinned by <c>AutonomyDialTests</c> (Tamma.Core.Tests).</para>
/// </summary>
public static class AutonomyDial
{
    /// <summary>Lowest valid autonomy level (the supervised baseline).</summary>
    public const int Min = 70;

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
