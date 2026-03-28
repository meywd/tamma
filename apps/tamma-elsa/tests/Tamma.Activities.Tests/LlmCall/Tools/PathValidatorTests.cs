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
}
