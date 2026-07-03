using System.Globalization;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Audit;

namespace Tamma.Core.Tests.Audit;

/// <summary>
/// Story 37-2 (AC2) — byte-stability of the canonicalizer. The whole chain's
/// determinism rests on these.
/// </summary>
[TestFixture]
public class AuditRecordCanonicalizerTests
{
    private static AuditChainRecordView Sample(long seq = 1) => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Discriminator = "tenant",
        TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        UserId = null,
        ActionCode = "SECRET.REVEAL",
        Category = "security",
        Severity = "high",
        ActorUserId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        ActorEmailSnapshot = "actor@example.com",
        TargetType = "secret",
        TargetId = "abc-123",
        Outcome = "success",
        IpAddress = "203.0.113.7",
        UserAgent = "curl/8.0",
        OccurredAt = new DateTime(2026, 7, 2, 12, 34, 56, 789, DateTimeKind.Utc),
        SourceEventId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        SourceSequenceNumber = 4242,
        PayloadJson = """{"tags":{"a":1},"data":{"b":2}}""",
        ChainSequence = seq,
        PrevRecordHash = "ignored-not-in-canonical",
        RecordHash = "ignored-not-in-canonical",
    };

    [Test]
    public void Canonical_Is_Identical_Across_Two_Runs()
    {
        AuditRecordCanonicalizer.ToBytes(Sample())
            .Should().Equal(AuditRecordCanonicalizer.ToBytes(Sample()));
    }

    [Test]
    public void Canonical_Ignores_The_Chain_Linkage_Hashes()
    {
        var a = Sample() with { PrevRecordHash = "aaaa", RecordHash = "bbbb" };
        var b = Sample() with { PrevRecordHash = "cccc", RecordHash = "dddd" };
        AuditRecordCanonicalizer.ToBytes(a).Should().Equal(AuditRecordCanonicalizer.ToBytes(b),
            "prev/record hashes are the chain link, composed AROUND the canonical, not inside it");
    }

    [Test]
    public void Different_Payload_Produces_Different_Bytes()
    {
        var a = Sample();
        var b = Sample() with { PayloadJson = """{"tags":{"a":9},"data":{"b":2}}""" };
        AuditRecordCanonicalizer.ToBytes(a).Should().NotEqual(AuditRecordCanonicalizer.ToBytes(b));
    }

    [Test]
    public void Different_ChainSequence_Produces_Different_Bytes()
    {
        AuditRecordCanonicalizer.ToBytes(Sample(1))
            .Should().NotEqual(AuditRecordCanonicalizer.ToBytes(Sample(2)));
    }

    [Test]
    public void Concatenation_Ambiguity_Is_Prevented_By_Length_Prefix()
    {
        // Moving a character across two adjacent string fields must change the
        // bytes (length-prefixing defeats "ab"+"c" == "a"+"bc").
        var a = Sample() with { TargetType = "ab", TargetId = "c" };
        var b = Sample() with { TargetType = "a", TargetId = "bc" };
        AuditRecordCanonicalizer.ToBytes(a).Should().NotEqual(AuditRecordCanonicalizer.ToBytes(b));
    }

    [Test]
    public void Canonical_Is_Culture_Invariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR"); // dotted-I, comma decimals
            var turkish = AuditRecordCanonicalizer.ToBytes(Sample());
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = AuditRecordCanonicalizer.ToBytes(Sample());
            turkish.Should().Equal(invariant);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Test]
    public void Timestamp_Kind_Is_Normalized_To_Utc()
    {
        var utc = Sample() with
        {
            OccurredAt = new DateTime(2026, 7, 2, 12, 34, 56, 789, DateTimeKind.Utc),
        };
        var unspecified = Sample() with
        {
            OccurredAt = new DateTime(2026, 7, 2, 12, 34, 56, 789, DateTimeKind.Unspecified),
        };
        AuditRecordCanonicalizer.ToBytes(utc).Should().Equal(AuditRecordCanonicalizer.ToBytes(unspecified),
            "an Unspecified kind read back from Postgres must canonicalize as UTC");
    }
}
