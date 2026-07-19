using System.Text.Json;
using Elsa.Agents;
using Elsa.Agents.Persistence.Contracts;
using Elsa.Agents.Persistence.Filters;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.Security;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// Resolves agent configuration (system prompt, provider chain, execution settings) from the
/// ELSA Agents DB store. Falls back to hardcoded defaults if the agent is not found in the DB.
///
/// This activity replaces the hardcoded GetRolePrompt() approach, enabling prompt management
/// via ELSA Studio UI or Tamma Dashboard without code changes or redeployment.
///
/// Resolution order:
///   1. Caller-provided systemPromptOverride (highest priority)
///   2. DB lookup via IAgentManager for "tamma-{role}"
///   3. Hardcoded fallback prompts (lowest priority)
///
/// Sets workflow variables: ResolvedSystemPrompt, ProviderChain (if DB provides one).
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Resolve Agent Config",
    "Resolve agent prompt and settings from ELSA Agents DB store",
    Kind = ActivityKind.Task
)]
public class ResolveAgentConfigActivity : CodeActivity
{
    [Input(Description = "Agent role (e.g. 'analyst', 'implementer', 'reviewer')")]
    public Input<string> AgentRoleProp { get; set; } = default!;

    [Input(Description = "Optional caller-provided system prompt override", UIHint = "multiline")]
    public Input<string?> SystemPromptOverrideProp { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var logger = context.GetRequiredService<ILogger<ResolveAgentConfigActivity>>();
        var role = AgentRoleProp.Get(context) ?? "assistant";
        var systemPromptOverride = SystemPromptOverrideProp.Get(context);

        // Priority 1: Caller override
        if (!string.IsNullOrWhiteSpace(systemPromptOverride))
        {
            logger.LogDebug("Using caller-provided system prompt override for role '{Role}'", role);
            // Sanitize untrusted override input, then harden
            var sanitizedOverride = SecurityHelpers.SanitizeForPrompt(systemPromptOverride);
            context.SetVariable("ResolvedSystemPrompt", PromptHardening.Harden(sanitizedOverride));
            return;
        }

        // Priority 2a (Story 9-11): Tamma API resolver — if TammaApiClient is
        // registered in DI we consult the central API plane first. On any
        // failure we fall through to the legacy DB lookup (AC 5 backward
        // compat). Pass X-Tenant-Id when a tenant context is carried in
        // workflow variables so resolution is tenant-scoped.
        var apiClient = context.GetService<TammaApiClient>();
        if (apiClient is not null)
        {
            try
            {
                var tenantId = context.GetVariable<string>("TenantId");
                var resolved = await apiClient
                    .ResolveAgentAsync(role, tenantId, context.CancellationToken)
                    .ConfigureAwait(false);
                if (resolved is not null && !string.IsNullOrWhiteSpace(resolved.SystemPrompt))
                {
                    context.SetVariable("ResolvedSystemPrompt",
                        PromptHardening.Harden(resolved.SystemPrompt));
                    if (!string.IsNullOrWhiteSpace(resolved.Provider))
                    {
                        context.SetVariable("ResolvedProvider", resolved.Provider);
                    }
                    if (!string.IsNullOrWhiteSpace(resolved.Model))
                    {
                        context.SetVariable("ResolvedModel", resolved.Model);
                    }
                    logger.LogInformation(
                        "Resolved agent config via Tamma API for role '{Role}': provider={Provider}, model={Model}, source={Source}",
                        role, resolved.Provider, resolved.Model, resolved.Source ?? "api");
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Tamma API agent resolve failed for role '{Role}', falling back to DB", role);
            }
        }

        // Priority 2b: DB lookup (legacy path, backward-compatible fallback)
        try
        {
            var agentManager = context.GetRequiredService<IAgentManager>();
            var agentName = $"tamma-{role.ToLowerInvariant()}";
            var filter = new AgentDefinitionFilter { Name = agentName };
            var agent = await agentManager.FindAsync(filter, context.CancellationToken);

            if (agent?.AgentConfig != null)
            {
                var config = agent.AgentConfig;

                // Set resolved system prompt from DB (hardened against extraction)
                context.SetVariable("ResolvedSystemPrompt", PromptHardening.Harden(config.PromptTemplate ?? ""));

                // Parse custom settings from ResponseFormat (provider chain, budget)
                var customSettings = ParseCustomSettings(config.ExecutionSettings.ResponseFormat);
                if (customSettings?.ProviderChain is { Count: > 0 })
                {
                    context.SetVariable("ProviderChain", (object)customSettings.ProviderChain);
                }

                logger.LogInformation(
                    "Resolved agent config from DB for '{AgentName}': prompt={PromptLength}chars, chain={Chain}",
                    agentName,
                    config.PromptTemplate?.Length ?? 0,
                    customSettings?.ProviderChain != null
                        ? string.Join(",", customSettings.ProviderChain)
                        : "(default)");
                return;
            }

            logger.LogDebug(
                "Agent '{AgentName}' not found in DB, falling back to hardcoded prompt", agentName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to resolve agent config from DB for role '{Role}', using fallback", role);
        }

        // Priority 3: Hardcoded fallback (hardened against extraction)
        context.SetVariable("ResolvedSystemPrompt", PromptHardening.Harden(GetFallbackPrompt(role)));
    }

    private static AgentCustomSettings? ParseCustomSettings(string? responseFormat)
    {
        if (string.IsNullOrWhiteSpace(responseFormat))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentCustomSettings>(responseFormat,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Hardcoded fallback prompts — used only when the agent is not found in the DB.
    /// These match the original GetRolePrompt() defaults from LlmCallWorkflow.
    /// </summary>
    internal static string GetFallbackPrompt(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "mentor" => "You are an experienced software development mentor guiding a junior developer. " +
                        "Provide encouraging, educational explanations, using Socratic questioning when appropriate.",
            "analyst" => "You are a technical analyst specializing in software development. " +
                         "Analyze code, diagnose issues, and provide structured, evidence-based assessments.",
            "implementer" => "You are an expert software developer. " +
                            "Write clean, well-tested, production-quality code that follows established patterns and conventions.",
            "reviewer" => "You are an expert code reviewer. " +
                         "Identify bugs, security issues, performance problems, and style violations with specific, actionable feedback.",
            _ => "You are Tamma, an AI-powered development assistant. " +
                 "Provide clear, accurate, and helpful responses."
        };
    }
}
