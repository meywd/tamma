using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC9/AC10 — the fractional-index algebra, property-style.
///
/// <para><b>44-1's obligation (D11, stated here so the storage story cannot miss
/// it):</b> ranks sort correctly only under ORDINAL comparison. Postgres agrees
/// with <see cref="StringComparer.Ordinal"/> only under the <c>C</c> collation —
/// under <c>en_US.UTF-8</c> case interleaves (<c>a</c> before <c>B</c>) and the
/// board order silently diverges from the API order. 44-1 must therefore create
/// <b>BOTH</b> rank columns — <c>work_items."Rank"</c> and
/// <c>work_items."SiblingRank"</c> — with <c>COLLATE "C"</c>, and its
/// Testcontainers test must insert generated ranks and assert SQL
/// <c>ORDER BY</c> matches <c>OrderBy(x =&gt; x, StringComparer.Ordinal)</c>.</para>
/// </summary>
[TestFixture]
public class RankTests
{
    private const int Insertions = 10_000;

    // Each appended digit covers at least 5 bisections (62 > 2^5); observed
    // growth is ~1 digit per 5-6 insertions for the adversarial patterns below.
    private const int LengthBound = Insertions / 5 + 10;

    [Test]
    public void Ten_thousand_midpoints_toward_the_left_never_collide()
    {
        // The adversarial pattern that exhausts a double in ~52 steps (D7/D11):
        // always insert between the FIXED left endpoint and the last result.
        var left = Rank.First();
        var right = Rank.Append(left);

        var generated = new List<string> { right };
        var current = right;
        for (var i = 0; i < Insertions; i++)
        {
            current = Rank.Between(left, current);
            generated.Add(current);
        }

        generated.Distinct(StringComparer.Ordinal).Should().HaveCount(generated.Count, "ranks must never collide");

        for (var i = 1; i < generated.Count; i++)
        {
            string.CompareOrdinal(generated[i], generated[i - 1])
                .Should().BeNegative(because: $"insertion {i} must sort strictly before its predecessor");
            string.CompareOrdinal(generated[i], left)
                .Should().BePositive(because: $"insertion {i} must stay strictly after the fixed left endpoint");
        }

        generated.Max(r => r.Length).Should().BeLessThanOrEqualTo(LengthBound);
        generated.Should().OnlyContain(r => Rank.IsValid(r));
    }

    [Test]
    public void Ten_thousand_midpoints_toward_the_right_never_collide()
    {
        var left = Rank.First();
        var right = Rank.Append(left);

        var generated = new List<string> { left };
        var current = left;
        for (var i = 0; i < Insertions; i++)
        {
            current = Rank.Between(current, right);
            generated.Add(current);
        }

        generated.Distinct(StringComparer.Ordinal).Should().HaveCount(generated.Count, "ranks must never collide");

        for (var i = 1; i < generated.Count; i++)
        {
            string.CompareOrdinal(generated[i], generated[i - 1])
                .Should().BePositive(because: $"insertion {i} must sort strictly after its predecessor");
            string.CompareOrdinal(generated[i], right)
                .Should().BeNegative(because: $"insertion {i} must stay strictly before the fixed right endpoint");
        }

        generated.Max(r => r.Length).Should().BeLessThanOrEqualTo(LengthBound);
        generated.Should().OnlyContain(r => Rank.IsValid(r));
    }

    [Test]
    public void Ordinal_sort_matches_insertion_intent()
    {
        // Build an ordering by inserting at random positions (seeded), then
        // verify that shuffling and ordinal-sorting reproduces the intended
        // order exactly — the property ORDER BY "Rank" COLLATE "C" relies on.
        var random = new Random(440);
        var ranks = new List<string>();

        for (var i = 0; i < 1_000; i++)
        {
            var index = random.Next(ranks.Count + 1);
            var left = index == 0 ? null : ranks[index - 1];
            var right = index == ranks.Count ? null : ranks[index];
            ranks.Insert(index, Rank.Between(left, right));
        }

        var shuffled = ranks.OrderBy(_ => random.Next()).ToList();
        var sorted = shuffled.OrderBy(x => x, StringComparer.Ordinal).ToList();

        sorted.Should().Equal(ranks, "ordinal sort must reproduce insertion intent");
    }

    [Test]
    public void Null_neighbours_are_defined()
    {
        var first = Rank.Between(null, null);
        Rank.IsValid(first).Should().BeTrue();
        Rank.First().Should().Be(first);

        var after = Rank.Between(first, null);
        string.CompareOrdinal(after, first).Should().BePositive();

        var before = Rank.Between(null, first);
        string.CompareOrdinal(before, first).Should().BeNegative();

        Rank.IsValid(after).Should().BeTrue();
        Rank.IsValid(before).Should().BeTrue();
    }

    [Test]
    public void Consecutive_appends_are_distinct_and_increasing()
    {
        // The regression test for the deleted Last() (AC9/D11): a fixed
        // sentinel would make two consecutive appends compare equal — the exact
        // failure D7 rejects double for. Append requires the caller's current
        // maximum, so each result is strictly greater than the last.
        var a = Rank.Append(null);
        var b = Rank.Append(a);
        var c = Rank.Append(b);

        string.CompareOrdinal(a, b).Should().BeNegative();
        string.CompareOrdinal(b, c).Should().BeNegative();
        new[] { a, b, c }.Distinct(StringComparer.Ordinal).Should().HaveCount(3);

        // Prepend is the mirror.
        var y = Rank.Prepend(null);
        var x = Rank.Prepend(y);
        var w = Rank.Prepend(x);
        string.CompareOrdinal(w, x).Should().BeNegative();
        string.CompareOrdinal(x, y).Should().BeNegative();
    }

    [Test]
    public void Append_and_prepend_are_between_with_an_open_side()
    {
        var anchor = Rank.First();
        Rank.Append(anchor).Should().Be(Rank.Between(anchor, null));
        Rank.Prepend(anchor).Should().Be(Rank.Between(null, anchor));
        Rank.Append(null).Should().Be(Rank.First());
        Rank.Prepend(null).Should().Be(Rank.First());
    }

    [Test]
    public void Never_emits_a_trailing_zero()
    {
        // Canonical-form invariant: "a0" denotes the same fraction as "a" but
        // sorts after it, so a trailing zero would break strictness. Checked
        // over a mixed generated population.
        var random = new Random(441);
        var ranks = new List<string> { Rank.First() };

        for (var i = 0; i < 10_000; i++)
        {
            var index = random.Next(ranks.Count + 1);
            var left = index == 0 ? null : ranks[index - 1];
            var right = index == ranks.Count ? null : ranks[index];
            var rank = Rank.Between(left, right);

            rank.Should().NotBeNullOrEmpty();
            rank[^1].Should().NotBe('0', because: "a trailing '0' is non-canonical");
            rank.Should().MatchRegex("^[0-9A-Za-z]+$");
            Rank.IsValid(rank).Should().BeTrue();

            ranks.Insert(index, rank);
        }
    }

    [Test]
    public void Invalid_neighbours_are_rejected_loud()
    {
        // Non-canonical inputs are rejected, never repaired — repairing would
        // silently reorder rows other rows were ranked against.
        FluentActions.Invoking(() => Rank.Between("", null)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Rank.Between(null, "")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Rank.Between("a0", null)).Should().Throw<ArgumentException>("trailing zero");
        FluentActions.Invoking(() => Rank.Between(null, "a!")).Should().Throw<ArgumentException>("outside the alphabet");
        FluentActions.Invoking(() => Rank.Between("é", null)).Should().Throw<ArgumentException>("outside the alphabet");

        // left must sort strictly before right.
        FluentActions.Invoking(() => Rank.Between("V", "V")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Rank.Between("W", "V")).Should().Throw<ArgumentException>();
    }

    [Test]
    public void IsValid_pins_the_canonical_form()
    {
        Rank.IsValid("V").Should().BeTrue();
        Rank.IsValid("0V").Should().BeTrue("leading zeros are meaningful (values below 1/62)");
        Rank.IsValid("zzz").Should().BeTrue();

        Rank.IsValid(null).Should().BeFalse();
        Rank.IsValid("").Should().BeFalse("the empty string is the exclusive lower bound, not a rank");
        Rank.IsValid("0").Should().BeFalse("trailing zero");
        Rank.IsValid("V0").Should().BeFalse("trailing zero");
        Rank.IsValid("V!").Should().BeFalse("outside the alphabet");
        Rank.IsValid("V ").Should().BeFalse();
    }

    [Test]
    public void Alphabet_is_ordinal_ascending()
    {
        // The property the whole algebra rests on: alphabet position order ==
        // ordinal char order (0-9 < A-Z < a-z in ASCII). If someone reorders
        // the alphabet, every comparison silently breaks — pin it.
        Rank.Alphabet.Should().HaveLength(62);
        Rank.Alphabet.ToCharArray().Should().BeInAscendingOrder();
        Rank.Alphabet.Distinct().Should().HaveCount(62);
    }
}
