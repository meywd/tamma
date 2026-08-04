using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

/// <summary>
/// Story 42-10 (AC4) — the sandboxed-profile CWD confinement screen. A
/// command-string screen, not a jail: it catches the obvious escapes and the
/// story records the rest as known gaps.
/// </summary>
[TestFixture]
public class WorkspaceConfinementScreenTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"tamma_ws_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestCase("cat /etc/passwd")]
    [TestCase("cat ../../secret")]
    [TestCase("cd / && ls")]
    [TestCase("cd /tmp")]
    [TestCase("less ../../../etc/shadow")]
    public void GetViolation_RejectsEscapes(string command)
    {
        WorkspaceConfinementScreen.GetViolation(command, _root)
            .Should().NotBeNull($"'{command}' reaches outside the workspace root");
    }

    [TestCase("cat src/foo.cs")]
    [TestCase("ls")]
    [TestCase("echo hello && npm test")]
    [TestCase("grep -r pattern .")]
    [TestCase("cat ./README.md")]
    public void GetViolation_AllowsWorkspaceRelativeCommands(string command)
    {
        WorkspaceConfinementScreen.GetViolation(command, _root)
            .Should().BeNull($"'{command}' stays inside the workspace root");
    }

    [Test]
    public void GetViolation_AllowsAPathInsideTheWorkspace_ByAbsolutePath()
    {
        var inside = Path.Combine(_root, "build", "out.txt");
        WorkspaceConfinementScreen.GetViolation($"cat {inside}", _root)
            .Should().BeNull("an absolute path that resolves inside the root is fine");
    }

    [Test]
    public void GetViolation_IsNullForEmptyOrBlank()
    {
        WorkspaceConfinementScreen.GetViolation("", _root).Should().BeNull();
        WorkspaceConfinementScreen.GetViolation("   ", _root).Should().BeNull();
    }
}
