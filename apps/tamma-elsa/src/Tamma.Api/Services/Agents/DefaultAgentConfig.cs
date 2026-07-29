using System.Collections.Frozen;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Platform-default agent configuration per role.
///
/// Ported from the deleted <c>packages/api/src/services/settings/ConfigService.ts</c>
/// <c>DEFAULT_CONFIG</c> + <c>packages/providers/src/role-based-agent-resolver.ts</c>
/// defaults. These defaults kick in when a tenant has no override, and also
/// fill gaps in partial overrides.
/// </summary>
public static class DefaultAgentConfig
{
    // -----------------------------------------------------------------------
    // Defaults shared across all roles (fallback when per-role absent)
    // -----------------------------------------------------------------------

    /// <summary>Primary provider identifier (per Story 9-8 spec).</summary>
    public const string DefaultProvider = "claude-code";

    /// <summary>Primary model identifier. Updated to match current Anthropic SKU.</summary>
    public const string DefaultModel = "claude-sonnet-4-20250514";

    /// <summary>Default max_tokens for an LLM call.</summary>
    public const int DefaultMaxTokens = 4096;

    /// <summary>Default token budget per role (context + completion ceiling).</summary>
    public const int DefaultTokenBudget = 16000;

    /// <summary>Default temperature (deterministic-ish).</summary>
    public const double DefaultTemperature = 0.2;

    /// <summary>Default tools enabled for a role (empty = provider-default).</summary>
    public static readonly IReadOnlyList<string> DefaultTools = Array.Empty<string>();

    // -----------------------------------------------------------------------
    // Per-role overrides of the platform defaults above
    // -----------------------------------------------------------------------

    private static readonly FrozenDictionary<string, ResolvedAgentConfig> s_perRole =
        new Dictionary<string, ResolvedAgentConfig>
        {
            ["developer"] = new()
            {
                Role = "developer",
                Handle = "tamma-developer",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.2,
                MaxTokens = 4096,
                TokenBudget = 16000,
                Tools = new[] { "Read", "Write", "Edit", "Bash", "Grep", "Glob" },
                SystemPrompt =
                    "You are an expert software developer working on the Tamma project. " +
                    "You write production-quality TypeScript/C# code that passes strict " +
                    "compilation, follows established conventions, and includes proper " +
                    "error handling.",
                Source = "platform-default",
            },
            ["tester"] = new()
            {
                Role = "tester",
                Handle = "tamma-tester",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.2,
                MaxTokens = 4096,
                TokenBudget = 16000,
                Tools = new[] { "Read", "Write", "Edit", "Bash", "Grep", "Glob" },
                SystemPrompt =
                    "You are a testing specialist for the Tamma project. You write thorough, " +
                    "maintainable tests using Vitest / NUnit with colocated test files.",
                Source = "platform-default",
            },
            ["security"] = new()
            {
                Role = "security",
                Handle = "tamma-security",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.1,
                MaxTokens = 4096,
                TokenBudget = 16000,
                Tools = new[] { "Read", "Grep", "Glob" },
                SystemPrompt =
                    "You are a security engineer specializing in application security for " +
                    "TypeScript/Node.js / .NET systems. You identify vulnerabilities (OWASP " +
                    "Top 10), review code for injection attacks, credential leaks, and " +
                    "insecure configurations.",
                Source = "platform-default",
            },
            ["devops"] = new()
            {
                Role = "devops",
                Handle = "tamma-devops",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.2,
                MaxTokens = 4096,
                TokenBudget = 16000,
                Tools = new[] { "Read", "Write", "Edit", "Bash", "Grep", "Glob" },
                SystemPrompt =
                    "You are a DevOps engineer specializing in CI/CD pipelines, Docker " +
                    "containerization, Kubernetes orchestration, and infrastructure " +
                    "automation for the Tamma platform.",
                Source = "platform-default",
            },
            ["architect"] = new()
            {
                Role = "architect",
                Handle = "tamma-architect",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.3,
                MaxTokens = 8192,
                TokenBudget = 32000,
                Tools = new[] { "Read", "Grep", "Glob" },
                SystemPrompt =
                    "You are a software architect specializing in distributed systems, " +
                    "microservices, and event-driven architectures. You have deep knowledge " +
                    "of DDD, CQRS, event sourcing, and the Tamma DCB pattern.",
                Source = "platform-default",
            },
            ["product_owner"] = new()
            {
                Role = "product_owner",
                Handle = "tamma-product-owner",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.4,
                MaxTokens = 4096,
                TokenBudget = 16000,
                Tools = Array.Empty<string>(),
                SystemPrompt =
                    "You are a product owner with expertise in agile development, user " +
                    "story management, and feature prioritization.",
                Source = "platform-default",
            },
            ["senior_developer"] = new()
            {
                Role = "senior_developer",
                Handle = "tamma-senior-developer",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.2,
                MaxTokens = 8192,
                TokenBudget = 32000,
                Tools = new[] { "Read", "Write", "Edit", "Bash", "Grep", "Glob" },
                SystemPrompt =
                    "You are a senior developer and technical lead on the Tamma project. " +
                    "You create detailed implementation plans, decompose complex tasks, " +
                    "and make technology decisions.",
                Source = "platform-default",
            },
            ["tech_writer"] = new()
            {
                Role = "tech_writer",
                Handle = "tamma-tech-writer",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.3,
                MaxTokens = 4096,
                TokenBudget = 16000,
                Tools = new[] { "Read", "Grep", "Glob" },
                SystemPrompt =
                    "You are a technical writer who produces clear, concise documentation " +
                    "for developer audiences.",
                Source = "platform-default",
            },
            // Story 41-1a (C2/D7) — every AgentRole member MUST have a row here:
            // ForRole asserts the role then indexes raw, so a missing row is a
            // KeyNotFoundException from AgentResolverService. The three Epic 41
            // roles clone the product_owner row's shape (planning/prose roles,
            // no code tools).
            ["scrum_master"] = new()
            {
                Role = "scrum_master",
                Handle = "tamma-scrum-master",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.3,
                MaxTokens = 4096,
                TokenBudget = 16000,
                Tools = Array.Empty<string>(),
                SystemPrompt =
                    "You are a scrum master facilitating agile delivery: sprint planning, " +
                    "standups, retrospectives, and impediment tracking.",
                Source = "platform-default",
            },
            ["project_manager"] = new()
            {
                Role = "project_manager",
                Handle = "tamma-project-manager",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.3,
                MaxTokens = 4096,
                TokenBudget = 16000,
                Tools = Array.Empty<string>(),
                SystemPrompt =
                    "You are a project manager coordinating delivery across teams: status " +
                    "reporting against commitments and release coordination.",
                Source = "platform-default",
            },
            ["ux_designer"] = new()
            {
                Role = "ux_designer",
                Handle = "tamma-ux-designer",
                Provider = DefaultProvider,
                Model = DefaultModel,
                Temperature = 0.4,
                MaxTokens = 4096,
                TokenBudget = 16000,
                Tools = Array.Empty<string>(),
                SystemPrompt =
                    "You are a UX and visual designer producing user flows, structured UI " +
                    "specifications, design reviews, and accessibility audits.",
                Source = "platform-default",
            },
        }.ToFrozenDictionary();

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Get the platform default <see cref="ResolvedAgentConfig"/> for a role.
    /// Returns a fresh copy so callers can mutate without affecting the
    /// frozen source.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if the role is unknown or forbidden.
    /// </exception>
    public static ResolvedAgentConfig ForRole(string role)
    {
        RolePhaseMap.AssertValidRole(role);
        var frozen = s_perRole[role];
        return new ResolvedAgentConfig
        {
            Role = frozen.Role,
            Handle = frozen.Handle,
            Provider = frozen.Provider,
            Model = frozen.Model,
            Temperature = frozen.Temperature,
            MaxTokens = frozen.MaxTokens,
            TokenBudget = frozen.TokenBudget,
            Tools = frozen.Tools.ToArray(),
            SystemPrompt = frozen.SystemPrompt,
            Source = frozen.Source,
        };
    }
}
