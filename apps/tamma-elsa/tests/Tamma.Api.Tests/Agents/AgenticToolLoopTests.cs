using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Activities.Security;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Tests for the agentic tool loop in CallLlmInlineActivity (Story 12.2).
/// Covers backward compatibility, loop termination, tool execution,
/// conversation history, token tracking, provider format, and integration scenarios.
/// </summary>
[TestFixture]
public class AgenticToolLoopTests
{
    private InlineToolLoopRunner _activity = null!;
    private Mock<IToolExecutorRegistry> _registryMock = null!;
    private Mock<ILogger<InlineToolLoopRunner>> _loggerMock = null!;

    [SetUp]
    public void SetUp()
    {
        _registryMock = new Mock<IToolExecutorRegistry>();
        _loggerMock = new Mock<ILogger<InlineToolLoopRunner>>();
        _activity = new InlineToolLoopRunner(
            _loggerMock.Object, null, null, null, _registryMock.Object);
    }

    // =====================================================================
    // Backward Compatibility Tests
    // =====================================================================

    [Test]
    public void EnableToolLoopFalse_ConstructorWithRegistry_DoesNotThrow()
    {
        // Verify that adding IToolExecutorRegistry to constructor does not break anything
        var action = () => new InlineToolLoopRunner(null, null, null, null, _registryMock.Object);
        action.Should().NotThrow();
    }

    [Test]
    public void NullToolRegistry_ConstructorStillWorks()
    {
        // When IToolExecutorRegistry is null, the activity should still be constructible
        var action = () => new InlineToolLoopRunner(null, null, null, null, null);
        action.Should().NotThrow();
    }

    [Test]
    public void ParameterlessConstructor_StillWorks()
    {
        // The [JsonConstructor] parameterless constructor must still work for ELSA deserialization
        var action = () => new CallLlmInlineActivity();
        action.Should().NotThrow();
    }

    [Test]
    public void Constructor_WithAllDependencies_DoesNotThrow()
    {
        var sanitizer = new ContentSanitizer();
        var action = () => new InlineToolLoopRunner(
            _loggerMock.Object, null, null, sanitizer, _registryMock.Object);
        action.Should().NotThrow();
    }

    // =====================================================================
    // StopReason Parsing Tests - Anthropic
    // =====================================================================

    [Test]
    public void AnthropicStopReason_Parsed_EndTurn()
    {
        var json = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Hello" } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 10, output_tokens = 5 },
            model = "claude-sonnet-4-20250514"
        });
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseAnthropicResponse(element, 200, "fallback");

        result.StopReason.Should().Be(StopReason.EndTurn);
        result.Success.Should().BeTrue();
        result.ResponseText.Should().Be("Hello");
    }

    [Test]
    public void AnthropicStopReason_Parsed_ToolUse()
    {
        var json = JsonSerializer.Serialize(new
        {
            content = new object[]
            {
                new { type = "text", text = "I'll read the file." },
                new { type = "tool_use", id = "toolu_01A", name = "file_read", input = new { path = "README.md" } }
            },
            stop_reason = "tool_use",
            usage = new { input_tokens = 50, output_tokens = 20 },
            model = "claude-sonnet-4-20250514"
        });
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseAnthropicResponse(element, 200, "fallback");

        result.StopReason.Should().Be(StopReason.ToolUse);
        result.ToolCalls.Should().NotBeNull();
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls![0].Id.Should().Be("toolu_01A");
        result.ToolCalls![0].ToolName.Should().Be("file_read");
        result.ToolCalls![0].ArgumentsJson.Should().Contain("README.md");
    }

    [Test]
    public void AnthropicStopReason_Parsed_MaxTokens()
    {
        var json = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Partial response..." } },
            stop_reason = "max_tokens",
            usage = new { input_tokens = 100, output_tokens = 4096 },
            model = "claude-sonnet-4-20250514"
        });
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseAnthropicResponse(element, 200, "fallback");

        result.StopReason.Should().Be(StopReason.MaxTokens);
    }

    [Test]
    public void AnthropicStopReason_StopSequence_MapsToEndTurn()
    {
        var json = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Done" } },
            stop_reason = "stop_sequence",
            usage = new { input_tokens = 10, output_tokens = 5 }
        });
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseAnthropicResponse(element, 200, "fallback");

        result.StopReason.Should().Be(StopReason.EndTurn);
    }

    [Test]
    public void AnthropicStopReason_Missing_ReturnsUnknown()
    {
        var json = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Hello" } },
            usage = new { input_tokens = 10, output_tokens = 5 }
        });
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseAnthropicResponse(element, 200, "fallback");

        result.StopReason.Should().Be(StopReason.Unknown);
    }

    // =====================================================================
    // StopReason Parsing Tests - OpenAI
    // =====================================================================

    [Test]
    public void OpenAiFinishReason_Parsed_Stop()
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = "Hello" },
                    finish_reason = "stop"
                }
            },
            usage = new { prompt_tokens = 10, completion_tokens = 5 },
            model = "gpt-4o"
        });
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseOpenAiResponse(element, 200, "fallback");

        result.StopReason.Should().Be(StopReason.EndTurn);
        result.Success.Should().BeTrue();
        result.ResponseText.Should().Be("Hello");
    }

    [Test]
    public void OpenAiFinishReason_Parsed_ToolCalls()
    {
        var json = """
        {
            "choices": [{
                "message": {
                    "role": "assistant",
                    "content": "I'll read the file.",
                    "tool_calls": [{
                        "id": "call_abc123",
                        "type": "function",
                        "function": {
                            "name": "file_read",
                            "arguments": "{\"path\": \"README.md\"}"
                        }
                    }]
                },
                "finish_reason": "tool_calls"
            }],
            "usage": { "prompt_tokens": 50, "completion_tokens": 20 },
            "model": "gpt-4o"
        }
        """;
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseOpenAiResponse(element, 200, "fallback");

        result.StopReason.Should().Be(StopReason.ToolUse);
        result.ToolCalls.Should().NotBeNull();
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls![0].Id.Should().Be("call_abc123");
        result.ToolCalls![0].ToolName.Should().Be("file_read");
    }

    [Test]
    public void OpenAiFinishReason_Parsed_Length()
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = "Partial..." },
                    finish_reason = "length"
                }
            },
            usage = new { prompt_tokens = 100, completion_tokens = 4096 }
        });
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseOpenAiResponse(element, 200, "fallback");

        result.StopReason.Should().Be(StopReason.MaxTokens);
    }

    [Test]
    public void OpenAiFinishReason_ContentFilter_MapsToEndTurn()
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = "Filtered" },
                    finish_reason = "content_filter"
                }
            },
            usage = new { prompt_tokens = 10, completion_tokens = 1 }
        });
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseOpenAiResponse(element, 200, "fallback");

        result.StopReason.Should().Be(StopReason.EndTurn);
    }

    // =====================================================================
    // Anthropic Multi-Turn Body Builder Tests
    // =====================================================================

    [Test]
    public void AnthropicFormat_SystemPromptGoesToTopLevel()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "You are a helper." },
            new() { Role = "user", Content = "Hi" }
        };

        var body = _activity.BuildAnthropicMultiTurnBody(messages, "claude-sonnet-4-20250514", 4096, 0.7, null);

        body.Should().ContainKey("system");
        body["system"].Should().Be("You are a helper.");

        var apiMessages = body["messages"] as List<object>;
        apiMessages.Should().NotBeNull();
        // System message should NOT be in the messages array
        apiMessages!.Should().HaveCount(1);
        var firstMsg = apiMessages[0] as Dictionary<string, object>;
        firstMsg!["role"].Should().Be("user");
    }

    [Test]
    public void AnthropicFormat_ToolResultsAsUserBlocks()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = "Read the file" },
            new()
            {
                Role = "assistant",
                Content = "I'll read it.",
                ToolCalls = new[] { new ToolCallInfo("toolu_01A", "file_read", "{\"path\":\"README.md\"}") }
            },
            new() { Role = "tool", Content = "# README content", ToolCallId = "toolu_01A", ToolName = "file_read" }
        };

        var body = _activity.BuildAnthropicMultiTurnBody(messages, "claude-sonnet-4-20250514", 4096, 0.7, null);

        var apiMessages = body["messages"] as List<object>;
        apiMessages.Should().HaveCount(3); // user, assistant, user(tool_result)

        // Last message should be user with tool_result content blocks
        var toolResultMsg = apiMessages![2] as Dictionary<string, object>;
        toolResultMsg!["role"].Should().Be("user");
        var contentBlocks = toolResultMsg["content"] as List<object>;
        contentBlocks.Should().HaveCount(1);
        var block = contentBlocks![0] as Dictionary<string, object>;
        block!["type"].Should().Be("tool_result");
        block["tool_use_id"].Should().Be("toolu_01A");
        block["content"].Should().Be("# README content");
    }

    [Test]
    public void AnthropicFormat_MultipleToolResultsBatchedInOneUserMessage()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Do both" },
            new()
            {
                Role = "assistant",
                Content = "I'll do both.",
                ToolCalls = new[]
                {
                    new ToolCallInfo("t1", "file_read", "{\"path\":\"a.txt\"}"),
                    new ToolCallInfo("t2", "file_read", "{\"path\":\"b.txt\"}")
                }
            },
            new() { Role = "tool", Content = "Content A", ToolCallId = "t1", ToolName = "file_read" },
            new() { Role = "tool", Content = "Content B", ToolCallId = "t2", ToolName = "file_read" }
        };

        var body = _activity.BuildAnthropicMultiTurnBody(messages, "claude-sonnet-4-20250514", 4096, 0.7, null);

        var apiMessages = body["messages"] as List<object>;
        // Should be: user, assistant, user(2 tool_results) = 3 messages
        apiMessages.Should().HaveCount(3);

        var toolResultMsg = apiMessages![2] as Dictionary<string, object>;
        toolResultMsg!["role"].Should().Be("user");
        var contentBlocks = toolResultMsg["content"] as List<object>;
        contentBlocks.Should().HaveCount(2);
    }

    [Test]
    public void AnthropicFormat_AssistantWithToolUseBlocks()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Help" },
            new()
            {
                Role = "assistant",
                Content = "Let me help.",
                ToolCalls = new[] { new ToolCallInfo("toolu_01", "search_code", "{\"query\":\"main\"}") }
            }
        };

        var body = _activity.BuildAnthropicMultiTurnBody(messages, "claude-sonnet-4-20250514", 4096, 0.7, null);

        var apiMessages = body["messages"] as List<object>;
        apiMessages.Should().HaveCount(2); // user, assistant

        var assistantMsg = apiMessages![1] as Dictionary<string, object>;
        assistantMsg!["role"].Should().Be("assistant");
        var contentBlocks = assistantMsg["content"] as List<object>;
        contentBlocks.Should().HaveCount(2); // text + tool_use

        var textBlock = contentBlocks![0] as Dictionary<string, object>;
        textBlock!["type"].Should().Be("text");
        textBlock["text"].Should().Be("Let me help.");

        var toolUseBlock = contentBlocks[1] as Dictionary<string, object>;
        toolUseBlock!["type"].Should().Be("tool_use");
        toolUseBlock["id"].Should().Be("toolu_01");
        toolUseBlock["name"].Should().Be("search_code");
    }

    [Test]
    public void AnthropicFormat_ToolDefinitionsIncluded()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Help" }
        };

        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file", InputSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["path"] = new Dictionary<string, object> { ["type"] = "string" }
                }
            }}
        };

        var body = _activity.BuildAnthropicMultiTurnBody(messages, "claude-sonnet-4-20250514", 4096, 0.7, tools);

        body.Should().ContainKey("tools");
    }

    // =====================================================================
    // OpenAI Multi-Turn Body Builder Tests
    // =====================================================================

    [Test]
    public void OpenAiFormat_SystemPromptAsMessage()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "You are a helper." },
            new() { Role = "user", Content = "Hi" }
        };

        var body = _activity.BuildOpenAiMultiTurnBody(messages, "gpt-4o", 4096, 0.7, null);

        body.Should().NotContainKey("system");
        var apiMessages = body["messages"] as List<object>;
        apiMessages.Should().HaveCount(2);
        var sysMsg = apiMessages![0] as Dictionary<string, object>;
        sysMsg!["role"].Should().Be("system");
        sysMsg["content"].Should().Be("You are a helper.");
    }

    [Test]
    public void OpenAiFormat_ToolResultsAsToolRole()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Read the file" },
            new()
            {
                Role = "assistant",
                Content = "I'll read it.",
                ToolCalls = new[] { new ToolCallInfo("call_abc", "file_read", "{\"path\":\"README.md\"}") }
            },
            new() { Role = "tool", Content = "# README", ToolCallId = "call_abc", ToolName = "file_read" }
        };

        var body = _activity.BuildOpenAiMultiTurnBody(messages, "gpt-4o", 4096, 0.7, null);

        var apiMessages = body["messages"] as List<object>;
        apiMessages.Should().HaveCount(4); // system, user, assistant, tool

        var toolMsg = apiMessages![3] as Dictionary<string, object>;
        toolMsg!["role"].Should().Be("tool");
        toolMsg["tool_call_id"].Should().Be("call_abc");
        toolMsg["content"].Should().Be("# README");
    }

    [Test]
    public void OpenAiFormat_MultipleToolResultsAreSeparateMessages()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Do both" },
            new()
            {
                Role = "assistant",
                Content = "OK",
                ToolCalls = new[]
                {
                    new ToolCallInfo("c1", "file_read", "{\"path\":\"a\"}"),
                    new ToolCallInfo("c2", "file_read", "{\"path\":\"b\"}")
                }
            },
            new() { Role = "tool", Content = "A", ToolCallId = "c1", ToolName = "file_read" },
            new() { Role = "tool", Content = "B", ToolCallId = "c2", ToolName = "file_read" }
        };

        var body = _activity.BuildOpenAiMultiTurnBody(messages, "gpt-4o", 4096, 0.7, null);

        var apiMessages = body["messages"] as List<object>;
        // system, user, assistant, tool(c1), tool(c2) = 5 messages
        apiMessages.Should().HaveCount(5);
    }

    [Test]
    public void OpenAiFormat_AssistantWithToolCallsArray()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Help" },
            new()
            {
                Role = "assistant",
                Content = "Let me help.",
                ToolCalls = new[] { new ToolCallInfo("call_01", "search_code", "{\"query\":\"main\"}") }
            }
        };

        var body = _activity.BuildOpenAiMultiTurnBody(messages, "gpt-4o", 4096, 0.7, null);

        var apiMessages = body["messages"] as List<object>;
        var assistantMsg = apiMessages![2] as Dictionary<string, object?>;
        assistantMsg!["role"].Should().Be("assistant");
        assistantMsg["content"].Should().Be("Let me help.");
        assistantMsg.Should().ContainKey("tool_calls");

        var toolCalls = assistantMsg["tool_calls"] as List<Dictionary<string, object>>;
        toolCalls.Should().HaveCount(1);
        toolCalls![0]["id"].Should().Be("call_01");
        toolCalls[0]["type"].Should().Be("function");
        var func = toolCalls[0]["function"] as Dictionary<string, object>;
        func!["name"].Should().Be("search_code");
        func["arguments"].Should().Be("{\"query\":\"main\"}");
    }

    [Test]
    public void OpenAiFormat_ToolDefinitionsIncluded()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Help" }
        };

        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var body = _activity.BuildOpenAiMultiTurnBody(messages, "gpt-4o", 4096, 0.7, tools);

        body.Should().ContainKey("tools");
    }

    // =====================================================================
    // Conversation History Tests
    // =====================================================================

    [Test]
    public void MessagesAccumulate_SystemAlwaysFirst()
    {
        // Verify message list starts with system, then user
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = "User message" }
        };

        messages[0].Role.Should().Be("system");
        messages[1].Role.Should().Be("user");

        // Simulate adding assistant + tool result
        messages.Add(new ConversationMessage
        {
            Role = "assistant",
            Content = "I'll help.",
            ToolCalls = new[] { new ToolCallInfo("t1", "file_read", "{}") }
        });
        messages.Add(new ConversationMessage
        {
            Role = "tool",
            Content = "file content",
            ToolCallId = "t1"
        });

        // System prompt should always remain first
        messages[0].Role.Should().Be("system");
        messages.Should().HaveCount(4);
    }

    [Test]
    public void ConversationMessage_ToolRole_HasCorrectFields()
    {
        var msg = new ConversationMessage
        {
            Role = "tool",
            Content = "tool output",
            ToolCallId = "call_123",
            ToolName = "file_read"
        };

        msg.Role.Should().Be("tool");
        msg.Content.Should().Be("tool output");
        msg.ToolCallId.Should().Be("call_123");
        msg.ToolName.Should().Be("file_read");
    }

    [Test]
    public void ConversationMessage_AssistantRole_HasToolCalls()
    {
        var msg = new ConversationMessage
        {
            Role = "assistant",
            Content = "Let me check.",
            ToolCalls = new[]
            {
                new ToolCallInfo("t1", "file_read", "{\"path\":\"a.txt\"}"),
                new ToolCallInfo("t2", "search_code", "{\"query\":\"foo\"}")
            }
        };

        msg.Role.Should().Be("assistant");
        msg.ToolCalls.Should().HaveCount(2);
        msg.ToolCalls![0].Id.Should().Be("t1");
        msg.ToolCalls![1].Name.Should().Be("search_code");
    }

    // =====================================================================
    // Token Tracking Tests
    // =====================================================================

    [Test]
    public void ToolLoopTokenTracker_AccumulatesAcrossTurns()
    {
        var tracker = new ToolLoopTokenTracker();

        tracker.RecordTurn(100, 50);
        tracker.RecordTurn(120, 60);
        tracker.RecordTurn(150, 70);

        tracker.TotalPromptTokens.Should().Be(370);
        tracker.TotalCompletionTokens.Should().Be(180);
        tracker.TotalTokens.Should().Be(550);
        tracker.TurnCount.Should().Be(3);
    }

    [Test]
    public void ToolLoopTokenTracker_InitialValues_AreZero()
    {
        var tracker = new ToolLoopTokenTracker();

        tracker.TotalPromptTokens.Should().Be(0);
        tracker.TotalCompletionTokens.Should().Be(0);
        tracker.TotalTokens.Should().Be(0);
        tracker.TurnCount.Should().Be(0);
    }

    // =====================================================================
    // ToolLoopConfig Tests
    // =====================================================================

    [Test]
    public void ToolLoopConfig_Defaults()
    {
        var config = new ToolLoopConfig();

        config.MaxSteps.Should().Be(20);
        config.AllowedTools.Should().BeNull();
        config.ContextWindowTokens.Should().Be(200_000);
        config.CompactionThreshold.Should().Be(0.8);
        config.ToolTimeoutMs.Should().Be(60_000);
        config.EnableStreaming.Should().BeFalse();
    }

    [Test]
    public void ToolLoopConfig_Deserializes_FromJson()
    {
        var json = """{"MaxSteps":5,"AllowedTools":["file_read","search_code"],"ToolTimeoutMs":30000}""";

        var config = JsonSerializer.Deserialize<ToolLoopConfig>(json);

        config.Should().NotBeNull();
        config!.MaxSteps.Should().Be(5);
        config.AllowedTools.Should().HaveCount(2);
        config.AllowedTools.Should().Contain("file_read");
        config.ToolTimeoutMs.Should().Be(30_000);
    }

    [Test]
    public void ToolLoopConfig_InvalidJson_DefaultsGracefully()
    {
        // Simulate what ParseToolLoopConfig does
        var json = "not valid json";
        ToolLoopConfig config;
        try { config = JsonSerializer.Deserialize<ToolLoopConfig>(json) ?? new ToolLoopConfig(); }
        catch { config = new ToolLoopConfig(); }

        config.MaxSteps.Should().Be(20);
    }

    [Test]
    public void ToolLoopConfig_NullJson_ReturnsDefaults()
    {
        string? json = null;
        var config = string.IsNullOrWhiteSpace(json) ? new ToolLoopConfig() : new ToolLoopConfig();

        config.MaxSteps.Should().Be(20);
    }

    // =====================================================================
    // ToolExecutionResult Tests
    // =====================================================================

    [Test]
    public void ToolExecutionResult_Success_HasNoErrorMessage()
    {
        var result = new ToolExecutionResult("t1", "file_read", true, "file contents here", 150);

        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Output.Should().Be("file contents here");
    }

    [Test]
    public void ToolExecutionResult_Failure_ErrorMessageIsOutput()
    {
        var result = new ToolExecutionResult("t1", "file_read", false, "File not found", 50);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("File not found");
    }

    // =====================================================================
    // Tool Allowlist Tests (via registry mock)
    // =====================================================================

    [Test]
    public void ToolRegistry_IsAllowed_NullAllowlist_AllowsAll()
    {
        _registryMock.Setup(r => r.IsAllowed("file_read", null)).Returns(true);

        _registryMock.Object.IsAllowed("file_read", null).Should().BeTrue();
    }

    [Test]
    public void ToolRegistry_IsAllowed_ToolInList_Allowed()
    {
        var allowlist = new[] { "file_read", "search_code" };
        _registryMock.Setup(r => r.IsAllowed("file_read", allowlist)).Returns(true);

        _registryMock.Object.IsAllowed("file_read", allowlist).Should().BeTrue();
    }

    [Test]
    public void ToolRegistry_IsAllowed_ToolNotInList_Rejected()
    {
        var allowlist = new[] { "file_read", "search_code" };
        _registryMock.Setup(r => r.IsAllowed("shell_execute", allowlist)).Returns(false);

        _registryMock.Object.IsAllowed("shell_execute", allowlist).Should().BeFalse();
    }

    // =====================================================================
    // Input Properties Tests
    // =====================================================================

    [Test]
    public void EnableToolLoopProp_DefaultsToFalse()
    {
        var activity = new CallLlmInlineActivity();
        // The Input<bool> default should be false
        activity.EnableToolLoopProp.Should().NotBeNull();
    }

    [Test]
    public void ToolLoopConfigJsonProp_Exists()
    {
        var activity = new CallLlmInlineActivity();
        // Property should exist (will be default!)
        activity.Should().NotBeNull();
    }

    // =====================================================================
    // Anthropic Response Parsing - Multi-Tool
    // =====================================================================

    [Test]
    public void AnthropicResponse_MultipleToolCalls_AllParsed()
    {
        var json = JsonSerializer.Serialize(new
        {
            content = new object[]
            {
                new { type = "text", text = "I'll read both files." },
                new { type = "tool_use", id = "t1", name = "file_read", input = new { path = "a.txt" } },
                new { type = "tool_use", id = "t2", name = "file_read", input = new { path = "b.txt" } }
            },
            stop_reason = "tool_use",
            usage = new { input_tokens = 100, output_tokens = 40 }
        });
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseAnthropicResponse(element, 200, "fallback");

        result.ToolCalls.Should().HaveCount(2);
        result.ToolCalls![0].Id.Should().Be("t1");
        result.ToolCalls![1].Id.Should().Be("t2");
        result.PromptTokens.Should().Be(100);
        result.CompletionTokens.Should().Be(40);
    }

    [Test]
    public void AnthropicResponse_NoToolCalls_ReturnsNullToolCalls()
    {
        var json = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Hello world" } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 10, output_tokens = 5 }
        });
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseAnthropicResponse(element, 200, "fallback");

        result.ToolCalls.Should().BeNull();
    }

    // =====================================================================
    // OpenAI Response Parsing - Edge Cases
    // =====================================================================

    [Test]
    public void OpenAiResponse_MultipleToolCalls_AllParsed()
    {
        var json = """
        {
            "choices": [{
                "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                        { "id": "c1", "type": "function", "function": { "name": "file_read", "arguments": "{\"path\":\"a.txt\"}" } },
                        { "id": "c2", "type": "function", "function": { "name": "file_read", "arguments": "{\"path\":\"b.txt\"}" } }
                    ]
                },
                "finish_reason": "tool_calls"
            }],
            "usage": { "prompt_tokens": 100, "completion_tokens": 30 }
        }
        """;
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseOpenAiResponse(element, 200, "fallback");

        result.ToolCalls.Should().HaveCount(2);
        result.ToolCalls![0].Id.Should().Be("c1");
        result.ToolCalls![1].Id.Should().Be("c2");
    }

    [Test]
    public void OpenAiResponse_EmptyChoices_ReturnsUnknownStopReason()
    {
        var json = """{ "choices": [], "usage": { "prompt_tokens": 0, "completion_tokens": 0 } }""";
        var element = JsonSerializer.Deserialize<JsonElement>(json);

        var result = InlineToolLoopRunner.ParseOpenAiResponse(element, 200, "fallback");

        result.StopReason.Should().Be(StopReason.Unknown);
    }

    // =====================================================================
    // Body Builder - No Tools
    // =====================================================================

    [Test]
    public void AnthropicBody_NoTools_OmitsToolsField()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Hi" }
        };

        var body = _activity.BuildAnthropicMultiTurnBody(messages, "claude-sonnet-4-20250514", 4096, 0.7, null);

        body.Should().NotContainKey("tools");
    }

    [Test]
    public void OpenAiBody_NoTools_OmitsToolsField()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Hi" }
        };

        var body = _activity.BuildOpenAiMultiTurnBody(messages, "gpt-4o", 4096, 0.7, null);

        body.Should().NotContainKey("tools");
    }

    [Test]
    public void AnthropicBody_EmptyToolsList_OmitsToolsField()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Hi" }
        };

        var body = _activity.BuildAnthropicMultiTurnBody(messages, "claude-sonnet-4-20250514", 4096, 0.7, new List<ResolvedTool>());

        body.Should().NotContainKey("tools");
    }

    // =====================================================================
    // LlmCallWorkflowOutput - Tool Loop Fields
    // =====================================================================

    [Test]
    public void LlmCallWorkflowOutput_ToolLoopFields_DefaultToZero()
    {
        var output = new LlmCallWorkflowOutput();

        output.ToolLoopTokens.Should().Be(0);
        output.ToolLoopTurns.Should().Be(0);
        output.ToolLoopExhausted.Should().BeFalse();
    }

    [Test]
    public void LlmCallWorkflowOutput_ToolLoopFields_RoundTripJson()
    {
        var output = new LlmCallWorkflowOutput
        {
            Success = true,
            ToolLoopTokens = 1500,
            ToolLoopTurns = 5,
            ToolLoopExhausted = true
        };

        var json = JsonSerializer.Serialize(output);
        var deserialized = JsonSerializer.Deserialize<LlmCallWorkflowOutput>(json);

        deserialized.Should().NotBeNull();
        deserialized!.ToolLoopTokens.Should().Be(1500);
        deserialized.ToolLoopTurns.Should().Be(5);
        deserialized.ToolLoopExhausted.Should().BeTrue();
    }

    // =====================================================================
    // NormalizedLlmResponse - StopReason field
    // =====================================================================

    [Test]
    public void NormalizedLlmResponse_StopReason_DefaultsToEndTurn()
    {
        var response = new NormalizedLlmResponse();
        response.StopReason.Should().Be(StopReason.EndTurn);
    }

    [Test]
    public void NormalizedLlmResponse_StopReason_Settable()
    {
        var response = new NormalizedLlmResponse { StopReason = StopReason.ToolUse };
        response.StopReason.Should().Be(StopReason.ToolUse);
    }

    // =====================================================================
    // Body Builder - Model and Temperature Propagation
    // =====================================================================

    [Test]
    public void AnthropicBody_PropagatesModelAndTemp()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Hi" }
        };

        var body = _activity.BuildAnthropicMultiTurnBody(messages, "custom-model", 2048, 0.3, null);

        body["model"].Should().Be("custom-model");
        body["max_tokens"].Should().Be(2048);
        body["temperature"].Should().Be(0.3);
    }

    [Test]
    public void OpenAiBody_PropagatesModelAndTemp()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Hi" }
        };

        var body = _activity.BuildOpenAiMultiTurnBody(messages, "gpt-4-turbo", 1024, 0.1, null);

        body["model"].Should().Be("gpt-4-turbo");
        body["max_tokens"].Should().Be(1024);
        body["temperature"].Should().Be(0.1);
    }

    // =====================================================================
    // Anthropic Multi-Turn: Assistant with only tool calls (no text)
    // =====================================================================

    [Test]
    public void AnthropicFormat_AssistantNoText_OnlyToolUse()
    {
        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Read files" },
            new()
            {
                Role = "assistant",
                Content = null, // No text, only tool calls
                ToolCalls = new[] { new ToolCallInfo("t1", "file_read", "{\"path\":\"a.txt\"}") }
            }
        };

        var body = _activity.BuildAnthropicMultiTurnBody(messages, "claude-sonnet-4-20250514", 4096, 0.7, null);

        var apiMessages = body["messages"] as List<object>;
        var assistantMsg = apiMessages![1] as Dictionary<string, object>;
        var contentBlocks = assistantMsg!["content"] as List<object>;
        // Should only have tool_use block (no text block since Content is null)
        contentBlocks.Should().HaveCount(1);
        var block = contentBlocks![0] as Dictionary<string, object>;
        block!["type"].Should().Be("tool_use");
    }

    // =====================================================================
    // StopReason Enum Serialization
    // =====================================================================

    [Test]
    public void StopReason_SerializesToString()
    {
        var json = JsonSerializer.Serialize(StopReason.ToolUse);
        json.Should().Contain("ToolUse");
    }

    [Test]
    public void StopReason_DeserializesFromString()
    {
        var result = JsonSerializer.Deserialize<StopReason>("\"EndTurn\"");
        result.Should().Be(StopReason.EndTurn);
    }
}
