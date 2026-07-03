using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Services.Audit;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Core.Audit;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-2 (AC5) — cabinet-backed checkpoint signer: HMAC round-trip, tamper
/// rejection, and fail-closed behaviour when the key is absent.
/// </summary>
[TestFixture]
public class AuditChainSignerTests
{
    private static AuditChainCheckpointView Checkpoint(byte[] sig) => new()
    {
        Id = Guid.NewGuid(),
        Scope = "platform",
        TenantId = null,
        HeadSequence = 42,
        HeadHash = new string('a', 64),
        SignedAt = new DateTime(2026, 7, 2, 10, 0, 0, DateTimeKind.Utc),
        Signature = sig,
        KeyVersion = 1,
    };

    private static SecretCabinetAuditChainSigner SignerWith(string? key)
    {
        var services = new ServiceCollection();
        if (key is not null)
        {
            services.AddSingleton<IRuntimeSecretResolver>(new FakeResolver(key));
        }
        return new SecretCabinetAuditChainSigner(services.BuildServiceProvider());
    }

    [Test]
    public async Task Sign_Then_Verify_Round_Trips()
    {
        var signer = SignerWith("s3cr3t-key-material");
        var cp = Checkpoint(Array.Empty<byte>());

        var (sig, keyVersion) = await signer.SignAsync(
            cp.Scope, cp.TenantId, cp.HeadSequence, cp.HeadHash, cp.SignedAt);
        keyVersion.Should().Be(SecretCabinetAuditChainSigner.AuditChainSigningKeyVersion);

        var valid = await signer.VerifyAsync(cp with { Signature = sig });
        valid.Should().BeTrue();
    }

    [Test]
    public async Task Corrupt_Signature_Fails_Verification()
    {
        var signer = SignerWith("s3cr3t-key-material");
        var cp = Checkpoint(Array.Empty<byte>());
        var (sig, _) = await signer.SignAsync(cp.Scope, cp.TenantId, cp.HeadSequence, cp.HeadHash, cp.SignedAt);

        sig[0] ^= 0xFF; // flip a byte
        (await signer.VerifyAsync(cp with { Signature = sig })).Should().BeFalse();
    }

    [Test]
    public async Task Different_Key_Fails_Verification()
    {
        var a = SignerWith("key-A");
        var b = SignerWith("key-B");
        var cp = Checkpoint(Array.Empty<byte>());
        var (sig, _) = await a.SignAsync(cp.Scope, cp.TenantId, cp.HeadSequence, cp.HeadHash, cp.SignedAt);

        (await b.VerifyAsync(cp with { Signature = sig })).Should().BeFalse();
    }

    [Test]
    public async Task Missing_Key_Sign_Throws_And_Verify_Returns_False_FailClosed()
    {
        var signer = SignerWith(null);

        var sign = async () => await signer.SignAsync("platform", null, 1, new string('a', 64), DateTime.UtcNow);
        await sign.Should().ThrowAsync<InvalidOperationException>();

        (await signer.VerifyAsync(Checkpoint(new byte[] { 1, 2, 3 }))).Should().BeFalse();
    }

    private sealed class FakeResolver : IRuntimeSecretResolver
    {
        private readonly string? _key;
        public FakeResolver(string? key) => _key = key;

        public Task<string?> GetAsync(string cabinetName, CancellationToken ct = default)
        {
            cabinetName.Should().Be(StopgapSecretMap.PlatformAuditChainSigningKey);
            return Task.FromResult(_key);
        }
    }
}
