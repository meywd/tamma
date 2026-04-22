using System.Collections.Frozen;

namespace Tamma.Api.Services.Secrets;

/// <summary>
/// Static lookup that turns a <see cref="ConsumerRef"/> into a
/// human-readable label and (optionally) a UI deep-link template, so
/// the admin UIs can render "Used by: Tamma API
/// (TammaAppDbContext)" rather than raw
/// <c>postgres / role=tamma_app</c> strings (Story 29-1 AC8).
///
/// <para>The table is intentionally code-shipped — the system keys are
/// platform-defined and stable; tenants don't add new system kinds.
/// Story 29-4 / 29-5 (admin UIs) consume this lookup to produce a
/// "consumers" pill row on the secret detail screen.</para>
///
/// <para>The lookup is a pure function — no DI, no allocations on the
/// hot path; built once into a <see cref="FrozenDictionary{TKey,TValue}"/>
/// for O(1) access from per-request rendering.</para>
/// </summary>
public static class ConsumerRefLookup
{
    /// <summary>
    /// Definition for a single system kind: how to render its label
    /// and (optionally) build a deep-link URL into its admin UI.
    /// </summary>
    /// <param name="SystemKey">Canonical lower-kebab-case key — must match
    /// the <see cref="ConsumerRef.System"/> field.</param>
    /// <param name="DisplayName">Short human label, e.g. "Tamma API",
    /// "Cranl App", "GitHub Webhook".</param>
    /// <param name="LinkTemplate">Optional sprintf-style template
    /// (placeholders <c>{identifier}</c>) for building a deep-link.
    /// Null means the consumer renders without a link.</param>
    public sealed record Definition(
        string SystemKey,
        string DisplayName,
        string? LinkTemplate);

    /// <summary>
    /// Rendered consumer reference suitable for direct binding to the
    /// admin UI. The <paramref name="Identifier"/> remains visible so
    /// operators can distinguish two consumers of the same kind.
    /// </summary>
    /// <param name="DisplayName">Human label, e.g. "Tamma API".</param>
    /// <param name="Identifier">Raw identifier verbatim from the
    /// <see cref="ConsumerRef"/>.</param>
    /// <param name="Url">Deep link into the consumer's admin UI, or
    /// null when no template was registered for the system.</param>
    public sealed record Rendered(
        string DisplayName,
        string Identifier,
        string? Url);

    /// <summary>
    /// Canonical system keys. Use the constants instead of string
    /// literals at call sites so the typo blast radius is a compiler
    /// error instead of a silent "unknown_system" pill.
    /// </summary>
    public static class Systems
    {
        public const string Postgres = "postgres";
        public const string TammaApi = "tamma-api";
        public const string CranlApp = "cranl";
        public const string GitHubWebhook = "github-webhook";
        public const string GitLabWebhook = "gitlab-webhook";
        public const string ElsaWorkflow = "elsa-workflow";
        public const string SmtpRelay = "smtp-relay";
        public const string OpenAiApi = "openai-api";
        public const string AnthropicApi = "anthropic-api";
    }

    private static readonly FrozenDictionary<string, Definition> Defs =
        new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase)
        {
            [Systems.Postgres] = new(
                Systems.Postgres,
                "Postgres",
                LinkTemplate: null),
            [Systems.TammaApi] = new(
                Systems.TammaApi,
                "Tamma API",
                LinkTemplate: null),
            [Systems.CranlApp] = new(
                Systems.CranlApp,
                "Cranl App",
                LinkTemplate: "https://cranl.io/apps/{identifier}"),
            [Systems.GitHubWebhook] = new(
                Systems.GitHubWebhook,
                "GitHub Webhook",
                LinkTemplate: "https://github.com/{identifier}/settings/hooks"),
            [Systems.GitLabWebhook] = new(
                Systems.GitLabWebhook,
                "GitLab Webhook",
                LinkTemplate: "https://gitlab.com/{identifier}/-/hooks"),
            [Systems.ElsaWorkflow] = new(
                Systems.ElsaWorkflow,
                "Elsa Workflow",
                LinkTemplate: null),
            [Systems.SmtpRelay] = new(
                Systems.SmtpRelay,
                "SMTP Relay",
                LinkTemplate: null),
            [Systems.OpenAiApi] = new(
                Systems.OpenAiApi,
                "OpenAI API",
                LinkTemplate: null),
            [Systems.AnthropicApi] = new(
                Systems.AnthropicApi,
                "Anthropic API",
                LinkTemplate: null),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// All registered system keys — for admin tooling that renders
    /// "available consumer kinds" pickers.
    /// </summary>
    public static IReadOnlyCollection<string> KnownSystems => Defs.Keys;

    /// <summary>
    /// Try to fetch the definition for <paramref name="systemKey"/>.
    /// Returns <c>null</c> when the key is not registered — callers
    /// should fall back to rendering the raw identifier.
    /// </summary>
    public static Definition? TryGetDefinition(string systemKey) =>
        Defs.TryGetValue(systemKey, out var def) ? def : null;

    /// <summary>
    /// Render a single <see cref="ConsumerRef"/> for display. Unknown
    /// system keys still render — they get the system key as the
    /// label and no URL.
    /// </summary>
    public static Rendered Render(ConsumerRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var def = TryGetDefinition(reference.System);

        if (def is null)
        {
            return new Rendered(
                DisplayName: reference.System,
                Identifier: reference.Identifier,
                Url: null);
        }

        var url = def.LinkTemplate is null
            ? null
            : def.LinkTemplate.Replace(
                "{identifier}",
                reference.Identifier,
                StringComparison.Ordinal);

        return new Rendered(def.DisplayName, reference.Identifier, url);
    }

    /// <summary>Render a list of consumers in one call.</summary>
    public static IReadOnlyList<Rendered> RenderAll(
        IEnumerable<ConsumerRef> consumers)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        return consumers.Select(Render).ToList();
    }
}
