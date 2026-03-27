using System.Security.Cryptography;
using System.Text;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Computes a deterministic version number from workflow source file hashes.
/// When any workflow .cs file changes, the hash changes, causing ELSA to
/// publish a new version of all code-based workflows on next startup.
///
/// This ensures DisplayText, Name, and structural changes always reach
/// the ELSA Studio designer without manual DB cleanup.
/// </summary>
internal static class WorkflowVersions
{
    /// <summary>
    /// Auto-computed version based on file content hashes.
    /// Falls back to timestamp-based version if files can't be read at build time.
    /// </summary>
    internal static int ComputedVersion { get; } = ComputeVersion();

    private static int ComputeVersion()
    {
        // Look for workflow source files relative to the assembly location
        // In production (Docker), files won't exist — use the embedded hash instead
        var embeddedHash = EmbeddedSourceHash;
        if (!string.IsNullOrEmpty(embeddedHash))
        {
            // Use first 8 hex chars → int, add base version offset
            var hashInt = int.Parse(embeddedHash[..8], System.Globalization.NumberStyles.HexNumber);
            return 2 + Math.Abs(hashInt % 10000); // Range: 2–10001
        }

        // Fallback: use assembly build timestamp as version proxy
        var assembly = typeof(WorkflowVersions).Assembly;
        var buildDate = File.GetLastWriteTimeUtc(assembly.Location);
        return 2 + (int)(buildDate.Ticks % 10000);
    }

    /// <summary>
    /// SHA256 hash of all workflow .cs files, injected at build time via MSBuild.
    /// If not set, falls back to assembly timestamp.
    /// </summary>
    internal static string? EmbeddedSourceHash => _embeddedHash;

    // This field is replaced by MSBuild at build time.
    // The placeholder value triggers the fallback path.
    private static readonly string? _embeddedHash = ComputeSourceHash();

    private static string? ComputeSourceHash()
    {
        // Compute hash at runtime from workflow files if available
        var assemblyDir = Path.GetDirectoryName(typeof(WorkflowVersions).Assembly.Location);
        if (assemblyDir == null) return null;

        // In dev, walk up to find the source Workflows directory
        // In Docker, this won't exist — return null to use timestamp fallback
        var searchPaths = new[]
        {
            Path.Combine(assemblyDir, "..", "..", "..", "..", "Workflows"),
            Path.Combine(assemblyDir, "Workflows"),
        };

        string? workflowsDir = null;
        foreach (var p in searchPaths)
        {
            var resolved = Path.GetFullPath(p);
            if (Directory.Exists(resolved))
            {
                workflowsDir = resolved;
                break;
            }
        }

        if (workflowsDir == null) return null;

        using var sha = SHA256.Create();
        var files = Directory.GetFiles(workflowsDir, "*.cs")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0) return null;

        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
}
