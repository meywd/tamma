# Story 11.3: Tool Call Validation — Implementation Plan

## Overview

Create a tool call validation layer that verifies every LLM-returned tool call against an allowlist, validates the name format, checks argument size/structure, sanitizes string arguments, and blocks dangerous shell commands via an `ActionGate`. Wire this validation into the LLM response processing path.

**Depends on:** Story 11.1 (ContentSanitizer C# Port)

---

## Step-by-Step Implementation Tasks

### Task 1: Create the `IToolCallValidator` interface and result model

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/IToolCallValidator.cs`

```csharp
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Security;

/// <summary>
/// Validates LLM-returned tool calls against an allowlist, format rules,
/// argument constraints, and blocked command patterns.
/// </summary>
public interface IToolCallValidator
{
    /// <summary>
    /// Validate a single tool call against the list of tools that were sent to the LLM.
    /// Returns a validation result. When invalid, ErrorMessage is populated and the
    /// caller should return this message to the LLM as a tool error (not crash the workflow).
    /// </summary>
    ToolCallValidationResult Validate(LlmToolCall toolCall, IReadOnlyList<string> allowedToolNames);
}

/// <summary>
/// Result of tool call validation.
/// </summary>
public class ToolCallValidationResult
{
    /// <summary>Whether the tool call passed all validation checks.</summary>
    public bool IsValid { get; init; }

    /// <summary>Human-readable error message when IsValid == false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The tool call arguments JSON after sanitization of string values.
    /// Same as original if no string values were modified.
    /// Only populated when IsValid == true.
    /// </summary>
    public string? SanitizedArgumentsJson { get; init; }
}
```

### Task 2: Create the `ToolCallValidator` implementation

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/ToolCallValidator.cs`

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.Security;

public class ToolCallValidator : IToolCallValidator
{
    private readonly IContentSanitizer _sanitizer;
    private readonly ActionGate _actionGate;
    private readonly ILogger<ToolCallValidator>? _logger;

    /// <summary>Maximum serialized JSON argument size in bytes.</summary>
    private const int MaxArgumentSizeBytes = 100 * 1024; // 100KB

    /// <summary>Tool name format: alphanumeric, underscore, hyphen, 1-64 chars.</summary>
    private static readonly Regex ToolNameFormatRe = new(
        @"^[a-zA-Z0-9_\-]{1,64}$", RegexOptions.Compiled);

    // Tool names that indicate shell/exec capability (used to trigger ActionGate)
    private static readonly HashSet<string> ShellToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "execute_shell_command", "run_command", "shell", "exec", "bash",
        "terminal", "run_shell", "execute_command", "system_command",
        "run_code", "execute", "cmd"
    };

    public ToolCallValidator(
        IContentSanitizer sanitizer,
        ActionGate actionGate,
        ILogger<ToolCallValidator>? logger = null)
    {
        _sanitizer = sanitizer;
        _actionGate = actionGate;
        _logger = logger;
    }

    public ToolCallValidationResult Validate(LlmToolCall toolCall, IReadOnlyList<string> allowedToolNames)
    {
        // 1. Name must be in the allowed list
        if (!allowedToolNames.Any(n => n.Equals(toolCall.ToolName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger?.LogWarning("Tool call rejected: name '{ToolName}' not in allowed list", toolCall.ToolName);
            return new ToolCallValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Tool '{toolCall.ToolName}' is not available. Available tools: {string.Join(", ", allowedToolNames)}"
            };
        }

        // 2. Name format validation
        if (!ToolNameFormatRe.IsMatch(toolCall.ToolName))
        {
            _logger?.LogWarning("Tool call rejected: name '{ToolName}' has invalid format", toolCall.ToolName);
            return new ToolCallValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Tool name '{toolCall.ToolName}' has invalid format. Must match [a-zA-Z0-9_-]{{1,64}}."
            };
        }

        // 3. Arguments must parse as valid JSON
        JsonElement argsElement;
        try
        {
            argsElement = JsonSerializer.Deserialize<JsonElement>(toolCall.ArgumentsJson);
        }
        catch (JsonException)
        {
            _logger?.LogWarning("Tool call rejected: arguments are not valid JSON for tool '{ToolName}'", toolCall.ToolName);
            return new ToolCallValidationResult
            {
                IsValid = false,
                ErrorMessage = "Tool arguments are not valid JSON."
            };
        }

        // 4. Arguments size check (on serialized string)
        if (toolCall.ArgumentsJson.Length > MaxArgumentSizeBytes)
        {
            _logger?.LogWarning("Tool call rejected: arguments exceed {MaxSize}KB for tool '{ToolName}'",
                MaxArgumentSizeBytes / 1024, toolCall.ToolName);
            return new ToolCallValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Tool arguments exceed maximum size of {MaxArgumentSizeBytes / 1024}KB."
            };
        }

        // 5. Sanitize string-valued arguments (recursive walk)
        var sanitizedArgs = SanitizeJsonStrings(argsElement);
        var sanitizedJson = JsonSerializer.Serialize(sanitizedArgs);

        // 6. ActionGate check for shell/exec tools
        if (IsShellTool(toolCall.ToolName))
        {
            var command = ExtractCommandFromArgs(argsElement);
            if (command != null && _actionGate.IsBlocked(command))
            {
                _logger?.LogWarning("Tool call rejected by ActionGate: blocked command pattern in tool '{ToolName}'", toolCall.ToolName);
                return new ToolCallValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"The command contains a blocked pattern and cannot be executed for safety reasons."
                };
            }
        }

        return new ToolCallValidationResult
        {
            IsValid = true,
            SanitizedArgumentsJson = sanitizedJson
        };
    }

    /// <summary>
    /// Recursively walk JSON and sanitize all string values via IContentSanitizer.SanitizeInput().
    /// </summary>
    private JsonElement SanitizeJsonStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var raw = element.GetString() ?? "";
                var sanitized = _sanitizer.SanitizeInput(raw).Result;
                return JsonSerializer.Deserialize<JsonElement>($"\"{EscapeJsonString(sanitized)}\"");

            case JsonValueKind.Object:
                var objDict = new Dictionary<string, JsonElement>();
                foreach (var prop in element.EnumerateObject())
                {
                    objDict[prop.Name] = SanitizeJsonStrings(prop.Value);
                }
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(objDict));

            case JsonValueKind.Array:
                var arr = new List<JsonElement>();
                foreach (var item in element.EnumerateArray())
                {
                    arr.Add(SanitizeJsonStrings(item));
                }
                return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(arr));

            default:
                return element; // Numbers, booleans, null -- pass through
        }
    }

    private static string EscapeJsonString(string s)
    {
        // Use System.Text.Json to properly escape
        return JsonSerializer.Serialize(s)[1..^1]; // Strip surrounding quotes
    }

    private static bool IsShellTool(string toolName)
    {
        return ShellToolNames.Contains(toolName);
    }

    /// <summary>
    /// Extract the command string from tool arguments. Looks for common field names.
    /// </summary>
    private static string? ExtractCommandFromArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return null;

        // Common field names for shell commands
        string[] commandFields = { "command", "cmd", "script", "code", "shell_command", "input" };

        foreach (var field in commandFields)
        {
            if (args.TryGetProperty(field, out var val) && val.ValueKind == JsonValueKind.String)
                return val.GetString();
        }

        return null;
    }
}
```

### Task 3: Create the `ActionGate` class

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs`

```csharp
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Tamma.Activities.Security;

/// <summary>
/// Gates dangerous shell commands. Maintains a configurable set of blocked
/// command patterns. Thread-safe and fast (target: under 0.1ms per check).
/// </summary>
public class ActionGate
{
    private readonly IReadOnlyList<Regex> _blockedPatterns;

    /// <summary>
    /// Default blocked command patterns. Each is compiled for performance.
    /// </summary>
    private static readonly IReadOnlyList<(string Name, string Pattern)> DefaultBlockedPatterns = new List<(string, string)>
    {
        ("recursive_delete_root", @"rm\s+-rf\s+/"),
        ("recursive_delete_home", @"rm\s+-rf\s+~"),
        ("curl_pipe_bash", @"curl.*\|\s*bash"),
        ("wget_pipe_bash", @"wget.*\|\s*bash"),
        ("chmod_777", @"chmod\s+777"),
        ("sudo", @"sudo\s+"),
        ("passwd", @"\bpasswd\b"),
        ("etc_shadow", @"/etc/shadow"),
        ("dotenv_access", @"\.env\b"),
        ("eval_call", @"eval\s*\("),
        ("exec_call", @"exec\s*\("),
        ("dev_write", @">\s*/dev/"),
        ("mkfs", @"\bmkfs\b"),
        ("dd_raw_disk", @"dd\s+if="),
        ("netcat_listener", @"nc\s+-l"),
        ("python_os_exec", @"python.*-c.*import\s+os"),
        ("reverse_shell", @"\b(bash|sh)\s+-i\s+>&"),
        ("base64_decode_pipe", @"base64\s+(-d|--decode).*\|"),
        ("curl_upload", @"curl.*-T\s+/etc/"),
        ("env_dump", @"\bprintenv\b"),
    };

    public ActionGate(IOptions<ActionGateOptions>? options = null)
    {
        var patterns = new List<Regex>();

        // Add default patterns
        foreach (var (_, pattern) in DefaultBlockedPatterns)
        {
            patterns.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase));
        }

        // Add extra patterns from configuration
        if (options?.Value.AdditionalBlockedPatterns != null)
        {
            foreach (var extra in options.Value.AdditionalBlockedPatterns)
            {
                try
                {
                    patterns.Add(new Regex(extra, RegexOptions.Compiled | RegexOptions.IgnoreCase));
                }
                catch (ArgumentException)
                {
                    // Skip invalid regex patterns
                }
            }
        }

        _blockedPatterns = patterns;
    }

    /// <summary>
    /// Check if a command matches any blocked pattern.
    /// Returns true if the command should be BLOCKED.
    /// </summary>
    public bool IsBlocked(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        foreach (var pattern in _blockedPatterns)
        {
            if (pattern.IsMatch(command))
                return true;
        }

        return false;
    }
}
```

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGateOptions.cs`

```csharp
namespace Tamma.Activities.Security;

/// <summary>
/// Configuration options for ActionGate.
/// Bound from "Security:ActionGate" config section.
/// </summary>
public class ActionGateOptions
{
    /// <summary>
    /// Additional regex patterns to block, beyond the built-in defaults.
    /// </summary>
    public List<string> AdditionalBlockedPatterns { get; set; } = new();
}
```

### Task 4: Wire validation into `CallLlmInlineActivity`

**File to modify:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

**Changes:**

1. **Add field** (after `_sanitizer`):
```csharp
private readonly IToolCallValidator? _toolCallValidator;
```

2. **Update constructor** to accept `IToolCallValidator?`:
```csharp
public CallLlmInlineActivity(
    ILogger<CallLlmInlineActivity>? logger,
    IHttpClientFactory? httpClientFactory,
    IConfiguration? configuration,
    IContentSanitizer? sanitizer,
    IToolCallValidator? toolCallValidator)
{
    _logger = logger;
    _httpClientFactory = httpClientFactory;
    _configuration = configuration;
    _sanitizer = sanitizer;
    _toolCallValidator = toolCallValidator;
}
```

3. **After the LLM response is received (before storing in variables), validate tool calls.**

Inside the `try` block of `ExecuteAsync()`, after the response is built (around line 111), before setting `context.SetVariable("LastResponse", ...)`:

```csharp
// Validate tool calls if validator is available and tools were sent
if (_toolCallValidator != null && response.ToolCalls != null && response.ToolCalls.Count > 0)
{
    var allowedTools = GetAllowedToolNames(toolsJson);
    var validatedToolCalls = new List<LlmToolCall>();

    foreach (var tc in response.ToolCalls)
    {
        var validationResult = _toolCallValidator.Validate(tc, allowedTools);
        if (validationResult.IsValid)
        {
            // Use sanitized arguments
            tc.ArgumentsJson = validationResult.SanitizedArgumentsJson ?? tc.ArgumentsJson;
            validatedToolCalls.Add(tc);
        }
        else
        {
            _logger?.LogWarning("Tool call '{ToolName}' rejected: {Error}", tc.ToolName, validationResult.ErrorMessage);
            // Replace the tool call with an error result (the LLM loop will see this)
            validatedToolCalls.Add(new LlmToolCall
            {
                Id = tc.Id,
                ToolName = tc.ToolName,
                ArgumentsJson = JsonSerializer.Serialize(new { error = validationResult.ErrorMessage })
            });
            response.Success = false;
            response.ErrorMessage = $"Tool call validation failed: {validationResult.ErrorMessage}";
        }
    }

    response.ToolCalls = validatedToolCalls;
}

// Helper method to extract allowed tool names from toolsJson
private static IReadOnlyList<string> GetAllowedToolNames(string? toolsJson)
{
    if (string.IsNullOrWhiteSpace(toolsJson))
        return Array.Empty<string>();

    try
    {
        var tools = JsonSerializer.Deserialize<List<ResolvedTool>>(toolsJson);
        return tools?.Select(t => t.Name).ToList() ?? new List<string>();
    }
    catch
    {
        return Array.Empty<string>();
    }
}
```

### Task 5: Wire validation into `CallLlmActivity`

**File to modify:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs`

Apply the same pattern as Task 4:

1. Add `IToolCallValidator?` field and constructor parameter
2. After `ExecuteProviderCall()` returns the response (line 115), validate tool calls before storing:

```csharp
// After line 118 (response received):
if (_toolCallValidator != null && response.ToolCalls != null && response.ToolCalls.Count > 0)
{
    var allowedToolNames = tools?.Select(t => t.Name).ToList() ?? new List<string>();
    foreach (var tc in response.ToolCalls)
    {
        var result = _toolCallValidator.Validate(tc, allowedToolNames);
        if (!result.IsValid)
        {
            _logger?.LogWarning("Tool call '{ToolName}' rejected in CallLlmActivity: {Error}",
                tc.ToolName, result.ErrorMessage);
            // Mark as failed -- the workflow retry logic will handle it
            response.Success = false;
            response.ErrorMessage = $"Tool validation failed: {result.ErrorMessage}";
            break;
        }
        tc.ArgumentsJson = result.SanitizedArgumentsJson ?? tc.ArgumentsJson;
    }
}
```

### Task 6: Register in DI

**File to modify:** `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`

Add after the existing security service registrations (from Story 11.1):

```csharp
// Tool call validation
builder.Services.Configure<ActionGateOptions>(
    builder.Configuration.GetSection("Security:ActionGate"));
builder.Services.AddSingleton<ActionGate>();
builder.Services.AddSingleton<IToolCallValidator, ToolCallValidator>();
```

### Task 7: Write unit tests

**File to create:** `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/ToolCallValidatorTests.cs`

```csharp
[TestFixture]
public class ToolCallValidatorTests
{
    private ToolCallValidator _validator = null!;
    private ContentSanitizer _sanitizer = null!;
    private ActionGate _gate = null!;

    [SetUp]
    public void SetUp()
    {
        _sanitizer = new ContentSanitizer();
        _gate = new ActionGate();
        _validator = new ToolCallValidator(_sanitizer, _gate);
    }

    // --- Allowlist tests ---
    [Test] public void Validate_ToolNameInAllowedList_IsValid()
    [Test] public void Validate_ToolNameNotInAllowedList_Rejected()
    [Test] public void Validate_EmptyAllowedList_RejectsAll()
    [Test] public void Validate_CaseInsensitiveToolNameMatch()
    [Test] public void Validate_DuplicateNamesInAllowedList_StillValid()

    // --- Name format tests ---
    [Test] public void Validate_ValidNameFormat_Passes()
    [Test] public void Validate_SpecialCharsInName_Rejected()
    [Test] public void Validate_TooLongName_Rejected()
    [Test] public void Validate_EmptyName_Rejected()

    // --- Argument validation tests ---
    [Test] public void Validate_ValidJsonArguments_Passes()
    [Test] public void Validate_InvalidJsonArguments_Rejected()
    [Test] public void Validate_OversizedArguments_Rejected()
    [Test] public void Validate_StringValuesAreSanitized()
    [Test] public void Validate_NestedObjectStringsSanitized()

    // --- ActionGate integration ---
    [Test] public void Validate_ShellTool_SafeCommand_Passes()
    [Test] public void Validate_ShellTool_DangerousCommand_Rejected()
    [Test] public void Validate_NonShellTool_SkipsActionGate()
    [Test] public void Validate_ShellTool_RmRfRoot_Rejected()
    [Test] public void Validate_ShellTool_CurlPipeBash_Rejected()
    [Test] public void Validate_ShellTool_Sudo_Rejected()

    // --- Integration tests ---
    [Test] public void Validate_ValidToolCall_ReturnsSanitizedArguments()
    [Test] public void Validate_RejectedTool_ReturnsErrorMessage()
}
```

**File to create:** `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/ActionGateTests.cs`

```csharp
[TestFixture]
public class ActionGateTests
{
    private ActionGate _gate = null!;

    [SetUp]
    public void SetUp()
    {
        _gate = new ActionGate();
    }

    [Test] public void IsBlocked_RmRfRoot_ReturnsTrue()
    [Test] public void IsBlocked_RmRfHome_ReturnsTrue()
    [Test] public void IsBlocked_CurlPipeBash_ReturnsTrue()
    [Test] public void IsBlocked_WgetPipeBash_ReturnsTrue()
    [Test] public void IsBlocked_Chmod777_ReturnsTrue()
    [Test] public void IsBlocked_Sudo_ReturnsTrue()
    [Test] public void IsBlocked_Passwd_ReturnsTrue()
    [Test] public void IsBlocked_EtcShadow_ReturnsTrue()
    [Test] public void IsBlocked_DotEnv_ReturnsTrue()
    [Test] public void IsBlocked_NetcatListener_ReturnsTrue()
    [Test] public void IsBlocked_SafeCommand_ReturnsFalse()
    [Test] public void IsBlocked_SafeLsCommand_ReturnsFalse()
    [Test] public void IsBlocked_SafeGitCommand_ReturnsFalse()
    [Test] public void IsBlocked_EmptyCommand_ReturnsFalse()
    [Test] public void IsBlocked_NullCommand_ReturnsFalse()
    [Test] public void IsBlocked_CaseInsensitive()
    [Test] public void IsBlocked_AdditionalPatternsFromConfig()
}
```

---

## Files to Create (Summary)

| File | Purpose |
|------|---------|
| `apps/tamma-elsa/src/Tamma.Activities/Security/IToolCallValidator.cs` | Interface + `ToolCallValidationResult` model |
| `apps/tamma-elsa/src/Tamma.Activities/Security/ToolCallValidator.cs` | Implementation: allowlist, format, size, sanitization, ActionGate |
| `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs` | Blocked command pattern checker |
| `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGateOptions.cs` | Configuration options for additional patterns |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/ToolCallValidatorTests.cs` | 22 unit tests |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/ActionGateTests.cs` | 17 unit tests |

## Files to Modify (Summary)

| File | Lines | Change |
|------|-------|--------|
| `CallLlmInlineActivity.cs` | Constructor + after response | Add `IToolCallValidator`, validate tool calls after LLM response |
| `CallLlmActivity.cs` | Constructor + after response | Add `IToolCallValidator`, validate tool calls after LLM response |
| `Program.cs` | After security DI block | Register `ActionGateOptions`, `ActionGate`, `IToolCallValidator` |

---

## Verification Steps

1. **Build:** `dotnet build` -- no errors
2. **Tests:** All 39 new tests pass
3. **Manual test:** Trigger a workflow that uses tools, inject a tool call with a name not in the sent list -- verify it is rejected with an error message, not a crash
4. **ActionGate test:** In a test, call `IsBlocked("rm -rf /")` -- returns `true`. Call `IsBlocked("ls -la")` -- returns `false`
5. **Performance:** The `Validate()` method should complete in under 1ms for typical tool calls (verify with a stopwatch in a test)

---

## Risks and Edge Cases

1. **Tool name case sensitivity:** The LLM might return `"GetWeather"` when the sent tool name was `"get_weather"`. The allowlist check is case-insensitive to handle this, but the ActionGate shell tool check is also case-insensitive.

2. **Argument sanitization of nested JSON:** The recursive `SanitizeJsonStrings()` walks the entire JSON tree. Deeply nested JSON (100+ levels) could cause a stack overflow. Mitigated by the 100KB size limit -- deeply nested JSON within 100KB cannot be deep enough to overflow.

3. **ActionGate false positives:** The pattern `\.env\b` will block `cat .env` but also `echo "test.environment"`. This is acceptable for a security gate -- it is better to be too strict than too permissive. Users can add exceptions via config.

4. **Empty tools list:** When no tools are sent to the LLM (`toolsJson` is null/empty), tool call validation is skipped entirely. If the LLM hallucinates a tool call without being offered tools, the validation will catch it with an empty allowlist (rejects all).

5. **Error message to LLM:** When a tool call is rejected, the error message is fed back to the LLM as a tool result. The LLM may retry with a corrected tool call. The error message must be informative but not reveal internal security logic. The messages use generic phrasing like "contains a blocked pattern" without specifying which pattern.
