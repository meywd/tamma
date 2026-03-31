using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ToolExecution;

namespace Tamma.Activities.Tests.ToolExecution;

/// <summary>
/// Tests for <see cref="ToolLoopEventEmitter"/> (Story 12.4).
/// Covers event emission for turn start/end, tool executing/completed, loop completion,
/// and behavior with null/no-op sinks.
/// </summary>
[TestFixture]
public class ToolLoopEventEmitterTests
{
    private ToolLoopEventEmitter _emitter = null!;
    private Mock<IToolLoopEventSink> _sinkMock = null!;
    private Mock<ILogger<ToolLoopEventEmitter>> _loggerMock = null!;

    [SetUp]
    public void SetUp()
    {
        _sinkMock = new Mock<IToolLoopEventSink>();
        _loggerMock = new Mock<ILogger<ToolLoopEventEmitter>>();
        _emitter = new ToolLoopEventEmitter(_loggerMock.Object, _sinkMock.Object);
    }

    // =====================================================================
    // TURN_STARTED Event Tests
    // =====================================================================

    [Test]
    public async Task EmitTurnStarted_SendsCorrectEventType()
    {
        await _emitter.EmitTurnStarted(3, 8, 45000, "wf-123");

        _sinkMock.Verify(
            s => s.WriteEventAsync(
                "TOOL_LOOP.TURN_STARTED",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task EmitTurnStarted_IncludesExpectedData()
    {
        object? capturedData = null;
        _sinkMock.Setup(s => s.WriteEventAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((_, data, _) => capturedData = data)
            .Returns(Task.CompletedTask);

        await _emitter.EmitTurnStarted(3, 8, 45000, "wf-123");

        capturedData.Should().NotBeNull();
        var json = System.Text.Json.JsonSerializer.Serialize(capturedData);
        json.Should().Contain("\"turnNumber\":3");
        json.Should().Contain("\"messageCount\":8");
        json.Should().Contain("\"estimatedTokens\":45000");
    }

    // =====================================================================
    // TOOL_EXECUTING Event Tests
    // =====================================================================

    [Test]
    public async Task EmitToolExecuting_SendsCorrectEventType()
    {
        await _emitter.EmitToolExecuting(3, "file_read", "call_abc", "wf-123");

        _sinkMock.Verify(
            s => s.WriteEventAsync(
                "TOOL_LOOP.TOOL_EXECUTING",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task EmitToolExecuting_IncludesToolNameAndCallId()
    {
        object? capturedData = null;
        _sinkMock.Setup(s => s.WriteEventAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((_, data, _) => capturedData = data)
            .Returns(Task.CompletedTask);

        await _emitter.EmitToolExecuting(3, "file_read", "call_abc", "wf-123");

        capturedData.Should().NotBeNull();
        var json = System.Text.Json.JsonSerializer.Serialize(capturedData);
        json.Should().Contain("\"toolName\":\"file_read\"");
        json.Should().Contain("\"toolCallId\":\"call_abc\"");
        json.Should().Contain("\"turnNumber\":3");
    }

    // =====================================================================
    // TOOL_COMPLETED Event Tests
    // =====================================================================

    [Test]
    public async Task EmitToolCompleted_SendsCorrectEventType()
    {
        await _emitter.EmitToolCompleted(3, "file_read", "call_abc", true, 45, "wf-123");

        _sinkMock.Verify(
            s => s.WriteEventAsync(
                "TOOL_LOOP.TOOL_COMPLETED",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task EmitToolCompleted_IncludesSuccessAndDuration()
    {
        object? capturedData = null;
        _sinkMock.Setup(s => s.WriteEventAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((_, data, _) => capturedData = data)
            .Returns(Task.CompletedTask);

        await _emitter.EmitToolCompleted(3, "file_read", "call_abc", true, 45, "wf-123");

        capturedData.Should().NotBeNull();
        var json = System.Text.Json.JsonSerializer.Serialize(capturedData);
        json.Should().Contain("\"success\":true");
        json.Should().Contain("\"durationMs\":45");
        json.Should().Contain("\"toolName\":\"file_read\"");
    }

    [Test]
    public async Task EmitToolCompleted_FailedTool_IncludesSuccessFalse()
    {
        object? capturedData = null;
        _sinkMock.Setup(s => s.WriteEventAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((_, data, _) => capturedData = data)
            .Returns(Task.CompletedTask);

        await _emitter.EmitToolCompleted(1, "shell_execute", "call_xyz", false, 1234, "wf-456");

        capturedData.Should().NotBeNull();
        var json = System.Text.Json.JsonSerializer.Serialize(capturedData);
        json.Should().Contain("\"success\":false");
    }

    // =====================================================================
    // TURN_COMPLETED Event Tests
    // =====================================================================

    [Test]
    public async Task EmitTurnCompleted_SendsCorrectEventType()
    {
        await _emitter.EmitTurnCompleted(3, 2, 165, 47500, "wf-123");

        _sinkMock.Verify(
            s => s.WriteEventAsync(
                "TOOL_LOOP.TURN_COMPLETED",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task EmitTurnCompleted_IncludesAggregates()
    {
        object? capturedData = null;
        _sinkMock.Setup(s => s.WriteEventAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((_, data, _) => capturedData = data)
            .Returns(Task.CompletedTask);

        await _emitter.EmitTurnCompleted(3, 2, 165, 47500, "wf-123");

        capturedData.Should().NotBeNull();
        var json = System.Text.Json.JsonSerializer.Serialize(capturedData);
        json.Should().Contain("\"turnNumber\":3");
        json.Should().Contain("\"totalTools\":2");
        json.Should().Contain("\"totalDurationMs\":165");
        json.Should().Contain("\"cumulativeTokens\":47500");
    }

    // =====================================================================
    // LOOP_COMPLETED Event Tests
    // =====================================================================

    [Test]
    public async Task EmitLoopCompleted_SendsCorrectEventType()
    {
        await _emitter.EmitLoopCompleted(5, 12, 3000, 150000, false, "wf-123");

        _sinkMock.Verify(
            s => s.WriteEventAsync(
                "TOOL_LOOP.COMPLETED",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task EmitLoopCompleted_IncludesExhaustedFlag()
    {
        object? capturedData = null;
        _sinkMock.Setup(s => s.WriteEventAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((_, data, _) => capturedData = data)
            .Returns(Task.CompletedTask);

        await _emitter.EmitLoopCompleted(5, 12, 3000, 150000, true, "wf-123");

        capturedData.Should().NotBeNull();
        var json = System.Text.Json.JsonSerializer.Serialize(capturedData);
        json.Should().Contain("\"exhausted\":true");
        json.Should().Contain("\"totalTurns\":5");
        json.Should().Contain("\"totalToolCalls\":12");
    }

    // =====================================================================
    // NullToolLoopEventSink Tests
    // =====================================================================

    [Test]
    public async Task NullSink_DoesNotThrow()
    {
        var emitter = new ToolLoopEventEmitter(_loggerMock.Object, null);

        // All of these should complete without throwing
        await emitter.EmitTurnStarted(0, 2, 1000);
        await emitter.EmitToolExecuting(0, "file_read", "c1");
        await emitter.EmitToolCompleted(0, "file_read", "c1", true, 50);
        await emitter.EmitTurnCompleted(0, 1, 50, 1000);
        await emitter.EmitLoopCompleted(1, 1, 50, 1000, false);
    }

    [Test]
    public async Task NullSink_Singleton_DropsSilently()
    {
        // Direct test of the NullToolLoopEventSink
        await NullToolLoopEventSink.Instance.WriteEventAsync(
            "TOOL_LOOP.TURN_STARTED",
            new { turnNumber = 1 });
        // No assertion needed — just verifying it doesn't throw
    }

    // =====================================================================
    // Streaming Disabled (No Sink) Tests
    // =====================================================================

    [Test]
    public async Task EmitterWithNullSink_NoExternalCalls()
    {
        var emitter = new ToolLoopEventEmitter(_loggerMock.Object, NullToolLoopEventSink.Instance);

        // These should all succeed silently
        await emitter.EmitTurnStarted(0, 2, 1000, "wf-1");
        await emitter.EmitToolExecuting(0, "test", "c1", "wf-1");
        await emitter.EmitToolCompleted(0, "test", "c1", true, 10, "wf-1");
        await emitter.EmitTurnCompleted(0, 1, 10, 1000, "wf-1");
        await emitter.EmitLoopCompleted(1, 1, 10, 1000, false, "wf-1");
    }

    // =====================================================================
    // Cancellation Token Propagation
    // =====================================================================

    [Test]
    public async Task CancellationToken_PropagatedToSink()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken capturedToken = default;

        _sinkMock.Setup(s => s.WriteEventAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((_, _, ct) => capturedToken = ct)
            .Returns(Task.CompletedTask);

        await _emitter.EmitTurnStarted(0, 2, 1000, "wf-1", cts.Token);

        capturedToken.Should().Be(cts.Token);
    }
}
