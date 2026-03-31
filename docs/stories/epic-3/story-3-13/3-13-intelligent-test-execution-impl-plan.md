# Story 3.13: Intelligent Test Execution Pipeline - Implementation Plan

## Overview

Wire real test execution, output parsing, and tooling detection into the Tamma testing pipeline. This replaces the current mock/pre-parsed data flow with actual shell command execution, structured output parsing, and intelligent framework detection.

**Scope**: 6 new service classes, 4 new ELSA activities, 3 model extensions, and modifications to TddWorkflow + TriggerCIActivity.

**Source files to modify**:
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Models/TestingModels.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/TriggerCIActivity.cs`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWorkflow.cs`

**Source files to create**:
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/ProjectToolingDetector.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/TestOutputParserFactory.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/Parsers/JUnitXmlParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/Parsers/VitestJsonParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/Parsers/GoTestJsonParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/Parsers/TapParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/Parsers/DotnetTrxParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/Parsers/PytestParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/Parsers/GenericExitCodeParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/CoverageParserFactory.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/CoverageParsers/LcovParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/CoverageParsers/IstanbulJsonParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/CoverageParsers/CoberturaXmlParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/CoverageParsers/GoCoverageParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/LinterDetector.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/LinterOutputParserFactory.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/LinterParsers/EslintJsonParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/LinterParsers/RuffJsonParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/LinterParsers/GolangciLintJsonParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/SecurityScannerDetector.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/SecurityOutputParserFactory.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/SecurityParsers/NpmAuditJsonParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/SecurityParsers/SarifParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/SecurityParsers/TrivyJsonParser.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/FailureCategorizer.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/SmartTestSelector.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/DetectProjectToolingActivity.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/RunTestsAndParseActivity.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/RunLinterAndParseActivity.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/RunSecurityScanAndParseActivity.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Models/ToolingDetectionModels.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Testing/Models/TestExecutionModels.cs`

---

## Phase 1: Model Extensions and Tooling Detection

### Step 1.1: Extend TestingModels.cs

**File**: `apps/tamma-elsa/src/Tamma.Activities/Testing/Models/TestingModels.cs`

Add the failure category enum and extend existing models:

```csharp
// Add to FailedTestDetail:
public FailureCategory Category { get; set; }
public bool AutoFixable { get; set; }

// New enum:
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

// Add to CIResultsPayload:
public string? DetectedFramework { get; set; }
public string? DetectedLinter { get; set; }
public string? DetectedSecurityScanner { get; set; }
public double? PreviousCoveragePercentage { get; set; }
public double CoverageDelta { get; set; }
```

### Step 1.2: Create Tooling Detection Models

**New file**: `apps/tamma-elsa/src/Tamma.Activities/Testing/Models/ToolingDetectionModels.cs`

```csharp
namespace Tamma.Activities.Testing.Models;

/// <summary>
/// Result of project tooling detection — which test framework, linter,
/// security scanner, and coverage tool a project uses.
/// </summary>
public class ProjectToolingResult
{
    public TestFrameworkInfo? TestFramework { get; set; }
    public LinterInfo? Linter { get; set; }
    public SecurityScannerInfo? SecurityScanner { get; set; }
    public CoverageToolInfo? CoverageTool { get; set; }
    public string? PackageManager { get; set; } // "pnpm", "npm", "yarn", "pip", "go", "dotnet", "cargo"
    public string? Language { get; set; } // "typescript", "javascript", "python", "go", "csharp", "rust"
    public List<string> DetectionLog { get; set; } = new();
}

public class TestFrameworkInfo
{
    public string Name { get; set; } = string.Empty; // "vitest", "jest", "pytest", "go-test", "dotnet-test", "cargo-test"
    public string TestCommand { get; set; } = string.Empty; // "pnpm vitest run --reporter=json"
    public string CoverageCommand { get; set; } = string.Empty; // "pnpm vitest run --coverage --reporter=json"
    public string OutputFormat { get; set; } = string.Empty; // "vitest-json", "junit-xml", "tap", "go-test-json", "trx"
    public string TestFilePattern { get; set; } = string.Empty; // "**/*.test.ts", "*_test.go"
    public string? ConfigFile { get; set; } // "vitest.config.ts", "jest.config.js"
    public string DetectionSource { get; set; } = string.Empty; // how it was detected
}

public class LinterInfo
{
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty; // "pnpm eslint . --format json"
    public string OutputFormat { get; set; } = string.Empty; // "eslint-json", "ruff-json", "golangci-json"
    public string? ConfigFile { get; set; }
    public string DetectionSource { get; set; } = string.Empty;
}

public class SecurityScannerInfo
{
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string OutputFormat { get; set; } = string.Empty; // "npm-audit-json", "sarif", "trivy-json"
    public string DetectionSource { get; set; } = string.Empty;
}

public class CoverageToolInfo
{
    public string Name { get; set; } = string.Empty;
    public string OutputFormat { get; set; } = string.Empty; // "lcov", "istanbul-json", "cobertura-xml", "go-cover"
    public string OutputPath { get; set; } = string.Empty; // "coverage/lcov.info"
}
```

### Step 1.3: Create Test Execution Result Models

**New file**: `apps/tamma-elsa/src/Tamma.Activities/Testing/Models/TestExecutionModels.cs`

```csharp
namespace Tamma.Activities.Testing.Models;

/// <summary>
/// Normalized test execution result produced by any test output parser.
/// This is the bridge between raw parser output and CIResultsPayload.
/// </summary>
public class TestExecutionResult
{
    public bool AllPassed { get; set; }
    public int TotalTests { get; set; }
    public int PassedTests { get; set; }
    public int FailedTests { get; set; }
    public int SkippedTests { get; set; }
    public TimeSpan Duration { get; set; }
    public int ExitCode { get; set; }
    public List<ParsedTestCase> TestCases { get; set; } = new();
    public List<FailedTestDetail> FailedTestDetails { get; set; } = new();
    public string? RawOutput { get; set; }
    public string ParserUsed { get; set; } = string.Empty;
}

public class ParsedTestCase
{
    public string Name { get; set; } = string.Empty;
    public string? Suite { get; set; }
    public string? FilePath { get; set; }
    public TestOutcome Outcome { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
}

public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
    Error
}

/// <summary>
/// Normalized coverage result produced by any coverage output parser.
/// </summary>
public class CoverageResult
{
    public double LineCoveragePercent { get; set; }
    public double BranchCoveragePercent { get; set; }
    public double FunctionCoveragePercent { get; set; }
    public double StatementCoveragePercent { get; set; }
    public int TotalLines { get; set; }
    public int CoveredLines { get; set; }
    public int TotalBranches { get; set; }
    public int CoveredBranches { get; set; }
    public List<FileCoverage> FileCoverages { get; set; } = new();
    public string ParserUsed { get; set; } = string.Empty;
}

public class FileCoverage
{
    public string FilePath { get; set; } = string.Empty;
    public double LineCoveragePercent { get; set; }
    public int TotalLines { get; set; }
    public int CoveredLines { get; set; }
    public List<int> UncoveredLineNumbers { get; set; } = new();
}

/// <summary>
/// Normalized linter result produced by any linter output parser.
/// </summary>
public class LinterResult
{
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public int FixableCount { get; set; }
    public List<LintIssue> Issues { get; set; } = new();
    public string ParserUsed { get; set; } = string.Empty;
}

public class LintIssue
{
    public string FilePath { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    public string Severity { get; set; } = string.Empty; // "error", "warning", "info"
    public string RuleId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool Fixable { get; set; }
}

/// <summary>
/// Normalized security scan result.
/// </summary>
public class SecurityScanResult
{
    public List<SecurityVulnerability> Vulnerabilities { get; set; } = new();
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }
    public string ParserUsed { get; set; } = string.Empty;
}
```

### Step 1.4: Create ProjectToolingDetector Service

**New file**: `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/ProjectToolingDetector.cs`

This is the core detection service. It inspects the file system at a given repository path.

```csharp
namespace Tamma.Activities.Testing.Services;

public interface IProjectToolingDetector
{
    Task<ProjectToolingResult> DetectAsync(string repositoryPath, CancellationToken ct = default);
}

public class ProjectToolingDetector : IProjectToolingDetector
{
    // Detection priority order for test frameworks
    private static readonly (string FileName, string? ContentMatch, string Framework, string TestCmd, string CovCmd, string OutputFmt, string TestPattern)[] TestFrameworkRules =
    {
        // Vitest (check before Jest -- vitest projects often also have jest in transitive deps)
        ("vitest.config.ts", null, "vitest", "npx vitest run --reporter=json", "npx vitest run --coverage --reporter=json", "vitest-json", "**/*.test.ts"),
        ("vitest.config.js", null, "vitest", "npx vitest run --reporter=json", "npx vitest run --coverage --reporter=json", "vitest-json", "**/*.test.ts"),
        ("vitest.config.mts", null, "vitest", "npx vitest run --reporter=json", "npx vitest run --coverage --reporter=json", "vitest-json", "**/*.test.ts"),
        // package.json check for vitest devDependency handled in code

        // Jest
        ("jest.config.js", null, "jest", "npx jest --json", "npx jest --coverage --json", "jest-json", "**/*.test.{ts,tsx,js,jsx}"),
        ("jest.config.ts", null, "jest", "npx jest --json", "npx jest --coverage --json", "jest-json", "**/*.test.{ts,tsx,js,jsx}"),
        ("jest.config.mjs", null, "jest", "npx jest --json", "npx jest --coverage --json", "jest-json", "**/*.test.{ts,tsx,js,jsx}"),

        // pytest
        ("pytest.ini", null, "pytest", "python -m pytest --tb=short -q --junitxml=test-results.xml", "python -m pytest --cov --cov-report=json --junitxml=test-results.xml", "junit-xml", "**/test_*.py"),
        ("conftest.py", null, "pytest", "python -m pytest --tb=short -q --junitxml=test-results.xml", "python -m pytest --cov --cov-report=json --junitxml=test-results.xml", "junit-xml", "**/test_*.py"),

        // Go test
        ("go.mod", null, "go-test", "go test ./... -json", "go test ./... -json -coverprofile=coverage.out", "go-test-json", "*_test.go"),

        // dotnet test
        // Detected by scanning for *.csproj with test SDK reference

        // Cargo test
        ("Cargo.toml", null, "cargo-test", "cargo test -- --format=json -Z unstable-options", "cargo tarpaulin --out json", "cargo-test-json", "**/tests/**/*.rs"),
    };

    public async Task<ProjectToolingResult> DetectAsync(string repositoryPath, CancellationToken ct = default)
    {
        var result = new ProjectToolingResult();

        // Detect language and package manager first
        DetectLanguageAndPackageManager(repositoryPath, result);

        // Detect test framework
        await DetectTestFramework(repositoryPath, result, ct);

        // Detect linter
        DetectLinter(repositoryPath, result);

        // Detect security scanner (based on package manager)
        DetectSecurityScanner(repositoryPath, result);

        // Detect coverage tool (based on test framework)
        DetectCoverageTool(repositoryPath, result);

        return result;
    }

    // Implementation methods: check for config files, parse package.json, etc.
    // Each adds entries to result.DetectionLog for traceability.
}
```

The detection logic for each method:

**`DetectLanguageAndPackageManager`**:
- `package.json` exists -> JavaScript/TypeScript. Check for `pnpm-lock.yaml` (pnpm), `yarn.lock` (yarn), `package-lock.json` (npm)
- `go.mod` exists -> Go
- `*.csproj` or `*.sln` exists -> C#/dotnet
- `pyproject.toml` or `requirements.txt` or `setup.py` exists -> Python
- `Cargo.toml` exists -> Rust

**`DetectTestFramework`**:
1. Check file-based rules in order (vitest.config.*, jest.config.*, pytest.ini, etc.)
2. If `package.json` exists, parse it:
   - Check `devDependencies` for `vitest`, `jest`, `mocha`
   - Check `scripts.test` for the test command (e.g., `"test": "vitest"`)
3. If `pyproject.toml` exists, check for `[tool.pytest.ini_options]`
4. If `*.csproj` exists, check for `Microsoft.NET.Test.Sdk` PackageReference
5. If `go.mod` exists, check for `*_test.go` files
6. Adjust test command based on detected package manager (e.g., `pnpm vitest` vs `npx vitest`)

**`DetectLinter`**: Check for `.eslintrc.*`, `eslint.config.*`, `biome.json`, `ruff.toml`, `pyproject.toml [tool.ruff]`, `.golangci.yml`

**`DetectSecurityScanner`**: Based on package manager -- npm/pnpm/yarn -> `npm audit --json` / `pnpm audit --json`; pip -> `pip-audit --format json`; go -> `govulncheck -json ./...`

**`DetectCoverageTool`**: Based on test framework -- vitest -> istanbul (built-in `--coverage`), jest -> istanbul, pytest -> `pytest-cov`, go -> `go tool cover`, dotnet -> coverlet

### Step 1.5: Create DetectProjectToolingActivity

**New file**: `apps/tamma-elsa/src/Tamma.Activities/Testing/DetectProjectToolingActivity.cs`

```csharp
[Activity("Tamma.Testing", "Detect Project Tooling",
    "Detect test framework, linter, and security scanner from project files",
    Kind = ActivityKind.Task)]
public class DetectProjectToolingActivity : CodeActivity<ProjectToolingResult>
{
    [Input(Description = "Repository path to scan")]
    public Input<string> RepositoryPath { get; set; } = default!;

    // Inject IProjectToolingDetector via constructor
    // Call DetectAsync, set result
}
```

### Verification

- `dotnet build` succeeds
- Unit test: `ProjectToolingDetectorTests.cs` with mock file system verifying detection of vitest, jest, pytest, go-test, dotnet-test projects

---

## Phase 2: Test Output Parsers

### Step 2.1: Create Parser Interface and Factory

**New file**: `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/TestOutputParserFactory.cs`

```csharp
namespace Tamma.Activities.Testing.Services;

public interface ITestOutputParser
{
    string FormatName { get; } // "vitest-json", "junit-xml", "go-test-json", etc.
    TestExecutionResult Parse(string output, int exitCode);
}

public class TestOutputParserFactory
{
    private readonly Dictionary<string, ITestOutputParser> _parsers = new();

    public TestOutputParserFactory()
    {
        Register(new VitestJsonParser());
        Register(new JUnitXmlParser());
        Register(new GoTestJsonParser());
        Register(new TapParser());
        Register(new DotnetTrxParser());
        Register(new PytestParser());
        Register(new GenericExitCodeParser());
    }

    public ITestOutputParser GetParser(string formatName)
    {
        if (_parsers.TryGetValue(formatName, out var parser))
            return parser;
        return _parsers["generic"]; // fallback
    }

    private void Register(ITestOutputParser parser)
    {
        _parsers[parser.FormatName] = parser;
    }
}
```

### Step 2.2: Implement VitestJsonParser

**New file**: `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/Parsers/VitestJsonParser.cs`

Parses Vitest's `--reporter=json` output:

```json
{
  "numTotalTestSuites": 5,
  "numPassedTestSuites": 4,
  "numFailedTestSuites": 1,
  "numTotalTests": 42,
  "numPassedTests": 40,
  "numFailedTests": 2,
  "numPendingTests": 0,
  "testResults": [
    {
      "name": "/path/to/file.test.ts",
      "status": "failed",
      "assertionResults": [
        {
          "fullName": "describe > should work",
          "status": "passed",
          "duration": 5
        },
        {
          "fullName": "describe > should fail",
          "status": "failed",
          "duration": 2,
          "failureMessages": ["Expected true to be false"]
        }
      ]
    }
  ]
}
```

Implementation:
- Deserialize JSON using `System.Text.Json`
- Map `assertionResults` to `ParsedTestCase` list
- Map failures to `FailedTestDetail` with error messages
- Handle edge case: vitest may prefix output with console logs before the JSON -- find the first `{` that starts the JSON object

### Step 2.3: Implement JUnitXmlParser

Parses standard JUnit XML format (used by pytest, Maven Surefire, GitHub Actions, etc.):

```xml
<testsuites tests="10" failures="2" errors="1" time="1.5">
  <testsuite name="MyTests" tests="5" failures="1">
    <testcase name="test_foo" classname="MyTests" time="0.1"/>
    <testcase name="test_bar" classname="MyTests" time="0.2">
      <failure message="Expected 1 but got 2">Stack trace here</failure>
    </testcase>
  </testsuite>
</testsuites>
```

Implementation:
- Use `System.Xml.Linq.XDocument.Parse()`
- Handle both `<testsuites>` (wrapper) and single `<testsuite>` root
- Extract test names, durations, failure messages, stack traces
- Map `<failure>` to AssertionFailure category, `<error>` to RuntimeError category

### Step 2.4: Implement GoTestJsonParser

Parses `go test -json` streaming JSON lines:

```json
{"Time":"2024-01-01T00:00:00Z","Action":"run","Package":"pkg","Test":"TestFoo"}
{"Time":"2024-01-01T00:00:01Z","Action":"output","Package":"pkg","Test":"TestFoo","Output":"=== RUN   TestFoo\n"}
{"Time":"2024-01-01T00:00:01Z","Action":"pass","Package":"pkg","Test":"TestFoo","Elapsed":0.5}
{"Time":"2024-01-01T00:00:02Z","Action":"fail","Package":"pkg","Test":"TestBar","Elapsed":0.3}
```

Implementation:
- Split output by newlines, parse each line as `JsonElement`
- Track test state: `run` starts a test, `pass`/`fail`/`skip` completes it
- Collect `output` lines between `run` and completion for error messages
- Handle package-level `pass`/`fail` (where `Test` is empty)

### Step 2.5: Implement TapParser

Parses TAP (Test Anything Protocol):

```
TAP version 13
1..5
ok 1 - should add numbers
not ok 2 - should handle nulls
  ---
  message: Expected undefined to be null
  severity: fail
  ---
ok 3 - should format string
```

Implementation:
- Parse `1..N` plan line for total test count
- Parse `ok N` / `not ok N` lines for pass/fail
- Parse YAML diagnostic blocks (indented `---` ... `---`) for failure details

### Step 2.6: Implement DotnetTrxParser

Parses Visual Studio Test Results (.trx) XML:

```xml
<TestRun>
  <Results>
    <UnitTestResult testName="MyTest" outcome="Passed" duration="00:00:01.234"/>
    <UnitTestResult testName="FailTest" outcome="Failed" duration="00:00:00.567">
      <Output><ErrorInfo><Message>Assert.Equal failure</Message><StackTrace>at ...</StackTrace></ErrorInfo></Output>
    </UnitTestResult>
  </Results>
</TestRun>
```

### Step 2.7: Implement PytestParser

Parses pytest text output (with `--tb=short`) or JSON report:

```
FAILED tests/test_foo.py::test_bar - assert 1 == 2
PASSED tests/test_foo.py::test_baz
=============== 1 failed, 1 passed in 0.5s ===============
```

Implementation:
- Regex match `FAILED|PASSED|SKIPPED` lines
- Parse summary line for counts
- If `--json-report` output is available, prefer it

### Step 2.8: Implement GenericExitCodeParser

Fallback parser when no structured output format is available:

```csharp
public class GenericExitCodeParser : ITestOutputParser
{
    public string FormatName => "generic";

    public TestExecutionResult Parse(string output, int exitCode)
    {
        return new TestExecutionResult
        {
            AllPassed = exitCode == 0,
            TotalTests = exitCode == 0 ? 1 : 1,
            PassedTests = exitCode == 0 ? 1 : 0,
            FailedTests = exitCode == 0 ? 0 : 1,
            ExitCode = exitCode,
            RawOutput = output,
            ParserUsed = "generic"
        };
    }
}
```

### Verification

- Unit tests for each parser with sample output fixtures
- Test file: `apps/tamma-elsa/tests/Tamma.Activities.Tests/Testing/Services/Parsers/` (one test class per parser)
- Each test provides known sample output and asserts correct counts, names, error messages

---

## Phase 3: Coverage, Linter, and Security Parsers

### Step 3.1: Coverage Parser Factory and Implementations

**Interface**: `ICoverageParser` with `CoverageResult Parse(string output)`

**Parsers**:

1. **LcovParser**: Parse `SF:path`, `DA:line,hits`, `FN:line,name`, `FNF:count`, `FNH:count`, `BRF:count`, `BRH:count`, `LF:count`, `LH:count` records. Group by source file.

2. **IstanbulJsonParser**: Parse NYC/Istanbul JSON coverage output. Each file key maps to `{ s: {}, b: {}, f: {} }` objects where keys are statement/branch/function indices and values are hit counts.

3. **CoberturaXmlParser**: Parse `<coverage line-rate="0.85" branch-rate="0.75">` with `<package>` / `<class>` / `<line>` elements.

4. **GoCoverageParser**: Parse `go tool cover -func=coverage.out` output. Last line contains `total: (statements) XX.X%`. Also parse individual function lines for per-file breakdown.

### Step 3.2: Linter Detector and Parsers

**LinterDetector** (`ILinterDetector`): Same pattern as ProjectToolingDetector but focused on linters. Check config files:

| File | Linter | Command |
|------|--------|---------|
| `.eslintrc.*`, `eslint.config.*` | ESLint | `npx eslint . --format json --no-error-on-unmatched-pattern` |
| `biome.json`, `biome.jsonc` | Biome | `npx biome check . --reporter=json` |
| `ruff.toml`, `pyproject.toml [tool.ruff]` | Ruff | `ruff check --output-format json .` |
| `.golangci.yml` | golangci-lint | `golangci-lint run --out-format json` |
| `.rubocop.yml` | RuboCop | `rubocop --format json` |

**Parsers**:

1. **EslintJsonParser**: Parse ESLint's `--format json` output -- array of `{ filePath, messages: [{ line, column, severity, ruleId, message }], errorCount, warningCount }`.

2. **RuffJsonParser**: Parse Ruff's `--output-format json` -- array of `{ code, message, filename, location: { row, column } }`.

3. **GolangciLintJsonParser**: Parse `--out-format json` -- `{ Issues: [{ FromLinter, Text, Pos: { Filename, Line, Column }, Severity }] }`.

### Step 3.3: Security Scanner Detector and Parsers

**SecurityScannerDetector**: Based on package manager:

| Package Manager | Scanner | Command |
|-----------------|---------|---------|
| npm | npm audit | `npm audit --json` |
| pnpm | pnpm audit | `pnpm audit --json` |
| yarn | yarn audit | `yarn audit --json` |
| pip | pip-audit | `pip-audit --format json` |
| go | govulncheck | `govulncheck -json ./...` |

**Parsers**:

1. **NpmAuditJsonParser**: Parse `{ vulnerabilities: { "pkg": { severity, via, fixAvailable } } }` (npm v7+ format). Map severity strings to `SecuritySeverity` enum.

2. **SarifParser**: Parse SARIF 2.1.0 format (used by CodeQL, Semgrep). Extract `runs[].results[]` with `ruleId`, `level` (error/warning/note), `message.text`, `locations[].physicalLocation`. Map SARIF levels to `SecuritySeverity`.

3. **TrivyJsonParser**: Parse Trivy's `{ Results: [{ Vulnerabilities: [{ VulnerabilityID, PkgName, InstalledVersion, FixedVersion, Severity }] }] }`.

### Step 3.4: Failure Categorizer

**New file**: `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/FailureCategorizer.cs`

```csharp
public class FailureCategorizer
{
    // Pattern-based categorization
    private static readonly (FailureCategory Category, Regex Pattern)[] CategoryPatterns =
    {
        // Syntax errors
        (FailureCategory.SyntaxError, new Regex(@"SyntaxError|ParseError|Unexpected token|Cannot find module|Module not found|error TS\d+|error CS\d+|cannot find symbol", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Timeouts
        (FailureCategory.Timeout, new Regex(@"timed?\s*out|timeout|exceeded\s+\d+\s*m?s|SIGTERM|killed|deadline exceeded", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Environment issues
        (FailureCategory.EnvironmentIssue, new Regex(@"ECONNREFUSED|ENOENT|EACCES|EPERM|connection refused|permission denied|file not found|No such file|command not found|not installed|ENOMEM|out of memory", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Assertion failures (check AFTER environment issues)
        (FailureCategory.AssertionFailure, new Regex(@"AssertionError|AssertionFailure|Expected.*(?:to be|to equal|to have|but received|but got)|assert\.equal|Assert\.Equal|Should\(\)\.Be|expect\(.*\)\.|assertEqual|assertRaises|assert_that", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Runtime errors (catch-all for exceptions)
        (FailureCategory.RuntimeError, new Regex(@"NullReferenceException|TypeError|ReferenceError|null reference|undefined is not|Cannot read propert|nil pointer|panic:|segmentation fault|stack overflow", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    public FailureCategory Categorize(string errorMessage, string? stackTrace = null)
    {
        var combined = $"{errorMessage}\n{stackTrace ?? ""}";

        foreach (var (category, pattern) in CategoryPatterns)
        {
            if (pattern.IsMatch(combined))
                return category;
        }

        return FailureCategory.Unknown;
    }

    public bool IsAutoFixable(FailureCategory category)
    {
        return category switch
        {
            FailureCategory.SyntaxError => true,
            FailureCategory.AssertionFailure => true, // might need code or test change
            FailureCategory.Timeout => true, // can increase timeout or optimize
            FailureCategory.RuntimeError => true, // code needs fix
            FailureCategory.EnvironmentIssue => false, // needs human intervention
            FailureCategory.Flaky => false, // mark and skip
            _ => false
        };
    }
}
```

### Step 3.5: Smart Test Selector

**New file**: `apps/tamma-elsa/src/Tamma.Activities/Testing/Services/SmartTestSelector.cs`

```csharp
public interface ISmartTestSelector
{
    Task<SmartTestSelectionResult> SelectTestsAsync(
        string repositoryPath,
        List<string> changedFiles,
        TestFrameworkInfo framework,
        CancellationToken ct = default);
}

public class SmartTestSelectionResult
{
    public List<string> SelectedTestFiles { get; set; } = new();
    public string? TestFilter { get; set; } // framework-specific filter string
    public SelectionStrategy Strategy { get; set; }
    public bool RunFullSuite { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public enum SelectionStrategy
{
    ColocatedTest,      // foo.ts -> foo.test.ts
    ImportAnalysis,     // foo.ts imported by bar.test.ts
    DirectoryBased,     // src/utils/* -> tests/utils/*
    FrameworkNative,    // vitest --changed, jest --changedSince
    FullSuite           // config changed or no related tests found
}
```

Implementation:
1. Check if changed files include config files (`package.json`, `tsconfig.json`, `pom.xml`, etc.) -- if so, run full suite
2. For each changed source file, look for co-located test file (same directory, `.test.` or `.spec.` variant)
3. If framework supports it, generate native filter:
   - Vitest: `--changed` flag or `--testPathPattern` with matched files
   - Jest: `--findRelatedTests <changed-files>` or `--changedSince`
   - pytest: `-k "test_foo or test_bar"` with matched test names
   - go test: `go test ./path/to/changed/package/...`
4. If no tests found via any strategy, set `RunFullSuite = true`

### Verification

- Unit tests for each coverage parser, linter parser, security parser
- Unit tests for FailureCategorizer with sample error messages from each category
- Unit tests for SmartTestSelector with mock file listings

---

## Phase 4: ELSA Activities and Workflow Wiring

### Step 4.1: Create RunTestsAndParseActivity

**New file**: `apps/tamma-elsa/src/Tamma.Activities/Testing/RunTestsAndParseActivity.cs`

```csharp
[Activity("Tamma.Testing", "Run Tests And Parse",
    "Execute tests and parse output into structured results",
    Kind = ActivityKind.Task)]
public class RunTestsAndParseActivity : CodeActivity<TestExecutionResult>
{
    [Input(Description = "Repository/workspace path")]
    public Input<string> RepositoryPath { get; set; } = default!;

    [Input(Description = "Test framework info from detection")]
    public Input<TestFrameworkInfo?> FrameworkInfo { get; set; } = default!;

    [Input(Description = "Optional test command override")]
    public Input<string?> TestCommandOverride { get; set; } = default!;

    [Input(Description = "Optional test filter (for smart selection)")]
    public Input<string?> TestFilter { get; set; } = default!;

    [Input(Description = "Timeout in seconds", DefaultValue = 120)]
    public Input<int> TimeoutSeconds { get; set; } = new(120);

    // Implementation:
    // 1. Get framework info (from input or run detection)
    // 2. Build test command (framework command + filter)
    // 3. Validate command against CommandValidator
    // 4. Execute via Process (same pattern as RunTestsTool)
    // 5. Parse output using TestOutputParserFactory.GetParser(framework.OutputFormat)
    // 6. Run FailureCategorizer on each failure
    // 7. Return TestExecutionResult
}
```

### Step 4.2: Create RunLinterAndParseActivity

Similar structure. Detects linter, runs command, parses output, returns `LinterResult`.

### Step 4.3: Create RunSecurityScanAndParseActivity

Similar structure. Detects scanner, runs command, parses output, returns `SecurityScanResult`.

### Step 4.4: Modify TddWorkflow -- Replace Mock Test Runs

**File**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWorkflow.cs`

This is the critical integration point. Replace the 3 mock sequences.

#### RED Phase Replacement

**Remove**:
```csharp
// TODO: Replace mock test runs with DispatchWorkflow calls to testing-pipeline (7-1C)
// Mock: simulate running new tests (tests FAIL = correct TDD)
var mockNewTestsFail = Assign(testRunAllPassed, _ => (object)false, ...);
var mockNewTestsFailCount = Assign(testRunFailedCount, ctx => ...);
var mockNewTestsPassCount = Assign(testRunPassedCount, _ => (object)0, ...);
```

**Replace with**:
```csharp
// RED phase: run new tests -- they should FAIL (correct TDD)
var runRedTests = new RunTestsAndParseActivity
{
    Id = "RunRedTests",
    Name = "Run New Tests (RED)",
    RepositoryPath = new Input<string>(ctx => repositoryUrl.Get(ctx)),
    FrameworkInfo = new Input<TestFrameworkInfo?>(ctx => null), // auto-detect
    TestFilter = new Input<string?>(ctx =>
    {
        // Only run the newly written test files
        var gen = testGenResult.Get(ctx);
        return gen?.TestFiles != null && gen.TestFiles.Count > 0
            ? string.Join(" ", gen.TestFiles)
            : null;
    }),
    TimeoutSeconds = new Input<int>(60),
    Result = new Output<TestExecutionResult>(redTestResult)
};
runRedTests.SetDisplayText("Run New Tests (RED)");

// Transfer RED test results to workflow variables
var transferRedResults = new SetVariable
{
    Id = "TransferRedResults",
    Name = "Transfer RED Results",
    Variable = testRunAllPassed,
    Value = new Input<object?>(ctx =>
    {
        var result = redTestResult.Get(ctx);
        testRunFailedCount.Set(ctx, result?.FailedTests ?? 0);
        testRunPassedCount.Set(ctx, result?.PassedTests ?? 0);
        return (object)(result?.AllPassed ?? false);
    })
};
```

#### GREEN Phase Replacement

Same pattern but runs the full test suite. The `TestFilter` is null (run all tests) because after implementation, all tests should pass.

#### REFACTOR Phase Replacement

Same pattern. Run full suite after refactoring to verify nothing broke.

#### Flowchart Connection Updates

Replace mock activity chains with the new activities in the flowchart connections:

```csharp
// OLD:
Connect(writeTests, mockNewTestsFail),
Connect(mockNewTestsFail, mockNewTestsFailCount),
Connect(mockNewTestsFailCount, mockNewTestsPassCount),
Connect(mockNewTestsPassCount, checkTestsFail),

// NEW:
Connect(writeTests, runRedTests),
Connect(runRedTests, transferRedResults),
Connect(transferRedResults, checkTestsFail),
```

### Step 4.5: Modify TriggerCIActivity -- Wire Real GitHub Actions

**File**: `apps/tamma-elsa/src/Tamma.Activities/Testing/TriggerCIActivity.cs`

The `TriggerRealCI` method currently POSTs to an unimplemented callback URL. Replace with direct GitHub Actions API integration:

```csharp
private async Task<CITriggerResult> TriggerRealCI(
    Guid sessionId, string repository, string branch, string? commitSha)
{
    // Option A: GitHub Actions workflow_dispatch
    // POST /repos/{owner}/{repo}/actions/workflows/{workflow_id}/dispatches
    // Requires: GITHUB_TOKEN with actions:write scope
    // The workflow file should be pre-configured to:
    //   1. Run tests
    //   2. Run linter
    //   3. Run security scan
    //   4. POST results back to Tamma callback URL or store as artifacts

    // Option B: Local execution (for repos without CI)
    // Detect tooling -> run tests -> run linter -> run security scan
    // Populate CIResultsPayload directly
    // Resume the WaitForCIResults bookmark programmatically

    // Implementation depends on whether the repo has a CI workflow file.
    // Check for .github/workflows/*.yml first.
    // If found: dispatch via GitHub API
    // If not found: run locally using the activities from this story
}
```

### Step 4.6: Add Local CI Execution Path to TestingWorkflow

For projects without a CI pipeline (or when running in standalone mode), add an alternative path in `TestingWorkflow.cs` that:

1. Runs `DetectProjectToolingActivity`
2. Runs `RunTestsAndParseActivity`
3. Runs coverage parsing
4. Runs `RunLinterAndParseActivity`
5. Runs `RunSecurityScanAndParseActivity`
6. Assembles `CIResultsPayload` from all results
7. Proceeds to `EvaluateResultsActivity` (existing)

This path is activated when `TriggerCIActivity` detects no CI workflow and runs locally.

### Step 4.7: Register New Services in DI

**File**: `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` (or wherever DI is configured)

```csharp
services.AddSingleton<IProjectToolingDetector, ProjectToolingDetector>();
services.AddSingleton<TestOutputParserFactory>();
services.AddSingleton<CoverageParserFactory>();
services.AddSingleton<LinterOutputParserFactory>();
services.AddSingleton<SecurityOutputParserFactory>();
services.AddSingleton<FailureCategorizer>();
services.AddScoped<ISmartTestSelector, SmartTestSelector>();
```

### Verification

- Build verification: `dotnet build` with no errors
- Integration test: Run TddWorkflow against a real TypeScript project with vitest, verify RED phase tests fail and GREEN phase tests pass
- Unit tests for each new activity
- Test that TriggerCIActivity correctly chooses local vs remote execution based on workflow file presence

---

## Phase 5: Integration Testing and End-to-End Verification

### Step 5.1: Test Fixtures

Create sample project fixtures in the test directory:

```
apps/tamma-elsa/tests/Tamma.Activities.Tests/Testing/Fixtures/
  vitest-project/           # package.json with vitest, sample test
  jest-project/             # package.json with jest
  pytest-project/           # pytest.ini, conftest.py
  go-project/               # go.mod, *_test.go
  dotnet-project/           # *.csproj with test SDK
  sample-outputs/
    vitest-json-output.json
    junit-xml-output.xml
    go-test-json-output.jsonl
    tap-output.txt
    trx-output.xml
    eslint-json-output.json
    npm-audit-output.json
    lcov-output.info
    cobertura-output.xml
    sarif-output.json
```

### Step 5.2: End-to-End Test Scenarios

1. **E2E: Framework detection on this repo** (`tamma`): Verify it detects pnpm, vitest, eslint
2. **E2E: TddWorkflow RED phase with real vitest**: Write a test, run it, verify it fails with structured output
3. **E2E: TestingWorkflow local mode**: Run full local CI pipeline, verify CIResultsPayload is populated
4. **E2E: Coverage delta**: Run tests twice, verify coverage delta is computed

---

## Summary of All Changes

| Action | File | Description |
|--------|------|-------------|
| MODIFY | `Testing/Models/TestingModels.cs` | Add `FailureCategory` enum, extend `FailedTestDetail`, extend `CIResultsPayload` |
| CREATE | `Testing/Models/ToolingDetectionModels.cs` | Detection result models |
| CREATE | `Testing/Models/TestExecutionModels.cs` | Normalized execution result models |
| CREATE | `Testing/Services/ProjectToolingDetector.cs` | Framework/linter/scanner detection |
| CREATE | `Testing/Services/TestOutputParserFactory.cs` | Parser factory + interface |
| CREATE | `Testing/Services/Parsers/*.cs` | 7 test output parsers |
| CREATE | `Testing/Services/CoverageParserFactory.cs` | Coverage parser factory |
| CREATE | `Testing/Services/CoverageParsers/*.cs` | 4 coverage parsers |
| CREATE | `Testing/Services/LinterDetector.cs` | Linter detection |
| CREATE | `Testing/Services/LinterOutputParserFactory.cs` | Linter parser factory |
| CREATE | `Testing/Services/LinterParsers/*.cs` | 3 linter parsers |
| CREATE | `Testing/Services/SecurityScannerDetector.cs` | Security scanner detection |
| CREATE | `Testing/Services/SecurityOutputParserFactory.cs` | Security parser factory |
| CREATE | `Testing/Services/SecurityParsers/*.cs` | 3 security parsers |
| CREATE | `Testing/Services/FailureCategorizer.cs` | Test failure classification |
| CREATE | `Testing/Services/SmartTestSelector.cs` | Changed-file-based test selection |
| CREATE | `Testing/DetectProjectToolingActivity.cs` | ELSA activity for detection |
| CREATE | `Testing/RunTestsAndParseActivity.cs` | ELSA activity for test execution + parsing |
| CREATE | `Testing/RunLinterAndParseActivity.cs` | ELSA activity for lint execution + parsing |
| CREATE | `Testing/RunSecurityScanAndParseActivity.cs` | ELSA activity for security scan + parsing |
| MODIFY | `TddWorkflow.cs` | Replace 3 mock test run sequences with real execution |
| MODIFY | `TriggerCIActivity.cs` | Add GitHub Actions dispatch + local execution fallback |
| MODIFY | `Program.cs` / DI config | Register new services |

### Estimated Line Counts

| Category | Files | Lines |
|----------|-------|-------|
| Models | 3 | ~250 |
| Detection services | 3 | ~500 |
| Test output parsers | 8 | ~800 |
| Coverage parsers | 5 | ~400 |
| Linter parsers | 4 | ~300 |
| Security parsers | 4 | ~350 |
| FailureCategorizer | 1 | ~80 |
| SmartTestSelector | 1 | ~150 |
| ELSA activities | 4 | ~400 |
| Workflow modifications | 2 | ~100 (net change) |
| **Total new code** | **35** | **~3,330** |
| Unit tests | ~15 | ~2,000 |
| **Grand total** | **~50** | **~5,330** |

---

## Risk Mitigation

| Risk | Mitigation |
|------|-----------|
| Parser fails on edge-case output | Every parser has a fallback to `GenericExitCodeParser`; parsers catch exceptions and degrade gracefully |
| Framework detection wrong | Detection is advisory; explicit override via config is always available |
| Real test execution takes too long | Configurable timeout per activity; SmartTestSelector reduces test scope |
| SecurityScanner not installed | Detect availability first (`which npm`, etc.); skip gracefully with log warning |
| TddWorkflow behavior change | Feature flag: keep `Testing:UseMock` config to fall back to old mock behavior during rollout |
| Breaking existing TestingWorkflow | Local CI path is additive; remote CI path (bookmark-based) is unchanged |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-31 | 1.0 | Initial implementation plan from deep audit | Architecture Team |
