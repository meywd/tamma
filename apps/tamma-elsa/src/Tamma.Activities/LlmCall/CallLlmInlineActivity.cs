using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Inline activity that performs the actual LLM HTTP call.
/// Writes results to workflow variables "LastDiagnostic" and "LastResponse".
/// This is used inside the Sequence-based retry loop of LlmCallWorkflow.
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Call LLM Inline",
    "Execute HTTP call to LLM provider (inline for Sequence-based workflows)",
    Kind = ActivityKind.Task
)]
public class CallLlmInlineActivity : CodeActivity
{
    [Input(Description = "Serialized workflow input JSON")]
    public Input<string> InputJsonProp { get; set; } = default!;

    [Input(Description = "Provider key")]
    public Input<string> ProviderNameProp { get; set; } = default!;

    [Input(Description = "Resolved system prompt")]
    public Input<string> SystemPromptProp { get; set; } = default!;

    [Input(Description = "Resolved tools JSON")]
    public Input<string?> ToolsJsonProp { get; set; } = default!;

    [Input(Description = "Attempt number")]
    public Input<int> AttemptNumberProp { get; set; } = default!;

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<CallLlmInlineActivity>? _logger;

    [JsonConstructor]
    public CallLlmInlineActivity() : this(null, null, null)
    {
    }

    public CallLlmInlineActivity(
        ILogger<CallLlmInlineActivity>? logger,
        IHttpClientFactory? httpClientFactory,
        IConfiguration? configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var inputJson = InputJsonProp.Get(context);
        var providerName = ProviderNameProp.Get(context);
        var systemPrompt = SystemPromptProp.Get(context);
        var toolsJson = ToolsJsonProp.Get(context);
        var attemptNumber = AttemptNumberProp.Get(context);

        var input = ParseInput(inputJson);

        var startedAt = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Apply exponential backoff delay for retry attempts (skip first attempt)
        if (attemptNumber > 1)
        {
            var baseDelay = 1000;
            var maxDelay = 30000;
            var delay = Math.Min(baseDelay * (int)Math.Pow(2, attemptNumber - 1), maxDelay);
            _logger?.LogInformation(
                "Retry backoff: waiting {Delay}ms before attempt {Attempt} for {Provider}",
                delay, attemptNumber, providerName);
            await Task.Delay(delay);
        }

        var model = input.ModelOverrides.TryGetValue(providerName, out var mo)
            ? mo
            : GetDefaultModel(providerName);

        try
        {
            var httpClient = _httpClientFactory?.CreateClient($"llm-{providerName}")
                             ?? new HttpClient();

            var providerConfig = LoadProviderConfig(providerName);
            httpClient.Timeout = TimeSpan.FromSeconds(providerConfig.TimeoutSeconds);

            NormalizedLlmResponse response;

            if (providerName.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            {
                response = await CallAnthropicMessages(httpClient, providerConfig, model,
                    systemPrompt, input.UserPrompt, input.MaxTokens, input.Temperature, toolsJson);
            }
            else
            {
                response = await CallOpenAiCompatible(httpClient, providerConfig, model,
                    systemPrompt, input.UserPrompt, input.MaxTokens, input.Temperature, toolsJson);
            }

            sw.Stop();

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

            context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
            context.SetVariable("LastResponse", JsonSerializer.Serialize(response));
        }
        catch (Exception ex)
        {
            sw.Stop();

            var diagnostic = new ProviderAttemptDiagnostic
            {
                ProviderName = providerName,
                Model = model,
                AttemptNumber = attemptNumber,
                Succeeded = false,
                HttpStatusCode = 0,
                ErrorMessage = ex is TaskCanceledException
                    ? "Request timed out"
                    : $"Error: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds,
                StartedAtUtc = startedAt
            };

            context.SetVariable("LastDiagnostic", JsonSerializer.Serialize(diagnostic));
            context.SetVariable("LastResponse", JsonSerializer.Serialize(new NormalizedLlmResponse
            {
                Success = false,
                ErrorMessage = diagnostic.ErrorMessage
            }));
        }
    }

    private async Task<NormalizedLlmResponse> CallAnthropicMessages(
        HttpClient httpClient, LlmProviderConfig config, string model,
        string systemPrompt, string userPrompt, int maxTokens, double temperature,
        string? toolsJson)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.BaseUrl)
            ? config.BaseUrl.TrimEnd('/')
            : "https://api.anthropic.com";

        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["system"] = systemPrompt,
            ["messages"] = new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = userPrompt } }
        };

        if (!string.IsNullOrWhiteSpace(toolsJson))
        {
            try
            {
                var tools = JsonSerializer.Deserialize<List<ResolvedTool>>(toolsJson);
                if (tools != null && tools.Count > 0)
                {
                    requestBody["tools"] = tools.Select(t => new Dictionary<string, object?>
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["input_schema"] = t.InputSchema
                    }).ToList();
                }
            }
            catch { /* ignore malformed tools */ }
        }

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{baseUrl}/v1/messages", content);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new NormalizedLlmResponse
            {
                Success = false,
                HttpStatusCode = statusCode,
                ErrorMessage = $"Anthropic API error {statusCode}: {Truncate(errorBody)}"
            };
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        var responseText = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();

        if (result.TryGetProperty("content", out var contentArr))
        {
            foreach (var block in contentArr.EnumerateArray())
            {
                var blockType = block.GetProperty("type").GetString();
                if (blockType == "text" && block.TryGetProperty("text", out var t))
                    responseText.Append(t.GetString());
                else if (blockType == "tool_use")
                {
                    toolCalls.Add(new LlmToolCall
                    {
                        Id = block.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        ToolName = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        ArgumentsJson = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}"
                    });
                }
            }
        }

        int promptTokens = 0, completionTokens = 0;
        if (result.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it)) promptTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot)) completionTokens = ot.GetInt32();
        }

        return new NormalizedLlmResponse
        {
            Success = true,
            ResponseText = responseText.ToString(),
            Model = result.TryGetProperty("model", out var m) ? m.GetString() : model,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            HttpStatusCode = statusCode,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null
        };
    }

    private async Task<NormalizedLlmResponse> CallOpenAiCompatible(
        HttpClient httpClient, LlmProviderConfig config, string model,
        string systemPrompt, string userPrompt, int maxTokens, double temperature,
        string? toolsJson)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.BaseUrl)
            ? config.BaseUrl.TrimEnd('/')
            : "https://api.openai.com";

        httpClient.DefaultRequestHeaders.Clear();
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");

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

        if (!string.IsNullOrWhiteSpace(toolsJson))
        {
            try
            {
                var tools = JsonSerializer.Deserialize<List<ResolvedTool>>(toolsJson);
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
            }
            catch { /* ignore */ }
        }

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{baseUrl}/v1/chat/completions", content);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            return new NormalizedLlmResponse
            {
                Success = false,
                HttpStatusCode = statusCode,
                ErrorMessage = $"OpenAI-compatible API error {statusCode}: {Truncate(errorBody)}"
            };
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        string? responseText = null;
        var toolCalls = new List<LlmToolCall>();

        if (result.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var msg = choices[0].TryGetProperty("message", out var msgEl) ? msgEl : default;
            if (msg.ValueKind != JsonValueKind.Undefined)
            {
                if (msg.TryGetProperty("content", out var c))
                    responseText = c.GetString();
                if (msg.TryGetProperty("tool_calls", out var tcs))
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var fn = tc.TryGetProperty("function", out var fnProp) ? fnProp : default;
                        toolCalls.Add(new LlmToolCall
                        {
                            Id = tc.TryGetProperty("id", out var tcId) ? tcId.GetString() ?? "" : "",
                            ToolName = fn.ValueKind != JsonValueKind.Undefined && fn.TryGetProperty("name", out var fnN)
                                ? fnN.GetString() ?? "" : "",
                            ArgumentsJson = fn.ValueKind != JsonValueKind.Undefined && fn.TryGetProperty("arguments", out var fnA)
                                ? fnA.GetString() ?? "{}" : "{}"
                        });
                    }
                }
            }
        }

        int promptTokens = 0, completionTokens = 0;
        if (result.TryGetProperty("usage", out var usg))
        {
            if (usg.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
            if (usg.TryGetProperty("completion_tokens", out var ct)) completionTokens = ct.GetInt32();
        }

        return new NormalizedLlmResponse
        {
            Success = true,
            ResponseText = responseText,
            Model = result.TryGetProperty("model", out var modEl) ? modEl.GetString() : model,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            HttpStatusCode = statusCode,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null
        };
    }

    private LlmProviderConfig LoadProviderConfig(string providerName)
    {
        var section = _configuration?.GetSection($"LlmProviders:{providerName}");
        if (section != null && section.Exists())
        {
            var config = new LlmProviderConfig { Name = providerName };
            config.BaseUrl = section["BaseUrl"] ?? "";
            config.ApiKey = section["ApiKey"] ?? "";
            config.DefaultModel = section["DefaultModel"] ?? "";
            if (int.TryParse(section["TimeoutSeconds"], out var t)) config.TimeoutSeconds = t;
            return config;
        }

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
            _ => new LlmProviderConfig { Name = providerName }
        };
    }

    private string GetDefaultModel(string providerName)
    {
        return LoadProviderConfig(providerName).DefaultModel;
    }

    private static LlmCallWorkflowInput ParseInput(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new LlmCallWorkflowInput();
        try { return JsonSerializer.Deserialize<LlmCallWorkflowInput>(json) ?? new LlmCallWorkflowInput(); }
        catch { return new LlmCallWorkflowInput(); }
    }

    private static string Truncate(string? s, int max = 500)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        return s.Length > max ? s[..max] + "..." : s;
    }
}
