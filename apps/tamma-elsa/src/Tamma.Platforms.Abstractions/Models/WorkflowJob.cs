namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Sub-job within a CI run (GitHub Actions job / GitLab pipeline job /
/// Azure DevOps stage). Same status/conclusion strategy as
/// <see cref="WorkflowRun"/> — verbatim platform strings.
/// <c>RawMetadata</c> is raw JSON text (parse on demand) — never a
/// live <see cref="System.Text.Json.JsonDocument"/>; pooled-buffer
/// leak hazard when stored in records.
/// </summary>
public sealed record WorkflowJob(
    string JobId,
    string Name,
    string Status,
    string? Conclusion,
    string? RawMetadata);
