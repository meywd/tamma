---
title: "Story 11.3: Tool Call Validation"
sidebar:
  order: 110
---

Status: ready-for-dev

## Story

As a **security engineer**,
I want all LLM-returned tool calls validated against an allowlist with argument schema checking before execution,
so that a compromised or manipulated LLM response cannot invoke unauthorized tools, execute dangerous shell commands, or pass oversized/malformed arguments.

## Acceptance Criteria

1. `IToolCallValidator` interface exists with `Validate(toolCall, allowedTools)` method returning a validation result
2. `ToolCallValidator` rejects tool names not present in the sent tools list (the LLM cannot call tools it was not offered)
3. Tool name format enforced: `^[a-zA-Z0-9_-]{1,64}$` (no special characters, max 64 chars)
4. Tool arguments are validated: JSON parseable, total size under 100KB, string values sanitized via `IContentSanitizer`
5. `ActionGate` class exists with blocked command patterns for shell/exec tools (e.g., `rm -rf /`, `curl | bash`, `wget`, `chmod 777`, `sudo`, credential access commands)
6. `ActionGate.IsBlocked(command)` returns true for any command matching blocked patterns
7. Validation is wired into the LLM response processing path — rejected tool calls return an error message to the LLM (not a crash)
8. 20+ unit tests covering allowlist enforcement, name format validation, argument validation, size limits, and blocked command patterns

## Technical Context

### Attack Vectors

LLM tool calls are a primary injection surface:
- **Tool name injection**: LLM returns a tool name that was not in the sent tools list (e.g., `eval`, `exec`, `system`)
- **Argument overflow**: Extremely large JSON arguments designed to exhaust memory or bypass validation
- **Argument injection**: String arguments containing shell metacharacters, path traversal (`../../etc/passwd`), or SQL injection
- **Dangerous shell commands**: LLM instructed (via prompt injection) to execute `rm -rf`, credential exfiltration, or reverse shells

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/Security/IToolCallValidator.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ToolCallValidator.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs`

### Files to Modify

- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` (DI registration for `IToolCallValidator`)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` (validate tool calls after LLM response, before execution)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs` (validate tool calls after LLM response)

### Validation Flow

```
LLM returns tool_call(name, arguments)
  |
  v
ToolCallValidator.Validate(toolCall, sentTools)
  |-- Name in sentTools?         NO --> return error result to LLM
  |-- Name matches format regex? NO --> return error result to LLM
  |-- Arguments parse as JSON?   NO --> return error result to LLM
  |-- Arguments < 100KB?         NO --> return error result to LLM
  |-- String args sanitized?     YES (mutate in place)
  |
  v (if shell/exec tool)
ActionGate.IsBlocked(command)
  |-- Matches blocked pattern?   YES --> return error result to LLM
  |
  v
Execute tool normally
```

### Blocked Command Patterns (ActionGate)

```
rm\s+-rf\s+/           # recursive delete from root
rm\s+-rf\s+~           # recursive delete home
curl.*\|\s*bash         # pipe curl to bash
wget.*\|\s*bash         # pipe wget to bash
chmod\s+777             # world-writable permissions
sudo\s+                 # privilege escalation
passwd                  # password changes
/etc/shadow             # credential file access
\.env                   # environment file access
eval\s*\(               # code evaluation
exec\s*\(               # code execution
>\s*/dev/               # device file writes
mkfs                    # filesystem formatting
dd\s+if=                # raw disk operations
nc\s+-l                 # netcat listener (reverse shell)
python.*-c.*import\s+os # python os command execution
```

## Implementation Notes

1. The validator returns a `ToolCallValidationResult` with `IsValid`, `ErrorMessage`, and `SanitizedArguments`. When invalid, the error message is fed back to the LLM as a tool result with `success: false` — the loop continues, giving the LLM a chance to correct itself.
2. `ActionGate` patterns should be configurable (loaded from config) but ship with sane defaults. Use `IOptions<ActionGateOptions>` for configuration.
3. Argument sanitization reuses `IContentSanitizer.SanitizeInput()` on all string-valued properties in the JSON arguments (recursive walk).
4. The 100KB size limit is on the serialized JSON string, not the deserialized object graph.
5. Validation must be synchronous and fast (no async I/O). Target: under 1ms per tool call.

## Testing Strategy

- **Allowlist tests** (5): Tool name in list passes, tool name not in list fails, empty list rejects all, case sensitivity, duplicate names
- **Name format tests** (4): Valid names pass, special characters rejected, too-long names rejected, empty name rejected
- **Argument validation tests** (5): Valid JSON passes, invalid JSON rejected, oversized JSON rejected, string values sanitized, nested objects handled
- **ActionGate tests** (6): Each blocked pattern category tested, safe commands pass, partial matches handled, case variations
- **Integration tests** (2): End-to-end validation in CallLlmInlineActivity — rejected tool returns error to LLM, accepted tool executes
- **Test files**:
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/ToolCallValidatorTests.cs`
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/ActionGateTests.cs`

## Dependencies

- **Story 11.1** (ContentSanitizer C# Port) — provides `IContentSanitizer` for argument string sanitization

## Estimated Effort

2 days

## Logging Requirements

### Existing Coverage

The story has **no logging requirements** specified. This is a critical gap for a security-sensitive component.

### Required Additions

`ToolCallValidator` and `ActionGate` **must** inject `ILogger<T>` via constructor.

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| Tool call validation started | DEBUG | `{ToolName}`, `{ToolCallId}`, `{ArgumentsSizeBytes}`, `{WorkflowInstanceId}` | Entry point trace |
| Tool call rejected: not in allowlist | WARN | `{ToolName}`, `{ToolCallId}`, `{AllowedToolCount}`, `{WorkflowInstanceId}` | Security event — operator should know about unauthorized tool attempts |
| Tool call rejected: invalid name format | WARN | `{ToolName}`, `{ToolCallId}`, `{WorkflowInstanceId}` | Possible injection attempt |
| Tool call rejected: invalid JSON arguments | WARN | `{ToolCallId}`, `{ToolName}`, `{ErrorMessage}`, `{WorkflowInstanceId}` | Do NOT log the raw arguments (may contain injection payloads) |
| Tool call rejected: arguments exceed size limit | WARN | `{ToolCallId}`, `{ToolName}`, `{ArgumentsSizeBytes}`, `{MaxSizeBytes}`, `{WorkflowInstanceId}` | Memory protection trigger |
| Tool arguments sanitized | DEBUG | `{ToolCallId}`, `{ToolName}`, `{StringFieldsSanitizedCount}` | How many string fields were sanitized in the recursive walk |
| ActionGate: command blocked | WARN | `{ToolCallId}`, `{ToolName}`, `{BlockedPatternName}`, `{WorkflowInstanceId}` | Never log the actual command — it may contain credentials or dangerous payloads |
| ActionGate: command allowed | DEBUG | `{ToolCallId}`, `{ToolName}` | Only log tool name, never the command itself |
| Tool call validation passed | DEBUG | `{ToolCallId}`, `{ToolName}`, `{ValidationDurationMs}` | Exit point trace with performance metric |

### Sensitive Data Redaction

- **Never** log raw tool call arguments — they may contain shell commands, file paths, or injected content.
- **Never** log the blocked command string — only the pattern name that triggered the block.
- Log only tool names (which are from a known vocabulary), tool call IDs, and counts.

### Correlation IDs

- All log messages from `ToolCallValidator` and `ActionGate` must include `{WorkflowInstanceId}` and `{ToolCallId}` for cross-referencing with the tool loop logs in Story 12.2.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/llm-injection-security-fix.md` Phase 3 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |
