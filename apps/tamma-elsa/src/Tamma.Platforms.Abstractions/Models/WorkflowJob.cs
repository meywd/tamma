using System.Text.Json;

namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Sub-job within a CI run (GitHub Actions job / GitLab pipeline job /
/// Azure DevOps stage). Same status/conclusion strategy as
/// <see cref="WorkflowRun"/> — verbatim platform strings.
/// </summary>
public sealed record WorkflowJob(
    string JobId,
    string Name,
    string Status,
    string? Conclusion,
    JsonDocument? RawMetadata);
