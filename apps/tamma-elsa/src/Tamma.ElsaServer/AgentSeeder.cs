using System.Text.Json;
using Elsa.Agents;
using Elsa.Agents.Persistence.Contracts;
using Elsa.Agents.Persistence.Entities;
using Elsa.Agents.Persistence.Filters;

namespace Tamma.ElsaServer;

/// <summary>
/// Seeds default Tamma agent definitions into the ELSA Agents store on startup.
/// Idempotent — skips agents whose name already exists. Each agent maps to a role
/// used by the llm-call workflow (e.g. "analyst" → "tamma-analyst").
/// </summary>
public class AgentSeeder : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AgentSeeder> _logger;

    public AgentSeeder(IServiceProvider serviceProvider, ILogger<AgentSeeder> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait briefly for ELSA persistence to initialize (migrations, etc.)
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var agentManager = scope.ServiceProvider.GetRequiredService<IAgentManager>();

            var seeded = 0;
            var skipped = 0;

            foreach (var definition in GetDefaultAgents())
            {
                try
                {
                    var existing = await agentManager.FindAsync(
                        new AgentDefinitionFilter { Name = definition.Name }, ct);

                    if (existing != null)
                    {
                        skipped++;
                        continue;
                    }

                    await agentManager.AddAsync(definition, ct);
                    seeded++;
                    _logger.LogInformation("Seeded agent '{Name}'", definition.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to seed agent '{Name}'", definition.Name);
                }
            }

            _logger.LogInformation(
                "Agent seeding complete: {Seeded} created, {Skipped} already existed",
                seeded, skipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent seeding failed");
        }
    }

    private static IReadOnlyList<AgentDefinition> GetDefaultAgents()
    {
        return
        [
            CreateAgent(
                name: "tamma-analyst",
                description: "Technical analysis, diagnostics, blocker assessment",
                prompt: """
                    You are a technical analyst specializing in software development.
                    Analyze code, diagnose issues, and provide structured assessments.
                    Be precise and evidence-based. When diagnosing problems, enumerate
                    root causes with confidence levels. Provide actionable next steps.
                    """,
                temperature: 0.3,
                maxTokens: 4096,
                providerChain: ["anthropic", "openai", "openrouter"]),

            CreateAgent(
                name: "tamma-implementer",
                description: "Expert developer for code generation and fixes",
                prompt: """
                    You are an expert software developer. Write clean, well-tested,
                    production-quality code. Follow established patterns and conventions
                    in the codebase. Include necessary imports, error handling, and tests.
                    Prefer small, focused changes over sweeping rewrites.
                    """,
                temperature: 0.4,
                maxTokens: 4096,
                providerChain: ["anthropic", "openai", "openrouter"]),

            CreateAgent(
                name: "tamma-reviewer",
                description: "Code reviewer for bugs, security, and style",
                prompt: """
                    You are an expert code reviewer. Identify bugs, security issues,
                    performance problems, and style violations. Provide specific,
                    actionable feedback with line references. Rate severity of each
                    finding (critical, major, minor, suggestion). Be thorough but fair.
                    """,
                temperature: 0.2,
                maxTokens: 4096,
                providerChain: ["anthropic", "openai", "openrouter"]),

            CreateAgent(
                name: "tamma-mentor",
                description: "Experienced mentor for developer guidance",
                prompt: """
                    You are an experienced software development mentor guiding a
                    developer. Provide encouraging, educational explanations. Use
                    Socratic questioning when appropriate. Help the developer build
                    understanding rather than just giving answers. Connect concepts
                    to broader engineering principles.
                    """,
                temperature: 0.5,
                maxTokens: 4096,
                providerChain: ["anthropic", "openai", "openrouter"]),

            CreateAgent(
                name: "tamma-architect",
                description: "Software architect for system design decisions",
                prompt: """
                    You are a software architect specializing in system design.
                    Evaluate trade-offs between approaches, considering scalability,
                    maintainability, and operational complexity. Provide architecture
                    decision records with context, options, and rationale.
                    """,
                temperature: 0.3,
                maxTokens: 4096,
                providerChain: ["anthropic", "openai", "openrouter"]),

            CreateAgent(
                name: "tamma-scrum-master",
                description: "Scrum master for triage and coordination",
                prompt: """
                    You are a scrum master experienced in agile software development.
                    Help triage issues, estimate complexity, identify blockers, and
                    coordinate work. Break large tasks into well-scoped stories.
                    Focus on unblocking the team and maintaining delivery velocity.
                    """,
                temperature: 0.4,
                maxTokens: 4096,
                providerChain: ["anthropic", "openai", "openrouter"]),

            CreateAgent(
                name: "tamma-researcher",
                description: "Technical researcher for context gathering",
                prompt: """
                    You are a technical researcher. Gather and synthesize relevant
                    context from codebases, documentation, and issue trackers.
                    Provide structured summaries with key findings, relevant code
                    references, and identified knowledge gaps.
                    """,
                temperature: 0.3,
                maxTokens: 4096,
                providerChain: ["anthropic", "openai", "openrouter"]),

            CreateAgent(
                name: "tamma-tester",
                description: "QA engineer for test strategy and generation",
                prompt: """
                    You are a QA engineer specializing in test strategy. Design
                    comprehensive test plans covering unit, integration, and edge
                    cases. Generate test code that is maintainable and provides
                    high confidence. Focus on testing behavior, not implementation.
                    """,
                temperature: 0.3,
                maxTokens: 4096,
                providerChain: ["anthropic", "openai", "openrouter"]),

            CreateAgent(
                name: "tamma-documenter",
                description: "Technical writer for documentation",
                prompt: """
                    You are a technical writer producing clear, accurate documentation.
                    Write for the target audience (developers, operators, or end users).
                    Include code examples, diagrams where helpful, and keep content
                    up to date with the codebase. Prefer concise explanations.
                    """,
                temperature: 0.4,
                maxTokens: 4096,
                providerChain: ["anthropic", "openai", "openrouter"]),
        ];
    }

    private static AgentDefinition CreateAgent(
        string name,
        string description,
        string prompt,
        double temperature,
        int maxTokens,
        string[] providerChain)
    {
        var customSettings = new { providerChain, maxBudgetUsd = 10.0 };

        return new AgentDefinition
        {
            Name = name,
            Description = description,
            AgentConfig = new AgentConfig
            {
                Name = name,
                Description = description,
                PromptTemplate = prompt.Trim(),
                InputVariables =
                [
                    new InputVariableConfig { Name = "taskPrompt", Description = "User prompt content", Type = "string" },
                    new InputVariableConfig { Name = "context", Description = "Serialized context object", Type = "string" }
                ],
                OutputVariable = new OutputVariableConfig { Description = "LLM response text", Type = "string" },
                ExecutionSettings = new ExecutionSettingsConfig
                {
                    Temperature = temperature,
                    MaxTokens = maxTokens,
                    // Store provider chain config in ResponseFormat as JSON
                    ResponseFormat = JsonSerializer.Serialize(customSettings)
                }
            }
        };
    }
}
