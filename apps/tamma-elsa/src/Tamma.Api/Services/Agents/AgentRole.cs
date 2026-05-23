namespace Tamma.Api.Services.Agents;

public enum AgentRole
{
    [Wire("developer")]        Developer,
    [Wire("tester")]           Tester,
    [Wire("security")]         Security,
    [Wire("devops")]           Devops,
    [Wire("architect")]        Architect,
    [Wire("product_owner")]    ProductOwner,
    [Wire("senior_developer")] SeniorDeveloper,
    [Wire("tech_writer")]      TechWriter,
}

public static class AgentRoleExtensions
{
    /// <summary>The canonical wire string for <paramref name="role"/>.</summary>
    public static string ToWire(this AgentRole role) => EnumWire<AgentRole>.ToWire(role);

    /// <summary>
    /// Resolves a wire string (or legacy alias) to an <see cref="AgentRole"/>.
    /// Applies <see cref="RolePhaseMap.NormalizeRole"/> first, then exact match.
    /// </summary>
    /// <exception cref="ArgumentException">Null, empty, or unknown role.</exception>
    public static AgentRole Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Role must not be null or empty.", nameof(input));

        var normalized = RolePhaseMap.NormalizeRole(input);
        if (EnumWire<AgentRole>.TryParse(normalized, out var role)) return role;

        throw new ArgumentException(
            $"Unknown role: '{input}'. Valid roles: {string.Join(", ", Enum.GetValues<AgentRole>().Select(r => r.ToWire()))}.",
            nameof(input));
    }
}
