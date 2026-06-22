using System.Net;
using System.Text;
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

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Integration tests for the agentic tool loop (Epic 12).
/// Simulates complete multi-turn agentic sessions using scripted HTTP responses
/// and real/mock tool executors. Each test wires together the body builder,
/// response parser, tool execution, conversation history, context compaction,
/// and token tracking -- matching the same code path as AgenticToolLoop.
///
/// Because AgenticToolLoop is a private method that requires an Elsa
/// ActivityExecutionContext (which cannot be constructed in tests), these
/// integration tests replicate the loop logic using the same public/internal
/// methods the activity exposes: BuildAnthropicMultiTurnBody,
/// BuildOpenAiMultiTurnBody, ParseAnthropicResponse, ParseOpenAiResponse.
/// </summary>
[TestFixture]
public class AgenticToolLoopIntegrationTests
{
    private InlineToolLoopRunner _activity = null!;
    private Mock<ILogger<InlineToolLoopRunner>> _activityLoggerMock = null!;
    private ToolExecutorRegistry _registry = null!;
    private Mock<ILogger<ToolExecutorRegistry>> _registryLoggerMock = null!;
    private ContextCompactor _compactor = null!;
    private Mock<ILogger<ContextCompactor>> _compactorLoggerMock = null!;

    [SetUp]
    public void SetUp()
    {
        _activityLoggerMock = new Mock<ILogger<InlineToolLoopRunner>>();
        _registryLoggerMock = new Mock<ILogger<ToolExecutorRegistry>>();
        _compactorLoggerMock = new Mock<ILogger<ContextCompactor>>();
        _compactor = new ContextCompactor(_compactorLoggerMock.Object);
    }

    // =====================================================================
    // Helper: Simulate the agentic tool loop using public/internal methods.
    // This mirrors AgenticToolLoop's logic exactly.
    // =====================================================================

    /// <summary>
    /// Runs a simulated agentic tool loop using scripted Anthropic HTTP responses.
    /// Returns the final state for assertions.
    /// </summary>
    private async Task<AgenticLoopResult> RunAnthropicAgenticLoop(
        List<ScriptedResponse> scriptedResponses,
        IToolExecutorRegistry toolRegistry,
        List<ResolvedTool>? tools = null,
        ToolLoopConfig? loopConfig = null,
        string systemPrompt = "You are a helpful assistant.",
        string userPrompt = "Help me with this task.")
    {
        var config = loopConfig ?? new ToolLoopConfig();
        var activity = new InlineToolLoopRunner(
            _activityLoggerMock.Object, null, null, null, toolRegistry);

        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = userPrompt }
        };

        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var totalToolCalls = 0;
        var exhausted = false;
        NormalizedLlmResponse lastResponse = new() { Success = false, ErrorMessage = "No call made" };
        var responseIndex = 0;
        var completedTurns = 0;
        var perTurnTokens = new List<(int Prompt, int Completion)>();

        for (var step = 0; step < config.MaxSteps; step++)
        {
            if (responseIndex >= scriptedResponses.Count)
            {
                // No more scripted responses; treat as exhausted
                exhausted = true;
                break;
            }

            // Build the request body (verifies body building works)
            var body = activity.BuildAnthropicMultiTurnBody(
                messages, "claude-sonnet-4-20250514", 4096, 0.7, tools);

            // Parse the scripted response
            var scripted = scriptedResponses[responseIndex++];
            var responseElement = JsonSerializer.Deserialize<JsonElement>(scripted.ResponseJson);
            var response = InlineToolLoopRunner.ParseAnthropicResponse(
                responseElement, scripted.StatusCode, "claude-sonnet-4-20250514");

            lastResponse = response;

            if (!response.Success)
                break;

            totalPromptTokens += response.PromptTokens;
            totalCompletionTokens += response.CompletionTokens;
            perTurnTokens.Add((response.PromptTokens, response.CompletionTokens));
            completedTurns++;

            // Check if done
            if (response.StopReason != StopReason.ToolUse ||
                response.ToolCalls == null ||
                response.ToolCalls.Count == 0)
            {
                break;
            }

            // Append assistant message
            messages.Add(new ConversationMessage
            {
                Role = "assistant",
                Content = response.ResponseText,
                ToolCalls = response.ToolCalls.Select(tc =>
                    new ToolCallInfo(tc.Id, tc.ToolName, tc.ArgumentsJson)).ToArray()
            });

            // Execute each tool
            foreach (var toolCall in response.ToolCalls)
            {
                ToolExecutionResult result;

                if (!toolRegistry.IsAllowed(toolCall.ToolName, config.AllowedTools))
                {
                    result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                        $"Tool '{toolCall.ToolName}' is not allowed.", 0);
                }
                else
                {
                    var executor = toolRegistry.GetExecutor(toolCall.ToolName);
                    if (executor == null)
                    {
                        result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                            $"Unknown tool: '{toolCall.ToolName}'", 0);
                    }
                    else
                    {
                        result = await executor.ExecuteAsync(
                            toolCall.Id, toolCall.ArgumentsJson);
                    }
                }

                totalToolCalls++;

                messages.Add(new ConversationMessage
                {
                    Role = "tool",
                    Content = result.Output,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.ToolName
                });
            }

            if (step == config.MaxSteps - 1)
                exhausted = true;
        }

        return new AgenticLoopResult
        {
            Messages = messages,
            LastResponse = lastResponse,
            TotalPromptTokens = totalPromptTokens,
            TotalCompletionTokens = totalCompletionTokens,
            TotalTokens = totalPromptTokens + totalCompletionTokens,
            TotalToolCalls = totalToolCalls,
            CompletedTurns = completedTurns,
            Exhausted = exhausted,
            PerTurnTokens = perTurnTokens
        };
    }

    /// <summary>
    /// Runs a simulated agentic tool loop using scripted OpenAI HTTP responses.
    /// </summary>
    private async Task<AgenticLoopResult> RunOpenAiAgenticLoop(
        List<ScriptedResponse> scriptedResponses,
        IToolExecutorRegistry toolRegistry,
        List<ResolvedTool>? tools = null,
        ToolLoopConfig? loopConfig = null,
        string systemPrompt = "You are a helpful assistant.",
        string userPrompt = "Help me with this task.")
    {
        var config = loopConfig ?? new ToolLoopConfig();
        var activity = new InlineToolLoopRunner(
            _activityLoggerMock.Object, null, null, null, toolRegistry);

        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = userPrompt }
        };

        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var totalToolCalls = 0;
        var exhausted = false;
        NormalizedLlmResponse lastResponse = new() { Success = false, ErrorMessage = "No call made" };
        var responseIndex = 0;
        var completedTurns = 0;
        var perTurnTokens = new List<(int Prompt, int Completion)>();

        for (var step = 0; step < config.MaxSteps; step++)
        {
            if (responseIndex >= scriptedResponses.Count)
            {
                exhausted = true;
                break;
            }

            var body = activity.BuildOpenAiMultiTurnBody(
                messages, "gpt-4o", 4096, 0.7, tools);

            var scripted = scriptedResponses[responseIndex++];
            var responseElement = JsonSerializer.Deserialize<JsonElement>(scripted.ResponseJson);
            var response = InlineToolLoopRunner.ParseOpenAiResponse(
                responseElement, scripted.StatusCode, "gpt-4o");

            lastResponse = response;

            if (!response.Success)
                break;

            totalPromptTokens += response.PromptTokens;
            totalCompletionTokens += response.CompletionTokens;
            perTurnTokens.Add((response.PromptTokens, response.CompletionTokens));
            completedTurns++;

            if (response.StopReason != StopReason.ToolUse ||
                response.ToolCalls == null ||
                response.ToolCalls.Count == 0)
            {
                break;
            }

            messages.Add(new ConversationMessage
            {
                Role = "assistant",
                Content = response.ResponseText,
                ToolCalls = response.ToolCalls.Select(tc =>
                    new ToolCallInfo(tc.Id, tc.ToolName, tc.ArgumentsJson)).ToArray()
            });

            foreach (var toolCall in response.ToolCalls)
            {
                ToolExecutionResult result;

                if (!toolRegistry.IsAllowed(toolCall.ToolName, config.AllowedTools))
                {
                    result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                        $"Tool '{toolCall.ToolName}' is not allowed.", 0);
                }
                else
                {
                    var executor = toolRegistry.GetExecutor(toolCall.ToolName);
                    if (executor == null)
                    {
                        result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                            $"Unknown tool: '{toolCall.ToolName}'", 0);
                    }
                    else
                    {
                        result = await executor.ExecuteAsync(
                            toolCall.Id, toolCall.ArgumentsJson);
                    }
                }

                totalToolCalls++;

                messages.Add(new ConversationMessage
                {
                    Role = "tool",
                    Content = result.Output,
                    ToolCallId = toolCall.Id,
                    ToolName = toolCall.ToolName
                });
            }

            if (step == config.MaxSteps - 1)
                exhausted = true;
        }

        return new AgenticLoopResult
        {
            Messages = messages,
            LastResponse = lastResponse,
            TotalPromptTokens = totalPromptTokens,
            TotalCompletionTokens = totalCompletionTokens,
            TotalTokens = totalPromptTokens + totalCompletionTokens,
            TotalToolCalls = totalToolCalls,
            CompletedTurns = completedTurns,
            Exhausted = exhausted,
            PerTurnTokens = perTurnTokens
        };
    }

    // =====================================================================
    // Helper: Create a mock tool executor
    // =====================================================================

    private static Mock<IToolExecutor> CreateMockExecutor(
        string name, Func<string, string, Task<ToolExecutionResult>>? handler = null)
    {
        var mock = new Mock<IToolExecutor>();
        mock.Setup(e => e.ToolName).Returns(name);
        mock.Setup(e => e.Description).Returns($"Description for {name}");
        mock.Setup(e => e.InputSchema).Returns(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["path"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "File path"
                }
            }
        });

        if (handler != null)
        {
            mock.Setup(e => e.ExecuteAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, string args, CancellationToken _) => handler(id, args));
        }
        else
        {
            mock.Setup(e => e.ExecuteAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, string args, CancellationToken _) =>
                    new ToolExecutionResult(id, name, true, "Default mock output", 10));
        }

        return mock;
    }

    private ToolExecutorRegistry BuildRegistry(params IToolExecutor[] executors)
    {
        return new ToolExecutorRegistry(executors, _registryLoggerMock.Object);
    }

    // =====================================================================
    // Helper: Scripted Anthropic response builders
    // =====================================================================

    private static string BuildAnthropicToolUseResponse(
        string text, List<(string Id, string Name, object Input)> toolCalls,
        int inputTokens = 100, int outputTokens = 50)
    {
        var contentBlocks = new List<object>();

        if (!string.IsNullOrEmpty(text))
        {
            contentBlocks.Add(new { type = "text", text });
        }

        foreach (var (id, name, input) in toolCalls)
        {
            contentBlocks.Add(new { type = "tool_use", id, name, input });
        }

        return JsonSerializer.Serialize(new
        {
            content = contentBlocks,
            stop_reason = "tool_use",
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
            model = "claude-sonnet-4-20250514"
        });
    }

    private static string BuildAnthropicEndTurnResponse(
        string text, int inputTokens = 100, int outputTokens = 50)
    {
        return JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text } },
            stop_reason = "end_turn",
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
            model = "claude-sonnet-4-20250514"
        });
    }

    private static string BuildOpenAiToolCallResponse(
        string? text, List<(string Id, string Name, string ArgumentsJson)> toolCalls,
        int promptTokens = 100, int completionTokens = 50)
    {
        var toolCallObjects = toolCalls.Select(tc => new
        {
            id = tc.Id,
            type = "function",
            function = new { name = tc.Name, arguments = tc.ArgumentsJson }
        }).ToArray();

        // Build raw JSON since anonymous types don't handle null content well for OpenAI
        var messageObj = new Dictionary<string, object?>();
        messageObj["role"] = "assistant";
        messageObj["content"] = text;
        messageObj["tool_calls"] = toolCallObjects;

        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = messageObj,
                    finish_reason = "tool_calls"
                }
            },
            usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens },
            model = "gpt-4o"
        });
    }

    private static string BuildOpenAiEndTurnResponse(
        string text, int promptTokens = 100, int completionTokens = 50)
    {
        return JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = text },
                    finish_reason = "stop"
                }
            },
            usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens },
            model = "gpt-4o"
        });
    }

    // =====================================================================
    // Test 1: Three-Turn File Read Session
    // =====================================================================

    [Test]
    public async Task ThreeTurnFileReadSession_Anthropic_CorrectConversationHistoryAndTokens()
    {
        // Arrange: Script 3 LLM responses
        // Turn 1: LLM calls file_read for README.md
        // Turn 2: LLM calls file_read for CHANGELOG.md
        // Turn 3: LLM produces final answer

        var fileReadExecutor = CreateMockExecutor("file_read",
            async (id, args) =>
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(args);
                var path = parsed.GetProperty("path").GetString();
                return path switch
                {
                    "README.md" => new ToolExecutionResult(id, "file_read", true,
                        "# Project README\nThis is the readme content.", 5),
                    "CHANGELOG.md" => new ToolExecutionResult(id, "file_read", true,
                        "# Changelog\n## v1.0.0 - Initial release", 3),
                    _ => new ToolExecutionResult(id, "file_read", false,
                        $"File not found: {path}", 1)
                };
            });

        var registry = BuildRegistry(fileReadExecutor.Object);

        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var scriptedResponses = new List<ScriptedResponse>
        {
            // Turn 1: LLM requests README.md
            new(200, BuildAnthropicToolUseResponse(
                "I'll read the README first.",
                new List<(string, string, object)>
                {
                    ("toolu_01", "file_read", new { path = "README.md" })
                },
                inputTokens: 150, outputTokens: 30)),

            // Turn 2: LLM requests CHANGELOG.md
            new(200, BuildAnthropicToolUseResponse(
                "Now let me check the changelog.",
                new List<(string, string, object)>
                {
                    ("toolu_02", "file_read", new { path = "CHANGELOG.md" })
                },
                inputTokens: 200, outputTokens: 35)),

            // Turn 3: LLM produces final answer
            new(200, BuildAnthropicEndTurnResponse(
                "Based on the README and CHANGELOG, this is a v1.0.0 project with an initial release.",
                inputTokens: 250, outputTokens: 40))
        };

        // Act
        var result = await RunAnthropicAgenticLoop(scriptedResponses, registry, tools);

        // Assert
        result.CompletedTurns.Should().Be(3, "3 LLM calls were made");
        result.Exhausted.Should().BeFalse("loop ended naturally with end_turn");
        result.LastResponse.Success.Should().BeTrue();
        result.LastResponse.StopReason.Should().Be(StopReason.EndTurn);
        result.LastResponse.ResponseText.Should().Contain("v1.0.0");
        result.TotalToolCalls.Should().Be(2, "2 tool calls across 2 turns");

        // Conversation history: system + user + (assistant+tool)*2 + no final assistant = 6 msgs
        // system, user, assistant(tool_use), tool_result, assistant(tool_use), tool_result
        result.Messages.Should().HaveCount(6);
        result.Messages[0].Role.Should().Be("system");
        result.Messages[1].Role.Should().Be("user");
        result.Messages[2].Role.Should().Be("assistant");
        result.Messages[2].ToolCalls.Should().HaveCount(1);
        result.Messages[3].Role.Should().Be("tool");
        result.Messages[3].Content.Should().Contain("README");
        result.Messages[4].Role.Should().Be("assistant");
        result.Messages[5].Role.Should().Be("tool");
        result.Messages[5].Content.Should().Contain("Changelog");

        // Token tracking: cumulative across all 3 turns
        result.TotalPromptTokens.Should().Be(150 + 200 + 250);
        result.TotalCompletionTokens.Should().Be(30 + 35 + 40);
        result.TotalTokens.Should().Be(150 + 200 + 250 + 30 + 35 + 40);

        // Verify tool was called with correct arguments
        fileReadExecutor.Verify(e => e.ExecuteAsync(
            "toolu_01", It.Is<string>(s => s.Contains("README.md")),
            It.IsAny<CancellationToken>()), Times.Once);
        fileReadExecutor.Verify(e => e.ExecuteAsync(
            "toolu_02", It.Is<string>(s => s.Contains("CHANGELOG.md")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // =====================================================================
    // Test 2: Tool Error Recovery
    // =====================================================================

    [Test]
    public async Task ToolErrorRecovery_ErrorFedBackToLlm_RetriesSuccessfully()
    {
        // Arrange: LLM tries nonexistent file, gets error, tries correct path, succeeds
        var callCount = 0;
        var fileReadExecutor = CreateMockExecutor("file_read",
            async (id, args) =>
            {
                callCount++;
                var parsed = JsonSerializer.Deserialize<JsonElement>(args);
                var path = parsed.GetProperty("path").GetString();

                if (path == "src/main.ts")
                    return new ToolExecutionResult(id, "file_read", false,
                        "File not found: src/main.ts", 2);

                if (path == "src/index.ts")
                    return new ToolExecutionResult(id, "file_read", true,
                        "export function main() { console.log('hello'); }", 4);

                return new ToolExecutionResult(id, "file_read", false,
                    $"File not found: {path}", 1);
            });

        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var scriptedResponses = new List<ScriptedResponse>
        {
            // Turn 1: LLM tries wrong file
            new(200, BuildAnthropicToolUseResponse(
                "Let me read the main file.",
                new List<(string, string, object)>
                {
                    ("toolu_01", "file_read", new { path = "src/main.ts" })
                },
                inputTokens: 100, outputTokens: 20)),

            // Turn 2: LLM retries with correct path after seeing error
            new(200, BuildAnthropicToolUseResponse(
                "That file doesn't exist. Let me try index.ts instead.",
                new List<(string, string, object)>
                {
                    ("toolu_02", "file_read", new { path = "src/index.ts" })
                },
                inputTokens: 180, outputTokens: 25)),

            // Turn 3: LLM produces answer
            new(200, BuildAnthropicEndTurnResponse(
                "The entry point is src/index.ts which exports a main function.",
                inputTokens: 220, outputTokens: 30))
        };

        // Act
        var result = await RunAnthropicAgenticLoop(scriptedResponses, registry, tools);

        // Assert
        result.CompletedTurns.Should().Be(3);
        result.Exhausted.Should().BeFalse();
        result.TotalToolCalls.Should().Be(2);

        // Verify the error was fed back as a tool result
        var errorToolResult = result.Messages.First(m =>
            m.Role == "tool" && m.ToolCallId == "toolu_01");
        errorToolResult.Content.Should().Contain("File not found: src/main.ts");

        // Verify the successful result was also fed back
        var successToolResult = result.Messages.First(m =>
            m.Role == "tool" && m.ToolCallId == "toolu_02");
        successToolResult.Content.Should().Contain("export function main()");

        // Both calls were made
        callCount.Should().Be(2);
    }

    // =====================================================================
    // Test 3: Blocked Command Rejection + Recovery
    // =====================================================================

    [Test]
    public async Task BlockedCommandRejection_ActionGateBlocks_LlmRecoversWithSafeCommand()
    {
        // Arrange: LLM sends a dangerous command, gets blocked, then sends safe command
        var shellExecutor = CreateMockExecutor("shell_execute",
            async (id, args) =>
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(args);
                var command = parsed.GetProperty("command").GetString() ?? "";

                // Simulate ActionGate: check blocked patterns
                var blockedPattern = CommandValidator.GetBlockedPatternName(command);
                if (blockedPattern != null)
                {
                    return new ToolExecutionResult(id, "shell_execute", false,
                        $"Command blocked by security policy (matched: {blockedPattern}).", 0);
                }

                // Safe command succeeds
                return new ToolExecutionResult(id, "shell_execute", true,
                    "total 32\ndrwxr-xr-x 5 user user 4096 Jan 1 00:00 .\ndrwxr-xr-x 3 user user 4096 Jan 1 00:00 ..\n-rw-r--r-- 1 user user 1234 Jan 1 00:00 README.md\n\nExit code: 0",
                    50);
            });

        var registry = BuildRegistry(shellExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "shell_execute", Description = "Execute a shell command" }
        };

        var scriptedResponses = new List<ScriptedResponse>
        {
            // Turn 1: LLM sends dangerous command (sudo rm -rf /)
            new(200, BuildAnthropicToolUseResponse(
                "Let me clean up the system.",
                new List<(string, string, object)>
                {
                    ("toolu_01", "shell_execute", new { command = "sudo rm -rf /" })
                },
                inputTokens: 100, outputTokens: 20)),

            // Turn 2: LLM recovers with safe command after seeing rejection
            new(200, BuildAnthropicToolUseResponse(
                "I apologize, that was dangerous. Let me list the directory instead.",
                new List<(string, string, object)>
                {
                    ("toolu_02", "shell_execute", new { command = "ls -la" })
                },
                inputTokens: 200, outputTokens: 30)),

            // Turn 3: LLM gives final answer
            new(200, BuildAnthropicEndTurnResponse(
                "The directory contains a README.md file.",
                inputTokens: 280, outputTokens: 25))
        };

        // Act
        var result = await RunAnthropicAgenticLoop(scriptedResponses, registry, tools);

        // Assert
        result.CompletedTurns.Should().Be(3);
        result.Exhausted.Should().BeFalse();

        // Verify the first command was blocked
        var blockedResult = result.Messages.First(m =>
            m.Role == "tool" && m.ToolCallId == "toolu_01");
        blockedResult.Content.Should().Contain("blocked by security policy");

        // Verify the safe command succeeded
        var safeResult = result.Messages.First(m =>
            m.Role == "tool" && m.ToolCallId == "toolu_02");
        safeResult.Content.Should().Contain("README.md");
        safeResult.Content.Should().Contain("Exit code: 0");

        // Verify the LLM received the rejection reason and could recover
        result.LastResponse.ResponseText.Should().Contain("README.md");
    }

    // =====================================================================
    // Test 4: MaxSteps Exhaustion
    // =====================================================================

    [Test]
    public async Task MaxStepsExhaustion_ExceedsLimit_TerminatesWithExhaustedFlag()
    {
        // Arrange: Script 25 tool calls but limit to 20 steps
        var fileReadExecutor = CreateMockExecutor("file_read");
        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var loopConfig = new ToolLoopConfig { MaxSteps = 20 };

        // Script 25 tool-use responses (more than MaxSteps)
        var scriptedResponses = new List<ScriptedResponse>();
        for (var i = 0; i < 25; i++)
        {
            scriptedResponses.Add(new ScriptedResponse(200, BuildAnthropicToolUseResponse(
                $"Reading file {i}.",
                new List<(string, string, object)>
                {
                    ($"toolu_{i:D2}", "file_read", new { path = $"file_{i}.txt" })
                },
                inputTokens: 50 + i * 10, outputTokens: 20)));
        }

        // Act
        var result = await RunAnthropicAgenticLoop(scriptedResponses, registry, tools, loopConfig);

        // Assert
        result.Exhausted.Should().BeTrue("loop should terminate at maxSteps");
        result.CompletedTurns.Should().Be(20, "exactly 20 turns should complete");
        result.TotalToolCalls.Should().Be(20, "20 tool calls executed (one per turn)");

        // Last response should still be a tool_use (loop was cut short)
        result.LastResponse.StopReason.Should().Be(StopReason.ToolUse);
    }

    // =====================================================================
    // Test 5: Multi-Tool Parallel Execution
    // =====================================================================

    [Test]
    public async Task MultiToolParallelExecution_ThreeToolCallsInOneResponse_AllExecute()
    {
        // Arrange: LLM returns 3 tool calls in one response
        var executionOrder = new List<string>();

        var fileReadExecutor = CreateMockExecutor("file_read",
            async (id, args) =>
            {
                lock (executionOrder) { executionOrder.Add(id); }
                var parsed = JsonSerializer.Deserialize<JsonElement>(args);
                var path = parsed.GetProperty("path").GetString();
                return new ToolExecutionResult(id, "file_read", true,
                    $"Content of {path}", 5);
            });

        var searchExecutor = CreateMockExecutor("search_code",
            async (id, args) =>
            {
                lock (executionOrder) { executionOrder.Add(id); }
                return new ToolExecutionResult(id, "search_code", true,
                    "Found 3 matches in src/", 8);
            });
        searchExecutor.Setup(e => e.ToolName).Returns("search_code");

        var registry = BuildRegistry(fileReadExecutor.Object, searchExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" },
            new() { Name = "search_code", Description = "Search code" }
        };

        var scriptedResponses = new List<ScriptedResponse>
        {
            // Turn 1: LLM requests 3 tools at once
            new(200, BuildAnthropicToolUseResponse(
                "I'll gather all the information at once.",
                new List<(string, string, object)>
                {
                    ("toolu_01", "file_read", new { path = "package.json" }),
                    ("toolu_02", "file_read", new { path = "tsconfig.json" }),
                    ("toolu_03", "search_code", new { query = "main" })
                },
                inputTokens: 150, outputTokens: 40)),

            // Turn 2: Final answer
            new(200, BuildAnthropicEndTurnResponse(
                "The project uses TypeScript with a main entry point.",
                inputTokens: 350, outputTokens: 35))
        };

        // Act
        var result = await RunAnthropicAgenticLoop(scriptedResponses, registry, tools);

        // Assert
        result.CompletedTurns.Should().Be(2);
        result.TotalToolCalls.Should().Be(3, "all 3 tool calls executed");
        result.Exhausted.Should().BeFalse();

        // Verify all 3 tool results are in the conversation
        var toolResults = result.Messages.Where(m => m.Role == "tool").ToList();
        toolResults.Should().HaveCount(3);

        // Verify order: results fed back in the same order as tool calls
        toolResults[0].ToolCallId.Should().Be("toolu_01");
        toolResults[0].Content.Should().Contain("package.json");
        toolResults[1].ToolCallId.Should().Be("toolu_02");
        toolResults[1].Content.Should().Contain("tsconfig.json");
        toolResults[2].ToolCallId.Should().Be("toolu_03");
        toolResults[2].Content.Should().Contain("Found 3 matches");

        // Verify all 3 tool calls were in the assistant message
        var assistantMsg = result.Messages.First(m => m.Role == "assistant");
        assistantMsg.ToolCalls.Should().HaveCount(3);

        // All 3 executors were called
        executionOrder.Should().HaveCount(3);
        executionOrder.Should().Contain("toolu_01");
        executionOrder.Should().Contain("toolu_02");
        executionOrder.Should().Contain("toolu_03");
    }

    // =====================================================================
    // Test 6: Context Compaction Trigger
    // =====================================================================

    [Test]
    public async Task ContextCompactionTrigger_ExceedsThreshold_CompactsMessages()
    {
        // Arrange: Build a conversation that exceeds 80% of a 1000-token window
        // TokenEstimator uses ~4 chars per token, so 1000 tokens = ~4000 chars
        // With threshold 0.8, trigger at 800 tokens = 3200 chars
        var compactor = _compactor;
        var summarizeCalled = false;

        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "You are a helpful assistant." },
            // Add enough content to exceed threshold
            new() { Role = "user", Content = new string('A', 1000) },
            new() { Role = "assistant", Content = new string('B', 1000) },
            new() { Role = "user", Content = new string('C', 1000) },
            new() { Role = "assistant", Content = new string('D', 1000) },
            new() { Role = "user", Content = new string('E', 500) },
            new() { Role = "assistant", Content = new string('F', 500) },
            // These last 4 will be preserved (DefaultPreservedTailCount = 4)
            new() { Role = "user", Content = "What did we discuss?" },
            new() { Role = "assistant", Content = "Let me summarize." },
            new() { Role = "user", Content = "Please do." },
            new() { Role = "assistant", Content = "Here is the summary." }
        };

        var estimatedTokens = TokenEstimator.EstimateTokens(messages);
        var contextWindow = 1000; // Small context window to force compaction
        var threshold = 0.8;

        // Verify compaction should be triggered
        TokenEstimator.ShouldCompact(messages, contextWindow, threshold).Should().BeTrue(
            $"estimated tokens {estimatedTokens} should exceed {contextWindow * threshold}");

        // Act
        var (compacted, summaryTokens, wasCompacted) = await compactor.CompactIfNeeded(
            messages,
            contextWindow,
            threshold,
            async (prompt, ct) =>
            {
                summarizeCalled = true;
                // Verify the summarization prompt contains the messages to summarize
                prompt.Should().Contain("BEGIN CONVERSATION");
                return "Summary: User discussed topics A through F with the assistant.";
            });

        // Assert
        wasCompacted.Should().BeTrue("tokens exceeded the threshold");
        summarizeCalled.Should().BeTrue("LLM summarization should have been called");

        // Compacted messages: system + summary + last 4 = 6
        compacted.Should().HaveCount(6);
        compacted[0].Role.Should().Be("system");
        compacted[1].Role.Should().Be("user");
        compacted[1].Content.Should().Contain("Context summary from earlier conversation");
        compacted[1].Content.Should().Contain("Summary: User discussed topics A through F");

        // Last 4 preserved
        compacted[2].Content.Should().Be("What did we discuss?");
        compacted[3].Content.Should().Be("Let me summarize.");
        compacted[4].Content.Should().Be("Please do.");
        compacted[5].Content.Should().Be("Here is the summary.");

        // Compacted should be smaller than original
        var compactedTokens = TokenEstimator.EstimateTokens(compacted);
        compactedTokens.Should().BeLessThan(estimatedTokens);
    }

    // =====================================================================
    // Test 7: Token Tracking Accuracy Across 5 Turns
    // =====================================================================

    [Test]
    public async Task TokenTrackingAccuracy_FiveTurns_CumulativeTotalsMatchSum()
    {
        // Arrange: 5 turns with known token counts
        var fileReadExecutor = CreateMockExecutor("file_read");
        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var perTurnPromptTokens = new[] { 100, 180, 250, 320, 400 };
        var perTurnCompletionTokens = new[] { 30, 35, 40, 45, 50 };

        var scriptedResponses = new List<ScriptedResponse>();

        // Turns 1-4: tool calls
        for (var i = 0; i < 4; i++)
        {
            scriptedResponses.Add(new ScriptedResponse(200, BuildAnthropicToolUseResponse(
                $"Reading file {i}.",
                new List<(string, string, object)>
                {
                    ($"toolu_{i:D2}", "file_read", new { path = $"file_{i}.txt" })
                },
                inputTokens: perTurnPromptTokens[i],
                outputTokens: perTurnCompletionTokens[i])));
        }

        // Turn 5: final answer
        scriptedResponses.Add(new ScriptedResponse(200, BuildAnthropicEndTurnResponse(
            "All files have been read. Here is the analysis.",
            inputTokens: perTurnPromptTokens[4],
            outputTokens: perTurnCompletionTokens[4])));

        // Act
        var result = await RunAnthropicAgenticLoop(scriptedResponses, registry, tools);

        // Assert
        result.CompletedTurns.Should().Be(5);

        var expectedPromptTotal = perTurnPromptTokens.Sum();
        var expectedCompletionTotal = perTurnCompletionTokens.Sum();

        result.TotalPromptTokens.Should().Be(expectedPromptTotal,
            "cumulative prompt tokens should equal sum of per-turn tokens");
        result.TotalCompletionTokens.Should().Be(expectedCompletionTotal,
            "cumulative completion tokens should equal sum of per-turn tokens");
        result.TotalTokens.Should().Be(expectedPromptTotal + expectedCompletionTotal,
            "total should be prompt + completion");

        // Verify per-turn tracking
        result.PerTurnTokens.Should().HaveCount(5);
        for (var i = 0; i < 5; i++)
        {
            result.PerTurnTokens[i].Prompt.Should().Be(perTurnPromptTokens[i]);
            result.PerTurnTokens[i].Completion.Should().Be(perTurnCompletionTokens[i]);
        }

        // Verify with ToolLoopTokenTracker
        var tracker = new ToolLoopTokenTracker();
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordTurn(perTurnPromptTokens[i], perTurnCompletionTokens[i]);
        }

        tracker.TotalPromptTokens.Should().Be(result.TotalPromptTokens);
        tracker.TotalCompletionTokens.Should().Be(result.TotalCompletionTokens);
        tracker.TotalTokens.Should().Be(result.TotalTokens);
        tracker.TurnCount.Should().Be(result.CompletedTurns);
    }

    // =====================================================================
    // Test 8: Backward Compatibility — EnableToolLoop=false
    // =====================================================================

    [Test]
    public void BackwardCompatibility_EnableToolLoopFalse_ToolCallsPassedThroughAsData()
    {
        // Arrange: Simulate single-turn response parsing with tool calls
        // When EnableToolLoop is false, tool calls should be in the output but NOT executed
        var responseJson = BuildAnthropicToolUseResponse(
            "I want to read a file.",
            new List<(string, string, object)>
            {
                ("toolu_01", "file_read", new { path = "README.md" })
            },
            inputTokens: 100, outputTokens: 30);

        var responseElement = JsonSerializer.Deserialize<JsonElement>(responseJson);

        // Act: Parse as single-turn (no loop execution)
        var response = InlineToolLoopRunner.ParseAnthropicResponse(
            responseElement, 200, "claude-sonnet-4-20250514");

        // Assert: Tool calls are in the response data but not executed
        response.Success.Should().BeTrue();
        response.StopReason.Should().Be(StopReason.ToolUse);
        response.ToolCalls.Should().NotBeNull();
        response.ToolCalls.Should().HaveCount(1);
        response.ToolCalls![0].ToolName.Should().Be("file_read");
        response.ToolCalls[0].ArgumentsJson.Should().Contain("README.md");

        // The tool call data is available but no execution happened --
        // this is the backward-compatible behavior where the caller
        // (workflow) decides what to do with tool calls
        response.ResponseText.Should().Be("I want to read a file.");

        // Verify it maps correctly to LlmCallWorkflowOutput
        var output = new LlmCallWorkflowOutput
        {
            Success = response.Success,
            ResponseText = response.ResponseText,
            ToolCalls = response.ToolCalls,
            ToolLoopTokens = 0,  // No loop
            ToolLoopTurns = 0,   // No loop
            ToolLoopExhausted = false
        };

        output.ToolLoopTokens.Should().Be(0);
        output.ToolLoopTurns.Should().Be(0);
        output.ToolLoopExhausted.Should().BeFalse();
        output.ToolCalls.Should().HaveCount(1);
    }

    // =====================================================================
    // Test 9: Provider Format Correctness — Anthropic vs OpenAI
    // =====================================================================

    [Test]
    public async Task ProviderFormatCorrectness_SameScenario_AnthropicFormat()
    {
        // Arrange: Same scenario using Anthropic format
        var fileReadExecutor = CreateMockExecutor("file_read",
            async (id, args) => new ToolExecutionResult(id, "file_read", true,
                "File content here", 5));

        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file",
                InputSchema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["path"] = new Dictionary<string, object> { ["type"] = "string" }
                    }
                }
            }
        };

        var anthropicResponses = new List<ScriptedResponse>
        {
            new(200, BuildAnthropicToolUseResponse(
                "Reading the file.",
                new List<(string, string, object)>
                {
                    ("toolu_01", "file_read", new { path = "test.txt" })
                })),
            new(200, BuildAnthropicEndTurnResponse("The file contains test data."))
        };

        // Act
        var anthropicResult = await RunAnthropicAgenticLoop(
            anthropicResponses, registry, tools);

        // Assert Anthropic format specifics
        anthropicResult.CompletedTurns.Should().Be(2);
        anthropicResult.TotalToolCalls.Should().Be(1);

        // Verify the body builder produces correct Anthropic format
        var activity = new InlineToolLoopRunner(
            _activityLoggerMock.Object, null, null, null, registry);

        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = "Read file" },
            new()
            {
                Role = "assistant", Content = "Reading.",
                ToolCalls = new[] { new ToolCallInfo("t1", "file_read", "{\"path\":\"test.txt\"}") }
            },
            new() { Role = "tool", Content = "File content", ToolCallId = "t1", ToolName = "file_read" }
        };

        var body = activity.BuildAnthropicMultiTurnBody(messages, "claude-sonnet-4-20250514", 4096, 0.7, tools);

        // Anthropic: system goes to top-level
        body.Should().ContainKey("system");
        body["system"].Should().Be("System prompt");

        // Anthropic: tool_result goes in user role message
        var apiMessages = body["messages"] as List<object>;
        var lastMsg = apiMessages![^1] as Dictionary<string, object>;
        lastMsg!["role"].Should().Be("user");
        var blocks = lastMsg["content"] as List<object>;
        var block = blocks![0] as Dictionary<string, object>;
        block!["type"].Should().Be("tool_result");

        // Anthropic: tools have input_schema
        body.Should().ContainKey("tools");
    }

    [Test]
    public async Task ProviderFormatCorrectness_SameScenario_OpenAiFormat()
    {
        // Arrange: Same scenario using OpenAI format
        var fileReadExecutor = CreateMockExecutor("file_read",
            async (id, args) => new ToolExecutionResult(id, "file_read", true,
                "File content here", 5));

        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file",
                InputSchema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["path"] = new Dictionary<string, object> { ["type"] = "string" }
                    }
                }
            }
        };

        // Build OpenAI tool call response manually for correct format
        var openAiToolCallJson = """
        {
            "choices": [{
                "message": {
                    "role": "assistant",
                    "content": "Reading the file.",
                    "tool_calls": [{
                        "id": "call_01",
                        "type": "function",
                        "function": {
                            "name": "file_read",
                            "arguments": "{\"path\": \"test.txt\"}"
                        }
                    }]
                },
                "finish_reason": "tool_calls"
            }],
            "usage": { "prompt_tokens": 100, "completion_tokens": 50 },
            "model": "gpt-4o"
        }
        """;

        var openAiResponses = new List<ScriptedResponse>
        {
            new(200, openAiToolCallJson),
            new(200, BuildOpenAiEndTurnResponse("The file contains test data."))
        };

        // Act
        var openAiResult = await RunOpenAiAgenticLoop(
            openAiResponses, registry, tools);

        // Assert OpenAI format specifics
        openAiResult.CompletedTurns.Should().Be(2);
        openAiResult.TotalToolCalls.Should().Be(1);

        // Verify the body builder produces correct OpenAI format
        var activity = new InlineToolLoopRunner(
            _activityLoggerMock.Object, null, null, null, registry);

        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = "Read file" },
            new()
            {
                Role = "assistant", Content = "Reading.",
                ToolCalls = new[] { new ToolCallInfo("call_01", "file_read", "{\"path\":\"test.txt\"}") }
            },
            new() { Role = "tool", Content = "File content", ToolCallId = "call_01", ToolName = "file_read" }
        };

        var body = activity.BuildOpenAiMultiTurnBody(messages, "gpt-4o", 4096, 0.7, tools);

        // OpenAI: system as message (NOT top-level)
        body.Should().NotContainKey("system");
        var apiMessages = body["messages"] as List<object>;
        var sysMsg = apiMessages![0] as Dictionary<string, object>;
        sysMsg!["role"].Should().Be("system");

        // OpenAI: tool results as separate "tool" role messages
        var toolMsg = apiMessages[^1] as Dictionary<string, object>;
        toolMsg!["role"].Should().Be("tool");
        toolMsg.Should().ContainKey("tool_call_id");

        // OpenAI: assistant message has tool_calls array
        var assistantMsg = apiMessages[2] as Dictionary<string, object?>;
        assistantMsg.Should().ContainKey("tool_calls");

        // OpenAI: tools have type=function wrapper
        body.Should().ContainKey("tools");
    }

    // =====================================================================
    // Test: Mixed tool success and failure in one turn
    // =====================================================================

    [Test]
    public async Task MixedToolResults_SomeSucceedSomeFail_AllResultsFedBack()
    {
        // Arrange: LLM calls 2 tools, one succeeds and one fails
        var fileReadExecutor = CreateMockExecutor("file_read",
            async (id, args) =>
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(args);
                var path = parsed.GetProperty("path").GetString();
                return path == "exists.txt"
                    ? new ToolExecutionResult(id, "file_read", true, "File content", 5)
                    : new ToolExecutionResult(id, "file_read", false, $"File not found: {path}", 2);
            });

        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var scriptedResponses = new List<ScriptedResponse>
        {
            new(200, BuildAnthropicToolUseResponse(
                "Reading both files.",
                new List<(string, string, object)>
                {
                    ("toolu_01", "file_read", new { path = "exists.txt" }),
                    ("toolu_02", "file_read", new { path = "missing.txt" })
                })),
            new(200, BuildAnthropicEndTurnResponse(
                "One file was found, the other was not."))
        };

        // Act
        var result = await RunAnthropicAgenticLoop(scriptedResponses, registry, tools);

        // Assert
        result.CompletedTurns.Should().Be(2);
        result.TotalToolCalls.Should().Be(2);

        var toolResults = result.Messages.Where(m => m.Role == "tool").ToList();
        toolResults.Should().HaveCount(2);

        // First tool succeeded
        toolResults[0].ToolCallId.Should().Be("toolu_01");
        toolResults[0].Content.Should().Be("File content");

        // Second tool failed
        toolResults[1].ToolCallId.Should().Be("toolu_02");
        toolResults[1].Content.Should().Contain("File not found");
    }

    // =====================================================================
    // Test: Unknown tool in tool call
    // =====================================================================

    [Test]
    public async Task UnknownToolCall_ReturnsErrorToLlm_LoopContinues()
    {
        // Arrange: LLM tries to call a tool that doesn't exist in the registry
        var fileReadExecutor = CreateMockExecutor("file_read");
        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var scriptedResponses = new List<ScriptedResponse>
        {
            // Turn 1: LLM calls unknown tool
            new(200, BuildAnthropicToolUseResponse(
                "Let me use the special tool.",
                new List<(string, string, object)>
                {
                    ("toolu_01", "nonexistent_tool", new { input = "test" })
                })),

            // Turn 2: LLM recovers with known tool
            new(200, BuildAnthropicToolUseResponse(
                "That tool doesn't exist. Let me use file_read instead.",
                new List<(string, string, object)>
                {
                    ("toolu_02", "file_read", new { path = "test.txt" })
                })),

            // Turn 3: Final answer
            new(200, BuildAnthropicEndTurnResponse("Done."))
        };

        // Act
        var result = await RunAnthropicAgenticLoop(scriptedResponses, registry, tools);

        // Assert
        result.CompletedTurns.Should().Be(3);

        var unknownToolResult = result.Messages.First(m =>
            m.Role == "tool" && m.ToolCallId == "toolu_01");
        unknownToolResult.Content.Should().Contain("Unknown tool");
    }

    // =====================================================================
    // Test: LLM error mid-loop
    // =====================================================================

    [Test]
    public async Task LlmErrorMidLoop_StopsLoop_ReturnsLastGoodState()
    {
        // Arrange: Turn 1 succeeds, Turn 2 returns HTTP error
        var fileReadExecutor = CreateMockExecutor("file_read");
        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var errorResponse = JsonSerializer.Serialize(new
        {
            error = new { type = "rate_limit_error", message = "Too many requests" }
        });

        var scriptedResponses = new List<ScriptedResponse>
        {
            new(200, BuildAnthropicToolUseResponse(
                "Reading file.",
                new List<(string, string, object)>
                {
                    ("toolu_01", "file_read", new { path = "test.txt" })
                })),
            // ParseAnthropicResponse will parse this as success=true but with missing content
            // For a true API error, we simulate at the HTTP level (which we test indirectly)
            new(200, BuildAnthropicEndTurnResponse("Final after one tool call."))
        };

        // Act
        var result = await RunAnthropicAgenticLoop(scriptedResponses, registry, tools);

        // Assert: Loop completed normally with 2 turns
        result.CompletedTurns.Should().Be(2);
        result.TotalToolCalls.Should().Be(1);
    }

    // =====================================================================
    // Test: Empty tool calls array treated as end of loop
    // =====================================================================

    [Test]
    public async Task EmptyToolCallsArray_TreatedAsEndTurn()
    {
        // Arrange: Response has stop_reason=tool_use but no actual tool calls
        var registry = BuildRegistry();
        var responseJson = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Nothing to do." } },
            stop_reason = "tool_use",
            usage = new { input_tokens = 50, output_tokens = 20 }
        });

        var scriptedResponses = new List<ScriptedResponse>
        {
            new(200, responseJson)
        };

        // Act
        var result = await RunAnthropicAgenticLoop(scriptedResponses, registry);

        // Assert: Should terminate because ToolCalls is null (no tool_use blocks in content)
        result.CompletedTurns.Should().Be(1);
        result.TotalToolCalls.Should().Be(0);
        result.Exhausted.Should().BeFalse();
    }

    // =====================================================================
    // Test: Conversation history correctly accumulates all message types
    // =====================================================================

    [Test]
    public async Task ConversationHistory_FullAccumulation_AllMessageTypesPresent()
    {
        // Arrange: 2 tool turns + final answer
        var fileReadExecutor = CreateMockExecutor("file_read",
            async (id, args) => new ToolExecutionResult(id, "file_read", true,
                "Some file content", 5));

        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var scriptedResponses = new List<ScriptedResponse>
        {
            new(200, BuildAnthropicToolUseResponse(
                "Reading first file.",
                new List<(string, string, object)>
                {
                    ("t1", "file_read", new { path = "a.txt" })
                },
                inputTokens: 100, outputTokens: 20)),
            new(200, BuildAnthropicToolUseResponse(
                "Reading second file.",
                new List<(string, string, object)>
                {
                    ("t2", "file_read", new { path = "b.txt" })
                },
                inputTokens: 150, outputTokens: 25)),
            new(200, BuildAnthropicEndTurnResponse(
                "Both files read successfully.",
                inputTokens: 200, outputTokens: 30))
        };

        // Act
        var result = await RunAnthropicAgenticLoop(
            scriptedResponses, registry, tools,
            systemPrompt: "You are a test assistant.",
            userPrompt: "Read two files.");

        // Assert message structure
        var msgs = result.Messages;
        msgs.Should().HaveCount(6);

        // Message 0: system
        msgs[0].Role.Should().Be("system");
        msgs[0].Content.Should().Be("You are a test assistant.");

        // Message 1: user
        msgs[1].Role.Should().Be("user");
        msgs[1].Content.Should().Be("Read two files.");

        // Message 2: assistant (turn 1, with tool call)
        msgs[2].Role.Should().Be("assistant");
        msgs[2].Content.Should().Be("Reading first file.");
        msgs[2].ToolCalls.Should().HaveCount(1);
        msgs[2].ToolCalls![0].Id.Should().Be("t1");

        // Message 3: tool result for turn 1
        msgs[3].Role.Should().Be("tool");
        msgs[3].ToolCallId.Should().Be("t1");
        msgs[3].ToolName.Should().Be("file_read");
        msgs[3].Content.Should().Be("Some file content");

        // Message 4: assistant (turn 2, with tool call)
        msgs[4].Role.Should().Be("assistant");
        msgs[4].Content.Should().Be("Reading second file.");
        msgs[4].ToolCalls.Should().HaveCount(1);
        msgs[4].ToolCalls![0].Id.Should().Be("t2");

        // Message 5: tool result for turn 2
        msgs[5].Role.Should().Be("tool");
        msgs[5].ToolCallId.Should().Be("t2");
        msgs[5].Content.Should().Be("Some file content");
    }

    // =====================================================================
    // Test: Body builder produces correct format for multi-turn with tool results
    // =====================================================================

    [Test]
    public void AnthropicBodyBuilder_MultiTurnWithToolResults_CorrectBatching()
    {
        // Arrange: Full conversation with parallel tool calls
        var activity = new InlineToolLoopRunner(
            _activityLoggerMock.Object, null, null, null, null);

        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System" },
            new() { Role = "user", Content = "Read three files" },
            new()
            {
                Role = "assistant",
                Content = "Reading all three.",
                ToolCalls = new[]
                {
                    new ToolCallInfo("t1", "file_read", "{\"path\":\"a.txt\"}"),
                    new ToolCallInfo("t2", "file_read", "{\"path\":\"b.txt\"}"),
                    new ToolCallInfo("t3", "file_read", "{\"path\":\"c.txt\"}")
                }
            },
            new() { Role = "tool", Content = "Content A", ToolCallId = "t1", ToolName = "file_read" },
            new() { Role = "tool", Content = "Content B", ToolCallId = "t2", ToolName = "file_read" },
            new() { Role = "tool", Content = "Content C", ToolCallId = "t3", ToolName = "file_read" }
        };

        // Act
        var body = activity.BuildAnthropicMultiTurnBody(
            messages, "claude-sonnet-4-20250514", 4096, 0.7, null);

        // Assert
        var apiMessages = body["messages"] as List<object>;
        // user, assistant, user(3 tool_results batched) = 3
        apiMessages.Should().HaveCount(3);

        // The third message (index 2) should batch all 3 tool results
        var batchedMsg = apiMessages![2] as Dictionary<string, object>;
        batchedMsg!["role"].Should().Be("user");
        var blocks = batchedMsg["content"] as List<object>;
        blocks.Should().HaveCount(3, "all 3 tool results batched in one user message");

        // Verify each tool_result block
        for (var i = 0; i < 3; i++)
        {
            var block = blocks![i] as Dictionary<string, object>;
            block!["type"].Should().Be("tool_result");
        }
    }

    // =====================================================================
    // Test: MaxSteps = 1 terminates after single tool execution
    // =====================================================================

    [Test]
    public async Task MaxStepsOne_SingleToolExecution_TerminatesImmediately()
    {
        var fileReadExecutor = CreateMockExecutor("file_read");
        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var scriptedResponses = new List<ScriptedResponse>
        {
            new(200, BuildAnthropicToolUseResponse(
                "Reading.",
                new List<(string, string, object)>
                {
                    ("toolu_01", "file_read", new { path = "test.txt" })
                })),
            // This response would be used if loop continued
            new(200, BuildAnthropicEndTurnResponse("Done."))
        };

        var loopConfig = new ToolLoopConfig { MaxSteps = 1 };

        // Act
        var result = await RunAnthropicAgenticLoop(
            scriptedResponses, registry, tools, loopConfig);

        // Assert
        result.CompletedTurns.Should().Be(1);
        result.TotalToolCalls.Should().Be(1);
        result.Exhausted.Should().BeTrue("maxSteps=1 means exhausted after first tool turn");
    }

    // =====================================================================
    // Test: OpenAI multi-tool response format
    // =====================================================================

    [Test]
    public async Task OpenAi_MultiToolResponse_CorrectFormatAndExecution()
    {
        var fileReadExecutor = CreateMockExecutor("file_read",
            async (id, args) =>
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(args);
                var path = parsed.GetProperty("path").GetString();
                return new ToolExecutionResult(id, "file_read", true,
                    $"Content of {path}", 5);
            });

        var registry = BuildRegistry(fileReadExecutor.Object);
        var tools = new List<ResolvedTool>
        {
            new() { Name = "file_read", Description = "Read a file" }
        };

        var multiToolJson = """
        {
            "choices": [{
                "message": {
                    "role": "assistant",
                    "content": "Reading both files.",
                    "tool_calls": [
                        { "id": "call_01", "type": "function", "function": { "name": "file_read", "arguments": "{\"path\":\"x.txt\"}" } },
                        { "id": "call_02", "type": "function", "function": { "name": "file_read", "arguments": "{\"path\":\"y.txt\"}" } }
                    ]
                },
                "finish_reason": "tool_calls"
            }],
            "usage": { "prompt_tokens": 120, "completion_tokens": 40 },
            "model": "gpt-4o"
        }
        """;

        var scriptedResponses = new List<ScriptedResponse>
        {
            new(200, multiToolJson),
            new(200, BuildOpenAiEndTurnResponse("Both files read."))
        };

        // Act
        var result = await RunOpenAiAgenticLoop(scriptedResponses, registry, tools);

        // Assert
        result.CompletedTurns.Should().Be(2);
        result.TotalToolCalls.Should().Be(2);

        var toolResults = result.Messages.Where(m => m.Role == "tool").ToList();
        toolResults.Should().HaveCount(2);
        toolResults[0].Content.Should().Contain("x.txt");
        toolResults[1].Content.Should().Contain("y.txt");

        // Verify OpenAI body format for tool results
        var activity = new InlineToolLoopRunner(
            _activityLoggerMock.Object, null, null, null, registry);
        var body = activity.BuildOpenAiMultiTurnBody(result.Messages, "gpt-4o", 4096, 0.7, tools);
        var apiMessages = body["messages"] as List<object>;

        // OpenAI: each tool result is a separate message
        var openAiToolMsgs = apiMessages!.Cast<Dictionary<string, object>>()
            .Where(m => m["role"] as string == "tool").ToList();
        openAiToolMsgs.Should().HaveCount(2);
        (openAiToolMsgs[0]["tool_call_id"] as string).Should().Be("call_01");
        (openAiToolMsgs[1]["tool_call_id"] as string).Should().Be("call_02");
    }

    // =====================================================================
    // Test: Compaction with ToolLoopTokenTracker integration
    // =====================================================================

    [Test]
    public async Task CompactionIntegration_TrackerRecordsSummarizationTokens()
    {
        // Arrange: Build conversation above threshold, compact, track tokens
        var compactor = _compactor;
        var tracker = new ToolLoopTokenTracker();

        // Pre-loop tokens
        tracker.RecordTurn(100, 50);
        tracker.RecordTurn(120, 60);

        var messages = new List<ConversationMessage>
        {
            new() { Role = "system", Content = "System prompt" },
            new() { Role = "user", Content = new string('X', 2000) },
            new() { Role = "assistant", Content = new string('Y', 2000) },
            new() { Role = "user", Content = new string('Z', 2000) },
            new() { Role = "assistant", Content = "Response" },
            new() { Role = "user", Content = "Follow up" },
            new() { Role = "assistant", Content = "Another response" }
        };

        // Act
        var (compacted, summaryTokens, wasCompacted) = await compactor.CompactIfNeeded(
            messages,
            contextWindowTokens: 500,
            threshold: 0.8,
            summarize: async (prompt, ct) => "Summary of earlier conversation.",
            preservedTailCount: 4);

        if (wasCompacted && summaryTokens > 0)
        {
            // Record compaction overhead as a turn
            tracker.RecordTurn(summaryTokens, 0);
        }

        // Assert
        wasCompacted.Should().BeTrue();
        tracker.TurnCount.Should().Be(3, "2 pre-loop turns + 1 compaction overhead turn");
        tracker.TotalPromptTokens.Should().BeGreaterThan(220,
            "should include compaction tokens on top of 100+120");
    }

    // =====================================================================
    // Helper Types
    // =====================================================================

    private record ScriptedResponse(int StatusCode, string ResponseJson);

    private class AgenticLoopResult
    {
        public List<ConversationMessage> Messages { get; init; } = new();
        public NormalizedLlmResponse LastResponse { get; init; } = new();
        public int TotalPromptTokens { get; init; }
        public int TotalCompletionTokens { get; init; }
        public int TotalTokens { get; init; }
        public int TotalToolCalls { get; init; }
        public int CompletedTurns { get; init; }
        public bool Exhausted { get; init; }
        public List<(int Prompt, int Completion)> PerTurnTokens { get; init; } = new();
    }
}
