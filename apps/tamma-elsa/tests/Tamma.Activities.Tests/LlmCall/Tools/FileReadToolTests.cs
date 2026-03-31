using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

[TestFixture]
public class FileReadToolTests
{
    private Mock<ILogger<FileReadTool>> _loggerMock = null!;
    private string _workspaceRoot = null!;
    private FileReadTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<FileReadTool>>();
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"tamma_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolExecution:WorkspaceRoot"] = _workspaceRoot
            })
            .Build();

        _tool = new FileReadTool(_loggerMock.Object, config);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
            Directory.Delete(_workspaceRoot, recursive: true);
    }

    [Test]
    public async Task ExecuteAsync_ExistingFile_ReturnsContent()
    {
        // Arrange
        var filePath = Path.Combine(_workspaceRoot, "test.txt");
        await File.WriteAllTextAsync(filePath, "Hello, World!");

        // Act
        var result = await _tool.ExecuteAsync("tc1", """{"path": "test.txt"}""");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Be("Hello, World!");
        result.ToolCallId.Should().Be("tc1");
        result.ToolName.Should().Be("file_read");
        result.DurationMs.Should().BeGreaterOrEqualTo(0);
    }

    [Test]
    public async Task ExecuteAsync_PathTraversal_ReturnsDenied()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc2", """{"path": "../../../etc/passwd"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("Access denied");
    }

    [Test]
    public async Task ExecuteAsync_FileNotFound_ReturnsError()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc3", """{"path": "nonexistent.txt"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("File not found");
    }

    [Test]
    public async Task ExecuteAsync_LargeFile_OutputTruncated()
    {
        // Arrange — create a file larger than 50KB
        var filePath = Path.Combine(_workspaceRoot, "large.txt");
        var largeContent = new string('A', 100 * 1024); // 100KB
        await File.WriteAllTextAsync(filePath, largeContent);

        // Act
        var result = await _tool.ExecuteAsync("tc4", """{"path": "large.txt"}""");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("[truncated:");
        result.Output.Length.Should().BeLessThan(largeContent.Length);
    }

    [Test]
    public async Task ExecuteAsync_NestedFile_ReturnsContent()
    {
        // Arrange
        var nestedDir = Path.Combine(_workspaceRoot, "src", "sub");
        Directory.CreateDirectory(nestedDir);
        await File.WriteAllTextAsync(Path.Combine(nestedDir, "nested.cs"), "using System;");

        // Act
        var result = await _tool.ExecuteAsync("tc5",
            """{"path": "src/sub/nested.cs"}""");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Be("using System;");
    }

    [Test]
    public async Task ExecuteAsync_MissingPathArgument_ReturnsError()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc6", """{"file": "test.txt"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("Error reading file");
    }

    [Test]
    public async Task ExecuteAsync_InvalidJson_ReturnsError()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc7", "not json");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("Error reading file");
    }

    [Test]
    public void ToolName_IsFileRead()
    {
        _tool.ToolName.Should().Be("file_read");
    }

    [Test]
    public void Description_IsNotEmpty()
    {
        _tool.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void InputSchema_ContainsPathProperty()
    {
        _tool.InputSchema.Should().ContainKey("properties");
    }
}
