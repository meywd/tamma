using Microsoft.Extensions.Configuration;

namespace Tamma.Activities.ADL;

/// <summary>
/// The operator STOP button for the autonomous loop.
///
/// <para>Engaging it halts NEW dispatch — <see cref="CheckLimitsActivity"/> takes the
/// <c>Stop</c> edge and the watchdog does not re-arm — while leaving the orchestrator
/// itself alive, so clearing the switch resumes the loop on the next cooldown tick with
/// no redeploy and no manual dispatch. In-flight cycles are deliberately NOT killed:
/// stopping mid-cycle would strand branches and PRs; the ceiling is on starting new work.</para>
///
/// <para>Two sources, either of which stops the loop:</para>
/// <list type="number">
///   <item><description><c>Adl:Stopped=true</c> — configuration/env
///     (<c>Adl__Stopped=true</c>). Survives restarts; needs a process restart to change.</description></item>
///   <item><description>the presence of the file at <c>Adl:StopFilePath</c> (default
///     <c>/var/tamma/adl.stop</c>) — the NO-RESTART path, the one an operator actually
///     reaches for mid-incident (<c>docker exec … touch /var/tamma/adl.stop</c>), and the
///     reason this is not a config key alone.</description></item>
/// </list>
///
/// <para>Every trip is audited: the stop reason is surfaced as the activity's
/// <c>stopReason</c> output, which lands on the <c>ADL.LIMITS.CHECK.COMPLETED</c> DCB
/// event, so "why did the loop stop dispatching at 14:05" is answerable from the event
/// stream rather than from a log file.</para>
/// </summary>
public interface IAdlStopSwitch
{
    /// <summary>
    /// Returns the reason the loop is stopped, or <c>null</c> when it may keep
    /// dispatching. Must never throw — an unreadable switch is reported as "not
    /// stopped" plus a caller-side warning, because a filesystem hiccup must not
    /// silently halt the autonomous loop.
    /// </summary>
    string? GetStopReason();
}

/// <summary>
/// Default <see cref="IAdlStopSwitch"/>: configuration flag + stop file. Constructed
/// directly from <see cref="IConfiguration"/> when no implementation is registered, so
/// the switch works without any DI wiring (the same ctor-or-resolve posture the ADL
/// activities use for their rehydrated-from-store instances).
/// </summary>
public sealed class ConfigAdlStopSwitch : IAdlStopSwitch
{
    /// <summary>Config key for the persistent stop flag.</summary>
    public const string StoppedKey = "Adl:Stopped";

    /// <summary>Config key overriding <see cref="DefaultStopFilePath"/>.</summary>
    public const string StopFilePathKey = "Adl:StopFilePath";

    /// <summary>
    /// Default stop-file location. Chosen to be writable by an operator shelled into the
    /// container without touching the image or the compose file. Set
    /// <c>Adl:StopFilePath</c> to an empty string to disable the file path entirely.
    /// </summary>
    public const string DefaultStopFilePath = "/var/tamma/adl.stop";

    private readonly IConfiguration? _configuration;

    public ConfigAdlStopSwitch(IConfiguration? configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc />
    public string? GetStopReason()
    {
        if (_configuration is null) return null;

        try
        {
            if (_configuration.GetValue<bool?>(StoppedKey) == true)
                return $"operator stop switch engaged ({StoppedKey}=true)";

            var path = _configuration.GetValue<string?>(StopFilePathKey) ?? DefaultStopFilePath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return $"operator stop switch engaged (stop file present at {path})";
        }
        catch (Exception)
        {
            // A config/filesystem fault must NOT be read as "stop". Halting the
            // autonomous loop on an unreadable switch would be a silent outage with
            // the same shape as the bug this lane exists to close; the caller logs it.
            return null;
        }

        return null;
    }
}
