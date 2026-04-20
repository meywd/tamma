namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Thrown by <see cref="LimitedStream"/> when a read exceeds the
/// configured byte cap. Callers typically catch this and substitute a
/// null/empty result so the surrounding pipeline can fall back to a
/// smaller / compare-API-based result path.
///
/// <para>Review-session 2026-04-20 finding 6: without a cap, a compromised
/// agent can upload a 10 GB artifact (GitHub Actions free-tier ceiling) and
/// OOM the Tamma API process, DoSing every other tenant. This exception
/// pairs with <see cref="LimitedStream"/> to fail fast well under the
/// memory headroom.</para>
/// </summary>
public sealed class ArtifactTooLargeException : Exception
{
    public long Limit { get; }
    public long BytesRead { get; }

    public ArtifactTooLargeException(long limit, long bytesRead)
        : base($"Artifact read exceeded {limit} bytes (read {bytesRead} so far)")
    {
        Limit = limit;
        BytesRead = bytesRead;
    }
}

/// <summary>
/// Read-only <see cref="Stream"/> decorator that throws
/// <see cref="ArtifactTooLargeException"/> as soon as the cumulative read
/// count exceeds <paramref name="byteLimit"/>. Used on the artifact
/// download path to bound an otherwise untrusted GitHub Actions upload
/// (review-session 2026-04-20 finding 6).
///
/// <para>The wrapper is intentionally minimal — only the async and sync
/// <c>Read</c> paths are guarded because <see cref="Stream.CopyToAsync(Stream, CancellationToken)"/>
/// routes all traffic through them. Length / Position / Seek are
/// delegated but we mark ourselves as non-seekable: an attacker must not
/// be able to skip the byte counter by seeking.</para>
/// </summary>
public sealed class LimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _byteLimit;
    private long _bytesRead;

    public LimitedStream(Stream inner, long byteLimit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (byteLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLimit), "byteLimit must be positive");
        }
        _byteLimit = byteLimit;
    }

    public long BytesRead => _bytesRead;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException("LimitedStream is non-seekable");
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Track(n);
        return n;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var n = await _inner.ReadAsync(buffer, offset, count, cancellationToken)
            .ConfigureAwait(false);
        Track(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Track(n);
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException("LimitedStream is non-seekable");

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    private void Track(int n)
    {
        if (n <= 0) return;
        _bytesRead += n;
        if (_bytesRead > _byteLimit)
        {
            throw new ArtifactTooLargeException(_byteLimit, _bytesRead);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
