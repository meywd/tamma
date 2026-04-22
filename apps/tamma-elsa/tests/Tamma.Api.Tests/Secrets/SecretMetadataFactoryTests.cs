using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Tests for <see cref="SecretMetadataFactory"/>. Covers the full
/// purpose × scope × tenant-id matrix (Story 29-1 AC10) plus the
/// name-slug regex, the rotation projection, and the edit projection.
/// </summary>
[TestFixture]
public class SecretMetadataFactoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ────────────────────────────────────────────────────────────────────────
    // Happy-path Create
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void Create_PlatformDbCredential_NoTenantId_Succeeds()
    {
        var meta = SecretMetadataFactory.Create(
            name: "db/app-role",
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.DbCredential,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: RotationSchedule.EveryDays(90),
            now: Now);

        meta.Id.Should().NotBe(Guid.Empty);
        meta.Name.Should().Be("db/app-role");
        meta.Scope.Should().Be(SecretScope.Platform);
        meta.TenantId.Should().BeNull();
        meta.Purpose.Should().Be(SecretPurpose.DbCredential);
        meta.ConsumerRefs.Should().BeEmpty();
        meta.OwnerUserId.Should().Be(Owner);
        meta.RotationSchedule.Kind.Should().Be(RotationScheduleKind.Days);
        meta.LastRotatedAt.Should().BeNull();
        meta.NextRotationDueAt.Should().Be(Now.AddDays(90));
        meta.ActiveVersionNumber.Should().Be(0);
        meta.CreatedAt.Should().Be(Now);
        meta.UpdatedAt.Should().Be(Now);
    }

    [Test]
    public void Create_TenantDbCredential_WithTenantId_Succeeds()
    {
        var meta = SecretMetadataFactory.Create(
            name: "db/tenant-role",
            scope: SecretScope.Tenant,
            tenantId: Tenant,
            purpose: SecretPurpose.DbCredential,
            consumerRefs: new[]
            {
                new ConsumerRef(ConsumerRefLookup.Systems.Postgres, "role=t1"),
            },
            ownerUserId: Owner,
            rotationSchedule: RotationSchedule.None,
            now: Now);

        meta.Scope.Should().Be(SecretScope.Tenant);
        meta.TenantId.Should().Be(Tenant);
        meta.ConsumerRefs.Should().HaveCount(1);
        meta.NextRotationDueAt.Should().BeNull(
            because: "RotationSchedule.None never has a next-due timestamp");
    }

    [Test]
    public void Create_DefaultsToNoneSchedule_WhenScheduleIsNull()
    {
        var meta = SecretMetadataFactory.Create(
            name: "platform/api-key",
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.ApiKey,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: null,
            now: Now);

        meta.RotationSchedule.Kind.Should().Be(RotationScheduleKind.None);
        meta.NextRotationDueAt.Should().BeNull();
    }

    [Test]
    public void ToRef_BuildsRefMatchingTheRow()
    {
        var meta = SecretMetadataFactory.Create(
            name: "db/app-role",
            scope: SecretScope.Tenant,
            tenantId: Tenant,
            purpose: SecretPurpose.DbCredential,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: null,
            now: Now);

        var refId = meta.ToRef();
        refId.Scope.Should().Be(SecretScope.Tenant);
        refId.TenantId.Should().Be(Tenant);
        refId.Name.Should().Be("db/app-role");
    }

    // ────────────────────────────────────────────────────────────────────────
    // AC10 invariant matrix — purpose × scope × tenant-id
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every purpose × scope cell. The truth table is:
    /// <list type="bullet">
    ///   <item><description>Platform + null tenantId → always
    ///     valid.</description></item>
    ///   <item><description>Platform + non-null tenantId → always
    ///     invalid (scope says no tenant).</description></item>
    ///   <item><description>Tenant + null tenantId → always invalid
    ///     (scope requires tenant id).</description></item>
    ///   <item><description>Tenant + non-null tenantId → always
    ///     valid.</description></item>
    /// </list>
    /// </summary>
    public static IEnumerable<TestCaseData> EnumScopeMatrix()
    {
        foreach (SecretPurpose purpose in Enum.GetValues<SecretPurpose>())
        {
            yield return new TestCaseData(
                purpose, SecretScope.Platform, /*tenantId*/ false, /*shouldThrow*/ false)
                .SetName($"Create_{purpose}_Platform_NullTenantId_Succeeds");

            yield return new TestCaseData(
                purpose, SecretScope.Platform, /*tenantId*/ true, /*shouldThrow*/ true)
                .SetName($"Create_{purpose}_Platform_NonNullTenantId_Throws");

            yield return new TestCaseData(
                purpose, SecretScope.Tenant, /*tenantId*/ false, /*shouldThrow*/ true)
                .SetName($"Create_{purpose}_Tenant_NullTenantId_Throws");

            yield return new TestCaseData(
                purpose, SecretScope.Tenant, /*tenantId*/ true, /*shouldThrow*/ false)
                .SetName($"Create_{purpose}_Tenant_NonNullTenantId_Succeeds");
        }
    }

    [TestCaseSource(nameof(EnumScopeMatrix))]
    public void Create_EnforcesPurposeScopeMatrix(
        SecretPurpose purpose,
        SecretScope scope,
        bool withTenantId,
        bool shouldThrow)
    {
        Guid? tenantId = withTenantId ? Tenant : null;

        Action act = () => SecretMetadataFactory.Create(
            name: "test/secret",
            scope: scope,
            tenantId: tenantId,
            purpose: purpose,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: null,
            now: Now);

        if (shouldThrow)
        {
            act.Should().Throw<ArgumentException>();
        }
        else
        {
            act.Should().NotThrow();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Name-slug regex
    // ────────────────────────────────────────────────────────────────────────

    [TestCase("db/app-role")]
    [TestCase("api-keys/openai")]
    [TestCase("simple")]
    [TestCase("a/b/c/d")]
    [TestCase("v1/db/role-1")]
    public void Create_AcceptsValidNames(string name)
    {
        Action act = () => SecretMetadataFactory.Create(
            name: name,
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.ApiKey,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: null,
            now: Now);

        act.Should().NotThrow();
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("DB/UPPER")]
    [TestCase("with space")]
    [TestCase("/leading-slash")]
    [TestCase("trailing-slash/")]
    [TestCase("-leading-dash")]
    [TestCase("trailing-dash-")]
    [TestCase("double//slash")]
    [TestCase("symbol$here")]
    [TestCase("ab")]                    // < 3 chars
    public void Create_RejectsInvalidNames(string name)
    {
        Action act = () => SecretMetadataFactory.Create(
            name: name,
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.ApiKey,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: null,
            now: Now);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Create_RejectsNamesLongerThan200Chars()
    {
        var longName = new string('a', 201);
        Action act = () => SecretMetadataFactory.Create(
            name: longName,
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.ApiKey,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: null,
            now: Now);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Create_RejectsEmptyOwnerGuid()
    {
        Action act = () => SecretMetadataFactory.Create(
            name: "test/secret",
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.ApiKey,
            consumerRefs: null,
            ownerUserId: Guid.Empty,
            rotationSchedule: null,
            now: Now);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Owner user id*");
    }

    // ────────────────────────────────────────────────────────────────────────
    // WithRotation projection
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void WithRotation_BumpsActiveVersion_AndStampsLastRotated()
    {
        var current = SecretMetadataFactory.Create(
            name: "db/role",
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.DbCredential,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: RotationSchedule.EveryDays(30),
            now: Now);

        var later = Now.AddDays(15);
        var rotated = SecretMetadataFactory.WithRotation(current, newActiveVersion: 1, later);

        rotated.ActiveVersionNumber.Should().Be(1);
        rotated.LastRotatedAt.Should().Be(later);
        rotated.UpdatedAt.Should().Be(later);
        rotated.NextRotationDueAt.Should().Be(later.AddDays(30));
        rotated.CreatedAt.Should().Be(Now,
            because: "WithRotation must not touch the original create timestamp");
    }

    [Test]
    public void WithRotation_RejectsNonMonotonicVersion()
    {
        var current = SecretMetadataFactory.Create(
            name: "db/role",
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.DbCredential,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: null,
            now: Now);

        // current.ActiveVersionNumber == 0
        Action sameVersion = () =>
            SecretMetadataFactory.WithRotation(current, 0, Now.AddDays(1));
        Action lowerVersion = () =>
            SecretMetadataFactory.WithRotation(current with { ActiveVersionNumber = 5 },
                3, Now.AddDays(1));

        sameVersion.Should().Throw<ArgumentException>();
        lowerVersion.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithRotation_ThrowsOnNullCurrent()
    {
        Action act = () => SecretMetadataFactory.WithRotation(null!, 1, Now);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────────────────────────────────────────────────────────────
    // WithEdits projection
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void WithEdits_AppliesNewSchedule_AndRecomputesNextDue()
    {
        var current = SecretMetadataFactory.Create(
            name: "db/role",
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.DbCredential,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: RotationSchedule.None,
            now: Now);

        var later = Now.AddDays(5);
        var edited = SecretMetadataFactory.WithEdits(
            current,
            consumerRefs: null,
            rotationSchedule: RotationSchedule.EveryDays(60),
            ownerUserId: null,
            now: later);

        edited.RotationSchedule.Kind.Should().Be(RotationScheduleKind.Days);
        edited.RotationSchedule.Days.Should().Be(60);
        edited.NextRotationDueAt.Should().Be(later.AddDays(60),
            because: "no last-rotation yet → anchor on the edit timestamp");
        edited.UpdatedAt.Should().Be(later);
    }

    [Test]
    public void WithEdits_AppliesNewConsumers()
    {
        var current = SecretMetadataFactory.Create(
            name: "platform/api",
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.ApiKey,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: null,
            now: Now);

        var newConsumers = new[]
        {
            new ConsumerRef(ConsumerRefLookup.Systems.TammaApi, "service=engine"),
            new ConsumerRef(ConsumerRefLookup.Systems.ElsaWorkflow, "definition=LlmCall"),
        };

        var edited = SecretMetadataFactory.WithEdits(
            current,
            consumerRefs: newConsumers,
            rotationSchedule: null,
            ownerUserId: null,
            now: Now.AddMinutes(1));

        edited.ConsumerRefs.Should().BeEquivalentTo(newConsumers);
    }

    [Test]
    public void WithEdits_AppliesNewOwner_RejectsEmpty()
    {
        var current = SecretMetadataFactory.Create(
            name: "platform/api",
            scope: SecretScope.Platform,
            tenantId: null,
            purpose: SecretPurpose.ApiKey,
            consumerRefs: null,
            ownerUserId: Owner,
            rotationSchedule: null,
            now: Now);

        var newOwner = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var edited = SecretMetadataFactory.WithEdits(
            current,
            consumerRefs: null,
            rotationSchedule: null,
            ownerUserId: newOwner,
            now: Now);
        edited.OwnerUserId.Should().Be(newOwner);

        Action withEmpty = () => SecretMetadataFactory.WithEdits(
            current,
            consumerRefs: null,
            rotationSchedule: null,
            ownerUserId: Guid.Empty,
            now: Now);
        withEmpty.Should().Throw<ArgumentException>();
    }

    // ────────────────────────────────────────────────────────────────────────
    // SecretRef constructor invariants (cross-checks AC10 entry point)
    // ────────────────────────────────────────────────────────────────────────

    [Test]
    public void SecretRef_ForPlatform_DisallowsTenantId()
    {
        Action act = () =>
            new SecretRef(SecretScope.Platform, tenantId: Tenant, name: "db/role");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void SecretRef_ForTenant_RequiresTenantId()
    {
        Action act = () =>
            new SecretRef(SecretScope.Tenant, tenantId: null, name: "db/role");
        act.Should().Throw<ArgumentException>();
    }

    [TestCase(SecretScope.Platform)]
    [TestCase(SecretScope.Tenant)]
    public void SecretRef_RequiresNonEmptyName(SecretScope scope)
    {
        Action act = () =>
            new SecretRef(scope, scope == SecretScope.Tenant ? Tenant : null, name: "");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void SecretRef_ToStorageKey_ReflectsScope()
    {
        SecretRef.ForPlatform("db/role").ToStorageKey()
            .Should().Be("platform:db/role");

        SecretRef.ForTenant(Tenant, "db/role").ToStorageKey()
            .Should().Be($"tenant:{Tenant}:db/role");
    }
}
