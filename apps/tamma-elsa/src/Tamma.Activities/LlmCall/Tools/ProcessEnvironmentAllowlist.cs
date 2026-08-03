using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Story 42-10 (AC1, D1) — the P0 fix for the verified secret leak.
///
/// <para>A shell tool call used to inherit the API process's <b>entire</b>
/// environment: <c>ProcessStartInfo</c> set <c>FileName</c>/<c>WorkingDirectory</c>
/// and redirects but never touched <c>EnvironmentVariables</c>, so the child
/// <c>/bin/bash -c</c> saw <c>GITHUB_TOKEN</c>, <c>JWT_SECRET</c>, and the DB
/// connection strings. That made <c>env</c> in a tool call an ungoverned
/// <c>secret.read</c> — any command could exfiltrate the deployment's
/// credentials.</para>
///
/// <para><b>This strip is unconditional</b> — it runs in both the sandboxed and
/// the unsandboxed profile, because inheriting the API's secrets is never
/// correct. The child gets an explicit allowlist and nothing else: the POSIX
/// basics needed to run a shell, plus a deployment-controlled additive list
/// (<c>Tools:Shell:EnvAllowlist</c>, names only — the value is copied from the
/// current process env if present, never taken from config).</para>
///
/// <para><b>Both spawn sites are covered.</b> <see cref="ShellExecuteTool"/> and
/// <see cref="RunTestsTool"/> build the identical <c>ProcessStartInfo</c> shape;
/// the leak is the same in both, so the strip is applied in both.</para>
/// </summary>
public static class ProcessEnvironmentAllowlist
{
    /// <summary>
    /// The base allowlist: variables a shell needs to run at all, with no secret
    /// material among them. Locale vars are matched by prefix (<c>LC_</c>) below.
    /// </summary>
    public static readonly IReadOnlyList<string> BaseAllowlist = new[]
    {
        "PATH", "HOME", "TMPDIR", "TEMP", "TMP", "TERM", "USER", "LOGNAME",
        "SHELL", "LANG", "LANGUAGE", "PWD",
    };

    /// <summary>The config key holding the deployment's additive allowlist (names only).</summary>
    public const string AdditiveConfigKey = "Tools:Shell:EnvAllowlist";

    /// <summary>
    /// Clear the child's inherited environment and repopulate it from the base
    /// allowlist + the configured additive names + locale (<c>LC_*</c>). Only
    /// names present in the current process env are copied; the value is the
    /// live value, never a config-supplied string.
    /// </summary>
    public static void Apply(ProcessStartInfo psi, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(psi);

        // UseShellExecute=false is required for EnvironmentVariables to take
        // effect; both call sites already set it, but assert so a future edit
        // that flips it fails loud instead of silently re-leaking.
        if (psi.UseShellExecute)
        {
            throw new InvalidOperationException(
                "ProcessEnvironmentAllowlist requires UseShellExecute=false; "
                + "otherwise EnvironmentVariables is ignored and the child re-inherits the parent env.");
        }

        var allowed = BuildAllowedNames(configuration);

        // Snapshot what the parent actually has BEFORE clearing psi's copy, so a
        // name in the allowlist is populated from the real current value.
        var parentEnv = Environment.GetEnvironmentVariables();

        psi.EnvironmentVariables.Clear();

        foreach (System.Collections.DictionaryEntry entry in parentEnv)
        {
            if (entry.Key is not string name)
                continue;

            if (IsAllowed(name, allowed))
            {
                psi.EnvironmentVariables[name] = entry.Value as string;
            }
        }
    }

    private static HashSet<string> BuildAllowedNames(IConfiguration configuration)
    {
        var allowed = new HashSet<string>(BaseAllowlist, StringComparer.Ordinal);

        foreach (var extra in configuration.GetSection(AdditiveConfigKey).Get<string[]>() ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(extra))
                allowed.Add(extra.Trim());
        }

        return allowed;
    }

    private static bool IsAllowed(string name, HashSet<string> allowed)
    {
        // Locale variables (LC_ALL, LC_CTYPE, …) are always safe and matched by prefix.
        return allowed.Contains(name)
               || name.StartsWith("LC_", StringComparison.Ordinal);
    }
}
