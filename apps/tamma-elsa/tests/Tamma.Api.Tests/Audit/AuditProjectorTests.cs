using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Audit;
using Tamma.Data.Audit;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-1 (AC7, AC10, AC11) — pure unit tests for <see cref="AuditProjector"/>.
/// No DB: the projector is pure classification + redaction + ownership routing.
/// </summary>
[TestFixture]
public class AuditProjectorTests
{
    private readonly AuditProjector _projector = new();

    private static RawAuditEvent Raw(
        string type, Guid? tenantId = null,
        object? tags = null, object? data = null, long seq = 1) =>
        new(
            Guid.NewGuid(), type, tenantId,
            JsonSerializer.Serialize(tags ?? new { }),
            JsonSerializer.Serialize(data ?? new { }),
            new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc),
            seq);

    // ── AC7 — non-catalog events produce no record ──

    [Test]
    public void NonCatalog_Event_Yields_Null()
    {
        var raw = Raw("WORKFLOW.STEP_COMPLETED", tenantId: Guid.NewGuid());
        _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null).Should().BeNull();
    }

    [Test]
    public void Catalog_Event_Yields_Record_With_Classification()
    {
        var raw = Raw("SECRET.REVEAL", tenantId: Guid.NewGuid());
        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null);

        rec.Should().NotBeNull();
        rec!.ActionCode.Should().Be("SECRET.REVEAL");
        rec.Category.Should().Be("secret");
        rec.Severity.Should().Be("critical");
        rec.SourceEventId.Should().Be(raw.Id);
        rec.SourceSequenceNumber.Should().Be(raw.SequenceNumber);
        // AC12 — reserved hash columns left null.
        rec.RecordHash.Should().BeNull();
        rec.PrevRecordHash.Should().BeNull();
    }

    // ── AC11 — per-mode ownership routing ──

    [Test]
    public void Saas_TenantScoped_Event_Keys_TenantId_Only()
    {
        var tenantId = Guid.NewGuid();
        var raw = Raw("TENANT.MEMBER_ROLE_CHANGED.SUCCESS", tenantId: tenantId);

        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!;

        rec.TenantId.Should().Be(tenantId);
        rec.UserId.Should().BeNull();
    }

    [Test]
    public void Saas_PlatformOnly_Event_Has_Neither_Owner_Set()
    {
        // TenantId null = platform-only (e.g. impersonation against the
        // platform); the host routes it to the CP store with tenant_id null.
        var raw = Raw("IMPERSONATION.STARTED", tenantId: null);
        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!;

        rec.TenantId.Should().BeNull();
        rec.UserId.Should().BeNull();
    }

    [Test]
    public void SingleUser_Event_Keys_UserId_Only()
    {
        var ownerId = Guid.NewGuid();
        var raw = Raw("SECRET.REVEAL", tenantId: Guid.NewGuid());

        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SingleUser, ownerId)!;

        rec.UserId.Should().Be(ownerId);
        rec.TenantId.Should().BeNull("single-user rows have no tenant dimension");
    }

    [Test]
    public void SingleUser_Falls_Back_To_Actor_When_No_Owner_Provided()
    {
        var actorId = Guid.NewGuid();
        var raw = Raw("SECRET.REVEAL", tenantId: Guid.NewGuid(),
            tags: new { actorUserId = actorId.ToString() });

        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SingleUser, null)!;

        rec.UserId.Should().Be(actorId);
        rec.TenantId.Should().BeNull();
    }

    [Test]
    public void SingleUser_With_No_Owner_And_No_Actor_Throws()
    {
        var raw = Raw("SECRET.REVEAL", tenantId: Guid.NewGuid());
        var act = () => _projector.TryBuildRecord(raw, AuditOwnershipMode.SingleUser, null);
        act.Should().Throw<InvalidOperationException>();
    }

    // ── AC10 — redaction BEFORE persistence; plaintext never lands ──

    [Test]
    public void SecretWrite_Payload_Is_Redacted_Never_Plaintext()
    {
        const string plaintext = "tamma_sk_DEADBEEF0123456789";
        var raw = Raw("SECRET.WRITE", tenantId: Guid.NewGuid(),
            data: new
            {
                apiKey = plaintext,
                authHeader = "Bearer abcdefghijklmnopqrstuvwxyz",
                connection = "password=hunter2supersecret",
            });

        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!;

        rec.PayloadJson.Should().Contain("[REDACTED]");
        rec.PayloadJson.Should().NotContain(plaintext);
        rec.PayloadJson.Should().NotContain("hunter2supersecret");
        rec.PayloadJson.Should().NotContain("abcdefghijklmnopqrstuvwxyz");
    }

    [Test]
    public void Bearer_Token_In_Tags_Is_Redacted()
    {
        var raw = Raw("SECRET.REVEAL", tenantId: Guid.NewGuid(),
            tags: new { note = "Authorization: Bearer ghp_0123456789abcdefABCDEF" });

        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!;

        rec.PayloadJson.Should().Contain("[REDACTED]");
        rec.PayloadJson.Should().NotContain("ghp_0123456789abcdefABCDEF");
    }

    // ── Actor / target / outcome resolution ──

    [Test]
    public void Resolves_Actor_And_Target_From_Tags()
    {
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var raw = Raw("TENANT.MEMBER_ROLE_CHANGED.SUCCESS", tenantId: Guid.NewGuid(),
            tags: new { userId = actorId.ToString(), actorEmail = "admin@example.com" },
            data: new { targetUserId = targetId.ToString() });

        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!;

        rec.ActorUserId.Should().Be(actorId);
        rec.ActorEmailSnapshot.Should().Be("admin@example.com");
        rec.TargetId.Should().Be(targetId.ToString());
        rec.TargetType.Should().Be("user");
    }

    [Test]
    public void Failure_Suffix_Sets_Outcome_Failure()
    {
        var raw = Raw("AGENT.DISPATCH.FAILED", tenantId: Guid.NewGuid());
        _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!
            .Outcome.Should().Be("failure");
    }

    [Test]
    public void ReuseDetected_Sets_Outcome_Denied()
    {
        var raw = Raw("AUTH.REFRESH_REUSE_DETECTED", tenantId: null);
        _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!
            .Outcome.Should().Be("denied");
    }

    [Test]
    public void Default_Outcome_Is_Success()
    {
        var raw = Raw("SECRET.READ", tenantId: Guid.NewGuid());
        _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!
            .Outcome.Should().Be("success");
    }

    [Test]
    public void OccurredAt_Mirrors_RawEvent_CreatedAt_Utc()
    {
        var raw = Raw("SECRET.READ", tenantId: Guid.NewGuid());
        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!;
        rec.OccurredAt.Should().Be(raw.CreatedAt);
        rec.OccurredAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    // ── C2 — quarantine record: safe placeholder, never plaintext ──

    [Test]
    public void Quarantine_Record_Has_Failure_Outcome_Safe_Payload_And_Same_SourceId()
    {
        const string plaintext = "tamma_sk_DEADBEEF0123456789";
        var raw = Raw("SECRET.REVEAL", tenantId: Guid.NewGuid(),
            data: new { apiKey = plaintext });

        var rec = _projector.BuildQuarantineRecord(raw, AuditOwnershipMode.SaaS, null);

        // Idempotency key preserved so a quarantine row dedups like any other.
        rec.SourceEventId.Should().Be(raw.Id);
        rec.SourceSequenceNumber.Should().Be(raw.SequenceNumber);
        // Classifiable fields survive (the redaction of the PAYLOAD is what failed).
        rec.ActionCode.Should().Be("SECRET.REVEAL");
        rec.Category.Should().Be("secret");
        rec.Outcome.Should().Be("failure");
        // The raw/un-redacted payload must NEVER appear; only the safe placeholder.
        rec.PayloadJson.Should().Be(AuditProjector.QuarantinePayload);
        rec.PayloadJson.Should().NotContain(plaintext);
        rec.PayloadJson.Should().Contain("redaction_failed");
    }

    [Test]
    public void Quarantine_Record_Routes_Ownership_Per_Mode()
    {
        var tenantId = Guid.NewGuid();
        var saas = _projector.BuildQuarantineRecord(
            Raw("SECRET.REVEAL", tenantId: tenantId), AuditOwnershipMode.SaaS, null);
        saas.TenantId.Should().Be(tenantId);
        saas.UserId.Should().BeNull();

        var ownerId = Guid.NewGuid();
        var single = _projector.BuildQuarantineRecord(
            Raw("SECRET.REVEAL", tenantId: tenantId), AuditOwnershipMode.SingleUser, ownerId);
        single.UserId.Should().Be(ownerId);
        single.TenantId.Should().BeNull();
    }

    // ── C2 — quarantine of an event with NO catalog descriptor ──
    //
    // When the descriptor itself is missing (a build failure with no
    // classification), Category must use its OWN dedicated sentinel constant
    // (UnclassifiedCategory) — NOT the target-type sentinel. The two columns are
    // different dimensions and must not be wired to one constant, even though
    // both currently spell "unclassified": a future divergence in either spelling
    // must not silently desync the other. This test pins the two constants to the
    // two columns independently so the cosmetic reuse can't creep back.
    [Test]
    public void Quarantine_Record_NoDescriptor_Uses_Dedicated_Category_And_TargetType_Constants()
    {
        // WORKFLOW.STEP_COMPLETED is intentionally NOT in SensitiveActionCatalog
        // (see NonCatalog_Event_Yields_Null), so the descriptor resolves to null
        // and the quarantine builder takes its fallback branch.
        var raw = Raw("WORKFLOW.STEP_COMPLETED", tenantId: Guid.NewGuid());

        var rec = _projector.BuildQuarantineRecord(raw, AuditOwnershipMode.SaaS, null);

        // Category comes from the category-dimension constant — proven by binding
        // the assertion to that exact constant, not the target-type one.
        rec.Category.Should().Be(AuditProjector.UnclassifiedCategory);
        // TargetType comes from the target-type-dimension constant.
        rec.TargetType.Should().Be(AuditProjector.UnclassifiedTargetType);
        // No catalog descriptor → ActionCode degrades to the raw event type.
        rec.ActionCode.Should().Be("WORKFLOW.STEP_COMPLETED");
        // Still a quarantine row: failure outcome + safe placeholder payload.
        rec.Outcome.Should().Be("failure");
        rec.PayloadJson.Should().Be(AuditProjector.QuarantinePayload);
    }

    // ── Finding 1 (Story 37-10 review, security — audit evasion) ──
    //
    // Resolved string fields are attacker-influenced (e.g. the submitted login
    // email travels under actorEmail). If an over-length value reaches the row
    // it overflows its varchar column on INSERT (Npgsql 22001) and the audit row
    // is SILENTLY DROPPED while the cursor still advances — an attacker padding
    // their email suppresses the audit of their own brute force. The projector
    // MUST defensively cap every varchar-bounded field to its column length so no
    // over-length value can ever throw 22001.

    [Test]
    public void OverLength_ActorEmail_Is_Truncated_To_Column_Limit()
    {
        // 421-char attacker-controlled email → would overflow varchar(320).
        var longEmail = new string('a', 400) + "@attacker.example.com";
        var raw = Raw("AUTH.LOGIN.FAILURE", tenantId: null,
            data: new { actorEmail = longEmail, reason = "bad_credentials" });

        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!;

        rec.ActorEmailSnapshot!.Length.Should().Be(320,
            "ActorEmailSnapshot must be capped to its varchar(320) column length");
        rec.ActorEmailSnapshot.Should().Be(longEmail[..320]);
    }

    [Test]
    public void OverLength_Context_Fields_Are_Truncated_To_Column_Limits()
    {
        var raw = Raw("SECRET.REVEAL", tenantId: Guid.NewGuid(),
            tags: new
            {
                targetType = new string('t', 200), // varchar(64)
                targetId = new string('i', 400),   // varchar(255)
                ip = new string('9', 200),         // varchar(64)
                userAgent = new string('u', 1000), // varchar(512)
            });

        var rec = _projector.TryBuildRecord(raw, AuditOwnershipMode.SaaS, null)!;

        rec.TargetType!.Length.Should().Be(64);
        rec.TargetId!.Length.Should().Be(255);
        rec.IpAddress!.Length.Should().Be(64);
        rec.UserAgent!.Length.Should().Be(512);
    }

    [Test]
    public void OverLength_Fields_Are_Also_Truncated_On_The_Quarantine_Path()
    {
        var longEmail = new string('z', 500) + "@attacker.example.com";
        var raw = Raw("SECRET.REVEAL", tenantId: Guid.NewGuid(),
            data: new { actorEmail = longEmail });

        var rec = _projector.BuildQuarantineRecord(raw, AuditOwnershipMode.SaaS, null);

        rec.ActorEmailSnapshot!.Length.Should().Be(320,
            "the quarantine builder must also cap fields so the placeholder row cannot 22001");
    }
}
