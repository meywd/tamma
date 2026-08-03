namespace Tamma.Core.Actions;

/// <summary>
/// Story 43-13 — WHO is asking the autonomy gate (Story 43-11 <b>Amendment 4</b>:
/// "who the dial governs: the LLM, and nothing else"). Three kinds of caller
/// exist, and the dial exists for exactly one of them:
///
/// <list type="bullet">
/// <item><see cref="Human"/> — a person acting through their own credential
/// (dashboard JWT, user-scope API key). NEVER gated: the dial is a control on the
/// SYSTEM's autonomy, and gating a person on themselves is absurd. Ordinary RBAC
/// still applies — it ran before the gate did.</item>
/// <item><see cref="Machinery"/> — deterministic automation (sweepers, seeders,
/// task handlers, plumbing writes). NEVER dial-gated: the approval was the human
/// who wrote/merged/configured it, or an upstream gated LLM decision it executes.
/// The <c>enabled</c> off-switch, role restrictions and the fail-closed
/// unreadable-policy posture still apply — only the threshold/dial machinery is
/// bypassed. <b><see cref="Machinery"/> has no wire spelling</b>: it exists only
/// as the in-process declaration Seam D's helper makes
/// (<c>BackgroundActionGate</c>), never as anything a request can claim — a
/// wire-claimable "never gate me" kind would be a self-service bypass.</item>
/// <item><see cref="Llm"/> — the model/agent, the only nondeterministic actor.
/// The dial exists for it alone. <b>This is the fail-closed default</b>
/// (<c>AutonomyQuery.Caller</c>): every engine-token call defaults here, because
/// deterministic workflow steps share <c>TammaApiClient</c> with LLM-driven steps
/// and cannot be told apart. A deterministic engine step wrongly gated is a
/// visible nuisance; an LLM call wrongly waved through is the failure mode this
/// epic exists to prevent.</item>
/// </list>
/// </summary>
public enum CallerKind
{
    /// <summary>A person, acting through their own credential. Never gated.</summary>
    Human,

    /// <summary>Deterministic automation, declared in-process only. Never
    /// dial-gated (enabled=false, roles and fail-closed degradation still
    /// apply).</summary>
    Machinery,

    /// <summary>The model/agent — the only caller the dial governs. The
    /// fail-closed default for everything not provably human or declared
    /// machinery.</summary>
    Llm,
}

/// <summary>Wire spellings for <see cref="CallerKind"/> (audit tags).</summary>
public static class CallerKindExtensions
{
    /// <summary>Lowercase wire value: <c>human</c> / <c>machinery</c> / <c>llm</c>.</summary>
    public static string ToWire(this CallerKind kind) => kind switch
    {
        CallerKind.Human => "human",
        CallerKind.Machinery => "machinery",
        _ => "llm",
    };
}
