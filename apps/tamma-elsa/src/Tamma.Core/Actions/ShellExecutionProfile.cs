namespace Tamma.Core.Actions;

/// <summary>
/// Story 42-10 (AC3, D4) — the shell executor's shipped autonomy level is a
/// property of the deployment PROFILE, not of an admin assignment.
///
/// <para><b>Why a static ambient rather than an assignment row.</b> The resolver
/// ladder composes by <c>max()</c> (<c>AutonomyGateEvaluator</c>), so a platform
/// assignment row can only RAISE the shipped level, never lower the shipped 80 to
/// 40. The sandboxed profile EARNS the lower level (egress blocked + CWD confined
/// + env stripped), so the level must be a catalog-build INPUT: the catalog is
/// static per process, and this is the one clean mechanism.</para>
///
/// <para><b>Ordering is fail-loud, not fragile.</b> <see cref="Initialize"/> must
/// run at host composition BEFORE anything touches <see cref="ActionCatalog"/> —
/// the catalog freezes <c>s_descriptors</c> at first access, reading
/// <see cref="ShippedMinAutonomy"/> then. If a host touched the catalog first, it
/// would freeze on the unsandboxed default; <c>ActionCatalogStartupValidator</c>
/// re-derives the expected level from configuration and refuses to boot on a
/// mismatch, so a wrong ordering is a boot failure, never a silently wrong level.
/// In a test process <see cref="Initialize"/> is never called, so the static
/// catalog ships the unsandboxed 80 (matching every pin); the sandboxed arm is
/// exercised through the <c>ActionCatalog.BuildDescriptors(int)</c> seam, which
/// never touches this ambient.</para>
/// </summary>
public static class ShellExecutionProfile
{
    /// <summary>The shipped level when the sandbox profile is verified in force.</summary>
    public const int SandboxedLevel = 40;

    /// <summary>The shipped level for an unsandboxed shell (arbitrary egress + the
    /// governed-route curl bypass; the deployment's secrets are already stripped by
    /// the env allowlist, but the reach is not).</summary>
    public const int UnsandboxedLevel = 80;

    private static readonly object s_lock = new();
    private static bool s_sandboxed;
    private static bool s_initialized;

    /// <summary>True once a host has composed the profile.</summary>
    public static bool IsInitialized
    {
        get { lock (s_lock) { return s_initialized; } }
    }

    /// <summary>Whether the sandbox profile is declared in force.</summary>
    public static bool Sandboxed
    {
        get { lock (s_lock) { return s_sandboxed; } }
    }

    /// <summary>The shell/process.spawn shipped level for the current profile.</summary>
    public static int ShippedMinAutonomy => Sandboxed ? SandboxedLevel : UnsandboxedLevel;

    /// <summary>
    /// Set the profile once, at host composition, before any catalog access.
    /// Idempotent for the same value; a second call with a DIFFERENT value throws
    /// (two composition paths disagreeing is a wiring fault, not a runtime toggle).
    /// </summary>
    public static void Initialize(bool sandboxed)
    {
        lock (s_lock)
        {
            if (s_initialized && s_sandboxed != sandboxed)
            {
                throw new InvalidOperationException(
                    $"ShellExecutionProfile already initialized to Sandboxed={s_sandboxed}; "
                    + $"a second Initialize({sandboxed}) disagrees. The profile is set once per process.");
            }

            s_sandboxed = sandboxed;
            s_initialized = true;
        }
    }
}
