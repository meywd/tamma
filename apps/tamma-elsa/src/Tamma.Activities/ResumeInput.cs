namespace Tamma.Activities;

/// <summary>
/// Tolerant read-back coercion for a control-flow boolean arriving on a workflow
/// resume input (<c>context.WorkflowInput</c> for a bookmark resume) OR on a
/// dispatched-workflow <c>Result</c> dictionary.
///
/// <para>The in-process workflow runtime keeps such a value as a boxed CLR type
/// (a boxed <see cref="bool"/> for a flag). A SERIALIZING dispatcher (distributed /
/// MassTransit / ProtoActor) instead delivers the same value as a
/// <see cref="string"/> or a <see cref="System.Text.Json.JsonElement"/> after a
/// round-trip. A bare <c>value is true</c> pattern-match — or a
/// <c>switch { bool b =&gt; b, string s =&gt; ..., _ =&gt; false }</c> that maps a
/// <see cref="System.Text.Json.JsonElement"/> to the <c>_ =&gt; false</c> arm — only
/// matches the boxed-bool / string paths; under serialization it silently evaluates
/// <c>false</c> and the resume advances the WRONG branch while still returning HTTP
/// 200 (a silent mis-branch).</para>
///
/// <para>This is the promoted, shared home of the tolerant read first introduced for
/// the blocker resume callbacks (<see cref="Blocker.BlockerResumeInput"/>), reused
/// across <c>Tamma.Activities</c> and <c>Tamma.ElsaServer</c>. It mirrors the
/// merge/deploy house convention of reading resumed values via <c>.ToString()</c>
/// then comparing.</para>
/// </summary>
public static class ResumeInput
{
    /// <summary>
    /// Coerce a resumed value to <see cref="bool"/> tolerant of the in-process
    /// boxed-<see cref="bool"/> path AND a serializing runtime that delivers the value
    /// as a <see cref="string"/> (<c>"true"</c>/<c>"false"</c>) or a
    /// <see cref="System.Text.Json.JsonElement"/>. <c>true</c> iff a real boxed
    /// <see cref="bool"/> <c>true</c>, or the value's string form is <c>"true"</c>
    /// (case-insensitive) — <see cref="System.Text.Json.JsonElement.ToString"/> yields
    /// <c>"True"</c>/<c>"False"</c> for a JSON boolean, so a JsonElement flows through
    /// the string comparison. A <c>null</c> / missing value is <c>false</c>
    /// (fail-closed).
    /// </summary>
    public static bool AsBool(object? value)
        => value is bool b
            ? b
            : string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
}
