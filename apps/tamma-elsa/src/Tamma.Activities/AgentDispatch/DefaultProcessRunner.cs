using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Default <see cref="IProcessRunner"/>. Spawns a real child process,
/// captures stdout+stderr, and enforces a hard timeout.
///
/// <para>The implementation is straightforward but has two traps worth
/// calling out:</para>
/// <list type="bullet">
///   <item>
///     Stream readers must start BEFORE <c>WaitForExit</c> — otherwise a
///     chatty child can fill the pipe buffer and deadlock us.
///   </item>
///   <item>
///     <c>Process.Kill(true)</c> kills the whole tree so grand-children
///     (the agent's subprocesses) go down too. Without it the agent's
///     npm/node chain can outlive the timeout.
///   </item>
/// </list>
/// </summary>
public sealed class DefaultProcessRunner : IProcessRunner
{
    private readonly ILogger<DefaultProcessRunner>? _logger;

    public DefaultProcessRunner(ILogger<DefaultProcessRunner>? logger = null)
    {
        _logger = logger;
    }

    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        if (request.EnvironmentOverrides is not null)
        {
            foreach (var kv in request.EnvironmentOverrides)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = false };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        var outTcs = new TaskCompletionSource<bool>();
        var errTcs = new TaskCompletionSource<bool>();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) outTcs.TrySetResult(true);
            else stdOut.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) errTcs.TrySetResult(true);
            else stdErr.AppendLine(e.Data);
        };

        var started = DateTime.UtcNow;
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Failed to start process {FileName} in {WorkingDirectory}",
                request.FileName, request.WorkingDirectory);
            return new ProcessRunResult(
                ExitCode: -1,
                StdOut: string.Empty,
                StdErr: ex.Message,
                TimedOut: false,
                DurationSeconds: 0);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.TimeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        }

        var timedOut = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { proc.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
        }

        await Task.WhenAny(Task.WhenAll(outTcs.Task, errTcs.Task), Task.Delay(250))
            .ConfigureAwait(false);

        var duration = (int)(DateTime.UtcNow - started).TotalSeconds;
        return new ProcessRunResult(
            ExitCode: proc.HasExited ? proc.ExitCode : -1,
            StdOut: stdOut.ToString(),
            StdErr: stdErr.ToString(),
            TimedOut: timedOut,
            DurationSeconds: Math.Max(0, duration));
    }
}
