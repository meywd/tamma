namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC5/AC16 — thrown by the tracker's fail-loud vocabulary indexes
/// (see <see cref="TrackerHierarchy"/>) when a declared rule set does not cover
/// a vocabulary exactly. Carries the offending member's name so tests assert on
/// a type + property rather than a message substring. Raised from a static
/// initializer, so a bad rule set is a <see cref="TypeInitializationException"/>
/// at first touch and the process refuses to serve — the
/// <c>PromptFileLoader</c>/<c>SystemPrompts</c> posture.
/// </summary>
public sealed class TrackerVocabularyException : Exception
{
    /// <summary>The vocabulary member whose rule is missing or duplicated.</summary>
    public string MemberName { get; }

    public TrackerVocabularyException(string memberName, string message)
        : base(message)
    {
        MemberName = memberName;
    }
}
