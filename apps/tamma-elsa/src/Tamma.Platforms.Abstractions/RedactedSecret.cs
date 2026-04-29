namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Story 31-8 — wrapper struct whose <see cref="ToString"/> never
/// emits the underlying value. Use anywhere a secret value flows
/// through a logger / structured-event pipeline that may stringify
/// arguments behind the caller's back. Implicitly converts from
/// <see cref="string"/> so call sites can pass the value as-is and
/// rely on the type system to catch the "oops, I logged the secret"
/// foot-gun.
///
/// <para>Round-trip:</para>
/// <code>
/// RedactedSecret s = "ghs_xxx";              // implicit ctor
/// _logger.LogInformation("token {S}", s);    // logs "[redacted:8]"
/// var raw = s.Reveal();                      // intentional reveal
/// var http = new HttpRequestMessage { ... }; // raw goes on the wire
/// </code>
///
/// <para>The struct is intentionally <c>readonly</c> and not
/// <c>IComparable</c>/<c>IEquatable</c> — equality on a secret is
/// rarely useful and would invite a "compare two
/// <see cref="RedactedSecret"/> values" usage that side-channels
/// length. Tests may use <see cref="Reveal"/> to compare.</para>
/// </summary>
public readonly struct RedactedSecret
{
    private readonly string _value;

    /// <summary>
    /// Construct from a plaintext value. Treats null as empty so
    /// <c>(RedactedSecret)null</c> never throws.
    /// </summary>
    public RedactedSecret(string? value)
    {
        _value = value ?? string.Empty;
    }

    /// <summary>
    /// Implicit conversion so callers can pass a string anywhere a
    /// <see cref="RedactedSecret"/> is expected without ceremony.
    /// </summary>
    public static implicit operator RedactedSecret(string? value) => new(value);

    /// <summary>
    /// Length of the underlying value. Safe to log; reveals only the
    /// length channel which is already implicit in
    /// <see cref="ToString"/>.
    /// </summary>
    public int Length => _value.Length;

    /// <summary>
    /// True when the wrapped value is empty.
    /// </summary>
    public bool IsEmpty => _value.Length == 0;

    /// <summary>
    /// Intentional reveal — call before sending the value over the wire
    /// or to a sealed-box / HMAC primitive. The named method makes
    /// reveals greppable in code review.
    /// </summary>
    public string Reveal() => _value;

    /// <summary>
    /// Hard-coded redaction string. Logging frameworks (Serilog,
    /// Microsoft.Extensions.Logging) call <see cref="ToString"/> when
    /// formatting structured arguments unless the argument is itself
    /// destructured — so this is the last line of defence.
    /// </summary>
    public override string ToString() => $"[redacted:{_value.Length} chars]";
}
