using System.Reflection;

namespace Tamma.Api.Services.Agents;

[AttributeUsage(AttributeTargets.Field)]
public sealed class WireAttribute(string wire) : Attribute
{
    public string Wire => wire;
}

public static class EnumWire<TEnum> where TEnum : struct, Enum
{
    private static readonly IReadOnlyDictionary<TEnum, string> s_toWire;
    private static readonly IReadOnlyDictionary<string, TEnum> s_fromWire;

    static EnumWire()
    {
        var map = Enum.GetValues<TEnum>().ToDictionary(v => v, v =>
            typeof(TEnum).GetField(v.ToString())!.GetCustomAttribute<WireAttribute>()?.Wire
            ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{v} is missing [Wire]"));
        s_toWire = map;
        s_fromWire = map.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
    }

    public static string ToWire(TEnum value) => s_toWire[value];
    public static bool TryParse(string wire, out TEnum value) => s_fromWire.TryGetValue(wire, out value);
}
