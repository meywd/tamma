namespace Tamma.Core.Documents.Resume;

/// <summary>
/// Story 39-10 (AC2, Design Decision D3) — the two resume modes a lifecycle
/// workflow can declare. A workflow is resumable either because it SUSPENDS on a
/// deterministic tenant-folded bookmark awaiting external input
/// (<see cref="BookmarkSuspend"/>), or because — after a crash / restart / definition
/// version bump — it RE-ENTERS from the latest accepted document state reconstructed
/// from the store + DCB events (<see cref="LatestStateReEntry"/>), or both.
/// </summary>
public enum ResumeMode
{
    /// <summary>Suspends on a canonical bookmark (the generalized Clarify/Design pattern).</summary>
    BookmarkSuspend,

    /// <summary>Re-enters from the latest-accepted read + DCB events (never from Elsa internals).</summary>
    LatestStateReEntry,

    /// <summary>Both a bookmark suspend AND crash re-entry (the <c>DocumentLifecycleWorkflow</c> posture).</summary>
    Both,
}

/// <summary>
/// Story 39-10 (AC2, D3) — the STATIC, reflectable resume declaration a lifecycle
/// workflow carries. Placed on the workflow class so the structural build gate
/// (<c>ResumableStandardStructuralTests</c>) can enumerate it: "is this workflow
/// resumable?" is answered by reflection, not by reading source. This is data a
/// test enumerates, not a doc comment (AC2).
///
/// <para><see cref="SuspendActivities"/> names the canonical suspend-activity
/// <see cref="Type"/>s the workflow registers (the "which builder" clause of AC2 —
/// each canonical gate type owns exactly one bookmark builder). It is REQUIRED
/// non-empty for <see cref="ResumeMode.BookmarkSuspend"/> / <see cref="ResumeMode.Both"/>
/// (a bookmark-suspend workflow that names no suspend activity is a contradiction the
/// gate rejects). A <c>Type[]</c> needs no Elsa reference, so this attribute lives in
/// <c>Tamma.Core</c> with the rest of the document vocabulary (39-2 pattern).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ResumeBehaviorAttribute : Attribute
{
    public ResumeBehaviorAttribute(ResumeMode mode)
    {
        Mode = mode;
    }

    /// <summary>The declared resume mode.</summary>
    public ResumeMode Mode { get; }

    /// <summary>
    /// The canonical suspend-activity types this workflow registers. REQUIRED
    /// non-empty for <see cref="ResumeMode.BookmarkSuspend"/> / <see cref="ResumeMode.Both"/>;
    /// left empty for a pure <see cref="ResumeMode.LatestStateReEntry"/> workflow.
    /// </summary>
    public Type[] SuspendActivities { get; init; } = Array.Empty<Type>();
}
