using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

/// <summary>
/// Story 42-10 (AC1) — the P0 fix: a shell tool child NEVER inherits the API
/// process's secrets. These test the allowlist helper directly (deterministic,
/// no process spawn); the end-to-end `env`-through-the-tool assertion lives in
/// <see cref="ShellExecuteToolTests"/>.
/// </summary>
[TestFixture]
public class ProcessEnvironmentAllowlistTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    [Test]
    public void Apply_StripsSecretCanaries_AndKeepsThePosixBasics()
    {
        // Canaries only this test controls, so we do not depend on the ambient env.
        Environment.SetEnvironmentVariable("TAMMA_TEST_JWT_SECRET", "super-secret");
        Environment.SetEnvironmentVariable("TAMMA_TEST_GITHUB_TOKEN", "ghp_leak");
        Environment.SetEnvironmentVariable("PATH", Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin");
        try
        {
            var psi = new ProcessStartInfo { UseShellExecute = false };
            // Pre-seed with a secret to prove Apply CLEARS first.
            psi.EnvironmentVariables["TAMMA_TEST_JWT_SECRET"] = "super-secret";

            ProcessEnvironmentAllowlist.Apply(psi, Config());

            psi.EnvironmentVariables.ContainsKey("TAMMA_TEST_JWT_SECRET").Should().BeFalse(
                "a secret in the parent env must never reach the child");
            psi.EnvironmentVariables.ContainsKey("TAMMA_TEST_GITHUB_TOKEN").Should().BeFalse();
            psi.EnvironmentVariables.ContainsKey("PATH").Should().BeTrue(
                "PATH is on the base allowlist so the shell can run at all");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAMMA_TEST_JWT_SECRET", null);
            Environment.SetEnvironmentVariable("TAMMA_TEST_GITHUB_TOKEN", null);
        }
    }

    [Test]
    public void Apply_HonoursTheAdditiveAllowlist_ByName()
    {
        Environment.SetEnvironmentVariable("TAMMA_TEST_DOTNET_ROOT", "/opt/dotnet");
        try
        {
            var psi = new ProcessStartInfo { UseShellExecute = false };
            ProcessEnvironmentAllowlist.Apply(
                psi, Config(("Tools:Shell:EnvAllowlist:0", "TAMMA_TEST_DOTNET_ROOT")));

            psi.EnvironmentVariables.ContainsKey("TAMMA_TEST_DOTNET_ROOT").Should().BeTrue(
                "a deployment can allowlist a toolchain variable by name");
            psi.EnvironmentVariables["TAMMA_TEST_DOTNET_ROOT"].Should().Be("/opt/dotnet",
                "the value comes from the live process env, never from config");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAMMA_TEST_DOTNET_ROOT", null);
        }
    }

    [Test]
    public void Apply_KeepsLocaleVariables_ByPrefix()
    {
        Environment.SetEnvironmentVariable("LC_TAMMA_TEST", "en_US.UTF-8");
        try
        {
            var psi = new ProcessStartInfo { UseShellExecute = false };
            ProcessEnvironmentAllowlist.Apply(psi, Config());
            psi.EnvironmentVariables.ContainsKey("LC_TAMMA_TEST").Should().BeTrue(
                "LC_* locale variables are always safe and matched by prefix");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LC_TAMMA_TEST", null);
        }
    }

    [Test]
    public void Apply_RefusesWhenUseShellExecuteIsTrue()
    {
        var psi = new ProcessStartInfo { UseShellExecute = true };
        var act = () => ProcessEnvironmentAllowlist.Apply(psi, Config());
        act.Should().Throw<InvalidOperationException>(
            "with UseShellExecute=true EnvironmentVariables is ignored and the child re-inherits the parent env");
    }
}
