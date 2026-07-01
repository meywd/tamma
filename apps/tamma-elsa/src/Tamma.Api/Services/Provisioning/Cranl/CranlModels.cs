using System.Text.Json.Serialization;

namespace Tamma.Api.Services.Provisioning.Cranl;

// ─── Projects ────────────────────────────────────────────────────────────────

public sealed class CranlProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("organization_id")]
    public string? OrganizationId { get; set; }
}

public sealed class CreateProjectRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("organizationId")]
    public string OrganizationId { get; set; } = string.Empty;
}

// ─── Databases ───────────────────────────────────────────────────────────────

public sealed class CreateDatabaseRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "postgresql";

    [JsonPropertyName("serverId")]
    public string? ServerId { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// Database row returned by <c>GET /api/databases/:id</c>. The docs only
/// guarantee a minimal shape (<c>id</c>, <c>name</c>, <c>type</c>,
/// <c>status</c>, <c>project_id</c>); the CLI implies the full record carries
/// connection details (<c>host</c>, <c>username</c>, <c>password</c>,
/// <c>database</c>, <c>connection</c>) once the row reaches <c>running</c>.
/// We model both shapes optimistically and fall back to the
/// <see cref="ConnectionString"/> field if present, otherwise stitch one from
/// the parts.
///
/// <para>See README "Gaps / assumptions" — validate against live API before
/// shipping.</para>
/// </summary>
public sealed class CranlDatabase
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "postgresql";

    /// <summary>One of <c>pending</c>, <c>running</c>, <c>idle</c>, <c>error</c>, etc.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("server_id")]
    public string? ServerId { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    // ── Optional connection fields (populated once status == "running") ──

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    /// <summary>
    /// Pre-built connection string (e.g. <c>postgresql://admin:pass@host:5432/mydb</c>).
    /// The CLI's <c>cranl db info</c> displays this; the API may return it directly
    /// once the DB is running, or callers can stitch one from the parts above.
    /// </summary>
    [JsonPropertyName("connection")]
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Build a postgres connection string from the parts. Returns the explicit
    /// <see cref="ConnectionString"/> if Cranl provided one; otherwise stitches
    /// <c>postgresql://user:pass@host:port/db</c>. Returns null when any required
    /// part is missing.
    ///
    /// <para>The username and password are percent-encoded
    /// (<see cref="Uri.EscapeDataString(string)"/>) so a random Cranl-minted
    /// credential containing URI-reserved chars (<c>@ : / # ? % + </c> or
    /// space) produces a VALID libpq URI — both for the pool-row admin string
    /// parse and for the <c>DATABASE_URL</c> the Cranl engine reads. The
    /// keyword-form parser percent-decodes on the way back, so the credential
    /// round-trips exactly.</para>
    /// </summary>
    public string? BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
            return ConnectionString;
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username)
            || string.IsNullOrWhiteSpace(Password) || string.IsNullOrWhiteSpace(Database))
            return null;
        var port = Port ?? 5432;
        var user = Uri.EscapeDataString(Username);
        var pass = Uri.EscapeDataString(Password);
        return $"postgresql://{user}:{pass}@{Host}:{port}/{Database}";
    }
}

// ─── Applications ────────────────────────────────────────────────────────────

public sealed class CreateApplicationRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("repositoryId")]
    public string RepositoryId { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("buildType")]
    public string? BuildType { get; set; }

    [JsonPropertyName("serverId")]
    public string? ServerId { get; set; }

    [JsonPropertyName("buildPath")]
    public string? BuildPath { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class CranlApplication
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>One of <c>pending</c>, <c>running</c>, <c>done</c>, <c>error</c>, <c>idle</c>, <c>deploying</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }
}

public sealed class CranlAppDomain
{
    [JsonPropertyName("domainId")]
    public string DomainId { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("https")]
    public bool Https { get; set; }

    [JsonPropertyName("certificateType")]
    public string? CertificateType { get; set; }

    [JsonPropertyName("sslStatus")]
    public string? SslStatus { get; set; }
}

public sealed class CranlAppDomains
{
    [JsonPropertyName("domains")]
    public List<CranlAppDomain> Domains { get; set; } = new();

    [JsonPropertyName("defaultDomain")]
    public string? DefaultDomain { get; set; }
}

// ─── Internal request/response wrappers ──────────────────────────────────────

internal sealed class LifecycleActionRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;
}

internal sealed class EnvironmentRequest
{
    [JsonPropertyName("env")]
    public string Env { get; set; } = string.Empty;
}

internal sealed class CranlErrorBody
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
