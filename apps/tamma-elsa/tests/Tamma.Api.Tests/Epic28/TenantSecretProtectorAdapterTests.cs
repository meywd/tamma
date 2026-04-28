using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-5 — adapter that lets tenant-lifecycle activities encrypt the
/// per-tenant connection string via the existing
/// <see cref="TenantSecretProtector"/> behind the lower-layer
/// <see cref="Tamma.Data.Abstractions.ITenantConnectionStringProtector"/>
/// port.
/// </summary>
[TestFixture]
public class TenantSecretProtectorAdapterTests
{
    [Test]
    public void Encrypt_RoundTripsViaInnerProtector()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var inner = new TenantSecretProtector(key);
        var sut = new TenantSecretProtectorAdapter(inner);

        var envelope = sut.Encrypt("Host=h;Database=tamma_tenant_xxx;Username=u;Password=p");
        envelope.Should().NotBeEmpty();

        var roundtripped = inner.Decrypt(envelope);
        roundtripped.Should().Be("Host=h;Database=tamma_tenant_xxx;Username=u;Password=p");
    }

    [Test]
    public void CurrentKekVersion_IsOne()
    {
        var inner = new TenantSecretProtector(RandomNumberGenerator.GetBytes(32));
        var sut = new TenantSecretProtectorAdapter(inner);
        sut.CurrentKekVersion.Should().Be(1);
    }

    [Test]
    public void Ctor_RejectsNullInner()
    {
        var act = () => new TenantSecretProtectorAdapter(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
