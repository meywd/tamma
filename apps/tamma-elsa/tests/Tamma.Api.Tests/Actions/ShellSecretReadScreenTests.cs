using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Actions;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 42-10 (AC6, D7) — the BEST-EFFORT shell secret-read screen. The sandbox
/// (env strip + egress block) is the real control; this screen makes the OBVIOUS
/// secret read a gated, audited decision, and its gaps are pinned as KNOWN so a
/// silent closure — or a silent widening — is a reviewed event.
/// </summary>
[TestFixture]
public class ShellSecretReadScreenTests
{
    [TestCase("env")]
    [TestCase("printenv")]
    [TestCase("printenv GITHUB_TOKEN")]
    [TestCase("export -p")]
    [TestCase("declare -x")]
    [TestCase("cat .env")]
    [TestCase("cat ./.env")]
    [TestCase("head -n5 .env.production")]
    [TestCase("cat /run/secrets/db-password")]
    [TestCase("base64 tls.pem")]
    [TestCase("grep SECRET config/id_rsa.key")]
    public void Matches_TheObviousSecretReads(string command)
    {
        ShellSecretReadScreen.Matches(command).Should().BeTrue($"'{command}' reads a secret value");
    }

    [TestCase("ls")]
    [TestCase("ls -la")]
    [TestCase("cat README.md")]
    [TestCase("echo hello")]
    [TestCase("grep TODO src/foo.cs")]
    [TestCase("npm test")]
    [TestCase("environment_setup.sh")]   // 'env' inside a word must not match
    [TestCase("cat prevented.txt")]      // 'prevent' contains 'env' — must not match
    public void DoesNotMatch_OrdinaryCommands(string command)
    {
        ShellSecretReadScreen.Matches(command).Should().BeFalse($"'{command}' reads no secret");
    }

    [Test]
    public void HonoursAConfiguredSecretPath()
    {
        ShellSecretReadScreen.Matches("cat vault/token.txt", new[] { "vault/token.txt" })
            .Should().BeTrue("a deployment can add a secret path");
        // And a default path is NOT matched when a custom list replaces it.
        ShellSecretReadScreen.Matches("cat .env", new[] { "vault/token.txt" })
            .Should().BeFalse("a custom list replaces the defaults");
    }

    // ── Documented gaps (AC6) — pinned so their silent closure is reviewed ──

    [Test]
    public void KnownGap_RedirectionOnlyRead_IsNotCaught()
    {
        // `while read l; do …; done < .env` streams the file via redirection, not a
        // read verb touching the path token — the screen does not catch it. The
        // sandbox is the control; this negative pin makes a future change visible.
        ShellSecretReadScreen.Matches("while read l; do echo \"$l\"; done < config.env")
            .Should().BeFalse("redirection-only reads are a documented gap");
    }

    [Test]
    public void KnownGap_TheSetBuiltinVariableDump_IsNotCaught()
    {
        ShellSecretReadScreen.Matches("set").Should().BeFalse("the set builtin dump is a documented gap");
    }
}
