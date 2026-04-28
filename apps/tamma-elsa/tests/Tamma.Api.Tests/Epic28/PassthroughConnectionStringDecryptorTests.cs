using FluentAssertions;
using NUnit.Framework;
using System.Text;
using Tamma.Data.Pooling;

namespace Tamma.Api.Tests.Epic28;

/// <summary>
/// Story 28-4 — sanity coverage for
/// <see cref="PassthroughConnectionStringDecryptor"/>. Production wraps
/// AES-GCM behind <see cref="Tamma.Data.Abstractions.IConnectionStringDecryptor"/>;
/// this passthrough is the dev/local default when no override is
/// registered.
/// </summary>
[TestFixture]
public class PassthroughConnectionStringDecryptorTests
{
    [Test]
    public void Decrypt_Returns_Utf8_String_From_Envelope()
    {
        var sut = new PassthroughConnectionStringDecryptor();
        var bytes = Encoding.UTF8.GetBytes(
            "Host=localhost;Port=5432;Database=tamma;Username=u;Password=p");

        var result = sut.Decrypt(bytes, kekVersion: 1);

        result.Should().Be("Host=localhost;Port=5432;Database=tamma;Username=u;Password=p");
    }

    [Test]
    public void Decrypt_Ignores_KekVersion()
    {
        var sut = new PassthroughConnectionStringDecryptor();
        var bytes = Encoding.UTF8.GetBytes("any-string");

        sut.Decrypt(bytes, kekVersion: null).Should().Be("any-string");
        sut.Decrypt(bytes, kekVersion: 99).Should().Be("any-string");
    }

    [Test]
    public void Decrypt_Throws_On_Null_Envelope()
    {
        var sut = new PassthroughConnectionStringDecryptor();
        Action act = () => sut.Decrypt(null!, kekVersion: 1);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Decrypt_Throws_On_Empty_Envelope()
    {
        var sut = new PassthroughConnectionStringDecryptor();
        Action act = () => sut.Decrypt(Array.Empty<byte>(), kekVersion: 1);
        act.Should().Throw<ArgumentException>();
    }
}
