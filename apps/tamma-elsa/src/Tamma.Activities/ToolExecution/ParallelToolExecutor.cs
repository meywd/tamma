using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall.Models;
using Tamma.Activities.LlmCall.Tools;

namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Executes multiple tool calls in parallel with per-path semaphore serialization
/// for filesystem tools. Non-filesystem tools run fully concurrently.
///
/// This executor is scoped per activity execution (not static) to avoid
/// cross-session interference in the semaphore dictionary.
/// </summary>
public class ParallelToolExecutor
{
    private readonly ILogger<ParallelToolExecutor> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    public ParallelToolExecutor(ILogger<ParallelToolExecutor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Execute multiple tool calls in parallel. Filesystem tools targeting the same
    /// normalized path are serialized via a per-path semaphore. Each tool gets an
    /// individual timeout via a linked CancellationTokenSource.
    /// </summary>
    /// <param name="toolCalls">Tool calls from the LLM response.</param>
    /// <param name="registry">Tool executor registry to resolve executors.</param>
    /// <param name="toolTimeoutMs">Per-tool timeout in milliseconds.</param>
    /// <param name="workflowInstanceId">Workflow instance ID for logging correlation.</param>
    /// <param name="turnNumber">Current turn number for logging correlation.</param>
    /// <param name="eventEmitter">Optional event emitter for progress events.</param>
    /// <param name="cancellationToken">Parent cancellation token.</param>
    /// <returns>
    /// Array of results in the same order as the input tool calls.
    /// Each result indicates success/failure — never throws.
    /// </returns>
    public async Task<ToolExecutionResult[]> ExecuteToolsInParallelAsync(
        LlmToolCall[] toolCalls,
        IToolExecutorRegistry registry,
        int toolTimeoutMs,
        string workflowInstanceId,
        int turnNumber,
        ToolLoopEventEmitter? eventEmitter = null,
        CancellationToken cancellationToken = default)
    {
        if (toolCalls.Length == 0)
            return Array.Empty<ToolExecutionResult>();

        // Single tool call — no parallelism overhead
        if (toolCalls.Length == 1)
        {
            var singleResult = await ExecuteSingleToolAsync(
                toolCalls[0], registry, toolTimeoutMs, workflowInstanceId, turnNumber,
                eventEmitter, cancellationToken);
            return new[] { singleResult };
        }

        _logger.LogInformation(
            "Parallel tool execution started: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, TotalToolCalls={TotalToolCalls}",
            workflowInstanceId, turnNumber, toolCalls.Length);

        var parallelSw = Stopwatch.StartNew();

        var tasks = toolCalls.Select(tc =>
            ExecuteSingleToolAsync(tc, registry, toolTimeoutMs, workflowInstanceId, turnNumber,
                eventEmitter, cancellationToken)).ToArray();

        var results = await Task.WhenAll(tasks);

        parallelSw.Stop();

        var successCount = results.Count(r => r.Success);
        var failureCount = results.Length - successCount;

        _logger.LogInformation(
            "Parallel tool execution completed: WorkflowInstanceId={WorkflowInstanceId}, TurnNumber={TurnNumber}, TotalToolCalls={TotalToolCalls}, TotalDurationMs={TotalDurationMs}, SuccessCount={SuccessCount}, FailureCount={FailureCount}",
            workflowInstanceId, turnNumber, toolCalls.Length, parallelSw.ElapsedMilliseconds,
            successCount, failureCount);

        return results;
    }

    /// <summary>
    /// Execute a single tool, applying per-path semaphore if it implements IFileSystemTool.
    /// </summary>
    private async Task<ToolExecutionResult> ExecuteSingleToolAsync(
        LlmToolCall toolCall,
        IToolExecutorRegistry registry,
        int toolTimeoutMs,
        string workflowInstanceId,
        int turnNumber,
        ToolLoopEventEmitter? eventEmitter,
        CancellationToken cancellationToken)
    {
        var executor = registry.GetExecutor(toolCall.ToolName);
        if (executor == null)
        {
            _logger.LogWarning(
                "Tool executor not found in parallel batch: ToolCallId={ToolCallId}, ToolName={ToolName}, WorkflowInstanceId={WorkflowInstanceId}",
                toolCall.Id, toolCall.ToolName, workflowInstanceId);
            return new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                $"Unknown tool: '{toolCall.ToolName}'", 0);
        }

        // Emit TOOL_EXECUTING event
        if (eventEmitter != null)
        {
            await eventEmitter.EmitToolExecuting(turnNumber, toolCall.ToolName, toolCall.Id,
                workflowInstanceId, cancellationToken);
        }

        var toolSw = Stopwatch.StartNew();

        try
        {
            using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            toolCts.CancelAfter(toolTimeoutMs);

            ToolExecutionResult result;

            if (executor is IFileSystemTool fsTool)
            {
                result = await ExecuteWithFileLockAsync(
                    executor, fsTool, toolCall, toolCts.Token, workflowInstanceId);
            }
            else
            {
                result = await executor.ExecuteAsync(toolCall.Id, toolCall.ArgumentsJson, toolCts.Token);
            }

            toolSw.Stop();

            // Emit TOOL_COMPLETED event
            if (eventEmitter != null)
            {
                await eventEmitter.EmitToolCompleted(turnNumber, toolCall.ToolName, toolCall.Id,
                    result.Success, toolSw.ElapsedMilliseconds, workflowInstanceId, cancellationToken);
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Individual tool timeout (not parent cancellation)
            toolSw.Stop();

            _logger.LogWarning(
                "Individual tool timeout in parallel batch: ToolCallId={ToolCallId}, ToolName={ToolName}, TimeoutMs={TimeoutMs}, WorkflowInstanceId={WorkflowInstanceId}",
                toolCall.Id, toolCall.ToolName, toolTimeoutMs, workflowInstanceId);

            var timeoutResult = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                $"Tool execution timed out after {toolTimeoutMs}ms", toolSw.ElapsedMilliseconds);

            if (eventEmitter != null)
            {
                await eventEmitter.EmitToolCompleted(turnNumber, toolCall.ToolName, toolCall.Id,
                    false, toolSw.ElapsedMilliseconds, workflowInstanceId, default);
            }

            return timeoutResult;
        }
        catch (Exception ex)
        {
            toolSw.Stop();

            _logger.LogError(
                "Tool execution exception in parallel batch: ToolCallId={ToolCallId}, ToolName={ToolName}, ExceptionType={ExceptionType}, ExceptionMessage={ExceptionMessage}, WorkflowInstanceId={WorkflowInstanceId}",
                toolCall.Id, toolCall.ToolName, ex.GetType().Name, ex.Message, workflowInstanceId);

            var errorResult = new ToolExecutionResult(toolCall.Id, toolCall.ToolName, false,
                $"Tool execution error: {ex.Message}", toolSw.ElapsedMilliseconds);

            if (eventEmitter != null)
            {
                await eventEmitter.EmitToolCompleted(turnNumber, toolCall.ToolName, toolCall.Id,
                    false, toolSw.ElapsedMilliseconds, workflowInstanceId, default);
            }

            return errorResult;
        }
    }

    /// <summary>
    /// Execute a filesystem tool while holding a per-path semaphore.
    /// </summary>
    private async Task<ToolExecutionResult> ExecuteWithFileLockAsync(
        IToolExecutor executor,
        IFileSystemTool fsTool,
        LlmToolCall toolCall,
        CancellationToken cancellationToken,
        string workflowInstanceId)
    {
        var targetPath = NormalizePath(fsTool.GetTargetPath(toolCall.ArgumentsJson));
        var semaphore = _fileLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));

        var waitSw = Stopwatch.StartNew();
        await semaphore.WaitAsync(cancellationToken);
        waitSw.Stop();

        _logger.LogDebug(
            "File semaphore acquired: ToolCallId={ToolCallId}, ToolName={ToolName}, WaitDurationMs={WaitDurationMs}, WorkflowInstanceId={WorkflowInstanceId}",
            toolCall.Id, toolCall.ToolName, waitSw.ElapsedMilliseconds, workflowInstanceId);

        try
        {
            return await executor.ExecuteAsync(toolCall.Id, toolCall.ArgumentsJson, cancellationToken);
        }
        finally
        {
            semaphore.Release();

            _logger.LogDebug(
                "File semaphore released: ToolCallId={ToolCallId}, ToolName={ToolName}, WorkflowInstanceId={WorkflowInstanceId}",
                toolCall.Id, toolCall.ToolName, workflowInstanceId);
        }
    }

    /// <summary>
    /// Normalize a file path for use as a semaphore key.
    /// Converts to lower-case, replaces backslashes, and trims trailing separators.
    /// </summary>
    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        return path
            .Replace('\\', '/')
            .TrimEnd('/')
            .ToLowerInvariant();
    }
}
