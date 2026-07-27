using System.Text.Json;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Api.Services.Providers;

/// <summary>
/// Phase 1 of the provider abstraction — ONE request-body builder per
/// <see cref="ProviderWireDialect"/>, extracted VERBATIM from
/// <c>InlineToolLoopRunner</c>'s multi-turn builders (the copy that already
/// shaped requests correctly, including the version-header handling). The
/// runner's multi-turn and single-turn callers and
/// <see cref="HttpProviderClient"/> all consume these; the byte-identity of
/// the produced JSON with the pre-refactor code is pinned by
/// <c>ProviderGoldenRequestTests</c>.
///
/// <para><c>maxTokens</c>/<c>temperature</c> are nullable because the
/// <see cref="HttpProviderClient"/> path historically serialized explicit
/// JSON <c>null</c>s for them; the runner always passes concrete values.</para>
/// </summary>
public static class ProviderRequestShaper
{
    /// <summary>
    /// Build the Anthropic Messages API request body for a conversation.
    /// System messages map to the top-level <c>system</c> field; assistant
    /// turns become content-block arrays (<c>text</c> + <c>tool_use</c>);
    /// tool results become <c>tool_result</c> blocks inside user messages
    /// (batched when consecutive).
    /// </summary>
    public static Dictionary<string, object?> BuildAnthropicBody(
        IReadOnlyList<ConversationMessage> messages,
        string model, int? maxTokens, double? temperature, IReadOnlyList<ResolvedTool>? tools)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
        };

        // System prompt goes to top-level "system" field (NOT a message)
        var systemMsg = messages.FirstOrDefault(m => m.Role == "system");
        if (systemMsg != null)
            body["system"] = systemMsg.Content ?? "";

        // Build messages array (skip system message)
        var apiMessages = new List<object>();

        foreach (var msg in messages.Where(m => m.Role != "system"))
        {
            if (msg.Role == "user" && msg.ToolCallId == null)
            {
                apiMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = msg.Content ?? ""
                });
            }
            else if (msg.Role == "assistant")
            {
                var contentBlocks = new List<object>();

                if (!string.IsNullOrEmpty(msg.Content))
                {
                    contentBlocks.Add(new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = msg.Content
                    });
                }

                if (msg.ToolCalls != null)
                {
                    foreach (var tc in msg.ToolCalls)
                    {
                        object inputObj;
                        try
                        {
                            inputObj = JsonSerializer.Deserialize<object>(tc.ArgumentsJson) ?? new object();
                        }
                        catch
                        {
                            inputObj = new object();
                        }

                        contentBlocks.Add(new Dictionary<string, object>
                        {
                            ["type"] = "tool_use",
                            ["id"] = tc.Id,
                            ["name"] = tc.Name,
                            ["input"] = inputObj
                        });
                    }
                }

                apiMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = contentBlocks
                });
            }
            else if (msg.Role == "tool")
            {
                // Anthropic: tool_result blocks go in a user-role message
                var toolResultBlock = new Dictionary<string, object>
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = msg.ToolCallId ?? "",
                    ["content"] = msg.Content ?? ""
                };

                // Batch multiple tool_result blocks into a single user message
                if (apiMessages.Count > 0 &&
                    apiMessages[^1] is Dictionary<string, object> lastMsg &&
                    lastMsg.TryGetValue("role", out var lastRole) &&
                    lastRole is string roleStr && roleStr == "user" &&
                    lastMsg.TryGetValue("content", out var lastContent) &&
                    lastContent is List<object> existingBlocks &&
                    existingBlocks.Count > 0 &&
                    existingBlocks[0] is Dictionary<string, object> firstBlock &&
                    firstBlock.TryGetValue("type", out var blockType) &&
                    blockType is string blockTypeStr && blockTypeStr == "tool_result")
                {
                    existingBlocks.Add(toolResultBlock);
                }
                else
                {
                    apiMessages.Add(new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = new List<object> { toolResultBlock }
                    });
                }
            }
        }

        body["messages"] = apiMessages;

        if (tools != null && tools.Count > 0)
        {
            body["tools"] = tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema
            }).ToList();
        }

        return body;
    }

    /// <summary>
    /// Build the OpenAI Chat Completions API request body for a conversation.
    /// Spoken verbatim by every <see cref="ProviderWireDialect.OpenAiCompatible"/>
    /// provider (OpenAI, OpenRouter, DeepSeek, Moonshot/Kimi, Z.ai/GLM, …).
    /// </summary>
    public static Dictionary<string, object?> BuildOpenAiCompatibleBody(
        IReadOnlyList<ConversationMessage> messages,
        string model, int? maxTokens, double? temperature, IReadOnlyList<ResolvedTool>? tools)
    {
        var apiMessages = new List<object>();

        foreach (var msg in messages)
        {
            if (msg.Role == "system" || (msg.Role == "user" && msg.ToolCallId == null))
            {
                apiMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = msg.Role,
                    ["content"] = msg.Content ?? ""
                });
            }
            else if (msg.Role == "assistant")
            {
                var assistantMsg = new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = msg.Content
                };

                if (msg.ToolCalls != null && msg.ToolCalls.Length > 0)
                {
                    assistantMsg["tool_calls"] = msg.ToolCalls.Select(tc =>
                        new Dictionary<string, object>
                        {
                            ["id"] = tc.Id,
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object>
                            {
                                ["name"] = tc.Name,
                                ["arguments"] = tc.ArgumentsJson
                            }
                        }).ToList();
                }

                apiMessages.Add(assistantMsg);
            }
            else if (msg.Role == "tool")
            {
                apiMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = msg.ToolCallId ?? "",
                    ["content"] = msg.Content ?? ""
                });
            }
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["messages"] = apiMessages
        };

        if (tools != null && tools.Count > 0)
        {
            body["tools"] = tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema
                }
            }).ToList();
        }

        return body;
    }

    /// <summary>Dialect-dispatching convenience over the two builders.</summary>
    public static Dictionary<string, object?> BuildBody(
        ProviderWireDialect dialect,
        IReadOnlyList<ConversationMessage> messages,
        string model, int? maxTokens, double? temperature, IReadOnlyList<ResolvedTool>? tools)
        => dialect == ProviderWireDialect.Anthropic
            ? BuildAnthropicBody(messages, model, maxTokens, temperature, tools)
            : BuildOpenAiCompatibleBody(messages, model, maxTokens, temperature, tools);
}
