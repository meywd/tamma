namespace Tamma.Activities.Blocker;

/// <summary>
/// Read-back coercion for values arriving on a bookmark resume input
/// (<c>context.WorkflowInput</c>) for the blocker-diagnosis resume callbacks.
///
/// <para>The in-process workflow runtime keeps a resumed value as a boxed CLR type
/// (a boxed <see cref="bool"/> for a flag). A SERIALIZING dispatcher
/// (distributed / MassTransit / ProtoActor) instead delivers the same value as a
/// <see cref="string"/> or a <see cref="System.Text.Json.JsonElement"/> after a
/// round-trip. A bare <c>value is true</c> pattern-match only matches the boxed-bool
/// path — under serialization it silently evaluates <c>false</c> and the resume
/// advances the wrong branch while still returning HTTP 200 (a silent failure).</para>
///
/// <para>This mirrors the merge/deploy house convention of reading resumed values via
/// <c>.ToString()</c> then parsing (see <c>WaitForMergeApprovalActivity</c>).</para>
/// </summary>
internal static class BlockerResumeInput
{
    /// <summary>
    /// Coerce a resumed value to <see cref="bool"/> tolerant of the in-process
    /// boxed-<see cref="bool"/> path AND a serializing runtime that delivers the value
    /// as a <see cref="string"/> (<c>"true"</c>/<c>"false"</c>) or a
    /// <see cref="System.Text.Json.JsonElement"/>. <c>true</c> iff a real boxed
    /// <see cref="bool"/> <c>true</c>, or the value's string form is <c>"true"</c>
    /// (case-insensitive) — <see cref="System.Text.Json.JsonElement.ToString"/> yields
    /// <c>"True"</c>/<c>"False"</c> for a JSON boolean, so a JsonElement flows through the
    /// string comparison. A <c>null</c> / missing value is <c>false</c>.
    /// </summary>
    internal static bool AsBool(object? value)
        => ResumeInput.AsBool(value);
}
