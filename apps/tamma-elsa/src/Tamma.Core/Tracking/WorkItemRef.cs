using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Tamma.Core.Tracking;

/// <summary>
/// Story 44-0 AC7/AC8 — the human-readable identity of a native work item:
/// <c>&lt;PROJECT_KEY&gt;-&lt;n&gt;</c>, e.g. <c>TAM-142</c>.
/// <see cref="ToWire"/> is the string 44-1 writes into <c>work_items."Key"</c>
/// and 44-7 writes into <c>DocumentInstance.IssueId</c> and DCB
/// <c>tags.issueId</c> — the join key the whole of Epic 44 rests on (epic README
/// §2). Because that namespace is a plain <c>string</c>, a native work item
/// inherits the entire Epic 39 spine (document store, lineage, re-entry,
/// approvals, replay) with zero modification.
///
/// <para><b>The key is frozen at creation.</b> It is minted once, from the
/// sequence of the project the item is created in, and <b>never re-minted</b> —
/// including when the item is moved to another project. After a move the key
/// prefix no longer matches the project's key. That is intended and must not be
/// "fixed": the key is already written into <c>DocumentInstance.IssueId</c> and
/// into DCB event tags, and event tags are append-only — there is no update
/// path — so a re-mint orphans the item's entire document lineage and event
/// history silently and unrecoverably. (Linear re-mints on a team move, which is
/// rare, and needed <c>previousIdentifiers</c> for it; a project move here is
/// the common case, so we freeze instead and pay a cosmetic mismatch rather than
/// a data one.)</para>
///
/// <para>The one sanctioned exception — a deliberate operator re-key, e.g.
/// renaming a project prefix <c>TAM → TAMMA</c> — is recorded via
/// <see cref="WorkItemKeyHistory"/>: the outgoing key is appended to the item's
/// <c>PreviousKeys</c> and lookup resolves current-or-previous.</para>
///
/// <para>Validation is strict and non-normalizing (the <c>EnumWire</c> ordinal
/// posture): a bad key is rejected, never coerced — a key that round-trips
/// through a lower-casing layer and back is a silent identity change on a row
/// other tables reference by string. <c>ProjectKey</c> must match
/// <c>^[A-Z][A-Z0-9]{1,9}$</c> and <c>Number</c> must be ≥ 1.</para>
///
/// <para>Note: <c>default(WorkItemRef)</c> (the uninitialized struct value) is
/// not a valid reference; construct via the constructor, <see cref="Parse"/> or
/// <see cref="TryParse"/>.</para>
/// </summary>
public readonly record struct WorkItemRef
{
    private const int MinProjectKeyLength = 2;
    private const int MaxProjectKeyLength = 10;

    /// <summary>The frozen project key prefix, e.g. <c>TAM</c>.</summary>
    public string ProjectKey { get; }

    /// <summary>The per-project sequence number, ≥ 1.</summary>
    public int Number { get; }

    /// <exception cref="TammaError">
    /// Code <c>TRACKER.INVALID_WORK_ITEM_KEY</c> when
    /// <paramref name="projectKey"/> does not match <c>^[A-Z][A-Z0-9]{1,9}$</c>
    /// or <paramref name="number"/> &lt; 1.
    /// </exception>
    public WorkItemRef(string projectKey, int number)
    {
        if (!IsValidProjectKey(projectKey))
        {
            throw new TammaError(
                "TRACKER.INVALID_WORK_ITEM_KEY",
                $"Invalid project key: '{projectKey}'. A project key is 2-10 characters, " +
                "upper-case A-Z0-9, starting with a letter (^[A-Z][A-Z0-9]{1,9}$). " +
                "Keys are never normalized — fix the input.",
                new Dictionary<string, object?> { ["projectKey"] = projectKey },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        if (number < 1)
        {
            throw new TammaError(
                "TRACKER.INVALID_WORK_ITEM_KEY",
                $"Invalid work item number: {number}. Numbers are minted from the project sequence and start at 1.",
                new Dictionary<string, object?> { ["projectKey"] = projectKey, ["number"] = number },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        ProjectKey = projectKey;
        Number = number;
    }

    /// <summary>
    /// The canonical wire form, e.g. <c>TAM-142</c> — the value written into
    /// <c>DocumentInstance.IssueId</c> and DCB <c>tags.issueId</c>.
    /// </summary>
    public string ToWire() => $"{ProjectKey}-{Number.ToString(CultureInfo.InvariantCulture)}";

    public override string ToString() => ToWire();

    /// <summary>
    /// Whether <paramref name="candidate"/> is a valid project key:
    /// <c>^[A-Z][A-Z0-9]{1,9}$</c>. Strict — lower-case is rejected, not
    /// upper-cased. 44-2 reuses this to validate project creation.
    /// </summary>
    public static bool IsValidProjectKey([NotNullWhen(true)] string? candidate)
    {
        if (candidate is null ||
            candidate.Length is < MinProjectKeyLength or > MaxProjectKeyLength)
        {
            return false;
        }

        if (candidate[0] is not (>= 'A' and <= 'Z'))
            return false;

        for (var i = 1; i < candidate.Length; i++)
        {
            if (candidate[i] is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Strict, non-normalizing parse of a wire-form key (<c>TAM-142</c>).
    /// Rejects lower-case keys, missing/extra separators, a zero or negative
    /// number, leading zeros (<c>TAM-01</c> would re-serialize as <c>TAM-1</c>,
    /// a coercion), and any surrounding whitespace.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? input, out WorkItemRef value)
    {
        value = default;
        if (string.IsNullOrEmpty(input))
            return false;

        var separator = input.IndexOf('-');
        if (separator <= 0 || separator == input.Length - 1)
            return false;

        var key = input[..separator];
        if (!IsValidProjectKey(key))
            return false;

        var numberPart = input.AsSpan(separator + 1);
        if (numberPart[0] is '0')
            return false; // covers "TAM-0" and non-canonical leading zeros ("TAM-01")

        foreach (var c in numberPart)
        {
            if (c is not (>= '0' and <= '9'))
                return false; // also rejects "TAM--1", "TAM-1x", "TAM-1-2"
        }

        if (!int.TryParse(numberPart, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return false; // overflow

        value = new WorkItemRef(key, number);
        return true;
    }

    /// <summary>
    /// Parse a wire-form key or throw.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>TRACKER.INVALID_WORK_ITEM_KEY</c> for null, empty, or malformed
    /// input.
    /// </exception>
    public static WorkItemRef Parse(string input)
    {
        if (TryParse(input, out var value))
            return value;

        throw new TammaError(
            "TRACKER.INVALID_WORK_ITEM_KEY",
            $"Invalid work item key: '{input}'. Expected '<PROJECT_KEY>-<n>' with an " +
            "upper-case 2-10 character project key and a positive number, e.g. 'TAM-142'. " +
            "Keys are never normalized — fix the input.",
            new Dictionary<string, object?> { ["input"] = input },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }
}
