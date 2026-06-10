namespace Tamma.Platforms.Abstractions;

/// <summary>
/// Lower-snake wire-format mapping for <see cref="PlatformKind"/>. The
/// strings on this side of the seam mirror the CHECK constraints on
/// the database side (<c>'github','gitea','forgejo','gitlab',
/// 'bitbucket','azure_devops'</c>) so a row written by one component
/// can never collide with another component's spelling.
///
/// <para>Centralised here so consumers in other assemblies (e.g.
/// <c>Tamma.Data.Repositories.PlatformWebhookDeliveryRepository</c>)
/// can call the same conversion as <c>PlatformResolver</c> without
/// taking a project reference on it.</para>
/// </summary>
public static class PlatformKindWire
{
    /// <summary>
    /// Convert a <see cref="PlatformKind"/> enum to the lower-snake
    /// string the database stores. Throws on values not yet wired —
    /// keeps adding a new <see cref="PlatformKind"/> from drifting
    /// between read + write paths.
    /// </summary>
    public static string ToWire(PlatformKind kind) => kind switch
    {
        PlatformKind.GitHub => "github",
        PlatformKind.Gitea => "gitea",
        PlatformKind.Forgejo => "forgejo",
        PlatformKind.GitLab => "gitlab",
        PlatformKind.Bitbucket => "bitbucket",
        PlatformKind.AzureDevOps => "azure_devops",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Unknown PlatformKind"),
    };

    /// <summary>
    /// Reverse mapping. Returns false on an unknown wire value
    /// (operational defence — a row written by a future migration
    /// shouldn't crash a current consumer).
    /// </summary>
    public static bool TryParse(string wire, out PlatformKind kind)
    {
        switch (wire)
        {
            case "github": kind = PlatformKind.GitHub; return true;
            case "gitea": kind = PlatformKind.Gitea; return true;
            case "forgejo": kind = PlatformKind.Forgejo; return true;
            case "gitlab": kind = PlatformKind.GitLab; return true;
            case "bitbucket": kind = PlatformKind.Bitbucket; return true;
            case "azure_devops": kind = PlatformKind.AzureDevOps; return true;
            default: kind = default; return false;
        }
    }
}
