using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

[TestFixture]
public class FileWriteToolTests
{
    private Mock<ILogger<FileWriteTool>> _loggerMock = null!;
    private string _workspaceRoot = null!;
    private FileWriteTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<FileWriteTool>>();
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"tamma_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolExecution:WorkspaceRoot"] = _workspaceRoot
            })
            .Build();

        _tool = new FileWriteTool(_loggerMock.Object, config);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
            Directory.Delete(_workspaceRoot, recursive: true);
    }

    [Test]
    public async Task ExecuteAsync_NewFile_CreatesFile()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc1",
            """{"path": "new_file.txt", "content": "Hello, World!"}""");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("Successfully wrote");
        result.Output.Should().Contain("13 characters");

        var writtenContent = await File.ReadAllTextAsync(
            Path.Combine(_workspaceRoot, "new_file.txt"));
        writtenContent.Should().Be("Hello, World!");
    }

    [Test]
    public async Task ExecuteAsync_ExistingFile_Overwrites()
    {
        // Arrange
        var filePath = Path.Combine(_workspaceRoot, "existing.txt");
        await File.WriteAllTextAsync(filePath, "old content");

        // Act
        var result = await _tool.ExecuteAsync("tc2",
            """{"path": "existing.txt", "content": "new content"}""");

        // Assert
        result.Success.Should().BeTrue();
        var writtenContent = await File.ReadAllTextAsync(filePath);
        writtenContent.Should().Be("new content");
    }

    [Test]
    public async Task ExecuteAsync_PathTraversal_ReturnsDenied()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc3",
            """{"path": "../../../tmp/evil.txt", "content": "malicious"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("Access denied");
    }

    [Test]
    public async Task ExecuteAsync_CreatesParentDirectories()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc4",
            """{"path": "deep/nested/dir/file.txt", "content": "nested content"}""");

        // Assert
        result.Success.Should().BeTrue();

        var writtenContent = await File.ReadAllTextAsync(
            Path.Combine(_workspaceRoot, "deep", "nested", "dir", "file.txt"));
        writtenContent.Should().Be("nested content");
    }

    [Test]
    public async Task ExecuteAsync_MissingContentArgument_ReturnsError()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc5", """{"path": "test.txt"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("Error writing file");
    }

    [Test]
    public void ToolName_IsFileWrite()
    {
        _tool.ToolName.Should().Be("file_write");
    }
}
