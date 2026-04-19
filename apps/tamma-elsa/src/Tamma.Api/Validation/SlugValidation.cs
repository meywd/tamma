using System.Text.RegularExpressions;

namespace Tamma.Api.Validation;

/// <summary>
/// Tenant slug validation constants + helpers ported from the deleted TS
/// <c>packages/api/src/routes/orgs/index.ts</c> (finding 007 remediation).
///
/// <para>Rules:</para>
/// <list type="bullet">
///   <item>Lowercase alphanumeric plus hyphen.</item>
///   <item>Length 3-40 characters.</item>
///   <item>Cannot start or end with a hyphen.</item>
///   <item>Cannot be one of the platform-reserved labels (<see cref="Reserved"/>).</item>
/// </list>
/// </summary>
public static class SlugValidation
{
    /// <summary>
    /// Reserved slugs that conflict with dashboard / API / marketing routes.
    /// Ported verbatim from the TS RESERVED_SLUGS set.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "admin", "api", "auth", "settings", "app", "www",
            "dashboard", "login", "register", "signup", "signin",
            "default", "help", "support", "docs", "blog",
        };

    /// <summary>
    /// Lowercase alphanumeric + hyphen, 3-40 chars, no leading/trailing hyphen.
    /// Mirrors TS <c>/^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/</c>.
    /// </summary>
    public static readonly Regex SlugRegex = new(
        "^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static bool IsValidSlug(string? slug)
        => !string.IsNullOrEmpty(slug) && SlugRegex.IsMatch(slug);

    public static bool IsReservedSlug(string? slug)
        => slug is not null && Reserved.Contains(slug);

    public static bool IsValidName(string? name)
    {
        if (name is null) return false;
        var trimmed = name.Trim();
        return trimmed.Length is >= 2 and <= 100;
    }
}
