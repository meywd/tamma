namespace Tamma.Api.Services.Secrets.Stopgap;

/// <summary>
/// Static mapping of every "stopgap" secret currently sourced from
/// <see cref="IConfiguration"/> / env / DB columns to the canonical
/// cabinet reference it should live under once Story 29-9's
/// <c>migrate-secrets</c> command has run.
///
/// <para>Story 29-9 iterates this list and imports each entry into the
/// cabinet; Story 29-10 removes every direct config read listed under
/// <see cref="StopgapSecretDescriptor.PreviousLocation"/>, routing
/// runtime reads through <see cref="IRuntimeSecretResolver"/>.</para>
///
/// <para>All entries are <b>platform-scoped</b> in the current port —
/// per-tenant Cranl DATABASE_URL secrets are handled out-of-band by the
/// provisioning flow. Since Epic 30 Phase B (Task B3) the encrypted admin
/// credential lives on the tenant's <c>tenant_databases</c> pool row
/// (<c>AdminConnectionStringEncrypted</c>), not a dedicated tenant column.</para>
/// </summary>
public static class StopgapSecretMap
{
    /// <summary>
    /// Canonical cabinet names per Story 29-9 AC1. Kept as constants so
    /// runtime consumers in <see cref="IRuntimeSecretResolver"/> and the
    /// migration service cannot drift.
    /// </summary>
    public const string PlatformAnthropicApiKey = "anthropic/api-key";
    public const string PlatformGitHubToken = "github/pat";
    public const string PlatformGitHubWebhookSecret = "github/app-webhook-hmac";
    public const string PlatformElsaApiKey = "elsa/api-key";
    public const string PlatformJiraApiToken = "jira/api-token";
    public const string PlatformCranlApiKey = "cranl/api-key";
    public const string PlatformTenantSharedSecret = "hmac/shared-engine";

    /// <summary>
    /// Story 37-2 — HMAC key that signs audit hash-chain checkpoints. Sourced
    /// from the cabinet (never a plaintext env key at rest); the config/env
    /// fallback here exists only for the Story 29-9 coexistence window, exactly
    /// like every other platform HMAC secret.
    /// </summary>
    public const string PlatformAuditChainSigningKey = "audit/chain-signing-key";

    /// <summary>
    /// Table of every platform-scoped stopgap. The migration service
    /// walks this list in order, skipping entries already present in
    /// the cabinet, and emits one
    /// <see cref="SecretAuditEventTypes.MigratedSuccess"/> event per
    /// imported row.
    /// </summary>
    public static readonly IReadOnlyList<StopgapSecretDescriptor> Platform =
        new[]
        {
            new StopgapSecretDescriptor(
                CabinetName: PlatformAnthropicApiKey,
                ConfigKeys: new[] { "Anthropic:ApiKey" },
                EnvVars: new[] { "ANTHROPIC_API_KEY" },
                Purpose: SecretPurpose.ApiKey,
                Consumer: new ConsumerRef("anthropic", "x-api-key"),
                RotationDays: null),
            new StopgapSecretDescriptor(
                CabinetName: PlatformGitHubToken,
                ConfigKeys: new[] { "GitHub:Token" },
                EnvVars: new[] { "GITHUB_TOKEN" },
                Purpose: SecretPurpose.ApiKey,
                Consumer: new ConsumerRef("github", "pat"),
                RotationDays: 90),
            new StopgapSecretDescriptor(
                CabinetName: PlatformGitHubWebhookSecret,
                ConfigKeys: new[] { "GitHub:WebhookSecret" },
                EnvVars: new[] { "GITHUB_WEBHOOK_SECRET" },
                Purpose: SecretPurpose.HmacSharedSecret,
                Consumer: new ConsumerRef("github_webhook", "app-level"),
                RotationDays: null),
            new StopgapSecretDescriptor(
                CabinetName: PlatformElsaApiKey,
                ConfigKeys: new[] { "Elsa:ApiKey" },
                EnvVars: new[] { "ELSA_API_KEY" },
                Purpose: SecretPurpose.ApiKey,
                Consumer: new ConsumerRef("elsa", "server"),
                RotationDays: 90),
            new StopgapSecretDescriptor(
                CabinetName: PlatformJiraApiToken,
                ConfigKeys: new[] { "Jira:ApiToken" },
                EnvVars: new[] { "JIRA_API_TOKEN" },
                Purpose: SecretPurpose.ApiKey,
                Consumer: new ConsumerRef("jira", "cloud-api"),
                RotationDays: null),
            new StopgapSecretDescriptor(
                CabinetName: PlatformCranlApiKey,
                ConfigKeys: new[] { "Cranl:ApiKey" },
                EnvVars: new[] { "CRANL_API_KEY" },
                Purpose: SecretPurpose.ApiKey,
                Consumer: new ConsumerRef("cranl", "org-scoped"),
                RotationDays: null),
            new StopgapSecretDescriptor(
                CabinetName: PlatformTenantSharedSecret,
                ConfigKeys: new[] { "Tamma:TenantSharedSecret", "Cranl:TenantSharedSecret" },
                EnvVars: new[] { "TAMMA_SHARED_SECRET" },
                Purpose: SecretPurpose.HmacSharedSecret,
                Consumer: new ConsumerRef("tamma-engine", "request-signing"),
                RotationDays: 30),
            new StopgapSecretDescriptor(
                CabinetName: PlatformAuditChainSigningKey,
                ConfigKeys: new[] { "Audit:ChainSigningKey" },
                EnvVars: new[] { "AUDIT_CHAIN_SIGNING_KEY" },
                Purpose: SecretPurpose.HmacSharedSecret,
                Consumer: new ConsumerRef("audit-chain", "checkpoint-signing"),
                RotationDays: 90),
        };
}

/// <summary>
/// A single stopgap entry — the (config-key, env-var, cabinet-name)
/// triple used by <see cref="IStopgapSecretMigrator"/> to import one
/// secret into the cabinet and by <see cref="IRuntimeSecretResolver"/>
/// to locate the runtime value.
/// </summary>
/// <param name="CabinetName">Canonical cabinet name, e.g.
/// <c>"anthropic/api-key"</c>.</param>
/// <param name="ConfigKeys">Config keys to probe in order when
/// sourcing a value from <see cref="IConfiguration"/>. First
/// non-empty wins.</param>
/// <param name="EnvVars">Fallback env var names probed after
/// <paramref name="ConfigKeys"/>. First non-empty wins.</param>
/// <param name="Purpose">Cabinet purpose category.</param>
/// <param name="Consumer">Primary consumer reference stamped onto the
/// cabinet row.</param>
/// <param name="RotationDays">Cadence in days (null = manual only).</param>
public sealed record StopgapSecretDescriptor(
    string CabinetName,
    IReadOnlyList<string> ConfigKeys,
    IReadOnlyList<string> EnvVars,
    SecretPurpose Purpose,
    ConsumerRef Consumer,
    int? RotationDays)
{
    /// <summary>
    /// Build the <see cref="RotationSchedule"/> for this descriptor —
    /// days-based when <see cref="RotationDays"/> is set, None
    /// otherwise.
    /// </summary>
    public RotationSchedule BuildSchedule() => RotationDays is { } d
        ? RotationSchedule.EveryDays(d)
        : RotationSchedule.None;

    /// <summary>
    /// Resolve the raw plaintext value from configuration / env, or
    /// return null when none of the probes hit.
    /// </summary>
    public string? ResolveFromConfig(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var key in ConfigKeys)
        {
            var v = configuration[key];
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        foreach (var env in EnvVars)
        {
            var v = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return null;
    }

    /// <summary>
    /// The primary "previous location" string for audit events —
    /// the first entry in <see cref="ConfigKeys"/>.
    /// </summary>
    public string PreviousLocation =>
        ConfigKeys.Count > 0 ? ConfigKeys[0]
        : EnvVars.Count > 0 ? EnvVars[0]
        : "(unknown)";
}
