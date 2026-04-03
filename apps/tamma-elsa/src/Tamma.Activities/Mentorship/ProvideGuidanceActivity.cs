using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Mentorship;

/// <summary>
/// ELSA activity to provide contextual guidance to junior developers.
/// Generates personalized hints, explanations, and resources based on the blocker type.
/// </summary>
[Activity(
    "Tamma.Mentorship",
    "Provide Guidance",
    "Generate and deliver contextual guidance to help junior developer progress",
    Kind = ActivityKind.Task
)]
public class ProvideGuidanceActivity : CodeActivity<GuidanceOutput>
{
    private readonly ILogger<ProvideGuidanceActivity>? _logger;
    private readonly IMentorshipSessionRepository? _repository;
    private readonly IIntegrationService? _integrationService;
    private readonly IAnalyticsService? _analyticsService;

    /// <summary>Mentorship session ID</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>ID of the junior developer</summary>
    [Input(Description = "ID of the junior developer")]
    public Input<string> JuniorId { get; set; } = default!;

    /// <summary>ID of the story being worked on</summary>
    [Input(Description = "ID of the story being worked on")]
    public Input<string> StoryId { get; set; } = default!;

    /// <summary>Type of blocker to provide guidance for</summary>
    [Input(Description = "Type of blocker")]
    public Input<BlockerType> BlockerType { get; set; } = default!;

    /// <summary>Specific context about the issue</summary>
    [Input(Description = "Specific issue context")]
    public Input<string?> IssueContext { get; set; } = default!;

    /// <summary>Level of guidance - hint (1), guidance (2), or assistance (3)</summary>
    [Input(Description = "Guidance level: 1=hint, 2=guidance, 3=assistance", DefaultValue = 2)]
    public Input<int> GuidanceLevel { get; set; } = new(2);

    [JsonConstructor]
    public ProvideGuidanceActivity() { }

    public ProvideGuidanceActivity(
        ILogger<ProvideGuidanceActivity> logger,
        IMentorshipSessionRepository repository,
        IIntegrationService integrationService,
        IAnalyticsService analyticsService)
    {
        _logger = logger;
        _repository = repository;
        _integrationService = integrationService;
        _analyticsService = analyticsService;
    }

    /// <summary>
    /// Execute the guidance provision activity
    /// </summary>
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var juniorId = JuniorId.Get(context);
        var storyId = StoryId.Get(context);
        var blockerType = BlockerType.Get(context);
        var issueContext = IssueContext.Get(context);
        var guidanceLevel = GuidanceLevel.Get(context);

        _logger?.LogInformation(
            "Providing guidance for junior {JuniorId} on blocker type {BlockerType}",
            juniorId, blockerType);

        try
        {
            // Update session state based on guidance level
            var newState = guidanceLevel switch
            {
                1 => MentorshipState.PROVIDE_HINT,
                2 => MentorshipState.PROVIDE_GUIDANCE,
                3 => MentorshipState.PROVIDE_ASSISTANCE,
                _ => MentorshipState.PROVIDE_GUIDANCE
            };
            await _repository!.UpdateStateAsync(sessionId, newState);

            // Get junior information for personalization
            var junior = await _repository.GetJuniorByIdAsync(juniorId);
            var story = await _repository.GetStoryByIdAsync(storyId);

            if (junior == null || story == null)
            {
                context.SetResult(new GuidanceOutput
                {
                    Success = false,
                    GuidanceProvided = "Unable to provide guidance - missing context",
                    NextState = MentorshipState.ESCALATE_TO_SENIOR
                });
                return;
            }

            // Generate appropriate guidance based on blocker type and level
            var guidance = GenerateGuidance(blockerType, guidanceLevel, junior.SkillLevel, issueContext, story);

            // Get relevant resources
            var resources = GetRelevantResources(blockerType, story);

            // Log the guidance event
            await _repository.LogEventAsync(new Tamma.Core.Entities.MentorshipEvent
            {
                SessionId = sessionId,
                EventType = Tamma.Core.Entities.EventTypes.GuidanceProvided,
                StateFrom = MentorshipState.DIAGNOSE_BLOCKER,
                StateTo = newState
            });

            // Record analytics
            await _analyticsService!.RecordMetricAsync(
                sessionId,
                "guidance_provided",
                guidanceLevel,
                "level");

            // Deliver guidance via appropriate channel
            if (!string.IsNullOrEmpty(junior.SlackId))
            {
                await DeliverGuidanceViaSlack(junior.SlackId, guidance, resources, guidanceLevel);
            }

            _logger?.LogInformation(
                "Guidance delivered to junior {JuniorId} at level {Level}",
                juniorId, guidanceLevel);

            var output = new GuidanceOutput
            {
                Success = true,
                GuidanceProvided = guidance.MainGuidance,
                GuidanceLevel = guidanceLevel,
                Examples = guidance.Examples,
                Resources = resources,
                NextSteps = guidance.NextSteps,
                NextState = guidanceLevel < 3
                    ? MentorshipState.MONITOR_PROGRESS
                    : MentorshipState.START_IMPLEMENTATION
            };

            context.SetResult(output);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error providing guidance for session {SessionId}", sessionId);

            context.SetResult(new GuidanceOutput
            {
                Success = false,
                GuidanceProvided = $"Error generating guidance: {ex.Message}",
                NextState = MentorshipState.ESCALATE_TO_SENIOR
            });
        }
    }

    private GuidanceContent GenerateGuidance(
        BlockerType blockerType,
        int level,
        int skillLevel,
        string? issueContext,
        Tamma.Core.Entities.Story story)
    {
        var content = new GuidanceContent();

        // Base guidance generation by blocker type
        switch (blockerType)
        {
            case Tamma.Core.Enums.BlockerType.REQUIREMENTS_UNCLEAR:
                content = GenerateRequirementsGuidance(level, story, issueContext);
                break;

            case Tamma.Core.Enums.BlockerType.TECHNICAL_KNOWLEDGE_GAP:
                content = GenerateTechnicalGuidance(level, skillLevel, issueContext);
                break;

            case Tamma.Core.Enums.BlockerType.ENVIRONMENT_ISSUE:
                content = GenerateEnvironmentGuidance(level, issueContext);
                break;

            case Tamma.Core.Enums.BlockerType.TESTING_CHALLENGE:
                content = GenerateTestingGuidance(level, issueContext);
                break;

            case Tamma.Core.Enums.BlockerType.ARCHITECTURE_CONFUSION:
                content = GenerateArchitectureGuidance(level, story, issueContext);
                break;

            case Tamma.Core.Enums.BlockerType.DEPENDENCY_ISSUE:
                content = GenerateDependencyGuidance(level, issueContext);
                break;

            case Tamma.Core.Enums.BlockerType.MOTIVATION_ISSUE:
                content = GenerateMotivationGuidance(level, skillLevel);
                break;

            default:
                content = GenerateDefaultGuidance(level, issueContext);
                break;
        }

        return content;
    }

    private GuidanceContent GenerateRequirementsGuidance(int level, Tamma.Core.Entities.Story story, string? context)
    {
        var content = new GuidanceContent();

        if (level == 1) // Hint
        {
            content.MainGuidance = "Take another look at the acceptance criteria. What's the first user action that needs to work?";
            content.NextSteps = new List<string>
            {
                "Re-read the story description",
                "List out what the user should be able to do",
                "Identify the simplest test case"
            };
        }
        else if (level == 2) // Guidance
        {
            content.MainGuidance = $"Let's break down the story '{story.Title}' step by step. The core requirement is about enabling a specific user capability.";
            content.Examples = new List<string>
            {
                "If the story says 'User can log in', think: What inputs? What outputs? What errors?",
                "Map each acceptance criterion to a specific function or component"
            };
            content.NextSteps = new List<string>
            {
                "Create a simple flowchart of user interactions",
                "Write pseudo-code for the happy path first",
                "Ask: What's the minimum viable implementation?"
            };
        }
        else // Assistance
        {
            content.MainGuidance = $"Here's how I'd approach '{story.Title}': Start with the primary user flow, then add edge cases.";
            content.Examples = new List<string>
            {
                $"For this story, your entry point should be: {GuessEntryPoint(story)}",
                "The data flow typically goes: User Input -> Validation -> Business Logic -> Response"
            };
            content.NextSteps = new List<string>
            {
                "Implement the happy path first (ignore errors for now)",
                "Add one test that proves the happy path works",
                "Then add error handling incrementally"
            };
        }

        return content;
    }

    private GuidanceContent GenerateTechnicalGuidance(int level, int skillLevel, string? context)
    {
        var content = new GuidanceContent();

        if (level == 1)
        {
            content.MainGuidance = "Think about what concept you're trying to implement. Have you seen something similar before?";
            content.NextSteps = new List<string>
            {
                "Search for similar patterns in the existing codebase",
                "Look up the core concept in documentation"
            };
        }
        else if (level == 2)
        {
            content.MainGuidance = context?.Contains("async") == true
                ? "Async programming can be tricky. Remember: async methods return Tasks, and you await them to get the result."
                : "When learning a new technical concept, start with the simplest example and build up.";
            content.Examples = new List<string>
            {
                "Find 3 examples of this pattern in production code",
                "Create a minimal test case that isolates the concept"
            };
            content.NextSteps = new List<string>
            {
                "Break the problem into smaller pieces",
                "Solve each piece independently",
                "Combine them once each works"
            };
        }
        else
        {
            content.MainGuidance = "Let's pair on this. Here's a step-by-step approach to implement this feature.";
            content.Examples = new List<string>
            {
                "Step 1: Define the interface/contract",
                "Step 2: Write a failing test",
                "Step 3: Implement the minimum to pass",
                "Step 4: Refactor and improve"
            };
            content.NextSteps = new List<string>
            {
                "Start with Step 1 - what should this function/class accept and return?",
                "Reply when you have questions about any step"
            };
        }

        return content;
    }

    private GuidanceContent GenerateEnvironmentGuidance(int level, string? context)
    {
        var content = new GuidanceContent
        {
            MainGuidance = level switch
            {
                1 => "Check if your development environment matches the project requirements.",
                2 => "Environment issues are common. Let's verify your setup: Node version, dependencies, environment variables.",
                _ => "Let's debug your environment together. Run these diagnostic commands and share the output."
            }
        };

        content.NextSteps = new List<string>
        {
            "Verify required tools are installed (node -v, dotnet --version, etc.)",
            "Check if .env file exists and has required variables",
            "Try 'npm ci' or 'dotnet restore' to refresh dependencies",
            "Check for any error logs in console output"
        };

        return content;
    }

    private GuidanceContent GenerateTestingGuidance(int level, string? context)
    {
        var content = new GuidanceContent();

        if (level == 1)
        {
            content.MainGuidance = "Read the test name carefully - it tells you exactly what behavior is expected.";
        }
        else if (level == 2)
        {
            content.MainGuidance = "Testing tip: The test is a specification. Expected vs Actual tells you what's wrong.";
            content.Examples = new List<string>
            {
                "If Expected: 5, Actual: null - something isn't returning a value",
                "If Expected: true, Actual: false - your condition logic needs review"
            };
        }
        else
        {
            content.MainGuidance = "Let's debug this test together. First, add console.log/Debug.WriteLine to trace the flow.";
            content.Examples = new List<string>
            {
                "Log the input values at the start of your function",
                "Log intermediate calculations",
                "Log the final result before returning"
            };
        }

        content.NextSteps = new List<string>
        {
            "Run just the failing test in isolation",
            "Add debug output to trace execution",
            "Compare expected behavior with actual behavior",
            "Fix one assertion at a time"
        };

        return content;
    }

    private GuidanceContent GenerateArchitectureGuidance(int level, Tamma.Core.Entities.Story story, string? context)
    {
        var content = new GuidanceContent
        {
            MainGuidance = level switch
            {
                1 => "Look at how similar features are structured in the codebase.",
                2 => "Good architecture follows patterns. Find a similar feature and use it as a template.",
                _ => "For this feature, I recommend: Controller -> Service -> Repository pattern. Let's map it out."
            }
        };

        content.Examples = new List<string>
        {
            "Controllers handle HTTP requests and responses",
            "Services contain business logic",
            "Repositories handle data access",
            "Keep each layer focused on its responsibility"
        };

        content.NextSteps = new List<string>
        {
            "Identify which layer you're working in",
            "Find an existing example of that pattern",
            "Follow the same structure for your implementation"
        };

        return content;
    }

    private GuidanceContent GenerateDependencyGuidance(int level, string? context)
    {
        return new GuidanceContent
        {
            MainGuidance = "Dependency issues usually come from version mismatches or missing packages.",
            Examples = new List<string>
            {
                "Check package.json or .csproj for version requirements",
                "Look for peer dependency warnings",
                "Sometimes deleting node_modules and reinstalling helps"
            },
            NextSteps = new List<string>
            {
                "Run 'npm ls' or 'dotnet list package' to see dependencies",
                "Check for version conflicts in error messages",
                "Try clearing cache and reinstalling"
            }
        };
    }

    private GuidanceContent GenerateMotivationGuidance(int level, int skillLevel)
    {
        return new GuidanceContent
        {
            MainGuidance = "Getting stuck is a normal part of development - every senior developer has been there!",
            Examples = new List<string>
            {
                "Even experts spend time debugging and researching",
                "Taking a short break can help you see the problem differently",
                "Asking for help is a sign of good judgment, not weakness"
            },
            NextSteps = new List<string>
            {
                "Take a 10-minute break if you've been stuck for a while",
                "Explain the problem out loud (rubber duck debugging)",
                "Break the problem into the smallest possible piece",
                "Celebrate small wins - each step forward matters"
            }
        };
    }

    private GuidanceContent GenerateDefaultGuidance(int level, string? context)
    {
        return new GuidanceContent
        {
            MainGuidance = "Let's approach this systematically. What's the specific challenge you're facing?",
            NextSteps = new List<string>
            {
                "Describe the problem in one sentence",
                "What have you already tried?",
                "What behavior did you expect vs what happened?"
            }
        };
    }

    private string GuessEntryPoint(Tamma.Core.Entities.Story story)
    {
        var title = story.Title?.ToLower() ?? "";
        if (title.Contains("api") || title.Contains("endpoint"))
            return "Create a new controller/route handler";
        if (title.Contains("ui") || title.Contains("component"))
            return "Create a new React/UI component";
        if (title.Contains("test"))
            return "Start with a test file in the tests directory";
        return "Start with the main feature file";
    }

    private List<Resource> GetRelevantResources(BlockerType blockerType, Tamma.Core.Entities.Story story)
    {
        var resources = new List<Resource>();

        switch (blockerType)
        {
            case Tamma.Core.Enums.BlockerType.TECHNICAL_KNOWLEDGE_GAP:
                resources.Add(new Resource
                {
                    Title = "Team Knowledge Base",
                    Url = "https://wiki.internal/tech-guides",
                    Type = ResourceType.Documentation
                });
                break;

            case Tamma.Core.Enums.BlockerType.TESTING_CHALLENGE:
                resources.Add(new Resource
                {
                    Title = "Testing Best Practices",
                    Url = "https://wiki.internal/testing-guide",
                    Type = ResourceType.Guide
                });
                break;

            case Tamma.Core.Enums.BlockerType.ARCHITECTURE_CONFUSION:
                resources.Add(new Resource
                {
                    Title = "Architecture Decision Records",
                    Url = "https://wiki.internal/adr",
                    Type = ResourceType.Documentation
                });
                break;
        }

        // Add general resources
        resources.Add(new Resource
        {
            Title = "Project README",
            Url = story.RepositoryUrl ?? "https://github.com/org/repo",
            Type = ResourceType.CodeExample
        });

        return resources;
    }

    private async Task DeliverGuidanceViaSlack(
        string slackId,
        GuidanceContent guidance,
        List<Resource> resources,
        int level)
    {
        var levelEmoji = level switch
        {
            1 => "lightbulb",
            2 => "books",
            3 => "handshake",
            _ => "sparkles"
        };

        var message = $@"**Tamma Guidance** :{levelEmoji}:

{guidance.MainGuidance}";

        if (guidance.Examples.Any())
        {
            message += "\n\n*Examples:*\n" + string.Join("\n", guidance.Examples.Select(e => $"- {e}"));
        }

        if (guidance.NextSteps.Any())
        {
            message += "\n\n*Next Steps:*\n" + string.Join("\n", guidance.NextSteps.Select((s, i) => $"{i + 1}. {s}"));
        }

        if (resources.Any())
        {
            message += "\n\n*Helpful Resources:*\n" + string.Join("\n", resources.Select(r => $"- [{r.Title}]({r.Url})"));
        }

        message += "\n\nReply if you need more help!";

        await _integrationService!.SendSlackDirectMessageAsync(slackId, message);
    }
}

/// <summary>
/// Generated guidance content
/// </summary>
public class GuidanceContent
{
    public string MainGuidance { get; set; } = string.Empty;
    public List<string> Examples { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();
}

/// <summary>
/// Resource types
/// </summary>
public enum ResourceType
{
    Documentation,
    Guide,
    Video,
    CodeExample,
    Tool
}

/// <summary>
/// Learning resource
/// </summary>
public class Resource
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public ResourceType Type { get; set; }
}

/// <summary>
/// Output model for guidance activity
/// </summary>
public class GuidanceOutput
{
    public bool Success { get; set; }
    public string GuidanceProvided { get; set; } = string.Empty;
    public int GuidanceLevel { get; set; }
    public List<string> Examples { get; set; } = new();
    public List<Resource> Resources { get; set; } = new();
    public List<string> NextSteps { get; set; } = new();
    public MentorshipState NextState { get; set; }
}
