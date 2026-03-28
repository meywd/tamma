using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Activities.ToolExecution;

namespace Tamma.Activities.Tests.ToolExecution;

/// <summary>
/// Tests for <see cref="ParallelToolExecutor"/> (Story 12.4).
/// Covers parallel execution, semaphore serialization, timeouts,
/// single-tool optimization, and event emission.
/// </summary>
[TestFixture]
public class ParallelToolExecutorTests
{
    private ParallelToolExecutor _executor = null!;
    private Mock<IToolExecutorRegistry> _registryMock = null!;
    private Mock<ILogger<ParallelToolExecutor>> _loggerMock = null!;

    [SetUp]
    public void SetUp()
    {
        _loggerMock = new Mock<ILogger<ParallelToolExecutor>>();
        _registryMock = new Mock<IToolExecutorRegistry>();
        _executor = new ParallelToolExecutor(_loggerMock.Object);
    }

    // =====================================================================
    // Parallel Execution Tests
    // =====================================================================

    [Test]
    public async Task IndependentTools_ExecuteInParallel_VerifiedByTiming()
    {
        // Two tools that each take 200ms — if sequential, >400ms; if parallel, ~200ms
        var slowTool = new DelayToolExecutor("search_code", delay: 200);
        _registryMock.Setup(r => r.GetExecutor("search_code")).Returns(slowTool);

        var slowTool2 = new DelayToolExecutor("shell_execute", delay: 200);
        _registryMock.Setup(r => r.GetExecutor("shell_execute")).Returns(slowTool2);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "search_code", ArgumentsJson = "{}" },
            new LlmToolCall { Id = "c2", ToolName = "shell_execute", ArgumentsJson = "{}" }
        };

        var sw = Stopwatch.StartNew();
        var results = await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0);
        sw.Stop();

        results.Should().HaveCount(2);
        results[0].Success.Should().BeTrue();
        results[1].Success.Should().BeTrue();
        // If parallel, total time should be under 400ms (approx 200ms + overhead)
        sw.ElapsedMilliseconds.Should().BeLessThan(400);
    }

    [Test]
    public async Task SingleToolCall_NoParallelOverhead()
    {
        var tool = new ImmediateToolExecutor("file_read");
        _registryMock.Setup(r => r.GetExecutor("file_read")).Returns(tool);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "file_read", ArgumentsJson = "{}" }
        };

        var results = await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        results[0].ToolCallId.Should().Be("c1");
    }

    [Test]
    public async Task EmptyToolCalls_ReturnsEmptyArray()
    {
        var toolCalls = Array.Empty<LlmToolCall>();

        var results = await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0);

        results.Should().BeEmpty();
    }

    [Test]
    public async Task UnknownTool_ReturnsErrorResult()
    {
        _registryMock.Setup(r => r.GetExecutor("nonexistent")).Returns((IToolExecutor?)null);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "nonexistent", ArgumentsJson = "{}" }
        };

        var results = await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeFalse();
        results[0].Output.Should().Contain("Unknown tool");
    }

    [Test]
    public async Task AllResultsCollected_EvenWithMixedSuccessFailure()
    {
        var successTool = new ImmediateToolExecutor("search_code");
        var failTool = new FailingToolExecutor("shell_execute");
        _registryMock.Setup(r => r.GetExecutor("search_code")).Returns(successTool);
        _registryMock.Setup(r => r.GetExecutor("shell_execute")).Returns(failTool);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "search_code", ArgumentsJson = "{}" },
            new LlmToolCall { Id = "c2", ToolName = "shell_execute", ArgumentsJson = "{}" }
        };

        var results = await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0);

        results.Should().HaveCount(2);
        results[0].Success.Should().BeTrue();
        results[0].ToolCallId.Should().Be("c1");
        results[1].Success.Should().BeFalse();
        results[1].ToolCallId.Should().Be("c2");
    }

    // =====================================================================
    // Semaphore Serialization Tests
    // =====================================================================

    [Test]
    public async Task SameFilePath_SerializedViaSemaphore()
    {
        // Two file_read calls on the same path — should be serialized, not concurrent
        var fsToolMock = new FileSystemDelayTool("file_read", "src/foo.cs", delay: 100);
        _registryMock.Setup(r => r.GetExecutor("file_read")).Returns(fsToolMock);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "file_read", ArgumentsJson = "{\"path\":\"src/foo.cs\"}" },
            new LlmToolCall { Id = "c2", ToolName = "file_read", ArgumentsJson = "{\"path\":\"src/foo.cs\"}" }
        };

        var sw = Stopwatch.StartNew();
        var results = await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0);
        sw.Stop();

        results.Should().HaveCount(2);
        results[0].Success.Should().BeTrue();
        results[1].Success.Should().BeTrue();
        // Serialized: should take at least 200ms (2x 100ms)
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(180);
    }

    [Test]
    public async Task DifferentFilePaths_RunInParallel()
    {
        // Two file_read calls on different paths — should run concurrently
        var fsTool1 = new FileSystemDelayTool("file_read", delay: 200);
        _registryMock.Setup(r => r.GetExecutor("file_read")).Returns(fsTool1);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "file_read", ArgumentsJson = "{\"path\":\"src/a.cs\"}" },
            new LlmToolCall { Id = "c2", ToolName = "file_read", ArgumentsJson = "{\"path\":\"src/b.cs\"}" }
        };

        var sw = Stopwatch.StartNew();
        var results = await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0);
        sw.Stop();

        results.Should().HaveCount(2);
        results[0].Success.Should().BeTrue();
        results[1].Success.Should().BeTrue();
        // Parallel: should take around 200ms, not 400ms
        sw.ElapsedMilliseconds.Should().BeLessThan(400);
    }

    [Test]
    public async Task FileSemaphore_AcquiredAndReleased()
    {
        // Test that semaphore is properly released even after execution
        var fsTool = new FileSystemDelayTool("file_read", delay: 10);
        _registryMock.Setup(r => r.GetExecutor("file_read")).Returns(fsTool);

        // First execution
        var toolCalls1 = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "file_read", ArgumentsJson = "{\"path\":\"src/x.cs\"}" }
        };
        var results1 = await _executor.ExecuteToolsInParallelAsync(
            toolCalls1, _registryMock.Object, 60_000, "wf-1", 0);
        results1[0].Success.Should().BeTrue();

        // Second execution on same path should also succeed (semaphore was released)
        var toolCalls2 = new[]
        {
            new LlmToolCall { Id = "c2", ToolName = "file_read", ArgumentsJson = "{\"path\":\"src/x.cs\"}" }
        };
        var results2 = await _executor.ExecuteToolsInParallelAsync(
            toolCalls2, _registryMock.Object, 60_000, "wf-1", 1);
        results2[0].Success.Should().BeTrue();
    }

    // =====================================================================
    // Timeout Tests
    // =====================================================================

    [Test]
    public async Task IndividualToolTimeout_CancelsWithoutAffectingOthers()
    {
        // First tool takes 5000ms but timeout is 100ms, second tool is instant
        var slowTool = new DelayToolExecutor("slow_tool", delay: 5000);
        var fastTool = new ImmediateToolExecutor("fast_tool");
        _registryMock.Setup(r => r.GetExecutor("slow_tool")).Returns(slowTool);
        _registryMock.Setup(r => r.GetExecutor("fast_tool")).Returns(fastTool);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "slow_tool", ArgumentsJson = "{}" },
            new LlmToolCall { Id = "c2", ToolName = "fast_tool", ArgumentsJson = "{}" }
        };

        var results = await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 100, "wf-1", 0);

        results.Should().HaveCount(2);
        // Slow tool should timeout
        results[0].Success.Should().BeFalse();
        results[0].Output.Should().Contain("timed out");
        // Fast tool should succeed
        results[1].Success.Should().BeTrue();
    }

    // =====================================================================
    // Event Emission Tests
    // =====================================================================

    [Test]
    public async Task WithEventEmitter_EmitsToolExecutingAndCompleted()
    {
        var sinkMock = new Mock<IToolLoopEventSink>();
        var emitterLogger = new Mock<ILogger<ToolLoopEventEmitter>>();
        var emitter = new ToolLoopEventEmitter(emitterLogger.Object, sinkMock.Object);

        var tool = new ImmediateToolExecutor("file_read");
        _registryMock.Setup(r => r.GetExecutor("file_read")).Returns(tool);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "file_read", ArgumentsJson = "{}" }
        };

        await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0, emitter);

        // Should have emitted TOOL_EXECUTING and TOOL_COMPLETED
        sinkMock.Verify(
            s => s.WriteEventAsync("TOOL_LOOP.TOOL_EXECUTING", It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
        sinkMock.Verify(
            s => s.WriteEventAsync("TOOL_LOOP.TOOL_COMPLETED", It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WithoutEventEmitter_NoEventsSent()
    {
        var tool = new ImmediateToolExecutor("file_read");
        _registryMock.Setup(r => r.GetExecutor("file_read")).Returns(tool);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "file_read", ArgumentsJson = "{}" }
        };

        // null emitter — should not throw
        var results = await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0, null);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
    }

    [Test]
    public async Task MultipleTools_EmitsEventsForEach()
    {
        var sinkMock = new Mock<IToolLoopEventSink>();
        var emitterLogger = new Mock<ILogger<ToolLoopEventEmitter>>();
        var emitter = new ToolLoopEventEmitter(emitterLogger.Object, sinkMock.Object);

        var tool1 = new ImmediateToolExecutor("search_code");
        var tool2 = new ImmediateToolExecutor("shell_execute");
        _registryMock.Setup(r => r.GetExecutor("search_code")).Returns(tool1);
        _registryMock.Setup(r => r.GetExecutor("shell_execute")).Returns(tool2);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "search_code", ArgumentsJson = "{}" },
            new LlmToolCall { Id = "c2", ToolName = "shell_execute", ArgumentsJson = "{}" }
        };

        await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0, emitter);

        sinkMock.Verify(
            s => s.WriteEventAsync("TOOL_LOOP.TOOL_EXECUTING", It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        sinkMock.Verify(
            s => s.WriteEventAsync("TOOL_LOOP.TOOL_COMPLETED", It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // =====================================================================
    // NormalizePath Tests
    // =====================================================================

    [Test]
    public void NormalizePath_BackslashesToForwardSlashes()
    {
        ParallelToolExecutor.NormalizePath(@"src\foo\bar.cs")
            .Should().Be("src/foo/bar.cs");
    }

    [Test]
    public void NormalizePath_LowercasesPath()
    {
        ParallelToolExecutor.NormalizePath("Src/Foo/Bar.cs")
            .Should().Be("src/foo/bar.cs");
    }

    [Test]
    public void NormalizePath_TrimsTrailingSeparator()
    {
        ParallelToolExecutor.NormalizePath("src/foo/")
            .Should().Be("src/foo");
    }

    [Test]
    public void NormalizePath_EmptyString_ReturnsEmpty()
    {
        ParallelToolExecutor.NormalizePath("")
            .Should().Be("");
    }

    [Test]
    public void NormalizePath_NullOrWhitespace_ReturnsEmpty()
    {
        ParallelToolExecutor.NormalizePath("  ")
            .Should().Be("");
    }

    // =====================================================================
    // Tool Executor Exception Handling
    // =====================================================================

    [Test]
    public async Task ToolThatThrows_ReturnsErrorResult()
    {
        var throwingTool = new ThrowingToolExecutor("bad_tool");
        _registryMock.Setup(r => r.GetExecutor("bad_tool")).Returns(throwingTool);

        var toolCalls = new[]
        {
            new LlmToolCall { Id = "c1", ToolName = "bad_tool", ArgumentsJson = "{}" }
        };

        var results = await _executor.ExecuteToolsInParallelAsync(
            toolCalls, _registryMock.Object, 60_000, "wf-1", 0);

        results.Should().HaveCount(1);
        results[0].Success.Should().BeFalse();
        results[0].Output.Should().Contain("Tool execution error");
    }

    // =====================================================================
    // Test Helper Tool Executors
    // =====================================================================

    private class ImmediateToolExecutor : IToolExecutor
    {
        public string ToolName { get; }
        public string Description => "Test tool";
        public Dictionary<string, object> InputSchema => new();

        public ImmediateToolExecutor(string name) => ToolName = name;

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolExecutionResult(toolCallId, ToolName, true, "ok", 1));
    }

    private class DelayToolExecutor : IToolExecutor
    {
        public string ToolName { get; }
        public string Description => "Test tool with delay";
        public Dictionary<string, object> InputSchema => new();
        private readonly int _delay;

        public DelayToolExecutor(string name, int delay = 100)
        {
            ToolName = name;
            _delay = delay;
        }

        public async Task<ToolExecutionResult> ExecuteAsync(
            string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return new ToolExecutionResult(toolCallId, ToolName, true, "delayed ok", _delay);
        }
    }

    private class FailingToolExecutor : IToolExecutor
    {
        public string ToolName { get; }
        public string Description => "Test tool that always fails";
        public Dictionary<string, object> InputSchema => new();

        public FailingToolExecutor(string name) => ToolName = name;

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolExecutionResult(toolCallId, ToolName, false, "Simulated failure", 1));
    }

    private class ThrowingToolExecutor : IToolExecutor
    {
        public string ToolName { get; }
        public string Description => "Test tool that throws";
        public Dictionary<string, object> InputSchema => new();

        public ThrowingToolExecutor(string name) => ToolName = name;

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated explosion");
    }

    /// <summary>
    /// A tool executor that implements both IToolExecutor and IFileSystemTool
    /// for testing semaphore serialization.
    /// </summary>
    private class FileSystemDelayTool : IToolExecutor, IFileSystemTool
    {
        public string ToolName { get; }
        public string Description => "Test filesystem tool";
        public Dictionary<string, object> InputSchema => new();
        private readonly string? _fixedPath;
        private readonly int _delay;

        public FileSystemDelayTool(string name, string? fixedPath = null, int delay = 100)
        {
            ToolName = name;
            _fixedPath = fixedPath;
            _delay = delay;
        }

        public string GetTargetPath(string argumentsJson)
        {
            if (_fixedPath != null)
                return _fixedPath;

            try
            {
                var args = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(argumentsJson);
                return args.GetProperty("path").GetString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        public async Task<ToolExecutionResult> ExecuteAsync(
            string toolCallId, string argumentsJson, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return new ToolExecutionResult(toolCallId, ToolName, true, "fs ok", _delay);
        }
    }
}
