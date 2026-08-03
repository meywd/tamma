namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Story 42-10 (AC4, D2) — under the sandboxed profile, confine a shell command
/// to the workspace root before it spawns.
///
/// <para><b>This is a command-string screen, not a jail.</b> It catches the
/// obvious escapes — an absolute path outside the workspace (<c>cat /etc/passwd</c>),
/// <c>..</c> traversal that leaves the root (<c>cat ../../secret</c>), and a
/// <c>cd</c> out of the tree (<c>cd / &amp;&amp; ls</c>) — by resolving every
/// path-shaped token through <see cref="PathValidator.ResolveSafePath"/>. An
/// interpreter, a relative symlink, or a redirection target can still escape a
/// string screen; heavier isolation is a separate concern and the egress block
/// plus the env strip are the real controls. Same best-effort posture the
/// <c>git_operations</c> holes are recorded with.</para>
///
/// <para>Runs ONLY when <c>Tools:Shell:Sandboxed=true</c>. Unsandboxed behaviour
/// is byte-identical to before this screen existed.</para>
/// </summary>
public static class WorkspaceConfinementScreen
{
    // Shell control operators that separate one command from the next. Splitting
    // on them lets a token scan see `cd /` in `cd / && ls`. Redirection operators
    // (`>`/`<`) are deliberately NOT split — redirection is a documented gap.
    private static readonly string[] ControlOperators = { "&&", "||", ";", "|", "&", "\n" };

    /// <summary>
    /// Returns a human-readable reason if the command reaches outside the
    /// workspace root, or null if every path-shaped token stays inside.
    /// </summary>
    public static string? GetViolation(string command, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(workspaceRoot))
            return null;

        foreach (var token in Tokenize(command))
        {
            if (!LooksLikePath(token))
                continue;

            try
            {
                PathValidator.ResolveSafePath(token, workspaceRoot);
            }
            catch (InvalidOperationException)
            {
                return $"'{token}' resolves outside the workspace root";
            }
            catch (ArgumentException)
            {
                // Empty/malformed token — not a confinement escape.
            }
        }

        return null;
    }

    private static IEnumerable<string> Tokenize(string command)
    {
        var normalized = command;
        foreach (var op in ControlOperators)
            normalized = normalized.Replace(op, " ");

        foreach (var raw in normalized.Split(
                     new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Unquote(raw);
        }
    }

    private static string Unquote(string token)
    {
        var t = token;
        // Strip a leading redirection sigil (2>, >, <) so the path after it is scanned.
        while (t.Length > 0 && (t[0] == '>' || t[0] == '<'
                                || (char.IsDigit(t[0]) && t.Length > 1 && (t[1] == '>' || t[1] == '<'))))
        {
            t = t[0] == '>' || t[0] == '<' ? t[1..] : t[2..];
        }

        if (t.Length >= 2
            && ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\'')))
        {
            t = t[1..^1];
        }

        return t;
    }

    private static bool LooksLikePath(string token)
    {
        if (string.IsNullOrEmpty(token) || token[0] == '-')
            return false;

        // Absolute POSIX path, home-relative, or a Windows drive path.
        if (token[0] == '/' || token.StartsWith("~/", StringComparison.Ordinal)
            || (token.Length >= 3 && char.IsLetter(token[0]) && token[1] == ':'
                && (token[2] == '\\' || token[2] == '/')))
        {
            return true;
        }

        // A `..` segment anywhere is a traversal attempt worth resolving.
        return ContainsParentSegment(token);
    }

    private static bool ContainsParentSegment(string token)
    {
        foreach (var seg in token.Split('/', '\\'))
        {
            if (seg == "..")
                return true;
        }

        return false;
    }
}
