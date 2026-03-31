namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Validates and resolves file paths against a workspace root to prevent directory traversal.
/// Also resolves symlinks so that a symlink pointing outside the workspace is rejected.
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

        // First check: the logical path must be within the workspace
        ValidateWithinWorkspace(resolvedPath, normalizedRoot);

        // Second check: if the path exists on disk, resolve symlinks and re-validate
        // the final physical target. This prevents symlink-based traversal attacks
        // where a symlink inside the workspace points to a file outside it.
        if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath))
        {
            var fileInfo = new FileInfo(resolvedPath);
            if (fileInfo.LinkTarget != null)
            {
                // ResolveLinkTarget(true) follows the entire chain to the final target.
                var finalTarget = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (finalTarget != null)
                {
                    var symlinkResolvedPath = Path.GetFullPath(finalTarget.FullName);
                    ValidateWithinWorkspace(symlinkResolvedPath, normalizedRoot);
                }
            }

            // Also check if it's a directory symlink
            var dirInfo = new DirectoryInfo(resolvedPath);
            if (dirInfo.LinkTarget != null)
            {
                var finalTarget = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (finalTarget != null)
                {
                    var symlinkResolvedPath = Path.GetFullPath(finalTarget.FullName);
                    ValidateWithinWorkspace(symlinkResolvedPath, normalizedRoot);
                }
            }
        }

        return resolvedPath;
    }

    /// <summary>
    /// Validates that a resolved path is within or equal to the workspace root.
    /// </summary>
    private static void ValidateWithinWorkspace(string resolvedPath, string normalizedRoot)
    {
        var normalizedRootWithout = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar);
        if (!resolvedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolvedPath, normalizedRootWithout, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Path resolves outside the workspace root.");
        }
    }
}
