using System.Collections.Frozen;
using System.Reflection;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Marks an enum member with its canonical "wire" string — the stable token
/// persisted to the database and sent in workflow payloads. Decouples the C#
/// identifier (freely renameable) from the persisted contract.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class WireAttribute(string wire) : Attribute
{
    public string Wire => wire;
}

/// <summary>
/// Bidirectional map between <typeparamref name="TEnum"/> members and their
/// <see cref="WireAttribute"/> strings, built and validated once in the static
/// constructor: every member must carry exactly one <c>[Wire]</c>, and all wire
/// strings must be distinct. Parsing is case-sensitive (ordinal) so
/// non-canonical casing in persisted data is rejected, not silently accepted.
/// </summary>
/// <remarks>Supports simple enums only (not <c>[Flags]</c> / duplicate values).</remarks>
public static class EnumWire<TEnum> where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<TEnum, string> s_toWire;
    private static readonly FrozenDictionary<string, TEnum> s_fromWire;

    static EnumWire()
    {
        var toWire = new Dictionary<TEnum, string>();
        var fromWire = new Dictionary<string, TEnum>(StringComparer.Ordinal);
        foreach (var value in Enum.GetValues<TEnum>())
        {
            var field = typeof(TEnum).GetField(value.ToString())
                ?? throw new InvalidOperationException(
                    $"{typeof(TEnum).Name}.{value} has no backing field ([Flags] / duplicate values are not supported).");
            var wire = field.GetCustomAttribute<WireAttribute>()?.Wire
                ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{value} is missing [Wire].");
            if (!fromWire.TryAdd(wire, value))
                throw new InvalidOperationException(
                    $"Duplicate wire string '{wire}' on {typeof(TEnum).Name}.{value} and {typeof(TEnum).Name}.{fromWire[wire]}.");
            toWire[value] = wire;
        }
        s_toWire = toWire.ToFrozenDictionary();
        s_fromWire = fromWire.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The canonical wire string for <paramref name="value"/>.</summary>
    public static string ToWire(TEnum value) => s_toWire[value];

    /// <summary>Case-sensitive lookup of the enum member for <paramref name="wire"/>.</summary>
    public static bool TryParse(string wire, out TEnum value) => s_fromWire.TryGetValue(wire, out value);
}
