namespace Tamma.Api.Logging;

/// <summary>
/// Strips log-injection vectors from user-controlled strings before they are
/// handed to <see cref="Microsoft.Extensions.Logging.ILogger"/>. Structured
/// logging alone isn't enough: when the sink serialises a parameter into the
/// final message text (console, file), raw <c>\r\n</c> can forge additional
/// log entries. CodeQL's "Log entries created from user input" rule flags any
/// unsanitized flow from HTTP inputs into logs.
/// </summary>
public static class LogSanitizer
{
    private const int MaxLength = 200;

    /// <summary>
    /// Replaces CR, LF, and TAB with visible escapes, strips other control
    /// characters, and truncates to <see cref="MaxLength"/>. Returns "&lt;null&gt;"
    /// for null input so the log still emits a stable token.
    /// </summary>
    public static string Clean(string? value)
    {
        if (value is null) return "<null>";
        if (value.Length == 0) return "";

        var buf = new System.Text.StringBuilder(Math.Min(value.Length, MaxLength));
        var limit = Math.Min(value.Length, MaxLength);
        for (var i = 0; i < limit; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '\r': buf.Append("\\r"); break;
                case '\n': buf.Append("\\n"); break;
                case '\t': buf.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7F) buf.Append('?');
                    else buf.Append(c);
                    break;
            }
        }

        if (value.Length > MaxLength) buf.Append("…[truncated]");
        return buf.ToString();
    }
}
