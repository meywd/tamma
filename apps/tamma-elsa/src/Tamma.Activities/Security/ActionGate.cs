using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamma.Activities.Security;

/// <summary>
/// Gates dangerous shell commands. Maintains a configurable set of blocked
/// command patterns. Thread-safe and fast (target: under 0.1ms per check).
///
/// Each blocked pattern is a compiled, case-insensitive regex. The default
/// set covers common attack vectors: recursive delete, pipe-to-shell,
/// privilege escalation, credential access, and reverse shells.
///
/// Additional patterns can be loaded from configuration via <see cref="ActionGateOptions"/>.
/// </summary>
public sealed class ActionGate
{
    private readonly IReadOnlyList<(string Name, Regex Pattern)> _blockedPatterns;
    private readonly ILogger<ActionGate>? _logger;

    /// <summary>
    /// Default blocked command patterns. Each is compiled for performance.
    /// The Name field is used for logging (never the actual command).
    /// </summary>
    private static readonly IReadOnlyList<(string Name, string Pattern)> DefaultBlockedPatterns =
        new List<(string, string)>
        {
            ("recursive_delete_root", @"rm\s+-rf\s+/"),
            ("recursive_delete_home", @"rm\s+-rf\s+~"),
            ("curl_pipe_bash", @"curl.*\|\s*bash"),
            ("wget_pipe_bash", @"wget.*\|\s*bash"),
            ("chmod_777", @"chmod\s+777"),
            ("sudo", @"sudo\s+"),
            ("passwd", @"\bpasswd\b"),
            ("etc_shadow", @"/etc/shadow"),
            ("dotenv_access", @"\.env\b"),
            ("eval_call", @"eval\s*\("),
            ("exec_call", @"exec\s*\("),
            ("dev_write", @">\s*/dev/"),
            ("mkfs", @"\bmkfs\b"),
            ("dd_raw_disk", @"dd\s+if="),
            ("netcat_listener", @"nc\s+-l"),
            ("python_os_exec", @"python.*-c.*import\s+os"),
            ("reverse_shell", @"\b(bash|sh)\s+-i\s+>&"),
            ("base64_decode_pipe", @"base64\s+(-d|--decode).*\|"),
            ("curl_upload", @"curl.*-T\s+/etc/"),
            ("env_dump", @"\bprintenv\b"),
        };

    /// <summary>
    /// Creates a new <see cref="ActionGate"/> with default and optionally configured patterns.
    /// </summary>
    /// <param name="options">Optional configuration for additional blocked patterns.</param>
    /// <param name="logger">Optional logger. Only logs pattern names, never the actual command.</param>
    public ActionGate(
        IOptions<ActionGateOptions>? options = null,
        ILogger<ActionGate>? logger = null)
    {
        _logger = logger;
        var patterns = new List<(string Name, Regex Pattern)>();

        // Compile default patterns
        foreach (var (name, pattern) in DefaultBlockedPatterns)
        {
            patterns.Add((name, new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase)));
        }

        // Compile additional patterns from configuration
        if (options?.Value.AdditionalBlockedPatterns != null)
        {
            var index = 0;
            foreach (var extra in options.Value.AdditionalBlockedPatterns)
            {
                try
                {
                    patterns.Add(($"custom_{index}", new Regex(extra, RegexOptions.Compiled | RegexOptions.IgnoreCase)));
                    index++;
                }
                catch (ArgumentException ex)
                {
                    _logger?.LogWarning(
                        "ActionGate: skipping invalid custom regex pattern at index {Index}: {ErrorMessage}",
                        index, ex.Message);
                    index++;
                }
            }
        }

        _blockedPatterns = patterns;
    }

    /// <summary>
    /// Check if a command matches any blocked pattern.
    /// Returns true if the command should be BLOCKED.
    /// Never logs the command itself -- only the matched pattern name.
    /// </summary>
    /// <param name="command">The command string to check.</param>
    /// <returns>True if the command is blocked; false if it is safe to execute.</returns>
    public bool IsBlocked(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        return IsBlocked(command, out _);
    }

    /// <summary>
    /// Check if a command matches any blocked pattern, returning the matched pattern name.
    /// </summary>
    /// <param name="command">The command string to check.</param>
    /// <param name="matchedPatternName">Name of the pattern that matched, or null if not blocked.</param>
    /// <returns>True if the command is blocked; false if it is safe to execute.</returns>
    public bool IsBlocked(string command, out string? matchedPatternName)
    {
        matchedPatternName = null;

        if (string.IsNullOrWhiteSpace(command))
            return false;

        foreach (var (name, pattern) in _blockedPatterns)
        {
            if (pattern.IsMatch(command))
            {
                matchedPatternName = name;
                return true;
            }
        }

        return false;
    }
}
