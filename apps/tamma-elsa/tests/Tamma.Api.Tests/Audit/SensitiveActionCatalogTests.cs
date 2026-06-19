using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Core.Audit;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-1 (AC1–AC3, AC13a) — pure catalog completeness + classification
/// tests for <see cref="SensitiveActionCatalog"/>. No DB. Reflects over the
/// REAL emitter constants (<see cref="SecretAuditEventTypes"/>) and the known
/// DCB event-type strings so a future rename that drops an emitted type from
/// the catalog fails CI.
/// </summary>
[TestFixture]
public class SensitiveActionCatalogTests
{
    // ── AC1 — ≥30 codes ──

    [Test]
    public void Catalog_Has_At_Least_30_Codes()
    {
        SensitiveActionCatalog.ByCode.Count.Should().BeGreaterThanOrEqualTo(30,
            "the taxonomy must be a meaningful catalogue, not a stub");
    }

    // ── AC2 — every one of the 11 categories has ≥1 code ──

    [Test]
    public void Catalog_Covers_All_Eleven_Categories()
    {
        var present = SensitiveActionCatalog.ByCode.Values
            .Select(d => d.Category).Distinct().ToHashSet();

        foreach (var cat in Enum.GetValues<AuditCategory>())
        {
            present.Should().Contain(cat,
                $"category {cat} must have at least one catalogued action code");
        }

        present.Count.Should().Be(11, "there are exactly 11 audit categories");
    }

    // ── AC1 — every descriptor is well-formed ──

    [Test]
    public void Every_Descriptor_Has_NonEmpty_Soc2_Control_And_TargetHint()
    {
        foreach (var (code, desc) in SensitiveActionCatalog.ByCode)
        {
            desc.ActionCode.Should().Be(code, "the descriptor's ActionCode must equal its dictionary key");
            desc.Soc2ControlId.Should().NotBeNullOrWhiteSpace(
                $"{code} must map a SOC2 control id for compliance evidence");
            desc.TargetTypeHint.Should().NotBeNullOrWhiteSpace(
                $"{code} must carry a target-type hint");
        }
    }

    [Test]
    public void Severity_Values_Are_Within_The_Closed_Enum()
    {
        foreach (var desc in SensitiveActionCatalog.ByCode.Values)
        {
            Enum.IsDefined(desc.Severity).Should().BeTrue(
                $"{desc.ActionCode} severity must be a defined AuditSeverity");
        }
    }

    // ── AC3 / AC13a — every REAL existing emitter is catalogued + matching ──

    [Test]
    public void Every_SecretAuditEventType_Is_Catalogued()
    {
        // Reflect over the real emitter const-class so a renamed/dropped
        // SECRET.* type that isn't reflected in the catalog fails CI.
        var secretTypes = typeof(SecretAuditEventTypes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        secretTypes.Should().NotBeEmpty("SecretAuditEventTypes defines the Epic 29 secret events");

        // The migration/skipped variants (MIGRATED.*) are out of the
        // compliance-sensitive surface; assert the core access events are
        // catalogued. Every catalogued one MUST be classified Secret.
        foreach (var t in new[]
        {
            SecretAuditEventTypes.Read, SecretAuditEventTypes.Write,
            SecretAuditEventTypes.Reveal, SecretAuditEventTypes.RotateStarted,
            SecretAuditEventTypes.RotateSucceeded, SecretAuditEventTypes.RotateFailed,
            SecretAuditEventTypes.VersionRevoked,
        })
        {
            SensitiveActionCatalog.ByCode.Should().ContainKey(t,
                $"the secret-access emitter '{t}' must be catalogued");
            SensitiveActionCatalog.ByCode[t].Category.Should().Be(AuditCategory.Secret);
            SensitiveActionCatalog.ByCode[t].MapsExistingEmitter.Should().BeTrue(
                $"'{t}' is a verified existing emitter");
        }
    }

    [TestCase("IMPERSONATION.STARTED", AuditCategory.Impersonation)]
    [TestCase("IMPERSONATION.ENDED", AuditCategory.Impersonation)]
    [TestCase("TENANT.MEMBER_ROLE_CHANGED.SUCCESS", AuditCategory.Rbac)]
    [TestCase("TENANT.MEMBER_REMOVED.SUCCESS", AuditCategory.Rbac)]
    [TestCase("TENANT.OWNERSHIP_TRANSFERRED.SUCCESS", AuditCategory.Rbac)]
    [TestCase("USER.LOGOUT_ALL.SUCCESS", AuditCategory.Auth)]
    [TestCase("AUTH.REFRESH_REUSE_DETECTED", AuditCategory.Auth)]
    [TestCase("CONVENTION.UPDATED.SUCCESS", AuditCategory.Config)]
    [TestCase("PROMPT.UPDATED.SUCCESS", AuditCategory.Persona)]
    [TestCase("AGENT_CONFIG.UPDATED.SUCCESS", AuditCategory.Config)]
    [TestCase("AGENT.CREATED.SUCCESS", AuditCategory.Agent)]
    [TestCase("BILLING.CUSTOMER.CREATED", AuditCategory.Billing)]
    [TestCase("TENANT.PROVISIONED.SUCCESS", AuditCategory.Tenant)]
    public void Verified_Existing_Emitter_Is_Catalogued_With_Matching_Category(
        string eventType, AuditCategory expectedCategory)
    {
        SensitiveActionCatalog.ByCode.Should().ContainKey(eventType,
            $"the verified existing emitter '{eventType}' must be in the catalog");
        var desc = SensitiveActionCatalog.ByCode[eventType];
        desc.Category.Should().Be(expectedCategory);
        desc.MapsExistingEmitter.Should().BeTrue(
            $"'{eventType}' is appended to the DCB store by a real emitter today");
    }

    // ── AC7 — IsSensitive / Resolve are the only lookup path ──

    [Test]
    public void IsSensitive_True_For_Catalogued_False_For_NonCatalogued()
    {
        SensitiveActionCatalog.IsSensitive("SECRET.REVEAL").Should().BeTrue();
        SensitiveActionCatalog.IsSensitive("WORKFLOW.STEP_COMPLETED").Should().BeFalse();
        SensitiveActionCatalog.IsSensitive(null).Should().BeFalse();
        SensitiveActionCatalog.IsSensitive("").Should().BeFalse();
    }

    [Test]
    public void Resolve_Returns_Descriptor_Or_Null()
    {
        SensitiveActionCatalog.Resolve("SECRET.REVEAL").Should().NotBeNull();
        SensitiveActionCatalog.Resolve("WORKFLOW.STEP_COMPLETED").Should().BeNull();
        SensitiveActionCatalog.Resolve(null).Should().BeNull();
    }

    // ── M1 — duplicate const VALUE detection ──

    [Test]
    public void Const_Event_Type_Values_Are_All_Distinct()
    {
        // The static ctor builds ByCode via map[code] = ... (indexer), so two
        // public const fields that accidentally carry the SAME string value would
        // SILENTLY overwrite / mis-classify with no signal. Reflect over the public
        // const string fields and assert there are no duplicate values, AND that
        // the built dictionary's Count equals the distinct code count (i.e. the
        // map was built without a silent collision).
        var constValues = typeof(SensitiveActionCatalog)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        constValues.Should().NotBeEmpty("the catalog defines its codes as public consts");

        var distinct = constValues.Distinct(StringComparer.Ordinal).ToList();
        constValues.Should().HaveCount(distinct.Count,
            "no two const action-code values may collide (the static ctor's indexer "
            + "would silently overwrite the earlier classification)");

        // Every distinct const value is a key, and the dictionary has exactly that
        // many entries — proof the indexer never silently merged two codes into one.
        SensitiveActionCatalog.ByCode.Count.Should().Be(distinct.Count,
            "ByCode.Count must equal the distinct const code count — no silent collision");
        foreach (var value in distinct)
        {
            SensitiveActionCatalog.ByCode.Should().ContainKey(value,
                $"every const code '{value}' must be catalogued");
        }
    }
}
