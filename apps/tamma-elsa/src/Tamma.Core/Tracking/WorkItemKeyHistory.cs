namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC8 — the pure rule for the one sanctioned exception to the
/// frozen work-item key (see <see cref="WorkItemRef"/>): a deliberate operator
/// <b>re-key</b>, e.g. renaming a project prefix <c>TAM → TAMMA</c>. When that
/// happens the outgoing key is appended to the item's <c>PreviousKeys</c>
/// (stored by 44-1, empty by default) and lookup by key must resolve
/// current-or-previous, so every already-written <c>DocumentInstance.IssueId</c>
/// and DCB event tag still finds its item.
///
/// <para>A <b>project move</b> alone produces no entry here — the key does not
/// change on a move (the freeze rule), so there is nothing to record.</para>
///
/// <para>44-1's repository and 44-2's lookup endpoint both call this type so the
/// resolve-old-keys rule has exactly one implementation.</para>
/// </summary>
public static class WorkItemKeyHistory
{
    /// <summary>
    /// Record <paramref name="outgoingKey"/> as a previous key. Idempotent
    /// (re-recording the same key does not duplicate it) and order-preserving,
    /// oldest first. Returns a new list; the input is never mutated.
    /// </summary>
    public static IReadOnlyList<string> Record(IReadOnlyList<string> previousKeys, WorkItemRef outgoingKey)
    {
        ArgumentNullException.ThrowIfNull(previousKeys);

        var wire = outgoingKey.ToWire();
        if (previousKeys.Contains(wire, StringComparer.Ordinal))
            return previousKeys;

        var next = new List<string>(previousKeys.Count + 1);
        next.AddRange(previousKeys);
        next.Add(wire);
        return next;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> resolves to the item identified by
    /// <paramref name="currentKey"/> — either the current key itself or any
    /// recorded previous key (ordinal comparison; keys are never normalized).
    /// </summary>
    public static bool Matches(WorkItemRef candidate, WorkItemRef currentKey, IReadOnlyList<string> previousKeys)
    {
        ArgumentNullException.ThrowIfNull(previousKeys);

        if (candidate == currentKey)
            return true;

        var wire = candidate.ToWire();
        foreach (var previous in previousKeys)
        {
            if (string.Equals(previous, wire, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
