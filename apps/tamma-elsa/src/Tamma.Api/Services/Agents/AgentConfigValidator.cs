using System.Text.Json;
using System.Text.RegularExpressions;
using Tamma.Api.Services.Security;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-1 — shared saved-config validator for agent definitions. Extracted
/// from the private <c>AgentEndpoints.ValidateConfigShape</c> (so the rules are
/// shared, not duplicated) and extended for the Epic 32 saved-config fields the
/// design names (<c>provider</c>, <c>model</c>, <c>temperature</c>,
/// <c>maxTokens</c>, <c>tokenBudget</c>, <c>tools[]</c>, <c>systemPromptRef</c>,
/// <c>rag{}</c>).
///
/// <para>The battle-tested legacy guards are kept verbatim (Finding 014):
/// provider name regex <c>^[a-z0-9][a-z0-9_-]{0,63}$</c>, <c>maxBudgetUsd</c>
/// range [0,100], non-empty provider chains, prototype-pollution rejection on
/// role/chain keys, ReDoS guard on <c>blockedCommandPatterns</c>, and
/// <c>maxFetchSizeBytes</c> range [0, 1 GiB]. Tolerant of an empty config
/// (valid — falls through to defaults).</para>
///
/// <para>Configs are credential-agnostic by design (no raw keys), so error
/// messages reference field names only, never values that could carry secrets.</para>
/// </summary>
public static class AgentConfigValidator
{
    /// <summary>Provider name regex (Story 9-1 AC 6 / TS validateAgentsConfig).</summary>
    private static readonly Regex ProviderNameRegex =
        new("^[a-z0-9][a-z0-9_-]{0,63}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Validate the shape + semantics of a proposed saved config. Returns
    /// <c>(valid, errors)</c>. Empty config is valid.
    /// </summary>
    public static (bool Valid, string[] Errors) Validate(string configJson)
    {
        var errors = new List<string>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(configJson);
        }
        catch (JsonException ex)
        {
            return (false, new[] { $"Invalid JSON: {ex.Message}" });
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Root must be a JSON object.");
                return (false, errors.ToArray());
            }

            // ── Epic 32 saved-config top-level fields ─────────────────────────
            ValidateSavedConfigFields(root, errors);

            // ── Roles (legacy 2D shape) ──────────────────────────────────────
            if (root.TryGetProperty("roles", out var roles))
            {
                if (roles.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("'roles' must be an object.");
                    return (false, errors.ToArray());
                }

                foreach (var prop in roles.EnumerateObject())
                {
                    if (RolePhaseMap.ForbiddenKeys.Contains(prop.Name))
                    {
                        errors.Add($"Forbidden role key: '{prop.Name}'.");
                        continue;
                    }
                    var roleKnown = RolePhaseMap.ValidRoles.Contains(prop.Name) ||
                                    RolePhaseMap.LegacyRoleAliases.ContainsKey(prop.Name);
                    if (!roleKnown)
                    {
                        errors.Add(
                            $"Unknown role '{prop.Name}'. Valid: " +
                            string.Join(", ", RolePhaseMap.ValidRoles) + ".");
                        continue;
                    }
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                    ValidateRoleSemantics(prop.Name, prop.Value, errors);
                }
            }

            // ── defaults.providerChain (legacy TS shape) ─────────────────────
            if (root.TryGetProperty("defaults", out var defaults) &&
                defaults.ValueKind == JsonValueKind.Object &&
                defaults.TryGetProperty("providerChain", out var defChain))
            {
                ValidateProviderChain("defaults.providerChain", defChain, errors);
            }

            // ── chains (canonical 2D shape) ──────────────────────────────────
            if (root.TryGetProperty("chains", out var chains) &&
                chains.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in chains.EnumerateObject())
                {
                    if (RolePhaseMap.ForbiddenKeys.Contains(prop.Name))
                    {
                        errors.Add($"Forbidden chain key: '{prop.Name}'.");
                        continue;
                    }
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        ValidateProviderChain($"chains.{prop.Name}", prop.Value, errors);
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var actionProp in prop.Value.EnumerateObject())
                        {
                            if (actionProp.Value.ValueKind != JsonValueKind.Array) continue;
                            ValidateProviderChain(
                                $"chains.{prop.Name}.{actionProp.Name}",
                                actionProp.Value, errors);
                        }
                    }
                }
            }

            // ── security branch (blockedCommandPatterns + maxFetchSizeBytes) ──
            if (root.TryGetProperty("security", out var security) &&
                security.ValueKind == JsonValueKind.Object)
            {
                ValidateSecurity(security, errors);
            }
        }

        return (errors.Count == 0, errors.ToArray());
    }

    /// <summary>
    /// Story 32-17 — visibility-aware validation. Runs the base shape rules
    /// (the visibility-agnostic <see cref="Validate(string)"/> overload) and
    /// then layers the <c>prompts</c>-block invariants (AC2/AC3):
    /// <list type="bullet">
    ///   <item><see cref="AgentVisibility.Public"/> + a NON-EMPTY <c>prompts</c>
    ///     block → <c>PROMPTS_NOT_ALLOWED_ON_PUBLIC</c> (rule 4 — personas are
    ///     prompt-free).</item>
    ///   <item>any NON-EMPTY <c>prompts</c> block (public or private): each
    ///     <c>byRoleAction</c> key must parse as a valid <c>"&lt;role&gt;:&lt;action&gt;"</c>
    ///     taxonomy cell (else <c>PROMPTS_INVALID_KEY</c>), each template value
    ///     must be non-empty after trim (else <c>PROMPTS_EMPTY_TEMPLATE</c>), and
    ///     prototype-pollution keys are rejected (<c>PROMPTS_PROTO_POLLUTION</c>,
    ///     reusing the 32-1 <see cref="RolePhaseMap.ForbiddenKeys"/> guard).</item>
    /// </list>
    /// A <c>prompts</c> object that parses but is wholly empty is treated as
    /// absent (allowed for both visibilities). The same overload backs BOTH the
    /// create and publish-version write paths so the invariant holds on both.
    /// </summary>
    public static (bool Valid, string[] Errors) Validate(string configJson, AgentVisibility visibility)
    {
        var (baseValid, baseErrors) = Validate(configJson);
        var errors = new List<string>(baseErrors);

        // The prompts block is parsed best-effort; only a present-and-non-empty
        // block triggers the new rules. (Malformed JSON already failed above.)
        var prompts = AgentPromptSet.TryRead(configJson);
        if (prompts is { IsEmpty: false })
        {
            ValidatePromptSet(prompts, visibility, errors);
        }

        return (errors.Count == 0, errors.ToArray());
    }

    /// <summary>
    /// Story 32-17 — the <c>prompts</c>-block content rules (AC2/AC3). Called
    /// only for a present-and-non-empty <see cref="AgentPromptSet"/>.
    /// </summary>
    private static void ValidatePromptSet(
        AgentPromptSet prompts, AgentVisibility visibility, List<string> errors)
    {
        // AC2 — public personas are prompt-free (rule 4).
        if (visibility == AgentVisibility.Public)
        {
            errors.Add(
                "PROMPTS_NOT_ALLOWED_ON_PUBLIC: public personas are prompt-free (Epic 32 rule 4); "
                + "custom prompts require a private agent.");
        }

        // AC3 — content rules on byRoleAction (key taxonomy + non-empty template
        // + prototype-pollution guard). The 'system' fallback is a free-form
        // string; it is already known non-empty (IsEmpty was false) when present.
        if (prompts.ByRoleAction is null)
        {
            return;
        }

        foreach (var (key, template) in prompts.ByRoleAction)
        {
            // Prototype-pollution keys first (reuse the 32-1 guard).
            if (RolePhaseMap.ForbiddenKeys.Contains(key))
            {
                errors.Add($"PROMPTS_PROTO_POLLUTION: forbidden byRoleAction key '{key}'.");
                continue;
            }

            // Key must parse as a valid "<role>:<action>" taxonomy cell.
            if (!IsValidRoleActionKey(key))
            {
                errors.Add(
                    $"PROMPTS_INVALID_KEY: byRoleAction key '{key}' must be a valid "
                    + "\"<role>:<action>\" taxonomy cell.");
            }

            // Template value must be non-empty after trim (no-empty-fallback).
            if (string.IsNullOrWhiteSpace(template))
            {
                errors.Add(
                    $"PROMPTS_EMPTY_TEMPLATE: byRoleAction['{key}'] template must be a "
                    + "non-empty string.");
            }
        }
    }

    /// <summary>
    /// True when <paramref name="key"/> is <c>"&lt;role&gt;:&lt;action&gt;"</c>
    /// with both tokens valid per the Epic 27 taxonomy AND the pair an eligible
    /// cell (e.g. <c>developer:deploy</c> — known tokens, no cell — is rejected).
    /// Mirrors <c>RoleActionParsing.TryParsePair</c> without the HTTP boundary.
    /// </summary>
    private static bool IsValidRoleActionKey(string key)
    {
        var sep = key.IndexOf(':');
        if (sep <= 0 || sep >= key.Length - 1)
        {
            return false;
        }

        var roleToken = key[..sep];
        var actionToken = key[(sep + 1)..];

        string roleWire;
        string actionWire;
        try
        {
            roleWire = AgentRoleExtensions.Parse(roleToken).ToWire();
            actionWire = AgentActionExtensions.Parse(actionToken).ToWire();
        }
        catch (ArgumentException)
        {
            return false;
        }

        return RolePhaseMap.IsRoleEligibleForPhase(actionWire, roleWire);
    }

    /// <summary>
    /// Validate the Epic 32 saved-config top-level fields. Each is optional;
    /// only present-and-wrong-typed/out-of-range values are rejected.
    /// </summary>
    private static void ValidateSavedConfigFields(JsonElement root, List<string> errors)
    {
        // provider (top-level) — same regex as the legacy role.provider.
        if (root.TryGetProperty("provider", out var provider))
        {
            if (provider.ValueKind != JsonValueKind.String)
            {
                errors.Add("provider must be a string.");
            }
            else
            {
                var name = provider.GetString() ?? string.Empty;
                if (!ProviderNameRegex.IsMatch(name))
                {
                    errors.Add(
                        $"provider '{name}' must match /^[a-z0-9][a-z0-9_-]{{0,63}}$/.");
                }
            }
        }

        // model — non-empty string.
        if (root.TryGetProperty("model", out var model) &&
            model.ValueKind != JsonValueKind.String)
        {
            errors.Add("model must be a string.");
        }

        // temperature ∈ [0, 2], finite.
        if (root.TryGetProperty("temperature", out var temp))
        {
            if (temp.ValueKind != JsonValueKind.Number ||
                !temp.TryGetDouble(out var t) || double.IsNaN(t) || double.IsInfinity(t))
            {
                errors.Add("temperature must be a finite number.");
            }
            else if (t < 0 || t > 2)
            {
                errors.Add($"temperature must be in [0, 2] (got {t}).");
            }
        }

        // maxTokens > 0.
        if (root.TryGetProperty("maxTokens", out var maxTokens))
        {
            if (maxTokens.ValueKind != JsonValueKind.Number ||
                !maxTokens.TryGetInt64(out var mt))
            {
                errors.Add("maxTokens must be an integer.");
            }
            else if (mt <= 0)
            {
                errors.Add($"maxTokens must be > 0 (got {mt}).");
            }
        }

        // tokenBudget >= 0.
        if (root.TryGetProperty("tokenBudget", out var tokenBudget))
        {
            if (tokenBudget.ValueKind != JsonValueKind.Number ||
                !tokenBudget.TryGetInt64(out var tb))
            {
                errors.Add("tokenBudget must be an integer.");
            }
            else if (tb < 0)
            {
                errors.Add($"tokenBudget must be >= 0 (got {tb}).");
            }
        }

        // tools[] — array of strings.
        if (root.TryGetProperty("tools", out var tools))
        {
            if (tools.ValueKind != JsonValueKind.Array)
            {
                errors.Add("tools must be an array of strings.");
            }
            else
            {
                var i = 0;
                foreach (var entry in tools.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                    {
                        errors.Add($"tools[{i}] must be a string.");
                    }
                    i++;
                }
            }
        }

        // systemPromptRef — string.
        if (root.TryGetProperty("systemPromptRef", out var promptRef) &&
            promptRef.ValueKind != JsonValueKind.String)
        {
            errors.Add("systemPromptRef must be a string.");
        }

        // rag{} — object.
        if (root.TryGetProperty("rag", out var rag) &&
            rag.ValueKind != JsonValueKind.Object)
        {
            errors.Add("rag must be an object.");
        }
    }

    private static void ValidateRoleSemantics(string role, JsonElement obj, List<string> errors)
    {
        // provider name regex
        if (obj.TryGetProperty("provider", out var prov) &&
            prov.ValueKind == JsonValueKind.String)
        {
            var name = prov.GetString() ?? string.Empty;
            if (!ProviderNameRegex.IsMatch(name))
            {
                errors.Add(
                    $"roles.{role}.provider '{name}' must match /^[a-z0-9][a-z0-9_-]{{0,63}}$/.");
            }
        }

        // maxBudgetUsd range [0, 100], finite
        if (obj.TryGetProperty("maxBudgetUsd", out var budget) &&
            budget.ValueKind == JsonValueKind.Number)
        {
            if (!budget.TryGetDouble(out var budgetVal) || double.IsNaN(budgetVal) ||
                double.IsInfinity(budgetVal))
            {
                errors.Add($"roles.{role}.maxBudgetUsd must be a finite number.");
            }
            else if (budgetVal < 0 || budgetVal > 100)
            {
                errors.Add($"roles.{role}.maxBudgetUsd must be in [0, 100] (got {budgetVal}).");
            }
        }

        // permissionMode whitelist
        if (obj.TryGetProperty("permissionMode", out var mode) &&
            mode.ValueKind == JsonValueKind.String)
        {
            var modeVal = mode.GetString();
            if (modeVal is not ("default" or "acceptEdits" or "bypassPermissions"))
            {
                errors.Add(
                    $"roles.{role}.permissionMode must be one of " +
                    "default | acceptEdits | bypassPermissions.");
            }
        }

        // providerChain shape
        if (obj.TryGetProperty("providerChain", out var chain) &&
            chain.ValueKind == JsonValueKind.Array)
        {
            ValidateProviderChain($"roles.{role}.providerChain", chain, errors);
        }
    }

    private static void ValidateProviderChain(string label, JsonElement arr, List<string> errors)
    {
        if (arr.GetArrayLength() == 0)
        {
            errors.Add($"{label}: chain must not be empty.");
            return;
        }
        var i = 0;
        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{label}[{i}]: entry must be an object.");
                i++;
                continue;
            }
            if (!entry.TryGetProperty("provider", out var prov) ||
                prov.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{label}[{i}]: missing 'provider' string field.");
                i++;
                continue;
            }
            var name = prov.GetString() ?? string.Empty;
            if (!ProviderNameRegex.IsMatch(name))
            {
                errors.Add(
                    $"{label}[{i}].provider '{name}' must match " +
                    "/^[a-z0-9][a-z0-9_-]{0,63}$/.");
            }
            i++;
        }
    }

    private static void ValidateSecurity(JsonElement sec, List<string> errors)
    {
        if (sec.TryGetProperty("maxFetchSizeBytes", out var fetch))
        {
            if (fetch.ValueKind != JsonValueKind.Number ||
                !fetch.TryGetInt64(out var bytes))
            {
                errors.Add("security.maxFetchSizeBytes must be a number.");
            }
            else if (bytes < 0 || bytes > 1L * 1024 * 1024 * 1024)
            {
                errors.Add(
                    $"security.maxFetchSizeBytes must be in [0, 1 GiB] (got {bytes}).");
            }
        }

        if (sec.TryGetProperty("blockedCommandPatterns", out var patterns) &&
            patterns.ValueKind == JsonValueKind.Array)
        {
            if (patterns.GetArrayLength() > ReDosGuard.MaxPatternCount)
            {
                errors.Add(
                    $"security.blockedCommandPatterns count {patterns.GetArrayLength()} " +
                    $"exceeds max {ReDosGuard.MaxPatternCount}.");
            }
            var i = 0;
            foreach (var entry in patterns.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"security.blockedCommandPatterns[{i}]: must be a string.");
                    i++;
                    continue;
                }
                try
                {
                    ReDosGuard.Validate(
                        $"security.blockedCommandPatterns[{i}]",
                        entry.GetString() ?? string.Empty);
                }
                catch (ArgumentException ex)
                {
                    errors.Add(ex.Message);
                }
                i++;
            }
        }
    }
}
