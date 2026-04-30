using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.TDD;
using Tamma.Activities.TDD.Models;

namespace Tamma.Activities.Tests.TDD;

/// <summary>
/// Unit tests for <see cref="ValidateTestSyntaxActivity"/>. We exercise the
/// pure <c>ValidateAsync</c> entry point so we don't need a live ELSA
/// runtime, and inject a fake <see cref="IProcessRunner"/> so the tests
/// don't require <c>tsc</c> / <c>python</c> on PATH.
/// </summary>
[TestFixture]
public class ValidateTestSyntaxActivityTests
{
    /// <summary>
    /// A fake <see cref="IProcessRunner"/> that lets each test wire up the
    /// exit code / stdout / stderr it wants to simulate per-invocation.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Func<ProcessRunRequest, ProcessRunResult> OnRun { get; set; } =
            _ => new ProcessRunResult(0, "", "", false, 0);

        public List<ProcessRunRequest> Calls { get; } = new();

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.FromResult(OnRun(request));
        }
    }

    private static TestGenerationResult MakeGen(string code, params string[] files) => new()
    {
        Success = true,
        TestCode = code,
        TestFiles = files.ToList(),
        TestCount = 1
    };

    [Test]
    public async Task ValidTypeScript_ExitCodeZero_IsValid()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = req => new ProcessRunResult(
                ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false, DurationSeconds: 1)
        };

        var gen = MakeGen(
            "describe('x', () => { it('y', () => { expect(1).toBe(1); }); });",
            "tests/example.test.ts");

        var result = await ValidateTestSyntaxActivity.ValidateAsync(gen, 30, runner);

        result.IsValid.Should().BeTrue("tsc returned exit code 0 → no syntax errors");
        result.Errors.Should().BeEmpty();
        result.SkippedLanguages.Should().NotContain("typescript");
        runner.Calls.Should().NotBeEmpty();
    }

    [Test]
    public async Task InvalidTypeScript_ParsesErrorPositions()
    {
        // Fake tsc output mirroring its real diagnostic format:
        //   <file>(line,col): error TSxxxx: <message>
        var fakeStdout =
            "tests/broken.test.ts(3,12): error TS1005: ',' expected.\n" +
            "tests/broken.test.ts(7,1): error TS1109: Expression expected.\n";

        var runner = new FakeProcessRunner
        {
            OnRun = req => new ProcessRunResult(
                ExitCode: 1, StdOut: fakeStdout, StdErr: "", TimedOut: false, DurationSeconds: 1)
        };

        var gen = MakeGen(
            "describe('x', () => { it('y' => /* missing ) */ }); });",
            "tests/broken.test.ts");

        var result = await ValidateTestSyntaxActivity.ValidateAsync(gen, 30, runner);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);

        result.Errors[0].Language.Should().Be("typescript");
        result.Errors[0].Line.Should().Be(3);
        result.Errors[0].Column.Should().Be(12);
        result.Errors[0].Message.Should().Contain("',' expected");

        result.Errors[1].Line.Should().Be(7);
        result.Errors[1].Column.Should().Be(1);
        result.Errors[1].Message.Should().Contain("Expression expected");
    }

    [Test]
    public async Task ValidPython_ExitCodeZero_IsValid()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = req => new ProcessRunResult(
                ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false, DurationSeconds: 1)
        };

        var gen = MakeGen(
            "def test_addition():\n    assert 1 + 1 == 2\n",
            "tests/test_example.py");

        var result = await ValidateTestSyntaxActivity.ValidateAsync(gen, 30, runner);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.SkippedLanguages.Should().NotContain("python");
    }

    [Test]
    public async Task InvalidPython_ParsesSyntaxError()
    {
        // py_compile prints to stderr like:
        //   File "/tmp/tests/test_broken.py", line 2
        //       def foo(:
        //              ^
        //   SyntaxError: invalid syntax
        var fakeStderr =
            "Sorry: SyntaxError: invalid syntax (test_broken.py, line 2)\n" +
            "  File \"tests/test_broken.py\", line 2\n" +
            "    def foo(:\n" +
            "           ^\n" +
            "SyntaxError: invalid syntax\n";

        var runner = new FakeProcessRunner
        {
            OnRun = req => new ProcessRunResult(
                ExitCode: 1, StdOut: "", StdErr: fakeStderr, TimedOut: false, DurationSeconds: 1)
        };

        var gen = MakeGen("def foo(:\n    pass\n", "tests/test_broken.py");

        var result = await ValidateTestSyntaxActivity.ValidateAsync(gen, 30, runner);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();

        var pyErr = result.Errors.First(e => e.Language == "python");
        pyErr.File.Should().Contain("test_broken.py");
        pyErr.Line.Should().Be(2);
        pyErr.Message.Should().Contain("SyntaxError");
    }

    [Test]
    public async Task UnknownLanguage_IsSkipped_NotFailed()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = req => throw new InvalidOperationException(
                "runner must not be invoked for unknown languages")
        };

        // .rs is a recognized language but has no validator wired
        var gen = MakeGen("// no validator for rust here\nfn test() {}\n", "tests/example.rs");

        var result = await ValidateTestSyntaxActivity.ValidateAsync(gen, 30, runner);

        result.IsValid.Should().BeTrue("missing validator must not fail the workflow");
        result.Errors.Should().BeEmpty();
        result.SkippedLanguages.Should().Contain("rust");
        runner.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task TypeScriptValidatorMissingFromPath_IsSkipped_NotFailed()
    {
        // Both tsc and `npx tsc` come back with the classic "command not found"
        // shape — DefaultProcessRunner returns ExitCode = -1 + the OS error
        // text on stderr. The activity must downgrade this to "skipped",
        // NOT fail the workflow.
        var runner = new FakeProcessRunner
        {
            OnRun = req => new ProcessRunResult(
                ExitCode: -1,
                StdOut: "",
                StdErr: $"{req.FileName}: command not found",
                TimedOut: false,
                DurationSeconds: 0)
        };

        var gen = MakeGen("const x: number = 1;\n", "tests/example.test.ts");

        var result = await ValidateTestSyntaxActivity.ValidateAsync(gen, 30, runner);

        result.IsValid.Should().BeTrue(
            "a missing dev tool must not block the workflow — the testing-pipeline catches genuine syntax bugs downstream");
        result.Errors.Should().BeEmpty();
        result.SkippedLanguages.Should().Contain("typescript");
    }

    [Test]
    public async Task TestGenerationFailed_PassesThrough_WithoutShellOut()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = req => throw new InvalidOperationException(
                "must not invoke runner when there's nothing to validate")
        };

        var gen = new TestGenerationResult
        {
            Success = false,
            ErrorMessage = "LLM returned no content"
        };

        var result = await ValidateTestSyntaxActivity.ValidateAsync(gen, 30, runner);

        result.IsValid.Should().BeTrue("upstream failure is the upstream's problem to surface");
        result.Errors.Should().BeEmpty();
        runner.Calls.Should().BeEmpty();
    }

    [Test]
    public async Task NullGeneration_Throws()
    {
        var runner = new FakeProcessRunner();
        var act = async () =>
            await ValidateTestSyntaxActivity.ValidateAsync(null!, 30, runner);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
