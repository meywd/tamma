namespace Tamma.Api.Services.Conventions;

/// <summary>
/// Default <see cref="IConventionTemplateService"/> backed by the static
/// <see cref="ConventionTemplates"/> data.
/// </summary>
public sealed class ConventionTemplateService : IConventionTemplateService
{
    /// <inheritdoc />
    public IReadOnlyList<ConventionTemplate> ListAll() => ConventionTemplates.All;

    /// <inheritdoc />
    public ConventionTemplate? GetByKey(string key) => ConventionTemplates.GetByKey(key);
}
