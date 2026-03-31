namespace Tamma.Activities.ToolExecution;

/// <summary>
/// Marker interface for tool executors that access the filesystem.
/// ParallelToolExecutor uses this to serialize access via per-path semaphores,
/// preventing race conditions when multiple tools target the same file.
/// </summary>
public interface IFileSystemTool
{
    /// <summary>
    /// Extract the target file path from the tool call arguments.
    /// Used for per-path locking during parallel execution.
    /// </summary>
    /// <param name="argumentsJson">JSON arguments from the LLM.</param>
    /// <returns>The file path that this tool will access.</returns>
    string GetTargetPath(string argumentsJson);
}
