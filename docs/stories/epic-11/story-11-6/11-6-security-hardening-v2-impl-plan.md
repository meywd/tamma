# Story 11.6: Security Hardening v2 — Implementation Plan

## Overview

This plan adds defense-in-depth controls to close the security gaps identified in the deep audit of the tool execution system. The implementation is organized into 10 tasks, each producing a testable unit. All new code follows the existing pattern in `Tamma.Activities/Security/`.

**Dependencies:** Stories 11.1, 11.3, 11.5, 12.1, 12.2 (all already implemented)

---

## Step-by-Step Implementation Tasks

### Task 1: CredentialScanner — Detect embedded secrets in content

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/CredentialScanner.cs`

This is the foundational component used by both OutputValidator and tool output redaction.

```csharp
using System.Text.RegularExpressions;

namespace Tamma.Activities.Security;

/// <summary>
/// Detects embedded credentials, API keys, tokens, and private key material
/// in arbitrary text content. Used for both pre-write validation (blocking)
/// and post-read redaction (replacing with [REDACTED]).
/// Thread-safe: all state is compiled regexes (immutable after construction).
/// </summary>
public sealed class CredentialScanner
{
    /// <summary>
    /// Result of a credential scan.
    /// </summary>
    public sealed class ScanResult
    {
        public bool HasCredentials { get; init; }
        public IReadOnlyList<CredentialMatch> Matches { get; init; } = Array.Empty<CredentialMatch>();
    }

    public sealed class CredentialMatch
    {
        /// <summary>Category name (e.g., "aws_access_key", "github_pat").</summary>
        public string Category { get; init; } = "";
        /// <summary>Character position in the input where the match starts.</summary>
        public int Position { get; init; }
        /// <summary>Length of the matched text (for redaction).</summary>
        public int Length { get; init; }
    }

    private static readonly IReadOnlyList<(string Category, Regex Pattern)> Patterns = new List<(string, Regex)>
    {
        // AWS Access Key ID
        ("aws_access_key", new Regex(@"(?<![A-Z0-9])AKIA[A-Z0-9]{16}(?![A-Z0-9])",
            RegexOptions.Compiled)),

        // AWS Secret Key (40 chars base64-like after a known prefix)
        ("aws_secret_key", new Regex(@"(?i)(?:aws_secret_access_key|secret_key)\s*[=:]\s*[A-Za-z0-9/+=]{40}",
            RegexOptions.Compiled)),

        // GitHub PAT (classic: ghp_, fine-grained: github_pat_)
        ("github_pat", new Regex(@"\b(ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{82})\b",
            RegexOptions.Compiled)),

        // GitHub OAuth/Server/App tokens
        ("github_token", new Regex(@"\b(gho_|ghs_|ghr_)[A-Za-z0-9]{36}\b",
            RegexOptions.Compiled)),

        // GitLab PAT
        ("gitlab_pat", new Regex(@"\bglpat-[A-Za-z0-9\-]{20,}\b",
            RegexOptions.Compiled)),

        // Anthropic API key (must be before OpenAI to avoid partial match)
        ("anthropic_key", new Regex(@"\bsk-ant-[A-Za-z0-9\-]{20,}\b",
            RegexOptions.Compiled)),

        // OpenAI API key
        ("openai_key", new Regex(@"\bsk-[A-Za-z0-9]{20,}\b",
            RegexOptions.Compiled)),

        // Generic API key assignment
        ("generic_api_key", new Regex(
            @"(?i)(api[_-]?key|api[_-]?secret|auth[_-]?token|access[_-]?token)\s*[=:]\s*[""']?[A-Za-z0-9_\-./+=]{16,}[""']?",
            RegexOptions.Compiled)),

        // Private key blocks (RSA, EC, DSA, generic)
        ("private_key", new Regex(@"-----BEGIN\s+(RSA\s+|EC\s+|DSA\s+|OPENSSH\s+)?PRIVATE\s+KEY-----",
            RegexOptions.Compiled)),

        // Connection strings with password
        ("connection_string_password", new Regex(
            @"(?i)(password|pwd)\s*=\s*[^;\s]{3,}",
            RegexOptions.Compiled)),

        // JWT token (header.payload.signature)
        ("jwt_token", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_\-.+/=]{10,}\b",
            RegexOptions.Compiled)),

        // .env style assignment with common secret variable names
        ("env_secret", new Regex(
            @"(?m)^(SECRET|TOKEN|PASSWORD|API_KEY|AUTH|PRIVATE|CREDENTIAL)[A-Z_]*\s*=\s*\S{8,}$",
            RegexOptions.Compiled)),

        // Bearer token in header-like context
        ("bearer_token", new Regex(@"(?i)bearer\s+[A-Za-z0-9._\-]{20,}",
            RegexOptions.Compiled)),
    };

    /// <summary>
    /// Scan content for embedded credentials.
    /// Returns all matches with their categories and positions.
    /// Never throws.
    /// </summary>
    public ScanResult Scan(string content)
    {
        if (string.IsNullOrEmpty(content))
            return new ScanResult { HasCredentials = false };

        var matches = new List<CredentialMatch>();

        foreach (var (category, pattern) in Patterns)
        {
            foreach (Match match in pattern.Matches(content))
            {
                matches.Add(new CredentialMatch
                {
                    Category = category,
                    Position = match.Index,
                    Length = match.Length
                });
            }
        }

        return new ScanResult
        {
            HasCredentials = matches.Count > 0,
            Matches = matches
        };
    }

    /// <summary>
    /// Redact all detected credentials in the content, replacing them with [REDACTED].
    /// Returns the redacted string.
    /// </summary>
    public string Redact(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content ?? string.Empty;

        var result = content;
        foreach (var (_, pattern) in Patterns)
        {
            result = pattern.Replace(result, "[REDACTED]");
        }
        return result;
    }
}
```

**Tests (12):** See acceptance criteria in story file.

---

### Task 2: OutputValidator — Block credential leakage in LLM-generated content

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/IOutputValidator.cs`

```csharp
namespace Tamma.Activities.Security;

public interface IOutputValidator
{
    /// <summary>
    /// Validate content that will be written to disk or committed.
    /// Returns validation result. When invalid, the write should be blocked.
    /// </summary>
    OutputValidationResult Validate(string content, string context);
}

public sealed class OutputValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> DetectedCategories { get; init; } = Array.Empty<string>();
}
```

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/OutputValidator.cs`

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamma.Activities.Security;

public class OutputValidationOptions
{
    public bool Enabled { get; set; } = true;
    public bool BlockOnCredentialDetection { get; set; } = true;
    public bool WarnOnlyMode { get; set; } = false;
}

public sealed class OutputValidator : IOutputValidator
{
    private readonly CredentialScanner _scanner;
    private readonly OutputValidationOptions _options;
    private readonly ILogger<OutputValidator>? _logger;

    public OutputValidator(
        CredentialScanner scanner,
        IOptions<OutputValidationOptions>? options = null,
        ILogger<OutputValidator>? logger = null)
    {
        _scanner = scanner;
        _options = options?.Value ?? new OutputValidationOptions();
        _logger = logger;
    }

    public OutputValidationResult Validate(string content, string context)
    {
        if (!_options.Enabled || string.IsNullOrEmpty(content))
            return new OutputValidationResult { IsValid = true };

        var scanResult = _scanner.Scan(content);
        if (!scanResult.HasCredentials)
            return new OutputValidationResult { IsValid = true };

        var categories = scanResult.Matches
            .Select(m => m.Category)
            .Distinct()
            .ToList();

        _logger?.LogWarning(
            "Credential detected in generated content: Context={Context}, Categories={Categories}, MatchCount={MatchCount}",
            context, string.Join(", ", categories), scanResult.Matches.Count);

        if (_options.WarnOnlyMode || !_options.BlockOnCredentialDetection)
        {
            return new OutputValidationResult
            {
                IsValid = true,
                DetectedCategories = categories
            };
        }

        return new OutputValidationResult
        {
            IsValid = false,
            ErrorMessage = $"Content contains embedded credentials ({string.Join(", ", categories)}). " +
                          "Remove all secrets and use environment variables or a secrets manager instead.",
            DetectedCategories = categories
        };
    }
}
```

**Tests (8):** See acceptance criteria in story file.

---

### Task 3: ToolRateLimiter — Sliding window rate limits per tool type

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/IToolRateLimiter.cs`

```csharp
namespace Tamma.Activities.Security;

public interface IToolRateLimiter
{
    /// <summary>
    /// Check if a tool call is allowed under the current rate limits.
    /// If allowed, records the call. If denied, returns the wait time.
    /// </summary>
    RateLimitResult CheckAndRecord(string toolName, long executionTimeMs = 0);
}

public sealed class RateLimitResult
{
    public bool Allowed { get; init; }
    public string? ErrorMessage { get; init; }
    public int RetryAfterSeconds { get; init; }
}
```

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/ToolRateLimiter.cs`

Implementation uses a `ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>>` for per-tool sliding windows. Each `CheckAndRecord` call dequeues expired entries (older than 60s) and checks the remaining count against the configured limit. Thread-safe via concurrent collections.

Also tracks cumulative execution time for `shell_execute` and `run_tests` against a configurable total time budget.

**Configuration class:**

```csharp
public class RateLimitOptions
{
    public Dictionary<string, int> MaxPerMinute { get; set; } = new()
    {
        ["shell_execute"] = 10,
        ["file_write"] = 30,
        ["git_operations"] = 20,
        ["run_tests"] = 5
    };
    public int MaxCumulativeShellTimeSeconds { get; set; } = 600;
}
```

**Tests (6):** See acceptance criteria in story file.

---

### Task 4: SensitiveFileGuard & CriticalFileGuard — File access controls

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/ISensitiveFileGuard.cs`

```csharp
namespace Tamma.Activities.Security;

public interface ISensitiveFileGuard
{
    /// <summary>Check if a file path matches the sensitive read blocklist.</summary>
    bool IsBlockedForRead(string relativePath);

    /// <summary>Check if a file path matches the critical write blocklist.</summary>
    bool IsBlockedForWrite(string relativePath);

    /// <summary>Check if file content exceeds the write size limit.</summary>
    bool ExceedsWriteSizeLimit(int contentSizeBytes);

    /// <summary>Record bytes written and check cumulative limit.</summary>
    bool ExceedsCumulativeWriteLimit(int contentSizeBytes);
}
```

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/SensitiveFileGuard.cs`

Implementation uses `FileSystemGlobbing` (Microsoft.Extensions.FileSystemGlobbing) for glob pattern matching against configurable blocklists. The guard is injected into `FileReadTool` and `FileWriteTool`.

Default read blocklist:
```
*.env, *.env.*, .env.local, .env.production
*.pem, *.key, *.p12, *.pfx, *.jks, *.keystore
id_rsa, id_rsa.*, id_ed25519, id_ed25519.*, id_ecdsa*
credentials.json, service-account*.json, secrets.yaml, secrets.json
.git/config, .gitconfig
.npmrc, .pypirc, .docker/config.json
.aws/credentials, .ssh/*
```

Default write blocklist:
```
.github/workflows/*, .gitlab-ci.yml, Jenkinsfile
Dockerfile, Dockerfile.*, docker-compose*.yml
Makefile, Rakefile, Gruntfile.js, Gulpfile.js
*.sh (in repository root only)
.gitignore, .gitattributes
.npmrc, .yarnrc, .nvmrc
```

**Configuration class:**

```csharp
public class FileGuardOptions
{
    public List<string> SensitiveReadPatterns { get; set; } = new() { /* defaults above */ };
    public List<string> CriticalWritePatterns { get; set; } = new() { /* defaults above */ };
    public List<string> AdditionalReadPatterns { get; set; } = new();
    public List<string> AdditionalWritePatterns { get; set; } = new();
    public int MaxWriteSizeBytes { get; set; } = 1_048_576; // 1MB
    public long MaxCumulativeWriteSizeBytes { get; set; } = 52_428_800; // 50MB
}
```

**Tests (9):** See acceptance criteria in story file.

---

### Task 5: GitSafetyValidator — Protect branches and prevent destructive operations

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/GitSafetyValidator.cs`

```csharp
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamma.Activities.Security;

public class GitSafetyOptions
{
    public List<string> ProtectedBranchPatterns { get; set; } = new()
    {
        "main", "master", "release/*", "production", "staging"
    };
    public bool BlockForcePush { get; set; } = true;
    public bool BlockBranchDelete { get; set; } = true;
    public bool BlockHardReset { get; set; } = true;
}

public sealed class GitSafetyValidator
{
    private readonly GitSafetyOptions _options;
    private readonly ILogger<GitSafetyValidator>? _logger;

    // Dangerous argument patterns for git push
    private static readonly Regex ForcePushRe = new(
        @"(-f\b|--force\b|--force-with-lease\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DeletePushRe = new(
        @"(--delete\b|:(?=[a-zA-Z]))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Dangerous argument patterns for git branch
    private static readonly Regex BranchDeleteRe = new(
        @"(-[dD]\b|--delete\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Dangerous argument patterns for git reset
    private static readonly Regex HardResetRe = new(
        @"(--hard\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Dangerous argument patterns for git checkout (destructive discard)
    private static readonly Regex CheckoutDiscardRe = new(
        @"^--\s", RegexOptions.Compiled);

    public GitSafetyValidator(
        IOptions<GitSafetyOptions>? options = null,
        ILogger<GitSafetyValidator>? logger = null)
    {
        _options = options?.Value ?? new GitSafetyOptions();
        _logger = logger;
    }

    /// <summary>
    /// Validate a git subcommand and its arguments.
    /// Returns null if safe, or an error message if blocked.
    /// </summary>
    public string? Validate(string subcommand, string extraArgs)
    {
        return subcommand.ToLowerInvariant() switch
        {
            "push" => ValidatePush(extraArgs),
            "branch" => ValidateBranch(extraArgs),
            "reset" => ValidateReset(extraArgs),
            "checkout" => ValidateCheckout(extraArgs),
            _ => null // Other subcommands pass through
        };
    }

    private string? ValidatePush(string args)
    {
        if (_options.BlockForcePush && ForcePushRe.IsMatch(args))
        {
            _logger?.LogWarning("Git push blocked: force push detected");
            return "Force push is blocked by security policy. Use regular push instead.";
        }

        if (_options.BlockBranchDelete && DeletePushRe.IsMatch(args))
        {
            _logger?.LogWarning("Git push blocked: remote branch deletion detected");
            return "Remote branch deletion via push is blocked by security policy.";
        }

        // Check if pushing to a protected branch
        foreach (var pattern in _options.ProtectedBranchPatterns)
        {
            var regex = GlobToRegex(pattern);
            // Look for the branch name in the args (simplified: check tokens)
            var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                if (!token.StartsWith("-") && regex.IsMatch(token))
                {
                    _logger?.LogWarning("Git push blocked: protected branch pattern matched: {Pattern}", pattern);
                    return $"Pushing to protected branch '{token}' is blocked. Push to a feature branch and create a PR.";
                }
            }
        }

        return null;
    }

    private string? ValidateBranch(string args)
    {
        if (_options.BlockBranchDelete && BranchDeleteRe.IsMatch(args))
        {
            _logger?.LogWarning("Git branch delete blocked");
            return "Branch deletion is blocked by security policy.";
        }
        return null;
    }

    private string? ValidateReset(string args)
    {
        if (_options.BlockHardReset && HardResetRe.IsMatch(args))
        {
            _logger?.LogWarning("Git hard reset blocked");
            return "Hard reset is blocked by security policy. Use soft or mixed reset.";
        }
        return null;
    }

    private string? ValidateCheckout(string args)
    {
        if (CheckoutDiscardRe.IsMatch(args))
        {
            _logger?.LogWarning("Git checkout discard blocked");
            return "Destructive checkout (discarding changes) is blocked. Use git stash instead.";
        }
        return null;
    }

    private static Regex GlobToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase);
    }
}
```

**Tests (6):** See acceptance criteria in story file.

---

### Task 6: Enhanced ActionGate — Network exfiltration and additional blocked patterns

**File to modify:** `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs`

Add the following patterns to `DefaultBlockedPatterns`:

```csharp
// Data exfiltration via curl/wget
("curl_post_data", @"curl.*(-d\b|--data\b|--data-raw\b|--data-binary\b)"),
("curl_file_upload", @"curl.*(-F\b|--form\b|-T\b|--upload-file\b)"),
("wget_post_data", @"wget.*--post-(data|file)\b"),

// Remote access
("scp_command", @"\bscp\b"),
("rsync_remote", @"\brsync\b.*:"),
("ssh_command", @"\bssh\b\s"),

// Process manipulation
("kill_all", @"\bkillall\b"),
("pkill_pattern", @"\bpkill\b"),

// Credential access
("cat_credentials", @"\bcat\b.*\.(env|pem|key|p12|pfx)\b"),
("cat_ssh_key", @"\bcat\b.*(id_rsa|id_ed25519|id_ecdsa)\b"),

// History and log access
("bash_history", @"\.bash_history\b"),
("shell_history", @"\bhistory\b"),
```

Also add a new `NetworkAccessOptions` configuration class:

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/NetworkAccessOptions.cs`

```csharp
namespace Tamma.Activities.Security;

public class NetworkAccessOptions
{
    public bool StrictMode { get; set; } = false;
    public List<string> AllowedDomains { get; set; } = new();
    public List<string> AdditionalBlockedPatterns { get; set; } = new();
}
```

---

### Task 7: ToolExecutionAuditLogger — Persistent audit trail

**File to create:** `apps/tamma-elsa/src/Tamma.Activities/Security/ToolExecutionAuditLogger.cs`

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamma.Activities.Security;

public class AuditLogOptions
{
    public bool Enabled { get; set; } = true;
    public string LogFilePath { get; set; } = "tool-audit.jsonl";
    public int RetentionDays { get; set; } = 90;
}

/// <summary>
/// Append-only audit logger for tool executions. Writes JSON Lines to a file
/// or a configured sink. Thread-safe via internal lock on file writes.
/// Never throws — audit logging failures must not crash tool execution.
/// </summary>
public sealed class ToolExecutionAuditLogger
{
    private readonly AuditLogOptions _options;
    private readonly ILogger<ToolExecutionAuditLogger>? _logger;
    private readonly object _writeLock = new();

    public ToolExecutionAuditLogger(
        IOptions<AuditLogOptions>? options = null,
        ILogger<ToolExecutionAuditLogger>? logger = null)
    {
        _options = options?.Value ?? new AuditLogOptions();
        _logger = logger;
    }

    /// <summary>
    /// Log a tool execution event. Never throws.
    /// </summary>
    public void LogExecution(ToolAuditEntry entry)
    {
        if (!_options.Enabled) return;

        try
        {
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            lock (_writeLock)
            {
                File.AppendAllText(_options.LogFilePath, json + "\n");
            }
        }
        catch (Exception ex)
        {
            // Never throw — audit logging must not crash tool execution
            _logger?.LogError("Audit log write failed: {ExceptionMessage}", ex.Message);
        }
    }

    /// <summary>
    /// Log a security event (blocked command, credential detection, etc.).
    /// </summary>
    public void LogSecurityEvent(string eventType, string toolName,
        string? toolCallId, string workflowInstanceId, string details)
    {
        if (!_options.Enabled) return;

        _logger?.LogWarning(
            "SECURITY_EVENT: EventType={EventType}, ToolName={ToolName}, ToolCallId={ToolCallId}, WorkflowInstanceId={WorkflowInstanceId}",
            eventType, toolName, toolCallId, workflowInstanceId);

        LogExecution(new ToolAuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            WorkflowInstanceId = workflowInstanceId,
            ToolName = toolName,
            ToolCallId = toolCallId ?? "",
            EventType = eventType,
            Success = false,
            DurationMs = 0,
            SecurityDetails = details
        });
    }
}

public sealed class ToolAuditEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string WorkflowInstanceId { get; init; } = "";
    public int TurnNumber { get; init; }
    public string ToolName { get; init; } = "";
    public string ToolCallId { get; init; } = "";
    public string EventType { get; init; } = "TOOL_EXECUTED";
    public bool Success { get; init; }
    public long DurationMs { get; init; }
    public int OutputSizeBytes { get; init; }
    public string? SecurityDetails { get; init; }
    // Argument hash (SHA256 of arguments JSON) — not raw args for audit safety
    public string? ArgumentHash { get; init; }
}
```

**Tests (4):** See acceptance criteria in story file.

---

### Task 8: Integrate CredentialScanner into ToolOutputHelper.RedactSecrets()

**File to modify:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolOutputHelper.cs`

**Current code (lines 69-81):**

The existing `RedactSecrets()` method uses a single generic regex. Replace it with a call to `CredentialScanner.Redact()` for comprehensive coverage.

```csharp
// Replace the existing RedactSecrets implementation:
private static readonly CredentialScanner Scanner = new();

public static string RedactSecrets(string output)
{
    if (string.IsNullOrEmpty(output))
        return output ?? string.Empty;

    return Scanner.Redact(output);
}
```

**Critical fix:** Call `RedactSecrets()` in `Truncate()` — currently it is defined but never invoked. Add the call after truncation:

```csharp
// In Truncate(), after building the truncated string:
var truncated = /* existing truncation logic */;
return RedactSecrets(truncated);
```

This ensures all tool output flowing back to the LLM has credentials redacted. This is the single most impactful change in this story — it closes the gap where file read results containing `.env` contents or config files with secrets are fed verbatim into the LLM context.

---

### Task 9: Integrate guards into tool executors

**File to modify:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/FileReadTool.cs`

Add `ISensitiveFileGuard` as a constructor dependency. Before reading, check:

```csharp
// After PathValidator.ResolveSafePath:
if (_fileGuard?.IsBlockedForRead(path) == true)
{
    return new ToolExecutionResult(toolCallId, ToolName, false,
        "Access denied: this file is on the sensitive files blocklist and cannot be read.",
        sw.ElapsedMilliseconds);
}
```

**File to modify:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/FileWriteTool.cs`

Add `ISensitiveFileGuard` and `IOutputValidator` as constructor dependencies. Before writing:

```csharp
// After PathValidator.ResolveSafePath:
if (_fileGuard?.IsBlockedForWrite(path) == true)
{
    return new ToolExecutionResult(toolCallId, ToolName, false,
        "Access denied: this file is on the critical files blocklist and cannot be written.",
        sw.ElapsedMilliseconds);
}

// Check content size
if (_fileGuard?.ExceedsWriteSizeLimit(content.Length) == true)
{
    return new ToolExecutionResult(toolCallId, ToolName, false,
        $"Content exceeds maximum write size limit.",
        sw.ElapsedMilliseconds);
}

// Scan for embedded credentials
if (_outputValidator != null)
{
    var validation = _outputValidator.Validate(content, $"file_write:{path}");
    if (!validation.IsValid)
    {
        return new ToolExecutionResult(toolCallId, ToolName, false,
            validation.ErrorMessage ?? "Content validation failed.",
            sw.ElapsedMilliseconds);
    }
}
```

**File to modify:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/GitOperationsTool.cs`

Add `GitSafetyValidator` as a constructor dependency. After subcommand validation:

```csharp
// After AllowedSubcommands check:
if (_gitSafetyValidator != null)
{
    var safetyError = _gitSafetyValidator.Validate(subcommand, extraArgs);
    if (safetyError != null)
    {
        _logger.LogWarning(
            "Git operation blocked by safety validator: {ToolName} {ToolCallId} subcommand={Subcommand}",
            ToolName, toolCallId, subcommand);
        return new ToolExecutionResult(toolCallId, ToolName, false, safetyError, sw.ElapsedMilliseconds);
    }
}

// For commit subcommand, scan commit message for credentials:
if (subcommand.Equals("commit", StringComparison.OrdinalIgnoreCase) && _outputValidator != null)
{
    // Extract -m "message" from args
    var messageMatch = Regex.Match(extraArgs, @"-m\s+""([^""]+)""");
    if (messageMatch.Success)
    {
        var validation = _outputValidator.Validate(messageMatch.Groups[1].Value, "git_commit_message");
        if (!validation.IsValid)
        {
            return new ToolExecutionResult(toolCallId, ToolName, false,
                validation.ErrorMessage ?? "Commit message validation failed.", sw.ElapsedMilliseconds);
        }
    }
}
```

---

### Task 10: Integrate rate limiter and audit logger into CallLlmInlineActivity

**File to modify:** `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`

Add `IToolRateLimiter` and `ToolExecutionAuditLogger` as constructor dependencies.

In the `AgenticToolLoop` method, before dispatching each tool call (both sequential and parallel paths):

```csharp
// Rate limit check
if (_rateLimiter != null)
{
    var rateResult = _rateLimiter.CheckAndRecord(toolCall.ToolName);
    if (!rateResult.Allowed)
    {
        result = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
            rateResult.ErrorMessage ?? "Rate limit exceeded.", 0);
        toolsFailed++;

        _auditLogger?.LogSecurityEvent("RATE_LIMITED", toolCall.ToolName,
            toolCall.Id, workflowInstanceId,
            $"Rate limit exceeded, retry after {rateResult.RetryAfterSeconds}s");

        // Skip execution, add error to conversation
        continue;
    }
}
```

After each tool execution (both success and failure):

```csharp
_auditLogger?.LogExecution(new ToolAuditEntry
{
    Timestamp = DateTimeOffset.UtcNow,
    WorkflowInstanceId = workflowInstanceId,
    TurnNumber = step,
    ToolName = toolCall.ToolName,
    ToolCallId = toolCall.Id,
    Success = result.Success,
    DurationMs = result.DurationMs,
    OutputSizeBytes = result.Output?.Length ?? 0,
    ArgumentHash = ComputeArgumentHash(toolCall.ArgumentsJson)
});
```

Helper method:

```csharp
private static string ComputeArgumentHash(string? argumentsJson)
{
    if (string.IsNullOrEmpty(argumentsJson)) return "";
    var bytes = System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(argumentsJson));
    return Convert.ToHexString(bytes)[..16]; // First 16 chars of SHA256
}
```

---

### Task 11: Register all new services in DI

**File to modify:** `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`

```csharp
// Security v2 services
builder.Services.Configure<OutputValidationOptions>(
    builder.Configuration.GetSection("Security:OutputValidation"));
builder.Services.Configure<RateLimitOptions>(
    builder.Configuration.GetSection("Security:RateLimits"));
builder.Services.Configure<FileGuardOptions>(
    builder.Configuration.GetSection("Security:FileGuards"));
builder.Services.Configure<GitSafetyOptions>(
    builder.Configuration.GetSection("Security:GitSafety"));
builder.Services.Configure<AuditLogOptions>(
    builder.Configuration.GetSection("Security:AuditLog"));
builder.Services.Configure<NetworkAccessOptions>(
    builder.Configuration.GetSection("Security:NetworkAccess"));

builder.Services.AddSingleton<CredentialScanner>();
builder.Services.AddSingleton<IOutputValidator, OutputValidator>();
builder.Services.AddSingleton<ISensitiveFileGuard, SensitiveFileGuard>();
builder.Services.AddSingleton<GitSafetyValidator>();
builder.Services.AddSingleton<ToolExecutionAuditLogger>();
builder.Services.AddScoped<IToolRateLimiter, ToolRateLimiter>();
```

Note: `ToolRateLimiter` is `Scoped` (per-request/per-workflow-execution) so rate limit state is isolated per session. For cross-session rate limiting, change to `Singleton`.

---

## Test Execution Order

1. `CredentialScanner.Tests.cs` — standalone, no dependencies
2. `OutputValidator.Tests.cs` — depends on CredentialScanner
3. `ToolRateLimiter.Tests.cs` — standalone
4. `SensitiveFileGuard.Tests.cs` — standalone
5. `CriticalFileGuard.Tests.cs` — standalone (same class, different patterns)
6. `GitSafetyValidator.Tests.cs` — standalone
7. `ToolExecutionAuditLogger.Tests.cs` — standalone
8. `FileReadTool.Tests.cs` — integration with SensitiveFileGuard
9. `FileWriteTool.Tests.cs` — integration with CriticalFileGuard + OutputValidator
10. `GitOperationsTool.Tests.cs` — integration with GitSafetyValidator
11. `CallLlmInlineActivity.Tests.cs` — integration with RateLimiter + AuditLogger

## Risk Mitigation

1. **Performance regression**: All new validators use compiled regexes and are designed for sub-1ms execution. Add benchmarks to CI.
2. **False positives**: CredentialScanner patterns are tuned to require minimum lengths and word boundaries. The `WarnOnlyMode` configuration allows operators to audit before enforcing.
3. **Breaking changes**: All new guards are opt-in via `IOptions`. Default configuration matches current behavior (no blocking) with logging. Strict mode requires explicit opt-in.
4. **Backward compatibility**: New constructor parameters are nullable with defaults. Existing code without DI registration continues to work.
