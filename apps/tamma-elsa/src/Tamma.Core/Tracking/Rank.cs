using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC9/AC10 — the fractional-index ordering algebra. A rank is a
/// non-empty base-62 string over <see cref="Alphabet"/> with no trailing
/// <c>'0'</c> (the canonical-form invariant: <c>"a0"</c> denotes the same
/// fraction as <c>"a"</c> but sorts after it, so trailing zeros would break
/// strictness). Ranks sort by <b>ordinal</b> string comparison; a drag is one
/// <c>UPDATE</c>; ordering is <c>ORDER BY</c> with no application-side sort.
///
/// <para><b>The collation trap (44-1's obligation):</b> the alphabet is chosen
/// so that ordinal comparison in C# (<see cref="StringComparer.Ordinal"/>) and
/// Postgres <c>ORDER BY</c> agree — which they do <b>only under the
/// <c>C</c> collation</b>. Under <c>en_US.UTF-8</c> Postgres interleaves case
/// (<c>a</c> before <c>B</c>) and board order silently diverges from API order.
/// 44-1 must create <b>both</b> rank columns — <c>work_items."Rank"</c> (flat
/// project-backlog position) and <c>work_items."SiblingRank"</c> (position among
/// siblings under the same parent, null parent included) — <c>COLLATE "C"</c>.
/// One algebra, two columns (AC10).</para>
///
/// <para><b>There is no <c>Last()</c>.</b> A <c>Last()</c> returning a fixed
/// sentinel collides on two consecutive appends — the exact failure the epic's
/// D7 rejects <c>double</c> for (IEEE-754 midpointing exhausts in ~52
/// insertions). Appending requires the caller's current maximum:
/// <see cref="Append"/> makes that parameter unavoidable at the call site;
/// <see cref="Prepend"/> is its mirror.</para>
/// </summary>
public static class Rank
{
    /// <summary>
    /// The base-62 digit alphabet, in ascending ASCII/ordinal order:
    /// <c>0-9</c> &lt; <c>A-Z</c> &lt; <c>a-z</c>.
    /// </summary>
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private const int Base = 62;

    /// <summary>The rank for the first item of an empty ordering: <c>Between(null, null)</c>.</summary>
    public static string First() => Between(null, null);

    // ── Append/Prepend convention (corrected 2026-07-28, BEFORE Story 44-1
    // freezes storage — no persisted ranks exist yet, so this convention change
    // needs no migration) ─────────────────────────────────────────────────────
    //
    // Append/Prepend used to delegate to Between with an open side, midpointing
    // toward the fixed bound: each append halved the remaining headroom, so a
    // rank grew one digit every ~5-6 appends — a 10k append chain produced
    // ~2,001-character ranks (Θ(n) per rank, Θ(n²) stored characters over the
    // chain). They now use the standard fractional-indexing edge convention
    // (digit-increment/decrement with carry, as LexoRank extends): amortized
    // O(log n) — one digit per ~61 edge insertions, a 10k chain tops out at
    // 165 characters. Interior Between is unchanged (its bisection is already
    // optimal), and the two conventions interoperate: every output is a
    // canonical base-62 string ordered by plain ordinal comparison, so any mix
    // of old-style and new-style ranks still orders and bisects correctly
    // (pinned by RankTests' mixed-history test).

    /// <summary>
    /// A canonical rank strictly after <paramref name="currentMax"/> (the
    /// caller's current maximum rank, or null for an empty ordering).
    /// Digit-increment with carry: increment the last non-<c>'z'</c> digit and
    /// truncate what follows; an all-<c>'z'</c> rank extends with <c>'1'</c>.
    /// Amortized O(log n) rank length over an n-append chain (~n/61 digits) —
    /// NOT equivalent to <c>Between(currentMax, null)</c>, which bisects toward
    /// the upper bound and grows linearly. See the convention note above.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="currentMax"/> is non-null and not a canonical rank.
    /// </exception>
    public static string Append(string? currentMax)
    {
        if (currentMax is null)
            return First();
        if (!IsValid(currentMax))
            throw new ArgumentException($"'{currentMax}' is not a canonical rank.", nameof(currentMax));

        // Last digit that still has headroom; everything after it is 'z'.
        var i = currentMax.Length - 1;
        while (i >= 0 && currentMax[i] == 'z')
            i--;

        return i < 0
            ? currentMax + '1' // all-'z': extend one digit ('1', never '0' — trailing zeros are non-canonical)
            : currentMax[..i] + Alphabet[DigitIndex(currentMax[i]) + 1];
    }

    /// <summary>
    /// A canonical rank strictly before <paramref name="currentMin"/> (the
    /// caller's current minimum rank, or null for an empty ordering). The
    /// mirror of <see cref="Append"/>: decrement the last digit while it stays
    /// canonical (≥ <c>'1'</c> after the decrement); a rank ending in
    /// <c>'1'</c> steps down into the next digit position with <c>"0z"</c>.
    /// Amortized O(log n) rank length over an n-prepend chain — NOT equivalent
    /// to <c>Between(null, currentMin)</c>. See the convention note above.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="currentMin"/> is non-null and not a canonical rank.
    /// </exception>
    public static string Prepend(string? currentMin)
    {
        if (currentMin is null)
            return First();
        if (!IsValid(currentMin))
            throw new ArgumentException($"'{currentMin}' is not a canonical rank.", nameof(currentMin));

        var last = DigitIndex(currentMin[^1]);
        return last >= 2
            ? currentMin[..^1] + Alphabet[last - 1]
            : currentMin[..^1] + "0z"; // last digit is '1': "…0z" sorts strictly between "…0" (non-canonical) and "…1"
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is a canonical rank: non-empty,
    /// alphabet characters only, no trailing <c>'0'</c>.
    /// </summary>
    public static bool IsValid([NotNullWhen(true)] string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate[^1] == '0')
            return false;

        foreach (var c in candidate)
        {
            if (DigitIndex(c) < 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// A canonical rank sorting strictly between <paramref name="left"/> and
    /// <paramref name="right"/> under ordinal comparison. <c>null</c> left means
    /// "before everything"; <c>null</c> right means "after everything".
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A non-null neighbour is not a canonical rank (see <see cref="IsValid"/>),
    /// or <paramref name="left"/> does not sort strictly before
    /// <paramref name="right"/>.
    /// </exception>
    public static string Between(string? left, string? right)
    {
        if (left is not null && !IsValid(left))
            throw new ArgumentException($"'{left}' is not a canonical rank.", nameof(left));
        if (right is not null && !IsValid(right))
            throw new ArgumentException($"'{right}' is not a canonical rank.", nameof(right));
        if (left is not null && right is not null && string.CompareOrdinal(left, right) >= 0)
            throw new ArgumentException(
                $"left ('{left}') must sort strictly before right ('{right}') under ordinal comparison.");

        return Midpoint(left ?? string.Empty, right);
    }

    /// <summary>
    /// The midpoint algorithm over canonical base-62 fractions. A rank string
    /// <c>d1 d2 …</c> denotes the fraction <c>Σ digit(di)/62^i</c> in (0, 1);
    /// the empty string is the exclusive lower bound (0) and a null
    /// <paramref name="right"/> the exclusive upper bound (1). With trailing
    /// zeros forbidden, fraction order and ordinal string order coincide.
    /// </summary>
    private static string Midpoint(ReadOnlySpan<char> left, string? rightOrNull)
    {
        var result = new StringBuilder();
        var right = rightOrNull is null ? ReadOnlySpan<char>.Empty : rightOrNull.AsSpan();
        var hasRight = rightOrNull is not null;

        while (true)
        {
            if (hasRight)
            {
                // Copy the common prefix (left padded with '0' beyond its length).
                var n = 0;
                while (n < right.Length && (n < left.Length ? left[n] : '0') == right[n])
                    n++;

                if (n > 0)
                {
                    result.Append(right[..n]);
                    left = n < left.Length ? left[n..] : [];
                    right = right[n..];
                }
            }

            // First digits now differ: digitLeft < digitRight.
            var digitLeft = left.Length > 0 ? DigitIndex(left[0]) : 0;
            var digitRight = hasRight && right.Length > 0 ? DigitIndex(right[0]) : Base;

            if (digitRight - digitLeft > 1)
            {
                // Room at this position: emit the midpoint digit (round half up,
                // so the result is never '0') and stop.
                result.Append(Alphabet[(digitLeft + digitRight + 1) / 2]);
                return result.ToString();
            }

            if (hasRight && right.Length > 1)
            {
                // Consecutive digits but right has more digits: right's first
                // digit alone sorts strictly between (right[0] > '0' here, and
                // right has no trailing zero, so truncating it moves it down).
                result.Append(right[0]);
                return result.ToString();
            }

            // Consecutive digits and right is a single digit (or absent): emit
            // left's digit and descend into left's remainder with an open top.
            result.Append(Alphabet[digitLeft]);
            left = left.Length > 0 ? left[1..] : [];
            right = [];
            hasRight = false;
        }
    }

    private static int DigitIndex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        >= 'a' and <= 'z' => c - 'a' + 36,
        _ => -1,
    };
}
