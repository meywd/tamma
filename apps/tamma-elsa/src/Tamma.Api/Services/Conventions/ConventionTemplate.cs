namespace Tamma.Api.Services.Conventions;

/// <summary>
/// A language/framework convention starter template.
///
/// Templates are read-only reference data shipped with Tamma. Users select
/// a template, optionally customise it, and save the <see cref="Conventions"/>
/// body under the <c>conventions</c> field of their repo's
/// <c>.tamma/config.json</c>. The body is then injected into every LLM
/// prompt via the <c>{{conventions}}</c> template variable.
/// </summary>
/// <param name="Key">Stable template identifier (e.g. <c>typescript-react</c>).</param>
/// <param name="Name">Human-readable template name.</param>
/// <param name="Description">Short one-line summary for pickers / UI.</param>
/// <param name="Conventions">The full multiline convention body (Markdown).</param>
public sealed record ConventionTemplate(
    string Key,
    string Name,
    string Description,
    string Conventions);
