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
/// Resolves tool definitions for the LLM call based on requested tool names.
/// Looks up tool schemas from configuration ("LlmTools:{toolName}") and returns
/// the normalized list. Unknown tools are logged and skipped.
/// </summary>
[Activity(
    "Tamma.LlmCall",
    "Resolve Tools",
    "Resolve tool/function definitions for LLM tool-calling support",
    Kind = ActivityKind.Task
)]
public class ResolveToolsActivity : CodeActivity<List<ResolvedTool>>
{
    private readonly ILogger<ResolveToolsActivity> _logger;
    private readonly IConfiguration _configuration;

    /// <summary>Comma-separated tool names, or JSON array of strings.</summary>
    [Input(Description = "Tool names to resolve (comma-separated or JSON array)")]
    public Input<string?> ToolNamesInput { get; set; } = default!;

    /// <summary>Provider name (some tools may be provider-specific).</summary>
    [Input(Description = "Provider name for provider-specific tool resolution")]
    public Input<string> ProviderName { get; set; } = default!;

    [JsonConstructor]
    public ResolveToolsActivity() : this(null!, null!)
    {
    }

    public ResolveToolsActivity(
        ILogger<ResolveToolsActivity> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var toolNamesRaw = ToolNamesInput.Get(context);
        var providerName = ProviderName.Get(context);

        var resolved = new List<ResolvedTool>();

        if (string.IsNullOrWhiteSpace(toolNamesRaw))
        {
            _logger?.LogDebug("No tools requested, returning empty list");
            context.SetResult(resolved);
            return;
        }

        var toolNames = ParseToolNames(toolNamesRaw);

        foreach (var toolName in toolNames)
        {
            var tool = ResolveToolDefinition(toolName, providerName);

            if (tool != null)
            {
                resolved.Add(tool);
                _logger?.LogDebug("Resolved tool '{Tool}' for provider {Provider}", toolName, providerName);
            }
            else
            {
                _logger?.LogWarning("Tool '{Tool}' not found in configuration, skipping", toolName);
            }
        }

        _logger?.LogInformation("Resolved {Count} of {Requested} requested tools",
            resolved.Count, toolNames.Count);

        context.SetResult(resolved);
    }

    private List<string> ParseToolNames(string raw)
    {
        raw = raw.Trim();

        // Try JSON array first
        if (raw.StartsWith('['))
        {
            try
            {
                var names = JsonSerializer.Deserialize<List<string>>(raw);
                if (names != null)
                    return names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            }
            catch
            {
                // Fall through to CSV parsing
            }
        }

        // CSV parsing
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
    }

    private ResolvedTool? ResolveToolDefinition(string toolName, string providerName)
    {
        // Try provider-specific tool first: LlmTools:{provider}:{toolName}
        var providerSpecificSection = _configuration?.GetSection($"LlmTools:{providerName}:{toolName}");
        if (providerSpecificSection != null && providerSpecificSection.Exists())
        {
            return BindToolFromConfig(providerSpecificSection, toolName);
        }

        // Try global tool: LlmTools:{toolName}
        var globalSection = _configuration?.GetSection($"LlmTools:{toolName}");
        if (globalSection != null && globalSection.Exists())
        {
            return BindToolFromConfig(globalSection, toolName);
        }

        // Return a well-known built-in tool if recognized
        return GetBuiltInTool(toolName);
    }

    private static ResolvedTool? BindToolFromConfig(IConfigurationSection section, string toolName)
    {
        var description = section["Description"];
        var schemaJson = section["InputSchema"];

        Dictionary<string, object>? inputSchema = null;
        if (!string.IsNullOrWhiteSpace(schemaJson))
        {
            try
            {
                inputSchema = JsonSerializer.Deserialize<Dictionary<string, object>>(schemaJson);
            }
            catch
            {
                // Ignore malformed schema
            }
        }

        return new ResolvedTool
        {
            Name = section["Name"] ?? toolName,
            Description = description ?? $"Tool: {toolName}",
            InputSchema = inputSchema
        };
    }

    /// <summary>
    /// Returns built-in tool definitions for well-known Tamma tools.
    /// </summary>
    private static ResolvedTool? GetBuiltInTool(string toolName)
    {
        return toolName.ToLowerInvariant() switch
        {
            "search_code" => new ResolvedTool
            {
                Name = "search_code",
                Description = "Search the codebase for files, symbols, or patterns",
                InputSchema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Search query"
                        },
                        ["file_pattern"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Optional file glob pattern"
                        }
                    },
                    ["required"] = new[] { "query" }
                }
            },
            "read_file" => new ResolvedTool
            {
                Name = "read_file",
                Description = "Read the contents of a file",
                InputSchema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["path"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "File path to read"
                        }
                    },
                    ["required"] = new[] { "path" }
                }
            },
            "run_tests" => new ResolvedTool
            {
                Name = "run_tests",
                Description = "Run tests in the project",
                InputSchema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["test_pattern"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "Optional test name or pattern to run"
                        }
                    }
                }
            },
            _ => null
        };
    }
}
