using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Tamma.Api.Services;

// ─── Story 23-8: Infrastructure Monitor — metrics DTOs ─────────────────────────
//
// A read-only, live snapshot of the API process + its host, composed with the
// existing per-dependency health probes. Everything here is derived from what
// .NET / the container already exposes (GC, Process, DriveInfo, cgroup files) —
// NO new metrics infrastructure (Prometheus / node-exporter / a metrics cluster)
// is stood up. System-scoped (not tenant-scoped): the wiring gates it
// PlatformOwnerAccess so a regular member / tenant never sees process internals.
//
// SECURITY: this response carries ONLY system statistics + coarse dependency
// up/down status. It NEVER carries a connection string, DB host/user, password,
// API key, or any tenant/customer data. The dependency `Detail` field is
// allowlist-sanitized (see <see cref="InfrastructureMetricsService"/>) so a raw
// exception message (which can embed a host or user) can never reach the client.

/// <summary>.NET runtime + host identity and live CPU / uptime for the process.</summary>
public sealed record RuntimeMetrics(
    string FrameworkDescription,
    string OsDescription,
    string ProcessArchitecture,
    int ProcessorCount,
    double CpuUsagePercent,
    long UptimeSeconds,
    string StartedAt);

/// <summary>Thread + GC counters for the process (cheap, always-available).</summary>
public sealed record ProcessMetrics(
    int ThreadCount,
    int ThreadPoolThreadCount,
    long ThreadPoolPendingWorkItems,
    long ThreadPoolCompletedWorkItems,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

/// <summary>
/// Process memory footprint against the effective limit. <see cref="MemoryLimitSource"/>
/// is <c>"cgroup"</c> when a container memory cap was read from the cgroup
/// filesystem, else <c>"gc"</c> (the GC's <c>TotalAvailableMemoryBytes</c> view,
/// which itself honours a cgroup cap when one is set).
/// </summary>
public sealed record MemoryMetrics(
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    long GcHeapSizeBytes,
    long MemoryLimitBytes,
    long MemoryUsedBytes,
    double MemoryUsagePercent,
    string MemoryLimitSource);

/// <summary>One mounted volume: total / free / used bytes + utilisation.</summary>
public sealed record DiskMetrics(
    string Name,
    string DriveFormat,
    long TotalBytes,
    long FreeBytes,
    long UsedBytes,
    double UsedPercent);

/// <summary>
/// Coarse connectivity of one backing service. <see cref="Status"/> is one of
/// <c>healthy | unhealthy | unknown</c>; <see cref="Detail"/> is a leak-free,
/// allowlist-sanitized hint (an HTTP status code, a timeout note, or a generic
/// category) — NEVER a raw exception message.
/// </summary>
public sealed record DependencyStatus(
    string Name,
    string Status,
    long ResponseTimeMs,
    string? Detail);

/// <summary>
/// The full infrastructure snapshot returned by
/// <c>GET /api/admin/monitoring/infrastructure</c>.
/// </summary>
public sealed record InfrastructureMetricsResponse(
    RuntimeMetrics Runtime,
    ProcessMetrics Process,
    MemoryMetrics Memory,
    IReadOnlyList<DiskMetrics> Disks,
    IReadOnlyList<DependencyStatus> Dependencies,
    string CollectedAt);

public interface IInfrastructureMetricsService
{
    Task<InfrastructureMetricsResponse> GetMetricsAsync(CancellationToken ct = default);
}

/// <summary>
/// Story 23-8 — composes the lightweight infra snapshot. The process/runtime/
/// memory/disk tiers are read live from .NET + the container filesystem; the
/// dependency tier reuses the existing <see cref="IAdminHealthService"/> probe
/// fan-out (Postgres <c>SELECT 1</c> + HTTP health of ELSA / RabbitMQ / ChromaDB /
/// OpenSearch) and SANITIZES each probe's detail so no connection host / user /
/// secret leaks. Read-only: no DB writes, no schema, no external metrics stack.
/// </summary>
public sealed class InfrastructureMetricsService : IInfrastructureMetricsService
{
    /// <summary>CPU is sampled across this window (keeps round-trip well under 1s).</summary>
    private static readonly TimeSpan CpuSampleWindow = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// A cgroup "no limit" sentinel (max long, rounded to the page size) — treat
    /// values at/above this as "unlimited" and fall back to the GC's view.
    /// </summary>
    private const long UnlimitedThreshold = 0x7FFF_FFFF_FFFF_F000L;

    private readonly IAdminHealthService _health;

    public InfrastructureMetricsService(IAdminHealthService health)
    {
        _health = health;
    }

    public async Task<InfrastructureMetricsResponse> GetMetricsAsync(CancellationToken ct = default)
    {
        using var process = Process.GetCurrentProcess();

        // ── CPU: sample TotalProcessorTime across a short wall-clock window and
        //        normalise by the core count. Kick off the dependency probes
        //        first so their latency overlaps the CPU sample window.
        var healthTask = _health.GetHealthAsync(ct);

        var cpuStart = SafeTotalProcessorTime(process);
        var sw = Stopwatch.StartNew();
        await Task.Delay(CpuSampleWindow, ct);
        process.Refresh();
        sw.Stop();
        var cpuEnd = SafeTotalProcessorTime(process);

        var wallMs = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;
        var cpuUsedMs = (cpuEnd - cpuStart).TotalMilliseconds;
        var cpuPercent = wallMs > 0 ? Math.Clamp(cpuUsedMs / wallMs * 100.0, 0, 100) : 0;

        var runtime = BuildRuntime(process, cpuPercent);
        var procMetrics = BuildProcess(process);
        var memory = BuildMemory(process);
        var disks = ReadDisks();

        var health = await healthTask;
        var dependencies = health.Services
            .Select(s => new DependencyStatus(s.Name, s.Status, s.ResponseTime, SanitizeDetail(s.Details)))
            .ToList();

        return new InfrastructureMetricsResponse(
            runtime, procMetrics, memory, disks, dependencies, NowIso());
    }

    private static RuntimeMetrics BuildRuntime(Process process, double cpuPercent)
    {
        long uptimeSeconds = 0;
        var startedAt = NowIso();
        try
        {
            // Process.StartTime is Local-kind; normalise to UTC before it crosses
            // the HTTP/string boundary, and compute uptime TZ-independently.
            var startUtc = process.StartTime.ToUniversalTime();
            uptimeSeconds = Math.Max(0, (long)(DateTime.UtcNow - startUtc).TotalSeconds);
            startedAt = startUtc.ToString("o", CultureInfo.InvariantCulture);
        }
        catch
        {
            // StartTime can be denied on some hosts — leave the defaults.
        }

        return new RuntimeMetrics(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            Math.Round(cpuPercent, 2),
            uptimeSeconds,
            startedAt);
    }

    private static ProcessMetrics BuildProcess(Process process)
    {
        int threadCount;
        try
        {
            threadCount = process.Threads.Count;
        }
        catch
        {
            threadCount = 0;
        }

        return new ProcessMetrics(
            threadCount,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.CompletedWorkItemCount,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    private static MemoryMetrics BuildMemory(Process process)
    {
        var gcInfo = GC.GetGCMemoryInfo();
        var managedHeap = GC.GetTotalMemory(forceFullCollection: false);
        var gcHeapSize = gcInfo.HeapSizeBytes;
        var workingSet = process.WorkingSet64;
        var privateBytes = process.PrivateMemorySize64;

        var (limit, source) = ResolveMemoryLimit(gcInfo.TotalAvailableMemoryBytes);
        var used = workingSet;
        var usagePercent = limit > 0 ? Math.Clamp((double)used / limit * 100.0, 0, 100) : 0;

        return new MemoryMetrics(
            workingSet,
            privateBytes,
            managedHeap,
            gcHeapSize,
            limit,
            used,
            Math.Round(usagePercent, 2),
            source);
    }

    /// <summary>
    /// Best-effort container memory cap: read the cgroup filesystem (v2
    /// <c>memory.max</c>, then v1 <c>memory.limit_in_bytes</c>). A missing /
    /// unreadable / "max" / unlimited value falls back to the GC's
    /// <c>TotalAvailableMemoryBytes</c> (which already honours a cgroup cap).
    /// No secrets are involved — these are numeric limit files.
    /// </summary>
    private static (long Limit, string Source) ResolveMemoryLimit(long gcAvailableBytes)
    {
        var cgroup = TryReadCgroupMemoryLimit();
        if (cgroup is > 0 and < UnlimitedThreshold)
            return (cgroup.Value, "cgroup");

        return (gcAvailableBytes > 0 ? gcAvailableBytes : 0, "gc");
    }

    private static long? TryReadCgroupMemoryLimit()
    {
        // cgroup v2 unified hierarchy.
        var v2 = TryReadLimitFile("/sys/fs/cgroup/memory.max");
        if (v2 is not null)
            return v2;

        // cgroup v1.
        return TryReadLimitFile("/sys/fs/cgroup/memory/memory.limit_in_bytes");
    }

    private static long? TryReadLimitFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var raw = File.ReadAllText(path).Trim();
            if (raw.Length == 0 || string.Equals(raw, "max", StringComparison.OrdinalIgnoreCase))
                return null;
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
        catch
        {
            // Not on Linux, or the cgroup files aren't readable — best-effort only.
            return null;
        }
    }

    /// <summary>
    /// Pseudo / ephemeral / read-only-image filesystems that add noise but carry no
    /// operator signal — snap loop-mounts (<c>squashfs</c>), RAM-backed mounts, and
    /// removable-image formats. Real persistent volumes (ext*/xfs/btrfs/overlay/…) pass.
    /// </summary>
    private static readonly HashSet<string> PseudoDriveFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "squashfs", "tmpfs", "devtmpfs", "ramfs", "iso9660", "udf", "overlayfs",
    };

    private static IReadOnlyList<DiskMetrics> ReadDisks()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            drives = Array.Empty<DriveInfo>();
        }

        // Collect the real, mounted, persistent volumes, then collapse bind-mounts of
        // the same underlying device (identical format + total + free) to the mount
        // with the shortest path so the panel shows one row per real volume.
        var candidates = new List<(string Name, string Format, long Total, long Free)>();
        foreach (var drive in drives)
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed || drive.TotalSize <= 0)
                    continue;
                var format = SafeDriveFormat(drive);
                if (PseudoDriveFormats.Contains(format))
                    continue;
                candidates.Add((drive.Name, format, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch
            {
                // A transient / permission error on one drive must not blank the panel.
            }
        }

        var disks = candidates
            .OrderBy(c => c.Name.Length)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .GroupBy(c => $"{c.Format}|{c.Total}|{c.Free}")
            .Select(g => g.First())
            .Select(c =>
            {
                var used = Math.Max(0, c.Total - c.Free);
                var usedPct = c.Total > 0 ? Math.Clamp((double)used / c.Total * 100.0, 0, 100) : 0;
                return new DiskMetrics(c.Name, c.Format, c.Total, c.Free, used, Math.Round(usedPct, 2));
            })
            .ToList();

        // Fallback: if no fixed drive surfaced (some minimal containers), report the
        // volume backing the app's content root.
        if (disks.Count == 0)
        {
            try
            {
                var root = Path.GetPathRoot(AppContext.BaseDirectory);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && drive.TotalSize > 0)
                    {
                        var total = drive.TotalSize;
                        var free = drive.AvailableFreeSpace;
                        var used = Math.Max(0, total - free);
                        var usedPct = total > 0 ? Math.Clamp((double)used / total * 100.0, 0, 100) : 0;
                        disks.Add(new DiskMetrics(
                            drive.Name, SafeDriveFormat(drive), total, free, used, Math.Round(usedPct, 2)));
                    }
                }
            }
            catch
            {
                // Give up gracefully — an empty disk list is a valid (degraded) snapshot.
            }
        }

        return disks;
    }

    private static string SafeDriveFormat(DriveInfo drive)
    {
        try
        {
            return drive.DriveFormat;
        }
        catch
        {
            return "unknown";
        }
    }

    private static TimeSpan SafeTotalProcessorTime(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Allowlist-sanitize a probe detail so no raw exception text (which can embed
    /// a DB host, user, or connection string) reaches the client. Only three
    /// leak-free shapes survive verbatim — an HTTP status code, a timeout note, and
    /// the "URL not configured" marker; everything else collapses to a coarse
    /// category. A healthy probe carries no detail.
    /// </summary>
    internal static string? SanitizeDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
            return null;
        if (string.Equals(detail, "URL not configured", StringComparison.Ordinal))
            return detail;
        if (detail.StartsWith("HTTP ", StringComparison.Ordinal))
            return detail;
        if (detail.StartsWith("Timed out after", StringComparison.Ordinal))
            return detail;
        // Any other message may carry connection details — collapse it.
        return "unreachable";
    }

    private static string NowIso() =>
        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
}
