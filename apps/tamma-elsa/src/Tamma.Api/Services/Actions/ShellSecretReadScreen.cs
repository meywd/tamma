using System.Text.RegularExpressions;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 42-10 (AC6, D7) — a BEST-EFFORT screen that reclassifies a shell command
/// which reads a secret value from <c>tool:shell_execute</c> to
/// <c>effect:secret.read</c> (level 90) inside the Seam B gate, the same
/// resolution-time split as <c>git_operations</c>.
///
/// <para><b>This is denylist-strength, and the sandbox is the real control.</b>
/// The env strip (42-10 AC1) already removed the deployment's secrets from the
/// child, and the sandbox blocks egress; this screen is defence in depth for the
/// FILE leg (reads of secret-bearing files) and the obvious env dumps. Its gaps
/// are KNOWN and named, not hidden — same posture as <c>git_operations</c>'
/// documented holes:</para>
/// <list type="bullet">
///   <item>the <c>set</c> builtin's full variable dump is not matched;</item>
///   <item>a redirection-only read (<c>while read l; do …; done &lt; .env</c>) is not matched;</item>
///   <item>any unlisted binary reading a secret file is not matched.</item>
/// </list>
/// <para>A determined exfiltration is stopped by the sandbox, not by this string
/// screen — the screen's job is to make the OBVIOUS secret read a gated, audited
/// decision.</para>
/// </summary>
public static class ShellSecretReadScreen
{
    /// <summary>Default secret-bearing path globs (D7). Overridable via config.</summary>
    public static readonly IReadOnlyList<string> DefaultSecretPaths =
        new[] { ".env", ".env.*", "/run/secrets", "*.pem", "*.key" };

    // env / printenv / export -p / declare -x — a whole-environment dump.
    private static readonly Regex s_envDump = new(
        @"(?<![\w./-])(env|printenv)(?![\w./-])|export\s+-p\b|declare\s+-x\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A read verb that could stream a file's contents.
    private static readonly Regex s_readVerb = new(
        @"(?<![\w./-])(cat|less|more|head|tail|grep|cut|awk|sed|sort|xxd|base64|od|strings)(?![\w./-])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="command"/> looks like it reads a secret VALUE: an
    /// environment dump, or a read verb whose text touches a configured secret
    /// path. <paramref name="secretPaths"/> defaults to
    /// <see cref="DefaultSecretPaths"/> when null/empty.
    /// </summary>
    public static bool Matches(string? command, IReadOnlyList<string>? secretPaths = null)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        if (s_envDump.IsMatch(command))
            return true;

        if (!s_readVerb.IsMatch(command))
            return false;

        var paths = secretPaths is { Count: > 0 } ? secretPaths : DefaultSecretPaths;
        foreach (var glob in paths)
        {
            if (TouchesGlob(command, glob))
                return true;
        }

        return false;
    }

    private static bool TouchesGlob(string command, string glob)
    {
        // A deliberately loose containment test (best-effort): boundaries are
        // WORD-char only, so a path prefix (`./.env`) or a directory's contents
        // (`/run/secrets/db-password`) still match. Not shell-accurate globbing —
        // the sandbox is the control, this is the tripwire.
        if (glob.StartsWith("*.", StringComparison.Ordinal))
        {
            var ext = glob[1..]; // ".pem"
            return Regex.IsMatch(command, $@"{Regex.Escape(ext)}(?![\w])", RegexOptions.IgnoreCase);
        }

        if (glob.EndsWith(".*", StringComparison.Ordinal))
        {
            var stem = glob[..^2]; // ".env"
            return Regex.IsMatch(command, $@"(?<![\w]){Regex.Escape(stem)}\.[\w-]+", RegexOptions.IgnoreCase);
        }

        // A literal path/file: match preceded by a non-word char (start / space /
        // '/' / './') and NOT followed by a word char (end / space / '/' for a
        // directory's contents).
        return Regex.IsMatch(command, $@"(?<![\w]){Regex.Escape(glob)}(?![\w])", RegexOptions.IgnoreCase);
    }
}
