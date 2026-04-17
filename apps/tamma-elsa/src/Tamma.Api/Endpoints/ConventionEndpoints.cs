using Tamma.Api.Services.Conventions;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Public, unauthenticated endpoints for the convention starter templates.
///
/// <list type="bullet">
///   <item><description><c>GET /api/convention-templates</c> — metadata-only list.</description></item>
///   <item><description><c>GET /api/convention-templates/{key}</c> — full template with conventions body.</description></item>
/// </list>
/// </summary>
public static class ConventionEndpoints
{
    /// <summary>
    /// Returns every shipped template as <c>{ key, name, description }</c> —
    /// excluding the (potentially large) conventions body.
    /// </summary>
    public static IResult ListAll()
    {
        var items = ConventionTemplates.All
            .Select(t => new ConventionTemplateSummary(t.Key, t.Name, t.Description))
            .ToList();
        return Results.Ok(items);
    }

    /// <summary>
    /// Returns a single template — including the full conventions body —
    /// or <c>404</c> if the key is unknown.
    /// </summary>
    public static IResult GetByKey(string key)
    {
        var template = ConventionTemplates.GetByKey(key);
        return template is null
            ? Results.NotFound(new { error = $"Convention template \"{key}\" not found" })
            : Results.Ok(template);
    }

    /// <summary>Metadata-only projection returned by <see cref="ListAll"/>.</summary>
    private sealed record ConventionTemplateSummary(string Key, string Name, string Description);
}
