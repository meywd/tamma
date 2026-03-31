using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Security;

/// <summary>
/// Validates LLM-returned tool calls against an allowlist, name format rules,
/// argument size/structure constraints, sanitizes string arguments via
/// <see cref="IContentSanitizer"/>, and blocks dangerous shell commands via
/// <see cref="ActionGate"/>.
///
/// All validation is synchronous and targets under 1ms per tool call.
/// Thread-safe (no mutable instance state).
/// </summary>
public sealed class ToolCallValidator : IToolCallValidator
{
    private readonly IContentSanitizer _sanitizer;
    private readonly ActionGate _actionGate;
    private readonly ILogger<ToolCallValidator>? _logger;

    /// <summary>Maximum serialized JSON argument size in bytes (100KB).</summary>
    private const int MaxArgumentSizeBytes = 100 * 1024;

    /// <summary>Tool name format: alphanumeric, underscore, hyphen, 1-64 chars.</summary>
    private static readonly Regex ToolNameFormatRe = new(
        @"^[a-zA-Z0-9_\-]{1,64}$", RegexOptions.Compiled);

    /// <summary>
    /// Tool names that indicate shell/exec capability. Used to trigger ActionGate checks.
    /// Case-insensitive matching.
    /// </summary>
    private static readonly HashSet<string> ShellToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "execute_shell_command", "run_command", "shell", "exec", "bash",
        "terminal", "run_shell", "execute_command", "system_command",
        "run_code", "execute", "cmd", "shell_execute"
    };

    /// <summary>
    /// Common field names in tool arguments that contain shell commands.
    /// </summary>
    private static readonly string[] CommandFields =
        { "command", "cmd", "script", "code", "shell_command", "input" };

    /// <summary>
    /// Creates a new <see cref="ToolCallValidator"/>.
    /// </summary>
    /// <param name="sanitizer">Content sanitizer for string argument values.</param>
    /// <param name="actionGate">Action gate for blocking dangerous shell commands.</param>
    /// <param name="logger">Optional logger. Never logs raw arguments or commands.</param>
    public ToolCallValidator(
        IContentSanitizer sanitizer,
        ActionGate actionGate,
        ILogger<ToolCallValidator>? logger = null)
    {
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        _actionGate = actionGate ?? throw new ArgumentNullException(nameof(actionGate));
        _logger = logger;
    }

    /// <inheritdoc />
    public ToolCallValidationResult Validate(LlmToolCall toolCall, IReadOnlyList<string> allowedToolNames)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(allowedToolNames);

        var sw = Stopwatch.StartNew();
        var toolName = toolCall.ToolName ?? "";
        var toolCallId = toolCall.Id ?? "";

        _logger?.LogDebug(
            "Tool call validation started: ToolName={ToolName}, ToolCallId={ToolCallId}, ArgumentsSizeBytes={ArgumentsSizeBytes}",
            toolName, toolCallId, toolCall.ArgumentsJson?.Length ?? 0);

        // 1. Name must be in the allowed list (case-insensitive)
        if (!allowedToolNames.Any(n => n.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger?.LogWarning(
                "Tool call rejected: not in allowlist. ToolName={ToolName}, ToolCallId={ToolCallId}, AllowedToolCount={AllowedToolCount}",
                toolName, toolCallId, allowedToolNames.Count);
            return new ToolCallValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Tool '{toolName}' is not available. Available tools: {string.Join(", ", allowedToolNames)}"
            };
        }

        // 2. Name format validation
        if (!ToolNameFormatRe.IsMatch(toolName))
        {
            _logger?.LogWarning(
                "Tool call rejected: invalid name format. ToolName={ToolName}, ToolCallId={ToolCallId}",
                toolName, toolCallId);
            return new ToolCallValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Tool name '{toolName}' has invalid format. Must match [a-zA-Z0-9_-]{{1,64}}."
            };
        }

        // 3. Arguments size check (on serialized string, before parsing)
        var argumentsJson = toolCall.ArgumentsJson ?? "{}";
        if (argumentsJson.Length > MaxArgumentSizeBytes)
        {
            _logger?.LogWarning(
                "Tool call rejected: arguments exceed size limit. ToolCallId={ToolCallId}, ToolName={ToolName}, ArgumentsSizeBytes={ArgumentsSizeBytes}, MaxSizeBytes={MaxSizeBytes}",
                toolCallId, toolName, argumentsJson.Length, MaxArgumentSizeBytes);
            return new ToolCallValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Tool arguments exceed maximum size of {MaxArgumentSizeBytes / 1024}KB."
            };
        }

        // 4. Arguments must parse as valid JSON
        JsonElement argsElement;
        try
        {
            argsElement = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        }
        catch (JsonException)
        {
            _logger?.LogWarning(
                "Tool call rejected: invalid JSON arguments. ToolCallId={ToolCallId}, ToolName={ToolName}",
                toolCallId, toolName);
            return new ToolCallValidationResult
            {
                IsValid = false,
                ErrorMessage = "Tool arguments are not valid JSON."
            };
        }

        // 5. ActionGate check for shell/exec tools (before sanitization, on raw args)
        if (IsShellTool(toolName))
        {
            var command = ExtractCommandFromArgs(argsElement);
            if (command != null && _actionGate.IsBlocked(command, out var patternName))
            {
                _logger?.LogWarning(
                    "ActionGate: command blocked. ToolCallId={ToolCallId}, ToolName={ToolName}, BlockedPatternName={BlockedPatternName}",
                    toolCallId, toolName, patternName);
                return new ToolCallValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "The command contains a blocked pattern and cannot be executed for safety reasons."
                };
            }

            _logger?.LogDebug(
                "ActionGate: command allowed. ToolCallId={ToolCallId}, ToolName={ToolName}",
                toolCallId, toolName);
        }

        // 6. Sanitize string-valued arguments (recursive walk)
        int sanitizedCount = 0;
        var sanitizedArgs = SanitizeJsonStrings(argsElement, ref sanitizedCount);
        var sanitizedJson = JsonSerializer.Serialize(sanitizedArgs);

        if (sanitizedCount > 0)
        {
            _logger?.LogDebug(
                "Tool arguments sanitized: ToolCallId={ToolCallId}, ToolName={ToolName}, StringFieldsSanitizedCount={StringFieldsSanitizedCount}",
                toolCallId, toolName, sanitizedCount);
        }

        sw.Stop();
        _logger?.LogDebug(
            "Tool call validation passed: ToolCallId={ToolCallId}, ToolName={ToolName}, ValidationDurationMs={ValidationDurationMs}",
            toolCallId, toolName, sw.Elapsed.TotalMilliseconds);

        return new ToolCallValidationResult
        {
            IsValid = true,
            SanitizedArgumentsJson = sanitizedJson
        };
    }

    /// <summary>
    /// Recursively walk JSON and sanitize all string values via <see cref="IContentSanitizer.SanitizeInput"/>.
    /// </summary>
    private JsonElement SanitizeJsonStrings(JsonElement element, ref int sanitizedCount)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            {
                var raw = element.GetString() ?? "";
                var result = _sanitizer.SanitizeInput(raw);
                sanitizedCount++;
                // Use JsonSerializer to properly encode the sanitized string
                var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Result);
                return JsonSerializer.Deserialize<JsonElement>(bytes);
            }

            case JsonValueKind.Object:
            {
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.Text.Json.Utf8JsonWriter(stream);
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    var sanitizedValue = SanitizeJsonStrings(prop.Value, ref sanitizedCount);
                    sanitizedValue.WriteTo(writer);
                }
                writer.WriteEndObject();
                writer.Flush();
                return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
            }

            case JsonValueKind.Array:
            {
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.Text.Json.Utf8JsonWriter(stream);
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    var sanitizedItem = SanitizeJsonStrings(item, ref sanitizedCount);
                    sanitizedItem.WriteTo(writer);
                }
                writer.WriteEndArray();
                writer.Flush();
                return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
            }

            default:
                // Numbers, booleans, null -- pass through unchanged
                return element;
        }
    }

    /// <summary>
    /// Check if a tool name indicates shell/exec capability.
    /// </summary>
    private static bool IsShellTool(string toolName)
    {
        return ShellToolNames.Contains(toolName);
    }

    /// <summary>
    /// Extract the command string from tool arguments.
    /// Looks for common field names that hold shell commands.
    /// </summary>
    private static string? ExtractCommandFromArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var field in CommandFields)
        {
            if (args.TryGetProperty(field, out var val) && val.ValueKind == JsonValueKind.String)
                return val.GetString();
        }

        return null;
    }
}
