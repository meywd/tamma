---
title: "Story 3.13: Intelligent Test Execution Pipeline"
sidebar:
  order: 30
---

**Epic**: Epic 3 - Quality Gates & Intelligence Layer
**Status**: Ready for Development
**Priority**: High

## Story

As a **workflow engineer**,
I want the testing pipeline activities to execute real test commands, parse real output, and detect project tooling automatically,
so that the TDD cycle and CI pipeline operate on actual test results instead of mocks, enabling Tamma to autonomously validate code quality across any project type.

## Background: Current State Audit

### What exists today

The testing pipeline is structurally complete but operating on **pre-parsed data** or **mocks** at every layer:

1. **TriggerCIActivity** (`Tamma.Activities/Testing/TriggerCIActivity.cs`): Has a `Testing:UseMock` config flag. In mock mode, it returns a fake `RunId` and URL. In real mode, it POSTs to `Engine:CallbackUrl/api/engine/trigger-ci` -- but there is no implementation behind that endpoint that actually dispatches GitHub Actions or any CI system.

2. **WaitForCIResultsActivity** (`Tamma.Activities/Testing/WaitForCIResultsActivity.cs`): Bookmark-based suspension. Expects a `CIResultsPayload` to be injected externally when the bookmark is resumed. The payload format is already well-defined (`CIResultsPayload` with `TotalTests`, `PassedTests`, `FailedTests`, `CoveragePercentage`, `LintWarnings`, `LintErrors`, `SecurityIssues`, `FailedTestDetails`). But **nothing currently produces this payload** from real CI output.

3. **EvaluateResultsActivity** (`Tamma.Activities/Testing/EvaluateResultsActivity.cs`): Fully implemented with skill-level-aware thresholds and 4-way routing (AllPass/MinorIssues/MajorIssues/Critical). Scoring weights: Coverage 40%, Lint 25%, Security 25%, Build 10%. This activity works correctly -- but it consumes `CIResultsPayload`, which is never populated from real data.

4. **CheckCoverageActivity**: Compares `CIResultsPayload.CoveragePercentage` against skill-level thresholds. The comparison logic works, but `CoveragePercentage` is a single number -- there is **no parsing of lcov, istanbul, cobertura, or go coverage output** to produce this number.

5. **CheckLintingActivity**: Compares `CIResultsPayload.LintWarnings` / `LintErrors` against thresholds. No detection of which linter ran. No parsing of ESLint JSON, Ruff output, golangci-lint output, etc.

6. **CheckSecurityActivity**: Evaluates `CIResultsPayload.SecurityIssues` (a `List<SecurityVulnerability>` with Id, Severity, Package, Description, FixVersion, CveId). No parsing of `npm audit`, `pnpm audit`, Snyk, or CodeQL SARIF output.

7. **CommitFixActivity**: Has real and mock modes. Real mode POSTs to `Engine:CallbackUrl/api/engine/commit-fix`. Mock mode simulates with 85% success rate. No actual file changes happen.

8. **TddWorkflow** (`TddWorkflow.cs`): The RED, GREEN, and REFACTOR phases all use **mock test run results**. Three TODO comments explicitly state: `// TODO: Replace mock test runs with DispatchWorkflow calls to testing-pipeline (7-1C)`. The mocks hardcode `testRunAllPassed = false` (RED) and `testRunAllPassed = true` (GREEN, REFACTOR).

9. **RunTestsTool** (`Tamma.Activities/LlmCall/Tools/RunTestsTool.cs`): Executes real shell commands via `Process`. Captures stdout/stderr. Default command is `dotnet test`. Supports `--filter` argument. Has configurable timeout (default 120s) and CommandValidator security checks. **This is the only real test execution surface**, but it returns raw text output -- no structured parsing.

10. **ShellExecuteTool** (`Tamma.Activities/LlmCall/Tools/ShellExecuteTool.cs`): General shell command executor. Same Process-based approach as RunTestsTool. Security validated. Returns raw text.

### Key gaps

| Gap | Impact |
|-----|--------|
| No test framework detection | Cannot auto-discover whether a project uses vitest, jest, pytest, go test, xunit, etc. |
| No test output parsing | RunTestsTool returns raw text; nothing converts it to structured `CIResultsPayload` |
| No coverage report parsing | `CoveragePercentage` is a placeholder; no lcov/istanbul/cobertura/go cover parser |
| No linter detection or output parsing | `LintWarnings`/`LintErrors` are never populated from real linter output |
| No security scanner output parsing | `SecurityIssues` list is never populated from npm audit / Snyk / SARIF |
| TDD mock test runs | Three phases in TddWorkflow use hardcoded mock results |
| No smart test selection | All tests run every time; no changed-file-based filtering |
| No failure categorization | Test failures are pass/fail; no distinction between syntax error, assertion failure, timeout, environment issue |
| No coverage delta tracking | No comparison of coverage before vs after changes |
| No CI dispatch implementation | `TriggerCIActivity` real mode calls an unimplemented API endpoint |

## Acceptance Criteria

### AC-1: Test Framework Auto-Detection

- [ ] Given a repository path, the system detects the test framework from project configuration files
- [ ] Detection rules:
  - `package.json` with `vitest` in devDependencies or `vitest.config.*` present -> Vitest
  - `package.json` with `jest` in devDependencies or `jest.config.*` present -> Jest
  - `package.json` with `mocha` in devDependencies -> Mocha
  - `pytest.ini`, `pyproject.toml` with `[tool.pytest]`, `setup.cfg` with `[tool:pytest]`, or `conftest.py` -> pytest
  - `*.csproj` with `Microsoft.NET.Test.Sdk` reference, or `*.sln` presence -> dotnet test (xUnit/NUnit/MSTest)
  - `go.mod` present and `*_test.go` files exist -> go test
  - `Cargo.toml` present -> cargo test
  - `pom.xml` or `build.gradle` -> Maven/Gradle (JUnit)
- [ ] Detection returns the test command, coverage command, and expected output format
- [ ] Detection priority: explicit config override > detected framework > fallback to `RunTestsTool` defaults
- [ ] If multiple frameworks detected (e.g., vitest for unit + playwright for e2e), all are reported

### AC-2: Test Output Parsing (Multi-Format)

- [ ] **TAP (Test Anything Protocol)**: Parse `ok` / `not ok` lines, extract test count, pass/fail names, diagnostic messages
- [ ] **JUnit XML**: Parse `<testsuite>`, `<testcase>`, `<failure>`, `<error>`, `<skipped>` elements. Extract test name, suite name, duration, error message, stack trace
- [ ] **Vitest JSON reporter**: Parse `{ testResults: [...], numPassedTests, numFailedTests, numTotalTests }` format. Extract per-test details including file path and duration
- [ ] **Go test JSON** (`go test -json`): Parse `{"Test":"...", "Action":"pass|fail|skip", "Elapsed":...}` streaming lines. Aggregate into suite results
- [ ] **dotnet test / TRX**: Parse Visual Studio Test Results XML. Extract test names, outcomes, duration, error messages
- [ ] **pytest**: Parse pytest's `--tb=short --no-header -q` text output or `--json-report` output. Extract test counts, failure details
- [ ] **Generic exit code**: If no structured output is available, fall back to exit code (0 = pass, non-zero = fail) with raw output capture
- [ ] Parser selection is automatic based on framework detection (AC-1) but can be overridden
- [ ] All parsers produce a normalized `TestExecutionResult` model compatible with `CIResultsPayload`

### AC-3: Smart Test Selection

- [ ] Given a list of changed files (from git diff), determine which test files are related
- [ ] Strategies (applied in order):
  1. **Co-located tests**: `foo.ts` changed -> run `foo.test.ts` / `foo.spec.ts`
  2. **Import analysis**: If `foo.ts` is imported by `bar.test.ts`, include `bar.test.ts`
  3. **Directory-based**: If `src/utils/` changed, run `tests/utils/` or `src/utils/**/*.test.*`
  4. **Framework-native**: Use `vitest --changed`, `jest --changedSince`, `pytest --co -q` with `--last-failed`
- [ ] Fallback: if no related tests found or if the change is to config files (package.json, tsconfig, etc.), run full suite
- [ ] The selection is advisory -- the LLM agent can override it

### AC-4: Coverage Parsing with Delta Tracking

- [ ] Parse coverage output in these formats:
  - **lcov.info**: Parse `SF:`, `DA:`, `FN:`, `BRF:`, `BRH:` records. Calculate line/branch/function coverage
  - **Istanbul/NYC JSON**: Parse `{ "/path/file.ts": { s: {...}, b: {...}, f: {...} } }` format
  - **Cobertura XML**: Parse `<coverage>` element with `line-rate`, `branch-rate` attributes
  - **Go coverage** (`go tool cover -func`): Parse `total: (statements) XX.X%` line
  - **dotnet coverage**: Parse Coverlet's cobertura or JSON output
- [ ] Coverage result includes:
  - Overall line coverage percentage
  - Overall branch coverage percentage
  - Per-file coverage with uncovered line ranges
  - List of files with zero coverage
- [ ] Delta tracking: compare current coverage with previous run (stored in workflow variable or event store)
- [ ] Report whether coverage went up, down, or stayed the same, with per-file delta
- [ ] Populate `CIResultsPayload.CoveragePercentage` from parsed results

### AC-5: Linter Auto-Detection and Output Parsing

- [ ] Detect linter from project files:
  - `.eslintrc.*`, `eslint.config.*`, `package.json` eslintConfig -> ESLint
  - `.prettierrc*`, `package.json` prettier -> Prettier (formatter, not linter -- report separately)
  - `ruff.toml`, `pyproject.toml` with `[tool.ruff]` -> Ruff
  - `.golangci.yml` / `.golangci.yaml` -> golangci-lint
  - `.rubocop.yml` -> RuboCop
  - `biome.json` / `biome.jsonc` -> Biome
- [ ] Parse linter output:
  - **ESLint JSON** (`--format json`): Extract `errorCount`, `warningCount`, per-file issues with line/column/rule
  - **Ruff** (`ruff check --output-format json`): Extract violation code, message, file, line
  - **golangci-lint** (`--out-format json`): Extract linter name, severity, message, file, line
  - **Generic**: Count lines matching `/error|warning/i` patterns as fallback
- [ ] Populate `CIResultsPayload.LintWarnings` and `CIResultsPayload.LintErrors` from parsed results
- [ ] Generate `QualityIssue` entries with `FilePath` and `LineNumber` for each lint finding

### AC-6: Security Scanner Integration

- [ ] Run and parse output from:
  - **npm audit / pnpm audit** (`--json`): Extract advisory ID, severity, package, vulnerable versions, patched versions
  - **pip-audit** (`--format json`): Extract vulnerability ID, package, installed version, fix version
  - **go vuln** (`govulncheck -json`): Extract CVE ID, affected package, symbol, fix version
  - **SARIF** (CodeQL, Semgrep, etc.): Parse `results[]` with `ruleId`, `level`, `message`, `locations`
  - **Trivy** (`--format json`): Extract CVE ID, severity, package, installed/fixed versions
- [ ] Map parsed results to `SecurityVulnerability` model (Id, Severity, Package, Description, FixVersion, CveId)
- [ ] Populate `CIResultsPayload.SecurityIssues` from parsed results
- [ ] Severity mapping: normalize all scanner severities to `SecuritySeverity` enum (Info/Low/Medium/High/Critical)

### AC-7: Test Failure Categorization

- [ ] Classify each test failure into one of these categories:
  - **SyntaxError**: Compilation/parse failure before test runs (detected from error output patterns)
  - **AssertionFailure**: Test assertion failed (`expect().toBe()`, `assertEqual`, `assert`, `Should().Be()`)
  - **Timeout**: Test exceeded time limit (detected from timeout error messages)
  - **EnvironmentIssue**: Missing dependency, connection refused, file not found, permission denied
  - **RuntimeError**: Unhandled exception during test execution (null reference, type error, etc.)
  - **Flaky**: Test that passed on retry but failed initially (requires retry data)
- [ ] Each `FailedTestDetail` in `CIResultsPayload` includes a `FailureCategory` field
- [ ] Category determines auto-fix eligibility:
  - SyntaxError: auto-fixable (code needs correction)
  - AssertionFailure: may need code or test fix (context-dependent)
  - Timeout: auto-fixable (increase timeout or optimize)
  - EnvironmentIssue: NOT auto-fixable (escalate)
  - RuntimeError: auto-fixable (code needs correction)
  - Flaky: mark as flaky, do not block

### AC-8: Wire TDD Workflow to Real Test Execution

- [ ] Replace the 3 mock test run sequences in `TddWorkflow.cs`:
  - RED phase: `mockNewTestsFail` / `mockNewTestsFailCount` / `mockNewTestsPassCount` -> real test execution
  - GREEN phase: `mockFullTestsPass` / `mockFullTestsPassedCount` / `mockFullTestsFailedCount` -> real test execution
  - REFACTOR phase: `mockRefactorTestsPass` -> real test execution
- [ ] Each replacement dispatches the test framework detection, runs tests, and parses output
- [ ] RED phase validates that new tests FAIL (correct TDD behavior)
- [ ] GREEN phase validates that ALL tests PASS after implementation
- [ ] REFACTOR phase validates that tests still PASS after refactoring

### AC-9: Wire Testing Pipeline to Real CI

- [ ] `TriggerCIActivity` real mode triggers GitHub Actions workflow dispatch via Octokit or REST API
- [ ] Support `workflow_dispatch` trigger with inputs (branch, commit SHA)
- [ ] Poll for workflow run completion or use webhook callback to resume bookmark
- [ ] Parse GitHub Actions job outputs and artifacts to populate `CIResultsPayload`
- [ ] Alternative: for projects without CI, run tests locally using framework detection + test execution + output parsing

## Technical Context

### Models to Extend

#### FailedTestDetail -- add FailureCategory

```csharp
public class FailedTestDetail
{
    public string TestName { get; set; } = string.Empty;
    public string? TestSuite { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
    public TimeSpan Duration { get; set; }
    public FailureCategory Category { get; set; } // NEW
    public bool AutoFixable { get; set; }          // NEW
}

public enum FailureCategory
{
    Unknown,
    SyntaxError,
    AssertionFailure,
    Timeout,
    EnvironmentIssue,
    RuntimeError,
    Flaky
}
```

#### CIResultsPayload -- add metadata

```csharp
public class CIResultsPayload
{
    // ... existing fields ...
    public string? DetectedFramework { get; set; }     // NEW: "vitest", "jest", "pytest", etc.
    public string? DetectedLinter { get; set; }        // NEW: "eslint", "ruff", etc.
    public string? DetectedSecurityScanner { get; set; } // NEW: "npm-audit", "trivy", etc.
    public double? PreviousCoveragePercentage { get; set; } // NEW: for delta tracking
    public double CoverageDelta { get; set; }            // NEW: current - previous
}
```

### New Activity: DetectProjectToolingActivity

An ELSA activity that inspects a repository directory and returns detected test framework, linter, and security scanner.

### New Activity: RunTestsAndParseActivity

An ELSA activity that:
1. Receives a repository path and optional test filter
2. Detects the test framework (or uses provided override)
3. Executes the test command via Process (reusing RunTestsTool's process management)
4. Parses the output using the appropriate parser
5. Returns a structured `TestExecutionResult`

### New Activity: RunLinterAndParseActivity

Similar to above but for linting. Detects linter, runs it, parses output.

### New Activity: RunSecurityScanAndParseActivity

Similar to above but for security scanning. Detects scanner, runs it, parses output.

## Testing Strategy

### Unit Tests

- Framework detection logic: provide mock file system contents, verify correct framework detected
- Each output parser: provide sample output strings for each format, verify parsed results
- Coverage delta calculation: provide two coverage results, verify delta
- Failure categorization: provide error messages from each category, verify classification
- Smart test selection: provide changed files and test file patterns, verify selection

### Integration Tests

- Run `vitest --reporter=json` on a real TypeScript project, verify output is parsed correctly
- Run `dotnet test --logger trx` on a real .NET project, verify TRX parsing
- Run `eslint --format json` on a project with known lint issues, verify error/warning counts
- Run `npm audit --json` on a project with known vulnerabilities, verify security issue parsing

### Performance Tests

- Parse a JUnit XML file with 10,000 test cases in under 500ms
- Parse an lcov.info file with 1,000 source files in under 200ms

## Dependencies

- **Story 3.1**: Build Automation Gate (defines build system detection patterns we can reuse)
- **Story 3.2**: Test Execution Gate (defines the quality gate interfaces we implement)
- **Story 13.1**: TDD Debug Retry Sub-Workflow (the TDD retry loop we wire real tests into)
- **Story 13.2**: CI Debug Retry Sub-Workflow (the CI retry loop that depends on real test results)

## Estimated Effort

5 days

## Logging Requirements

### Required Log Events

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| Framework detected | INFO | `{Repository}`, `{Framework}`, `{DetectionSource}` | What was detected and how (file match, config parse) |
| Framework detection failed | WARN | `{Repository}`, `{FilesChecked}` | No known framework found |
| Test execution started | INFO | `{Repository}`, `{Framework}`, `{Command}`, `{TestFilter}` | Actual command being run |
| Test execution completed | INFO | `{Repository}`, `{Framework}`, `{ExitCode}`, `{DurationMs}`, `{TotalTests}`, `{PassedTests}`, `{FailedTests}` | Summary of results |
| Test output parse started | DEBUG | `{Framework}`, `{OutputFormat}`, `{OutputSizeBytes}` | Which parser is being used |
| Test output parse failed | ERROR | `{Framework}`, `{OutputFormat}`, `{ErrorMessage}` | Parser could not process output |
| Coverage parsed | INFO | `{Repository}`, `{CoveragePercent}`, `{PreviousCoveragePercent}`, `{CoverageDelta}` | Coverage with delta |
| Linter detected | INFO | `{Repository}`, `{Linter}`, `{DetectionSource}` | Which linter and how detected |
| Linter execution completed | INFO | `{Repository}`, `{Linter}`, `{ErrorCount}`, `{WarningCount}`, `{DurationMs}` | Lint results summary |
| Security scan completed | INFO | `{Repository}`, `{Scanner}`, `{CriticalCount}`, `{HighCount}`, `{MediumCount}`, `{LowCount}` | Scan results summary |
| Failure categorized | DEBUG | `{TestName}`, `{Category}`, `{AutoFixable}`, `{ErrorSnippet}` | How each failure was classified |
| Smart test selection | INFO | `{ChangedFileCount}`, `{SelectedTestCount}`, `{SelectionStrategy}` | Which tests were selected and why |

### Sensitive Data Redaction

- Do NOT log full test output (may contain secrets in environment variable dumps)
- Do NOT log security vulnerability details beyond severity + package name
- Log only test names, file paths, and summary counts
- Truncate error messages to 500 characters max

## References

- **Mandatory Process**: [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base**: [.dev/README.md](../../.dev/README.md)
- **Story 3.1**: [Build Automation Gate](../story-3-1/3-1-build-automation-gate-implementation.md)
- **Story 3.2**: [Test Execution Gate](../story-3-2/3-2-test-execution-gate-implementation.md)
- **Story 13.1**: [TDD Debug Retry](../../epic-13/13-1-tdd-debug-retry-sub-workflow.md)
- **Story 13.2**: [CI Debug Retry](../../epic-13/13-2-ci-debug-retry-sub-workflow.md)

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-31 | 1.0 | Initial story from deep audit of CI/Testing pipeline | Architecture Team |
