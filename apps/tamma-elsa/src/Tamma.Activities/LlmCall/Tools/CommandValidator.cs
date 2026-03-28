using System.Text.RegularExpressions;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Shared command validation logic used by ShellExecuteTool, RunTestsTool, and
/// any other tool that executes shell commands. Centralises blocked-pattern
/// definitions so they stay consistent across all command-execution surfaces.
/// </summary>
public static class CommandValidator
{
    /// <summary>
    /// Blocked command patterns. Each entry has a human-readable name (for logging)
    /// and a compiled regex.
    /// </summary>
    public static readonly (string Name, Regex Pattern)[] BlockedPatterns =
    {
        // Destructive file operations
        ("rm_rf_root", new Regex(@"\brm\b[^|;]*(-r\b|-R\b|--recursive\b)[^|;]*(-f\b|--force\b)[^|;]*/|\brm\b[^|;]*(-f\b|--force\b)[^|;]*(-r\b|-R\b|--recursive\b)[^|;]*/", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Privilege escalation
        ("sudo", new Regex(@"\bsudo\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Disk formatting / low-level write
        ("mkfs", new Regex(@"\bmkfs\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("dd_if", new Regex(@"\bdd\s+if=", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Recursive permission changes on root
        ("recursive_chmod_root", new Regex(@"\b(chmod|chown)\s+.*(-R|--recursive)\s+/", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Remote code execution — curl/wget piping into shell
        ("curl_pipe_shell", new Regex(@"\bcurl\b.*\|\s*(bash|sh)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("wget_pipe_shell", new Regex(@"\bwget\b.*\|\s*(bash|sh)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Remote code execution — curl/wget piping into interpreter
        ("curl_pipe_interpreter", new Regex(@"\bcurl\b.*\|\s*(python|python3|perl|ruby|node)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("wget_pipe_interpreter", new Regex(@"\bwget\b.*\|\s*(python|python3|perl|ruby|node)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Truncate system file
        ("truncate_system_file", new Regex(@":>\s*/", RegexOptions.Compiled)),

        // Windows disk format
        ("format_disk", new Regex(@"\bformat\b.*[a-zA-Z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // System power operations
        ("reboot_shutdown", new Regex(@"\b(reboot|shutdown|halt|poweroff)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Base64 decode piped to shell — obfuscation bypass
        ("base64_pipe", new Regex(@"\bbase64\b.*\|", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // eval with argument — arbitrary code execution
        ("eval_command", new Regex(@"\beval\s", RegexOptions.IgnoreCase | RegexOptions.Compiled)),

        // Command substitution — $(...) can hide arbitrary commands
        ("command_substitution", new Regex(@"\$\(", RegexOptions.Compiled)),

        // Backtick command substitution
        ("backtick_substitution", new Regex(@"`[^`]+`", RegexOptions.Compiled)),
    };

    /// <summary>
    /// Regex that matches shell metacharacters which should never appear in
    /// individual arguments passed to direct-exec tools (e.g. git args).
    /// Pipe, semicolon, ampersand, dollar-sign, backtick, subshell.
    /// </summary>
    public static readonly Regex ShellMetacharacters = new(
        @"[|;&`$]|\$\(|\)\s*\{",
        RegexOptions.Compiled);

    /// <summary>
    /// Check a command string against all blocked patterns.
    /// Returns the name of the first matched pattern, or null if the command is allowed.
    /// </summary>
    /// <param name="command">The command string to validate.</param>
    /// <returns>The name of the blocked pattern that matched, or null if allowed.</returns>
    public static string? GetBlockedPatternName(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        foreach (var (name, pattern) in BlockedPatterns)
        {
            if (pattern.IsMatch(command))
                return name;
        }

        return null;
    }

    /// <summary>
    /// Check whether a string contains shell metacharacters that could enable injection.
    /// Used for arguments passed directly to executables (not through a shell).
    /// </summary>
    /// <param name="input">The string to check.</param>
    /// <returns>True if shell metacharacters are found.</returns>
    public static bool ContainsShellMetacharacters(string input)
    {
        return !string.IsNullOrEmpty(input) && ShellMetacharacters.IsMatch(input);
    }
}
