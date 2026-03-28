using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.Security;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Resolves the system prompt using a 6-level configuration hierarchy.
/// Resolution order (first match wins):
///   1. Per-provider + per-role:   LlmPrompts:{provider}:{role}
///   2. Per-provider default:      LlmPrompts:{provider}:default
///   3. Per-role global:           LlmPrompts:roles:{role}
///   4. Per-operation global:      LlmPrompts:operations:{operation}
///   5. Global default:            LlmPrompts:default
///   6. Hardcoded fallback
///
/// If the caller provides a SystemPromptOverride, it is used as-is (CallerOverride level).
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Resolve LLM Prompt",
    "Resolve system prompt via 6-level configuration hierarchy",
    Kind = ActivityKind.Task
)]
public class ResolveLlmPromptActivity : CodeActivity<ResolvedPrompt>
{
    private readonly ILogger<ResolveLlmPromptActivity> _logger;
    private readonly IConfiguration _configuration;

    /// <summary>Provider key (e.g. "anthropic").</summary>
    [Input(Description = "Provider key")]
    public Input<string> ProviderName { get; set; } = default!;

    /// <summary>Role / persona (e.g. "mentor", "code_reviewer").</summary>
    [Input(Description = "Role for prompt resolution")]
    public Input<string> Role { get; set; } = default!;

    /// <summary>Operation name (e.g. "blocker_diagnosis").</summary>
    [Input(Description = "Operation name for prompt resolution")]
    public Input<string> OperationName { get; set; } = default!;

    /// <summary>User prompt (passed through to output).</summary>
    [Input(Description = "User prompt content")]
    public Input<string> UserPrompt { get; set; } = default!;

    /// <summary>Optional caller-provided system prompt override.</summary>
    [Input(Description = "Explicit system prompt override (optional)")]
    public Input<string?> SystemPromptOverride { get; set; } = default!;

    [JsonConstructor]
    public ResolveLlmPromptActivity() : this(null!, null!)
    {
    }

    public ResolveLlmPromptActivity(
        ILogger<ResolveLlmPromptActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var providerName = ProviderName.Get(context);
        var role = Role.Get(context);
        var operationName = OperationName.Get(context);
        var userPrompt = UserPrompt.Get(context);
        var systemOverride = SystemPromptOverride.Get(context);

        // If caller provided an explicit override, sanitize untrusted input then harden
        if (!string.IsNullOrWhiteSpace(systemOverride))
        {
            _logger?.LogDebug("Using caller-provided system prompt override");

            var sanitized = SecurityHelpers.SanitizeForPrompt(systemOverride);
            context.SetResult(new ResolvedPrompt
            {
                SystemPrompt = PromptHardening.Harden(sanitized),
                UserPrompt = userPrompt,
                ResolvedLevel = PromptResolutionLevel.CallerOverride,
                MatchedConfigKey = "(caller override)"
            });
            return;
        }

        // Walk the 6-level hierarchy
        var (prompt, level, key) = ResolveFromHierarchy(providerName, role, operationName);

        _logger?.LogInformation(
            "Resolved system prompt at level {Level} via key '{Key}' for provider={Provider}, role={Role}, op={Op}",
            level, key, providerName, role, operationName);

        context.SetResult(new ResolvedPrompt
        {
            SystemPrompt = PromptHardening.Harden(prompt),
            UserPrompt = userPrompt,
            ResolvedLevel = level,
            MatchedConfigKey = key
        });
    }

    private (string prompt, PromptResolutionLevel level, string key) ResolveFromHierarchy(
        string provider, string role, string operation)
    {
        // Level 1: Per-provider + per-role
        var key1 = $"LlmPrompts:{provider}:{role}";
        var val = _configuration?[key1];
        if (!string.IsNullOrWhiteSpace(val))
            return (val, PromptResolutionLevel.PerProviderPerRole, key1);

        // Level 2: Per-provider default
        var key2 = $"LlmPrompts:{provider}:default";
        val = _configuration?[key2];
        if (!string.IsNullOrWhiteSpace(val))
            return (val, PromptResolutionLevel.PerProviderDefault, key2);

        // Level 3: Per-role global
        var key3 = $"LlmPrompts:roles:{role}";
        val = _configuration?[key3];
        if (!string.IsNullOrWhiteSpace(val))
            return (val, PromptResolutionLevel.PerRole, key3);

        // Level 4: Per-operation global
        var key4 = $"LlmPrompts:operations:{operation}";
        val = _configuration?[key4];
        if (!string.IsNullOrWhiteSpace(val))
            return (val, PromptResolutionLevel.PerOperation, key4);

        // Level 5: Global default
        var key5 = "LlmPrompts:default";
        val = _configuration?[key5];
        if (!string.IsNullOrWhiteSpace(val))
            return (val, PromptResolutionLevel.GlobalDefault, key5);

        // Level 6: Hardcoded fallback
        return (HardcodedFallbackPrompt, PromptResolutionLevel.HardcodedFallback, "(hardcoded)");
    }

    private const string HardcodedFallbackPrompt =
        "You are Tamma, an AI-powered development assistant. " +
        "Provide clear, accurate, and helpful responses. " +
        "Focus on actionable guidance and best practices. " +
        "Be concise but thorough.";
}
