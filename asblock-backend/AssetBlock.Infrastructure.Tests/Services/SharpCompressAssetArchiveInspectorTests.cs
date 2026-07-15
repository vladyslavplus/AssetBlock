using System.IO.Compression;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.Services;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class SharpCompressAssetArchiveInspectorTests
{
    private readonly SharpCompressAssetArchiveInspector _sut = new();

    [Fact]
    public async Task Inspect_WhenValidZip_ShouldSucceed()
    {
        await using var zip = CreateZip(("ok.txt", "hello"));
        await _sut.Inspect(zip, "ok.zip");
    }

    [Fact]
    public async Task Inspect_WhenPathTraversal_ShouldReject()
    {
        await using var zip = CreateZip(("../evil.txt", "x"));
        var act = async () => await _sut.Inspect(zip, "evil.zip");
        await act.Should().ThrowAsync<ArchiveRejectedException>()
            .WithMessage("*traversal*");
    }

    [Fact]
    public async Task Inspect_WhenAbsolutePath_ShouldReject()
    {
        await using var zip = CreateZip(("/etc/passwd", "x"));
        var act = async () => await _sut.Inspect(zip, "evil.zip");
        await act.Should().ThrowAsync<ArchiveRejectedException>()
            .WithMessage("*absolute*");
    }

    [Fact]
    public async Task Inspect_WhenStreamIsNonSeekable_ShouldRejectWithExplicitContract()
    {
        await using var archive = new NonSeekableStream([1, 2, 3]);

        var act = () => _sut.Inspect(archive, "archive.zip");

        await act.Should().ThrowAsync<ArchiveRejectedException>()
            .WithMessage("*seekable*");
    }

    [Fact]
    public async Task Inspect_WhenTooManyEntries_ShouldReject()
    {
        await using var ms = new MemoryStream();
        await using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 10_001; i++)
            {
                var entry = archive.CreateEntry($"e{i}.txt", CompressionLevel.NoCompression);
                await using var s = await entry.OpenAsync();
                await s.WriteAsync("x"u8.ToArray());
            }
        }

        ms.Position = 0;
        var act = async () => await _sut.Inspect(ms, "many.zip");
        await act.Should().ThrowAsync<ArchiveRejectedException>()
            .WithMessage("*10000*");
    }

    [Fact]
    public async Task Inspect_WhenTooManyDirectoryEntries_ShouldReject()
    {
        await using var ms = new MemoryStream();
        await using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 10_001; i++)
            {
                archive.CreateEntry($"directory{i}/");
            }
        }

        ms.Position = 0;
        var act = async () => await _sut.Inspect(ms, "many-directories.zip");
        await act.Should().ThrowAsync<ArchiveRejectedException>()
            .WithMessage("*10000*");
    }

    [Fact]
    public async Task Inspect_WhenCompressionRatioExceedsLimit_ShouldReject()
    {
        // Highly compressible payload: large zeros compress tiny → ratio >> 100:1
        var zeros = new byte[2 * 1024 * 1024];
        await using var ms = new MemoryStream();
        await using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("bomb.bin", CompressionLevel.Optimal);
            await using var s = await entry.OpenAsync();
            await s.WriteAsync(zeros);
        }

        ms.Position = 0;
        var act = async () => await _sut.Inspect(ms, "bomb.zip");
        await act.Should().ThrowAsync<ArchiveRejectedException>()
            .WithMessage("*ratio*");
    }

    private static MemoryStream CreateZip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        ms.Position = 0;
        return ms;
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }
}
