namespace Tamma.Api.Services.Conventions;

/// <summary>
/// Read-only access to the shipped convention starter templates.
/// </summary>
public interface IConventionTemplateService
{
    /// <summary>
    /// Lists every convention template with its full metadata and body.
    /// </summary>
    /// <remarks>
    /// Callers that only need the list-view metadata should project the
    /// result via <see cref="ConventionTemplate.Key"/>,
    /// <see cref="ConventionTemplate.Name"/> and
    /// <see cref="ConventionTemplate.Description"/>.
    /// </remarks>
    IReadOnlyList<ConventionTemplate> ListAll();

    /// <summary>
    /// Looks up a template by key, returning <c>null</c> if none exists.
    /// </summary>
    /// <param name="key">Stable template identifier (e.g. <c>typescript-react</c>).</param>
    ConventionTemplate? GetByKey(string key);
}
