using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.TDD.Models;

namespace Tamma.Activities.TDD;

/// <summary>
/// ELSA activity that validates test-file syntax between RED-phase test
/// generation and dispatching the testing-pipeline. Closes the
/// <c>validateTestSyntax()</c> AC from story 2-5: a syntax-only check
/// (compiler/parser dry-run) before we burn cycles actually running the
/// tests.
///
/// <para>The activity is intentionally minimal:</para>
/// <list type="bullet">
///   <item><description><b>TypeScript</b> (.ts/.tsx): <c>tsc --noEmit</c></description></item>
///   <item><description><b>Python</b> (.py): <c>python -m py_compile</c></description></item>
///   <item><description><b>Other languages</b>: skipped, recorded under
///     <see cref="TestSyntaxValidationResult.SkippedLanguages"/>.</description></item>
/// </list>
///
/// <para>If the validating tool isn't on PATH the activity treats the
/// language as <i>skipped</i> rather than <i>failed</i> — CI environments
/// often don't have <c>tsc</c>/<c>python</c> available, and we don't
/// want a missing dev tool to block the workflow.</para>
///
/// <para>The validation does NOT execute the tests — that remains the
/// job of the dispatched <c>testing-pipeline</c> workflow downstream.</para>
/// </summary>
[Activity(
    "Tamma.TDD",
    "Validate Test Syntax",
    "Syntax-only validation of generated test files (RED phase pre-flight)",
    Kind = ActivityKind.Task
)]
public class ValidateTestSyntaxActivity : CodeActivity<TestSyntaxValidationResult>
{
    /// <summary>Default per-file shell-out timeout in seconds.</summary>
    private const int DefaultTimeoutSeconds = 30;

    private readonly ILogger<ValidateTestSyntaxActivity>? _logger;
    private readonly IProcessRunner? _processRunner;

    /// <summary>Mentorship session ID (for log correlation)</summary>
    [Input(Description = "Mentorship session ID")]
    public Input<Guid> SessionId { get; set; } = default!;

    /// <summary>Result of the upstream test-generation step.</summary>
    [Input(Description = "Test generation result from WriteTestsActivity")]
    public Input<TestGenerationResult> TestGeneration { get; set; } = default!;

    /// <summary>Per-file shell-out timeout (seconds).</summary>
    [Input(Description = "Per-file validator timeout in seconds", DefaultValue = DefaultTimeoutSeconds)]
    public Input<int> TimeoutSeconds { get; set; } = new(DefaultTimeoutSeconds);

    [JsonConstructor]
    public ValidateTestSyntaxActivity()
    {
    }

    public ValidateTestSyntaxActivity(
        ILogger<ValidateTestSyntaxActivity> logger,
        IProcessRunner processRunner)
    {
        _logger = logger;
        _processRunner = processRunner;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var sessionId = SessionId.Get(context);
        var generation = TestGeneration.Get(context) ?? new TestGenerationResult();
        var timeout = Math.Max(1, TimeoutSeconds.Get(context));

        var runner = _processRunner ?? new DefaultProcessRunner(logger: null);
        var result = await ValidateAsync(generation, timeout, runner, _logger);

        _logger?.LogInformation(
            "ValidateTestSyntax: session {SessionId}, isValid={IsValid}, errors={ErrorCount}, skipped=[{Skipped}]",
            sessionId, result.IsValid, result.Errors.Count, string.Join(", ", result.SkippedLanguages));

        context.SetResult(result);
    }

    /// <summary>
    /// Pure validation entry point — public so tests can drive it without
    /// constructing an <see cref="ActivityExecutionContext"/>. Writes the
    /// generated test code to a temp directory, dispatches the per-language
    /// validator, then cleans the temp dir up.
    /// </summary>
    /// <param name="generation">Output of <see cref="WriteTestsActivity"/>.</param>
    /// <param name="timeoutSeconds">Per-file validator timeout.</param>
    /// <param name="runner">Process runner abstraction (mockable).</param>
    /// <param name="logger">Optional logger.</param>
    public static async Task<TestSyntaxValidationResult> ValidateAsync(
        TestGenerationResult generation,
        int timeoutSeconds,
        IProcessRunner runner,
        ILogger? logger = null)
    {
        if (generation == null) throw new ArgumentNullException(nameof(generation));
        if (runner == null) throw new ArgumentNullException(nameof(runner));

        // Nothing to validate? Pass through. Upstream WriteTestsActivity has
        // already encoded the failure; we don't compound it.
        if (!generation.Success || string.IsNullOrEmpty(generation.TestCode))
        {
            return new TestSyntaxValidationResult
            {
                IsValid = true,
                Errors = new List<TestSyntaxError>(),
                SkippedLanguages = new List<string> { "no-tests-to-validate" }
            };
        }

        var errors = new List<TestSyntaxError>();
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Group files by detected language. If TestFiles is empty (defensive),
        // synthesize a single ".test.ts" path so we still validate something.
        var filesToWrite = generation.TestFiles?.Count > 0
            ? generation.TestFiles.ToList()
            : new List<string> { "tests/generated.test.ts" };

        var byLanguage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in filesToWrite)
        {
            var lang = DetectLanguage(file);
            if (!byLanguage.ContainsKey(lang))
            {
                byLanguage[lang] = new List<string>();
            }
            byLanguage[lang].Add(file);
        }

        // Write all files to a temp dir, validate, then clean up.
        var tempDir = Path.Combine(Path.GetTempPath(), "tamma-validate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write each file (subdirs honored). Every file gets the SAME
            // generated code — generation.TestCode is the merged output of
            // the LLM call, not per-file content. This is consistent with
            // how WriteTestsActivity stores results.
            var writtenFiles = new List<string>();
            foreach (var relativePath in filesToWrite)
            {
                var sanitized = SanitizeRelativePath(relativePath);
                var fullPath = Path.Combine(tempDir, sanitized);
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(fullPath, generation.TestCode);
                writtenFiles.Add(fullPath);
            }

            foreach (var (lang, _) in byLanguage)
            {
                var langFiles = writtenFiles
                    .Where(f => DetectLanguage(f) == lang)
                    .ToList();

                switch (lang)
                {
                    case "typescript":
                        await ValidateTypeScriptAsync(langFiles, tempDir, timeoutSeconds, runner, errors, skipped, logger);
                        break;
                    case "python":
                        await ValidatePythonAsync(langFiles, tempDir, timeoutSeconds, runner, errors, skipped, logger);
                        break;
                    default:
                        // Best-practice warnings & framework-specific discovery
                        // (pytest discoverability etc.) are deliberately out of
                        // scope for this minimal viable validator. See AC notes.
                        skipped.Add(lang);
                        logger?.LogInformation(
                            "ValidateTestSyntax: skipping language '{Language}' ({FileCount} files) — no validator wired",
                            lang, langFiles.Count);
                        break;
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort */ }
        }

        return new TestSyntaxValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            SkippedLanguages = skipped.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>
    /// Detect language from a file extension. Falls back to "unknown".
    /// </summary>
    private static string DetectLanguage(string path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
        return ext switch
        {
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".cs" => "csharp",
            ".go" => "go",
            ".java" => "java",
            ".rb" => "ruby",
            ".rs" => "rust",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Strip leading slashes / drive letters and reject "..", so we never
    /// escape <paramref name="tempDir"/> when honoring LLM-supplied paths.
    /// </summary>
    private static string SanitizeRelativePath(string relativePath)
    {
        var trimmed = relativePath
            .Replace('\\', '/')
            .TrimStart('/', ' ', '\t');

        // Reject parent-dir escapes outright.
        if (trimmed.Split('/').Any(seg => seg == ".."))
        {
            return Path.GetFileName(trimmed);
        }

        return trimmed;
    }

    private static async Task ValidateTypeScriptAsync(
        IReadOnlyList<string> files,
        string workingDirectory,
        int timeoutSeconds,
        IProcessRunner runner,
        List<TestSyntaxError> errors,
        HashSet<string> skipped,
        ILogger? logger)
    {
        if (files.Count == 0) return;

        // Best-effort tsc lookup. We try `tsc` first (if globally available),
        // then `npx --no-install tsc` (uses project-local install).
        var commands = new[]
        {
            new { File = "tsc", Args = BuildTscArgs(files) },
            new { File = "npx", Args = BuildNpxTscArgs(files) }
        };

        ProcessRunResult? lastResult = null;
        foreach (var cmd in commands)
        {
            var request = new ProcessRunRequest(
                FileName: cmd.File,
                Arguments: cmd.Args,
                WorkingDirectory: workingDirectory,
                EnvironmentOverrides: null,
                TimeoutSeconds: timeoutSeconds);

            try
            {
                lastResult = await runner.RunAsync(request);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex,
                    "ValidateTestSyntax: TypeScript validator '{File}' threw — trying next",
                    cmd.File);
                continue;
            }

            // ExitCode -1 + non-empty stderr containing "No such file" or
            // "command not found" indicates the tool itself isn't on PATH.
            if (LooksLikeMissingTool(lastResult))
            {
                logger?.LogDebug(
                    "ValidateTestSyntax: TypeScript validator '{File}' not on PATH",
                    cmd.File);
                continue;
            }

            // Tool ran. Parse output and we're done — don't try the fallback.
            if (lastResult.ExitCode == 0)
            {
                logger?.LogInformation(
                    "ValidateTestSyntax: TypeScript syntax check passed for {Count} file(s)",
                    files.Count);
                return;
            }

            ParseTscErrors(lastResult.StdOut, lastResult.StdErr, errors, files);
            return;
        }

        // Both attempts indicated the tool wasn't available.
        skipped.Add("typescript");
        logger?.LogWarning(
            "ValidateTestSyntax: no TypeScript validator on PATH (tried tsc, npx tsc) — marking 'typescript' as skipped");
    }

    private static async Task ValidatePythonAsync(
        IReadOnlyList<string> files,
        string workingDirectory,
        int timeoutSeconds,
        IProcessRunner runner,
        List<TestSyntaxError> errors,
        HashSet<string> skipped,
        ILogger? logger)
    {
        if (files.Count == 0) return;

        var pythonCommands = new[] { "python3", "python" };

        foreach (var py in pythonCommands)
        {
            var args = new List<string> { "-m", "py_compile" };
            args.AddRange(files);

            var request = new ProcessRunRequest(
                FileName: py,
                Arguments: args,
                WorkingDirectory: workingDirectory,
                EnvironmentOverrides: null,
                TimeoutSeconds: timeoutSeconds);

            ProcessRunResult result;
            try
            {
                result = await runner.RunAsync(request);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex,
                    "ValidateTestSyntax: Python validator '{File}' threw — trying next",
                    py);
                continue;
            }

            if (LooksLikeMissingTool(result))
            {
                logger?.LogDebug(
                    "ValidateTestSyntax: Python validator '{File}' not on PATH",
                    py);
                continue;
            }

            if (result.ExitCode == 0)
            {
                logger?.LogInformation(
                    "ValidateTestSyntax: Python syntax check passed for {Count} file(s)",
                    files.Count);
                return;
            }

            ParsePythonErrors(result.StdErr, result.StdOut, errors, files);
            return;
        }

        skipped.Add("python");
        logger?.LogWarning(
            "ValidateTestSyntax: no Python validator on PATH (tried python3, python) — marking 'python' as skipped");
    }

    private static IReadOnlyList<string> BuildTscArgs(IReadOnlyList<string> files)
    {
        var args = new List<string>
        {
            "--noEmit",
            "--target", "es2022",
            "--module", "esnext",
            "--moduleResolution", "node",
            "--allowJs",
            "--strict", "false",
            "--skipLibCheck"
        };
        args.AddRange(files);
        return args;
    }

    private static IReadOnlyList<string> BuildNpxTscArgs(IReadOnlyList<string> files)
    {
        var args = new List<string> { "--no-install", "tsc" };
        args.AddRange(BuildTscArgs(files));
        return args;
    }

    private static bool LooksLikeMissingTool(ProcessRunResult result)
    {
        if (result.ExitCode != -1 && result.ExitCode != 127) return false;
        var probe = (result.StdErr ?? string.Empty) + " " + (result.StdOut ?? string.Empty);
        return probe.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || probe.Contains("No such file", StringComparison.OrdinalIgnoreCase)
            || probe.Contains("ENOENT", StringComparison.OrdinalIgnoreCase)
            || probe.Contains("is not recognized", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse <c>tsc</c> diagnostic output. Format:
    /// <c>path/to/file.ts(line,col): error TSxxxx: message</c>
    /// </summary>
    private static void ParseTscErrors(
        string stdOut,
        string stdErr,
        List<TestSyntaxError> errors,
        IReadOnlyList<string> files)
    {
        var combined = (stdOut ?? string.Empty) + "\n" + (stdErr ?? string.Empty);
        var lines = combined.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var any = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            // Pattern: "<file>(<line>,<col>): error TSxxxx: <message>"
            var openParen = line.IndexOf('(');
            var closeParen = openParen > 0 ? line.IndexOf(')', openParen) : -1;
            var colon = closeParen > 0 ? line.IndexOf(':', closeParen) : -1;
            if (openParen <= 0 || closeParen < 0 || colon < 0) continue;

            var file = line[..openParen].Trim();
            var positionPart = line.Substring(openParen + 1, closeParen - openParen - 1);
            var message = line[(colon + 1)..].Trim();

            int? lineNum = null, colNum = null;
            var pos = positionPart.Split(',');
            if (pos.Length >= 1 && int.TryParse(pos[0], out var l)) lineNum = l;
            if (pos.Length >= 2 && int.TryParse(pos[1], out var c)) colNum = c;

            errors.Add(new TestSyntaxError
            {
                Language = "typescript",
                File = file,
                Line = lineNum,
                Column = colNum,
                Message = string.IsNullOrEmpty(message) ? line : message
            });
            any = true;
        }

        // Tool exit code != 0 but we couldn't parse anything — don't lose the
        // signal; emit a generic error so IsValid flips to false.
        if (!any)
        {
            errors.Add(new TestSyntaxError
            {
                Language = "typescript",
                File = files.Count > 0 ? files[0] : "<unknown>",
                Message = string.IsNullOrWhiteSpace(stdErr) ? "tsc reported a non-zero exit code" : stdErr.Trim()
            });
        }
    }

    /// <summary>
    /// Parse <c>py_compile</c> diagnostic output. Format varies but typically:
    /// <c>  File "path/to/file.py", line N\n    ...\nSyntaxError: msg</c>
    /// </summary>
    private static void ParsePythonErrors(
        string stdErr,
        string stdOut,
        List<TestSyntaxError> errors,
        IReadOnlyList<string> files)
    {
        var combined = (stdErr ?? string.Empty) + "\n" + (stdOut ?? string.Empty);
        var lines = combined.Split('\n');

        string? currentFile = null;
        int? currentLine = null;
        string? errorMessage = null;
        var any = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            // "  File "path/to/file.py", line N"
            var fileIdx = line.IndexOf("File \"", StringComparison.Ordinal);
            if (fileIdx >= 0)
            {
                var start = fileIdx + 6;
                var end = line.IndexOf('"', start);
                if (end > start)
                {
                    currentFile = line.Substring(start, end - start);
                    var lineMarker = line.IndexOf("line ", end, StringComparison.Ordinal);
                    if (lineMarker > 0 && int.TryParse(line[(lineMarker + 5)..].Trim().TrimEnd(','), out var n))
                    {
                        currentLine = n;
                    }
                }
                continue;
            }

            // "<ErrorType>: <message>" — typical terminator line.
            if (line.Contains("Error:", StringComparison.Ordinal)
                || line.StartsWith("SyntaxError", StringComparison.Ordinal)
                || line.StartsWith("IndentationError", StringComparison.Ordinal)
                || line.StartsWith("TabError", StringComparison.Ordinal))
            {
                errorMessage = line.Trim();
                if (currentFile != null)
                {
                    errors.Add(new TestSyntaxError
                    {
                        Language = "python",
                        File = currentFile,
                        Line = currentLine,
                        Column = null,
                        Message = errorMessage
                    });
                    any = true;
                    currentFile = null;
                    currentLine = null;
                    errorMessage = null;
                }
            }
        }

        if (!any)
        {
            errors.Add(new TestSyntaxError
            {
                Language = "python",
                File = files.Count > 0 ? files[0] : "<unknown>",
                Message = string.IsNullOrWhiteSpace(stdErr) ? "python reported a non-zero exit code" : stdErr.Trim()
            });
        }
    }
}

/// <summary>
/// Result of <see cref="ValidateTestSyntaxActivity"/>. Surfaces both
/// hard errors (which fail the workflow) and the list of languages that
/// were skipped because no validator was wired or available.
/// </summary>
public class TestSyntaxValidationResult
{
    /// <summary>True when no language reported a syntax error.</summary>
    public bool IsValid { get; set; } = true;

    /// <summary>Per-file syntax errors emitted by language validators.</summary>
    public List<TestSyntaxError> Errors { get; set; } = new();

    /// <summary>
    /// Languages that were detected but not validated (no validator wired,
    /// validator not on PATH, etc.). Surfaced as a workflow warning, not a
    /// failure — best-practice warnings AC isn't covered by this minimal
    /// implementation.
    /// </summary>
    public List<string> SkippedLanguages { get; set; } = new();
}

/// <summary>
/// A single syntax error returned by a language validator.
/// </summary>
public class TestSyntaxError
{
    public string Language { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public int? Line { get; set; }
    public int? Column { get; set; }
    public string Message { get; set; } = string.Empty;
}
