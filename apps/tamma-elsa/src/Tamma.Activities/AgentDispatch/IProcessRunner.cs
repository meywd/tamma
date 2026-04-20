namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Abstraction for spawning child processes. Separated out so
/// <see cref="LocalExecutor"/> is testable without actually forking
/// a real process.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides,
    int TimeoutSeconds);

public sealed record ProcessRunResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    int DurationSeconds);
