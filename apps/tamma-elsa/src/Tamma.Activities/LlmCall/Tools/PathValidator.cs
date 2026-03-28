namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Validates and resolves file paths against a workspace root to prevent directory traversal.
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// Resolve a path relative to workspaceRoot. Returns an error message if the resolved path
    /// escapes the workspace.
    /// </summary>
    /// <param name="requestedPath">Path from the LLM (relative or absolute).</param>
    /// <param name="workspaceRoot">The workspace root directory (absolute).</param>
    /// <returns>The fully resolved absolute path within the workspace.</returns>
    /// <exception cref="ArgumentException">Thrown if path or workspace root is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown if path escapes workspace.</exception>
    public static string ResolveSafePath(string requestedPath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new ArgumentException("Path cannot be empty.", nameof(requestedPath));

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root cannot be empty.", nameof(workspaceRoot));

        var normalizedRoot = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;

        // If relative, combine with workspace root; if absolute, use as-is for validation
        var combinedPath = Path.IsPathRooted(requestedPath)
            ? requestedPath
            : Path.Combine(workspaceRoot, requestedPath);

        var resolvedPath = Path.GetFullPath(combinedPath);

        // Check if the resolved path is within or equal to the workspace root
        var normalizedRootWithout = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar);
        if (!resolvedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolvedPath, normalizedRootWithout, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Path resolves outside the workspace root.");
        }

        return resolvedPath;
    }
}
