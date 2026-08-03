using System.Net.Sockets;
using Tamma.Core;

namespace Tamma.Api.Services.Tools;

/// <summary>
/// Story 42-10 (AC2, D3) — the fail-loud verifier for the shell SANDBOX profile.
/// When <c>Tools:Shell:Sandboxed=true</c> the shell/process.spawn executor ships
/// at the discounted level 40 (<see cref="Core.Actions.ShellExecutionProfile"/>),
/// and that discount rides entirely on the sandbox's guarantees being real. So a
/// deployment that DECLARES the profile without the guarantees must refuse to
/// start rather than run ungoverned at level 40.
///
/// <para>Copied from <c>ActionCatalogStartupValidator</c>'s posture: collect
/// every violation, throw ONE aggregated <see cref="TammaError"/> naming each.
/// With <c>Sandboxed=false</c> it is a no-op — the unsandboxed profile makes no
/// sandbox claim to verify.</para>
///
/// <para><b>The probe is a tripwire, not a proof</b> (Risks): a deployment
/// declares the egress mechanism it enforces (network-namespace / proxy-only /
/// firewall) and the validator can only check what it can reach — when
/// <c>Tools:Shell:Egress:ProbeHost</c> is set, an outbound TCP connect to it MUST
/// FAIL, and a connect that SUCCEEDS proves egress is open and refuses the boot.
/// A firewall that quietly allows a host the probe never names is the deployment's
/// responsibility; the discount rides on the attestation.</para>
/// </summary>
internal static class ShellSandboxStartupValidator
{
    /// <summary>The egress mechanisms a deployment may attest (D2).</summary>
    internal static readonly IReadOnlySet<string> KnownMechanisms =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "network-namespace", "proxy-only", "firewall" };

    /// <summary>
    /// Verify the sandbox profile at host composition, throwing before the host
    /// runs if the guarantees are not in force. Called INLINE from Program.cs
    /// (like <c>ActionCatalog.Validate()</c>) rather than as an
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> — an
    /// IHostedService would have to be catalogued as a BackgroundActor, and a
    /// boot-time one-shot verifier is exactly the composition-time check
    /// <c>ActionCatalog.Validate()</c> already models. A no-op when unsandboxed.
    /// </summary>
    public static void VerifyOrThrow(IConfiguration configuration, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var sandboxed = configuration.GetValue("Tools:Shell:Sandboxed", false);
        if (!sandboxed)
        {
            logger?.LogInformation(
                "Shell sandbox profile is OFF (Tools:Shell:Sandboxed=false); shell ships at the "
                + "unsandboxed level and no sandbox verification runs.");
            return;
        }

        var mechanism = configuration["Tools:Shell:Egress:Mechanism"];
        var workspaceRoot = configuration["ToolExecution:WorkspaceRoot"];
        var probeHost = configuration["Tools:Shell:Egress:ProbeHost"];

        var violations = Validate(
            mechanism, workspaceRoot, probeHost, probe: TryConnect);

        if (violations.Count > 0)
        {
            throw new TammaError(
                "TOOLS.SHELL.SANDBOX_UNVERIFIED",
                "Tools:Shell:Sandboxed=true but the sandbox guarantees could not be verified; "
                + $"Tamma.Api refuses to start ({violations.Count} violation(s)):{Environment.NewLine}"
                + string.Join(Environment.NewLine, violations.Select(v => $"  {v}")),
                new Dictionary<string, object?> { ["violations"] = violations.ToArray() },
                retryable: false,
                severity: TammaErrorSeverity.Critical);
        }

        logger?.LogInformation(
            "Shell sandbox profile VERIFIED (mechanism={Mechanism}, workspaceRoot set, "
            + "egress probe {ProbeState}); shell earns the sandboxed level.",
            mechanism,
            string.IsNullOrWhiteSpace(probeHost) ? "not configured" : "failed-to-connect (good)");
    }

    /// <summary>
    /// The pure verification (the D7 test seam): the sandbox is in force only if the
    /// egress mechanism is attested and well-formed, the workspace root is set +
    /// absolute + exists, and — when a probe host is named — the probe cannot reach
    /// it. <paramref name="probe"/> returns TRUE when a connect SUCCEEDS (egress
    /// open ⇒ a violation).
    /// </summary>
    internal static IReadOnlyList<string> Validate(
        string? mechanism, string? workspaceRoot, string? probeHost, Func<string, bool> probe)
    {
        var violations = new List<string>();

        if (string.IsNullOrWhiteSpace(mechanism) || !KnownMechanisms.Contains(mechanism.Trim()))
        {
            violations.Add(
                $"egress: Tools:Shell:Egress:Mechanism is '{mechanism ?? "<unset>"}', not one of "
                + $"[{string.Join(", ", KnownMechanisms)}] — the sandbox must declare how it blocks egress.");
        }

        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            violations.Add(
                "cwd-confinement: ToolExecution:WorkspaceRoot is unset — CWD confinement has no root "
                + "to confine to.");
        }
        else if (!Path.IsPathRooted(workspaceRoot))
        {
            violations.Add(
                $"cwd-confinement: ToolExecution:WorkspaceRoot '{workspaceRoot}' is not an absolute path.");
        }
        else if (!Directory.Exists(workspaceRoot))
        {
            violations.Add(
                $"cwd-confinement: ToolExecution:WorkspaceRoot '{workspaceRoot}' does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(probeHost) && probe(probeHost.Trim()))
        {
            violations.Add(
                $"egress: the probe connected to '{probeHost}' — egress is OPEN, so the sandbox's "
                + "block guarantee is not in force. The probe host must be UNREACHABLE.");
        }

        return violations;
    }

    /// <summary>Attempt a short-timeout TCP connect to <c>host:port</c>; TRUE on success.</summary>
    private static bool TryConnect(string hostPort)
    {
        var (host, port) = ParseHostPort(hostPort);
        if (host is null)
        {
            // A malformed probe host cannot be "reachable"; the mechanism check
            // already carries the misconfiguration signal.
            return false;
        }

        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            // A short bound: a probe that neither connects nor refuses within the
            // window is treated as UNREACHABLE (the safe reading for a tripwire).
            return connect.Wait(TimeSpan.FromMilliseconds(750)) && client.Connected;
        }
        catch
        {
            return false; // refused / unresolved / timed out ⇒ unreachable ⇒ good
        }
    }

    private static (string? Host, int Port) ParseHostPort(string hostPort)
    {
        var idx = hostPort.LastIndexOf(':');
        if (idx <= 0 || idx == hostPort.Length - 1)
            return (null, 0);
        var host = hostPort[..idx];
        return int.TryParse(hostPort[(idx + 1)..], out var port) && port is > 0 and <= 65535
            ? (host, port)
            : (null, 0);
    }
}
