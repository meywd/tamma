using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

[TestFixture]
public class SearchCodeToolTests
{
    private Mock<ILogger<SearchCodeTool>> _loggerMock = null!;
    private string _workspaceRoot = null!;
    private SearchCodeTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<SearchCodeTool>>();
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"tamma_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ToolExecution:WorkspaceRoot"] = _workspaceRoot
            })
            .Build();

        _tool = new SearchCodeTool(_loggerMock.Object, config);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
            Directory.Delete(_workspaceRoot, recursive: true);
    }

    [Test]
    public async Task ExecuteAsync_PatternFound_ReturnsMatches()
    {
        // Arrange
        await File.WriteAllTextAsync(
            Path.Combine(_workspaceRoot, "test.cs"),
            "using System;\nclass Foo { }\nclass Bar { }");

        // Act
        var result = await _tool.ExecuteAsync("tc1",
            """{"pattern": "class\\s+\\w+"}""");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("test.cs");
        result.Output.Should().Contain("class Foo");
        result.Output.Should().Contain("class Bar");
    }

    [Test]
    public async Task ExecuteAsync_NoMatches_ReturnsEmpty()
    {
        // Arrange
        await File.WriteAllTextAsync(
            Path.Combine(_workspaceRoot, "test.cs"),
            "using System;");

        // Act
        var result = await _tool.ExecuteAsync("tc2",
            """{"pattern": "ZZZZZZZZZ_NOT_FOUND"}""");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("No matches found");
    }

    [Test]
    public async Task ExecuteAsync_MaxResultsRespected()
    {
        // Arrange — create a file with many matching lines
        var lines = Enumerable.Range(1, 100).Select(i => $"match line {i}").ToArray();
        await File.WriteAllLinesAsync(
            Path.Combine(_workspaceRoot, "many.txt"), lines);

        // Act
        var result = await _tool.ExecuteAsync("tc3",
            """{"pattern": "match line", "max_results": 5}""");

        // Assert
        result.Success.Should().BeTrue();
        // Count the number of lines in the output (minus empty trailing line)
        var outputLines = result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        outputLines.Length.Should().BeLessOrEqualTo(5);
    }

    [Test]
    public async Task ExecuteAsync_FileGlobFilter_RespectsGlob()
    {
        // Arrange
        await File.WriteAllTextAsync(
            Path.Combine(_workspaceRoot, "file.cs"), "match here");
        await File.WriteAllTextAsync(
            Path.Combine(_workspaceRoot, "file.txt"), "match here too");

        // Act
        var result = await _tool.ExecuteAsync("tc4",
            """{"pattern": "match", "file_glob": "*.cs"}""");

        // Assert
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("file.cs");
        result.Output.Should().NotContain("file.txt");
    }

    [Test]
    public async Task ExecuteAsync_InvalidRegex_ReturnsError()
    {
        // Act
        var result = await _tool.ExecuteAsync("tc5",
            """{"pattern": "[invalid"}""");

        // Assert
        result.Success.Should().BeFalse();
        result.Output.Should().Contain("Invalid regex pattern");
    }

    [Test]
    public void ToolName_IsSearchCode()
    {
        _tool.ToolName.Should().Be("search_code");
    }
}
