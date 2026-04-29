using System.Text.Json;

namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// CI run / pipeline / build record. Status + conclusion strings are
/// kept verbatim from the platform — drivers should NOT re-interpret
/// values into a normalized enum because each platform has different
/// vocabulary (queued/in_progress/completed for GitHub vs.
/// pending/running/success/failed/canceled/skipped/manual for
/// GitLab). Callers branch on string values per their platform; the
/// abstraction keeps the conclusion as <c>"success"</c>,
/// <c>"failure"</c>, <c>"cancelled"</c>, etc.
/// </summary>
/// <param name="RunId">Platform-scoped run id (string for portability).</param>
/// <param name="Status">Platform-native status string.</param>
/// <param name="Conclusion">Final conclusion (null while running).</param>
/// <param name="HtmlUrl">Browser-facing URL to the run.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="CompletedAt">When the run completed (null while running).</param>
/// <param name="RawMetadata">
/// Optional driver-specific fields (GitLab pipeline source, Azure
/// DevOps reason, etc.) — kept as JSON so abstraction-level callers
/// can ignore it but drivers can round-trip platform metadata
/// without losing fidelity.
/// </param>
public sealed record WorkflowRun(
    string RunId,
    string Status,
    string? Conclusion,
    string HtmlUrl,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    JsonDocument? RawMetadata);
