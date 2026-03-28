using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.Security;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Executes the actual HTTP call to the LLM provider API.
/// Supports Anthropic, OpenAI-compatible, and generic chat-completion endpoints.
/// Implements retry with exponential backoff within a single provider.
///
/// Outcomes:
///   "Success" — call succeeded, response is available.
///   "Retryable" — transient failure (429, 5xx), caller should retry or failover.
///   "Fatal" — non-retryable failure (401, 403, 400), skip this provider.
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Call LLM",
    "Execute HTTP call to LLM provider API with retry and backoff",
    Kind = ActivityKind.Task
)]
[FlowNode("Success", "Retryable", "Fatal")]
public class CallLlmActivity : Activity
{
    private readonly ILogger<CallLlmActivity> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IContentSanitizer? _sanitizer;
    private readonly IToolCallValidator? _toolCallValidator;

    /// <summary>Provider key (e.g. "anthropic", "openai").</summary>
    [Input(Description = "Provider key")]
    public Input<string> ProviderName { get; set; } = default!;

    /// <summary>Resolved system prompt.</summary>
    [Input(Description = "System prompt")]
    public Input<string> SystemPrompt { get; set; } = default!;

    /// <summary>User prompt.</summary>
    [Input(Description = "User prompt")]
    public Input<string> UserPrompt { get; set; } = default!;

    /// <summary>Model override (optional, falls back to provider default).</summary>
    [Input(Description = "Model override")]
    public Input<string?> ModelOverride { get; set; } = default!;

    /// <summary>Max tokens.</summary>
    [Input(Description = "Max tokens", DefaultValue = 4096)]
    public Input<int> MaxTokens { get; set; } = new(4096);

    /// <summary>Temperature.</summary>
    [Input(Description = "Temperature", DefaultValue = 0.7)]
    public Input<double> Temperature { get; set; } = new(0.7);

    /// <summary>Serialized tools JSON (list of ResolvedTool).</summary>
    [Input(Description = "Serialized tools (JSON array of ResolvedTool)")]
    public Input<string?> ToolsJson { get; set; } = default!;

    /// <summary>Current attempt number (1-based, managed by the workflow's retry loop).</summary>
    [Input(Description = "Current attempt number (1-based)", DefaultValue = 1)]
    public Input<int> AttemptNumber { get; set; } = new(1);

    [JsonConstructor]
    public CallLlmActivity() : this(null!, null!, null!, null, null)
    {
    }

    public CallLlmActivity(
        ILogger<CallLlmActivity> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IContentSanitizer? sanitizer,
        IToolCallValidator? toolCallValidator = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _sanitizer = sanitizer;
        _toolCallValidator = toolCallValidator;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var providerName = ProviderName.Get(context);
        var systemPromptRaw = SystemPrompt.Get(context);
        var userPromptRaw = UserPrompt.Get(context);
        var modelOverride = ModelOverride.Get(context);

        // Sanitize prompts before LLM call (defense-in-depth against prompt injection)
        string systemPrompt;
        string userPrompt;
        if (_sanitizer != null)
        {
            var totalPatterns = 0;

            var systemResult = _sanitizer.SanitizeInput(systemPromptRaw);
            systemPrompt = systemResult.Result;
            if (systemResult.Warnings.Count > 0)
            {
                totalPatterns += systemResult.Warnings.Count;
                _logger?.LogWarning(
                    "Injection pattern detected in SystemPrompt for CallLlmActivity, patterns matched: {Count}, workflow: {WorkflowInstanceId}",
                    systemResult.Warnings.Count, context.WorkflowExecutionContext.Id);
            }

            var userResult = _sanitizer.SanitizeInput(userPromptRaw);
            userPrompt = userResult.Result;
            if (userResult.Warnings.Count > 0)
            {
                totalPatterns += userResult.Warnings.Count;
                _logger?.LogWarning(
                    "Injection pattern detected in UserPrompt for CallLlmActivity, patterns matched: {Count}, workflow: {WorkflowInstanceId}",
                    userResult.Warnings.Count, context.WorkflowExecutionContext.Id);
            }

            if (totalPatterns > 0)
                _logger?.LogInformation(
                    "Total injection patterns detected per LLM call: {TotalPatternsMatched}, activity=CallLlmActivity, provider={Provider}, workflow: {WorkflowInstanceId}",
                    totalPatterns, providerName, context.WorkflowExecutionContext.Id);
        }
        else
        {
            systemPrompt = systemPromptRaw;
            userPrompt = userPromptRaw;
        }
        var maxTokens = MaxTokens.Get(context);
        var temperature = Temperature.Get(context);
        var toolsJson = ToolsJson.Get(context);
        var attemptNumber = AttemptNumber.Get(context);

        var startedAt = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Load provider config
        var providerConfig = LoadProviderConfig(providerName);
        var model = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride
            : providerConfig.DefaultModel;

        var tools = DeserializeTools(toolsJson);

        _logger?.LogInformation(
            "CallLlm: provider={Provider}, model={Model}, attempt={Attempt}",
            providerName, model, attemptNumber);

        try
        {
            var response = await ExecuteProviderCall(
                providerName, providerConfig, model, systemPrompt, userPrompt,
                maxTokens, temperature, tools);

            // Output sanitization: strip HTML/zero-width from LLM response before storage
            if (_sanitizer != null && response.ResponseText != null)
            {
                var outputResult = _sanitizer.SanitizeOutput(response.ResponseText);
                response.ResponseText = outputResult.Result;
            }

            // Tool call validation (Story 11.3): validate tool calls before returning
            if (_toolCallValidator != null && response.ToolCalls != null && response.ToolCalls.Count > 0)
            {
                var allowedToolNames = tools?.Select(t => t.Name).ToList()
                    ?? new List<string>();
                foreach (var tc in response.ToolCalls)
                {
                    var vr = _toolCallValidator.Validate(tc, allowedToolNames);
                    if (vr.IsValid)
                    {
                        tc.ArgumentsJson = vr.SanitizedArgumentsJson ?? tc.ArgumentsJson;
                    }
                    else
                    {
                        _logger?.LogWarning(
                            "Tool call '{ToolName}' rejected in CallLlmActivity: {Error}",
                            tc.ToolName, vr.ErrorMessage);
                        response.Success = false;
                        response.ErrorMessage = $"Tool validation failed: {vr.ErrorMessage}";
                        break;
                    }
                }
            }

            sw.Stop();

            // Store the diagnostic on the activity execution context for RecordDiagnosticsActivity
            var diagnostic = new ProviderAttemptDiagnostic
            {
                ProviderName = providerName,
                Model = model,
                AttemptNumber = attemptNumber,
                Succeeded = response.Success,
                HttpStatusCode = response.HttpStatusCode,
                ErrorMessage = response.ErrorMessage,
                DurationMs = sw.ElapsedMilliseconds,
                StartedAtUtc = startedAt,
                PromptTokens = response.PromptTokens,
                CompletionTokens = response.CompletionTokens
            };

            // Serialize diagnostic and response into workflow variables
            context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
            context.SetVariable("LastResponse", JsonSerializer.Serialize(response));

            if (response.Success)
            {
                _logger?.LogInformation(
                    "CallLlm succeeded: provider={Provider}, model={Model}, tokens={Tokens}, duration={Duration}ms",
                    providerName, model, response.PromptTokens + response.CompletionTokens, sw.ElapsedMilliseconds);

                await context.CompleteActivityWithOutcomesAsync("Success");
            }
            else if (IsRetryableStatusCode(response.HttpStatusCode))
            {
                _logger?.LogWarning(
                    "CallLlm retryable failure: provider={Provider}, status={Status}, error={Error}",
                    providerName, response.HttpStatusCode, response.ErrorMessage);

                await context.CompleteActivityWithOutcomesAsync("Retryable");
            }
            else
            {
                _logger?.LogError(
                    "CallLlm fatal failure: provider={Provider}, status={Status}, error={Error}",
                    providerName, response.HttpStatusCode, response.ErrorMessage);

                await context.CompleteActivityWithOutcomesAsync("Fatal");
            }
        }
        catch (TaskCanceledException ex)
        {
            sw.Stop();

            _logger?.LogWarning(ex,
                "CallLlm timeout: provider={Provider}, model={Model}, after {Duration}ms",
                providerName, model, sw.ElapsedMilliseconds);

            var diagnostic = new ProviderAttemptDiagnostic
            {
                ProviderName = providerName,
                Model = model,
                AttemptNumber = attemptNumber,
                Succeeded = false,
                HttpStatusCode = 0,
                ErrorMessage = $"Request timed out after {providerConfig.TimeoutSeconds}s",
                DurationMs = sw.ElapsedMilliseconds,
                StartedAtUtc = startedAt
            };

            context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
            context.SetVariable("LastResponse", JsonSerializer.Serialize(new NormalizedLlmResponse
            {
                Success = false,
                ErrorMessage = diagnostic.ErrorMessage
            }));

            await context.CompleteActivityWithOutcomesAsync("Retryable");
        }
        catch (Exception ex)
        {
            sw.Stop();

            _logger?.LogError(ex,
                "CallLlm unexpected error: provider={Provider}, model={Model}",
                providerName, model);

            var diagnostic = new ProviderAttemptDiagnostic
            {
                ProviderName = providerName,
                Model = model,
                AttemptNumber = attemptNumber,
                Succeeded = false,
                HttpStatusCode = 0,
                ErrorMessage = $"Unexpected error: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds,
                StartedAtUtc = startedAt
            };

            context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
            context.SetVariable("LastResponse", JsonSerializer.Serialize(new NormalizedLlmResponse
            {
                Success = false,
                ErrorMessage = diagnostic.ErrorMessage
            }));

            await context.CompleteActivityWithOutcomesAsync("Fatal");
        }
    }

    private async Task<NormalizedLlmResponse> ExecuteProviderCall(
        string providerName,
        LlmProviderConfig config,
        string model,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        List<ResolvedTool>? tools)
    {
        var httpClient = _httpClientFactory?.CreateClient($"llm-{providerName}")
                         ?? new HttpClient();

        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            httpClient.BaseAddress = new Uri(config.BaseUrl);
        }

        httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

        // Route to the appropriate provider format
        return providerName.ToLowerInvariant() switch
        {
            "anthropic" => await CallAnthropicApi(httpClient, config, model, systemPrompt, userPrompt, maxTokens, temperature, tools),
            _ => await CallOpenAiCompatibleApi(httpClient, config, model, systemPrompt, userPrompt, maxTokens, temperature, tools)
        };
    }

    // ================================================================
    // Anthropic Messages API
    // ================================================================

    private async Task<NormalizedLlmResponse> CallAnthropicApi(
        HttpClient httpClient,
        LlmProviderConfig config,
        string model,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        List<ResolvedTool>? tools)
    {
        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["system"] = systemPrompt,
            ["messages"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["role"] = "user",
                    ["content"] = userPrompt
                }
            }
        };

        if (tools != null && tools.Count > 0)
        {
            requestBody["tools"] = tools.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema
            }).ToList();
        }

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var baseUrl = config.BaseUrl.TrimEnd('/');
        var response = await httpClient.PostAsync($"{baseUrl}/v1/messages", content);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new NormalizedLlmResponse
            {
                Success = false,
                HttpStatusCode = statusCode,
                ErrorMessage = $"Anthropic API error {statusCode}: {TruncateError(errorBody)}"
            };
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Extract text from content blocks
        var responseText = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();

        if (result.TryGetProperty("content", out var contentArray))
        {
            foreach (var block in contentArray.EnumerateArray())
            {
                var blockType = block.GetProperty("type").GetString();

                if (blockType == "text" && block.TryGetProperty("text", out var textProp))
                {
                    responseText.Append(textProp.GetString());
                }
                else if (blockType == "tool_use")
                {
                    toolCalls.Add(new LlmToolCall
                    {
                        Id = block.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "",
                        ToolName = block.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "",
                        ArgumentsJson = block.TryGetProperty("input", out var inputProp) ? inputProp.GetRawText() : "{}"
                    });
                }
            }
        }

        // Extract usage
        var promptTokens = 0;
        var completionTokens = 0;
        if (result.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var inp))
                promptTokens = inp.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var outp))
                completionTokens = outp.GetInt32();
        }

        var actualModel = result.TryGetProperty("model", out var modelProp2)
            ? modelProp2.GetString()
            : model;

        return new NormalizedLlmResponse
        {
            Success = true,
            ResponseText = responseText.ToString(),
            Model = actualModel,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            HttpStatusCode = statusCode,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null
        };
    }

    // ================================================================
    // OpenAI-compatible chat/completions API
    // (works for OpenAI, OpenRouter, local LLMs, etc.)
    // ================================================================

    private async Task<NormalizedLlmResponse> CallOpenAiCompatibleApi(
        HttpClient httpClient,
        LlmProviderConfig config,
        string model,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        List<ResolvedTool>? tools)
    {
        httpClient.DefaultRequestHeaders.Clear();
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
        }

        var messages = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "system", ["content"] = systemPrompt },
            new() { ["role"] = "user", ["content"] = userPrompt }
        };

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["messages"] = messages
        };

        if (tools != null && tools.Count > 0)
        {
            requestBody["tools"] = tools.Select(t => new Dictionary<string, object?>
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

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var baseUrl = config.BaseUrl.TrimEnd('/');
        var response = await httpClient.PostAsync($"{baseUrl}/v1/chat/completions", content);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new NormalizedLlmResponse
            {
                Success = false,
                HttpStatusCode = statusCode,
                ErrorMessage = $"OpenAI-compatible API error {statusCode}: {TruncateError(errorBody)}"
            };
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Extract from choices[0].message
        string? responseText = null;
        var toolCalls = new List<LlmToolCall>();

        if (result.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var msgContent))
                {
                    responseText = msgContent.GetString();
                }

                if (message.TryGetProperty("tool_calls", out var msgToolCalls))
                {
                    foreach (var tc in msgToolCalls.EnumerateArray())
                    {
                        var fn = tc.TryGetProperty("function", out var fnProp) ? fnProp : default;
                        toolCalls.Add(new LlmToolCall
                        {
                            Id = tc.TryGetProperty("id", out var tcId) ? tcId.GetString() ?? "" : "",
                            ToolName = fn.ValueKind != JsonValueKind.Undefined && fn.TryGetProperty("name", out var fnName)
                                ? fnName.GetString() ?? ""
                                : "",
                            ArgumentsJson = fn.ValueKind != JsonValueKind.Undefined && fn.TryGetProperty("arguments", out var fnArgs)
                                ? fnArgs.GetString() ?? "{}"
                                : "{}"
                        });
                    }
                }
            }
        }

        // Extract usage
        var promptTokens = 0;
        var completionTokens = 0;
        if (result.TryGetProperty("usage", out var usageEl))
        {
            if (usageEl.TryGetProperty("prompt_tokens", out var pt))
                promptTokens = pt.GetInt32();
            if (usageEl.TryGetProperty("completion_tokens", out var ct))
                completionTokens = ct.GetInt32();
        }

        var actualModel = result.TryGetProperty("model", out var modelEl)
            ? modelEl.GetString()
            : model;

        return new NormalizedLlmResponse
        {
            Success = true,
            ResponseText = responseText,
            Model = actualModel,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            HttpStatusCode = statusCode,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null
        };
    }

    // ================================================================
    // Helpers
    // ================================================================

    private LlmProviderConfig LoadProviderConfig(string providerName)
    {
        var section = _configuration?.GetSection($"LlmProviders:{providerName}");
        var config = new LlmProviderConfig { Name = providerName };

        if (section == null || !section.Exists())
        {
            // Fall back to well-known defaults
            return providerName.ToLowerInvariant() switch
            {
                "anthropic" => new LlmProviderConfig
                {
                    Name = providerName,
                    BaseUrl = "https://api.anthropic.com",
                    ApiKey = _configuration?["Anthropic:ApiKey"] ?? "",
                    DefaultModel = _configuration?["Anthropic:Model"] ?? "claude-sonnet-4-20250514"
                },
                "openai" => new LlmProviderConfig
                {
                    Name = providerName,
                    BaseUrl = "https://api.openai.com",
                    ApiKey = _configuration?["OpenAI:ApiKey"] ?? "",
                    DefaultModel = "gpt-4o"
                },
                "openrouter" => new LlmProviderConfig
                {
                    Name = providerName,
                    BaseUrl = "https://openrouter.ai/api",
                    ApiKey = _configuration?["OpenRouter:ApiKey"] ?? "",
                    DefaultModel = "anthropic/claude-sonnet-4-20250514"
                },
                _ => config
            };
        }

        config.BaseUrl = section["BaseUrl"] ?? "";
        config.ApiKey = section["ApiKey"] ?? "";
        config.DefaultModel = section["DefaultModel"] ?? "";

        if (int.TryParse(section["MaxRetries"], out var maxRetries))
            config.MaxRetries = maxRetries;
        if (int.TryParse(section["BaseRetryDelayMs"], out var baseDelay))
            config.BaseRetryDelayMs = baseDelay;
        if (int.TryParse(section["MaxRetryDelayMs"], out var maxDelay))
            config.MaxRetryDelayMs = maxDelay;
        if (int.TryParse(section["CircuitBreakerFailureThreshold"], out var cbThreshold))
            config.CircuitBreakerFailureThreshold = cbThreshold;
        if (int.TryParse(section["CircuitBreakerCooldownSeconds"], out var cbCooldown))
            config.CircuitBreakerCooldownSeconds = cbCooldown;
        if (decimal.TryParse(section["CostPer1KPromptTokens"], out var costPrompt))
            config.CostPer1KPromptTokens = costPrompt;
        if (decimal.TryParse(section["CostPer1KCompletionTokens"], out var costCompletion))
            config.CostPer1KCompletionTokens = costCompletion;
        if (int.TryParse(section["TimeoutSeconds"], out var timeout))
            config.TimeoutSeconds = timeout;
        if (bool.TryParse(section["Enabled"], out var enabled))
            config.Enabled = enabled;

        return config;
    }

    private static bool IsRetryableStatusCode(int statusCode)
    {
        return statusCode == 429 || statusCode == 502 || statusCode == 503 || statusCode == 504 || statusCode == 0;
    }

    private static string TruncateError(string? errorBody, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(errorBody))
            return "(empty response)";
        return errorBody.Length > maxLength
            ? errorBody[..maxLength] + "..."
            : errorBody;
    }

    private static List<ResolvedTool>? DeserializeTools(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<ResolvedTool>>(json);
        }
        catch
        {
            return null;
        }
    }
}
