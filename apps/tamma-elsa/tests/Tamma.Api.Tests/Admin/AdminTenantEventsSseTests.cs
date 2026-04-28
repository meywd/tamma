using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Endpoints.Admin;

namespace Tamma.Api.Tests.Admin;

/// <summary>
/// Round-2 (M4 / M14 / M15) — focused unit tests for the
/// <see cref="AdminTenantEventsSseEndpoint"/> response scrubber +
/// constants.
///
/// <para>The full SSE pipeline is exercised through integration tests;
/// here we lock the security-critical scrub behaviour because that's
/// the boundary that protects against tag/data leakage. The poll loop's
/// consecutive-error counter is also covered by a test that doesn't
/// require spinning up the HTTP host.</para>
/// </summary>
[TestFixture]
public class AdminTenantEventsSseTests
{
    [Test]
    public void ScrubEvent_KeepsTopLevelFields()
    {
        var id = Guid.NewGuid();
        var ts = new DateTime(2026, 4, 26, 10, 0, 0, DateTimeKind.Utc);
        var safe = AdminTenantEventsSseEndpoint.ScrubForTesting(
            id: id,
            type: "TENANT.PROVISION.STEP_COMPLETED",
            sequenceNumber: 42,
            createdAt: ts,
            tags: "{}",
            data: """{"secret":"leaked"}""");

        safe.Id.Should().Be(id);
        safe.Type.Should().Be("TENANT.PROVISION.STEP_COMPLETED");
        safe.SequenceNumber.Should().Be(42);
        safe.CreatedAt.Should().Be(ts);
        safe.Tags.Should().BeEmpty(
            "M4 — empty tag bag means no allowlisted keys present");
    }

    [Test]
    public void ScrubEvent_KeepsAllowlistedTagKeys()
    {
        var safe = AdminTenantEventsSseEndpoint.ScrubForTesting(
            id: Guid.NewGuid(),
            type: "TENANT.DELETE.STEP_STARTED",
            sequenceNumber: 1,
            createdAt: DateTime.UtcNow,
            tags: """{"tenantId":"abc","step":"drop-tenant-db","attempt":2}""",
            data: null);

        safe.Tags.Should().HaveCount(3);
        safe.Tags.Should().ContainKey("tenantId").WhoseValue.Should().Be("abc");
        safe.Tags.Should().ContainKey("step").WhoseValue.Should().Be("drop-tenant-db");
        // attempt is a JSON number — scrubber stringifies via raw text.
        safe.Tags.Should().ContainKey("attempt").WhoseValue.Should().Be("2");
    }

    [Test]
    public void ScrubEvent_DropsNonAllowlistedTagKeys()
    {
        // M4 — non-allowlisted keys (sensitive material that could
        // leak from upstream) must NOT survive the scrub.
        var safe = AdminTenantEventsSseEndpoint.ScrubForTesting(
            id: Guid.NewGuid(),
            type: "TENANT.PROVISION.STEP_FAILED",
            sequenceNumber: 99,
            createdAt: DateTime.UtcNow,
            tags: """
            {
              "tenantId":"abc",
              "apiKey":"sk-leaked-credential",
              "internalUrl":"http://10.0.0.1:5432/secret",
              "stack":"at MyClass.MyMethod()..."
            }
            """,
            data: null);

        safe.Tags.Should().ContainKey("tenantId");
        safe.Tags.Should().NotContainKey("apiKey",
            "M4 — non-allowlisted keys must be stripped from the response");
        safe.Tags.Should().NotContainKey("internalUrl");
        safe.Tags.Should().NotContainKey("stack");
    }

    [Test]
    public void ScrubEvent_MalformedJson_YieldsEmptyTags_NoException()
    {
        var safe = AdminTenantEventsSseEndpoint.ScrubForTesting(
            id: Guid.NewGuid(),
            type: "TENANT.PROVISION.STEP_FAILED",
            sequenceNumber: 1,
            createdAt: DateTime.UtcNow,
            tags: "{not valid json",
            data: null);

        safe.Tags.Should().BeEmpty();
    }

    [Test]
    public void ScrubEvent_NullOrEmptyTags_YieldsEmptyTagBag()
    {
        var safeNull = AdminTenantEventsSseEndpoint.ScrubForTesting(
            id: Guid.NewGuid(), type: "X", sequenceNumber: 1,
            createdAt: DateTime.UtcNow, tags: null, data: null);
        var safeEmpty = AdminTenantEventsSseEndpoint.ScrubForTesting(
            id: Guid.NewGuid(), type: "X", sequenceNumber: 1,
            createdAt: DateTime.UtcNow, tags: "{}", data: null);

        safeNull.Tags.Should().BeEmpty();
        safeEmpty.Tags.Should().BeEmpty();
    }

    [Test]
    public void ScrubEvent_NestedObjectsAndArrays_AreDropped()
    {
        // Allowlist values are scalars only — nested objects/arrays
        // mean a misuse of the convention; scrubber drops them so a
        // malformed publisher can't sneak structured payloads through.
        var safe = AdminTenantEventsSseEndpoint.ScrubForTesting(
            id: Guid.NewGuid(),
            type: "X",
            sequenceNumber: 1,
            createdAt: DateTime.UtcNow,
            tags: """{"tenantId":{"nested":"object"},"step":["array"],"attempt":3}""",
            data: null);

        // Scalar 'attempt' survives; the malformed nested ones don't.
        safe.Tags.Should().NotContainKey("tenantId");
        safe.Tags.Should().NotContainKey("step");
        safe.Tags.Should().ContainKey("attempt").WhoseValue.Should().Be("3");
    }

    [Test]
    public void AllowedTagKeys_Are_DocumentedSet()
    {
        // Pin the contract so any future PR that broadens the allowlist
        // is forced to update this test (and review the security
        // implications).
        AdminTenantEventsSseEndpoint.AllowedTagKeys.Should().BeEquivalentTo(new[]
        {
            "tenantId", "step", "attempt", "actorUserId", "actorEmail",
        });
    }

    [Test]
    public void MaxConsecutiveErrors_IsFive()
    {
        // M15 — five consecutive failures before the stream gives up.
        // Pinned so a refactor doesn't accidentally weaken it.
        AdminTenantEventsSseEndpoint.MaxConsecutiveErrors.Should().Be(5);
    }
}
