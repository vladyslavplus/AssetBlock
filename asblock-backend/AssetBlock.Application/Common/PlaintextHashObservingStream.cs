using System.Security.Cryptography;

namespace AssetBlock.Application.Common;

/// <summary>
/// Wraps a plaintext read stream and computes a SHA-256 hash of every byte read.
/// After the caller finishes reading (or calls <see cref="FinalizeHash"/>),
/// <see cref="HashHex"/> contains the lowercase 64-character hex digest.
/// </summary>
internal sealed class PlaintextHashObservingStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash;
    private string? _hashHex;

    public PlaintextHashObservingStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    /// <summary>Lowercase hex SHA-256 digest. Available after <see cref="FinalizeHash"/> is called or stream is disposed.</summary>
    public string HashHex => _hashHex ?? throw new InvalidOperationException("Hash has not been finalized yet.");

    /// <summary>Finalizes the incremental hash and stores the hex result. Safe to call multiple times.</summary>
    public void FinalizeHash()
    {
        if (_hashHex is null)
        {
            _hashHex = Convert.ToHexString(_hash.GetCurrentHash()).ToLowerInvariant();
        }
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _hash.AppendData(buffer, offset, read);
        }

        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        if (read > 0)
        {
            _hash.AppendData(buffer[..read]);
        }

        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _hash.AppendData(buffer, offset, read);
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _hash.AppendData(buffer.Span[..read]);
        }

        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FinalizeHash();
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
