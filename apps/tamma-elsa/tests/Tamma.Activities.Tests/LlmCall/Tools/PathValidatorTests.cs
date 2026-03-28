using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

[TestFixture]
public class PathValidatorTests
{
    private string _workspaceRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"tamma_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
            Directory.Delete(_workspaceRoot, recursive: true);
    }

    [Test]
    public void ResolveSafePath_ValidRelative_ReturnsAbsolute()
    {
        // Act
        var result = PathValidator.ResolveSafePath("src/foo.cs", _workspaceRoot);

        // Assert
        result.Should().Be(Path.Combine(_workspaceRoot, "src", "foo.cs"));
    }

    [Test]
    public void ResolveSafePath_Traversal_Throws()
    {
        // Act
        Action act = () => PathValidator.ResolveSafePath("../../etc/passwd", _workspaceRoot);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside*");
    }

    [Test]
    public void ResolveSafePath_EmptyPath_ThrowsArgumentException()
    {
        // Act
        Action act = () => PathValidator.ResolveSafePath("", _workspaceRoot);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ResolveSafePath_EmptyWorkspaceRoot_ThrowsArgumentException()
    {
        // Act
        Action act = () => PathValidator.ResolveSafePath("file.txt", "");

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ResolveSafePath_AbsolutePathOutsideWorkspace_Throws()
    {
        // Act
        Action act = () => PathValidator.ResolveSafePath("/etc/passwd", _workspaceRoot);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside*");
    }

    [Test]
    public void ResolveSafePath_AbsolutePathInsideWorkspace_ReturnsPath()
    {
        // Arrange
        var innerPath = Path.Combine(_workspaceRoot, "inner", "file.txt");

        // Act
        var result = PathValidator.ResolveSafePath(innerPath, _workspaceRoot);

        // Assert
        result.Should().Be(innerPath);
    }

    [Test]
    public void ResolveSafePath_DotDotInsideWorkspace_ResolvesCorrectly()
    {
        // "src/../lib/file.cs" should resolve to {workspace}/lib/file.cs
        var result = PathValidator.ResolveSafePath("src/../lib/file.cs", _workspaceRoot);

        result.Should().Be(Path.Combine(_workspaceRoot, "lib", "file.cs"));
    }

    [Test]
    public void ResolveSafePath_WhitespacePath_ThrowsArgumentException()
    {
        Action act = () => PathValidator.ResolveSafePath("   ", _workspaceRoot);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void ResolveSafePath_SymlinkPointingOutsideWorkspace_Throws()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Symlink creation may require elevated privileges on Windows.");
            return;
        }

        // Arrange — create a symlink inside workspace that points to /tmp (outside workspace)
        var outsideTarget = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var symlinkPath = Path.Combine(_workspaceRoot, "evil_link");
        File.CreateSymbolicLink(symlinkPath, outsideTarget);

        // Act
        Action act = () => PathValidator.ResolveSafePath("evil_link", _workspaceRoot);

        // Assert — should throw because the symlink target is outside workspace
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside*");
    }

    [Test]
    public void ResolveSafePath_SymlinkPointingInsideWorkspace_Succeeds()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Symlink creation may require elevated privileges on Windows.");
            return;
        }

        // Arrange — create a real target inside workspace, then a symlink to it
        var subdir = Path.Combine(_workspaceRoot, "real_dir");
        Directory.CreateDirectory(subdir);
        var realFile = Path.Combine(subdir, "file.txt");
        File.WriteAllText(realFile, "content");

        var symlinkPath = Path.Combine(_workspaceRoot, "good_link");
        File.CreateSymbolicLink(symlinkPath, realFile);

        // Act
        var result = PathValidator.ResolveSafePath("good_link", _workspaceRoot);

        // Assert — should succeed; the resolved path is the symlink path itself
        result.Should().Be(symlinkPath);
    }

    [Test]
    public void ResolveSafePath_ChainedSymlinksEscapingWorkspace_Throws()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Symlink creation may require elevated privileges on Windows.");
            return;
        }

        // Arrange — create a chain: link1 -> link2 -> /tmp (outside workspace)
        // We create an intermediate directory outside workspace to be the final target
        var outsideDir = Path.Combine(Path.GetTempPath(), $"tamma_outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);

        try
        {
            var link1 = Path.Combine(_workspaceRoot, "chain_link");
            File.CreateSymbolicLink(link1, outsideDir);

            // Act
            Action act = () => PathValidator.ResolveSafePath("chain_link", _workspaceRoot);

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*outside*");
        }
        finally
        {
            if (Directory.Exists(outsideDir))
                Directory.Delete(outsideDir, recursive: true);
        }
    }
}
