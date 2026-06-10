using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Phase 0 final-review fix I2 — regression tests for the idempotency
/// guard in <see cref="EncryptAndPersistConnectionStringActivity"/>.
/// The <c>EncryptedConnectionString</c> shadow property is a bytea column,
/// so its <c>CurrentValue</c> is a boxed <c>byte[]</c>; the guard used to
/// cast it to <c>string?</c>, throwing <see cref="InvalidCastException"/>
/// on the exact path it exists for (workflow replay / downstream-step
/// retry with an already-populated envelope). Like the other lifecycle
/// activities, <c>ProcessAsync</c> only runs inside the Elsa runtime, so
/// the guard is exposed as the static
/// <c>ShouldSkipReencrypt(object?, int?, int)</c> and tested directly.
/// </summary>
[TestFixture]
public class EncryptAndPersistConnectionStringActivityTests
{
    [Test]
    public void ShouldSkipReencrypt_PopulatedEnvelopeSameKek_SkipsWithoutThrowing()
    {
        // Boxed byte[] exactly as EF hands back the bytea shadow CurrentValue.
        object existingEnvelope = Encoding.UTF8.GetBytes("aes-gcm-envelope-bytes");

        var act = () => EncryptAndPersistConnectionStringActivity
            .ShouldSkipReencrypt(existingEnvelope, existingKek: 3, activeKek: 3);

        act.Should().NotThrow<InvalidCastException>(
            "the guard must handle the boxed byte[] the bytea column produces");
        EncryptAndPersistConnectionStringActivity
            .ShouldSkipReencrypt(existingEnvelope, 3, 3)
            .Should().BeTrue("populated envelope under the active KEK is the documented no-op");
    }

    [Test]
    public void ShouldSkipReencrypt_PopulatedEnvelopeDifferentKek_Reencrypts()
    {
        object existingEnvelope = new byte[] { 0x01, 0x02, 0x03 };

        EncryptAndPersistConnectionStringActivity
            .ShouldSkipReencrypt(existingEnvelope, existingKek: 2, activeKek: 3)
            .Should().BeFalse("KEK rotation in flight must re-encrypt");
    }

    [Test]
    public void ShouldSkipReencrypt_NullEnvelope_Reencrypts()
    {
        EncryptAndPersistConnectionStringActivity
            .ShouldSkipReencrypt(null, existingKek: 3, activeKek: 3)
            .Should().BeFalse("first run has no envelope yet");
    }

    [Test]
    public void ShouldSkipReencrypt_EmptyEnvelope_Reencrypts()
    {
        EncryptAndPersistConnectionStringActivity
            .ShouldSkipReencrypt(Array.Empty<byte>(), existingKek: 3, activeKek: 3)
            .Should().BeFalse("an empty envelope is not a sealed credential");
    }

    [Test]
    public void Activity_HasCorrectStepName()
    {
        new EncryptAndPersistConnectionStringActivity()
            .StepName.Should().Be("encrypt-creds");
    }
}
