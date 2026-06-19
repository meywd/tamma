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
}
