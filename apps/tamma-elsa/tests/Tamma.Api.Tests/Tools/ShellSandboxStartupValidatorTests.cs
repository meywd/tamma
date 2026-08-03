using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Tools;
using Tamma.Core;

namespace Tamma.Api.Tests.Tools;

/// <summary>
/// Story 42-10 (AC2) — the fail-loud shell SANDBOX verifier. The level-40 discount
/// rides on these guarantees being real, so a declared-but-unverified sandbox
/// refuses to boot.
/// </summary>
[TestFixture]
public class ShellSandboxStartupValidatorTests
{
    private static string ExistingRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tamma_sbx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    // ── The pure check (D7 seam) ─────────────────────────────────────────────

    [Test]
    public void Validate_GreenWhenAttestedAndConfinedAndProbeUnreachable()
    {
        ShellSandboxStartupValidator.Validate(
            mechanism: "network-namespace", workspaceRoot: ExistingRoot(),
            probeHost: "192.0.2.1:9", probe: _ => false /* unreachable */)
            .Should().BeEmpty();
    }

    [Test]
    public void Validate_FlagsAnUnknownOrMissingMechanism()
    {
        ShellSandboxStartupValidator.Validate(null, ExistingRoot(), null, _ => false)
            .Should().ContainSingle(v => v.StartsWith("egress:"));

        ShellSandboxStartupValidator.Validate("wishful-thinking", ExistingRoot(), null, _ => false)
            .Should().ContainSingle(v => v.StartsWith("egress:"));
    }

    [Test]
    public void Validate_FlagsAMissingOrRelativeOrAbsentWorkspaceRoot()
    {
        ShellSandboxStartupValidator.Validate("firewall", null, null, _ => false)
            .Should().ContainSingle(v => v.Contains("cwd-confinement") && v.Contains("unset"));
        ShellSandboxStartupValidator.Validate("firewall", "relative/dir", null, _ => false)
            .Should().ContainSingle(v => v.Contains("not an absolute path"));
        ShellSandboxStartupValidator.Validate("firewall", "/tamma/does/not/exist/xyz", null, _ => false)
            .Should().ContainSingle(v => v.Contains("does not exist"));
    }

    [Test]
    public void Validate_FlagsAnOpenEgressWhenTheProbeConnects()
    {
        ShellSandboxStartupValidator.Validate(
            "proxy-only", ExistingRoot(), "127.0.0.1:1234", probe: _ => true /* connected */)
            .Should().ContainSingle(v => v.Contains("egress is OPEN"));
    }

    // ── Against VerifyOrThrow (real config + real probe) ─────────────────────

    [Test]
    public void VerifyOrThrow_IsANoOp_WhenUnsandboxed()
    {
        ShellSandboxStartupValidator.VerifyOrThrow(Config(("Tools:Shell:Sandboxed", "false"))); // no throw
    }

    [Test]
    public void VerifyOrThrow_Refuses_WhenSandboxedWithoutAttestation()
    {
        var act = () => ShellSandboxStartupValidator.VerifyOrThrow(
            Config(("Tools:Shell:Sandboxed", "true"))); // no mechanism, no workspace root

        act.Should().Throw<TammaError>().Which.Code.Should().Be("TOOLS.SHELL.SANDBOX_UNVERIFIED");
    }

    [Test]
    public void VerifyOrThrow_Refuses_WhenTheEgressProbeConnects()
    {
        // A loopback listener the probe WILL reach ⇒ egress open ⇒ refuse boot.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var act = () => ShellSandboxStartupValidator.VerifyOrThrow(Config(
            ("Tools:Shell:Sandboxed", "true"),
            ("Tools:Shell:Egress:Mechanism", "firewall"),
            ("ToolExecution:WorkspaceRoot", ExistingRoot()),
            ("Tools:Shell:Egress:ProbeHost", $"127.0.0.1:{port}")));

        act.Should().Throw<TammaError>().Which.Message.Should().Contain("egress is OPEN");

        listener.Stop();
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();
}
