using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.Tests.LlmCall.Tools;

[TestFixture]
public class ToolExecutorRegistryTests
{
    private Mock<ILogger<ToolExecutorRegistry>> _loggerMock = null!;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<ToolExecutorRegistry>>();
    }

    private static Mock<IToolExecutor> CreateMockExecutor(string name)
    {
        var mock = new Mock<IToolExecutor>();
        mock.Setup(e => e.ToolName).Returns(name);
        mock.Setup(e => e.Description).Returns($"Description for {name}");
        mock.Setup(e => e.InputSchema).Returns(new Dictionary<string, object>());
        return mock;
    }

    [Test]
    public void GetExecutor_RegisteredTool_ReturnsExecutor()
    {
        // Arrange
        var mockExecutor = CreateMockExecutor("file_read");
        var registry = new ToolExecutorRegistry(
            new[] { mockExecutor.Object }, _loggerMock.Object);

        // Act
        var result = registry.GetExecutor("file_read");

        // Assert
        result.Should().NotBeNull();
        result!.ToolName.Should().Be("file_read");
    }

    [Test]
    public void GetExecutor_UnknownTool_ReturnsNull()
    {
        // Arrange
        var registry = new ToolExecutorRegistry(
            Array.Empty<IToolExecutor>(), _loggerMock.Object);

        // Act
        var result = registry.GetExecutor("nonexistent_tool");

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public void GetExecutor_CaseInsensitive_ReturnsExecutor()
    {
        // Arrange
        var mockExecutor = CreateMockExecutor("file_read");
        var registry = new ToolExecutorRegistry(
            new[] { mockExecutor.Object }, _loggerMock.Object);

        // Act
        var result = registry.GetExecutor("FILE_READ");

        // Assert
        result.Should().NotBeNull();
        result!.ToolName.Should().Be("file_read");
    }

    [Test]
    public void IsAllowed_NullAllowlist_ReturnsTrue()
    {
        // Arrange
        var registry = new ToolExecutorRegistry(
            Array.Empty<IToolExecutor>(), _loggerMock.Object);

        // Act
        var result = registry.IsAllowed("file_read", null);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void IsAllowed_EmptyAllowlist_ReturnsTrue()
    {
        // Arrange
        var registry = new ToolExecutorRegistry(
            Array.Empty<IToolExecutor>(), _loggerMock.Object);

        // Act
        var result = registry.IsAllowed("file_read", Array.Empty<string>());

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void IsAllowed_ToolInAllowlist_ReturnsTrue()
    {
        // Arrange
        var registry = new ToolExecutorRegistry(
            Array.Empty<IToolExecutor>(), _loggerMock.Object);

        // Act
        var result = registry.IsAllowed("file_read", new[] { "file_read", "file_write" });

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void IsAllowed_ToolNotInAllowlist_ReturnsFalse()
    {
        // Arrange
        var registry = new ToolExecutorRegistry(
            Array.Empty<IToolExecutor>(), _loggerMock.Object);

        // Act
        var result = registry.IsAllowed("shell_execute", new[] { "file_read", "file_write" });

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void IsAllowed_CaseInsensitiveAllowlist_ReturnsTrue()
    {
        // Arrange
        var registry = new ToolExecutorRegistry(
            Array.Empty<IToolExecutor>(), _loggerMock.Object);

        // Act
        var result = registry.IsAllowed("FILE_READ", new[] { "file_read" });

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void GetAll_ReturnsAllRegistered()
    {
        // Arrange
        var executors = new[]
        {
            CreateMockExecutor("file_read").Object,
            CreateMockExecutor("file_write").Object,
            CreateMockExecutor("search_code").Object,
        };
        var registry = new ToolExecutorRegistry(executors, _loggerMock.Object);

        // Act
        var result = registry.GetAll();

        // Assert
        result.Should().HaveCount(3);
        result.Select(e => e.ToolName).Should().Contain("file_read");
        result.Select(e => e.ToolName).Should().Contain("file_write");
        result.Select(e => e.ToolName).Should().Contain("search_code");
    }

    [Test]
    public void GetAllowed_FiltersCorrectly()
    {
        // Arrange
        var executors = new[]
        {
            CreateMockExecutor("file_read").Object,
            CreateMockExecutor("file_write").Object,
            CreateMockExecutor("shell_execute").Object,
        };
        var registry = new ToolExecutorRegistry(executors, _loggerMock.Object);

        // Act
        var result = registry.GetAllowed(new[] { "file_read", "file_write" });

        // Assert
        result.Should().HaveCount(2);
        result.Select(e => e.ToolName).Should().Contain("file_read");
        result.Select(e => e.ToolName).Should().Contain("file_write");
        result.Select(e => e.ToolName).Should().NotContain("shell_execute");
    }

    [Test]
    public void GetAllowed_NullAllowlist_ReturnsAll()
    {
        // Arrange
        var executors = new[]
        {
            CreateMockExecutor("file_read").Object,
            CreateMockExecutor("file_write").Object,
        };
        var registry = new ToolExecutorRegistry(executors, _loggerMock.Object);

        // Act
        var result = registry.GetAllowed(null);

        // Assert
        result.Should().HaveCount(2);
    }

    [Test]
    public void DuplicateRegistration_KeepsFirst()
    {
        // Arrange
        var first = CreateMockExecutor("file_read");
        first.Setup(e => e.Description).Returns("First registration");
        var second = CreateMockExecutor("file_read");
        second.Setup(e => e.Description).Returns("Second registration");

        var registry = new ToolExecutorRegistry(
            new[] { first.Object, second.Object }, _loggerMock.Object);

        // Act
        var result = registry.GetExecutor("file_read");

        // Assert
        result.Should().NotBeNull();
        result!.Description.Should().Be("First registration");
        registry.GetAll().Should().HaveCount(1);
    }
}
