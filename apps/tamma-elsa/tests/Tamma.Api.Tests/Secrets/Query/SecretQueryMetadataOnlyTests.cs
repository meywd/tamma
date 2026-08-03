using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;
using Tamma.Api.Services.Secrets.Query;

namespace Tamma.Api.Tests.Secrets.Query;

/// <summary>
/// Story 42-10 (AC7, D8) — <c>agent-action:audit-secrets</c> stays zone-10
/// metadata ONLY. What that LLM action can ever read is exactly what the
/// <see cref="ISecretQueryService"/> projections surface, so this pins those
/// projections to metadata: the moment a DTO gains a value-bearing member, or the
/// service gains a decrypt dependency, this fails and the action's level question
/// reopens (Amendment 4 — "if the audit path ever reads values, it IS
/// secret.read at 90").
///
/// <para>Red state is proved by mutation (add a <c>Plaintext</c> property to
/// <see cref="SecretMetadata"/> or <see cref="SecretVersion"/> ⇒ red; inject
/// <c>ISecretRevealService</c> into <see cref="SecretQueryService"/> ⇒ red), not
/// against today's tree — the projections are already metadata-only, which is the
/// property being locked in.</para>
/// </summary>
[TestFixture]
public class SecretQueryMetadataOnlyTests
{
    // Any member whose name matches one of these is a value-bearing leak.
    private static readonly string[] ForbiddenSubstrings =
        { "plaintext", "ciphertext", "value", "material", "secretbytes", "decrypted" };

    [Test]
    public void SecretMetadata_HasExactlyTheseMetadataMembers_AndNoValue()
    {
        AssertNoValueMember(typeof(SecretMetadata));

        PropertyNames(typeof(SecretMetadata)).Should().BeEquivalentTo(new[]
        {
            "Id", "Name", "Scope", "TenantId", "Purpose", "ConsumerRefs", "OwnerUserId",
            "RotationSchedule", "LastRotatedAt", "NextRotationDueAt", "ActiveVersionNumber",
            "CreatedAt", "UpdatedAt",
        }, "a new SecretMetadata member must be reviewed — if it carries a value the audit "
           + "action's level reopens (Amendment 4)");
    }

    [Test]
    public void SecretVersion_HasExactlyTheseMetadataMembers_AndNoValue()
    {
        AssertNoValueMember(typeof(SecretVersion));

        PropertyNames(typeof(SecretVersion)).Should().BeEquivalentTo(new[]
        {
            "SecretId", "VersionNumber", "Status", "CreatedAt", "ActivatedAt", "RetiredAt",
            "CreatedByUserId",
        }, "a new SecretVersion member must be reviewed for value exposure");
    }

    [Test]
    public void SecretQueryService_HasNoDecryptDependency()
    {
        // The metadata read path must not be able to reach plaintext. Its ONLY
        // constructor dependencies are the secrets DbContext factory, the access
        // auditor, TimeProvider and a logger — none of which decrypts. A decrypt
        // seam (ISecretStore / ISecretStoreBackend / IKekProvider /
        // ISecretRevealService) injected here would make an audit-path value read
        // one call away, so its NAME appearing among the ctor params is red.
        var decryptSeams = new[]
        {
            "ISecretStore", "ISecretStoreBackend", "IKekProvider", "ISecretRevealService",
            "IKeyProtector",
        };

        var ctorParamTypeNames = typeof(SecretQueryService)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType.Name)
            .ToList();

        ctorParamTypeNames.Should().NotContain(
            n => decryptSeams.Contains(n),
            "the metadata query service must not inject any decrypt seam — that is what keeps "
            + "agent-action:audit-secrets a metadata-only (zone 10) action");
    }

    private static void AssertNoValueMember(Type dto)
    {
        foreach (var name in PropertyNames(dto))
        {
            var lower = name.ToLowerInvariant();
            ForbiddenSubstrings.Should().NotContain(
                s => lower.Contains(s),
                $"{dto.Name}.{name} looks value-bearing; the audit projection must be metadata-only");
        }
    }

    private static IEnumerable<string> PropertyNames(Type dto) =>
        dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name);
}
