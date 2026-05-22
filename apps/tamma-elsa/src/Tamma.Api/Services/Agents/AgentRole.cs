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
    public static string ToWire(this AgentRole role) => EnumWire<AgentRole>.ToWire(role);

    public static AgentRole Parse(string input)
    {
        var normalized = RolePhaseMap.NormalizeRole(input);
        if (EnumWire<AgentRole>.TryParse(normalized, out var role)) return role;
        throw new ArgumentException(
            $"Unknown role: '{input}'. Valid roles: {string.Join(", ", Enum.GetValues<AgentRole>().Select(r => r.ToWire()))}.",
            nameof(input));
    }
}
