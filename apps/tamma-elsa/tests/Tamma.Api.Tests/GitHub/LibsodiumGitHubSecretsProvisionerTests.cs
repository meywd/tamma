using FluentAssertions;
using NUnit.Framework;
using Sodium;
using Tamma.Api.Services.GitHub;

namespace Tamma.Api.Tests.GitHub;

/// <summary>
/// Focused tests for the libsodium sealed-box encryption helper used by
/// <see cref="LibsodiumGitHubSecretsProvisioner"/>. End-to-end GitHub calls
/// are exercised via the Octokit-client integration tests; this fixture
/// asserts only the crypto round-trip, which is the non-trivial piece.
/// Audit finding github 013.
/// </summary>
[TestFixture]
public class LibsodiumGitHubSecretsProvisionerTests
{
    // ─── Sealed-box round-trip ───────────────────────────────────────────────

    [Test]
    public void EncryptSealedBox_RoundTripsWithKnownKeypair()
    {
        // Generate a keypair the way a GitHub repo's public key is issued.
        var keypair = PublicKeyBox.GenerateKeyPair();
        var publicKeyB64 = Convert.ToBase64String(keypair.PublicKey);

        const string plaintext = "tamma_sk_super_secret_value_42";

        // Sender encrypts with the public key (no access to the private key).
        var ciphertextB64 = LibsodiumGitHubSecretsProvisioner.EncryptSealedBox(publicKeyB64, plaintext);

        // Recipient (GitHub in production, us in tests) decrypts using both
        // keys. The round-trip must equal the original plaintext.
        var ciphertext = Convert.FromBase64String(ciphertextB64);
        var decrypted = SealedPublicKeyBox.Open(ciphertext, keypair.PrivateKey, keypair.PublicKey);
        var recovered = System.Text.Encoding.UTF8.GetString(decrypted);

        recovered.Should().Be(plaintext);
    }

    [Test]
    public void EncryptSealedBox_ProducesDifferentCiphertextEachCall()
    {
        // Sealed-box uses an ephemeral keypair per call, so the ciphertext
        // for the same plaintext MUST differ between invocations. This is a
        // critical property — if it didn't, secrets would be trivially
        // correlatable across rotations.
        var keypair = PublicKeyBox.GenerateKeyPair();
        var publicKeyB64 = Convert.ToBase64String(keypair.PublicKey);

        var a = LibsodiumGitHubSecretsProvisioner.EncryptSealedBox(publicKeyB64, "hello");
        var b = LibsodiumGitHubSecretsProvisioner.EncryptSealedBox(publicKeyB64, "hello");

        a.Should().NotBe(b);
    }

    [Test]
    public void EncryptSealedBox_OutputIsStandardBase64()
    {
        // GitHub expects standard base64 (not URL-safe) in the
        // `encrypted_value` field. If our helper ever switched to url-safe
        // base64 accidentally, this test would catch it.
        var keypair = PublicKeyBox.GenerateKeyPair();
        var publicKeyB64 = Convert.ToBase64String(keypair.PublicKey);

        var ciphertextB64 = LibsodiumGitHubSecretsProvisioner.EncryptSealedBox(publicKeyB64, "x");

        // Must decode cleanly with standard base64.
        Action act = () => Convert.FromBase64String(ciphertextB64);
        act.Should().NotThrow();

        // And should NOT contain URL-safe-only characters.
        ciphertextB64.Should().NotContain("-").And.NotContain("_");
    }

    [Test]
    public void EncryptSealedBox_HandlesUtf8Plaintext()
    {
        var keypair = PublicKeyBox.GenerateKeyPair();
        var publicKeyB64 = Convert.ToBase64String(keypair.PublicKey);

        const string plaintext = "héllo — émoji \ud83d\udc4b";
        var ciphertextB64 = LibsodiumGitHubSecretsProvisioner.EncryptSealedBox(publicKeyB64, plaintext);
        var decrypted = SealedPublicKeyBox.Open(
            Convert.FromBase64String(ciphertextB64),
            keypair.PrivateKey,
            keypair.PublicKey);

        System.Text.Encoding.UTF8.GetString(decrypted).Should().Be(plaintext);
    }

    [Test]
    public void EncryptSealedBox_ThrowsOnInvalidPublicKeyLength()
    {
        // X25519 public keys are exactly 32 bytes. Passing a shorter key
        // must fail fast — otherwise we could encrypt a secret against a
        // malformed key and ship gibberish to GitHub.
        var tooShort = Convert.ToBase64String(new byte[16]);

        Action act = () => LibsodiumGitHubSecretsProvisioner.EncryptSealedBox(tooShort, "x");
        act.Should().Throw<Exception>();
    }
}
