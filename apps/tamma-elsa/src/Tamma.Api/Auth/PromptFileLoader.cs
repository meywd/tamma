using Tamma.Api.Services.Agents;
using Tamma.Core;

namespace Tamma.Api.Auth;

/// <summary>
/// Loads the system-shipped prompt registry from the embedded
/// <c>Prompts/{role}/{action}.md</c> resources (plus one
/// <c>Prompts/{role}/_system.md</c> role identity preamble per role).
///
/// <para>
/// <b>File format.</b> Each file is a minimal front-matter document: the first
/// line is <c>---</c>, followed by simple <c>key: value</c> pairs, a closing
/// <c>---</c> line, and then the template body VERBATIM (everything after the
/// closing delimiter line, no trimming, no newline normalization). Cell files
/// carry <c>variables</c> (comma-separated), <c>enableTools</c>
/// (<c>true</c>/<c>false</c>), <c>maxTokens</c> (int) and <c>version</c> (int);
/// <c>_system.md</c> files carry only <c>version</c>. No YAML library is
/// involved — the parser is deliberately this dumb so the format stays trivial.
/// </para>
///
/// <para>
/// <b>Fail-loud drift invariants</b> (all enforced at static init, mirroring the
/// old <c>BodyBuilderFor</c> exhaustive-switch guarantee):
/// <list type="bullet">
///   <item>a taxonomy cell in <see cref="RolePhaseMap.EligibleActions"/> with no
///         file → <see cref="TammaError"/> <c>PROMPT.SEED.NO_BODY_FAMILY</c>
///         naming the missing cell;</item>
///   <item>a file whose <c>(role, action)</c> is NOT in the taxonomy (or whose
///         role directory is unknown) → <c>PROMPT.SEED.UNKNOWN_CELL</c>;</item>
///   <item>a role with no <c>_system.md</c> → <c>PROMPT.SEED.MISSING_SYSTEM_PROMPT</c>;</item>
///   <item>malformed front matter → <c>PROMPT.SEED.MALFORMED_FILE</c> naming the
///         file path.</item>
/// </list>
/// </para>
/// </summary>
internal static class PromptFileLoader
{
    private const string ResourcePrefix = "Prompts/";
    private const string SystemFileName = "_system";
    private const string Delimiter = "---\n";

    /// <summary>A raw prompt file, path plus full text content.</summary>
    internal readonly record struct PromptFile(string Path, string Content);

    /// <summary>
    /// Load the registry from the embedded resources of the Tamma.Api assembly.
    /// </summary>
    internal static (IReadOnlyDictionary<string, string> RoleSystemPrompts,
                     IReadOnlyList<PromptTemplate> RoleActionTemplates) Load()
        => Build(ReadEmbeddedFiles());

    /// <summary>
    /// Enumerate the embedded <c>Prompts/**/*.md</c> resources. The csproj pins
    /// the logical name to the literal path (<c>Prompts/{role}/{file}.md</c>).
    /// </summary>
    internal static IReadOnlyList<PromptFile> ReadEmbeddedFiles()
    {
        var assembly = typeof(PromptFileLoader).Assembly;
        var files = new List<PromptFile>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // Normalize in case a Windows build stamped backslashes into
            // %(RecursiveDir).
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !normalized.EndsWith(".md", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded resource '{name}' listed but not readable.");
            using var reader = new StreamReader(stream);
            files.Add(new PromptFile(normalized, reader.ReadToEnd()));
        }
        return files;
    }

    /// <summary>
    /// Pure core: parse + validate a set of prompt files into the registry
    /// structures. Split from <see cref="Load"/> so tests can drive it with
    /// synthetic file sets (missing cell / unknown cell / malformed front
    /// matter) without touching the real embedded resources.
    /// </summary>
    internal static (IReadOnlyDictionary<string, string> RoleSystemPrompts,
                     IReadOnlyList<PromptTemplate> RoleActionTemplates) Build(
        IEnumerable<PromptFile> files)
    {
        var systemPrompts = new Dictionary<string, string>(StringComparer.Ordinal);
        var cells = new Dictionary<(string Role, string Action), ParsedCell>();

        // Expected cell set — the authoritative jagged taxonomy, as wire strings.
        var taxonomy = RolePhaseMap.EligibleActions
            .SelectMany(kv => kv.Value.Select(a => (Role: kv.Key.ToWire(), Action: a.ToWire())))
            .ToHashSet();

        foreach (var file in files)
        {
            var (role, name) = SplitPath(file.Path);

            if (name == SystemFileName)
            {
                if (!RolePhaseMap.ValidRoles.Contains(role))
                {
                    throw UnknownCell(file.Path, role, name);
                }
                var (fm, body) = ParseFrontMatter(file.Path, file.Content);
                RequireKeys(file.Path, fm, "version");
                systemPrompts[role] = body;
                continue;
            }

            if (!taxonomy.Contains((role, name)))
            {
                // Drift: a prompt file exists for a (role, action) pair the
                // RolePhaseMap taxonomy does not define.
                throw UnknownCell(file.Path, role, name);
            }

            var (cellFm, cellBody) = ParseFrontMatter(file.Path, file.Content);
            RequireKeys(file.Path, cellFm, "variables", "enableTools", "maxTokens", "version");
            cells[(role, name)] = new ParsedCell(
                Body: cellBody,
                Variables: ParseVariables(cellFm["variables"]),
                EnableTools: ParseBool(file.Path, "enableTools", cellFm["enableTools"]),
                MaxTokens: ParseInt(file.Path, "maxTokens", cellFm["maxTokens"]),
                Version: ParseInt(file.Path, "version", cellFm["version"]));
        }

        // Every role must ship its identity preamble.
        foreach (var role in RolePhaseMap.ValidRoles)
        {
            if (!systemPrompts.ContainsKey(role))
            {
                throw new TammaError(
                    "PROMPT.SEED.MISSING_SYSTEM_PROMPT",
                    $"No embedded Prompts/{role}/_system.md role identity preamble for role '{role}'.",
                    new Dictionary<string, object?> { ["role"] = role },
                    retryable: false,
                    severity: TammaErrorSeverity.Critical);
            }
        }

        // Build the template list by iterating the taxonomy in the SAME order
        // the old BuildRoleActionTemplates() did, so RoleActionTemplates is
        // structurally identical. A taxonomy cell with no file fails loud with
        // the same code the old exhaustive switch used.
        var list = new List<PromptTemplate>(taxonomy.Count);
        foreach (var (role, actions) in RolePhaseMap.EligibleActions)
        {
            var roleWire = role.ToWire();
            var systemPrompt = SystemFor(systemPrompts, roleWire);
            foreach (var action in actions)
            {
                var actionWire = action.ToWire();
                if (!cells.TryGetValue((roleWire, actionWire), out var cell))
                {
                    throw new TammaError(
                        "PROMPT.SEED.NO_BODY_FAMILY",
                        $"No embedded prompt file for taxonomy cell '{roleWire}/{actionWire}'. " +
                        $"Expected Prompts/{roleWire}/{actionWire}.md as an embedded resource.",
                        new Dictionary<string, object?> { ["role"] = roleWire, ["action"] = actionWire },
                        retryable: false,
                        severity: TammaErrorSeverity.Critical);
                }

                list.Add(new PromptTemplate(
                    Role: roleWire,
                    Action: actionWire,
                    Template: cell.Body,
                    SystemPrompt: systemPrompt,
                    Variables: cell.Variables,
                    EnableTools: cell.EnableTools,
                    MaxTokens: cell.MaxTokens,
                    Version: cell.Version));
            }
        }

        return (systemPrompts.AsReadOnly(), list.AsReadOnly());
    }

    // -----------------------------------------------------------------------
    // Parsing helpers
    // -----------------------------------------------------------------------

    private sealed record ParsedCell(
        string Body,
        IReadOnlyList<string> Variables,
        bool EnableTools,
        int MaxTokens,
        int Version);

    /// <summary>Preserves the old <c>SystemFor</c> developer fallback.</summary>
    private static string SystemFor(IReadOnlyDictionary<string, string> systemPrompts, string role)
        => systemPrompts.TryGetValue(role, out var s) ? s : systemPrompts["developer"];

    private static (string Role, string Name) SplitPath(string path)
    {
        // "Prompts/{role}/{name}.md"
        var relative = path[ResourcePrefix.Length..];
        var parts = relative.Split('/');
        if (parts.Length != 2 || !parts[1].EndsWith(".md", StringComparison.Ordinal))
        {
            throw Malformed(path, "expected path shape Prompts/{role}/{action}.md");
        }
        return (parts[0], parts[1][..^3]);
    }

    /// <summary>
    /// Minimal front-matter split: the content must start with a <c>---</c>
    /// line; everything up to the next <c>---</c> line is <c>key: value</c>
    /// pairs; everything AFTER that closing delimiter line is the body,
    /// verbatim (no trimming — byte fidelity is the point).
    /// </summary>
    private static (Dictionary<string, string> FrontMatter, string Body) ParseFrontMatter(
        string path, string content)
    {
        if (!content.StartsWith(Delimiter, StringComparison.Ordinal))
        {
            throw Malformed(path, "missing opening '---' front-matter delimiter on line 1");
        }

        var close = content.IndexOf("\n" + Delimiter, Delimiter.Length - 1, StringComparison.Ordinal);
        if (close < 0)
        {
            throw Malformed(path, "missing closing '---' front-matter delimiter");
        }

        var frontMatterText = close >= Delimiter.Length
            ? content[Delimiter.Length..close]
            : string.Empty;
        var body = content[(close + 1 + Delimiter.Length)..];

        var frontMatter = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in frontMatterText.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw Malformed(path, $"front-matter line is not a 'key: value' pair: '{line}'");
            }
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!frontMatter.TryAdd(key, value))
            {
                throw Malformed(path, $"duplicate front-matter key '{key}'");
            }
        }

        return (frontMatter, body);
    }

    private static void RequireKeys(string path, Dictionary<string, string> frontMatter, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!frontMatter.ContainsKey(key))
            {
                throw Malformed(path, $"missing required front-matter key '{key}'");
            }
        }
        foreach (var present in frontMatter.Keys)
        {
            if (!keys.Contains(present, StringComparer.Ordinal))
            {
                throw Malformed(path, $"unknown front-matter key '{present}'");
            }
        }
    }

    private static IReadOnlyList<string> ParseVariables(string value)
        => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool ParseBool(string path, string key, string value) => value switch
    {
        "true" => true,
        "false" => false,
        _ => throw Malformed(path, $"front-matter key '{key}' must be 'true' or 'false', got '{value}'"),
    };

    private static int ParseInt(string path, string key, string value)
        => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw Malformed(path, $"front-matter key '{key}' must be a non-negative integer, got '{value}'");

    private static TammaError Malformed(string path, string detail) => new(
        "PROMPT.SEED.MALFORMED_FILE",
        $"Malformed prompt file '{path}': {detail}.",
        new Dictionary<string, object?> { ["path"] = path, ["detail"] = detail },
        retryable: false,
        severity: TammaErrorSeverity.Critical);

    private static TammaError UnknownCell(string path, string role, string action) => new(
        "PROMPT.SEED.UNKNOWN_CELL",
        $"Prompt file '{path}' targets ({role}, {action}), which is not a cell in the " +
        "RolePhaseMap.EligibleActions taxonomy. Remove the file or add the cell to the taxonomy.",
        new Dictionary<string, object?> { ["path"] = path, ["role"] = role, ["action"] = action },
        retryable: false,
        severity: TammaErrorSeverity.Critical);
}
