namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-8 — parse/modify/serialize helper for Cranl's env-var
/// text format. Cranl stores env as a single string <c>K=V\nK2=V2\n</c>;
/// <see cref="PutEnvironmentAsync"/> replaces the entire set. This
/// helper lets the rotation handler merge a single key without
/// clobbering other vars.
///
/// <para>Key ordering: parse preserves insertion order so a round-trip
/// through <see cref="Parse"/> + <see cref="Serialize"/> yields the
/// same text (modulo trailing whitespace). New keys inserted by
/// <see cref="Merge"/> are appended at the end.</para>
///
/// <para>Values may contain <c>=</c> (only the first <c>=</c> on each
/// line is a separator). Empty lines and lines without <c>=</c> are
/// treated as comments and preserved verbatim.</para>
///
/// <para>No log-safe serialization lives here — see
/// <see cref="DiffKeys"/> for the PII-safe change summary.</para>
/// </summary>
public static class CranlEnvText
{
    /// <summary>
    /// Parse a Cranl env-text blob into an ordered list of entries.
    /// Each entry is either a key/value pair or a preserved line
    /// (empty string or comment).
    /// </summary>
    public static IReadOnlyList<EnvEntry> Parse(string text)
    {
        var result = new List<EnvEntry>();
        if (string.IsNullOrEmpty(text)) return result;

        // Split on any line separator (Cranl is Unix-y but operators may
        // paste in CRLF). Trim trailing newline so an empty final line
        // doesn't become a phantom entry.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // Drop the final empty element from a trailing newline.
            if (i == lines.Length - 1 && string.IsNullOrEmpty(line))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                // Empty line, comment, or malformed — preserve as-is.
                result.Add(EnvEntry.FromLine(line));
                continue;
            }
            var key = line[..eq];
            var value = line[(eq + 1)..];
            result.Add(EnvEntry.Pair(key, value));
        }
        return result;
    }

    /// <summary>
    /// Merge a single key update into the parsed entry list. If the
    /// key already exists, its value is replaced in-place; otherwise a
    /// new entry is appended.
    /// </summary>
    public static IReadOnlyList<EnvEntry> Merge(
        IReadOnlyList<EnvEntry> current,
        string key,
        string value)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Env key must be non-empty.", nameof(key));

        var merged = new List<EnvEntry>(current.Count + 1);
        var replaced = false;
        foreach (var entry in current)
        {
            if (entry.IsPair && string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                merged.Add(EnvEntry.Pair(key, value));
                replaced = true;
            }
            else
            {
                merged.Add(entry);
            }
        }
        if (!replaced) merged.Add(EnvEntry.Pair(key, value));
        return merged;
    }

    /// <summary>
    /// Serialize an entry list back to Cranl's wire format. Lines are
    /// joined with <c>\n</c> and a trailing newline is appended when
    /// there is at least one entry.
    /// </summary>
    public static string Serialize(IReadOnlyList<EnvEntry> entries)
    {
        if (entries.Count == 0) return string.Empty;
        var lines = new List<string>(entries.Count);
        foreach (var entry in entries)
            lines.Add(entry.IsPair ? $"{entry.Key}={entry.Value}" : entry.Preserved);
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// PII-safe diff of key names between two env texts. Returns
    /// lines in the shape <c>+ NEW_KEY</c> / <c>~ CHANGED_KEY</c> /
    /// <c>- REMOVED_KEY</c> — no values ever surface.
    /// </summary>
    public static IReadOnlyList<string> DiffKeys(string before, string after)
    {
        var beforeMap = Parse(before).Where(e => e.IsPair).ToDictionary(e => e.Key, e => e.Value);
        var afterMap = Parse(after).Where(e => e.IsPair).ToDictionary(e => e.Key, e => e.Value);

        var lines = new List<string>();
        foreach (var kv in afterMap)
        {
            if (!beforeMap.TryGetValue(kv.Key, out var oldVal))
                lines.Add($"+ {kv.Key}");
            else if (!string.Equals(oldVal, kv.Value, StringComparison.Ordinal))
                lines.Add($"~ {kv.Key}");
        }
        foreach (var kv in beforeMap)
        {
            if (!afterMap.ContainsKey(kv.Key))
                lines.Add($"- {kv.Key}");
        }
        return lines;
    }
}

/// <summary>
/// One parsed line from a Cranl env-text blob. Either a
/// <see cref="Key"/>/<see cref="Value"/> pair
/// (<see cref="IsPair"/>=true) or a preserved line (comment / blank).
/// </summary>
public sealed record EnvEntry
{
    public bool IsPair { get; }
    public string Key { get; } = string.Empty;
    public string Value { get; } = string.Empty;
    public string Preserved { get; } = string.Empty;

    private EnvEntry(bool isPair, string key, string value, string preserved)
    {
        IsPair = isPair;
        Key = key;
        Value = value;
        Preserved = preserved;
    }

    public static EnvEntry Pair(string key, string value) => new(true, key, value, string.Empty);
    public static EnvEntry FromLine(string line) => new(false, string.Empty, string.Empty, line);
}
